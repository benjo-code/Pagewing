using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// "Recently opened" on the start screen (its DataContext is the <see cref="MainViewModel"/>): a click on an entry opens
/// it, Delete removes the focused entry (the keys go to the entry that takes its place, or to <see cref="FocusFallback"/>
/// when none is left), Up and Down move the keys between the entries. Enter and Space are the button's own.
/// </summary>
public sealed partial class RecentFilesSection : UserControl
{
    public RecentFilesSection()
    {
        InitializeComponent();
        RecentList.AddHandler(Button.ClickEvent, OnClick);
        RecentList.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Gets or sets where the keys go when the last entry was removed with Delete ("Choose file").</summary>
    internal Action? FocusFallback { get; set; }

    /// <summary>Gets the entry buttons in their order (tests).</summary>
    internal IReadOnlyList<Button> EntryButtons =>
        [.. RecentList.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("recentEntry"))];

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { DataContext: RecentEntry entry } button || ViewModel is not { } viewModel)
        {
            return;
        }

        e.Handled = true;
        if (button.Classes.Contains("recentRemove"))
        {
            Remove(viewModel, entry, keepKeys: button.IsKeyboardFocusWithin || EntryHasFocus());
        }
        else
        {
            viewModel.OpenRecentCommand.Execute(entry);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || ViewModel is not { } viewModel
            || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Button { DataContext: RecentEntry entry } focused
            || !focused.Classes.Contains("recentEntry"))
        {
            return;
        }

        var index = IndexOf(viewModel, entry);
        switch (e.Key)
        {
            case Key.Delete:
                e.Handled = true;
                Remove(viewModel, entry, keepKeys: true);
                break;
            case Key.Up when index > 0:
                e.Handled = true;
                FocusEntry(index - 1);
                break;
            case Key.Down when index >= 0 && index < viewModel.StartRecentFiles.Count - 1:
                e.Handled = true;
                FocusEntry(index + 1);
                break;
        }
    }

    private static int IndexOf(MainViewModel viewModel, RecentEntry entry)
    {
        var entries = viewModel.StartRecentFiles;
        for (var i = 0; i < entries.Count; i++)
        {
            if (Equals(entries[i], entry))
            {
                return i;
            }
        }

        return -1;
    }

    private bool EntryHasFocus() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused && RecentList.IsVisualAncestorOf(focused);

    /// <summary>
    /// Takes <paramref name="entry"/> out of the history; with <paramref name="keepKeys"/> the keys go to the entry
    /// now at its place (the one before it, at the end), or to <see cref="FocusFallback"/> when the list is empty.
    /// </summary>
    private void Remove(MainViewModel viewModel, RecentEntry entry, bool keepKeys)
    {
        var index = IndexOf(viewModel, entry);
        viewModel.RemoveRecentCommand.Execute(entry);
        if (!keepKeys)
        {
            return;
        }

        // The list makes its entries anew: they can take the keys once they are laid out.
        Dispatcher.UIThread.Post(
            () =>
            {
                var count = viewModel.StartRecentFiles.Count;
                if (count == 0)
                {
                    FocusFallback?.Invoke();
                }
                else
                {
                    FocusEntry(Math.Clamp(index, 0, count - 1));
                }
            },
            DispatcherPriority.Loaded);
    }

    private void FocusEntry(int index)
    {
        if (RecentList.ContainerFromIndex(index) is { } container
            && container.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("recentEntry")) is { } button)
        {
            button.Focus(NavigationMethod.Directional);
        }
    }
}
