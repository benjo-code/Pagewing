using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The tab strip of the title bar; its <c>DataContext</c> is a <see cref="MainViewModel"/>. Keeps the children of the
/// <see cref="Controls.TabStripPanel"/> in the order of <see cref="MainViewModel.Tabs"/>: one <see cref="TabView"/>
/// for the active tab, which stays the same control while the active tab changes (as cheap as the single M3 tab with
/// one file open), and one inactive view per other tab, created when needed and dropped with its tab. A press on a tab
/// activates it, a middle click closes it, and dragging along the strip reorders the tabs (pointer captured by the
/// panel, no system drag and drop); Esc cancels the drag. Dragging out of the strip does nothing in Milestone 4.
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
    private int _pressIndex;
    private bool _dragging;
    private TopLevel? _keySource;

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
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.Tabs.CollectionChanged += OnTabsChanged;
            _viewModel.Tabs.ActiveChanged += Sync;
        }

        Sync();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A tab that is closed while it is pressed (for example by Ctrl+W) ends the press.
        if (_pressedTab is not null && _viewModel?.Tabs.IndexOf(_pressedTab) < 0)
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
        if (_dragging && _pressedTab is not null)
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
                _viewModel.ActivateTabCommand.Execute(tab);
                _pressedTab = tab;
                _pressX = point.Position.X;
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

        // Only the movement along the strip counts; up and down changes nothing.
        var offset = e.GetPosition(TabPanel).X - _pressX;
        if (!_dragging)
        {
            if (Math.Abs(offset) < DragThreshold || TabPanel.ShowOnlyActive)
            {
                return;
            }

            StartDrag();
        }

        DragTo(offset);
        e.Handled = true;
    }

    private void OnPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        switch (e.InitialPressMouseButton)
        {
            case MouseButton.Left when _pressedTab is not null:
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
    /// The context menu of a tab: "Close". Created when it is asked for, so a tab costs nothing for it before.
    /// </summary>
    private void OnPanelContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null || TabAt(e.Source) is not { } tab)
        {
            return;
        }

        var menu = new ContextMenu
        {
            ItemsSource = new[]
            {
                new MenuItem { Header = Strings.CloseTabMenu, Command = _viewModel.CloseTabCommand, CommandParameter = tab },
            },
        };
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

    /// <summary>Ends the press of a tab; <paramref name="cancel"/> puts a dragged tab back to where it was.</summary>
    private void EndPress(bool cancel)
    {
        var tab = _pressedTab;
        var wasDragging = _dragging;
        _pressedTab = null;
        _dragging = false;

        _keySource?.RemoveHandler(KeyDownEvent, OnDragKeyDown);
        _keySource = null;
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
