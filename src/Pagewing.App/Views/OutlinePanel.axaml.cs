using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The content of the "Contents" panel: the outline of the active tab (<see cref="TabViewModel.Outline"/>) as a tree.
/// A click on an entry with a target, or Enter or Space on it, shows its page (<see cref="TabViewModel.ShowPageFromPanel"/>);
/// Up, Down, Page Up, Page Down, Home and End move the focus over the shown entries (handled here, also beyond the
/// realized containers), Right and Left open and close an entry. Built when the panel opens, released when it
/// closes; the tab reads its outline when "Contents" opens (<see cref="TabViewModel.EnsureOutlineAsync"/>).
/// </summary>
public sealed partial class OutlinePanel : UserControl
{
    /// <summary>
    /// Whether the entry of a <see cref="TreeViewItem"/> has a target (bound to <see cref="OutlineNodeViewModel.CanJump"/>);
    /// gives the item the class <c>jump</c>, which its styles select (hover only on entries that do something).
    /// </summary>
    public static readonly AttachedProperty<bool> IsJumpTargetProperty =
        AvaloniaProperty.RegisterAttached<OutlinePanel, TreeViewItem, bool>("IsJumpTarget");

    static OutlinePanel() =>
        IsJumpTargetProperty.Changed.AddClassHandler<TreeViewItem>((item, e) => item.Classes.Set("jump", e.GetNewValue<bool>()));

    public OutlinePanel()
    {
        InitializeComponent();
        OutlineTree.AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        OutlineTree.AddHandler(TappedEvent, OnTapped);

        // Key bindings of the window (Page Up/Down turn the document's pages) are matched from the focused element
        // upwards before any key event is raised, so the tree claims these two keys with bindings of its own.
        OutlineTree.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(() => Move(Key.PageDown)) });
        OutlineTree.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(() => Move(Key.PageUp)) });
    }

    public static bool GetIsJumpTarget(TreeViewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.GetValue(IsJumpTargetProperty);
    }

    public static void SetIsJumpTarget(TreeViewItem item, bool value)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.SetValue(IsJumpTargetProperty, value);
    }

    /// <summary>The entry an input event belongs to, unless it came from the chevron (which only opens and closes).</summary>
    private static OutlineNodeViewModel? EntryOf(object? source) =>
        source is Visual visual
        && visual.FindAncestorOfType<ToggleButton>(includeSelf: true) is null
        && visual.FindAncestorOfType<TreeViewItem>(includeSelf: true) is { DataContext: OutlineNodeViewModel node }
            ? node
            : null;

    // The panel handles Enter and Space (jump) and the moving keys itself, before the tree gets them:
    // - Enter on an entry without a target opens or closes it, as the tree would; the tree would pass it on to the
    //   parent entry when there is nothing to open, which then closes and takes the focus away.
    // - Up, Down, Page Up, Page Down, Home and End move over the entries the tree shows (open entries included) and
    //   bring the new one into view first: the tree only knows the realized containers, so with virtualized levels it
    //   would have no Home, End or Page keys and could not rely on the next entry being realized (Docs/DECISIONS.md,
    //   "Inhaltsverzeichnis: Tastatur selbst geführt (M4)"). Left and Right stay with the tree.
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var node = EntryOf(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement());
        switch (e.Key)
        {
            case Key.Enter or Key.Space when node is { CanJump: true }:
                e.Handled = true;
                ShowPage(node);
                break;
            case Key.Enter when node is not null:
                e.Handled = true;
                if (node.HasChildren)
                {
                    node.IsExpanded = !node.IsExpanded;
                }

                break;
            case Key.Down or Key.Up or Key.PageDown or Key.PageUp or Key.Home or Key.End:
                e.Handled = true;
                Move(e.Key);
                break;
        }
    }

    /// <summary>Moves the keyboard focus for a moving key (Up, Down, Page Up, Page Down, Home, End).</summary>
    private void Move(Key key)
    {
        if (DataContext is not TabViewModel { Outline: { Count: > 0 } roots })
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var node = EntryOf(focused);
        if (Target(key, node, roots, focused as Control) is { } target && !ReferenceEquals(target, node))
        {
            FocusEntry(target);
        }
    }

    /// <summary>The entry a moving key goes to from <paramref name="node"/> (the first one when no entry has the focus).</summary>
    private OutlineNodeViewModel? Target(Key key, OutlineNodeViewModel? node, IReadOnlyList<OutlineNodeViewModel> roots, Control? focused)
    {
        if (node is null || key == Key.Home)
        {
            return key == Key.End ? OutlineNodeViewModel.Last(roots) : roots[0];
        }

        switch (key)
        {
            case Key.End:
                return OutlineNodeViewModel.Last(roots);
            case Key.Down:
                return node.Next(roots) ?? node;
            case Key.Up:
                return node.Previous(roots) ?? node;
        }

        // A page: as many entries as fit into the view, less one, so the old entry stays in sight.
        var rowHeight = focused is TreeViewItem item && item.MinHeight > 0 ? item.MinHeight : 28;
        var viewport = OutlineTree.Bounds.Height;
        var steps = Math.Max(1, (int)(viewport / rowHeight) - 1);
        var target = node;
        for (var i = 0; i < steps; i++)
        {
            var next = key == Key.PageDown ? target.Next(roots) : target.Previous(roots);
            if (next is null)
            {
                break;
            }

            target = next;
        }

        return target;
    }

    /// <summary>
    /// Gives the keyboard focus to the entry of <paramref name="node"/>: realizes the container of every level on its
    /// way down (each brought into view by its virtualizing panel), then focuses it, which selects it.
    /// </summary>
    private void FocusEntry(OutlineNodeViewModel node)
    {
        var path = new List<OutlineNodeViewModel>();
        for (var step = node; step is not null; step = step.Parent)
        {
            path.Add(step);
        }

        ItemsControl level = OutlineTree;
        TreeViewItem? container = null;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var index = path[i].Index;
            if (level.ContainerFromIndex(index) is null)
            {
                level.ScrollIntoView(index);
            }

            if (level.ContainerFromIndex(index) is not TreeViewItem realized)
            {
                return;
            }

            container = realized;
            level = realized;
        }

        if (container is not null)
        {
            container.BringIntoView();
            container.Focus(NavigationMethod.Directional);
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (EntryOf(e.Source) is { CanJump: true } node)
        {
            e.Handled = true;
            ShowPage(node);
        }
    }

    private void ShowPage(OutlineNodeViewModel node)
    {
        if (node.PageIndex is { } index && DataContext is TabViewModel tab)
        {
            tab.ShowPageFromPanel(index + 1);
        }
    }
}
