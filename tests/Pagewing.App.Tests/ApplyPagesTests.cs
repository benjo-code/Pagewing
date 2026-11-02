using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Viewing;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// Applying the page changes (milestone 13, task 8): the card with the summary and every loss, the questions before it
/// (comments saved first, a refused plan), the rewrite and what the tab keeps of its view, the failures that leave the plan and
/// the target as they were, Ctrl+S, the locks against printing and the way the update path and closing see an open plan.
/// Every file is a copy in a temp folder; nothing is printed and nothing leaves the temp folder.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed partial class ApplyPagesTests : IDisposable
{
    private readonly SaveCardHost _host = new();
    private readonly CultureInfo _originalCulture = Strings.Culture;

    public void Dispose()
    {
        _host.Dispose();
        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task ApplyAsksToSaveAnnotationsFirstWhenDirty()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab);
        Stage(tab, delete: [0, 1]);

        // "Cancel" on the card: nothing is written, the plan, the mode and the comment stay.
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveAnnotationsFirst);
        Assert.Equal(Strings.SaveFirstText, viewModel.PendingSave!.Message);
        Assert.Equal([PendingSaveChoice.Save, PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        Assert.Equal(PendingSaveChoice.Save, viewModel.PendingSave.DefaultChoice);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.True(tab.HasPendingPageEdits);
        Assert.True(tab.IsOrganizing);
        Assert.True(tab.IsDirty);
        Assert.False(tab.IsSaving);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        // "Save": the incremental save runs, then the card of the apply comes; "Back" there leaves the file saved (comments
        // only) and the plan staged.
        apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveAnnotationsFirst);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Save));
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.False(tab.IsDirty);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.True(saved.Length > original.Length);
        Assert.Equal(original, saved[..original.Length]);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(6, tab.PageCount);
        Assert.False(tab.IsSaving);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task CardShowsSummaryAndEveryLossOfTheReport(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _, tab, _) = await OrganizedAsync("pages-marked.pdf");
        Stage(tab, delete: [4, 5], rotate: [0, 1, 2]);
        var report = EveryKindOfLoss();
        viewModel.PageAnalyzer = (_, _, _, _) => Task.FromResult(report);

        var apply = viewModel.ApplyPageEditsAsync(tab);
        var card = await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        var info = viewModel.PendingSave!.Apply!;

        // The summary says the count before and after and what is turned; the card shows it.
        Assert.Equal(language == "en" ? "6 → 4 pages, 3 turned" : "6 → 4 Seiten, 3 gedreht", info.Summary);
        Assert.Equal(info.Summary, Find<TextBlock>(card, "ApplyPagesSummary").Text);

        // Every loss of the report is a line of its own, in plain words, and every line is on the card.
        Assert.Equal(report.Losses.Count, info.Lines.Count);
        Assert.Equal(info.Lines.Count, info.Lines.Distinct().Count());
        Assert.All(info.Lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        var shown = card.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.All(info.Lines, line => Assert.Contains(line, shown));

        // Pages are page numbers (1-based), several of them as a list.
        Assert.Contains(info.Lines, line => line.Contains("2, 3", StringComparison.Ordinal));
        Assert.Contains(info.Lines, line => line.Contains("2–4", StringComparison.Ordinal));
        Assert.DoesNotContain(info.Lines, line => line.Contains("source:", StringComparison.Ordinal) || line.Contains("docmdp", StringComparison.Ordinal));
        Assert.Contains(Strings.ApplyPagesIrreversible, shown);

        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
    }

    [Fact]
    public void TheSummaryNamesPagesTurnedBlankCopiesAndInsertedFiles()
    {
        Strings.Culture = new CultureInfo("en");
        var plan = PageEditPlan.Identity(12)
            .Delete(new HashSet<int> { 0, 1, 2 })
            .InsertFrom(new PageSourceId(1), [0], 9)
            .Rotate(new HashSet<int> { 0, 1, 2 }, 90);

        Assert.Equal("12 → 10 pages, 3 turned, 1 file inserted", ApplyPagesInfo.Create(plan, new PageEditReport([])).Summary);

        var more = plan.InsertBlank(0, new BlankPage(100, 100)).Duplicate(new HashSet<int> { 3 }).InsertFrom(new PageSourceId(2), [0, 1], 0);
        Assert.Equal("12 → 14 pages, 4 turned, 1 blank page, 1 duplicated, 2 files inserted", ApplyPagesInfo.Create(more, new PageEditReport([])).Summary);
        Assert.Equal("1 page", ApplyPagesInfo.Create(PageEditPlan.Identity(1), new PageEditReport([])).Summary);
    }

    [AvaloniaFact]
    public async Task DefaultButtonIsSaveAs()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Stage(tab, delete: [0]);

        var apply = viewModel.ApplyPageEditsAsync(tab);
        var card = await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        var question = viewModel.PendingSave!;

        // "Save as …" is the default: it has the focus and Enter; the original stays the safe choice.
        Assert.Equal(PendingSaveChoice.SaveAs, question.DefaultChoice);
        Assert.Equal([PendingSaveChoice.SaveAs, PendingSaveChoice.SaveInOriginal, PendingSaveChoice.Cancel], question.Choices);
        var saveAs = card.ButtonOf(PendingSaveChoice.SaveAs)!;
        Assert.True(saveAs.IsFocused);
        Assert.Contains("primary", saveAs.Classes);
        Assert.Equal(["Save as …", "Save in the original file", "Back"], ShownButtons(card));
        Assert.All(new[] { PendingSaveChoice.SaveAs, PendingSaveChoice.SaveInOriginal, PendingSaveChoice.Cancel },
            choice => Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(card.ButtonOf(choice)!))));

        // Writing into the original is dangerous: not the default, locked for the first 400 ms.
        var original1 = card.ButtonOf(PendingSaveChoice.SaveInOriginal)!;
        Assert.DoesNotContain("primary", original1.Classes);
        Assert.False(original1.IsEnabled);
        Assert.False(question.Choose(PendingSaveChoice.SaveInOriginal));
        _host.PassTheLock(window);
        Assert.True(original1.IsEnabled);

        // Enter takes the default: the file dialog opens (cancelled here: nothing is written, the plan stays).
        _host.Picker.Result = null;
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.False(await apply);
        Assert.Equal(1, _host.Picker.Calls);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaTheory]
    [InlineData("signed-docmdp-none.pdf")]
    [InlineData("signed-docmdp-1.pdf")]
    [InlineData("signed-docmdp-2.pdf")]
    [InlineData("signed-docmdp-3.pdf")]
    public async Task SignedDocumentShowsTheWarningAndNeverSaysValid(string fixture)
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync(fixture);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);

        // The real report, for every level of the dummy signature: the warning is on the card each time.
        var apply = viewModel.ApplyPageEditsAsync(tab);
        var card = await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        var lines = viewModel.PendingSave!.Apply!.Lines;
        Assert.Contains(Strings.ApplyLossSignature, lines);
        Assert.Contains("invalid", Strings.ApplyLossSignature, StringComparison.Ordinal);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Strings.ApplyLossSignature);
        Assert.All(lines.Append(viewModel.PendingSave.Message), line => Assert.DoesNotMatch(ValidWord(), line));
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task ApplyWritesAndTheTabAdoptsTheNewDocument()
    {
        var (window, viewModel, view, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var oldDocument = tab.Document!;
        var target = Path.Combine(_host.Folder, "applied.pdf");

        // Zoom, panel and the position are the tab's: the user looks at page 4 at 150 %.
        tab.ZoomMode = ZoomMode.Free;
        tab.ApplyFitZoom(1.5);
        tab.GoToPage(4);
        await WaitUntilAsync(window, () => tab.CurrentPage == 4);
        var zoom = tab.Zoom;
        Stage(tab, delete: [0, 1], rotate: [3]);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        _host.Picker.Result = target;

        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        Assert.True(await apply);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);

        // The tab shows the written file: four pages, the plan gone and the mode over, the panel and the zoom as they were, the
        // current page the one that followed (page 4 of the document is page 2 of the result), nothing of the old history left.
        Assert.Equal(Path.GetFullPath(target), tab.FullPath);
        Assert.Equal(4, tab.PageCount);
        Assert.NotSame(oldDocument, tab.Document);
        Assert.Same(tab.Session.Document, tab.Document);
        Assert.Equal(4, tab.Document!.PageCount);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Null(tab.PagePlan);
        Assert.False(tab.IsOrganizing);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        Assert.Equal(zoom, tab.Zoom);
        Assert.Equal(ZoomMode.Free, tab.ZoomMode);
        Assert.Equal(2, tab.CurrentPage);
        Assert.False(tab.CanUndo);
        Assert.False(tab.IsDirty);
        Assert.False(tab.IsSaving);
        Assert.Contains("4", await tab.Document.GetTextAsync(1, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(90, await tab.Document.GetPageRotationAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));

        // The list of pages shows the new document, not the old one.
        var list = window.GetVisualDescendants().OfType<ThumbnailList>().Single();
        await WaitUntilAsync(window, () => list.ItemCount == 4);
        Assert.False(tab.SaveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TheRewrittenFileKeepsTheViewAndIsNotANewDocument()
    {
        var (window, viewModel, view, tab, path) = await OrganizedAsync("pages-marked.pdf", Pagewing.App.Services.RecentFilesService.Detached());

        // Search, outline and the signatures belong to the document: they are read again for the new pages; the query stays.
        tab.Search.Query = "PAGE";
        await WaitUntilAsync(window, () => tab.Search.State == SearchState.Done || tab.Search.Hits.Count > 0);
        await tab.EnsureOutlineAsync();
        var outline = tab.Outline;
        Assert.NotNull(outline);
        tab.ZoomMode = ZoomMode.Free;
        tab.ApplyFitZoom(0.75);
        Stage(tab, delete: [1, 2, 3]);

        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveInOriginal));
        _host.PassTheLock(window);
        Assert.True(await apply);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);

        Assert.Equal(path, tab.FullPath);
        Assert.Equal(3, tab.PageCount);
        Assert.Equal(0.75, tab.Zoom);
        Assert.Equal("PAGE", tab.Search.Query);
        await WaitUntilAsync(window, () => tab.Search.Hits.Count == 3 && tab.Search.State != SearchState.Searching);
        Assert.NotSame(outline, tab.Outline);
        Assert.Equal(3, view.Layout!.PageCount);

        // The history of recent files is not told again about the same file.
        Assert.Same(tab.Document, tab.ReportedDocument);
    }

    [AvaloniaTheory]
    [InlineData(SaveFailureReason.DiskFull)]
    [InlineData(SaveFailureReason.EncryptedNotSupported)]
    [InlineData(SaveFailureReason.SourceModified)]
    [InlineData(SaveFailureReason.OriginalChanged)]
    [InlineData(SaveFailureReason.VerificationFailed)]
    [InlineData(SaveFailureReason.NotWritable)]
    public async Task ApplyFailureKeepsThePlanAndTheTarget(SaveFailureReason reason)
    {
        var (window, viewModel, view, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var document = tab.Document;
        Stage(tab, delete: [0]);
        viewModel.PageRewriter = (_, _, _, _, _) => throw new SaveFailureException(reason, "injected");
        _host.Picker.Result = path;

        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        // The card says why and offers "Save as" only where another name can help; the plan and the tab are as they were.
        var changedFile = reason is SaveFailureReason.SourceModified or SaveFailureReason.OriginalChanged;
        if (changedFile)
        {
            // A changed file is a dead end with "Cancel" alone, and nothing can overwrite it.
            Assert.Equal(Strings.ApplyErrorFileChanged, viewModel.PendingSave!.Message);
            Assert.DoesNotContain(PendingSaveChoice.Overwrite, viewModel.PendingSave.Choices);
        }
        else
        {
            Assert.Equal(reason, viewModel.PendingSave!.Reason);
        }

        Assert.False(string.IsNullOrWhiteSpace(viewModel.PendingSave.Message));
        Assert.Equal(changedFile || reason == SaveFailureReason.EncryptedNotSupported ? 1 : 2, viewModel.PendingSave.Choices.Count);
        Assert.True(tab.HasPendingPageEdits);
        Assert.True(tab.IsOrganizing);
        viewModel.PendingSave.Abandon();
        Assert.False(await apply);
        Assert.Same(document, tab.Document);
        Assert.Equal(6, tab.PageCount);
        Assert.True(tab.HasPendingPageEdits);
        Assert.False(tab.IsSaving);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
        Assert.True(tab.ApplyPageEditsCommand.CanExecute(null));
        Assert.Same(tab.Session.Document, view.Document);
    }

    [AvaloniaFact]
    public async Task ApplyFailureWhenTheSourceChangedKeepsThePlanAndTheTarget()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        Stage(tab, delete: [0]);

        // Another program rewrote the opened file in place: the rewrite refuses before a byte is replaced.
        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.WriteByte((byte)'\n');
        }

        var changed = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        _host.PassTheLock(window);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveInOriginal));
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        Assert.Equal(Strings.ApplyErrorFileChanged, viewModel.PendingSave!.Message);
        Assert.Equal([PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(changed, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherFileAtThePathIsNeverRearrangedOrReplaced(bool inOriginal)
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        Stage(tab, delete: [0]);

        // The path now names another file with the same number of pages (replaced, not rewritten). "Save as" elsewhere must not
        // write a rearranged copy of that foreign file, and "Save in the original file" must not replace it: both end in the
        // card with "Cancel" alone, and nothing can overwrite.
        var other = Path.Combine(_host.Folder, "other.pdf");
        File.Copy(Pagewing.TestSupport.TestData.Pdf("pages-marked.pdf"), other);
        File.AppendAllText(other, "\n");
        File.Move(path, Path.Combine(_host.Folder, "moved.pdf"));
        File.Move(other, path);
        var target = Path.Combine(_host.Folder, "elsewhere.pdf");
        _host.Picker.Result = target;
        var rewrites = 0;
        viewModel.PageRewriter = (copy, engine, request, progress, token) =>
        {
            rewrites++;
            return DocumentSaver.RewriteAsync(copy, engine, request, progress, token);
        };

        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        Assert.Equal(PendingSaveKind.ApplyPages, viewModel.PendingSave!.Kind);
        _host.PassTheLock(window);
        Assert.True(viewModel.PendingSave!.Resolve(inOriginal ? PendingSaveChoice.SaveInOriginal : PendingSaveChoice.SaveAs));
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        var foreign = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(Strings.ApplyErrorFileChanged, viewModel.PendingSave!.Message);
        Assert.Equal([PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.Equal(0, rewrites);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(target));
        Assert.True(tab.HasPendingPageEdits);
        Assert.True(tab.IsOrganizing);
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
    }

    [AvaloniaFact]
    public async Task AFailureAfterTheWriteSaysTheFileIsWrittenAndReopensIt()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Stage(tab, delete: [0, 1]);
        var target = Path.Combine(_host.Folder, "written.pdf");
        _host.Picker.Result = target;

        // The checked file is closed before the tab can take it over: the adopt fails after the file is written.
        viewModel.PageRewriter = async (copy, engine, request, progress, token) =>
        {
            var result = await DocumentSaver.RewriteAsync(copy, engine, request, progress, token);
            await result.Reopened.DisposeAsync();
            return result;
        };
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        // The card says the file is written (never "not saved") and where.
        var question = viewModel.PendingSave!;
        Assert.Contains(Path.GetFileName(target), question.Title, StringComparison.Ordinal);
        Assert.Contains(target, question.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not saved", question.Title + question.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not be saved", question.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([PendingSaveChoice.Cancel], question.Choices);
        question.Resolve(PendingSaveChoice.Cancel);
        Assert.True(await apply);

        // No stale plan and no offer to apply again onto the old document; the written file is opened through the normal path.
        Assert.False(tab.HasPendingPageEdits);
        Assert.False(tab.IsOrganizing);
        Assert.False(tab.IsSaving);
        await WaitUntilAsync(window, () => tab.FullPath == Path.GetFullPath(target) && tab.IsReady && tab.PageCount == 4);
        Assert.False(tab.ApplyPageEditsCommand.CanExecute(null));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task TypedTextAfterTheCommentsWereSavedStopsTheApplyAndStaysInTheTab()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab);
        Stage(tab, delete: [0]);
        tab.PendingTextBoxDraft = new TextBoxDraft(0, new Core.Text.PageRect(50, 50, 150, 80), null, "kept", 12, new Core.Annotations.AnnotationColor(0, 0, 0));

        // The comments can be saved, the refused text cannot: the save-first branch saves what it can, then stops with the reason.
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveAnnotationsFirst);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Save));
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        Assert.Equal(Strings.ApplyErrorDraft, viewModel.PendingSave!.Message);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);

        Assert.False(await apply);
        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.True(saved.Length > original.Length);
        Assert.Equal(original, saved[..original.Length]);
        Assert.NotNull(tab.PendingTextBoxDraft);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(6, tab.Document!.PageCount);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public void EachLossNamesTheIndexSpaceOfItsPages(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var original = language == "en" ? "originally" : "ursprünglich";
        var fresh = language == "en" ? "of the new file" : "der neuen Datei";
        var sources = new (PageLoss Loss, bool Original)[]
        {
            (new PageLoss(PageLossKind.BookmarkToDeletedPage, null, [1, 2]), true),
            (new PageLoss(PageLossKind.OrphanFormFields, null, [4]), true),
            (new PageLoss(PageLossKind.DuplicatedFormFields, PageLossDetail.Duplicated, [0, 3]), false),
            (new PageLoss(PageLossKind.DuplicatedFormFields, PageLossDetail.Inserted, [5]), false),
            (new PageLoss(PageLossKind.FormFieldNameCollision, null, [4]), false),
            (new PageLoss(PageLossKind.InternalLinkLost, PageLossDetail.DeletedPage, [0]), false),
            (new PageLoss(PageLossKind.InternalLinkLost, PageLossDetail.CopiedPage, [1, 2, 3]), false),
        };
        foreach (var (loss, isOriginal) in sources)
        {
            var line = ApplyPagesInfo.LineOf(loss);
            Assert.Equal(isOriginal, line.Contains(original, StringComparison.Ordinal));
            Assert.Equal(!isOriginal, line.Contains(fresh, StringComparison.Ordinal));
        }

        // Numbers are 1-based, in the language's own wording.
        Assert.Contains(language == "en" ? "originally pages 2, 3" : "ursprünglich Seiten 2, 3", ApplyPagesInfo.LineOf(sources[0].Loss), StringComparison.Ordinal);
        Assert.Contains(language == "en" ? "page 1 of the new file" : "Seite 1 der neuen Datei", ApplyPagesInfo.LineOf(sources[5].Loss), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AFileThatHasOtherPagesNowIsNotRewritten()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        Stage(tab, delete: [0]);

        // The path names a file of one page now: the plan was made for six, so nothing is built or written.
        var other = Path.Combine(_host.Folder, "other.pdf");
        File.Copy(Pagewing.TestSupport.TestData.Pdf("hello_world.pdf"), other);
        File.Move(path, Path.Combine(_host.Folder, "moved.pdf"));
        File.Move(other, path);
        var foreign = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        _host.PassTheLock(window);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveInOriginal));
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        Assert.Equal(Strings.ApplyErrorNotPrepared, viewModel.PendingSave!.Message);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
    }

    [AvaloniaFact]
    public async Task CtrlSOpensTheApplyCardWhileAPlanIsPending()
    {
        var (window, viewModel, view, tab, _) = await OrganizedAsync("pages-marked.pdf");
        Assert.False(tab.SaveCommand.CanExecute(null));
        Stage(tab, delete: [0, 1]);

        // A plan makes the tab saveable although nothing else is unsaved; Ctrl+S opens the card of "Apply".
        Assert.False(tab.IsDirty);
        Assert.True(tab.SaveCommand.CanExecute(null));
        view.Focus();
        Press(window, Key.S, PhysicalKey.S, Avalonia.Input.RawInputModifiers.Control);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.Equal("6 → 4 pages", viewModel.PendingSave!.Apply!.Summary);

        // The keys of the document do nothing behind the card, and Esc answers "Back".
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(viewModel.PendingSave);
        Assert.True(tab.HasPendingPageEdits);

        // "Save as" (Ctrl+Shift+S) leads to the same card.
        Press(window, Key.S, PhysicalKey.S, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        viewModel.PendingSave!.Abandon();
    }

    [AvaloniaFact]
    public async Task ApplyIsBlockedWhilePrintingAndViceVersa()
    {
        var (window, viewModel, _, tab, _) = await OrganizedAsync("pages-marked.pdf");
        Stage(tab, delete: [0]);
        Assert.True(tab.ApplyPageEditsCommand.CanExecute(null));

        // A print job runs: no apply, no Ctrl+S card, the reason is the one of saving.
        tab.IsPrinting = true;
        Assert.False(tab.ApplyPageEditsCommand.CanExecute(null));
        Assert.False(tab.SaveCommand.CanExecute(null));
        Assert.Equal(Strings.SaveBlockedPrinting, tab.SaveBlockedReason);
        Assert.False(await viewModel.ApplyPageEditsAsync(tab));
        Assert.Null(viewModel.PendingSave);
        tab.IsPrinting = false;
        Assert.True(tab.ApplyPageEditsCommand.CanExecute(null));

        // While the apply asks or runs, printing and editing the plan wait.
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.False(viewModel.PrintCommand.CanExecute(null));
        Assert.False(tab.ApplyPageEditsCommand.CanExecute(null));
        Assert.False(tab.UndoCommand.CanExecute(null));
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        Assert.Equal([0], tab.PagePlan!.Slots.Select(slot => slot.ExtraRotation).Distinct());
        viewModel.PendingSave!.Abandon();
        Assert.False(await apply);
        Assert.True(viewModel.PrintCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task EscCancelsTheCardNotThePlan()
    {
        var (window, viewModel, view, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Stage(tab, delete: [0, 1]);

        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Press(window, Key.Escape, PhysicalKey.Escape);

        Assert.False(await apply);
        Assert.Null(viewModel.PendingSave);
        Assert.True(tab.HasPendingPageEdits);
        Assert.True(tab.IsOrganizing);
        Assert.Equal(4, tab.PagePlan!.Slots.Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(0, _host.Picker.Calls);
        Assert.Same(tab.Session.Document, view.Document);
    }

    [AvaloniaFact]
    public async Task APlanTheReportRefusesIsNeverWritten()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Stage(tab, delete: [0]);
        var rewrites = 0;
        viewModel.PageRewriter = (_, _, _, _, _) =>
        {
            rewrites++;
            throw new InvalidOperationException("must not be called");
        };
        viewModel.PageAnalyzer = (_, _, _, _) => Task.FromResult(new PageEditReport([new PageLoss(PageLossKind.EncryptionNotPreserved, PageLossDetail.Source(1), null, Blocks: true)]));

        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        // The reason, "Cancel" alone, and no card with the choice to write.
        Assert.Equal(Strings.ApplyErrorRefusedEncrypted, viewModel.PendingSave!.Message);
        Assert.Equal([PendingSaveChoice.Cancel], viewModel.PendingSave.Choices);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.Equal(0, rewrites);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task ARotatedMultiLineTextPageIsNotRefusedByTheFingerprintCheck()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("text-rich.pdf");
        tab.SelectPages([0, 1]);
        tab.RotateSelectionCommand.Execute(90);
        _host.Picker.Result = Path.Combine(_host.Folder, "turned.pdf");

        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs);

        // PDFium reads a turned page of several lines in another order: the check must still accept the correct file.
        Assert.True(await apply);
        await WaitUntilAsync(window, () => tab.FullPath == Path.GetFullPath(_host.Picker.Result));
        Assert.Equal(90, await tab.Document!.GetPageRotationAsync(0, TestContext.Current.CancellationToken));
        Assert.Equal(90, await tab.Document.GetPageRotationAsync(1, TestContext.Current.CancellationToken));
        Assert.NotEqual(path, tab.FullPath);
    }

    [AvaloniaFact]
    public async Task CloseNamesThePageChangesAndNeverDropsThemUnnamed()
    {
        var (window, viewModel, _, tab, _) = await OrganizedAsync("pages-marked.pdf");
        await MarkAsync(window, tab);
        Stage(tab, delete: [0]);

        // Comments and a plan: "Don't save" would drop both, so the text says that the page changes go as well.
        var resolve = viewModel.ResolveUnsavedChangesAsync();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.Equal(Strings.SaveChangesWithPagesText, viewModel.PendingSave!.Message);
        Assert.Contains("page", viewModel.PendingSave.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await resolve);

        // Without a plan the text is the old one.
        tab.DiscardPageEditsCommand.Execute(null);
        resolve = viewModel.ResolveUnsavedChangesAsync();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.Equal(Strings.SaveChangesText, viewModel.PendingSave!.Message);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await resolve);
    }

    [AvaloniaFact]
    public async Task SavingBeforeClosingSavesTheCommentsAndThenAsksAboutThePlan()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab);
        Stage(tab, delete: [0]);

        var resolve = viewModel.ResolveUnsavedChangesAsync();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Save);

        // "Save" saves the comments (it does not apply the plan: no rewrite); the plan is asked about on its own.
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        Assert.Equal(Strings.DiscardPageEditsText, viewModel.PendingSave!.Message);
        Assert.False(tab.IsDirty);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(6, tab.Document!.PageCount);
        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(original, saved[..original.Length]);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await resolve);
    }

    [AvaloniaFact]
    public async Task TypedTextThatWasRefusedIsNeverDroppedByTheApply()
    {
        var (window, viewModel, _, tab, path) = await OrganizedAsync("pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Stage(tab, delete: [0]);

        // A text box text that could not be applied waits at the tab (it survives entering the mode): it counts as unsaved.
        tab.PendingTextBoxDraft = new TextBoxDraft(0, new Core.Text.PageRect(50, 50, 150, 80), null, "kept", 12, new Core.Annotations.AnnotationColor(0, 0, 0));
        Assert.True(tab.HasTextBoxText);
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);

        Assert.Equal(Strings.ApplyErrorDraft, viewModel.PendingSave!.Message);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await apply);
        Assert.NotNull(tab.PendingTextBoxDraft);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task NoTextCallsAnythingLegallyBindingOrValid(string language)
    {
        Strings.Culture = new CultureInfo(language);
        using var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        var path = Path.Combine(_host.Folder, "a.pdf");
        var info = ApplyPagesInfo.Create(PageEditPlan.Identity(6).Delete(new HashSet<int> { 0 }), EveryKindOfLoss());
        var texts = new List<string>
        {
            info.Summary,
            Strings.ApplyLossesHeading,
            Strings.ApplyChoiceBack,
        };
        texts.AddRange(info.Lines);
        foreach (var question in new[]
        {
            PendingSave.ApplyPages(tab, path, info),
            PendingSave.SaveAnnotationsFirst(tab, path),
            PendingSave.SaveChanges(tab, path),
            PendingSave.PageApplyError(tab, path, static () => Strings.ApplyErrorNotPrepared),
            PendingSave.PageApplyError(tab, path, static () => Strings.ApplyErrorRefusedEncrypted),
            PendingSave.PageApplyError(tab, path, static () => Strings.ApplyErrorDraft),
            PendingSave.PageApplyError(tab, path, static () => Strings.ApplyErrorFileChanged),
            PendingSave.PageApplyViewFailed(tab, path),
        })
        {
            texts.Add(question.Title);
            texts.Add(question.Message);
        }

        texts.AddRange(Enum.GetValues<PendingSaveChoice>().Select(PendingSave.LabelOf));
        texts.Add(Strings.SaveChangesWithPagesText);
        await Task.CompletedTask;

        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        foreach (var text in texts)
        {
            Assert.DoesNotContain("rechtsgültig", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legally", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(ValidWord(), text);
        }

        // The signature line says "invalid", the one thing it may claim.
        Assert.Matches(language == "en" ? "invalid" : "ungültig", Strings.ApplyLossSignature);
    }

    [AvaloniaFact]
    public async Task EveryNewTextExistsInBothLanguages()
    {
        var keys = typeof(Strings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(property => property.Name)
            .Where(name => name.StartsWith("Apply", StringComparison.Ordinal) || name.StartsWith("SaveFirst", StringComparison.Ordinal)
                || name is "SaveChangesWithPagesText" or "SaveChoiceSaveInOriginal" or "PagesOriginalOne" or "PagesOriginalMany" or "PagesNewOne" or "PagesNewMany")
            .ToList();
        Assert.True(keys.Count >= 30);
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            foreach (var key in keys)
            {
                var value = (string)typeof(Strings).GetProperty(key)!.GetValue(null)!;
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} in {language}");
                Assert.DoesNotMatch(ValidWord(), value);
            }
        }

        Strings.Culture = new CultureInfo("en");
        var english = keys.Select(key => (string)typeof(Strings).GetProperty(key)!.GetValue(null)!).ToList();
        Strings.Culture = new CultureInfo("de");
        var german = keys.Select(key => (string)typeof(Strings).GetProperty(key)!.GetValue(null)!).ToList();
        Assert.Equal(english.Count, german.Count);
        Assert.NotEqual(english, german);
        await Task.CompletedTask;
    }

    private static PageEditReport EveryKindOfLoss() => new(
    [
        new PageLoss(PageLossKind.ForeignOutlineDropped, PageLossDetail.Source(2), null),
        new PageLoss(PageLossKind.BookmarkToDeletedPage, null, [1, 2]),
        new PageLoss(PageLossKind.DuplicatedFormFields, PageLossDetail.Duplicated, [0, 3]),
        new PageLoss(PageLossKind.DuplicatedFormFields, PageLossDetail.Inserted, [5]),
        new PageLoss(PageLossKind.FormFieldNameCollision, null, [4]),
        new PageLoss(PageLossKind.FormFieldNameCollision, PageLossDetail.Unchecked, null),
        new PageLoss(PageLossKind.SignatureInvalidated, PageLossDetail.DocMdp(1), null),
        new PageLoss(PageLossKind.EncryptionNotPreserved, PageLossDetail.Source(2), null),
        new PageLoss(PageLossKind.InternalLinkLost, PageLossDetail.DeletedPage, [0]),
        new PageLoss(PageLossKind.InternalLinkLost, PageLossDetail.CopiedPage, [1, 2, 3]),
        new PageLoss(PageLossKind.PageLabelsShifted, PageLossDetail.Shifted, null),
        new PageLoss(PageLossKind.PageLabelsShifted, PageLossDetail.Source(3), null),
        new PageLoss(PageLossKind.OrphanFormFields, null, [2]),
    ]);

    /// <summary>Opens a copy of <paramref name="fixture"/> in a new tab and enters the mode "Organize pages".</summary>
    private async Task<(MainWindow Window, MainViewModel ViewModel, DocumentView View, TabViewModel Tab, string Path)> OrganizedAsync(
        string fixture, Pagewing.App.Services.RecentFilesService? recent = null)
    {
        var (window, viewModel, view) = _host.Show(recent);
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, fixture);
        tab.OrganizeCommand.Execute(null);
        Assert.True(tab.IsOrganizing);
        Settle(window);
        return (window, viewModel, view, tab, path);
    }

    private static void Stage(TabViewModel tab, int[]? delete = null, int[]? rotate = null)
    {
        if (rotate is not null)
        {
            tab.SelectPages(rotate);
            tab.RotateSelectionCommand.Execute(90);
        }

        if (delete is not null)
        {
            tab.SelectPages(delete);
            tab.DeletePagesCommand.Execute(null);
        }

        Assert.True(tab.HasPendingPageEdits);
    }

    [GeneratedRegex(@"(?<![\p{L}])(valid|gültig)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValidWord();
}
