using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The side panel of the window (design system sections 6 and 7.3): shows the panel of the active tab beside the
/// document, or over it below <see cref="MainViewModel.NarrowWindowWidth"/>; resizes it with the splitter on its edge
/// (a double click restores the default width; the settings keep the width once the edge is let go); fades it in and
/// out once the window has shown its first frame, over the durations of the resources (<see cref="Appearance"/>). The
/// <see cref="SidePanel"/> is created only when a panel opens for the first time, so nothing of it is built in the
/// startup path.
/// </summary>
public sealed partial class MainWindow
{
    // Section 5.6, used only when the resources are missing (Appearance.OpenDurationKey and CloseDurationKey).
    private static readonly TimeSpan DefaultOpenDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DefaultCloseDuration = TimeSpan.FromMilliseconds(100);
    private static readonly CornerRadius DocumentCorner = new(10, 0, 0, 0);

    // Created with the first fade, not in the startup path; made again when the duration in the resources changed.
    private Transitions? _openTransitions;
    private Transitions? _closeTransitions;
    private DispatcherTimer? _closeTimer;
    private SidePanel? _sidePanel;

    // The tab whose panel the window shows; a change of the panel of the same tab fades, a change of the tab does not.
    private TabViewModel? _panelTab;
    private bool _closing;
    private bool _settingColumn;

    // Section 5.6: no animation before the first frame of the window.
    private bool _animationsAllowed;

    /// <summary>Gets the column the panel takes while it lies beside the document.</summary>
    private ColumnDefinition PanelColumn => WorkArea.ColumnDefinitions[1];

    private void InitializePanel()
    {
        DocumentView.FirstSharpTileDrawn += () => _viewModel?.OnFirstSharpTileDrawn();
        PanelColumn.PropertyChanged += OnPanelColumnChanged;
        PanelSplitter.AddHandler(PointerPressedEvent, OnSplitterPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The width is stored once the edge is let go, not on every pixel of the drag.
        PanelSplitter.DragCompleted += (_, _) => _viewModel?.SavePanelWidth();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RequestAnimationFrame(_ => _animationsAllowed = true);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // A fade-out that is still running does not collapse a closed window.
        StopCloseTimer();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateNarrowWindow();
    }

    private void AttachPanel(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (next is not null)
        {
            next.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateNarrowWindow();
        UpdatePanel();
    }

    private void UpdateNarrowWindow()
    {
        if (_viewModel is not null && Bounds.Width > 0)
        {
            _viewModel.IsNarrowWindow = Bounds.Width < MainViewModel.NarrowWindowWidth;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ActivePanel) or nameof(MainViewModel.ActiveTab) or nameof(MainViewModel.HasTabs)
            or nameof(MainViewModel.PanelWidth) or nameof(MainViewModel.IsNarrowWindow))
        {
            UpdatePanel();
        }
    }

    /// <summary>Brings the panel in line with the view model: the panel of the active tab, its width and where it lies.</summary>
    private void UpdatePanel()
    {
        var viewModel = _viewModel;
        var tab = viewModel?.ActiveTab;
        var kind = viewModel is { HasTabs: true } ? viewModel.ActivePanel : SidePanelKind.None;
        var sameTab = ReferenceEquals(tab, _panelTab);
        _panelTab = tab;
        if (viewModel is null || kind == SidePanelKind.None)
        {
            if (!PanelHost.IsVisible)
            {
                return;
            }

            if (sameTab && _animationsAllowed)
            {
                FadeOut();
            }
            else
            {
                Collapse();
            }

            return;
        }

        StopCloseTimer();
        var panel = _sidePanel ??= CreateSidePanel();
        panel.CloseCommand = viewModel.CloseSidePanelCommand;
        panel.DataContext = tab;
        panel.Kind = kind;

        // Beside the document the panel takes its own column; over it (narrow window) the document keeps its width.
        var beside = !viewModel.IsNarrowWindow;
        panel.IsOverlay = !beside;
        PanelHost.Width = viewModel.PanelWidth;
        SetPanelColumn(beside ? viewModel.PanelWidth : 0, beside);
        PanelSplitter.IsVisible = beside;
        DocumentArea.CornerRadius = beside ? default : DocumentCorner;

        var wasShown = PanelHost.IsVisible && !_closing;
        _closing = false;
        PanelHost.IsVisible = true;
        if (!wasShown)
        {
            Fade(1, sameTab ? OpenTransitions() : null);
        }
    }

    /// <summary>The fade-in with the current open duration, or none when it is zero (reduced motion).</summary>
    private Transitions? OpenTransitions()
    {
        var duration = Appearance.Duration(this, Appearance.OpenDurationKey, DefaultOpenDuration);
        if (duration <= TimeSpan.Zero)
        {
            return null;
        }

        if (_openTransitions is not [DoubleTransition { } open] || open.Duration != duration)
        {
            _openTransitions = [new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = new CubicEaseOut() }];
        }

        return _openTransitions;
    }

