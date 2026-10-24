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
    private TabViewModel? _tab;
    private bool _fitting;

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
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(() => _tab?.EscapeAnnotate()),
        });
        SizeChanged += (_, _) => Fit();
    }

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
    /// Too narrow for everything (design system 6, narrow window): first the hint "Esc ends" goes, then the labels of the
    /// tools (icons with tooltips stay). Mode name and "Done" always stay.
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

            ShowLabels(true);
            EscHint.IsVisible = true;
            if (Needs() <= width)
            {
                return;
            }

            EscHint.IsVisible = false;
            if (Needs() <= width)
            {
                return;
            }

            ShowLabels(false);
        }
        finally
        {
            // The measures above ran without a limit: the next layout pass measures the content for its real width again.
            ToolbarContent.InvalidateMeasure();
            ToolbarFrame.InvalidateMeasure();
            _fitting = false;
        }
    }

    private double Needs()
    {
        ToolbarContent.Measure(Size.Infinity);
        return ToolbarContent.DesiredSize.Width - ToolbarContent.Margin.Left - ToolbarContent.Margin.Right;
    }

    private void ShowLabels(bool show)
    {
        HighlightLabel.IsVisible = show;
        UnderlineLabel.IsVisible = show;
        StrikeOutLabel.IsVisible = show;
    }
}
