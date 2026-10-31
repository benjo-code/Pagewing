using System.Diagnostics;
using System.Globalization;
using System.Text;
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
using Pagewing.Core.Viewing;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// "Insert pages from a file" in the mode "Organize pages" (milestone 13, task 10): the dialog (file, password, range,
/// position), the files the tab holds for its plan, their thumbnails, their release, and the way the apply uses them: the
/// documents for the report, the files and the stamps taken when they were inserted for the rewrite. Merging is inserting a
/// whole file. Every file is a copy in a temp folder; nothing is written beside the inputs.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class InsertFileTests : IDisposable
{
    private readonly SaveCardHost _host = new();
    private readonly CultureInfo _originalCulture = Strings.Culture;

    public void Dispose()
    {
        _host.Dispose();
        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task InsertAppendsTheSelectedRangeAfterTheSelection()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var other = _host.Copy("text-rich.pdf", "other.pdf");

        // Nothing selected: the dialog says "at the end", and the pages land there and are selected.
        var dialog = await ChooseAsync(window, viewModel, tab, other);
        Assert.Equal(Strings.InsertFilePositionEnd, dialog.PositionText);
        Assert.Equal("all", dialog.RangeText);
        Assert.Equal(2, dialog.PageCount);
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        var plan = tab.PagePlan!;
        Assert.Equal(8, plan.Slots.Count);
        Assert.Equal([new PageSlot(new PageSourceId(1), 0, 0, null), new PageSlot(new PageSourceId(1), 1, 0, null)], plan.Slots.Skip(6));
        Assert.Equal([6, 7], tab.SelectedPagePositions.Order());
        Assert.True(tab.HasPendingPageEdits);
        Assert.Single(tab.InsertedSources);

        // A selection: after its last page, a range of the file; the dialog names the page.
        tab.UndoPagesCommand.Execute(null);
        tab.SelectPages([1, 2]);
        dialog = await ChooseAsync(window, viewModel, tab, other);
        Assert.Equal(Fmt(Strings.InsertFilePositionAfter, 3), dialog.PositionText);
        dialog.RangeText = "2";
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        plan = tab.PagePlan!;
        Assert.Equal(7, plan.Slots.Count);
        Assert.Equal(new PageSlot(new PageSourceId(2), 1, 0, null), plan.Slots[3]);
        Assert.Equal([3], tab.SelectedPagePositions.Order());
        Assert.Equal(2, tab.InsertedSources.Count);
    }

    [AvaloniaFact]
    public async Task RangeSyntaxIsThePrintSyntax()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var other = _host.Copy("pages-marked.pdf", "other.pdf");
        var dialog = await ChooseAsync(window, viewModel, tab, other);
        Assert.Equal(6, dialog.PageCount);

        // Mistakes stay in the dialog with a reason on the field; nothing is staged, nothing is opened twice.
        var clock = Stopwatch.StartNew();
        foreach (var text in new[] { "5-3", "0", "abc", "", "1-", "7", "1-2000000000", "2000000000", "4294967296" })
        {
            dialog.RangeText = text;
            dialog.InsertCommand.Execute(null);
            Assert.NotNull(dialog.RangeError);
            Assert.False(tab.HasPendingPageEdits);
            Assert.Equal(OverlayKind.InsertFile, viewModel.Overlay);
            Assert.Empty(tab.InsertedSources);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "a huge range must not stall");

        // The message names the file's size when a page is outside, and the syntax when the text is no range.
        dialog.RangeText = "7";
        dialog.InsertCommand.Execute(null);
        Assert.Equal(Fmt(Strings.InsertFileRangeOutside, Fmt(Strings.PageCounterSame, 6), 7), dialog.RangeError);
        dialog.RangeText = "5-3";
        dialog.InsertCommand.Execute(null);
        Assert.Equal(Strings.InsertFileRangeInvalid, dialog.RangeError);

        // The same syntax as printing: lists and ranges, in file order, once each.
        dialog.RangeText = "1-3,5";
        Assert.Null(dialog.RangeError);
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        Assert.Equal([0, 1, 2, 4], tab.PagePlan!.Slots.Skip(6).Select(slot => slot.SourceIndex));

        dialog = await ChooseAsync(window, viewModel, tab, other);
        Assert.Equal("all", dialog.RangeText);
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        Assert.Equal(16, tab.PagePlan!.Slots.Count);
    }

    [AvaloniaFact]
    public async Task EncryptedFileAsksForThePasswordAndWrongPasswordIsReported()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var secret = _host.Copy("encrypted_hello_world_r3.pdf", "secret.pdf");

        // The existing prompt: the file needs a password; the dialog asks for it and offers no pages yet.
        var dialog = await ChooseAsync(window, viewModel, tab, secret, InsertFileState.NeedsPassword);
        Assert.False(dialog.IsPasswordIncorrect);
        Assert.False(dialog.InsertCommand.CanExecute(null));
        Assert.Equal(Fmt(Strings.InsertFilePasswordPrompt, "secret.pdf"), dialog.PasswordPrompt);

        // A wrong password is told in words; the field stays for another try; nothing was added.
        dialog.Password = "wrong";
        dialog.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => dialog is { State: InsertFileState.NeedsPassword, IsPasswordIncorrect: true });
        Assert.Empty(tab.InsertedSources);
        Assert.False(tab.HasPendingPageEdits);
        var card = Find<InsertFileCard>(window, "InsertFileCard");
        Assert.True(Find<TextBlock>(card, "InsertFilePasswordMessage").IsEffectivelyVisible);
        Assert.Equal(Strings.PasswordIncorrect, Find<TextBlock>(card, "InsertFilePasswordMessage").Text);

        // The right one opens the file; the pages go in, and the plan remembers the password for the rewrite.
        dialog.Password = "hôtel";
        dialog.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => dialog.State == InsertFileState.Ready);
        Assert.Equal(1, dialog.PageCount);
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        var source = Assert.Single(tab.InsertedSources).Value;
        Assert.Equal("hôtel", source.File.Password);
        Assert.Equal(7, tab.PagePlan!.Slots.Count);
    }

    [AvaloniaFact]
    public async Task NotAPdfIsReported()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var junk = _host.Copy("not-a-pdf.pdf", "junk.pdf");

        var dialog = await ChooseAsync(window, viewModel, tab, junk, InsertFileState.Failed);
        Assert.Equal(Fmt(Strings.ErrorCause, "junk.pdf", Strings.ErrorNotAPdf), dialog.ErrorText);
        Assert.False(dialog.InsertCommand.CanExecute(null));
        var card = Find<InsertFileCard>(window, "InsertFileCard");
        Assert.Equal(Fmt(Strings.ErrorCause, "junk.pdf", Strings.ErrorNotAPdf), Find<TextBlock>(card, "InsertFileError").Text);
        Assert.Empty(tab.InsertedSources);
        Assert.False(tab.HasPendingPageEdits);

        // A file that is gone by the time it is opened is told as well; "Choose another file" asks for a new one.
        var missing = Path.Combine(_host.Folder, "missing.pdf");
        _host.Picker.OpenResult = missing;
        dialog.ChooseOtherCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.InsertFile is { State: InsertFileState.Failed } next && next.ErrorText == Fmt(Strings.ErrorCause, "missing.pdf", Strings.ErrorFileNotFound));
        Assert.Empty(tab.InsertedSources);
    }

    [AvaloniaFact]
    public async Task TwentyFirstFileIsRefused()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var single = _host.Copy("hello_world.pdf", "single.pdf");
        for (var i = 0; i < PageEditLimits.MaxInsertedFiles; i++)
        {
            var copy = Path.Combine(_host.Folder, $"file-{i:00}.pdf");
            File.Copy(single, copy);
            await InsertAsync(window, viewModel, tab, copy);
        }

        Assert.Equal(PageEditLimits.MaxInsertedFiles, tab.InsertedSources.Count);
        Assert.Equal(6 + PageEditLimits.MaxInsertedFiles, tab.PagePlan!.Slots.Count);

        // The 21st is refused before the file dialog: nothing is picked, nothing is opened, the plan is as it was.
        var calls = _host.Picker.OpenCalls;
        var plan = tab.PagePlan;
        _host.Picker.OpenResult = single;
        tab.InsertFileCommand.Execute(null);
        Settle(window);
        Assert.Equal(calls, _host.Picker.OpenCalls);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Null(viewModel.InsertFile);
        Assert.Equal(Strings.PageEditsFileLimit, tab.PageEditNotice);
        Assert.Equal(PageEditLimits.MaxInsertedFiles, tab.InsertedSources.Count);
        Assert.Same(plan, tab.PagePlan);
    }

    [AvaloniaTheory]
    [InlineData("touched")]
    [InlineData("replaced")]
    [InlineData("deleted")]
    public async Task InsertedSourceChangedBeforeApplyFailsWithReasonAndKeepsThePlan(string change)
    {
        var (window, viewModel, tab, path) = await OrganizedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var other = _host.Copy("text-rich.pdf", "other.pdf");
        await InsertAsync(window, viewModel, tab, other);
        var plan = tab.PagePlan;
        var target = Path.Combine(_host.Folder, "merged.pdf");
        var rewrites = 0;
        viewModel.PageRewriter = (_, _, _, _, _) =>
        {
            rewrites++;
            throw new InvalidOperationException("the rewrite must not start");
        };

        // After the insert, the file is changed behind the back of the program.
        switch (change)
        {
            case "touched":
                File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(3));
                break;
            case "replaced":
                await File.WriteAllBytesAsync(other, [1, 2, 3], TestContext.Current.CancellationToken);
                break;
            default:
                File.Delete(other);
                break;
        }

        _host.Picker.Result = target;
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));

        // The card names the inserted file, with "Cancel" alone; nothing was written and the plan is as it was.
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        var question = viewModel.PendingSave!;
        Assert.Equal(Fmt(Strings.ApplyErrorInsertedChanged, "other.pdf"), question.Message);
        Assert.Equal([PendingSaveChoice.Cancel], question.Choices);
        Assert.True(question.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Equal(0, rewrites);
        Assert.False(File.Exists(target));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
        Assert.Same(plan, tab.PagePlan);
        Assert.True(tab.HasPendingPageEdits);
        Assert.True(tab.IsOrganizing);
        Assert.Single(tab.InsertedSources);
        Assert.Equal(2, tab.InsertedSources.Single().Value.PageCount);
        Assert.False(tab.IsSaving);
    }

    [AvaloniaFact]
    public async Task ForeignThumbnailsComeFromTheirOwnSource()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var list = window.GetVisualDescendants().OfType<ThumbnailList>().Single();
        var wide = _host.Copy("pages-marked.pdf", "wide.pdf");
        tab.SelectPages([1]);
        var dialog = await ChooseAsync(window, viewModel, tab, wide);

        // Page 3 of the other file is the landscape one (400 x 300); the entry has its size, not the A4 stand-in and not the
        // size of the page of this document at the same index.
        dialog.RangeText = "3";
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        var source = Assert.Single(tab.InsertedSources).Value;
        await WaitUntilAsync(window, () => source.Sizes.Count == source.PageCount);
        await WaitUntilAsync(window, () => list.Thumbnails.Count == 7 && list.Thumbnails[2].Bitmap is not null);
        var foreign = list.Thumbnails[2];
        Assert.Equal(PageKind.Foreign, foreign.Kind);
        Assert.True(foreign.ShowsImage);
        Assert.False(foreign.ShowsStandIn);
        Assert.True(foreign.FrameWidth > foreign.FrameHeight, "the landscape page is wider than high");
        Assert.Equal(400.0 / 300.0, foreign.FrameWidth / foreign.FrameHeight, 2);
        var picture = foreign.Bitmap!.PixelSize;
        Assert.True(picture.Width > picture.Height);

        // The picture comes from the thumbnails of the inserted file: they exist, hold page 3 and are not the tab's own.
        Assert.NotNull(source.Thumbnails);
        Assert.True(source.Thumbnails.Cache.TryGet(new TileKey(2, TileKey.PreviewScaleKey, 0, 0), out _));
        Assert.NotSame(list.Source, source.Thumbnails);

        // Turned like the pages of this document: the entry lies on its side and says so; its sides swap.
        tab.SelectPages([2]);
        tab.RotateSelectionCommand.Execute(90);
        Settle(window);
        var turned = list.Thumbnails[2];
        Assert.Equal(90, turned.Rotation);
        Assert.NotNull(turned.ImageTransform);
        Assert.True(turned.FrameHeight > turned.FrameWidth);
        Assert.NotNull(turned.Bitmap);
        Assert.Contains(Strings.PageMarkerOtherFile, turned.AutomationName, StringComparison.Ordinal);

        // A neighbour of this document keeps its own size and picture.
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        Assert.True(list.Thumbnails[0].FrameHeight > list.Thumbnails[0].FrameWidth);
    }

    [AvaloniaFact]
    public async Task BlankPageNextToAnInsertedPageHasItsRealSize()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var wide = _host.Copy("pages-marked.pdf", "wide.pdf");
        tab.SelectPages([5]);
        var dialog = await ChooseAsync(window, viewModel, tab, wide);
        dialog.RangeText = "3";
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        var source = Assert.Single(tab.InsertedSources).Value;
        await WaitUntilAsync(window, () => source.Sizes.Count == source.PageCount);

        // The blank page after the inserted landscape page is as large as it (400 x 300), not A4 (595 x 842).
        tab.SelectPages([6]);
        tab.InsertBlankPageCommand.Execute(null);
        Assert.Equal(new BlankPage(400, 300), tab.PagePlan!.Slots[7].Blank);

        // Turned by 90 degrees the inserted page lies the other way round, and so does the blank page beside it.
        tab.SelectPages([6]);
        tab.RotateSelectionCommand.Execute(90);
        tab.SelectPages([6]);
        tab.InsertBlankPageCommand.Execute(null);
        Assert.Equal(new BlankPage(300, 400), tab.PagePlan.Slots[7].Blank);
    }

    [AvaloniaFact]
    public async Task InsertedFilesAreClosedOnDiscardAndClose()
    {
        var (window, viewModel, tab, path) = await OrganizedAsync();
        var list = window.GetVisualDescendants().OfType<ThumbnailList>().Single();
        var other = _host.Copy("text-rich.pdf", "other.pdf");
        await InsertAsync(window, viewModel, tab, other);
        var source = tab.InsertedSources.Single().Value;
        await WaitUntilAsync(window, () => list.Thumbnails.Count == 8 && list.Thumbnails[6].Bitmap is not null);
        var thumbnails = source.Thumbnails!;
        Assert.Equal(2, source.Document.PageCount);

        // Discard: thumbnails first, then the document; the tab holds nothing any more.
        tab.DiscardPageEditsCommand.Execute(null);
        Settle(window);
        Assert.Empty(tab.InsertedSources);
        Assert.True(thumbnails.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => source.Document.PageCount);

        // The ids do not come back: a later file is another source.
        tab.OrganizeCommand.Execute(null);
        var second = await InsertAsync(window, viewModel, tab, _host.Copy("text-rich.pdf", "again.pdf"));
        Assert.Equal(new PageSourceId(2), second);
        var closing = tab.InsertedSources.Single().Value;

        // Closing the tab (after the question about the page changes) closes the inserted document as well.
        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.Discard)!);
        await WaitUntilAsync(window, () => !viewModel.Tabs.Contains(tab));
        Assert.Throws<ObjectDisposedException>(() => closing.Document.PageCount);
        Assert.Empty(tab.InsertedSources);
        Assert.True(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task InsertedSourcesTravelWithTheTab()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var (otherWindow, otherViewModel, _) = _host.Show();
        await InsertAsync(window, viewModel, tab, _host.Copy("text-rich.pdf", "other.pdf"));
        var source = tab.InsertedSources.Single().Value;
        var document = source.Document;
        var plan = tab.PagePlan;

        // The tab, the plan and the inserted file move together; the file stays open and its pages are drawn in the new window.
        Assert.True(viewModel.DetachTab(tab));
        otherViewModel.AttachTab(tab, 0);
        Settle(otherWindow);
        Assert.Same(source, tab.InsertedSources.Single().Value);
        Assert.Same(document, source.Document);
        Assert.Equal(2, document.PageCount);
        Assert.Same(plan, tab.PagePlan);
        var list = otherWindow.GetVisualDescendants().OfType<ThumbnailList>().Single();
        await WaitUntilAsync(otherWindow, () => list.Thumbnails.Count == 8 && list.Thumbnails[6].Bitmap is not null);

        // And it is the new window that closes it.
        tab.DiscardPageEditsCommand.Execute(null);
        Assert.Throws<ObjectDisposedException>(() => document.PageCount);
    }

    [AvaloniaFact]
    public async Task MergeResultHasPagesOfBothFilesInOrder()
    {
        var (window, viewModel, tab, path) = await OrganizedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var other = _host.Copy("text-rich.pdf", "other.pdf");
        var list = window.GetVisualDescendants().OfType<ThumbnailList>().Single();

        // Both pages of the other file go behind page 2 of this one.
        tab.SelectPages([1]);
        await InsertAsync(window, viewModel, tab, other);
        await WaitUntilAsync(window, () => list.Thumbnails.Count == 8 && list.Thumbnails[2].Bitmap is not null);
        var source = tab.InsertedSources.Single().Value;
        var inserted = source.Document;
        var thumbnails = source.Thumbnails!;
        var target = Path.Combine(_host.Folder, "merged.pdf");
        _host.Picker.Result = target;

        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        var info = viewModel.PendingSave!.Apply!;
        Assert.Equal("6 → 8 pages, 1 file inserted", info.Summary);

        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.SaveAs));
        Assert.True(await apply);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage && tab.PageCount == 8);

        // The tab shows the written file: the pages of both files in the order of the plan, text per page.
        Assert.Equal(Path.GetFullPath(target), tab.FullPath);
        string[] expected = ["PAGE 1", "PAGE 2", "Pagewing Handbuch", "Seite zwei", "PAGE 3", "PAGE 4", "PAGE 5", "PAGE 6"];
        var written = await new PdfEngine().OpenAsync(target, null, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(8, written.PageCount);
            for (var i = 0; i < expected.Length; i++)
            {
                var text = await written.GetTextAsync(i, TestContext.Current.CancellationToken);
                Assert.Contains(expected[i], text, StringComparison.Ordinal);
            }
        }
        finally
        {
            await written.DisposeAsync();
        }

        // The inserted file and its thumbnails are closed, the plan is gone, the original and the inserted file are untouched.
        Assert.Empty(tab.InsertedSources);
        Assert.True(thumbnails.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => inserted.PageCount);
        Assert.Null(tab.PagePlan);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(other));
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.tmp"));
    }

    [AvaloniaFact]
    public async Task TheReportIsReadFromTheInsertedDocumentsAndNamesTheirFiles()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var outline = _host.Copy("outline.pdf", "outline-copy.pdf");
        await InsertAsync(window, viewModel, tab, outline);

        // The bookmarks of the inserted file are not taken over: the real analysis of the document the tab holds says so, and
        // names the file the user chose, not a number.
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        var lines = viewModel.PendingSave!.Apply!.Lines;
        Assert.Contains(Fmt(Strings.ApplyLossForeignOutline, "outline-copy.pdf"), lines);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Single(tab.InsertedSources);
    }

    [AvaloniaFact]
    public async Task InsertingTheFileOfTheOpenDocumentItselfWorks()
    {
        var (window, viewModel, tab, path) = await OrganizedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // The same file is just another file: its pages are copied in from the disk, as a second copy of the document.
        tab.SelectPages([5]);
        var id = await InsertAsync(window, viewModel, tab, path, "1-2");
        Assert.Equal(new PageSourceId(1), id);
        Assert.Equal(8, tab.PagePlan!.Slots.Count);
        var target = Path.Combine(_host.Folder, "twice.pdf");
        _host.Picker.Result = target;
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<ApplyPagesCard>(window, viewModel, PendingSaveKind.ApplyPages);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs));
        Assert.True(await apply);
        await WaitUntilAsync(window, () => tab.PageCount == 8);
        var written = await new PdfEngine().OpenAsync(target, null, TestContext.Current.CancellationToken);
        try
        {
            string[] expected = ["PAGE 1", "PAGE 2", "PAGE 3", "PAGE 4", "PAGE 5", "PAGE 6", "PAGE 1", "PAGE 2"];
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Contains(expected[i], await written.GetTextAsync(i, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            }
        }
        finally
        {
            await written.DisposeAsync();
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task EncryptedInsertIntoAPlainDocumentIsRefusedWithItsReason()
    {
        var (window, viewModel, tab, path) = await OrganizedAsync();
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var rewrites = 0;
        viewModel.PageRewriter = (_, _, _, _, _) =>
        {
            rewrites++;
            throw new InvalidOperationException("the rewrite must not start");
        };
        var secret = _host.Copy("encrypted_hello_world_r3.pdf", "secret.pdf");
        var dialog = await ChooseAsync(window, viewModel, tab, secret, InsertFileState.NeedsPassword);
        dialog.Password = "hôtel";
        dialog.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => dialog.State == InsertFileState.Ready);
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);

        // Its pages would be written unprotected: the apply says why it refuses (the real report), writes nothing, keeps the plan.
        var apply = viewModel.ApplyPageEditsAsync(tab);
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        Assert.Equal(Strings.ApplyErrorRefusedEncrypted, viewModel.PendingSave!.Message);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await apply);
        Assert.Equal(0, rewrites);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Single(tab.InsertedSources);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task SourceIdsAreNeverReusedAfterTheirPagesAreGone()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var first = await InsertAsync(window, viewModel, tab, _host.Copy("hello_world.pdf", "a.pdf"));
        Assert.Equal(new PageSourceId(1), first);

        // Deleting every page of the file, and undoing, does not free the id: the slot of an undone step may come back.
        tab.SelectPages([6]);
        tab.DeletePagesCommand.Execute(null);
        Assert.DoesNotContain(tab.PagePlan!.Slots, slot => slot.Source == first);
        var second = await InsertAsync(window, viewModel, tab, _host.Copy("hello_world.pdf", "b.pdf"));
        Assert.Equal(new PageSourceId(2), second);
        tab.UndoPagesCommand.Execute(null);
        tab.UndoPagesCommand.Execute(null);
        Assert.Contains(tab.PagePlan!.Slots, slot => slot.Source == first);
        Assert.Equal(2, tab.InsertedSources.Count);
        Assert.Equal("a.pdf", tab.InsertedSources[first].Name);
    }

    [AvaloniaFact]
    public async Task DialogIsKeyboardCompleteAndEscCancelsTheDialogNotThePlan()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var other = _host.Copy("pages-marked.pdf", "other.pdf");
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        var plan = tab.PagePlan;

        // The command is in the catalog (only in the mode), the keys open the dialog; the range field has the focus.
        var command = viewModel.Commands.ById("pages.insertFile")!;
        Assert.NotNull(command.Gesture);
        Assert.True(command.CanExecute());
        _host.Picker.OpenResult = other;
        Press(window, command.Gesture!.Key, PhysicalKeyOf(command.Gesture.Key), command.Gesture.KeyModifiers.ToRaw());
        await WaitUntilAsync(window, () => viewModel.InsertFile is { State: InsertFileState.Ready });
        var card = Find<InsertFileCard>(window, "InsertFileCard");
        var range = Find<TextBox>(card, "InsertFileRange");
        await WaitUntilAsync(window, () => range.IsFocused);
        var dialog = viewModel.InsertFile!;
        var document = dialog.Source!.Document;

        // Every control is reachable with Tab and has a name for assistive technology; Tab never holds the focus.
        foreach (var name in new[] { "InsertFileRange", "InsertFileButton", "InsertFileChooseOtherButton", "InsertFileCancelButton", "InsertFileCloseButton" })
        {
            var control = Find<Control>(card, name);
            Assert.True(control.Focusable && control.IsEffectivelyEnabled, name);
            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(control)), name);
        }

        // Esc closes the dialog and the file it opened, and nothing else: the plan, the mode and the inserted files stay.
        Press(window, Key.Escape, PhysicalKey.Escape);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        Assert.Same(plan, tab.PagePlan);
        Assert.True(tab.IsOrganizing);
        Assert.Empty(tab.InsertedSources);
        Assert.Throws<ObjectDisposedException>(() => document.PageCount);

        // Enter in the field inserts with the text typed there.
        tab.SelectPages([1]);
        _host.Picker.OpenResult = other;
        tab.InsertFileCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.InsertFile is { State: InsertFileState.Ready });
        range = Find<TextBox>(Find<InsertFileCard>(window, "InsertFileCard"), "InsertFileRange");
        await WaitUntilAsync(window, () => range.IsFocused);
        range.Text = "1-2";
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        Assert.Equal(8, tab.PagePlan!.Slots.Count);
        Assert.Equal([2, 3], tab.SelectedPagePositions.Order());
    }

    [AvaloniaFact]
    public async Task TheDialogGoesWithItsTabAndAnotherFileReplacesTheFirstOne()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var firstFile = _host.Copy("text-rich.pdf", "first.pdf");
        var secondFile = _host.Copy("pages-marked.pdf", "second.pdf");
        var first = await ChooseAsync(window, viewModel, tab, firstFile);
        var firstDocument = first.Source!.Document;

        // The dialog follows the selection of the list: it says where the pages would go.
        Assert.Equal(Strings.InsertFilePositionEnd, first.PositionText);
        tab.SelectPages([3]);
        Assert.Equal(Fmt(Strings.InsertFilePositionAfter, 4), first.PositionText);
        tab.ClearPageSelection();
        Assert.Equal(Strings.InsertFilePositionEnd, first.PositionText);

        // Cancelling the second file dialog keeps the dialog and its file as they were.
        _host.Picker.OpenResult = null;
        first.ChooseOtherCommand.Execute(null);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Settle(window);
        Assert.Same(first, viewModel.InsertFile);
        Assert.Equal(2, firstDocument.PageCount);

        // Another file replaces it, and the first file is closed.
        _host.Picker.OpenResult = secondFile;
        first.ChooseOtherCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.InsertFile is { State: InsertFileState.Ready } next && !ReferenceEquals(next, first));
        Assert.Throws<ObjectDisposedException>(() => firstDocument.PageCount);
        var second = viewModel.InsertFile!;
        var secondDocument = second.Source!.Document;
        Assert.Equal("second.pdf", second.FileName);
        Assert.Equal(6, second.PageCount);

        // The dialog belongs to the tab: when the tab is not in front any more, it goes and closes its file.
        await _host.OpenCopyAsync(window, viewModel, window.GetVisualDescendants().OfType<DocumentView>().Single(), "hello_world.pdf", "front.pdf");
        Assert.NotSame(tab, viewModel.ActiveTab);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Null(viewModel.InsertFile);
        Assert.Throws<ObjectDisposedException>(() => secondDocument.PageCount);
        Assert.Empty(tab.InsertedSources);
        Assert.True(tab.IsOrganizing);

        // And when the tab leaves the mode.
        viewModel.ActivateTabCommand.Execute(tab);
        Settle(window);
        var third = await ChooseAsync(window, viewModel, tab, firstFile);
        var thirdDocument = third.Source!.Document;
        tab.DiscardPageEditsCommand.Execute(null);
        Settle(window);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Throws<ObjectDisposedException>(() => thirdDocument.PageCount);
    }

    [AvaloniaFact]
    public async Task ThePasswordIsNeverPrintedAndNotKeptAfterUse()
    {
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var secret = _host.Copy("encrypted_hello_world_r3.pdf", "secret.pdf");

        // A wrong password stays in the field for another try; closing the dialog empties the field and the view model.
        var dialog = await ChooseAsync(window, viewModel, tab, secret, InsertFileState.NeedsPassword);
        var card = Find<InsertFileCard>(window, "InsertFileCard");
        var field = Find<TextBox>(card, "InsertFilePassword");
        dialog.Password = "wrong-secret";
        dialog.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => dialog.IsPasswordIncorrect);
        Assert.Equal("wrong-secret", field.Text);
        Press(window, Key.Escape, PhysicalKey.Escape);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        Assert.Equal(string.Empty, dialog.Password);
        Assert.True(string.IsNullOrEmpty(field.Text));

        // A right one is dropped from the dialog as soon as the file is open and after the pages went in.
        dialog = await ChooseAsync(window, viewModel, tab, secret, InsertFileState.NeedsPassword);
        dialog.Password = "hôtel";
        dialog.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => dialog.State == InsertFileState.Ready);
        Assert.Equal(string.Empty, dialog.Password);
        Assert.True(string.IsNullOrEmpty(Find<TextBox>(Find<InsertFileCard>(window, "InsertFileCard"), "InsertFilePassword").Text));
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);

        // The plan needs it for the rewrite and keeps it, but no text of the objects that carry it shows it.
        var source = Assert.Single(tab.InsertedSources).Value;
        Assert.Equal("hôtel", source.File.Password);
        string[] texts =
        [
            source.File.ToString(), source.ToString()!, dialog.ToString(), tab.InsertedFiles().ToString()!,
            new PageApplyRequest(tab.PagePlan!, "x.pdf", tab.InsertedFiles()).ToString(),
        ];
        Assert.All(texts, text => Assert.DoesNotContain("hôtel", text, StringComparison.Ordinal));
        Assert.Contains("(hidden)", source.File.ToString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AFileThatFailsInAnUnforeseenWayIsReportedInTheDialog()
    {
        var path = _host.Copy("text-rich.pdf", "other.pdf");
        using var dialog = new InsertFileViewModel(
            path,
            new PdfEngine(),
            (_, _) => null,
            () => string.Empty,
            (_, _, _) => throw new InvalidOperationException("boom with /secret/path"));

        await dialog.OpenAsync();

        // Not stuck on "Opening": the dialog says it failed, in the words of the card of a file that cannot be opened, with no technical text.
        Assert.Equal(InsertFileState.Failed, dialog.State);
        Assert.Equal(Fmt(Strings.ErrorCause, "other.pdf", Strings.ErrorUnknown), dialog.ErrorText);
        Assert.DoesNotContain("boom", dialog.ErrorText, StringComparison.Ordinal);
        Assert.False(dialog.InsertCommand.CanExecute(null));
        Assert.Null(dialog.Source);
    }

    [AvaloniaFact]
    public async Task TheCommandExistsOnlyWhileOrganizingAndWaitsForTheRulesOfThePageCommands()
    {
        var (window, viewModel, view) = _host.Show();
        var catalog = viewModel.Commands;
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "pages-marked.pdf");

        // Reading view: no command, no button, no entry; nothing of it was made.
        Assert.False(catalog.ById("pages.insertFile")!.CanExecute());
        Assert.False(tab.HasPageEditCommands);
        Assert.Empty(window.GetVisualDescendants().OfType<PagesOrganizeBar>());
        Assert.Null(viewModel.InsertFile);
        Assert.Empty(window.GetVisualDescendants().OfType<InsertFileCard>());
        Assert.Empty(tab.InsertedSources);

        // In the mode: the button of the bar, the entry of the context menu and the palette entry (the catalog) are there.
        tab.OrganizeCommand.Execute(null);
        Settle(window);
        Assert.True(catalog.ById("pages.insertFile")!.CanExecute());
        var bar = window.GetVisualDescendants().OfType<PagesOrganizeBar>().Single();
        var button = Find<Button>(bar, "InsertFileToolButton");
        Assert.Same(tab.InsertFileCommand, button.Command);
        Assert.Equal(Strings.CommandInsertFile, Avalonia.Automation.AutomationProperties.GetName(button));
        var list = window.GetVisualDescendants().OfType<ThumbnailList>().Single();
        Assert.Contains(list.ContextMenu!.Items.OfType<MenuItem>(), item => ReferenceEquals(item.Command, tab.InsertFileCommand));

        // Like the other page commands, it does not run while a card lies over the window.
        viewModel.ShowSettingsCommand.Execute(null);
        Settle(window);
        Assert.False(catalog.ById("pages.insertFile")!.CanExecute());
        viewModel.HideOverlay();
        Settle(window);
        Assert.True(catalog.ById("pages.insertFile")!.CanExecute());

        // And not while the plan is being written.
        tab.IsSaving = true;
        Assert.False(tab.InsertFileCommand.CanExecute(null));
        tab.IsSaving = false;
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task TextsExistInBothLanguagesAndNoneCallsAnythingLegallyBinding(string language)
    {
        Strings.Culture = new CultureInfo(language);
        string[] texts =
        [
            Strings.CommandInsertFile, Strings.PageEditsFileLimit, Strings.InsertFileTitle, Strings.InsertFileOpening, Strings.InsertFileRangeLabel,
            Strings.InsertFileRangeHint, Strings.InsertFileRangeInvalid, Strings.InsertFileRangeOutside, Strings.InsertFilePositionAfter,
            Strings.InsertFilePositionEnd, Strings.InsertFileButton, Strings.InsertFileChooseOther, Strings.InsertFileCancel, Strings.InsertFileClose,
            Strings.InsertFileFailedTitle, Strings.InsertFilePasswordPrompt, Strings.InsertFilePickFailed, Strings.ApplyErrorInsertedChanged,
        ];
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.All(texts, text => Assert.DoesNotContain("rechtsgültig", text, StringComparison.OrdinalIgnoreCase));
        Assert.All(texts, text => Assert.DoesNotContain("legally binding", text, StringComparison.OrdinalIgnoreCase));

        // The dialog shows them: a window in this language has the title and the buttons.
        var (window, viewModel, tab, _) = await OrganizedAsync();
        var dialog = await ChooseAsync(window, viewModel, tab, _host.Copy("text-rich.pdf", "other.pdf"));
        var card = Find<InsertFileCard>(window, "InsertFileCard");
        Assert.Equal(Strings.InsertFileTitle, Avalonia.Automation.AutomationProperties.GetName(card));
        Assert.Equal(Strings.InsertFileButton, (string?)Find<Button>(card, "InsertFileButton").Content);
        Assert.Equal(Strings.InsertFileCancel, (string?)Find<Button>(card, "InsertFileCancelButton").Content);
        Assert.Equal(Strings.InsertFilePositionEnd, Find<TextBlock>(card, "InsertFilePosition").Text);
        Assert.Equal(Strings.InsertFileRangeHint, Find<TextBlock>(card, "InsertFileRangeHint").Text);
        Assert.Equal("other.pdf", Find<TextBlock>(card, "InsertFileName").Text);
        Assert.NotNull(dialog);
    }

    // --- Helpers --------------------------------------------------------------------------------------------------------

    private static string Fmt(string format, params object[] arguments) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), arguments);

    private async Task<(MainWindow Window, MainViewModel ViewModel, TabViewModel Tab, string Path)> OrganizedAsync(string fixture = "pages-marked.pdf")
    {
        Strings.Culture = Strings.Culture.TwoLetterISOLanguageName == "de" ? Strings.Culture : new CultureInfo("en");
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, fixture);
        tab.OrganizeCommand.Execute(null);
        Assert.True(tab.IsOrganizing);
        Settle(window);
        return (window, viewModel, tab, path);
    }

    /// <summary>Starts "Insert file" with <paramref name="path"/> as the picked file and waits for the dialog to reach <paramref name="state"/>.</summary>
    private async Task<InsertFileViewModel> ChooseAsync(MainWindow window, MainViewModel viewModel, TabViewModel tab, string path, InsertFileState state = InsertFileState.Ready)
    {
        _host.Picker.OpenResult = path;
        tab.InsertFileCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.InsertFile && viewModel.InsertFile is { } dialog && dialog.State == state);
        Settle(window);
        return viewModel.InsertFile!;
    }

    /// <summary>Inserts the pages <paramref name="range"/> of <paramref name="path"/> and returns the id of the new source.</summary>
    private async Task<PageSourceId> InsertAsync(MainWindow window, MainViewModel viewModel, TabViewModel tab, string path, string range = "all")
    {
        var before = tab.InsertedSources.Keys.ToHashSet();
        var dialog = await ChooseAsync(window, viewModel, tab, path);
        dialog.RangeText = range;
        dialog.InsertCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.None);
        return tab.InsertedSources.Keys.Single(id => !before.Contains(id));
    }

    private static PhysicalKey PhysicalKeyOf(Key key) => key switch
    {
        Key.V => PhysicalKey.V,
        _ => PhysicalKey.None,
    };
}

internal static class RawModifiers
{
    /// <summary>The raw input modifiers of key modifiers.</summary>
    public static RawInputModifiers ToRaw(this KeyModifiers modifiers)
    {
        var raw = RawInputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            raw |= RawInputModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            raw |= RawInputModifiers.Shift;
        }

        return raw;
    }
}