    private SidePanel CreateSidePanel()
    {
        var panel = new SidePanel { Name = "SidePanel" };
        PanelHost.Children.Add(panel);
        return panel;
    }

    /// <summary>
    /// Fades the panel out over the close duration (100 ms) and collapses it afterwards, unless it opens again
    /// meanwhile; with a zero duration (reduced motion) it collapses at once.
    /// </summary>
    private void FadeOut()
    {
        if (_closing)
        {
            return;
        }

        var duration = Appearance.Duration(this, Appearance.CloseDurationKey, DefaultCloseDuration);
        if (duration <= TimeSpan.Zero)
        {
            Collapse();
            return;
        }

        _closing = true;
        if (_closeTransitions is not [DoubleTransition { } close] || close.Duration != duration)
        {
            _closeTransitions = [new DoubleTransition { Property = OpacityProperty, Duration = duration }];
        }

        Fade(0, _closeTransitions);
        if (_closeTimer is null)
        {
            _closeTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _closeTimer.Tick += (_, _) => Collapse();
        }

        _closeTimer.Interval = duration;
        _closeTimer.Start();
    }

    /// <summary>Hides the panel at once: no column, no splitter, the document area takes the corner again, the content goes.</summary>
    private void Collapse()
    {
        StopCloseTimer();
        _closing = false;
        PanelHost.Transitions = null;
        PanelHost.Opacity = 0;
        PanelHost.IsVisible = false;
        SetPanelColumn(0, beside: false);
        PanelSplitter.IsVisible = false;
        DocumentArea.CornerRadius = DocumentCorner;
        if (_sidePanel is not null)
        {
            // Releases the content and the tab: a closed panel keeps nothing of them (the tab may be closed or moved
            // to another window meanwhile).
            _sidePanel.Kind = SidePanelKind.None;
            _sidePanel.DataContext = null;
        }
    }

    private void Fade(double opacity, Transitions? transitions)
    {
        PanelHost.Transitions = _animationsAllowed ? transitions : null;
        PanelHost.Opacity = opacity;
    }

    private void StopCloseTimer() => _closeTimer?.Stop();

    private void SetPanelColumn(double width, bool beside)
    {
        _settingColumn = true;
        try
        {
            var column = PanelColumn;
            column.MinWidth = beside ? MainViewModel.MinPanelWidth : 0;
            column.MaxWidth = beside ? MainViewModel.MaxPanelWidth : double.PositiveInfinity;
            column.Width = new GridLength(width);
        }
        finally
        {
            _settingColumn = false;
        }
    }

    // The splitter sets the width of the panel column while it is dragged; the view model keeps it for every tab.
    private void OnPanelColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_settingColumn && e.Property == ColumnDefinition.WidthProperty && _viewModel is { } viewModel && PanelSplitter.IsVisible)
        {
            var width = PanelColumn.Width;
            viewModel.PanelWidth = width.IsAbsolute ? width.Value : PanelColumn.ActualWidth;
        }
    }

    private void OnSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2 && e.GetCurrentPoint(PanelSplitter).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            _viewModel?.ResetPanelWidth();
        }
    }
}
