using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// Extracting the selected pages, splitting the document and exporting pages as images (milestone 13, task 13; spec 5.3): the
/// three commands of the tab, the cards that ask for target and options, what they show before anything is written (the
/// files, what the new files lose, what is in the way), progress and cancel, and the rule that nothing is replaced unless the
/// user chose it. They read the open document without the plan. Every file is a copy in a temp folder; nothing is written
/// beside the inputs.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class SplitAndExportCardTests : IDisposable
{
    private static readonly string[] CommandIds = ["pages.extract", "pages.split", "pages.exportImages"];

    private readonly SaveCardHost _host = new();
    private readonly CultureInfo _originalCulture = Strings.Culture;

    public void Dispose()
    {
        _host.Dispose();
        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task ActionsAreDisabledWithReasonWhilePlanOrChangesArePending()
    {
        var (window, viewModel, tab, _) = await OpenAsync(organize: true);
        var catalog = viewModel.Commands;
        var ids = CommandIds;

        // Nothing pending and pages selected: all three run, from the catalog as well (the palette).
        tab.SelectPages([1, 2]);
        Assert.All([tab.ExtractSelectionCommand, tab.SplitCommand, tab.ExportImagesCommand], command => Assert.True(command.CanExecute(null)));
        Assert.All(ids, id => Assert.True(catalog.ById(id)!.CanExecute(), id));
        Assert.Null(tab.ExtractUnavailableReason);

        // Extracting needs a selection and says so; the other two do not.
        tab.ClearPageSelection();
        Assert.False(tab.ExtractSelectionCommand.CanExecute(null));
        Assert.Equal(Strings.ExtractNeedsSelection, tab.ExtractUnavailableReason);
        Assert.True(tab.SplitCommand.CanExecute(null));
        Assert.True(tab.ExportImagesCommand.CanExecute(null));

        // A pending plan: apply or discard first. The note of the tools says that a plan waits (in words, beside "Apply"), and the
        // menu button names the reason as its help text; the line under the buttons is for the other reason, so it does not double.
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        Settle(window);
        Assert.All([tab.ExtractSelectionCommand, tab.SplitCommand, tab.ExportImagesCommand], command => Assert.False(command.CanExecute(null)));
        Assert.All(ids, id => Assert.False(catalog.ById(id)!.CanExecute(), id));
        Assert.Equal(Strings.PageOutputBlockedPlan, tab.ExtractUnavailableReason);
        var reason = Find<TextBlock>(window, "PageOutputReason");
        Assert.False(reason.IsEffectivelyVisible);
        Assert.True(Find<TextBlock>(window, "PendingText").IsEffectivelyVisible);
        var toolButton = Find<Button>(window, "PageOutputToolButton");
        Assert.Equal(Strings.PageOutputBlockedPlan, Avalonia.Automation.AutomationProperties.GetHelpText(toolButton));
        Assert.Contains(Strings.PageOutputBlockedPlan, (string)ToolTip.GetTip(toolButton)!, StringComparison.Ordinal);

        // A disabled command opens nothing.
        tab.SplitCommand.Execute(null);
        Settle(window);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Null(viewModel.PageOutput);

        // Undone, the reason goes; unsaved comments are the other reason ("save first").
        tab.UndoPagesCommand.Execute(null);
        Settle(window);
        Assert.False(reason.IsEffectivelyVisible);
        Assert.True(tab.SplitCommand.CanExecute(null));
        Assert.Null(Avalonia.Automation.AutomationProperties.GetHelpText(toolButton));
        tab.DiscardPageEditsCommand.Execute(null);
        await MarkAsync(window, tab);
        Settle(window);
        Assert.False(tab.SplitCommand.CanExecute(null));
        Assert.False(tab.ExportImagesCommand.CanExecute(null));
        Assert.Equal(Strings.PageOutputBlockedUnsaved, tab.ExtractUnavailableReason);
        Assert.Equal(Strings.PageOutputBlockedUnsaved, reason.Text);
        Assert.True(reason.IsEffectivelyVisible);
        Assert.All(ids, id => Assert.False(catalog.ById(id)!.CanExecute(), id));
    }

    [AvaloniaFact]
    public async Task ExtractWritesTheSelectedPagesAsANewFileAndLeavesTheOriginal()
    {
        var (window, viewModel, tab, path) = await OpenAsync(organize: true);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        tab.SelectPages([2, 4]);

        var card = await ShowSplitAsync(window, viewModel, tab.ExtractSelectionCommand, PageOutputState.Review);

        // The card says what it will do and what the new file loses, before anything is written.
        Assert.True(card.IsExtract);
        Assert.Equal(Fmt(Strings.ExtractSummaryMany, "3, 5", Fmt(Strings.PageCounterSame, 2)), card.Summary);
        Assert.NotEmpty(card.Notes);
        Assert.Empty(Directory.GetFiles(_host.Folder, "*extract*"));

        // The target is chosen with the save dialog, which suggests "<name>-extract.pdf" in the folder of the document.
        var target = Path.Combine(_host.Folder, "chosen.pdf");
        _host.Picker.Result = target;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);

        Assert.Equal("pages-marked-extract.pdf", _host.Picker.LastSuggestedName);
        Assert.False(card.IsError, card.ResultText);
        Assert.Equal(1.0, card.Progress);
        Assert.Equal(["PAGE 3", "PAGE 5"], await PageMarksAsync(target));

        // The original is byte for byte as it was, and the tab still shows it with no change pending.
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(path, tab.FullPath);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Empty(Directory.GetFiles(_host.Folder, ".pw-*"));

        // "Close" ends the card.
        card.CloseCommand.Execute(null);
        Settle(window);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Null(viewModel.PageOutput);
    }

    [AvaloniaFact]
    public async Task ExtractFromASignedDocumentSaysTheSignatureIsShownAsInvalidAndCancelledPickWritesNothing()
    {
        var (window, viewModel, tab, _) = await OpenAsync("signed-docmdp-1.pdf");
        tab.OrganizeCommand.Execute(null);
        tab.SelectPages([0]);
        var card = await ShowSplitAsync(window, viewModel, tab.ExtractSelectionCommand, PageOutputState.Review);

        // The loss list is the one of the apply card (a signature of the source is shown as invalid in a new file).
        Assert.Contains(Strings.ApplyLossSignature, card.Notes);
        Assert.DoesNotContain("valid", Strings.ApplyLossSignature.Replace("invalid", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);

        // A cancelled save dialog writes nothing and leaves the card as it was.
        _host.Picker.Result = null;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => _host.Picker.Calls == 1);
        Settle(window);
        Assert.Equal(PageOutputState.Review, card.State);
        Assert.Empty(Directory.GetFiles(_host.Folder, "*extract*"));
    }

    [AvaloniaFact]
    public async Task SplitCardOffersEveryNRangesAndBookmarks()
    {
        var (window, viewModel, tab, _) = await OpenAsync();
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "out")).FullName;

        var card = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        await WaitUntilAsync(window, () => card.BookmarksReason != Strings.SplitBookmarksReading);

        // The document has bookmarks, so all three ways are open; the folder starts at the folder of the document.
        Assert.False(card.IsExtract);
        Assert.True(card.BookmarksAvailable);
        Assert.Null(card.BookmarksReason);
        Assert.True(card.IsEvery);
        Assert.Equal(_host.Folder, card.Folder);

        // Every N pages: a wrong number stays on the card with its reason; a right one lists the files before they are written.
        card.Folder = output;
        card.EveryText = "0";
        card.ReviewCommand.Execute(null);
        Assert.Equal(Strings.SplitEveryInvalid, card.InputError);
        Assert.Equal(PageOutputState.Setup, card.State);
        card.EveryText = "4";
        card.ReviewCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Review);
        Assert.Null(card.InputError);
        Assert.Equal(["pages-marked-p001-004.pdf", "pages-marked-p005-006.pdf"], card.FileNames);
        Assert.NotEmpty(card.Notes);
        Assert.Empty(Directory.GetFiles(output));

        // Back changes the way; ranges one file each, in the syntax of printing, and a page the document lacks is told.
        card.BackCommand.Execute(null);
        Assert.Equal(PageOutputState.Setup, card.State);
        card.IsRanges = true;
        card.RangesText = "1-2, 9";
        card.ReviewCommand.Execute(null);
        Assert.Equal(Fmt(Strings.InsertFileRangeOutside, Fmt(Strings.PageCounterSame, 6), 9), card.InputError);
        card.RangesText = "1-2, 5";
        card.ReviewCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Review);
        Assert.Equal(["pages-marked-p001-002.pdf", "pages-marked-p005.pdf"], card.FileNames);

        // Bookmarks of the top level: one file each, named by the bookmark.
        card.BackCommand.Execute(null);
        card.IsBookmarks = true;
        card.ReviewCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Review);
        Assert.Equal(["Kapitel 1.pdf", "Kapitel 2.pdf", "Kapitel 3.pdf"], card.FileNames);
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.False(card.IsError, card.ResultText);
        Assert.Equal(["Kapitel 1.pdf", "Kapitel 2.pdf", "Kapitel 3.pdf"], Directory.GetFiles(output).Select(Path.GetFileName).Order());
        Assert.Equal(["PAGE 1", "PAGE 2"], await PageMarksAsync(Path.Combine(output, "Kapitel 1.pdf")));
        Assert.Equal(["PAGE 3", "PAGE 4"], await PageMarksAsync(Path.Combine(output, "Kapitel 2.pdf")));
        Assert.Equal(["PAGE 5", "PAGE 6"], await PageMarksAsync(Path.Combine(output, "Kapitel 3.pdf")));
        Assert.Equal(Fmt(Strings.SplitDoneMany, 3, output), card.ResultText);
        Assert.Empty(Directory.GetFiles(output, ".pw-*"));
    }

    [AvaloniaFact]
    public async Task BookmarksCanOnlyBeChosenWhenTheDocumentHasThem()
    {
        var (window, viewModel, tab, _) = await OpenAsync("text-basic.pdf");

        var card = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        await WaitUntilAsync(window, () => card.BookmarksReason != Strings.SplitBookmarksReading);

        Assert.False(card.BookmarksAvailable);
        Assert.Equal(Strings.SplitNoBookmarks, card.BookmarksReason);
        card.IsBookmarks = true;
        Assert.False(card.IsBookmarks);
        Assert.True(card.IsEvery);

        // The card shows it: the choice is off and the reason stands beside it.
        var choice = Find<RadioButton>(window, "SplitBookmarksChoice");
        Assert.False(choice.IsEnabled);
        var reason = Find<TextBlock>(window, "SplitBookmarksReason");
        Assert.True(reason.IsEffectivelyVisible);
        Assert.Equal(Strings.SplitNoBookmarks, reason.Text);
    }

    [AvaloniaFact]
    public async Task ExportCardUsesDpiAndSelection()
    {
        var (window, viewModel, tab, _) = await OpenAsync(organize: true);
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "images")).FullName;
        tab.SelectPages([1, 2]);

        var card = await ShowExportAsync(window, viewModel, tab);

        // PNG at 144 dpi is the default; the selection is the default scope when there is one.
        Assert.Equal("144", card.DpiText);
        Assert.Null(card.DpiError);
        Assert.True(card.HasSelection);
        Assert.True(card.ExportSelection);
        Assert.Equal(_host.Folder, card.Folder);
        card.Folder = output;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.False(card.IsError, card.ResultText);
        Assert.Equal(["pages-marked-p002.png", "pages-marked-p003.png"], Directory.GetFiles(output).Select(Path.GetFileName).Order());

        // Page 2 is 300 x 400 pt: twice that at 144 dpi, and a real PNG.
        Assert.Equal((600, 800), PngSize(Path.Combine(output, "pages-marked-p002.png")));
        Assert.Equal((800, 600), PngSize(Path.Combine(output, "pages-marked-p003.png")));
        Assert.Equal(Fmt(Strings.PageOutputExportDoneMany, 2, output), card.ResultText);

        // All pages at another resolution; a resolution out of range is refused on the card, before anything is written.
        card.CloseCommand.Execute(null);
        Settle(window);
        var other = Directory.CreateDirectory(Path.Combine(_host.Folder, "images72")).FullName;
        card = await ShowExportAsync(window, viewModel, tab);
        card.Folder = other;
        card.ExportSelection = false;
        Assert.True(card.ExportAll);
        foreach (var text in new[] { "35", "1201", "abc", "", "-72", "72.5" })
        {
            card.DpiText = text;
            Assert.Equal(Fmt(Strings.ExportDpiInvalid, 36, 1200), card.DpiError);
            Assert.False(card.StartCommand.CanExecute(null), text);
        }

        card.DpiText = "72";
        Assert.Null(card.DpiError);
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.Equal(6, Directory.GetFiles(other).Length);
        Assert.Equal((300, 400), PngSize(Path.Combine(other, "pages-marked-p001.png")));
    }

    [AvaloniaFact]
    public async Task ExportWithoutASelectionExportsAllPagesAndTellsPageTooLargeByNumber()
    {
        var (window, viewModel, tab, _) = await OpenAsync("wide-page.pdf");
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "images")).FullName;

        var card = await ShowExportAsync(window, viewModel, tab);
        Assert.False(card.HasSelection);
        Assert.False(card.ExportSelection);
        Assert.True(card.ExportAll);
        card.Folder = output;

        // 600 x 120 pt at 1200 dpi is 10000 x 2000 px: 20 megapixels, fine.
        card.DpiText = "1200";
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.False(card.IsError, card.ResultText);
        var (width, height) = PngSize(Path.Combine(output, "wide-page-p001.png"));
        Assert.Equal(10000, width);
        Assert.InRange(height, 2000, 2001);
    }

    [AvaloniaFact]
    public async Task ExportTellsTheNumberOfAPageThatIsTooLarge()
    {
        // A page above 64 megapixels at the chosen resolution is refused by its number, before any page is written; no test
        // file has one, so the document is written by hand.
        var path = Path.Combine(_host.Folder, "huge.pdf");
        await File.WriteAllBytesAsync(path, HugePageDocument(), TestContext.Current.CancellationToken);
        var (window, viewModel, view) = _host.Show();
        var tab = await OpenAsync(window, viewModel, view, path);
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "images")).FullName;

        var card = await ShowExportAsync(window, viewModel, tab);
        card.Folder = output;
        card.DpiText = "144";
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);

        Assert.True(card.IsError);
        Assert.Equal(Fmt(Strings.PageOutputExportTooLarge, 1, 144), card.ResultText);
        Assert.Empty(Directory.GetFiles(output));
    }

    [AvaloniaFact]
    public async Task ProgressAndCancelWork()
    {
        using var gate = new JobGate();
        var (window, viewModel, tab, card, output) = await StartBlockedExportAsync(gate);

        // Running with the first file written (the worker waits in the progress callback): the bar moved, files exist.
        Assert.Equal(PageOutputState.Running, card.State);
        Assert.False(card.StartCommand.CanExecute(null));
        Assert.InRange(card.Progress, 0.0001, 0.999);
        var first = Assert.Single(Directory.GetFiles(output));
        Assert.EndsWith(".png", first, StringComparison.Ordinal);

        // Cancel: the run stops before the next file, the card says what was written, nothing half-done is left.
        card.CancelCommand.Execute(null);
        gate.Release();
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.False(card.IsError);
        Assert.Equal(Strings.PageOutputCanceled, card.ResultText);
        Assert.Equal([first], Directory.GetFiles(output));
        Assert.Equal(Fmt(Strings.PageOutputWritten, 1, Path.GetFileName(first)), card.WrittenText);
        Assert.True(card.Progress < 1);
        Assert.Empty(Directory.GetFiles(output, ".pw-*"));

        // The card can be closed now.
        card.CloseCommand.Execute(null);
        Settle(window);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.NotNull(tab);
    }

    [AvaloniaFact]
    public async Task ARunningCardIsNotReplacedByAnotherCommandOrOverlay()
    {
        using var gate = new JobGate();
        var (window, viewModel, tab, card, output) = await StartBlockedExportAsync(gate);

        // Another command of the pages panel, the palette and the settings are ignored: the job that writes stays the card of the window.
        tab.ExportImagesCommand.Execute(null);
        tab.SplitCommand.Execute(null);
        viewModel.ShowPaletteCommand.Execute(null);
        viewModel.ShowSettingsCommand.Execute(null);
        viewModel.HideOverlay();
        Settle(window);
        Assert.Equal(OverlayKind.PageOutput, viewModel.Overlay);
        Assert.Same(card, viewModel.PageOutput);
        Assert.Equal(PageOutputState.Running, card.State);
        Assert.False(card.IsDisposed);
        Assert.Same(card, Find<ExportImagesCard>(window, "ExportImagesCard").DataContext);

        // The job is intact: it ends the way the user ends it, with its file.
        card.CancelCommand.Execute(null);
        gate.Release();
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.Single(Directory.GetFiles(output));
    }

    [AvaloniaFact]
    public async Task AShownCardIsReplacedByTheNextOneAndItsViewFollows()
    {
        var (window, viewModel, tab, _) = await OpenAsync();
        var split = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        var splitView = Find<SplitCard>(window, "SplitCard");
        Assert.Same(split, splitView.DataContext);

        // Another command while a card waits for its options: the old view model is disposed, the window has the new one, and
        // the view shows it (not the old card, whose Start would run a job the window does not know).
        var export = await ShowExportAsync(window, viewModel, tab);
        Assert.Same(export, viewModel.PageOutput);
        Assert.True(split.IsDisposed);
        Assert.False(export.IsDisposed);
        var exportView = Find<ExportImagesCard>(window, "ExportImagesCard");
        Assert.Same(export, exportView.DataContext);
        Assert.True(exportView.IsVisible);
        Assert.False(splitView.IsVisible);

        // And back.
        var again = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        Assert.NotSame(split, again);
        Assert.True(export.IsDisposed);
        Assert.Same(again, splitView.DataContext);
        Assert.True(splitView.IsVisible);
        Assert.False(exportView.IsVisible);
    }

    [AvaloniaFact]
    public async Task ClosingTheTabCancelsARunningJobOfTheReplacingCard()
    {
        // A card is replaced by another one, whose job is then started: closing the tab cancels exactly that job.
        using var gate = new JobGate();
        var (window, viewModel, tab, _) = await OpenAsync("thousand-pages.pdf");
        viewModel.PageOutputProgressWrapper = gate.Wrap;
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "images")).FullName;
        var old = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        var card = await ShowExportAsync(window, viewModel, tab);
        Assert.True(old.IsDisposed);
        card.Folder = output;
        card.DpiText = "72";
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => gate.Started.IsCompleted && card.Progress > 0);
        var written = Directory.GetFiles(output).Single();

        // The tab goes while the worker waits in the callback: the card goes with it, the token is canceled, and after the
        // release the worker writes no further file and leaves no temporary one.
        viewModel.CloseTabCommand.Execute(tab);
        await WaitUntilAsync(window, () => viewModel.PageOutput is null);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.True(card.IsDisposed);
        gate.Release();
        await card.LastRun!.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal([written], Directory.GetFiles(output));
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowCancelsARunningJob()
    {
        using var gate = new JobGate();
        var (_, viewModel, _, card, output) = await StartBlockedExportAsync(gate);
        var written = Directory.GetFiles(output).Single();

        viewModel.Dispose();
        Assert.True(card.IsDisposed);
        gate.Release();
        await card.LastRun!.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal([written], Directory.GetFiles(output));
    }

    [AvaloniaFact]
    public async Task TheFirstRedactionMarkCancelsARunningJob()
    {
        // The files would be written from the document without the marks: a mark made while the job runs ends it.
        using var gate = new JobGate();
        var (window, viewModel, tab, card, output) = await StartBlockedExportAsync(gate);
        var written = Directory.GetFiles(output).Single();
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        Assert.Equal(PageOutputState.Running, card.State);

        Assert.True(tab.AddAreaMark(0, new PageRect(72, 100, 200, 130)));
        await WaitUntilAsync(window, () => viewModel.PageOutput is null);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.True(card.IsDisposed);
        gate.Release();
        await card.LastRun!.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal([written], Directory.GetFiles(output));
    }

    [AvaloniaFact]
    public async Task ExtractReplacesAFileTheSaveDialogConfirmedButNeverTheOpenDocument()
    {
        var (window, viewModel, tab, path) = await OpenAsync(organize: true);
        var confirmed = Path.Combine(_host.Folder, "confirmed.pdf");
        await File.WriteAllTextAsync(confirmed, "old", TestContext.Current.CancellationToken);
        tab.SelectPages([0]);
        var card = await ShowSplitAsync(window, viewModel, tab.ExtractSelectionCommand, PageOutputState.Review);

        // The save dialog names a file that exists (it asked the user): that is the explicit choice, so it is replaced.
        _host.Picker.Result = confirmed;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.False(card.IsError, card.ResultText);
        Assert.Equal(["PAGE 1"], await PageMarksAsync(confirmed));
        Assert.Equal(Fmt(Strings.SplitDoneOne, confirmed), card.ResultText);
        Assert.True(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task ANameThatTheNamingRulesChangeIsNotTreatedAsConfirmed()
    {
        var (window, viewModel, tab, _) = await OpenAsync(organize: true);
        tab.SelectPages([0]);

        // A name of 100 characters becomes a shorter file name. The file the dialog confirmed (the long name) is not the file
        // that would be written, so nothing is replaced on its account: the new file is written beside it.
        var longPath = Path.Combine(_host.Folder, new string('x', 100) + ".pdf");
        await File.WriteAllTextAsync(longPath, "precious", TestContext.Current.CancellationToken);
        var written = DocumentSplitter.TargetPaths([new SplitPart(new string('x', 100), [0])], _host.Folder)[0];
        Assert.NotEqual(longPath, written);

        var card = await ShowSplitAsync(window, viewModel, tab.ExtractSelectionCommand, PageOutputState.Review);
        _host.Picker.Result = longPath;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.False(card.IsError, card.ResultText);
        Assert.Equal("precious", await File.ReadAllTextAsync(longPath, TestContext.Current.CancellationToken));
        Assert.Equal(Fmt(Strings.SplitDoneOne, written), card.ResultText);
        card.CloseCommand.Execute(null);
        Settle(window);

        // The changed name now exists, and a second try with the same long name is refused with the words of an extract (there is
        // no "Replace existing files" here), without touching either file.
        var before = await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken);
        card = await ShowSplitAsync(window, viewModel, tab.ExtractSelectionCommand, PageOutputState.Review);
        _host.Picker.Result = longPath;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.True(card.IsError);
        Assert.Equal(Strings.PageOutputErrorExtractTargetExists, card.ResultText);
        Assert.Equal(before, await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken));
        Assert.Equal("precious", await File.ReadAllTextAsync(longPath, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task TheWordsForAFailureFollowItsCause()
    {
        var (window, viewModel, tab, _) = await OpenAsync(organize: true);
        tab.SelectPages([0]);
        var split = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        var path = Path.Combine(_host.Folder, "a.pdf");

        DocumentSplitException Part(SaveFailureReason reason) =>
            new(DocumentSplitError.PartFailed, "x", [path], 1, new SaveFailureException(reason, "y"));

        // A part that failed is named with its cause; a file that changed outside says so alone.
        Assert.Equal(Fmt(Strings.PageOutputErrorPart, 2, Strings.SaveErrorEncrypted), split.DescribeError(Part(SaveFailureReason.EncryptedNotSupported)));
        Assert.Equal(Fmt(Strings.PageOutputErrorPart, 2, Strings.SaveErrorVerificationFailed), split.DescribeError(Part(SaveFailureReason.VerificationFailed)));
        Assert.Equal(Fmt(Strings.PageOutputErrorPart, 2, Strings.SaveErrorDiskFull), split.DescribeError(Part(SaveFailureReason.DiskFull)));
        Assert.Equal(Strings.PageOutputErrorSourceChanged, split.DescribeError(Part(SaveFailureReason.SourceModified)));
        Assert.Equal(
            Fmt(Strings.PageOutputErrorTargetExists, "a.pdf"),
            split.DescribeError(new DocumentSplitException(DocumentSplitError.TargetExists, "x", [path], 1, null)));
        Assert.Equal(Strings.PageOutputErrorTargetIsSource, split.DescribeError(new DocumentSplitException(DocumentSplitError.TargetIsSource, "x", [path])));
        Assert.Equal(Strings.PageOutputErrorGone, split.DescribeError(new ObjectDisposedException("x")));
        Assert.DoesNotContain(path, split.DescribeError(Part(SaveFailureReason.WriteFailed)), StringComparison.Ordinal);

        // An extract has no "Replace existing files": its words for a file in the way do not mention it.
        split.CancelCommand.Execute(null);
        Settle(window);
        var extract = await ShowSplitAsync(window, viewModel, tab.ExtractSelectionCommand, PageOutputState.Review);
        Assert.Equal(Strings.PageOutputErrorExtractTargetExists, extract.DescribeError(new DocumentSplitException(DocumentSplitError.TargetExists, "x", [path], 0, null)));
        extract.CancelCommand.Execute(null);
        Settle(window);

        // An export names the page and the cause.
        var export = await ShowExportAsync(window, viewModel, tab);
        Assert.Equal(
            Fmt(Strings.PageOutputExportWriteFailed, 3, Strings.SaveErrorDiskFull),
            export.DescribeError(new PageExportException(PageExportError.WriteFailed, "x", 2, [path], new SaveFailureException(SaveFailureReason.DiskFull, "y"))));
        Assert.Equal(
            Fmt(Strings.PageOutputErrorTargetExists, "a.pdf"),
            export.DescribeError(new PageExportException(PageExportError.TargetExists, "x", -1, [path], null)));
    }

    [AvaloniaFact]
    public async Task TheMenuNamesTheReasonWhyEntriesAreOff()
    {
        var (window, viewModel, tab, _) = await OpenAsync();
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        var button = Find<Button>(window, "PageOutputButton");

        // Reading view, nothing selected: the entry for the selection is off, and the menu says why in words.
        var line = ReasonLineOf(button);
        Assert.True(line.IsVisible);
        Assert.Equal(Strings.ExtractNeedsSelection, line.Text);
        Assert.Equal(Strings.ExtractNeedsSelection, Avalonia.Automation.AutomationProperties.GetName(line));

        // In the mode with a selection nothing is off: no reason, no line. The menu of the tools is the same menu.
        tab.OrganizeCommand.Execute(null);
        Settle(window);
        tab.SelectPages([0]);
        Settle(window);
        var toolButton = Find<Button>(window, "PageOutputToolButton");
        Assert.False(ReasonLineOf(toolButton).IsVisible);
        Assert.False(ReasonLineOf(toolButton).IsVisible);

        // A page plan that waits: apply or discard first.
        tab.RotateSelectionCommand.Execute(90);
        Settle(window);
        line = ReasonLineOf(toolButton);
        Assert.True(line.IsVisible);
        Assert.Equal(Strings.PageOutputBlockedPlan, line.Text);
        tab.UndoPagesCommand.Execute(null);
        tab.SelectPages([0]);
        Settle(window);
        Assert.False(ReasonLineOf(toolButton).IsVisible);

        // Unsaved changes: save first.
        tab.DiscardPageEditsCommand.Execute(null);
        await MarkAsync(window, tab);
        Settle(window);
        line = ReasonLineOf(Find<Button>(window, "PageOutputButton"));
        Assert.True(line.IsVisible);
        Assert.Equal(Strings.PageOutputBlockedUnsaved, line.Text);
    }

    [AvaloniaFact]
    public async Task NothingIsOverwrittenWithoutAnExplicitChoice()
    {
        var (window, viewModel, tab, path) = await OpenAsync();
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "out")).FullName;
        var inTheWay = Path.Combine(output, "pages-marked-p001-004.pdf");
        await File.WriteAllTextAsync(inTheWay, "precious", TestContext.Current.CancellationToken);

        // Split: the card names the file in the way before it starts, and "Create files" does not run.
        var card = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        card.Folder = output;
        card.EveryText = "4";
        card.ReviewCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Review);
        Assert.False(card.ReplaceExisting);
        Assert.Equal(["pages-marked-p001-004.pdf"], card.Conflicts);
        Assert.False(card.StartCommand.CanExecute(null));
        card.StartCommand.Execute(null);
        Settle(window);
        Assert.Equal(PageOutputState.Review, card.State);
        Assert.Equal("precious", await File.ReadAllTextAsync(inTheWay, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(output));

        // Only the explicit choice replaces it; the other file is new.
        card.ReplaceExisting = true;
        Assert.Empty(card.Conflicts);
        Assert.True(card.StartCommand.CanExecute(null));
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.False(card.IsError, card.ResultText);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await File.ReadAllBytesAsync(inTheWay, TestContext.Current.CancellationToken))[..4]));
        Assert.Equal(2, Directory.GetFiles(output).Length);
        card.CloseCommand.Execute(null);
        Settle(window);

        // Export: a file in the way is named, nothing is written, and it stays as it was until "Replace existing files" is on.
        var images = Directory.CreateDirectory(Path.Combine(_host.Folder, "images")).FullName;
        var png = Path.Combine(images, "pages-marked-p002.png");
        await File.WriteAllTextAsync(png, "precious", TestContext.Current.CancellationToken);
        var export = await ShowExportAsync(window, viewModel, tab);
        export.Folder = images;
        export.DpiText = "72";
        export.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => export.State == PageOutputState.Finished);
        Assert.True(export.IsError);
        Assert.Contains("pages-marked-p002.png", export.ResultText, StringComparison.Ordinal);
        Assert.Equal("precious", await File.ReadAllTextAsync(png, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(images));
        export.CloseCommand.Execute(null);
        Settle(window);

        export = await ShowExportAsync(window, viewModel, tab);
        export.Folder = images;
        export.DpiText = "72";
        export.ReplaceExisting = true;
        export.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => export.State == PageOutputState.Finished);
        Assert.False(export.IsError, export.ResultText);
        Assert.Equal(6, Directory.GetFiles(images).Length);
        Assert.Equal((300, 400), PngSize(png));
        export.CloseCommand.Execute(null);
        Settle(window);

        // Extract: the open document itself is never a target, not even when the save dialog returns it.
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        tab.OrganizeCommand.Execute(null);
        tab.SelectPages([0, 1]);
        var extract = await ShowSplitAsync(window, viewModel, tab.ExtractSelectionCommand, PageOutputState.Review);
        _host.Picker.Result = path;
        extract.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => extract.State == PageOutputState.Finished);
        Assert.True(extract.IsError);
        Assert.Equal(Strings.PageOutputErrorTargetIsSource, extract.ResultText);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task AFileThatChangedOutsideTheProgramIsToldAndKeepsWhatWasWritten()
    {
        var (window, viewModel, tab, path) = await OpenAsync();
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "out")).FullName;
        var card = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        card.Folder = output;
        card.EveryText = "2";
        card.ReviewCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Review);

        // Another program touches the file between the review and the start: the rewrite refuses (the file is not the one
        // that was opened), and the card says that, not "failed", and that no file was written.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);

        Assert.True(card.IsError);
        Assert.Contains(Strings.PageOutputErrorSourceChanged, card.ResultText, StringComparison.Ordinal);
        Assert.Equal(Strings.PageOutputNothingWritten, card.WrittenText);
        Assert.Empty(Directory.GetFiles(output));
        Assert.Empty(Directory.GetFiles(_host.Folder, ".pw-*"));
    }

    [AvaloniaFact]
    public async Task TheCommandsAreInThePanelMenuTheContextMenuAndTheCatalog()
    {
        var (window, viewModel, tab, _) = await OpenAsync();
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);

        // Reading view: the panel has the menu button, the catalog the entries; no card and no view model yet.
        var catalog = viewModel.Commands;
        Assert.All(CommandIds, id =>
        {
            Assert.NotNull(catalog.ById(id));
            Assert.Null(catalog.ById(id)!.Gesture);
        });
        Assert.False(catalog.ById("pages.extract")!.CanExecute());
        Assert.True(catalog.ById("pages.split")!.CanExecute());
        Assert.Null(viewModel.PageOutput);
        Assert.Empty(window.GetVisualDescendants().OfType<SplitCard>());
        Assert.Empty(window.GetVisualDescendants().OfType<ExportImagesCard>());
        var button = Find<Button>(window, "PageOutputButton");
        Assert.Equal(Strings.PageOutputMenu, Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.True(button.Focusable && button.IsTabStop);

        // The menu offers the three, and a click on one opens its card.
        var items = PageOutputMenuItems(button);
        Assert.Equal([Strings.CommandExtractPages, Strings.CommandSplitDocument, Strings.CommandExportImages], items.Select(item => (string)item.Header!));
        Assert.Same(tab.ExtractSelectionCommand, items[0].Command);
        Assert.False(items[0].IsEffectivelyEnabled);
        Assert.True(items[1].IsEffectivelyEnabled);

        // Organize: the context menu of the list has them too.
        tab.OrganizeCommand.Execute(null);
        Settle(window);
        tab.SelectPages([0]);
        var list = window.GetVisualDescendants().OfType<Controls.ThumbnailList>().Single();
        var entries = list.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Command).ToList();
        Assert.Contains(tab.ExtractSelectionCommand, entries);
        Assert.Contains(tab.SplitCommand, entries);
        Assert.Contains(tab.ExportImagesCommand, entries);

        // From the catalog (the palette) the command opens the card.
        catalog.ById("pages.split")!.Execute();
        Settle(window);
        Assert.Equal(OverlayKind.PageOutput, viewModel.Overlay);
        Assert.IsType<SplitViewModel>(viewModel.PageOutput);
        Assert.Single(window.GetVisualDescendants().OfType<SplitCard>(), card => card.IsVisible);
    }

    [AvaloniaFact]
    public async Task KeyboardOnlyUseWorks()
    {
        var (window, viewModel, tab, _) = await OpenAsync();
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "out")).FullName;
        var card = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        var view = Find<SplitCard>(window, "SplitCard");
        Assert.Equal(Strings.SplitTitle, Avalonia.Automation.AutomationProperties.GetName(view));
        card.Folder = output;

        // The focus starts on the field of the way that is chosen; Enter goes on; every control of the card is a stop of Tab.
        await WaitUntilAsync(window, () => Find<TextBox>(view, "SplitEveryField").IsFocused);
        var tabStops = view.GetVisualDescendants().OfType<InputElement>()
            .Where(element => element.IsEffectivelyVisible && element.IsEffectivelyEnabled && element is Button or RadioButton or TextBox or CheckBox)
            .ToList();
        Assert.NotEmpty(tabStops);
        Assert.All(tabStops, element => Assert.True(element.Focusable && element.IsTabStop, element.Name));
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Review);

        // In the review the primary button has the focus; Enter on it creates the files; Esc would leave without writing.
        await WaitUntilAsync(window, () => Find<Button>(view, "SplitStartButton").IsFocused);
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => card.State == PageOutputState.Finished);
        Assert.False(card.IsError, card.ResultText);
        Assert.NotEmpty(Directory.GetFiles(output));
        await WaitUntilAsync(window, () => Find<Button>(view, "SplitDoneButton").IsFocused);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);

        // Esc in the setup closes the card and writes nothing; Esc while it runs cancels the run and leaves the card.
        var second = await ShowExportAsync(window, viewModel, tab);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.NotNull(second);
        Assert.Empty(Directory.GetFiles(_host.Folder, "*.png"));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task TextsExistInBothLanguages(string language)
    {
        Strings.Culture = new CultureInfo(language);
        string[] texts =
        [
            Strings.CommandExtractPages, Strings.CommandSplitDocument, Strings.CommandExportImages, Strings.PageOutputMenu, Strings.ExtractNeedsSelection,
            Strings.PageOutputClose, Strings.PageOutputCancel, Strings.PageOutputDone, Strings.PageOutputFolderLabel, Strings.PageOutputChooseFolder,
            Strings.PageOutputReplace, Strings.PageOutputRunning, Strings.PageOutputCanceled, Strings.PageOutputWritten, Strings.PageOutputNothingWritten,
            Strings.PageOutputChecking, Strings.PageOutputNotesHeading, Strings.PageOutputConflicts, Strings.PageOutputConflictsHint,
            Strings.PageOutputErrorRefused, Strings.PageOutputErrorFolderMissing, Strings.PageOutputErrorTargetExists, Strings.PageOutputErrorTargetIsSource,
            Strings.PageOutputErrorUnsafeName, Strings.PageOutputErrorSourceChanged, Strings.PageOutputErrorPart, Strings.PageOutputErrorGone,
            Strings.PageOutputErrorUnknown, Strings.PageOutputErrorNotPossible, Strings.PageOutputPickFailed,
            Strings.SplitTitle, Strings.ExtractTitle, Strings.SplitEveryChoice, Strings.SplitRangesChoice, Strings.SplitBookmarksChoice,
            Strings.SplitEveryLabel, Strings.SplitEveryInvalid, Strings.SplitRangesLabel, Strings.SplitRangesHint, Strings.SplitRangesInvalid,
            Strings.SplitNoBookmarks, Strings.SplitBookmarksReading, Strings.SplitContinue, Strings.SplitBack, Strings.SplitStart,
            Strings.SplitFilesHeading, Strings.SplitMoreFiles, Strings.SplitDoneOne, Strings.SplitDoneMany,
            Strings.ExtractSummaryOne, Strings.ExtractSummaryMany, Strings.ExtractSaveAs,
            Strings.ExportTitle, Strings.ExportDpiLabel, Strings.ExportDpiHint, Strings.ExportDpiInvalid, Strings.ExportScopeLabel,
            Strings.ExportSelected, Strings.ExportAll, Strings.ExportFormat, Strings.ExportStart,
            Strings.PageOutputExportDoneOne, Strings.PageOutputExportDoneMany, Strings.PageOutputExportTooLarge, Strings.PageOutputExportWriteFailed,
            Strings.PageOutputExportRenderFailed, Strings.PageOutputMoreNotes, Strings.PageOutputErrorExtractTargetExists,
        ];
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.All(texts, text => Assert.DoesNotContain("rechtsgültig", text, StringComparison.OrdinalIgnoreCase));
        Assert.All(texts, text => Assert.DoesNotContain("legally binding", text, StringComparison.OrdinalIgnoreCase));

        // The cards show them: a window in this language has the titles and the buttons.
        var (window, viewModel, tab, _) = await OpenAsync(organize: true);
        tab.SelectPages([0]);
        var split = await ShowSplitAsync(window, viewModel, tab.SplitCommand, PageOutputState.Setup);
        var splitCard = Find<SplitCard>(window, "SplitCard");
        Assert.Equal(Strings.SplitTitle, Find<TextBlock>(splitCard, "SplitTitleText").Text);
        Assert.Equal(Strings.SplitContinue, (string?)Find<Button>(splitCard, "SplitContinueButton").Content);
        Assert.Equal(Strings.PageOutputCancel, (string?)Find<Button>(splitCard, "SplitCancelButton").Content);
        Assert.Equal(Strings.PageOutputReplace, (string?)Find<CheckBox>(splitCard, "SplitReplaceChoice").Content);
        split.CancelCommand.Execute(null);
        Settle(window);

        var export = await ShowExportAsync(window, viewModel, tab);
        var exportCard = Find<ExportImagesCard>(window, "ExportImagesCard");
        Assert.Equal(Strings.ExportTitle, Find<TextBlock>(exportCard, "ExportTitleText").Text);
        Assert.Equal(Strings.ExportStart, (string?)Find<Button>(exportCard, "ExportStartButton").Content);
        Assert.Equal(Strings.ExportFormat, Find<TextBlock>(exportCard, "ExportFormatText").Text);
        Assert.Equal(Strings.ExportDpiHint, Find<TextBlock>(exportCard, "ExportDpiHint").Text);
        Assert.NotNull(export);
    }

    // --- Helpers --------------------------------------------------------------------------------------------------------

    private static TextBlock ReasonLineOf(Button button)
    {
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        flyout.ShowAt(button);
        var line = Assert.Single(flyout.Items.OfType<TextBlock>());
        flyout.Hide();
        return line;
    }

    /// <summary>Opens the export card on the 1000 pages, starts it, and returns when the first file is written and the worker waits.</summary>
    private async Task<(MainWindow Window, MainViewModel ViewModel, TabViewModel Tab, ExportImagesViewModel Card, string Output)> StartBlockedExportAsync(JobGate gate)
    {
        var (window, viewModel, tab, _) = await OpenAsync("thousand-pages.pdf");
        viewModel.PageOutputProgressWrapper = gate.Wrap;
        var output = Directory.CreateDirectory(Path.Combine(_host.Folder, "images")).FullName;
        var card = await ShowExportAsync(window, viewModel, tab);
        card.Folder = output;
        card.DpiText = "72";
        card.StartCommand.Execute(null);
        Assert.Equal(PageOutputState.Running, card.State);
        await WaitUntilAsync(window, () => gate.Started.IsCompleted && card.Progress > 0);
        return (window, viewModel, tab, card, output);
    }

    /// <summary>Blocks the worker of a job in its progress callback after the first file, so that the job is provably running when the test acts.</summary>
    private sealed class JobGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public IProgress<double> Wrap(IProgress<double> inner) => new Blocking(inner, this);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }

        private sealed class Blocking(IProgress<double> inner, JobGate owner) : IProgress<double>
        {
            public void Report(double value)
            {
                inner.Report(value);
                owner._started.TrySetResult();
                owner._release.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    private static string Fmt(string format, params object[] arguments) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), arguments);

    private async Task<(MainWindow Window, MainViewModel ViewModel, TabViewModel Tab, string Path)> OpenAsync(string fixture = "pages-marked.pdf", bool organize = false)
    {
        Strings.Culture = Strings.Culture.TwoLetterISOLanguageName == "de" ? Strings.Culture : new CultureInfo("en");
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, fixture);
        if (organize)
        {
            tab.OrganizeCommand.Execute(null);
            Assert.True(tab.IsOrganizing);
            Settle(window);
        }

        return (window, viewModel, tab, path);
    }

    private static Task<TabViewModel> OpenAsync(MainWindow window, MainViewModel viewModel, DocumentView view, string path) =>
        SaveCardHost.OpenAsync(window, viewModel, view, path);

    private static async Task<SplitViewModel> ShowSplitAsync(MainWindow window, MainViewModel viewModel, ICommand command, PageOutputState state)
    {
        command.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.PageOutput && viewModel.PageOutput is SplitViewModel card && card.State == state);
        Settle(window);
        return (SplitViewModel)viewModel.PageOutput!;
    }

    private static async Task<ExportImagesViewModel> ShowExportAsync(MainWindow window, MainViewModel viewModel, TabViewModel tab)
    {
        tab.ExportImagesCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.PageOutput && viewModel.PageOutput is ExportImagesViewModel);
        Settle(window);
        return (ExportImagesViewModel)viewModel.PageOutput!;
    }

    private static List<MenuItem> PageOutputMenuItems(Button button)
    {
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        flyout.ShowAt(button);
        var items = flyout.Items.OfType<MenuItem>().ToList();
        flyout.Hide();
        return items;
    }

    /// <summary>The "PAGE n" marks of the pages of a file written by a test, in page order.</summary>
    private static async Task<IReadOnlyList<string>> PageMarksAsync(string path)
    {
        await using var document = await new PdfEngine().OpenAsync(path, null, TestContext.Current.CancellationToken);
        var marks = new List<string>();
        for (var i = 0; i < document.PageCount; i++)
        {
            var text = await document.GetPageTextAsync(i, TestContext.Current.CancellationToken);
            marks.Add(text.GetText(0, text.Chars.Count).Trim());
        }

        return marks;
    }

    private static (int Width, int Height) PngSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
    }

    // A document with a page of 6000 x 6000 pt (144 dpi: 12000 x 12000 px, 144 megapixels) and a small page after it.
    private static byte[] HugePageDocument()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 6000 6000] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>",
        };
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
