using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;

namespace Pagewing.App.Views;

/// <summary>
/// The toolbar of the mode "Comment" over the document (design system 6 point 4 and 7.7): tools, colours, undo, redo and
/// "Done". Its <c>DataContext</c> is the active tab; the chosen tool and colour follow the tab
/// (<see cref="TabViewModel.ActiveTool"/>, <see cref="TabViewModel.ActiveColor"/>): toggle state for screen readers, a frame
/// and an accent label for the tool, a ring and a check mark for the colour, so neither is told by colour alone. Made by
/// the window when the mode is first started.
/// </summary>
public sealed partial class AnnotationToolbar : UserControl
{
    private readonly Dictionary<MarkupColor, ActionToggleButton> _swatches = [];
    private readonly Dictionary<MarkupColor, StrokeIcon> _checks = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<Control> _moreEntries = [];
    private TabViewModel? _tab;
    private bool _fitting;
    private bool _undoInMenu;
    private bool _colorsInMenu;

    /// <summary>The last level of <see cref="Fit"/>: the colours are in the menu "⋯" as well.</summary>
    private const int MaxFitLevel = 5;

    public AnnotationToolbar()
    {
        InitializeComponent();
        foreach (var color in AnnotationPalette.All)
        {
            AddSwatch(color);
        }

        // The arrows move within the colours (one stop of Tab); the window binds none of these keys.
        foreach (var key in new[] { Key.Left, Key.Right, Key.Home, Key.End })
        {
            ColorGroup.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key), Command = new RelayCommand(() => MoveInColors(key)) });
        }

        // Esc of the toolbar: a text selection first, then the mode (the keys go back to the document then, see MainWindow).
        // An Esc of the window (a waiting address, a card) comes first: this binding lies nearer the focus, so it lets the
        // key go on then.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(
                () => _tab?.EscapeAnnotate(),
                () => _tab is { IsAnnotating: true } && TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel { HasWindowEscape: true }),
        });
        SizeChanged += (_, _) => Fit();

        var more = new MenuFlyout { ItemsSource = _moreEntries };
        more.Opening += OnMoreMenuOpening;

        // The entries hold the commands of the tab: a closed menu drops them.
        more.Closed += (_, _) => _moreEntries.Clear();
        MoreButton.Flyout = more;
    }

    /// <summary>Gets whether Undo and Redo are in the menu "⋯" (for tests).</summary>
    internal bool IsUndoInMenu => _undoInMenu;

    /// <summary>Gets whether the colours are in the menu "⋯" (for tests).</summary>
    internal bool AreColorsInMenu => _colorsInMenu;

    /// <summary>Gets the button of a colour (for tests).</summary>
    internal ActionToggleButton SwatchOf(MarkupColor color) => _swatches[color];

    /// <summary>Gets the check mark of a colour (for tests).</summary>
    internal StrokeIcon CheckOf(MarkupColor color) => _checks[color];

    /// <summary>
    /// The tooltips of the tools, undo and redo name their shortcuts as the catalog has them ("Highlight (Ctrl+Shift+H)"),
    /// and screen readers get them as accelerator keys.
    /// </summary>
    internal void NameShortcuts(CommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Describe(HighlightTool, Strings.CommandHighlight, catalog.ById("annotate.highlight"));
        Describe(UnderlineTool, Strings.CommandUnderline, catalog.ById("annotate.underline"));
        Describe(StrikeOutTool, Strings.CommandStrikeOut, catalog.ById("annotate.strikeout"));
        Describe(UndoButton, Strings.CommandUndo, catalog.ById("edit.undo"));
        Describe(RedoButton, Strings.CommandRedo, catalog.ById("edit.redo"));

        static void Describe(Control control, string label, AppCommand? command)
        {
            if (command?.Gesture is not { } gesture)
            {
                ToolTip.SetTip(control, label);
                return;
            }

            var keys = GestureText.Of(gesture);
            ToolTip.SetTip(control, $"{label} ({keys})");
            AutomationProperties.SetAcceleratorKey(control, keys);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _tab = DataContext as TabViewModel;
        if (_tab is not null)
        {
            _tab.PropertyChanged += OnTabPropertyChanged;
        }

        foreach (var (color, swatch) in _swatches)
        {
            swatch.Command = _tab?.SetColorCommand;
            swatch.CommandParameter = color;
        }

        UpdateState();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.ActiveTool) or nameof(TabViewModel.ActiveColor) or nameof(TabViewModel.IsAnnotating))
        {
            UpdateState();
        }
    }

    /// <summary>Shows the chosen tool and colour of the tab.</summary>
    private void UpdateState()
    {
        var tool = _tab?.ActiveTool;
        SetChosen(HighlightTool, tool == AnnotationKind.Highlight);
        SetChosen(UnderlineTool, tool == AnnotationKind.Underline);
        SetChosen(StrikeOutTool, tool == AnnotationKind.StrikeOut);

        // Without a tool the colours show the colour of the last tool, which a click on them turns on again.
        var color = _tab?.ActiveColor;
        foreach (var (each, swatch) in _swatches)
        {
            var chosen = each == color;
            SetChosen(swatch, chosen);
            _checks[each].IsVisible = chosen;
        }
    }

    private static void SetChosen(ActionToggleButton button, bool chosen)
    {
        button.IsChecked = chosen;
        button.Classes.Set("active", chosen);
    }

    private void AddSwatch(MarkupColor color)
    {
        var rgb = AnnotationPalette.ColorOf(color);
        var name = Strings.ColorName(color);
        var check = new StrokeIcon { Name = "Check" + color, IsVisible = false };
        check.Classes.Add("swatchCheck");
        var fill = new Border
        {
            Name = "Fill" + color,

            // The colour of the markup is content of the PDF, not a token.
            Background = new ImmutableSolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)),
            Child = check,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        fill.Classes.Add("swatchFill");
        var swatch = new ActionToggleButton { Name = "Swatch" + color, Content = fill };
        swatch.Classes.Add("swatch");
        ToolTip.SetTip(swatch, name);
        AutomationProperties.SetName(swatch, name);
        ColorGroup.Children.Add(swatch);
        _swatches[color] = swatch;
        _checks[color] = check;
    }

    /// <summary>Moves the keyboard focus within the colours (Left, Right with wrap-around, Home, End); Enter or Space chooses.</summary>
    private void MoveInColors(Key key)
    {
        var buttons = ColorGroup.Children.OfType<ActionToggleButton>().ToArray();
        var current = Array.FindIndex(buttons, button => button.IsFocused);
        if (current < 0)
        {
            current = Array.FindIndex(buttons, button => button.IsChecked == true);
        }

        var count = buttons.Length;
        var target = key switch
        {
            Key.Left => (current - 1 + count) % count,
            Key.Right => (current + 1) % count,
            Key.Home => 0,
            _ => count - 1,
        };
        buttons[target].Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Too narrow for everything (design system 6, narrow window), step by step until it fits: first the hint "Esc ends"
    /// goes, then the labels of the tools (icons with tooltips stay), then the text of the mode name (its icon stays), then
    /// Undo and Redo and after them the colours move into the menu "⋯". The tools and "Done" always stay in the row.
    /// </summary>
    private void Fit()
    {
        if (_fitting)
        {
            return;
        }

        _fitting = true;
        try
        {
            var width = ToolbarContent.Bounds.Width;
            if (!(width > 0))
            {
                return;
            }

            for (var level = 0; level <= MaxFitLevel; level++)
            {
                ApplyFitLevel(level);
                if (level == MaxFitLevel || Needs() <= width)
                {
                    break;
                }
            }
        }
        finally
        {
            // The measures above ran without a limit: the next layout pass measures the content for its real width again.
            ToolbarContent.InvalidateMeasure();
            ToolbarFrame.InvalidateMeasure();
            _fitting = false;
        }
    }

    /// <summary>Shows what the toolbar has at a level of <see cref="Fit"/> (0: everything).</summary>
    private void ApplyFitLevel(int level)
    {
        EscHint.IsVisible = level < 1;
        ShowLabels(level < 2);
        ModeName.IsVisible = level < 3;
        _undoInMenu = level >= 4;
        _colorsInMenu = level >= 5;
        UndoButton.IsVisible = !_undoInMenu;
        RedoButton.IsVisible = !_undoInMenu;
        UndoSeparator.IsVisible = !_colorsInMenu;
        ColorGroup.IsVisible = !_colorsInMenu;
        ColorSeparator.IsVisible = true;
        MoreButton.IsVisible = _undoInMenu;
    }

    private double Needs()
    {
        ToolbarContent.Measure(Size.Infinity);
        return ToolbarContent.DesiredSize.Width - ToolbarContent.Margin.Left - ToolbarContent.Margin.Right;
    }

    /// <summary>
    /// Fills the menu "⋯" when it opens with what is not in the row: Undo and Redo (with their keys), then the colours (each
    /// with its swatch and name, the chosen one with a check mark).
    /// </summary>
    private void OnMoreMenuOpening(object? sender, EventArgs e)
    {
        MenuKeyboardFocus.EnsureRegistered();
        _moreEntries.Clear();
        if (_tab is not { } tab)
        {
            return;
        }

        if (_undoInMenu)
        {
            _moreEntries.Add(MenuEntry("MoreUndo", Strings.CommandUndo, tab.UndoCommand, null, new KeyGesture(Key.Z, KeyModifiers.Control)));
            _moreEntries.Add(MenuEntry("MoreRedo", Strings.CommandRedo, tab.RedoCommand, null, new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)));
        }

        if (_colorsInMenu)
        {
            _moreEntries.Add(new Separator());
            foreach (var color in AnnotationPalette.All)
            {
                var entry = MenuEntry("More" + color, Strings.ColorName(color), tab.SetColorCommand, color, null);
                entry.Icon = CommentEditor.Swatch(color, tab.ActiveColor == color);
                _moreEntries.Add(entry);
            }
        }
    }

    private static MenuItem MenuEntry(string name, string header, System.Windows.Input.ICommand command, object? parameter, KeyGesture? gesture)
    {
        var entry = new MenuItem { Name = name, Header = header, Command = command, CommandParameter = parameter, InputGesture = gesture };
        AutomationProperties.SetName(entry, header);
        return entry;
    }

    private void ShowLabels(bool show)
    {
        HighlightLabel.IsVisible = show;
        UnderlineLabel.IsVisible = show;
        StrikeOutLabel.IsVisible = show;
    }
}
