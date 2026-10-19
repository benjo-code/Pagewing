using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// The tab strip of the title bar; its <c>DataContext</c> is a <see cref="MainViewModel"/>. Keeps the children of the
/// <see cref="Controls.TabStripPanel"/> in the order of <see cref="MainViewModel.Tabs"/>: one <see cref="TabView"/>
/// for the active tab, which stays the same control while the active tab changes (as cheap as the single M3 tab with
/// one file open), and one inactive view per other tab, created when needed and dropped with its tab. A press on a tab
/// activates it, a middle click closes it, and dragging along the strip reorders the tabs (pointer captured by the
/// panel, no system drag and drop); Esc cancels the drag. Pulled away from the strip (<see cref="TabDragMath.ShouldDetach"/>),
/// the tab comes loose: the window's <see cref="MainViewModel.TabDragHost"/> starts a <see cref="TabDrag"/> whose window
/// follows the pointer, this strip keeps the pointer and reports it, and the release drops the tab (Milestone 4.1).
/// While another window's strip is the target, that strip shows <c>DropMarker</c> at the position the tab would take.
/// </summary>
public sealed partial class TabStrip : UserControl
{
    /// <summary>How far the pointer moves along the strip before a pressed tab is dragged, in device-independent pixels.</summary>
    public const double DragThreshold = 6;

    private readonly TabView _activeView = new(isActive: true);
    private readonly Dictionary<TabViewModel, TabView> _inactiveViews = [];
    private readonly List<Control> _wanted = [];
    private readonly ObservableCollection<MenuItem> _menuEntries = [];
    private MainViewModel? _viewModel;
    private bool _attached;

    // The press of the left button on a tab and the drag that may follow it.
    private TabViewModel? _pressedTab;
    private IPointer? _capturedPointer;
    private double _pressX;
    private double _pressY;
    private double _pressInTab;
    private Point _pressInTopLevel;
    private int _pressIndex;
    private bool _dragging;
    private TopLevel? _keySource;
    private TopLevel? _floatingKeySource;

    // The tab came loose: the drag that leads its window, and where the pointer was last on the screen. _detaching holds
    // while the workspace takes the tab out of this window, which must not end the press.
    private TabDrag? _drag;
    private PixelPoint _lastScreen;
    private bool _detaching;

    // The tab a middle button press started on; the release on it closes the tab.
    private TabViewModel? _middlePressedTab;

    public TabStrip()
    {
        InitializeComponent();
        TabPanel.Children.Add(_activeView);
        TabPanel.ActiveItem = _activeView;
        TabPanel.PointerPressed += OnPanelPointerPressed;
        TabPanel.PointerMoved += OnPanelPointerMoved;
        TabPanel.PointerReleased += OnPanelPointerReleased;
        TabPanel.PointerCaptureLost += OnPanelPointerCaptureLost;
        TabPanel.ContextRequested += OnPanelContextRequested;
        if (AllTabsButton.Flyout is MenuFlyout menu)
        {
            menu.ItemsSource = _menuEntries;
            menu.Opening += OnAllTabsMenuOpening;

            // The entries hold their tabs (CommandParameter): a closed menu drops them, so a closed tab is not kept.
            menu.Closed += (_, _) => _menuEntries.Clear();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Connect();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Connect();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Connect();
    }

    /// <summary>Follows the view model of the window while the strip is in a window; lets go of it otherwise.</summary>
    private void Connect()
    {
        var viewModel = _attached ? DataContext as MainViewModel : null;
        if (ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        EndPress(cancel: false);
        if (_viewModel is not null)
        {
            _viewModel.Tabs.CollectionChanged -= OnTabsChanged;
            _viewModel.Tabs.ActiveChanged -= Sync;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.Tabs.CollectionChanged += OnTabsChanged;
            _viewModel.Tabs.ActiveChanged += Sync;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        Sync();
        UpdateDropMarker();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TabDropIndex))
        {
            UpdateDropMarker();
        }
    }

    /// <summary>Shows the insertion mark where a tab that is dragged over this strip would go in; hides it otherwise.</summary>
    private void UpdateDropMarker()
    {
        if (_viewModel?.TabDropIndex is not { } index)
        {
            DropMarker.IsVisible = false;
            return;
        }

        // With only the active tab shown, every position is after it.
        var x = (TabPanel.ShowOnlyActive ? 1 : index) * TabPanel.TabWidth;
        DropMarker.Margin = new Thickness(Math.Max(0, x - 1), 4, 0, 4);
        DropMarker.IsVisible = true;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A tab that is closed while it is pressed (for example by Ctrl+W) ends the press; one that comes loose into a
        // window of its own does not.
        if (!_detaching && _drag is null && _pressedTab is not null && _viewModel?.Tabs.IndexOf(_pressedTab) < 0)
        {
            EndPress(cancel: false);
        }

        Sync();
    }

    /// <summary>Puts one view per tab into the panel, in the order of the tabs; the active tab gets the active view.</summary>
    private void Sync()
    {
        var tabs = _viewModel?.Tabs;
        var active = tabs?.Active;
        _activeView.DataContext = active;

        // The views of closed tabs go and let go of their view models.
        foreach (var (tab, view) in _inactiveViews.ToArray())
        {
            if (tabs is null || tabs.IndexOf(tab) < 0)
            {
                _inactiveViews.Remove(tab);
                view.DataContext = null;
            }
        }

        _wanted.Clear();
        if (tabs is null || tabs.Count == 0)
        {
            _wanted.Add(_activeView);
        }
        else
        {
            foreach (var tab in tabs)
            {
                _wanted.Add(ViewOf(tab));
            }
        }

        var children = TabPanel.Children;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (!_wanted.Contains(children[i]))
            {
                children.RemoveAt(i);
            }
        }

        for (var i = 0; i < _wanted.Count; i++)
        {
            var index = children.IndexOf(_wanted[i]);
            if (index < 0)
            {
                children.Insert(i, _wanted[i]);
            }
            else if (index != i)
            {
                children.Move(index, i);
            }
        }

        _wanted.Clear();

        // The dragged tab may have changed its view (it was activated or deactivated meanwhile).
        if (_dragging && !_detaching && _pressedTab is not null)
        {
            SetDragItem(ViewOf(_pressedTab));
        }

        var count = tabs?.Count ?? 0;
        AllTabsCount.Text = count.ToString(CultureInfo.InvariantCulture);
        ToolTip.SetTip(AllTabsButton, string.Format(Strings.Culture, CompositeFormat.Parse(Strings.AllTabsTooltip), count));
        AutomationProperties.SetName(AllTabsButton, string.Format(Strings.Culture, CompositeFormat.Parse(Strings.AllTabs), count));
    }

