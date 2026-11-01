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
/// The content of the panel "Redact": the marks of the plan of the active tab by page, and "find and redact"
/// (<see cref="TabViewModel.RedactionPanel"/>). In the field Enter starts the search. In both lists Up, Down, Page Up, Page Down, Home
/// and End move the cursor without leaving the list or moving the document (handled here: the window binds Page Up and Page Down to the
/// pages of the document; the cursor of the list of marks skips the headers of the pages), a click, Enter or Space shows the entry, and
/// in the list of hits Space checks and unchecks; Delete in the list of marks removes the selected marks. Tab leaves each list (it never
/// holds the focus), and Esc is the Esc of the mode (drop a drag, the selected marks, the text selection, then the mode). Built when the
/// panel opens, released when it closes; the model belongs to the tab and outlives the panel until the mode ends.
/// </summary>
public sealed partial class RedactionPanel : UserControl
{
    private static readonly Key[] MovingKeys = [Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.Home, Key.End];

    // Once a search ran, the result takes the height that is left, at least this much, and the list of marks becomes a strip below it
    // of at most this height (all in DIP); the list of hits inside the result is as high as this at most, and the result scrolls.
    private const double HitsMinHeight = 160;
    private const double MarksStripMaxHeight = 180;
    private const double HitListMaxHeight = 280;

    private TabViewModel? _tab;
    private RedactionViewModel? _panel;

