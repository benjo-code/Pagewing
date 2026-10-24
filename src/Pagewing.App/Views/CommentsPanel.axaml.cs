using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The content of the "Comments" panel: the list of the annotations of the active tab (<see cref="TabViewModel.Comments"/>).
/// In the list Up, Down, Page Up, Page Down, Home and End move the cursor without jumping (handled here: the window binds
/// Page Up and Page Down to the pages of the document), and a click, Enter or Space shows the entry. The open filter list
/// claims Esc and the page keys like the lists of the cards (<see cref="CardLists"/>). Built when the panel opens, released
/// when it closes; the list itself belongs to the tab and outlives the panel.
/// </summary>
public sealed partial class CommentsPanel : UserControl
{
    private static readonly Key[] MovingKeys = [Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.Home, Key.End];

    private TabViewModel? _tab;
    private CommentsViewModel? _comments;

    public CommentsPanel()
    {
        InitializeComponent();
        CommentList.AddHandler(KeyDownEvent, OnListKeyDownTunnel, RoutingStrategies.Tunnel);
        CommentList.AddHandler(TappedEvent, OnEntryTapped);
        FilterBox.TemplateApplied += OnFilterTemplateApplied;

        // Key bindings of the window are matched from the focused element upwards before any key event is raised, so the
        // list claims its moving keys with bindings of its own.
        foreach (var key in MovingKeys)
        {
            CommentList.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key), Command = new RelayCommand(() => Move(key)) });
        }
    }

    // The window may hand the panel another tab just before it replaces the panel (a tab that shows another panel): the
    // list of such a tab is not created here, it is only taken from a tab that shows "Comments".
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        if (DataContext is TabViewModel tab)
        {
            _tab = tab;
            tab.PropertyChanged += OnTabPropertyChanged;
            Attach();
        }
    }

    private void Attach()
    {
        var comments = _tab is { SidePanel: SidePanelKind.Comments } tab ? tab.Comments : null;
        if (ReferenceEquals(comments, _comments) && ReferenceEquals(CommentsRoot.DataContext, comments))
        {
            // Already shown: the list keeps its scroll position.
            return;
        }

        CommentsRoot.DataContext = null;
        _comments = comments;
        CommentsRoot.DataContext = comments;
    }

    private void Detach()
    {
        CommentsRoot.DataContext = null;
        _comments = null;
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab = null;
        }
    }

    // Another document in the tab dropped its list: show the new one.
    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is (nameof(TabViewModel.Comments) or nameof(TabViewModel.SidePanel)) && _tab is { SidePanel: SidePanelKind.Comments })
        {
            Attach();
        }
    }

    private void OnFilterTemplateApplied(object? sender, TemplateAppliedEventArgs e) => CardLists.OnTemplateApplied(this, FilterBox, e);

    /// <summary>The position of the entry with the keyboard focus, or -1.</summary>
    private int FocusedIndex() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
        && focused.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container
            ? CommentList.IndexFromContainer(container)
            : -1;

    /// <summary>Moves the cursor of the list for a moving key; the document stays.</summary>
    private void Move(Key key)
    {
        var count = CommentList.ItemCount;
        if (count == 0)
        {
            return;
        }

        var current = FocusedIndex();
        if (current < 0)
        {
            current = Math.Max(CommentList.SelectedIndex, 0);
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
        CommentList.SelectedIndex = target;
        CommentList.ScrollIntoView(target);
        CommentList.ContainerFromIndex(target)?.Focus(NavigationMethod.Directional);
    }

    /// <summary>As many entries as are wholly in view, less one, so the old entry stays in sight; at least one.</summary>
    private int EntriesPerPage()
    {
        var inView = 0;
        foreach (var realized in CommentList.GetRealizedContainers())
        {
            if (realized.TranslatePoint(default, CommentList) is { } origin
                && origin.Y >= -1
                && origin.Y + realized.Bounds.Height <= CommentList.Bounds.Height + 1)
            {
                inView++;
            }
        }

        return Math.Max(1, inView - 1);
    }

    private void OnListKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        var index = FocusedIndex();
        if (index >= 0 && CommentList.Items[index] is CommentItemViewModel entry && _comments is { } comments)
        {
            e.Handled = true;
            comments.JumpCommand.Execute(entry);
        }
    }

    private void OnEntryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: CommentItemViewModel entry }
            && _comments is { } comments)
        {
            e.Handled = true;
            comments.JumpCommand.Execute(entry);
        }
    }
}
