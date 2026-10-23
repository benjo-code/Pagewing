using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card with the keyboard shortcuts (<see cref="OverlayKind.Help"/>): every listed command of the catalog that has
/// a shortcut, grouped, with a filter field. The window shows it with <see cref="Show"/> and hides it with
/// <see cref="Hide"/>; its close button and Esc ask the window's view model to hide the overlay.
/// </summary>
public sealed partial class HelpCard : UserControl
{
    private readonly List<(Control Row, string Title, string Gesture)> _rows = [];
    private readonly List<(Control Group, int First, int Count)> _groups = [];

    public HelpCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus
        // on the card, Esc closes it (and does not reach the link card or the search) and the page keys scroll the list
        // instead of turning the pages of the document behind it.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(() => (DataContext as MainViewModel)?.HideOverlay()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(HelpScroller.PageUp) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(HelpScroller.PageDown) });
        HelpFilter.TextChanged += (_, _) => ApplyFilter();
    }

    /// <summary>
    /// Shows the shortcuts of <paramref name="catalog"/> in the language of the interface with an empty filter, and puts
    /// the focus into the filter field.
    /// </summary>
    internal void Show(CommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Build(catalog);
        HelpFilter.Text = string.Empty;
        ApplyFilter();
        HelpScroller.Offset = default;
        IsVisible = true;

        // After the layout of this change, when the field can take the focus.
        Dispatcher.UIThread.Post(FocusFilter, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the card.</summary>
    internal void Hide() => IsVisible = false;

    /// <summary>Puts the keys into the filter field while the card is shown.</summary>
    internal void FocusFilter()
    {
        if (IsVisible && HelpFilter.IsEffectivelyVisible)
        {
            HelpFilter.Focus();
        }
    }

    private void Build(CommandCatalog catalog)
    {
        HelpGroups.Children.Clear();
        _rows.Clear();
        _groups.Clear();
        foreach (var group in CommandCatalog.Groups)
        {
            var first = _rows.Count;
            StackPanel? panel = null;
            foreach (var command in catalog.All)
            {
                if (command.Group != group || !command.Listed || command.Gesture is not { } gesture)
                {
                    continue;
                }

                if (panel is null)
                {
                    panel = new StackPanel { Tag = group };
                    panel.Classes.Add("helpGroup");
                    var title = new TextBlock { Text = CommandCatalog.GroupTitle(group) };
                    title.Classes.AddRange(["strong", "helpGroupTitle"]);
                    AutomationProperties.SetHeadingLevel(title, 2);
                    panel.Children.Add(title);
                    HelpGroups.Children.Add(panel);
                }

                var row = Row(command.Title(), GestureText.Of(gesture), command.Id);
                panel.Children.Add(row.Row);
                _rows.Add(row);
            }

            if (panel is not null)
            {
                _groups.Add((panel, first, _rows.Count - first));
            }
        }
    }

    private static (Control Row, string Title, string Gesture) Row(string title, string gesture, string id)
    {
        var name = new TextBlock { Text = title };
        name.Classes.Add("body");
        var key = new TextBlock { Text = gesture };
        key.Classes.Add("mono");
        var kbd = new Border { Child = key };
        kbd.Classes.Add("kbd");
        Grid.SetColumn(kbd, 1);
        var row = new Grid { Tag = id, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { name, kbd } };
        row.Classes.Add("helpRow");

        // One element for screen readers, read once: what the command does and its keys. A Grid has no element of its
        // own (its peer is not in the control or content view) until it is put into the content view; its texts stay out.
        AutomationProperties.SetName(row, $"{title}, {gesture}");
        AutomationProperties.SetAccessibilityView(row, AccessibilityView.Content);
        AutomationProperties.SetControlTypeOverride(row, AutomationControlType.Text);
        AutomationProperties.SetAccessibilityView(name, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(key, AccessibilityView.Raw);
        return (row, title, gesture);
    }

    /// <summary>Shows the rows whose title or shortcut contains the filter text (case ignored), and the groups that keep one.</summary>
    private void ApplyFilter()
    {
        var query = HelpFilter.Text?.Trim() ?? string.Empty;
        var compare = Strings.Culture.CompareInfo;
        var any = false;
        foreach (var (group, first, count) in _groups)
        {
            var shown = false;
            for (var i = first; i < first + count; i++)
            {
                var (row, title, gesture) = _rows[i];
                var match = query.Length == 0
                    || compare.IndexOf(title, query, System.Globalization.CompareOptions.IgnoreCase) >= 0
                    || compare.IndexOf(gesture, query, System.Globalization.CompareOptions.IgnoreCase) >= 0;
                row.IsVisible = match;
                shown |= match;
            }

            group.IsVisible = shown;
            any |= shown;
        }

        HelpEmpty.IsVisible = !any;
        HelpScroller.IsVisible = any;
    }
}