    public RedactionPanel()
    {
        InitializeComponent();
        HitList.MaxHeight = HitListMaxHeight;
        HitList.AddHandler(KeyDownEvent, OnHitListKeyDownTunnel, RoutingStrategies.Tunnel);
        HitList.AddHandler(TappedEvent, OnHitTapped);
        MarkList.AddHandler(KeyDownEvent, OnMarkListKeyDownTunnel, RoutingStrategies.Tunnel);
        MarkList.AddHandler(TappedEvent, OnMarkTapped);

        // Key bindings of the window are matched from the focused element upwards before any key event is raised, so the lists
        // claim their moving keys (and the list of marks Delete) with bindings of their own.
        foreach (var key in MovingKeys)
        {
            HitList.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key), Command = new RelayCommand(() => Move(HitList, key)) });
            MarkList.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key), Command = new RelayCommand(() => Move(MarkList, key)) });
        }

        MarkList.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Delete),
            Command = new RelayCommand(
                () => _panel?.DeleteMarksCommand.Execute(null),
                () => _panel?.DeleteMarksCommand.CanExecute(null) == true),
        });

        // Esc anywhere in the panel is the Esc of the mode, unless the window has an Esc of its own to take first (a waiting address,
        // a card): this binding lies nearer the focus, so it lets the key go on then.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(
                () => _tab?.EscapeRedact(),
                () => _tab is { IsRedacting: true } && TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel { HasWindowEscape: true }),
        });
    }

    // The window may hand the panel another tab just before it replaces the panel (a tab that shows another panel): the model of such a
    // tab is not created here, it is only taken from a tab that shows "Redact" in the mode.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        if (DataContext is not TabViewModel { SidePanel: SidePanelKind.Redact, IsRedacting: true } tab)
        {
            return;
        }

        _tab = tab;
        _panel = tab.RedactionPanel;
        RedactRoot.DataContext = _panel;
        tab.RedactionSearchFocusRequested += OnSearchFocusRequested;
        _panel.PropertyChanged += OnPanelPropertyChanged;
        UpdateHitsRow();
        if (tab.IsRedactionSearchFocusPending)
        {
            OnSearchFocusRequested();
        }
    }

    private void Detach()
    {
        RedactRoot.DataContext = null;
        if (_tab is not null)
        {
            _tab.RedactionSearchFocusRequested -= OnSearchFocusRequested;
            _tab = null;
        }

        if (_panel is not null)
        {
            _panel.PropertyChanged -= OnPanelPropertyChanged;
            _panel = null;
        }
    }

    // After the window has shown the panel (it may still be hidden while it opens): a hidden field takes no focus.
    private void OnSearchFocusRequested() => Dispatcher.UIThread.Post(FocusField, DispatcherPriority.Loaded);

    /// <summary>Gives the focus to the search field with its content selected, if the toolbar asked for it.</summary>
    private void FocusField()
    {
        if (_tab is not { IsRedactionSearchFocusPending: true } tab)
        {
            return;
        }

        // A keyboard command: the focus ring shows where the keys go (design system 5.3).
        if (QueryField.Focus(NavigationMethod.Tab))
        {
            QueryField.SelectAll();
            tab.ClearRedactionSearchFocusRequest();
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RedactionViewModel.HasResults))
        {
            UpdateHitsRow();
        }
    }

    // The result of a search takes the height that is left once a search ran, and the list of marks is a strip below it; before a
    // search the list of marks has the whole height.
    private void UpdateHitsRow()
    {
        var shown = _panel is { HasResults: true };
        var hits = RedactRoot.RowDefinitions[1];
        hits.Height = shown ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        hits.MinHeight = shown ? HitsMinHeight : 0;
        RedactRoot.RowDefinitions[3].Height = shown ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        MarkList.MaxHeight = shown ? MarksStripMaxHeight : double.PositiveInfinity;
    }

    /// <summary>The position of the entry with the keyboard focus, or -1.</summary>
    private static int FocusedIndex(ListBox list) =>
        TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is Visual focused
        && focused.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
            ? list.IndexFromContainer(container)
            : -1;

    /// <summary>Moves the cursor of a list for a moving key; the document stays. Headers of pages are skipped.</summary>
    private static void Move(ListBox list, Key key)
    {
        var count = list.ItemCount;
        if (count == 0)
        {
            return;
        }

        var current = FocusedIndex(list);
        if (current < 0)
        {
            current = Math.Max(list.SelectedIndex, 0);
        }

        var target = key switch
        {
            Key.Up => current - 1,
            Key.Down => current + 1,
            Key.Home => 0,
            Key.End => count - 1,
            Key.PageUp => current - EntriesPerPage(list),
            _ => current + EntriesPerPage(list),
        };
        target = Math.Clamp(target, 0, count - 1);

        // A header cannot hold the cursor: on to the next mark in the direction of the key (back for Up, Page Up and End).
        var step = key is Key.Up or Key.PageUp or Key.End ? -1 : 1;
        while (target >= 0 && target < count && list.Items[target] is RedactionMarkRowViewModel { IsHeader: true })
        {
            target += step;
        }

        if (target < 0 || target >= count)
        {
            // Nothing but headers in that direction: the cursor stays.
            return;
        }

        list.SelectedIndex = target;
        list.ScrollIntoView(target);
        list.ContainerFromIndex(target)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>As many entries as are wholly in view, less one, so the old entry stays in sight; at least one.</summary>
    private static int EntriesPerPage(ListBox list)
    {
        var inView = 0;
        foreach (var realized in list.GetRealizedContainers())
        {
            if (realized.TranslatePoint(default, list) is { } origin
                && origin.Y >= -1
                && origin.Y + realized.Bounds.Height <= list.Bounds.Height + 1)
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

        var index = FocusedIndex(HitList);
        if (index < 0 || HitList.Items[index] is not RedactionHitViewModel hit)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Space)
        {
            // Space is the check, as on a list of check boxes; the entry is shown with Enter or a click.
            hit.IsChecked = !hit.IsChecked;
        }
        else
        {
            _panel?.ChooseHit(hit);
        }
    }

    private void OnHitTapped(object? sender, TappedEventArgs e)
    {
        // The check box takes its own click; the rest of the row shows the hit.
        if (e.Source is Visual source
            && source.FindAncestorOfType<CheckBox>(includeSelf: true) is null
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: RedactionHitViewModel hit })
        {
            e.Handled = true;
            _panel?.ChooseHit(hit);
        }
    }

    private void OnMarkListKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        var index = FocusedIndex(MarkList);
        if (index >= 0 && MarkList.Items[index] is RedactionMarkRowViewModel row && _panel is { } panel)
        {
            e.Handled = true;
            panel.ChooseMark(row);
        }
    }

    private void OnMarkTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: RedactionMarkRowViewModel row }
            && _panel is { } panel)
        {
            e.Handled = true;
            panel.ChooseMark(row);
        }
    }
}
