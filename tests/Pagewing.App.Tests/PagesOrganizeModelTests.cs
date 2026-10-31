using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// The mode "Organize pages" of the tab (milestone 13, task 6): the plan the tab holds, the locks it puts on the other modes,
/// closing with an open plan, and what the plan does not touch (the document). Without a window where the tab suffices; with
/// one for closing and for moving the tab. Every file is a copy in a temp folder; settings and history are in memory.
/// </summary>
// Strings has a process-wide culture (see LocalizationTests).
[Collection("Localization")]
public sealed class PagesOrganizeModelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly SaveCardHost _host = new();
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagewing-organize-" + Guid.NewGuid().ToString("N"))).FullName;

    public PagesOrganizeModelTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        _host.Dispose();
        Strings.Culture = _originalCulture;
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    [AvaloniaFact]
    public async Task OrganizingNeedsASharpPageFirst()
    {
        var tab = await OpenAsync("pages-marked.pdf", sharp: false);

        Assert.False(tab.OrganizeCommand.CanExecute(null));
        Assert.False(tab.CanOrganize);
        Assert.Equal(Strings.PageOrganizeNotReady, tab.OrganizeUnavailableReason);
        tab.OrganizeCommand.Execute(null);
        Assert.False(tab.IsOrganizing);
        Assert.Null(tab.PageHistory);

        tab.HasShownSharpPage = true;
        Assert.True(tab.OrganizeCommand.CanExecute(null));
        Assert.True(tab.CanOrganize);
        Assert.Null(tab.OrganizeUnavailableReason);
        tab.OrganizeCommand.Execute(null);
        Assert.True(tab.IsOrganizing);
        Assert.NotNull(tab.PageHistory);
        Assert.True(tab.PageHistory.Current.IsIdentity);
        Assert.Equal(6, tab.PageHistory.Current.Slots.Count);
        Assert.False(tab.HasPendingPageEdits);
    }

    [AvaloniaFact]
    public async Task EditsAreStagedAndTheDocumentStaysUnchanged()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        var document = tab.Document!;
        tab.OrganizeCommand.Execute(null);

        tab.SelectPages([1, 2]);
        Assert.Equal([1, 2], tab.SelectedPagePositions.Order());
        Assert.True(tab.RotateSelectionCommand.CanExecute(90));
        tab.RotateSelectionCommand.Execute(90);
        tab.DeletePagesCommand.Execute(null);

        // The plan has the change, the tab shows and holds the document as it was.
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(4, tab.PageHistory!.Current.Slots.Count);
        Assert.Equal([0, 3, 4, 5], tab.PageHistory.Current.Slots.Select(slot => slot.SourceIndex));
        Assert.Equal(6, tab.PageCount);
        Assert.Same(document, tab.Document);
        Assert.Equal(6, document.PageCount);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
        Assert.Empty(tab.SelectedPagePositions);

        // Duplicate selects the copies, a blank page goes after the selection and is selected, a move keeps the selection on the moved pages.
        tab.SelectPages([0]);
        tab.DuplicateSelectionCommand.Execute(null);
        Assert.Equal([0, 0, 3, 4, 5], tab.PageHistory.Current.Slots.Select(slot => slot.SourceIndex));
        Assert.Equal([1], tab.SelectedPagePositions.Order());
        tab.InsertBlankPageCommand.Execute(null);
        Assert.Equal(6, tab.PageHistory.Current.Slots.Count);
        Assert.NotNull(tab.PageHistory.Current.Slots[2].Blank);
        Assert.Equal([2], tab.SelectedPagePositions.Order());
        Assert.True(tab.MoveSelectionTo(6));
        Assert.NotNull(tab.PageHistory.Current.Slots[5].Blank);
        Assert.Equal([5], tab.SelectedPagePositions.Order());
        Assert.Equal(6, tab.PageCount);
    }

    [AvaloniaFact]
    public async Task AnEditThatChangesNothingIsNoStep()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        tab.OrganizeCommand.Execute(null);
        var history = tab.PageHistory!;
        var version = history.Version;

        // Turning by a full circle, and moving a page next to where it is, give an equal plan: no step, nothing pending.
        tab.SelectPages([2]);
        tab.RotateSelectionCommand.Execute(360);
        Assert.False(tab.MoveSelectionTo(2));
        Assert.False(tab.MoveSelectionTo(3));
        Assert.Equal(version, history.Version);
        Assert.False(tab.HasPendingPageEdits);
        Assert.False(history.CanUndo);

        // A real step is one, and turning back to where it was is the identity again: nothing pending, though undo is possible.
        tab.RotateSelectionCommand.Execute(90);
        Assert.True(tab.HasPendingPageEdits);
        tab.RotateSelectionCommand.Execute(-90);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Equal(version + 2, history.Version);
        Assert.False(tab.AsksBeforeClosing);
    }

    [AvaloniaFact]
    public async Task PendingPlanLocksAnnotateAndFillModes()
    {
        var tab = await OpenAsync("forms-basic.pdf");
        Assert.True(tab.BeginAnnotateCommand.CanExecute(null));
        Assert.True(tab.ToggleFillCommand.CanExecute(null));

        // The mode ends an annotation mode that is on.
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsAnnotating);
        tab.OrganizeCommand.Execute(null);
        Assert.True(tab.IsOrganizing);
        Assert.False(tab.IsAnnotating);
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        Assert.True(tab.HasPendingPageEdits);

        // Neither mode starts, with the reason where the panels show one.
        Assert.False(tab.BeginAnnotateCommand.CanExecute(null));
        Assert.False(tab.ToggleAnnotateCommand.CanExecute(null));
        Assert.False(tab.HighlightCommand.CanExecute(null));
        Assert.False(tab.InkCommand.CanExecute(null));
        Assert.False(tab.ToggleFillCommand.CanExecute(null));
        Assert.False(tab.CanAnnotate);
        Assert.Equal(Strings.PageEditsPending, tab.PageEditsBlockedReason);
        Assert.Equal(tab.PageEditsBlockedReason, tab.AnnotateUnavailableReason);
        Assert.False(tab.OffersFill);
        await tab.EnterFillAsync();
        Assert.False(tab.IsFilling);
        Assert.False(tab.CanRunEdit);

        // In the mode but without a change the reason says so; discarding frees the modes again.
        tab.DiscardPageEditsCommand.Execute(null);
        Assert.False(tab.IsOrganizing);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Null(tab.PageEditsBlockedReason);
        Assert.Null(tab.PageHistory);
        Assert.True(tab.BeginAnnotateCommand.CanExecute(null));
        Assert.True(tab.ToggleFillCommand.CanExecute(null));
        Assert.True(tab.CanAnnotate);
        Assert.True(tab.OffersFill);
    }

    [AvaloniaFact]
    public async Task TheModeWithoutAChangeNamesItselfAsTheReason()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        tab.OrganizeCommand.Execute(null);

        Assert.Equal(Strings.PageOrganizeActive, tab.PageEditsBlockedReason);
        Assert.Equal(Strings.PageOrganizeActive, tab.AnnotateUnavailableReason);
        Assert.False(tab.BeginAnnotateCommand.CanExecute(null));

        // Leaving without a change is the same command; with a change it does nothing (apply or discard first).
        Assert.True(tab.OrganizeCommand.CanExecute(null));
        tab.SelectPages([0]);
        tab.DeletePagesCommand.Execute(null);
        Assert.False(tab.OrganizeCommand.CanExecute(null));
        tab.OrganizeCommand.Execute(null);
        Assert.True(tab.IsOrganizing);
        tab.UndoPagesCommand.Execute(null);
        Assert.True(tab.OrganizeCommand.CanExecute(null));
        tab.OrganizeCommand.Execute(null);
        Assert.False(tab.IsOrganizing);
    }

    [AvaloniaFact]
    public async Task PendingPlanCountsAsUnsavedWhenClosing()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "pages-marked.pdf");
        Assert.False(tab.AsksBeforeClosing);
        tab.OrganizeCommand.Execute(null);
        Assert.False(tab.AsksBeforeClosing);
        tab.SelectPages([1]);
        tab.DeletePagesCommand.Execute(null);
        Assert.True(tab.AsksBeforeClosing);
        Assert.True(viewModel.HasUnsavedChanges);

        // The tab and the window ask first: "Discard the page changes?" with "Cancel" as the default, no "Save".
        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        var question = viewModel.PendingSave!;
        Assert.Same(tab, question.Tab);
        Assert.Equal(path, question.Path);
        Assert.Equal("Discard the page changes to “pages-marked.pdf”?", question.Title);
        Assert.Equal(Strings.DiscardPageEditsText, question.Message);
        Assert.Equal(PendingSaveChoice.Cancel, question.DefaultChoice);
        Assert.DoesNotContain(PendingSaveChoice.Save, question.Choices);
        Click(window, card.ButtonOf(PendingSaveChoice.Cancel)!);
        await SaveCardHost.WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(4 + 1, tab.PageHistory!.Current.Slots.Count);

        // "Close and discard changes" closes the tab without writing anything.
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        viewModel.CloseActiveTabCommand.Execute(null);
        card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.Discard)!);
        await SaveCardHost.WaitUntilAsync(window, () => !viewModel.Tabs.Contains(tab));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task ADiscardedPlanDoesNotHideALaterChangeFromTheAnswersOfTheUpdateFlow()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        var start = tab.ChangeVersion;
        tab.OrganizeCommand.Execute(null);
        tab.SelectPages([0]);
        tab.DeletePagesCommand.Execute(null);
        var asked = tab.ChangeVersion;
        Assert.True(asked > start);
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        Assert.True(tab.ChangeVersion > asked);

        // The version never goes back, also when the plan is dropped: an answer "Don't save" for an older version asks again.
        var before = tab.ChangeVersion;
        tab.DiscardPageEditsCommand.Execute(null);
        Assert.True(tab.ChangeVersion > before);
        Assert.Equal(0, tab.EditVersion);
    }

    [AvaloniaFact]
    public async Task PendingPlanSurvivesMovingTheTabToANewWindow()
    {
        var (window, viewModel, view) = _host.Show();
        var (other, otherViewModel, _) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "pages-marked.pdf");
        tab.OrganizeCommand.Execute(null);
        tab.SelectPages([4]);
        tab.RotateSelectionCommand.Execute(180);
        tab.DuplicateSelectionCommand.Execute(null);
        var history = tab.PageHistory;
        var plan = history!.Current;
        var selected = tab.SelectedPagePositions.Order().ToArray();

        // The tab keeps its session and its state, also the plan, the selection and the mode, while the flag of the sharp page starts over.
        Assert.True(viewModel.DetachTab(tab));
        otherViewModel.AttachTab(tab, 0);
        Settle(other);

        Assert.True(tab.IsOrganizing);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Same(history, tab.PageHistory);
        Assert.Same(plan, tab.PageHistory!.Current);
        Assert.Equal(selected, tab.SelectedPagePositions.Order());
        Assert.True(tab.AsksBeforeClosing);
        Assert.True(otherViewModel.HasUnsavedChanges);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.True(tab.UndoPagesCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ExtractSplitAndExportAreDisabledWhilePlanOrChangesArePending()
    {
        var tab = await OpenAsync("pages-marked.pdf");

        // Nothing pending: they are possible, with or without the mode (a plan that equals the document changes nothing).
        Assert.Null(tab.PageOutputBlockedReason);
        Assert.True(tab.CanRunPageOutput);
        tab.OrganizeCommand.Execute(null);
        Assert.Null(tab.PageOutputBlockedReason);
        Assert.True(tab.CanRunPageOutput);

        // A pending plan: apply or discard first; the reason is the same one for all three.
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        Assert.Equal(Strings.PageOutputBlockedPlan, tab.PageOutputBlockedReason);
        Assert.False(tab.CanRunPageOutput);
        tab.UndoPagesCommand.Execute(null);
        Assert.Null(tab.PageOutputBlockedReason);
        tab.DiscardPageEditsCommand.Execute(null);

        // Unsaved changes of the document: save first.
        await tab.Editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(72, 72, 220, 90)], new AnnotationColor(255, 230, 0), null, null);
        await WaitUntilAsync(() => tab.IsDirty);
        Assert.Equal(Strings.PageOutputBlockedUnsaved, tab.PageOutputBlockedReason);
        Assert.False(tab.CanRunPageOutput);

        // Both together: the plan is named first.
        tab.OrganizeCommand.Execute(null);
        tab.SelectPages([0]);
        tab.DeletePagesCommand.Execute(null);
        Assert.Equal(Strings.PageOutputBlockedPlan, tab.PageOutputBlockedReason);
        Assert.True(tab.AsksBeforeClosing);
    }

    [AvaloniaFact]
    public async Task DeletingTheLastPageIsRefusedWithReason()
    {
        var tab = await OpenAsync("hello_world.pdf");
        Assert.Equal(1, tab.PageCount);
        tab.OrganizeCommand.Execute(null);
        var history = tab.PageHistory!;
        var version = history.Version;
        tab.SelectPages([0]);

        Assert.True(tab.DeletePagesCommand.CanExecute(null));
        tab.DeletePagesCommand.Execute(null);
        Assert.Equal(Strings.PageEditsLastPage, tab.PageEditNotice);
        Assert.Equal(version, history.Version);
        Assert.Single(history.Current.Slots);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Equal([0], tab.SelectedPagePositions.Order());

        // Every page of a longer document is the same refusal; the next edit that works clears the notice.
        tab.DismissPageEditNoticeCommand.Execute(null);
        Assert.Null(tab.PageEditNotice);
        tab.RotateSelectionCommand.Execute(90);
        Assert.Null(tab.PageEditNotice);

        var many = await OpenAsync("pages-marked.pdf");
        many.OrganizeCommand.Execute(null);
        many.SelectPages([0, 1, 2, 3, 4, 5]);
        many.DeletePagesCommand.Execute(null);
        Assert.Equal(Strings.PageEditsLastPage, many.PageEditNotice);
        Assert.Equal(6, many.PageHistory!.Current.Slots.Count);
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Die letzte Seite kann nicht gelöscht werden.", Strings.PageEditsLastPage);
    }

    [AvaloniaFact]
    public void ADocumentOverTheLimitOffersNoModeAndSaysWhy()
    {
        Assert.Null(TabViewModel.OrganizeUnavailableReasonFor(PageEditLimits.MaxSlots));
        var reason = TabViewModel.OrganizeUnavailableReasonFor(PageEditLimits.MaxSlots + 1);
        Assert.Equal("This document has more pages than can be organized (at most 20,000).", reason);
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Dieses Dokument hat mehr Seiten, als sich organisieren lassen (höchstens 20.000).", TabViewModel.OrganizeUnavailableReasonFor(PageEditLimits.MaxSlots + 1));
    }

    [AvaloniaFact]
    public async Task DuplicatingPastTheLimitIsRefusedWithReasonAndChangesNothing()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        tab.OrganizeCommand.Execute(null);

        // 6 pages doubled again and again: the step that would pass 20,000 pages is refused, the plan stays as it was.
        var history = tab.PageHistory!;
        while (history.Current.Slots.Count * 2 <= PageEditLimits.MaxSlots)
        {
            tab.SelectPages(Enumerable.Range(0, history.Current.Slots.Count));
            tab.DuplicateSelectionCommand.Execute(null);
            Assert.Null(tab.PageEditNotice);
        }

        var count = history.Current.Slots.Count;
        var version = history.Version;
        tab.SelectPages(Enumerable.Range(0, count));
        tab.DuplicateSelectionCommand.Execute(null);
        Assert.Equal(Strings.PageEditsLimit, tab.PageEditNotice);
        Assert.Equal(count, history.Current.Slots.Count);
        Assert.Equal(version, history.Version);
        Assert.Equal(6, tab.PageCount);
    }

    [AvaloniaFact]
    public async Task UndoRedoFollowTheHistory()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        tab.OrganizeCommand.Execute(null);
        Assert.False(tab.UndoPagesCommand.CanExecute(null));
        Assert.False(tab.RedoPagesCommand.CanExecute(null));
        Assert.False(tab.UndoCommand.CanExecute(null));

        tab.SelectPages([0, 1]);
        tab.DeletePagesCommand.Execute(null);
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(270);
        Assert.Equal(270, tab.PageHistory!.Current.Slots[0].ExtraRotation);
        Assert.True(tab.UndoPagesCommand.CanExecute(null));

        // Ctrl+Z (the edit command of the catalog) undoes a page step while the mode is on, never an annotation.
        Assert.True(tab.UndoCommand.CanExecute(null));
        tab.UndoCommand.Execute(null);
        Assert.Equal(0, tab.PageHistory.Current.Slots[0].ExtraRotation);
        Assert.Equal(4, tab.PageHistory.Current.Slots.Count);
        Assert.True(tab.RedoPagesCommand.CanExecute(null));
        Assert.True(tab.RedoCommand.CanExecute(null));
        Assert.False(tab.HasEditor);

        tab.UndoPagesCommand.Execute(null);
        Assert.Equal(6, tab.PageHistory.Current.Slots.Count);
        Assert.False(tab.HasPendingPageEdits);
        Assert.False(tab.UndoPagesCommand.CanExecute(null));
        tab.RedoPagesCommand.Execute(null);
        tab.RedoCommand.Execute(null);
        Assert.Equal(270, tab.PageHistory.Current.Slots[0].ExtraRotation);
        Assert.Equal(4, tab.PageHistory.Current.Slots.Count);
        Assert.True(tab.HasPendingPageEdits);
        Assert.False(tab.RedoPagesCommand.CanExecute(null));

        // Out of the mode the same command is the undo of the annotations again.
        tab.DiscardPageEditsCommand.Execute(null);
        Assert.False(tab.UndoCommand.CanExecute(null));
        Assert.False(tab.UndoPagesCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ABlankPageTakesTheSizeOfItsNeighbour()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        await WaitUntilAsync(() => tab.Session.LoadedSizes.Count == 6);
        var sizes = tab.Session.LoadedSizes;
        tab.OrganizeCommand.Execute(null);

        // After page 3 (the landscape one) the blank page is as large; selecting nothing puts it at the end, with the size of the last page.
        tab.SelectPages([2]);
        tab.InsertBlankPageCommand.Execute(null);
        var blank = tab.PageHistory!.Current.Slots[3].Blank;
        Assert.NotNull(blank);
        Assert.Equal(sizes[2].Width, blank.Width, 3);
        Assert.Equal(sizes[2].Height, blank.Height, 3);

        tab.ClearPageSelection();
        tab.InsertBlankPageCommand.Execute(null);
        var last = tab.PageHistory.Current.Slots[^1];
        Assert.NotNull(last.Blank);
        Assert.Equal(sizes[5].Width, last.Blank.Width, 3);
        Assert.Equal(sizes[5].Height, last.Blank.Height, 3);

        // A turned page is turned on the sheet: the blank page next to it has its displayed shape.
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        tab.InsertBlankPageCommand.Execute(null);
        var beside = tab.PageHistory.Current.Slots[1].Blank;
        Assert.NotNull(beside);
        Assert.Equal(sizes[0].Height, beside.Width, 3);
        Assert.Equal(sizes[0].Width, beside.Height, 3);
    }

    [AvaloniaFact]
    public async Task APageOfAnotherDocumentEndsTheModeWithTheOldOne()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        tab.OrganizeCommand.Execute(null);
        tab.SelectPages([0]);
        tab.DeletePagesCommand.Execute(null);
        Assert.True(tab.HasPendingPageEdits);

        await tab.OpenAsync(Copy("hello_world.pdf"));
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.PageCount == 1);

        Assert.False(tab.IsOrganizing);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Null(tab.PageHistory);
        Assert.Empty(tab.SelectedPagePositions);
        Assert.False(tab.AsksBeforeClosing);
    }

    [AvaloniaFact]
    public async Task EditingIsRefusedWhileACardAsksOrAnUpdateLocks()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        tab.OrganizeCommand.Execute(null);
        tab.SelectPages([0]);

        tab.IsAskedAbout = true;
        Assert.False(tab.RotateSelectionCommand.CanExecute(90));
        Assert.False(tab.DeletePagesCommand.CanExecute(null));
        tab.IsAskedAbout = false;
        Assert.True(tab.RotateSelectionCommand.CanExecute(90));

        tab.IsEditingFrozen = true;
        Assert.False(tab.RotateSelectionCommand.CanExecute(90));
        Assert.False(tab.InsertBlankPageCommand.CanExecute(null));
        Assert.False(tab.DiscardPageEditsCommand.CanExecute(null));
        tab.IsEditingFrozen = false;
        Assert.True(tab.InsertBlankPageCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task PageSourceIdsAreNeverReused()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        var first = tab.AllocatePageSource();
        var second = tab.AllocatePageSource();
        Assert.True(first.Value > 0);
        Assert.True(second.Value > first.Value);

        // Not after the plan is dropped either: a page of an earlier file never means a later one.
        tab.OrganizeCommand.Execute(null);
        tab.DiscardPageEditsCommand.Execute(null);
        Assert.True(tab.AllocatePageSource().Value > second.Value);
    }

    [AvaloniaFact]
    public async Task NothingIsCreatedAtStartup()
    {
        var tab = await OpenAsync("pages-marked.pdf");

        // An open document with a sharp page, and the catalog asked about every command of the mode: no plan, no history, no command of the mode.
        Assert.False(tab.IsOrganizing);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Null(tab.PageHistory);
        Assert.Empty(tab.SelectedPagePositions);
        Assert.Null(tab.PageEditsBlockedReason);
        Assert.Null(tab.PageEditNotice);
        Assert.False(tab.HasPageEditCommands);

        // Entering the mode makes the history; the commands are made by the first access, as for every command of a tab.
        tab.OrganizeCommand.Execute(null);
        Assert.NotNull(tab.PageHistory);
        _ = tab.RotateSelectionCommand;
        Assert.True(tab.HasPageEditCommands);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Pump();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The tab did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private string Copy(string name)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + "-" + name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    /// <summary>
    /// Opens a copy of a test file in a new tab without a window and, unless <paramref name="sharp"/> is false, marks its
    /// first sharp page as shown (as the document view does).
    /// </summary>
    private async Task<TabViewModel> OpenAsync(string name, bool sharp = true)
    {
        var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(tab);
        tab.AnnotationSettings = UserSettings.Detached();
        await tab.OpenAsync(Copy(name));
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null);
        tab.HasShownSharpPage = sharp;
        return tab;
    }
}
