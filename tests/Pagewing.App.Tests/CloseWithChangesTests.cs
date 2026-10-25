using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.Core.Saving;
using Pagewing.Core.Viewing;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// Closing a tab or a window with unsaved changes (milestone 9, task 14): the card "Save the changes?" asks first, for
/// several tabs in turn; "Cancel" keeps everything, "Don't save" writes nothing, "Save" writes before closing; a tab whose
/// changes cannot be saved warns that they are lost. Copies in a temp folder, settings and history in memory or there.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class CloseWithChangesTests : IDisposable
{
    private readonly SaveCardHost _host = new();

    public void Dispose() => _host.Dispose();

    [AvaloniaFact]
    public async Task ClosingADirtyTabAsksFirstAndCancelKeepsTheTab()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);

        // Ctrl+W: the card in the document area of the tab, "Save" with the focus.
        view.Focus();
        Press(window, Key.W, PhysicalKey.W, RawInputModifiers.Control);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        var question = viewModel.PendingSave!;
        Assert.Same(tab, question.Tab);
        Assert.Equal(path, question.Path);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.Same(Find<Panel>(window, "DocumentAreaLayers"), card.Parent);
        Assert.Equal([Strings.SaveChoiceSave, Strings.SaveChoiceDontSave, Strings.SaveChoiceCancel], ShownButtons(card));

        // Closing the same tab again while it asks is ignored (key, close button, menu command).
        Press(window, Key.W, PhysicalKey.W, RawInputModifiers.Control);
        tab.CloseCommand.Execute(null);
        viewModel.CloseTabCommand.Execute(tab);
        Settle(window);
        Assert.Same(question, viewModel.PendingSave);

        // "Cancel": the tab stays with its changes, the card goes, the keys go back to the document.
        Click(window, card.ButtonOf(PendingSaveChoice.Cancel)!);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.False(card.IsVisible);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.True(tab.IsDirty);
        Assert.Single(await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));

        // The close button of the tab asks again.
        Click(window, Find<Button>(window, "CloseTabButton"));
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.NotSame(question, viewModel.PendingSave);
    }

    [AvaloniaFact]
    public async Task DontSaveClosesWithoutWriting()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab);

        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 0);

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_host.Folder).Select(Path.GetFileName));
        Assert.Null(viewModel.PendingSave);
        Assert.False(card.IsVisible);
    }

    [AvaloniaFact]
    public async Task SaveThenCloseWritesBeforeClosing()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab);

        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);

        // Enter on "Save", which has the focus: written first, then closed.
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 0);

        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(original, saved[..original.Length]);
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowAsksForEachDirtyTabInTurn()
    {
        var (window, viewModel, view) = _host.Show();
        var (first, firstPath) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var (clean, _) = await _host.OpenCopyAsync(window, viewModel, view, "hello_world.pdf");
        var (second, secondPath) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf", "second.pdf");
        await MarkAsync(window, first);
        await MarkAsync(window, second);
        var firstOriginal = await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken);
        var closed = 0;
        window.Closed += (_, _) => closed++;

        // The active tab first.
        window.Close();
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.True(window.IsVisible);
        Assert.Same(second, viewModel.PendingSave!.Tab);
        Assert.Same(second, viewModel.ActiveTab);

        // A second close while it asks changes nothing.
        window.Close();
        Settle(window);
        Assert.Same(second, viewModel.PendingSave!.Tab);

        Click(window, card.ButtonOf(PendingSaveChoice.Save)!);

        // Then the other tab with changes, shown for it; the clean tab is not asked about.
        await WaitUntilAsync(window, () => viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges } question && ReferenceEquals(question.Tab, first));
        Assert.Same(first, viewModel.ActiveTab);
        Assert.False(second.IsDirty);
        Assert.True(window.IsVisible);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);

        await WaitUntilAsync(window, () => closed == 1);
        Assert.False(window.IsVisible);
        Assert.Equal(firstOriginal, await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken));
        await using var reopened = await new PdfEngine().OpenAsync(secondPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        Assert.False(clean.IsDirty);
    }

    [AvaloniaFact]
    public async Task CancelOnTheSecondTabKeepsBothWindowAndTabs()
    {
        var (window, viewModel, view) = _host.Show();
        var (first, firstPath) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var (second, secondPath) = await _host.OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await MarkAsync(window, first);
        await MarkAsync(window, second);
        var originals = (await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(secondPath, TestContext.Current.CancellationToken));

        window.Close();
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);
        await WaitUntilAsync(window, () => viewModel.PendingSave is { } question && ReferenceEquals(question.Tab, first) && card.IsVisible);

        // "Cancel" on the second question: the window stays with both tabs and all their changes; nothing was written.
        Press(window, Key.Escape, PhysicalKey.Escape);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Settle(window);
        Assert.True(window.IsVisible);
        Assert.Equal([first, second], viewModel.Tabs);
        Assert.True(first.IsDirty);
        Assert.True(second.IsDirty);
        Assert.Equal(originals.Item1, await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken));
        Assert.Equal(originals.Item2, await File.ReadAllBytesAsync(secondPath, TestContext.Current.CancellationToken));

        // Closing again asks again, the active tab (now the first) first.
        window.Close();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.Same(first, viewModel.PendingSave!.Tab);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowRunsTheHistoryWritingOnce()
    {
        var recent = RecentFilesService.Load(Directory.CreateDirectory(Path.Combine(_host.Folder, "config")).FullName);
        var (window, viewModel, view) = _host.Show(recent);
        var (first, firstPath) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var (_, secondPath) = await _host.OpenCopyAsync(window, viewModel, view, "hello_world.pdf");
        await MarkAsync(window, first);
        await WaitUntilAsync(window, () => recent.IsLoaded && !recent.HasUnsavedChanges);
        var writes = recent.Writes;
        var changes = 0;
        recent.Changed += () => changes++;

        // Asked and cancelled: the history is not touched.
        window.Close();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.Equal(writes, recent.Writes);
        Assert.Equal(0, changes);

        // Asked and answered: both tabs go into the history with their position, in one write.
        window.Close();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Assert.True(viewModel.PendingSave!.Choose(PendingSaveChoice.DontSave));
        await WaitUntilAsync(window, () => !window.IsVisible);
        Settle(window);

        Assert.Equal(writes + 1, recent.Writes);
        Assert.Equal(1, changes);
        Assert.Equal(
            new[] { firstPath, secondPath }.Order(StringComparer.Ordinal),
            recent.Files.Items.Take(2).Select(item => item.Path).Order(StringComparer.Ordinal));
        Assert.All(recent.Files.Items.Take(2), item => Assert.NotNull(item.View));
    }

    [AvaloniaFact]
    public async Task MovingADirtyTabKeepsItDirty()
    {
        var workspace = new Workspace(
            () => new ViewerSession(new PdfEngine()),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => _host.Picker);
        _host.Own(workspace);
        var first = workspace.OpenWindow();
        var firstWindow = (MainWindow)workspace.WindowOf(first);
        var firstView = Find<DocumentView>(firstWindow, "DocumentView");
        await OpenAsync(firstWindow, first, firstView, _host.Copy("hello_world.pdf"));
        var path = _host.Copy("text-basic.pdf");
        var tab = await OpenAsync(firstWindow, first, firstView, path);
        await MarkAsync(firstWindow, tab);
        var editor = tab.Editor;
        var document = tab.Document;

        workspace.MoveToNewWindow(first, tab);
        var second = workspace.Windows[1];
        var secondWindow = (MainWindow)workspace.WindowOf(second);
        second.Clock = _host.Clock;
        await WaitUntilAsync(secondWindow, () => second.ActiveTab == tab && Find<DocumentView>(secondWindow, "DocumentView").Document == document);

        // The same editor with its history, still unsaved, with its dot in the new window; the old window lets it go.
        Assert.Same(editor, tab.Editor);
        Assert.True(tab.IsDirty);
        Assert.True(tab.CanUndo);
        Assert.Same(document, tab.Document);
        Assert.True(Find<Panel>(secondWindow, "TabUnsavedDot").IsEffectivelyVisible);
        Assert.False(first.HasUnsavedChanges);
        Assert.True(second.HasUnsavedChanges);

        // Closing it there asks there; "Save" writes it from the new window.
        second.CloseActiveTabCommand.Execute(null);
        await WaitForCardAsync<SaveChangesCard>(secondWindow, second, PendingSaveKind.SaveChanges);
        Assert.Null(first.PendingSave);
        Assert.True(second.PendingSave!.Resolve(PendingSaveChoice.Save));
        await WaitUntilAsync(secondWindow, () => !secondWindow.IsVisible);
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task SecondTabOfTheSameFileGetsTheFileChangedCardOnSave()
    {
        // Two windows with the same file (a window activates its open tab instead of a second one).
        var path = _host.Copy("text-basic.pdf");
        var (window1, viewModel1, view1) = _host.Show();
        var (window2, viewModel2, view2) = _host.Show();
        var tab1 = await OpenAsync(window1, viewModel1, view1, path);
        var tab2 = await OpenAsync(window2, viewModel2, view2, path);
        await MarkAsync(window1, tab1);
        await MarkAsync(window2, tab2, page: 1);

        // The first one saves: the file at the path is a new one now.
        Assert.True(await viewModel1.SaveActiveTabAsync());

        // The second one finds another file at its path: overwrite, save as (default) or cancel.
        var save = viewModel2.SaveActiveTabAsync();
        var card = await WaitForCardAsync<FileChangedCard>(window2, viewModel2, PendingSaveKind.FileChanged);
        Assert.Equal(PendingSaveChoice.SaveAs, viewModel2.PendingSave!.DefaultChoice);
        Assert.Contains("primary", card.ButtonOf(PendingSaveChoice.SaveAs)!.Classes);
        Assert.Equal([Strings.SaveChoiceOverwrite, Strings.SaveChoiceSaveAs, Strings.SaveChoiceCancel], ShownButtons(card));

        // Enter takes "Save as": the dialog, here into another file; the first file keeps the first tab's save.
        var target = Path.Combine(_host.Folder, "second.pdf");
        _host.Picker.Result = target;
        Press(window2, Key.Enter, PhysicalKey.Enter);
        Assert.True(await save);
        Assert.Equal(target, tab2.FullPath);
        await using (var written = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Single(await written.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
            Assert.Empty(await written.GetAnnotationsAsync(1, TestContext.Current.CancellationToken));
        }
    }

    [AvaloniaFact]
    public async Task SourceModifiedTabWarnsThatClosingDiscardsTheEdits()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);
        var stamp = File.GetLastWriteTimeUtc(path);

        // Another program rewrote the file in place: the save cannot work, the tab knows it.
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(-5));
        var save = viewModel.SaveActiveTabAsync();
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(await save);
        Assert.Equal(SaveFailureReason.SourceModified, tab.UnsaveableReason);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // Closing warns that the changes are lost; no "Save" that could not work; "Cancel" is the default.
        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        var question = viewModel.PendingSave!;
        Assert.Equal(Strings.DiscardChangesSourceModified, question.Message);
        Assert.Equal(Find<TextBlock>(card, "SaveChangesText").Text, question.Message);
        Assert.Equal([Strings.SaveChoiceDiscard, Strings.SaveChoiceCancel], ShownButtons(card));
        Assert.Equal(PendingSaveChoice.Cancel, question.DefaultChoice);
        Assert.True(Find<Control>(card, "SaveChangesWarningIcon").IsEffectivelyVisible);
        Assert.False(Find<Control>(card, "SaveChangesIcon").IsEffectivelyVisible);
        Assert.False(card.ButtonOf(PendingSaveChoice.Discard)!.IsEffectivelyEnabled);

        // "Cancel" keeps the tab; after the file is as it was, a save works again and the flag goes.
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.Contains(tab, viewModel.Tabs);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.Null(tab.UnsaveableReason);

        // Once more unsaveable, then "Close and discard changes": closed, the file as it was.
        await MarkAsync(window, tab);
        stamp = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(-5));
        save = viewModel.SaveActiveTabAsync();
        await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);
        before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        viewModel.CloseActiveTabCommand.Execute(null);
        card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.Discard)!);
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 0);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task ATabChangedAfterItsAnswerIsAskedAgainBeforeTheWindowCloses()
    {
        var (window, viewModel, view) = _host.Show();
        var (b, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (a, aPath) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, a);
        await MarkAsync(window, b);
        var closed = 0;
        window.Closed += (_, _) => closed++;

        // A (active) is saved first; then the card for B waits.
        window.Close();
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.Same(a, viewModel.PendingSave!.Tab);
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => viewModel.PendingSave is { } question && ReferenceEquals(question.Tab, b) && card.IsVisible);
        Assert.False(a.IsDirty);

        // Meanwhile the user goes back to A and changes it again, then answers B with "Don't save".
        viewModel.ActivateTabCommand.Execute(a);
        Settle(window);
        Assert.False(card.IsVisible);
        await MarkAsync(window, a, page: 1);
        viewModel.ActivateTabCommand.Execute(b);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);

        // The window does not close: A is asked about its new change.
        await WaitUntilAsync(window, () => viewModel.PendingSave is { } question && ReferenceEquals(question.Tab, a) && card.IsVisible);
        Assert.True(window.IsVisible);
        Assert.Equal(0, closed);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);
        await WaitUntilAsync(window, () => closed == 1);

        // A keeps only its first save.
        await using var reopened = await new PdfEngine().OpenAsync(aPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        Assert.Empty(await reopened.GetAnnotationsAsync(1, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task ADirtyTabAttachedWhileTheWindowAsksIsAskedBeforeItCloses()
    {
        var (window, viewModel, view) = _host.Show();
        var (other, otherViewModel, otherView) = _host.Show();
        var (first, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, first);
        await _host.OpenCopyAsync(other, otherViewModel, otherView, "hello_world.pdf");
        var (moved, _) = await _host.OpenCopyAsync(other, otherViewModel, otherView, "text-basic.pdf", "moved.pdf");
        await MarkAsync(other, moved);
        var closed = 0;
        window.Closed += (_, _) => closed++;

        window.Close();
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.Same(first, viewModel.PendingSave!.Tab);

        // A dirty tab comes from the other window (a tab dropped on the strip) while the first question waits.
        otherViewModel.DetachTab(moved);
        viewModel.AttachTab(moved, 1);
        Settle(window);
        viewModel.ActivateTabCommand.Execute(first);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);

        // It is asked about too, before the window closes.
        await WaitUntilAsync(window, () => viewModel.PendingSave is { } question && ReferenceEquals(question.Tab, moved) && card.IsVisible);
        Assert.Equal(0, closed);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);
        await WaitUntilAsync(window, () => closed == 1);
    }

    [AvaloniaFact]
    public async Task ATabDiscardedAndNotTouchedIsNotAskedTwice()
    {
        var (window, viewModel, view) = _host.Show();
        var (b, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (a, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, a);
        await MarkAsync(window, b);
        var asked = new List<TabViewModel>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PendingSave) && viewModel.PendingSave is { } question)
            {
                asked.Add(question.Tab);
            }
        };
        var closed = 0;
        window.Closed += (_, _) => closed++;

        window.Close();
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);
        await WaitUntilAsync(window, () => viewModel.PendingSave is { } question && ReferenceEquals(question.Tab, b) && card.IsVisible);

        // Looking at A again without changing it keeps its answer.
        viewModel.ActivateTabCommand.Execute(a);
        Settle(window);
        viewModel.ActivateTabCommand.Execute(b);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);

        await WaitUntilAsync(window, () => closed == 1);
        Assert.Equal([a, b], asked);
    }

    [AvaloniaFact]
    public async Task SaveThatCannotRunNowSaysWhyAndKeepsTheTab()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab);
        var release = new TaskCompletionSource();
        _host.Print.SystemDialog = true;
        _host.Print.DialogTicket = new PrintTicket(new PrintSettings { Printer = "Plotter" });
        _host.Print.OnPrint = async (_, _, cancellationToken) => await release.Task.WaitAsync(cancellationToken);
        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: true });

        // "Save" on the close card while the tab prints: a card says why, with "Cancel" alone; the tab stays.
        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Press(window, Key.Enter, PhysicalKey.Enter);
        var card = await WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        Assert.Equal(Strings.SaveNotPossiblePrinting, Find<TextBlock>(card, "SaveErrorText").Text);
        Assert.Equal([Strings.SaveChoiceCancel], ShownButtons(card));
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.True(tab.IsDirty);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        release.SetResult();
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: false });
    }

    [AvaloniaFact]
    public async Task EditAndPrintKeysDoNothingBehindTheCard()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab);
        await MarkAsync(window, tab, page: 1);
        await tab.Editor.UndoAsync();
        await WaitUntilAsync(window, () => tab.CanRedo);
        var version = tab.Editor.Version;

        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        var question = viewModel.PendingSave!;

        // With the focus on the card and elsewhere: undo, redo, save, save as and print do nothing.
        foreach (var focusOnCard in new[] { true, false })
        {
            if (!focusOnCard)
            {
                Find<Button>(window, "PaletteButton").Focus();
                Settle(window);
            }

            Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
            Press(window, Key.Y, PhysicalKey.Y, RawInputModifiers.Control);
            Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
            Press(window, Key.S, PhysicalKey.S, RawInputModifiers.Control);
            Press(window, Key.S, PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift);
            Press(window, Key.P, PhysicalKey.P, RawInputModifiers.Control);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Settle(window);

            Assert.Equal(version, tab.Editor.Version);
            Assert.Null(viewModel.PrintJob);
            Assert.Equal(OverlayKind.None, viewModel.Overlay);
            Assert.Same(question, viewModel.PendingSave);
            Assert.Equal(0, _host.Picker.Calls);
        }

        Assert.False(viewModel.Commands.ById("edit.undo")!.CanExecute());
        Assert.False(viewModel.Commands.ById("edit.redo")!.CanExecute());
        Assert.False(viewModel.Commands.ById("file.save")!.CanExecute());
        Assert.False(viewModel.Commands.ById("file.saveAs")!.CanExecute());
        Assert.False(viewModel.Commands.ById("file.print")!.CanExecute());
        Assert.True(card.IsVisible);

        // Answered: they act again.
        question.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.True(viewModel.Commands.ById("edit.undo")!.CanExecute());
        Assert.True(viewModel.Commands.ById("file.print")!.CanExecute());
    }

    [AvaloniaFact]
    public async Task ATabDiscardedAndChangedAfterwardsIsAskedAgain()
    {
        var (window, viewModel, view) = _host.Show();
        var (b, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (a, _) = await _host.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, a);
        await MarkAsync(window, b);
        var closed = 0;
        window.Closed += (_, _) => closed++;

        // "Don't save" for A, then A changes while the card for B waits: the answer was for the old state of A.
        window.Close();
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);
        await WaitUntilAsync(window, () => viewModel.PendingSave is { } question && ReferenceEquals(question.Tab, b) && card.IsVisible);
        viewModel.ActivateTabCommand.Execute(a);
        Settle(window);
        await MarkAsync(window, a, page: 1);
        viewModel.ActivateTabCommand.Execute(b);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.DontSave)!);

        await WaitUntilAsync(window, () => viewModel.PendingSave is { } question && ReferenceEquals(question.Tab, a) && card.IsVisible);
        Assert.Equal(0, closed);
        Press(window, Key.Escape, PhysicalKey.Escape);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.True(window.IsVisible);
        Assert.Equal(2, viewModel.Tabs.Count);
    }
}
