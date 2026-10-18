using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The content of the "Search" panel: the search of the active tab (<see cref="TabViewModel.Search"/>). Enter and
/// Shift+Enter in the field go to the next and the previous match, Esc ends the search; in the list Up, Down, Page Up,
/// Page Down, Home and End move the cursor without changing the current match (handled here: the window binds Page Up
/// and Page Down to the pages of the document), and a click, Enter or Space shows the match. Built when the panel opens,
/// released when it closes; the search itself belongs to the tab and outlives the panel.
/// </summary>
public sealed partial class SearchPanel : UserControl
{
    private static readonly Key[] MovingKeys = [Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.Home, Key.End];

    private TabViewModel? _tab;
    private SearchViewModel? _search;

    public SearchPanel()
    {
        InitializeComponent();
        HitList.AddHandler(KeyDownEvent, OnHitListKeyDownTunnel, RoutingStrategies.Tunnel);
        HitList.AddHandler(TappedEvent, OnHitTapped);

        // Key bindings of the window are matched from the focused element upwards before any key event is raised, so the
        // list claims its moving keys with bindings of its own.
        foreach (var key in MovingKeys)
        {
            HitList.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key), Command = new RelayCommand(() => Move(key)) });
        }
    }

    // The window may hand the panel another tab just before it replaces the panel (a tab that shows another panel): the
    // search of such a tab is not created here, it is only taken from a tab that shows "Search".
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        if (DataContext is not TabViewModel { SidePanel: SidePanelKind.Search } tab)
        {
            return;
        }

        _tab = tab;
        _search = tab.Search;
        SearchRoot.DataContext = _search;
        tab.SearchFocusRequested += OnSearchFocusRequested;
        _search.PropertyChanged += OnSearchPropertyChanged;

        // Once the list shows the matches of this search (its binding follows the new DataContext).
        Dispatcher.UIThread.Post(ShowCurrent, DispatcherPriority.Loaded);
        if (tab.IsSearchFocusPending)
        {
            OnSearchFocusRequested();
        }
    }

    private void Detach()
    {
        SearchRoot.DataContext = null;
        if (_tab is not null)
        {
            _tab.SearchFocusRequested -= OnSearchFocusRequested;
            _tab = null;
        }

        if (_search is not null)
        {
            _search.PropertyChanged -= OnSearchPropertyChanged;
            _search = null;
        }
    }

    // After the window has shown the panel (it may still be hidden while it opens): a hidden field takes no focus.
    private void OnSearchFocusRequested() => Dispatcher.UIThread.Post(FocusField, DispatcherPriority.Loaded);

    /// <summary>Gives the focus to the search field with its content selected, if Ctrl+F asked for it.</summary>
    private void FocusField()
    {
        if (_tab is not { IsSearchFocusPending: true } tab)
        {
            return;
        }

        // A keyboard command: the focus ring shows where the keys go (design system 5.3).
        if (SearchField.Focus(NavigationMethod.Tab))
        {
            SearchField.SelectAll();
            tab.ClearSearchFocusRequest();
        }
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.Current))
        {
            ShowCurrent();
        }
    }

    /// <summary>Puts the cursor of the list on the current match and brings it into view.</summary>
    private void ShowCurrent()
    {
        if (_search is { CurrentIndex: >= 0 and var index })
        {
            HitList.SelectedIndex = index;
            HitList.ScrollIntoView(index);
        }
    }

    /// <summary>The position of the entry with the keyboard focus, or -1.</summary>
    private int FocusedIndex() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
        && focused.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
            ? HitList.IndexFromContainer(container)
            : -1;

    /// <summary>Moves the cursor of the list for a moving key; the current match and the document stay.</summary>
    private void Move(Key key)
    {
        var count = HitList.ItemCount;
        if (count == 0)
        {
            return;
        }

        var current = FocusedIndex();
        if (current < 0)
        {
            current = Math.Max(HitList.SelectedIndex, 0);
        }

        var target = key switch
        {
            Key.Up => current - 1,
            Key.Down => current + 1,
            Key.Home => 0,
            Key.End => count - 1,
            Key.PageUp => current - EntriesPerPage(),
            _ => current + EntriesPerPage(),
        };
        target = Math.Clamp(target, 0, count - 1);
        HitList.SelectedIndex = target;
        HitList.ScrollIntoView(target);
        HitList.ContainerFromIndex(target)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>As many entries as are wholly in view, less one, so the old entry stays in sight; at least one.</summary>
    private int EntriesPerPage()
    {
        var inView = 0;
        foreach (var realized in HitList.GetRealizedContainers())
        {
            if (realized.TranslatePoint(default, HitList) is { } origin
                && origin.Y >= -1
                && origin.Y + realized.Bounds.Height <= HitList.Bounds.Height + 1)
            {
                inView++;
            }
        }

        return Math.Max(1, inView - 1);
    }

    private void OnHitListKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        var index = FocusedIndex();
        if (index >= 0 && HitList.Items[index] is SearchHitViewModel hit)
        {
            e.Handled = true;
            _search?.Choose(hit);
        }
    }

    private void OnHitTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: SearchHitViewModel hit })
        {
            e.Handled = true;
            _search?.Choose(hit);
        }
    }
}
