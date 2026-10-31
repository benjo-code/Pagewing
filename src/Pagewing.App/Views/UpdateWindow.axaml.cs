using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The progress window of an update, one per process (per coordinator): "Install" in any window opens it, or brings it
/// to the front while it is open (<see cref="ShowFor"/>). It has a view model of its own over the coordinator and closes
/// by itself when the installation ends without restarting (cancelled, or a failure closed); the user cannot close it
/// while the installer works. While it is open the coordinator knows where it lies, for the window of the helper that
/// takes its place (<see cref="UpdateCoordinator.ProgressWindowBounds"/>). The helper of the Windows updater shows the same
/// window over its own installation, at that place (<see cref="ShowHelper"/>).
/// </summary>
public sealed partial class UpdateWindow : Window
{
    // The width the helper takes over from the progress window of the app, within sensible bounds.
    private const double MinHelperWidth = 320;
    private const double MaxHelperWidth = 1200;

    private static readonly ConditionalWeakTable<UpdateCoordinator, UpdateWindow> Shown = new();

    private UpdateViewModel? _viewModel;
    private IUpdateFlow? _coordinator;
    private bool _closingByFlow;

    public UpdateWindow()
    {
        InitializeComponent();
        UpdateHeader.PointerPressed += OnHeaderPressed;
    }

    /// <summary>Gets the progress window of <paramref name="coordinator"/> while it is open.</summary>
    internal static UpdateWindow? For(UpdateCoordinator coordinator) =>
        Shown.TryGetValue(coordinator, out var window) ? window : null;

    /// <summary>
    /// Shows the progress window of the coordinator of <paramref name="source"/> over <paramref name="owner"/>, or brings the
    /// open one to the front. Does nothing while no installation runs or waits with its failure.
    /// </summary>
    internal static UpdateWindow? ShowFor(UpdateViewModel source, Window owner)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(owner);
        if (source.Coordinator is not UpdateCoordinator coordinator)
        {
            return null;
        }

        if (For(coordinator) is { } open)
        {
            // Out of the way while a window asks about unsaved changes (it comes back by itself afterwards).
            if (open.IsVisible)
            {
                open.Activate();
            }

            return open;
        }

        var viewModel = new UpdateViewModel(coordinator, source.RequestLink);
        if (!viewModel.IsInFlow)
        {
            viewModel.Dispose();
            return null;
        }

        var window = new UpdateWindow { DataContext = viewModel };
        window.Attach(viewModel, coordinator);
        if (!OperatingSystem.IsWindows())
        {
            // Windows takes the icon of the executable; elsewhere the window carries it, as the main windows do.
            window.Icon = App.LoadWindowIcon();
        }

        window.Show(owner);
        return window;
    }

    /// <summary>
    /// Shows the window of the helper of the Windows updater over its installation (<paramref name="helper"/>), where the
    /// progress window of the app was (<see cref="Core.Updates.UpdateRequest"/>: position in physical pixels, width in
    /// device-independent pixels), or in the middle of the screen when that place is not known or not on a screen any
    /// more. The helper ends with the window.
    /// </summary>
    internal static UpdateWindow ShowHelper(Platform.Windows.WindowsUpdateHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        var viewModel = new UpdateViewModel(helper, static _ => { });
        var window = new UpdateWindow { DataContext = viewModel };
        window.Attach(viewModel, helper);
        if (helper.Request is { Left: { } left, Top: { } top } request
            && window.Screens.ScreenFromPoint(new PixelPoint(left, top)) is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(left, top);
            if (request.Width is >= MinHelperWidth and <= MaxHelperWidth)
            {
                window.Width = request.Width.Value;
            }
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (!OperatingSystem.IsWindows())
        {
            window.Icon = App.LoadWindowIcon();
        }

        window.Show();
        if (viewModel.IsFailed)
        {
            // Refused before it began (the folder or the request): the keys go to "Close".
            Dispatcher.UIThread.Post(window.FocusFirstButton);
        }

        return window;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);

        // The user cannot stop an installer that runs: closing the window (or Alt+F4) does nothing then. When the app
        // ends (the installer closes the windows), the window goes with it.
        if (!_closingByFlow && _viewModel is { IsLocked: true } && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_coordinator is { } coordinator)
        {
            if (coordinator is UpdateCoordinator shared && For(shared) == this)
            {
                Shown.Remove(shared);
                shared.ProgressWindowBounds = null;
            }

            // Closed by the user: closing is "Cancel" during the download and "Close" after a failure.
            coordinator.CancelInstall();
            coordinator.Dismiss();
        }

        if (_viewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        }

        _viewModel = null;
        _coordinator = null;
    }

    private void Attach(UpdateViewModel viewModel, IUpdateFlow coordinator)
    {
        _viewModel = viewModel;
        _coordinator = coordinator;
        if (coordinator is UpdateCoordinator shared)
        {
            Shown.AddOrUpdate(shared, this);
            shared.ProgressWindowBounds = () => (Position.X, Position.Y, ClientSize.Width, ClientSize.Height);
        }

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(UpdateViewModel.IsInFlow) && !viewModel.IsInFlow)
        {
            // Cancelled, or the failure was closed: back to the offer, the window goes.
            _closingByFlow = true;
            Close();
        }
        else if (e.PropertyName == nameof(UpdateViewModel.IsAwaitingAnswer))
        {
            // Right before the installer the windows are asked about unsaved changes: the card lies in the middle of a
            // main window, where this window (in front of its owner) would cover it. It steps aside until the answer.
            if (viewModel.IsAwaitingAnswer)
            {
                Hide();
            }
            else if (!IsVisible && viewModel.IsInFlow)
            {
                if (Owner is Window owner)
                {
                    Show(owner);
                }
                else
                {
                    Show();
                }
            }
        }
        else if (e.PropertyName == nameof(UpdateViewModel.OffersDownloadPage) && viewModel.IsFailed)
        {
            // The last of the properties of a failure: the buttons are there now. The keys go to the first way on.
            Dispatcher.UIThread.Post(FocusFirstButton);
        }
    }

    private void FocusFirstButton()
    {
        UpdateLayout();
        foreach (var button in new[] { UpdateRetryButton, UpdateDownloadPageButton, UpdateCloseButton })
        {
            if (button.IsEffectivelyVisible)
            {
                button.Focus(NavigationMethod.Unspecified);
                return;
            }
        }
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