    private TabView ViewOf(TabViewModel tab)
    {
        if (ReferenceEquals(tab, _viewModel?.Tabs.Active))
        {
            return _activeView;
        }

        if (!_inactiveViews.TryGetValue(tab, out var view))
        {
            view = new TabView(isActive: false) { DataContext = tab };
            _inactiveViews.Add(tab, view);
        }

        return view;
    }

    /// <summary>
    /// Gets the position a tab dropped at <paramref name="screen"/> takes in this strip, or <see langword="null"/>
    /// when the point is not over the strip (the whole row: tabs, buttons and drag area): 0 without tabs, the number of
    /// tabs when only the active one is shown (the others have no place to point at), else the boundary between tabs
    /// the point is nearest to.
    /// </summary>
    internal int? DropIndexAt(PixelPoint screen)
    {
        if (_viewModel is null || !new Rect(Bounds.Size).Contains(this.PointToClient(screen)))
        {
            return null;
        }

        var count = _viewModel.Tabs.Count;
        if (count == 0)
        {
            return 0;
        }

        return TabPanel.ShowOnlyActive ? count : TabDragMath.InsertionIndex(TabPanel.PointToClient(screen).X, TabPanel.TabWidth, count);
    }

    /// <summary>Activates the tab that <paramref name="view"/> shows (a screen reader selected it).</summary>
    internal void Activate(TabView view)
    {
        if (_viewModel is not null && view.DataContext is TabViewModel tab && _viewModel.Tabs.IndexOf(tab) >= 0)
        {
            _viewModel.ActivateTabCommand.Execute(tab);
        }
    }

    /// <summary>Gets the tab whose view contains <paramref name="source"/>, or <see langword="null"/>.</summary>
    private TabViewModel? TabAt(object? source)
    {
        for (var visual = source as Visual; visual is not null && !ReferenceEquals(visual, TabPanel); visual = visual.GetVisualParent())
        {
            if (visual is TabView view && ReferenceEquals(view.GetVisualParent(), TabPanel))
            {
                return view.DataContext as TabViewModel;
            }
        }

        return null;
    }

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The close buttons handle their own press, so it never arrives here.
        if (_viewModel is null || TabAt(e.Source) is not { } tab)
        {
            return;
        }

