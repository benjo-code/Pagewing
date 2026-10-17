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
/// the arrow keys move the focus, Right and Left open and close an entry. Built when the panel opens, released when it
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
        OutlineTree.AddHandler(GotFocusEvent, OnGotFocus);
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

    // Enter and Space show the page of the focused entry, before the tree gets the keys. Enter on an entry without a
    // target opens or closes it, as the tree would; it is handled here because the tree would pass it on to the parent
    // entry when there is nothing to open, which then closes and takes the focus away.
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Enter or Key.Space)
            || EntryOf(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()) is not { } node)
        {
            return;
        }

        if (node.CanJump)
        {
            e.Handled = true;
            ShowPage(node);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (node.Children.Count > 0)
            {
                node.IsExpanded = !node.IsExpanded;
            }
        }
    }

    // The tree moves with the arrow keys only from a selected entry; an entry reached by Tab becomes it, so the first
    // arrow key already moves (instead of selecting the first entry).
    private void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (OutlineTree.SelectedItem is null && e.Source is TreeViewItem { DataContext: OutlineNodeViewModel node })
        {
            OutlineTree.SelectedItem = node;
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
