using Avalonia.Headless.XUnit;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// The update path and a page plan that waits (milestone 13, task 8): "Install" asks about it like about unsaved changes
/// (<see cref="Workspace.ResolveUnsavedChangesAsync"/> via <see cref="MainViewModel.ResolveUnsavedChangesAsync()"/>), with the
/// card "Discard the page changes?", and "Cancel" stops the installation before anything is fetched or staged. Network and
/// installer are fakes; files lie in temporary folders, never in the user's.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class ApplyPagesUpdateTests : UpdateTestHost
{
    private static readonly UpdateVersion Offered = new(0, 2, 0);

    private readonly SaveCardHost _files = new();

    public ApplyPagesUpdateTests() => Own(_files);

    [AvaloniaFact]
    public async Task UpdatePathAsksAboutAPendingPlan()
    {
        Source.Offer("0.2.0");
        UpdateCoordinator? made = null;
        var workspace = new Workspace(
            () => new ViewerSession(new PdfEngine()),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => _files.Picker,
            updateFactory: () => made ??= NewCoordinator());
        Own(workspace);
        var coordinator = workspace.UpdateCoordinator!;
        await coordinator.CheckNowAsync();
        await PumpUntilAsync(() => coordinator.State == UpdateState.Offer(Offered));

        var viewModel = workspace.OpenWindow();
        viewModel.Clock = _files.Clock;
        var window = (MainWindow)workspace.WindowOf(viewModel);
        var view = Find<DocumentView>(window, "DocumentView");
        var tab = await OpenAsync(window, viewModel, view, _files.Copy("pages-marked.pdf", "plan.pdf"));
        tab.OrganizeCommand.Execute(null);
        tab.SelectPages([0, 1]);
        tab.DeletePagesCommand.Execute(null);
        Assert.True(tab.HasPendingPageEdits);
        Assert.False(tab.IsDirty);
        await WaitUntilAsync(window, () => viewModel.Update is not null);

        // Nothing but the plan is unsaved, and the update asks about it before anything is fetched.
        var fetches = Source.FetchCalls;
        var install = coordinator.BeginInstallAsync();
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        Assert.Same(tab, viewModel.PendingSave!.Tab);
        Assert.Equal(Strings.DiscardPageEditsText, viewModel.PendingSave.Message);
        Assert.True(coordinator.IsAwaitingAnswer);
        Assert.Equal(fetches, Source.FetchCalls);
        Assert.Equal(0, Source.DownloadCalls);

        // "Cancel": the installation stops, the plan and the mode stay.
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(window, () => install.IsCompleted);
        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);
        Assert.Equal(0, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
        Assert.True(tab.HasPendingPageEdits);
        Assert.True(tab.IsOrganizing);
        Assert.Null(viewModel.PendingSave);
    }

    private static async Task<TabViewModel> OpenAsync(MainWindow window, MainViewModel viewModel, DocumentView view, string path) =>
        await SaveCardHost.OpenAsync(window, viewModel, view, path);
}