        var point = e.GetCurrentPoint(TabPanel);
        switch (point.Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                EndPress(cancel: false);

                // Before the activation: the active view is one control for every active tab, and its bounds still
                // are the slot of the tab that was active.
                var pressInTab = point.Position.X - ViewOf(tab).Bounds.X;
                _viewModel.ActivateTabCommand.Execute(tab);
                _pressedTab = tab;
                _pressX = point.Position.X;
                _pressY = point.Position.Y;
                _pressInTab = pressInTab;
                _pressInTopLevel = TopLevel.GetTopLevel(this) is { } top ? e.GetPosition(top) : default;
                _pressIndex = _viewModel.Tabs.IndexOf(tab);
                _capturedPointer = e.Pointer;
                e.Pointer.Capture(TabPanel);
                e.Handled = true;
                break;
            case PointerUpdateKind.MiddleButtonPressed:
                _middlePressedTab = tab;
                e.Handled = true;
                break;
        }
    }

    private void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTab is null || _viewModel is null || !ReferenceEquals(e.Pointer, _capturedPointer))
        {
            return;
        }

        _lastScreen = ScreenOf(e);
        if (_drag is not null)
        {
            // The tab is loose: its window follows the pointer, wherever it is.
            _drag.MoveTo(_lastScreen);
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(TabPanel);
        var offset = position.X - _pressX;
        if (!_dragging)
        {
            if (Math.Max(Math.Abs(offset), Math.Abs(position.Y - _pressY)) < DragThreshold)
            {
                return;
            }

            StartDrag();
        }

        if (!TryDetach(e, position))
        {
            // Along the strip the order changes; with only the active tab shown there is no order to change.
            if (!TabPanel.ShowOnlyActive)
            {
                DragTo(offset);
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Lets the pressed tab come loose when the pointer is far enough from the strip (or outside the window): the
    /// workspace opens a window for it that follows the pointer. <see langword="false"/> when the tab keeps to the
    /// strip (not far enough, or nothing that takes the tab).
    /// </summary>
    private bool TryDetach(PointerEventArgs e, Point position)
    {
        var host = _viewModel?.TabDragHost;
        if (host is null || _pressedTab is null || TopLevel.GetTopLevel(this) is not { } top)
        {
            return false;
        }

        var inTopLevel = e.GetPosition(top);
        if (!TabDragMath.ShouldDetach(position.Y - _pressY, !new Rect(top.Bounds.Size).Contains(inTopLevel)))
        {
            return false;
        }

        // The pointer holds the tab where it held it here; in the new window the first tab starts at the strip's left edge.
        var scaling = top.RenderScaling;
        var stripLeft = TabPanel.TranslatePoint(default, top)?.X ?? 0;
        var grip = new PixelPoint(
            (int)Math.Round((stripLeft + Math.Clamp(_pressInTab, 0, TabStripLayout.MinTabWidth)) * scaling),
            (int)Math.Round(_pressInTopLevel.Y * scaling));

        TabDrag? drag;
        _detaching = true;
        try
        {
            drag = host.BeginTabDrag(_viewModel!, _pressedTab, _lastScreen, grip);
        }
        finally
        {
            _detaching = false;
        }

        if (drag is null)
        {
            return false;
        }

        if (_pressedTab is null)
        {
            // The press ended while the window was being shown (the system took the pointer): nothing leads the drag.
            drag.Complete(_lastScreen);
            return true;
        }

        _drag = drag;
        // Esc must cancel wherever the system keeps the keyboard: with this window or with the one that follows the pointer.
        _floatingKeySource = _drag.FloatingWindow;
        _floatingKeySource?.AddHandler(KeyDownEvent, OnDragKeyDown, RoutingStrategies.Tunnel);
        _dragging = false;
        SetDragItem(null);
        return true;
    }

    private PixelPoint ScreenOf(PointerEventArgs e) =>
        TopLevel.GetTopLevel(this) is { } top ? top.PointToScreen(e.GetPosition(top)) : _lastScreen;

    private void OnPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        switch (e.InitialPressMouseButton)
        {
            case MouseButton.Left when _pressedTab is not null:
                _lastScreen = ScreenOf(e);
                EndPress(cancel: false);
                e.Handled = true;
                break;
            case MouseButton.Middle:
                var tab = _middlePressedTab;
                _middlePressedTab = null;
                if (tab is not null && _viewModel is not null && _viewModel.Tabs.IndexOf(tab) >= 0
                    && ViewOf(tab).Bounds.Contains(e.GetPosition(TabPanel)))
                {
                    _viewModel.CloseTabCommand.Execute(tab);
                }

                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The context menu of a tab: "Open in new window" when the window has at least two tabs
    /// (<see cref="MainViewModel.CanMoveTabToNewWindow"/>), and "Close". Created when it is asked for, so a tab costs
    /// nothing for it before.
    /// </summary>
    private void OnPanelContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null || TabAt(e.Source) is not { } tab)
        {
            return;
        }

        var items = new List<MenuItem>(2);
        if (_viewModel.CanMoveTabToNewWindow)
        {
            items.Add(new MenuItem { Header = Strings.MoveToNewWindow, Command = _viewModel.MoveTabToNewWindowCommand, CommandParameter = tab });
        }

        items.Add(new MenuItem { Header = Strings.CloseTabMenu, Command = _viewModel.CloseTabCommand, CommandParameter = tab });
        MenuKeyboardFocus.EnsureRegistered();
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(ViewOf(tab));
        e.Handled = true;
    }

    private void OnPanelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // The system took the pointer away (for example the window lost the focus): the tab stays where it is.
        if (_pressedTab is not null && ReferenceEquals(e.Pointer, _capturedPointer))
        {
            EndPress(cancel: false);
        }
    }

    private void StartDrag()
    {
        _dragging = true;
        SetDragItem(ViewOf(_pressedTab!));
        _keySource = TopLevel.GetTopLevel(this);
        _keySource?.AddHandler(KeyDownEvent, OnDragKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Moves the dragged tab <paramref name="offset"/> from where it was pressed, within the strip.</summary>
    private void DragTo(double offset)
    {
        var tabs = _viewModel!.Tabs;
        var tab = _pressedTab!;
        var width = TabPanel.TabWidth;
        var x = Math.Clamp((_pressIndex * width) + offset, 0, (tabs.Count - 1) * width);

        // The tab takes the place whose slot its left edge is nearest to; the others move aside.
        var target = (int)Math.Round(x / width);
        if (target != tabs.IndexOf(tab))
        {
            tabs.Move(tab, target);
        }

        TabPanel.DragOffset = x - (tabs.IndexOf(tab) * width);
    }

    private void OnDragKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _pressedTab is not null)
        {
            EndPress(cancel: true);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Ends the press of a tab; <paramref name="cancel"/> puts a dragged tab back to where it was. A tab that came loose
    /// is dropped where the pointer last was, or, when cancelled, goes back into this window.
    /// </summary>
    private void EndPress(bool cancel)
    {
        var tab = _pressedTab;
        var wasDragging = _dragging;
        var drag = _drag;
        _pressedTab = null;
        _dragging = false;
        _drag = null;
        if (drag is not null)
        {
            if (cancel)
            {
                drag.Cancel();
            }
            else
            {
                drag.Complete(_lastScreen);
            }
        }

        _keySource?.RemoveHandler(KeyDownEvent, OnDragKeyDown);
        _keySource = null;
        _floatingKeySource?.RemoveHandler(KeyDownEvent, OnDragKeyDown);
        _floatingKeySource = null;
        SetDragItem(null);

        if (cancel && wasDragging && tab is not null && _viewModel is not null)
        {
            _viewModel.Tabs.Move(tab, _pressIndex);
        }

        var pointer = _capturedPointer;
        _capturedPointer = null;
        if (pointer is not null && ReferenceEquals(pointer.Captured, TabPanel))
        {
            pointer.Capture(null);
        }
    }

    private void SetDragItem(Control? view)
    {
        if (TabPanel.DragItem is { } previous && !ReferenceEquals(previous, view))
        {
            previous.ZIndex = 0;
        }

        if (view is null)
        {
            TabPanel.DragOffset = 0;
        }
        else
        {
            // Drawn above the tabs it passes.
            view.ZIndex = 1;
        }

        TabPanel.DragItem = view;
    }

    /// <summary>
    /// Fills the "All tabs" menu with the tabs, in their order; the active one in bold. The entries are created only
    /// when the menu opens. They go into the collection the menu was given at the start: the presenter of an open menu
    /// does not see a new ItemsSource set while it opens.
    /// </summary>
    private void OnAllTabsMenuOpening(object? sender, EventArgs e)
    {
        MenuKeyboardFocus.EnsureRegistered();
        _menuEntries.Clear();
        if (_viewModel is not null)
        {
            foreach (var tab in _viewModel.Tabs)
            {
                _menuEntries.Add(new MenuItem
                {
                    Header = tab.FileName ?? string.Empty,
                    Command = _viewModel.ActivateTabCommand,
                    CommandParameter = tab,
                    FontWeight = ReferenceEquals(tab, _viewModel.ActiveTab) ? FontWeight.SemiBold : FontWeight.Normal,
                });
            }
        }
    }
}
