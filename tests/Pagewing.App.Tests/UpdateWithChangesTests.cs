using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// "Install" with unsaved changes (milestone 9, task 18): before anything of the installation starts, every window with
/// unsaved tabs is asked in turn (the active one first) with the card of closing; "Cancel" stops the installation before
/// anything is fetched or staged. Right before the installer takes over, the windows are looked at again: a tab changed
/// during the download is asked about then, and "Cancel" there keeps the verified download for the next "Install".
/// Network and installer are fakes; files, staging and status lie in temporary folders, never in the user's.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateWithChangesTests : UpdateTestHost
{
    private static readonly UpdateVersion Offered = new(0, 2, 0);

    private readonly SaveCardHost _files = new();

    public UpdateWithChangesTests() => Own(_files);

    [AvaloniaFact]
    public async Task InstallAsksForEveryWindowWithChangesInTurn()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, aWindow, aTab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var (b, _, _) = await WindowWithFileAsync(workspace, "hello_world.pdf", "b.pdf", dirty: false);
        var (c, cWindow, cTab) = await WindowWithFileAsync(workspace, "text-rich.pdf", "c.pdf", dirty: true);
        cWindow.Activate();
        Settle(cWindow);
        Assert.Same(c, workspace.ActiveWindow);
        var asked = RecordQuestions(a, b, c);
        var fetches = Source.FetchCalls;

        var install = coordinator.BeginInstallAsync();

        // The active window first; nothing of the installation has started while it asks.
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(cWindow, c, PendingSaveKind.SaveChanges);
        Assert.Same(cTab, c.PendingSave!.Tab);
        Assert.Null(a.PendingSave);
        Assert.True(coordinator.IsAwaitingAnswer);
        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);
        Assert.Equal(fetches, Source.FetchCalls);
        Assert.Equal(0, Source.DownloadCalls);
        Assert.False(install.IsCompleted);
        c.PendingSave.Resolve(PendingSaveChoice.DontSave);

        // Then the other window with changes, brought to the front; the clean one is not asked.
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(aWindow, a, PendingSaveKind.SaveChanges);
        Assert.Same(aTab, a.PendingSave!.Tab);
        Assert.Same(a, workspace.ActiveWindow);
        Assert.Equal(0, Source.DownloadCalls);
        a.PendingSave.Resolve(PendingSaveChoice.DontSave);

        await WaitUntilAsync(aWindow, () => install.IsCompleted);
        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(1, Applier.Calls);

        // Each window once; the answers of the start count for the check before the installer (nothing changed since).
        Assert.Equal([c, a], asked);
        Assert.False(coordinator.IsAwaitingAnswer);
    }

    [AvaloniaFact]
    public async Task CancelOnTheCardStopsTheInstallationBeforeAnythingStarts()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var fetches = Source.FetchCalls;

        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => install.IsCompleted);

        // Nothing fetched, downloaded or staged; the offer stays; the tab keeps its changes.
        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);
        Assert.False(coordinator.IsAwaitingAnswer);
        Assert.Equal(fetches, Source.FetchCalls);
        Assert.Equal(0, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
        Assert.True(tab.IsDirty);
        Assert.Null(a.PendingSave);
    }

    [AvaloniaFact]
    public async Task NoDirtyTabsMeansNoQuestion()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, _) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: false);
        var asked = RecordQuestions(a);

        // As without the question: the download starts at once.
        var install = coordinator.BeginInstallAsync();
        Assert.Equal(UpdateStage.Downloading, coordinator.State.Stage);
        Assert.False(coordinator.IsAwaitingAnswer);
        await WaitUntilAsync(window, () => install.IsCompleted);

        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(1, Applier.Calls);
        Assert.Empty(asked);
    }

    [AvaloniaFact]
    public async Task SecondClickWhileAskingStartsNothing()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, aWindow, _) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var (b, bWindow, _) = await WindowWithFileAsync(workspace, "hello_world.pdf", "b.pdf", dirty: false);
        aWindow.Activate();
        Settle(aWindow);
        await WaitUntilAsync(aWindow, () => a.Update is { ShowButton: true } && b.Update is { ShowButton: true });

        a.Update!.InstallCommand.Execute(null);
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(aWindow, a, PendingSaveKind.SaveChanges);
        var question = a.PendingSave!;

        // "Install" again in both windows and a direct call: nothing new starts, the question stays the one.
        Assert.False(a.Update.InstallCommand.CanExecute(null));
        Assert.False(b.Update!.InstallCommand.CanExecute(null));
        a.Update.InstallCommand.Execute(null);
        b.Update.InstallCommand.Execute(null);
        var again = coordinator.BeginInstallAsync();
        Settle(aWindow);
        Settle(bWindow);
        Assert.False(again.IsCompleted);
        Assert.Same(question, a.PendingSave);
        Assert.Null(b.PendingSave);
        Assert.Equal(0, Source.DownloadCalls);

        // The card of the other window says that Pagewing waits for an answer, without "Install".
        b.Update.IsCardOpen = true;
        Settle(bWindow);
        var card = Find<UpdateCard>(bWindow, "UpdateCard");
        Assert.Equal(Strings.UpdateAwaitingAnswer, Find<TextBlock>(card, "UpdateText").Text);
        Assert.Equal("Waiting for your answer …", Strings.UpdateAwaitingAnswer);
        Assert.False(Find<Button>(card, "UpdateInstallButton").IsEffectivelyEnabled);
        Assert.True(Find<Button>(card, "UpdateLaterButton").IsEffectivelyEnabled);

        // Answered: one download, one installation, one progress window.
        question.Resolve(PendingSaveChoice.DontSave);
        await WaitUntilAsync(aWindow, () => again.IsCompleted);
        Assert.Equal(1, Source.DownloadCalls);
        Assert.Equal(1, Applier.Calls);
        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        var progress = UpdateWindow.For(coordinator);
        Assert.NotNull(progress);
        OwnWindow(progress);
    }

    [AvaloniaFact]
    public async Task SavedChangesAreWrittenBeforeTheApplierRuns()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var path = tab.FullPath!;
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        byte[]? atApply = null;
        Applier.OnApply = _ => atApply = File.ReadAllBytes(path);

        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        a.PendingSave!.Resolve(PendingSaveChoice.Save);
        await WaitUntilAsync(window, () => install.IsCompleted);

        // The file held the highlight when the installer was called (incrementally saved after the original bytes).
        Assert.Equal(1, Applier.Calls);
        Assert.NotNull(atApply);
        Assert.Equal(original, atApply[..original.Length]);
        Assert.True(atApply.Length > original.Length);
        Assert.Equal(atApply, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task ATabDirtiedDuringTheDownloadIsAskedBeforeTheApply()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: false);
        var path = tab.FullPath!;
        byte[]? atApply = null;
        Applier.OnApply = _ => atApply = File.ReadAllBytes(path);
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WaitUntilAsync(window, () => a.Update is { ShowButton: true });

        // No changes at "Install": no question, the download and its window start.
        a.Update!.InstallCommand.Execute(null);
        await WaitUntilAsync(window, () => Source.DownloadCalls == 1);
        var progress = UpdateWindow.For(coordinator);
        Assert.NotNull(progress);
        OwnWindow(progress);
        Assert.True(progress.IsVisible);

        // A highlight during the download; then the download ends and is verified.
        await SaveCardHost.MarkAsync(window, tab);
        Source.DownloadGate.SetResult();

        // Right before the installer: the card asks; the progress window steps aside so the card is seen.
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        Assert.Equal(UpdateStage.Ready, coordinator.State.Stage);
        Assert.True(coordinator.IsAwaitingAnswer);
        Assert.Equal(0, Applier.Calls);
        Assert.False(progress.IsVisible);
        Assert.True(File.Exists(Path.Combine(StagingOf("0.2.0"), "Pagewing-0.2.0-win-x64-setup.exe")));

        a.PendingSave!.Resolve(PendingSaveChoice.Save);
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Restarting);

        Assert.Equal(1, Applier.Calls);
        Assert.True(progress.IsVisible);
        Assert.False(coordinator.IsAwaitingAnswer);
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        Assert.Equal(atApply, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task CancelAtTheSecondCheckKeepsTheStagedUpdateInstallable()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: false);
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WaitUntilAsync(window, () => a.Update is { ShowButton: true });
        a.Update!.InstallCommand.Execute(null);
        await WaitUntilAsync(window, () => Source.DownloadCalls == 1);
        var progress = UpdateWindow.For(coordinator);
        Assert.NotNull(progress);
        OwnWindow(progress);
        await SaveCardHost.MarkAsync(window, tab);
        Source.DownloadGate.SetResult();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);

        // "Cancel" right before the installer: nothing is installed, the offer comes back, the progress window goes.
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => coordinator.State == UpdateState.Offer(Offered) && !coordinator.IsAwaitingAnswer);
        Assert.Equal(0, Applier.Calls);
        Assert.Null(UpdateWindow.For(coordinator));
        Assert.True(tab.IsDirty);

        // The verified download stays where it was staged.
        var staged = Path.Combine(StagingOf("0.2.0"), "Pagewing-0.2.0-win-x64-setup.exe");
        Assert.Equal(Source.Content, await File.ReadAllBytesAsync(staged, TestContext.Current.CancellationToken));

        // "Install" again: asked again (the tab still has its changes), then installed without a second download; the
        // manifest is verified again and the file is checked again.
        var fetches = Source.FetchCalls;
        Source.DownloadGate = null;
        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        a.PendingSave!.Resolve(PendingSaveChoice.DontSave);
        await WaitUntilAsync(window, () => install.IsCompleted);

        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(1, Source.DownloadCalls);
        Assert.Equal(fetches + 1, Source.FetchCalls);
        Assert.Equal(1, Applier.Calls);
        Assert.Equal(Source.Content, Applier.StagedBytes);
    }

    [AvaloniaFact]
    public async Task ACorruptedKeptDownloadIsNotInstalled()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: false);
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var install = coordinator.BeginInstallAsync();
        await WaitUntilAsync(window, () => Source.DownloadCalls == 1);
        await SaveCardHost.MarkAsync(window, tab);
        Source.DownloadGate.SetResult();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => install.IsCompleted);

        // Someone changes the kept file: the next "Install" checks it again and refuses it.
        var staged = Path.Combine(StagingOf("0.2.0"), "Pagewing-0.2.0-win-x64-setup.exe");
        var bytes = await File.ReadAllBytesAsync(staged, TestContext.Current.CancellationToken);
        bytes[0] ^= 0xFF;
        await File.WriteAllBytesAsync(staged, bytes, TestContext.Current.CancellationToken);
        install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        a.PendingSave!.Resolve(PendingSaveChoice.DontSave);
        await WaitUntilAsync(window, () => install.IsCompleted);

        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
        Assert.Equal(UpdateFailure.Checksum, coordinator.State.Error);
        Assert.Equal(0, Applier.Calls);
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
    }

    [AvaloniaFact]
    public async Task DiscardOfAnUnsavableTabLetsTheInstallProceed()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var path = tab.FullPath!;

        // Another program rewrote the file: the changes cannot be saved any more.
        var stamp = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(-5));
        var save = a.SaveActiveTabAsync();
        await SaveCardHost.WaitForCardAsync<SaveErrorCard>(window, a, PendingSaveKind.SaveError);
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);
        Assert.Equal(SaveFailureReason.SourceModified, tab.UnsaveableReason);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // "Install" warns that the changes are lost; "Discard" lets the installation go on.
        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.DiscardChanges);
        Assert.Equal(Strings.DiscardChangesSourceModified, a.PendingSave!.Message);
        Assert.Equal(0, Source.DownloadCalls);
        a.PendingSave.Resolve(PendingSaveChoice.Discard);
        await WaitUntilAsync(window, () => install.IsCompleted);

        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(1, Applier.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task CancelOnTheWarningOfAnUnsavableTabStopsTheInstallation()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var path = tab.FullPath!;
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));
        var save = a.SaveActiveTabAsync();
        await SaveCardHost.WaitForCardAsync<SaveErrorCard>(window, a, PendingSaveKind.SaveError);
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);

        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.DiscardChanges);
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => install.IsCompleted);

        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);
        Assert.Equal(0, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task TheUpdateButtonStaysUsableAfterACancel()
    {
        var (workspace, _) = await WorkspaceAsync();
        var (a, window, _) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        await WaitUntilAsync(window, () => a.Update is { ShowButton: true });
        var update = a.Update!;
        var normalText = update.Text;

        // The button opens the card, "Install" asks.
        update.ToggleCardCommand.Execute(null);
        Assert.True(update.IsCardOpen);
        update.InstallCommand.Execute(null);
        Assert.False(update.IsCardOpen);
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        var first = a.PendingSave!;

        // While it asks, the button still opens the card, which waits for the answer.
        update.ToggleCardCommand.Execute(null);
        Assert.True(update.IsCardOpen);
        Assert.True(update.IsAwaitingAnswer);
        Assert.Equal(Strings.UpdateAwaitingAnswer, update.Text);
        Assert.False(update.InstallCommand.CanExecute(null));
        update.LaterCommand.Execute(null);

        // "Cancel": the button and "Install" work as before, and "Install" asks again.
        first.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => a.PendingSave is null && !update.IsAwaitingAnswer);
        Assert.True(update.ShowButton);
        Assert.Equal(normalText, update.Text);
        Assert.True(update.InstallCommand.CanExecute(null));
        Assert.Null(UpdateWindow.For(a.UpdateCoordinator!));
        update.ToggleCardCommand.Execute(null);
        Assert.True(update.IsCardOpen);
        update.InstallCommand.Execute(null);
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        Assert.NotSame(first, a.PendingSave);
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => !update.IsAwaitingAnswer);
    }

    [AvaloniaFact]
    public async Task TheAwaitingTextSpeaksGerman()
    {
        Strings.Culture = new CultureInfo("de");
        var (workspace, _) = await WorkspaceAsync();
        var (a, window, _) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        await WaitUntilAsync(window, () => a.Update is { ShowButton: true });

        a.Update!.InstallCommand.Execute(null);
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);

        Assert.Equal("Wartet auf Ihre Antwort …", a.Update.Text);
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => !a.Update.IsAwaitingAnswer);
    }

    [AvaloniaFact]
    public async Task TheAppEndingWhileItAsksInstallsNothing()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, _) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);

        // The app ends (the workspace closes its windows and the coordinator): the question goes, nothing is installed.
        workspace.Dispose();
        await PumpUntilAsync(() => install.IsCompleted);

        Assert.Equal(0, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
    }

    [AvaloniaFact]
    public async Task EditsWhileTheInstallerRunsAreRefusedUntilItFails()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, answered) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var view = Find<DocumentView>(window, "DocumentView");
        var clean = await SaveCardHost.OpenAsync(window, a, view, _files.Copy("text-rich.pdf", "b.pdf"));
        clean.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(window, () => clean.IsAnnotating);
        var cleanBytes = await File.ReadAllBytesAsync(clean.FullPath!, TestContext.Current.CancellationToken);

        // The installer waits: the app has not ended yet, as while the real one copies the program.
        Applier.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Applier.Result = ApplyResult.Failed("The folder is read-only.");
        var atApply = new List<string>();
        Applier.OnApply = _ =>
        {
            // From the last answer on: every tab locked, none with a change nobody was asked about.
            foreach (var tab in workspace.Windows.SelectMany(each => each.Tabs))
            {
                atApply.Add($"{tab.FileName}: frozen={tab.IsEditingFrozen} inFlight={tab.HasEditsInFlight}");
            }
        };
        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        Assert.Same(answered, a.PendingSave!.Tab);
        a.PendingSave.Resolve(PendingSaveChoice.DontSave);
        await WaitUntilAsync(window, () => Applier.Calls == 1);
        var version = answered.EditVersion;
        Assert.Equal(["a.pdf: frozen=True inFlight=False", "b.pdf: frozen=True inFlight=False"], atApply);
        Assert.True(workspace.EditingFrozen);
        Assert.True(a.IsEditingFrozen);

        // A markup, the commands of the mode, undo, save and closing: nothing happens.
        a.ActivateTabCommand.Execute(clean);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, clean.Session.Document));
        var selection = await view.SelectAsync(0, 0, 5);
        Assert.NotNull(selection);
        Assert.False(clean.HighlightCommand.CanExecute(null));
        clean.HighlightCommand.Execute(null);
        await clean.ApplySelectionAsync(selection.Value, AnnotationKind.Underline);
        await clean.RunEditAsync(editor => editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(72, 72, 220, 90)], new AnnotationColor(255, 230, 0), null, null));
        Assert.False(clean.HasEditsInFlight);
        Assert.False(clean.IsDirty);
        Assert.False(clean.AsksBeforeClosing);
        Assert.False(answered.UndoCommand.CanExecute(null));
        answered.UndoCommand.Execute(null);
        Assert.False(answered.SaveCommand.CanExecute(null));
        Assert.False(clean.SaveAsCommand.CanExecute(null));
        a.ActivateTabCommand.Execute(answered);
        Settle(window);
        Assert.False(await a.SaveActiveTabAsync());
        Assert.Equal(version, answered.EditVersion);
        a.CloseActiveTabCommand.Execute(null);
        a.CloseTabCommand.Execute(clean);
        window.Close();
        Settle(window);
        Assert.Equal(2, a.Tabs.Count);
        Assert.True(window.IsVisible);
        Assert.Null(a.PendingSave);
        Assert.True(Find<TextBlock>(window, "EditingLockedText").IsEffectivelyVisible);
        Assert.Equal("Installing update – editing is locked", Strings.EditingLockedForUpdate);
        Assert.Equal(cleanBytes, await File.ReadAllBytesAsync(clean.FullPath!, TestContext.Current.CancellationToken));

        // The installer fails: the windows take changes again.
        Applier.Gate.SetResult();
        await WaitUntilAsync(window, () => install.IsCompleted);
        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
        Assert.False(workspace.EditingFrozen);
        Assert.False(answered.IsEditingFrozen);
        Assert.True(answered.UndoCommand.CanExecute(null));
        Assert.False(Find<TextBlock>(window, "EditingLockedText").IsEffectivelyVisible);
        await SaveCardHost.MarkAsync(window, clean);
    }

    [AvaloniaFact]
    public async Task WithoutAnInstallerTheLockEndsAtOnce()
    {
        var (workspace, coordinator) = await WorkspaceAsync(noApplier: true);
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: false);
        var frozen = new List<bool>();
        a.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsEditingFrozen))
            {
                frozen.Add(a.IsEditingFrozen);
            }
        };

        await coordinator.BeginInstallAsync();

        // Locked with the last look at the windows, unlocked with the failure.
        Assert.Equal(UpdateFailure.NoInstaller, coordinator.State.Error);
        Assert.Equal([true, false], frozen);
        Assert.False(tab.IsEditingFrozen);
        Assert.True(tab.SaveAsCommand.CanExecute(null));
        Settle(window);
    }

    [AvaloniaFact]
    public async Task ACommentTypedDuringTheDownloadIsAskedAboutAndSaved()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        Assert.True(await a.SaveActiveTabAsync());
        tab.BeginAnnotateCommand.Execute(null);
        var comments = tab.Comments;
        await WaitUntilAsync(window, () => tab.IsAnnotating && !comments.IsLoading && comments.Items.Count == 1);
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? contentsAtApply = null;
        Applier.OnApply = _ => contentsAtApply = tab.Comments.SelectedItem?.Annotation.Contents;

        // Clean at "Install": no question. A comment is typed during the download and not applied.
        var install = coordinator.BeginInstallAsync();
        await WaitUntilAsync(window, () => Source.DownloadCalls == 1);
        comments.SelectedItem = comments.Items[0];
        comments.CommentDraft = "Typed while it downloads";
        Assert.False(tab.IsDirty);
        Source.DownloadGate.SetResult();

        // Before the installer it is applied and asked about like any change; "Save" writes it.
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        Assert.Same(tab, a.PendingSave!.Tab);
        a.PendingSave.Resolve(PendingSaveChoice.Save);
        await WaitUntilAsync(window, () => install.IsCompleted);

        Assert.Equal(1, Applier.Calls);
        await using var reopened = await new PdfEngine().OpenAsync(tab.FullPath!, cancellationToken: TestContext.Current.CancellationToken);
        var saved = Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken), annotation => annotation.Kind == AnnotationKind.Highlight);
        Assert.Equal("Typed while it downloads", saved.Contents);

        // Locked now: the comment field keeps its text, read-only; nothing more is applied.
        Assert.True(comments.IsEditingFrozen);
        Assert.Equal("Typed while it downloads", comments.CommentDraft);
        Assert.False(comments.CommitCommentCommand.CanExecute(null));
        Assert.False(comments.DeleteCommand.CanExecute(null));
        Assert.NotNull(contentsAtApply);
    }

    [AvaloniaFact]
    public async Task AKeptDownloadGoesWithTheProcess()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: false);
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var install = coordinator.BeginInstallAsync();
        await WaitUntilAsync(window, () => Source.DownloadCalls == 1);
        await SaveCardHost.MarkAsync(window, tab);
        Source.DownloadGate.SetResult();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        a.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => install.IsCompleted);
        Assert.True(Directory.Exists(StagingOf("0.2.0")));

        // The app ends without installing: the kept download goes with it.
        coordinator.Dispose();
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
    }

    [AvaloniaFact]
    public async Task AQuestionOfClosingIsAnsweredFirstThenTheUpdateAsks()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);

        // The window asks before it closes; "Install" meanwhile waits for that answer and starts nothing.
        window.Close();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        var closing = a.PendingSave!;
        var install = coordinator.BeginInstallAsync();
        Settle(window);
        Assert.False(install.IsCompleted);
        Assert.True(coordinator.IsAwaitingAnswer);
        Assert.Same(closing, a.PendingSave);
        Assert.Equal(0, Source.DownloadCalls);

        // "Cancel" there keeps the window; then the update asks for itself.
        closing.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => a.PendingSave is { } next && !ReferenceEquals(next, closing));
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        Assert.True(window.IsVisible);
        Assert.Equal(0, Source.DownloadCalls);
        a.PendingSave!.Resolve(PendingSaveChoice.DontSave);
        await WaitUntilAsync(window, () => install.IsCompleted);

        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(1, Applier.Calls);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task AWindowThatClosesAfterItsOwnQuestionIsNotAskedAgain()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, window, _) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: true);
        var asked = RecordQuestions(a);
        window.Close();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, a, PendingSaveKind.SaveChanges);
        var install = coordinator.BeginInstallAsync();

        // "Don't save" closes the window: nothing is left to ask about, the installation goes on.
        a.PendingSave!.Resolve(PendingSaveChoice.DontSave);
        await PumpUntilAsync(() => install.IsCompleted);

        Assert.False(window.IsVisible);
        Assert.Single(asked);
        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(1, Applier.Calls);
    }

    [AvaloniaFact]
    public async Task TheUpdateButtonBringsTheAskingWindowForward()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (a, aWindow, tab) = await WindowWithFileAsync(workspace, "text-basic.pdf", "a.pdf", dirty: false);
        var (b, bWindow, _) = await WindowWithFileAsync(workspace, "hello_world.pdf", "b.pdf", dirty: false);
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var install = coordinator.BeginInstallAsync();
        await WaitUntilAsync(aWindow, () => Source.DownloadCalls == 1);
        await SaveCardHost.MarkAsync(aWindow, tab);
        Source.DownloadGate.SetResult();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(aWindow, a, PendingSaveKind.SaveChanges);

        // The user went to the other window; its button "Update" brings the question back to the front.
        bWindow.Activate();
        Settle(bWindow);
        Assert.Same(b, workspace.ActiveWindow);
        await WaitUntilAsync(bWindow, () => b.Update is { IsAwaitingAnswer: true });
        b.Update!.ToggleCardCommand.Execute(null);
        Settle(aWindow);
        Assert.Same(a, workspace.ActiveWindow);
        Assert.False(b.Update.IsCardOpen);

        a.PendingSave!.Resolve(PendingSaveChoice.DontSave);
        await WaitUntilAsync(aWindow, () => install.IsCompleted);
        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
    }

    [AvaloniaFact]
    public async Task MeasurementRunsAndTestsWithoutHooksBehaveAsBefore()
    {
        // A coordinator nobody gave the questions (tests without a workspace): "Install" starts at once, as in M8.1.
        Source.Offer("0.2.0");
        var bare = NewCoordinator();
        await bare.CheckNowAsync();
        Assert.Null(bare.BeforeInstall);
        Assert.Null(bare.BeforeApply);
        var install = bare.BeginInstallAsync();
        Assert.Equal(UpdateStage.Downloading, bare.State.Stage);
        Assert.False(bare.IsAwaitingAnswer);
        await install;
        Assert.Equal(UpdateStage.Restarting, bare.State.Stage);

        // A workspace without an updater (a measurement run) has no coordinator and asks nothing.
        var plain = new Workspace(
            () => new ViewerSession(new PdfEngine()),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => _files.Picker);
        Own(plain);
        var window = plain.OpenWindow();
        Assert.False(window.CanCheckForUpdates());
        Assert.Null(window.UpdateCoordinator);

        // The workspace that has one gives it both questions.
        var (_, coordinator) = await WorkspaceAsync();
        Assert.NotNull(coordinator.BeforeInstall);
        Assert.NotNull(coordinator.BeforeApply);
    }

    /// <summary>
    /// A workspace whose windows share one coordinator over the fake source (offering 0.2.0, already found), with the
    /// file picker of the host; the windows open without taking the focus.
    /// </summary>
    private async Task<(Workspace Workspace, UpdateCoordinator Coordinator)> WorkspaceAsync(bool noApplier = false)
    {
        Source.Offer("0.2.0");
        UpdateCoordinator? made = null;
        var workspace = new Workspace(
            () => new ViewerSession(new PdfEngine()),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => _files.Picker,
            updateFactory: () => made ??= NewCoordinator(noApplier: noApplier));
        Own(workspace);

        // The windows make the coordinator when they first need it; here it is asked for directly.
        var coordinator = workspace.UpdateCoordinator!;
        await coordinator.CheckNowAsync();
        await PumpUntilAsync(() => coordinator.State == UpdateState.Offer(Offered));
        return (workspace, coordinator);
    }

    /// <summary>Opens a window of <paramref name="workspace"/> with a copy of <paramref name="name"/>, with a highlight when <paramref name="dirty"/>.</summary>
    private async Task<(MainViewModel ViewModel, MainWindow Window, TabViewModel Tab)> WindowWithFileAsync(
        Workspace workspace, string name, string copyName, bool dirty)
    {
        var viewModel = workspace.OpenWindow();
        viewModel.Clock = _files.Clock;
        var window = (MainWindow)workspace.WindowOf(viewModel);
        var view = Find<DocumentView>(window, "DocumentView");
        var tab = await SaveCardHost.OpenAsync(window, viewModel, view, _files.Copy(name, copyName));
        if (dirty)
        {
            await SaveCardHost.MarkAsync(window, tab);
        }

        await WaitUntilAsync(window, () => viewModel.Update is not null);
        return (viewModel, window, tab);
    }

    /// <summary>Records every question of the save flow in <paramref name="windows"/>, once per window and question.</summary>
    private static List<MainViewModel> RecordQuestions(params MainViewModel[] windows)
    {
        var asked = new List<MainViewModel>();
        foreach (var window in windows)
        {
            PendingSave? last = null;
            window.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.PendingSave) && window.PendingSave is { } question && !ReferenceEquals(question, last))
                {
                    last = question;
                    asked.Add(window);
                }
            };
        }

        return asked;
    }
}
