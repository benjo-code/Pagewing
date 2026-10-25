using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;

namespace Pagewing.App.Views;

/// <summary>
/// The toolbar of the mode "Comment" over the document (design system 6 point 4 and 7.7): tools, their colours (and widths),
/// undo, redo and "Done". Its <c>DataContext</c> is the active tab; the chosen tool and style follow the tab
/// (<see cref="TabViewModel.ActiveTool"/>, <see cref="TabViewModel.IsSelectTool"/>, <see cref="TabViewModel.ActiveColor"/>,
/// <see cref="TabViewModel.ActiveStyle"/>, the selection of the select tool): toggle state for screen readers, a frame and an
/// accent label for the tool, a ring and a check mark for the colour, a ring for the width, so none is told by colour alone.
/// The colours and widths shown depend on the tool: a text markup tool has the markup colours, a drawing tool its colours and
/// widths, the select tool those of the selection; without either nothing is shown there. Made by the window when the mode
/// is first started.
/// </summary>
public sealed partial class AnnotationToolbar : UserControl
{
    /// <summary>The shapes that move into the menu "⋯" in the narrowest toolbar, in the order of the row.</summary>
    private static readonly AnnotationKind[] Shapes = [AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow];

    private static readonly StrokeWidth[] Widths = [StrokeWidth.Thin, StrokeWidth.Medium, StrokeWidth.Thick];

    /// <summary>The last level of <see cref="Fit"/>: the shapes are in the menu "⋯" as well and the icon of the mode is gone.</summary>
    private const int MaxFitLevel = 7;

    private readonly Dictionary<MarkupColor, ActionToggleButton> _swatches = [];
    private readonly Dictionary<MarkupColor, StrokeIcon> _checks = [];
    private readonly Dictionary<DrawColor, ActionToggleButton> _drawSwatches = [];
    private readonly Dictionary<DrawColor, StrokeIcon> _drawChecks = [];
    private readonly Dictionary<StrokeWidth, ActionToggleButton> _widths = [];
    private readonly Dictionary<AnnotationKind, KeyGesture> _gestures = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<Control> _moreEntries = [];
    private readonly RelayCommand<DrawColor> _chooseDrawColor;
    private readonly RelayCommand<StrokeWidth> _chooseWidth;
    private TabViewModel? _tab;
    private StyleMode _style;
    private bool _fitting;
    private bool _undoInMenu;
    private bool _colorsInMenu;
    private bool _shapesInMenu;

    public AnnotationToolbar()
    {
        InitializeComponent();
        _chooseDrawColor = new RelayCommand<DrawColor>(ChooseDrawColor);
        _chooseWidth = new RelayCommand<StrokeWidth>(ChooseWidth);
        foreach (var color in AnnotationPalette.All)
        {
            AddSwatch(color);
        }

        foreach (var color in DrawPalette.All)
        {
            AddDrawSwatch(color);
        }

        foreach (var width in Widths)
        {
            AddWidth(width);
        }

        // The arrows move within the tools, the colours and the widths (each group one stop of Tab, the tools one stop each);
        // the window binds none of these keys.
        foreach (var group in new Panel[] { ToolGroup, ColorGroup, DrawColorGroup, WidthGroup })
        {
            foreach (var key in new[] { Key.Left, Key.Right, Key.Home, Key.End })
            {
                group.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key), Command = new RelayCommand(() => MoveIn(group, key)) });
            }
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

    /// <summary>What the toolbar shows after the tools.</summary>
    private enum StyleMode
    {
        /// <summary>Nothing: no tool, or the select tool without a selection.</summary>
        None,

        /// <summary>The colours of the text markup tool that is on.</summary>
        Markup,

        /// <summary>The markup colours of a selected highlight, underline or strike-out.</summary>
        MarkupSelection,

        /// <summary>The colours and widths of the drawing tool that is on.</summary>
        Draw,

        /// <summary>The colours and widths of a selected drawing Pagewing can redraw.</summary>
        DrawSelection,

        /// <summary>A lock with the reason: a selected drawing of another program.</summary>
        Locked,
    }

    /// <summary>Gets whether Undo and Redo are in the menu "⋯" (for tests).</summary>
    internal bool IsUndoInMenu => _undoInMenu;

    /// <summary>Gets whether the colours (and widths) are in the menu "⋯" (for tests).</summary>
    internal bool AreColorsInMenu => _colorsInMenu;

    /// <summary>Gets whether the shapes other than the tool that is on are in the menu "⋯" (for tests).</summary>
    internal bool AreShapesInMenu => _shapesInMenu;

    /// <summary>
    /// Gets the token of the check mark on a swatch of <paramref name="color"/> (content of the PDF): search.on (dark) on a
    /// light colour, page.darkForeground (light) on a dark one; both are the same in every theme, so the mark keeps 3:1.
    /// </summary>
    /// <param name="color">The colour of the swatch.</param>
    /// <returns>The name of the token without its prefix, "Search.On" or "Page.DarkForeground".</returns>
    internal static string CheckTokenFor(AnnotationColor color)
    {
        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        // Relative luminance (WCAG): above this a dark mark has the better contrast, below it a light one.
        var luminance = (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
        return luminance > 0.18 ? "Search.On" : "Page.DarkForeground";
    }

    /// <summary>Gets the button of a colour (for tests).</summary>
    internal ActionToggleButton SwatchOf(MarkupColor color) => _swatches[color];

    /// <summary>Gets the check mark of a colour (for tests).</summary>
    internal StrokeIcon CheckOf(MarkupColor color) => _checks[color];

    /// <summary>Gets the button of a colour of the drawing tools (for tests).</summary>
    internal ActionToggleButton DrawSwatchOf(DrawColor color) => _drawSwatches[color];

    /// <summary>Gets the check mark of a colour of the drawing tools (for tests).</summary>
    internal StrokeIcon DrawCheckOf(DrawColor color) => _drawChecks[color];

    /// <summary>Gets the button of a stroke width (for tests).</summary>
    internal ActionToggleButton WidthButtonOf(StrokeWidth width) => _widths[width];

    /// <summary>Gets whether a shape tool is in the menu "⋯" instead of the row (for tests).</summary>
    internal bool IsShapeInMenu(AnnotationKind kind) => _shapesInMenu && Shapes.Contains(kind) && _tab?.ActiveTool != kind;

    /// <summary>
    /// The tooltips of the tools, undo and redo name their shortcuts as the catalog has them ("Highlight (Ctrl+Shift+H)"),
    /// and screen readers get them as accelerator keys; the entries of the shapes in the menu "⋯" show them too.
    /// </summary>
    internal void NameShortcuts(CommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Describe(SelectTool, Strings.ToolSelect, catalog.ById("annotate.select"), null);
        Describe(InkTool, Strings.ToolInk, catalog.ById("annotate.ink"), AnnotationKind.Ink);
        Describe(RectangleTool, Strings.ToolRectangle, catalog.ById("annotate.rectangle"), AnnotationKind.Rectangle);
        Describe(EllipseTool, Strings.ToolEllipse, catalog.ById("annotate.ellipse"), AnnotationKind.Ellipse);
        Describe(LineTool, Strings.ToolLine, catalog.ById("annotate.line"), AnnotationKind.Line);
        Describe(ArrowTool, Strings.ToolArrow, catalog.ById("annotate.arrow"), AnnotationKind.Arrow);
        Describe(HighlightTool, Strings.CommandHighlight, catalog.ById("annotate.highlight"), null);
        Describe(UnderlineTool, Strings.CommandUnderline, catalog.ById("annotate.underline"), null);
        Describe(StrikeOutTool, Strings.CommandStrikeOut, catalog.ById("annotate.strikeout"), null);
        Describe(UndoButton, Strings.CommandUndo, catalog.ById("edit.undo"), null);
        Describe(RedoButton, Strings.CommandRedo, catalog.ById("edit.redo"), null);

        void Describe(Control control, string label, AppCommand? command, AnnotationKind? kind)
        {
            if (command?.Gesture is not { } gesture)
            {
                ToolTip.SetTip(control, label);
                return;
            }

            if (kind is { } shape)
            {
                _gestures[shape] = gesture;
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

        UpdateState();
    }

    private static void SetChosen(ActionToggleButton button, bool chosen)
    {
        button.IsChecked = chosen;
        button.Classes.Set("active", chosen);
    }

    private static MenuItem MenuEntry(string name, string header, System.Windows.Input.ICommand command, object? parameter, KeyGesture? gesture)
    {
        var entry = new MenuItem { Name = name, Header = header, Command = command, CommandParameter = parameter, InputGesture = gesture };
        AutomationProperties.SetName(entry, header);
        return entry;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.ActiveTool) or nameof(TabViewModel.ActiveColor) or nameof(TabViewModel.ActiveStyle)
            or nameof(TabViewModel.IsAnnotating) or nameof(TabViewModel.IsSelectTool) or nameof(TabViewModel.SelectedAnnotation)
            or nameof(TabViewModel.SelectionGeometry))
        {
            UpdateState();
        }
    }

    /// <summary>What the toolbar shows after the tools, from the tool that is on or the selection of the select tool.</summary>
    private static StyleMode StyleOf(TabViewModel? tab)
    {
        if (tab is not { IsAnnotating: true })
        {
            return StyleMode.None;
        }

        if (tab.ActiveTool is { } tool)
        {
            return TabViewModel.IsMarkupKind(tool) ? StyleMode.Markup : TabViewModel.IsDrawKind(tool) ? StyleMode.Draw : StyleMode.None;
        }

        if (!tab.IsSelectTool || tab.SelectedAnnotation is null)
        {
            return StyleMode.None;
        }

        return tab.CanMoveSelection ? StyleMode.DrawSelection
            : tab.IsMarkupSelected ? StyleMode.MarkupSelection
            : tab.SelectionLockReason is not null ? StyleMode.Locked
            : StyleMode.None;
    }

    /// <summary>Shows the chosen tool, the colours and widths that go with it and their chosen values; then fits the row again.</summary>
    private void UpdateState()
    {
        var tab = _tab;
        var tool = tab?.ActiveTool;
        SetChosen(SelectTool, tab?.IsSelectTool == true);
        SetChosen(InkTool, tool == AnnotationKind.Ink);
        SetChosen(RectangleTool, tool == AnnotationKind.Rectangle);
        SetChosen(EllipseTool, tool == AnnotationKind.Ellipse);
        SetChosen(LineTool, tool == AnnotationKind.Line);
        SetChosen(ArrowTool, tool == AnnotationKind.Arrow);
        SetChosen(HighlightTool, tool == AnnotationKind.Highlight);
        SetChosen(UnderlineTool, tool == AnnotationKind.Underline);
        SetChosen(StrikeOutTool, tool == AnnotationKind.StrikeOut);

        _style = StyleOf(tab);
        var markupColor = _style switch
        {
            StyleMode.Markup => tab?.ActiveColor,
            StyleMode.MarkupSelection => tab?.SelectionMarkupColor,
            _ => null,
        };
        var markupCommand = _style == StyleMode.MarkupSelection ? tab?.SetSelectionMarkupColorCommand : tab?.SetColorCommand;
        foreach (var (each, swatch) in _swatches)
        {
            var chosen = each == markupColor;
            SetChosen(swatch, chosen);
            _checks[each].IsVisible = chosen;
            swatch.Command = markupCommand;
            swatch.CommandParameter = each;
        }

        var (drawColor, width) = DrawStyle(tab);
        foreach (var (each, swatch) in _drawSwatches)
        {
            var chosen = each == drawColor;
            SetChosen(swatch, chosen);
            _drawChecks[each].IsVisible = chosen;
        }

        foreach (var (each, button) in _widths)
        {
            SetChosen(button, each == width);
        }

        var reason = _style == StyleMode.Locked ? tab?.SelectionLockReason : null;
        AutomationProperties.SetName(SelectionLock, reason);
        ToolTip.SetTip(SelectionLock, reason);
        ApplyVisibility();
        Fit();
    }

    /// <summary>The colour and width the drawing controls show: of the drawing tool, or of the selected drawing.</summary>
    private (DrawColor? Color, StrokeWidth? Width) DrawStyle(TabViewModel? tab) => _style switch
    {
        StyleMode.Draw when tab is not null => (tab.ActiveStyle.Color, tab.ActiveStyle.Width),
        StyleMode.DrawSelection when tab is not null => (tab.SelectionDrawColor, tab.SelectionStrokeWidth),
        _ => (null, null),
    };

    /// <summary>Shows what the state and the level of <see cref="Fit"/> put into the row.</summary>
    private void ApplyVisibility()
    {
        var markup = (_style is StyleMode.Markup or StyleMode.MarkupSelection) && !_colorsInMenu;
        var draw = (_style is StyleMode.Draw or StyleMode.DrawSelection) && !_colorsInMenu;
        var locked = _style == StyleMode.Locked;
        ColorGroup.IsVisible = markup;
        DrawColorGroup.IsVisible = draw;
        WidthSeparator.IsVisible = draw;
        WidthGroup.IsVisible = draw;
        SelectionLock.IsVisible = locked;
        ColorSeparator.IsVisible = markup || draw || locked;
        var tool = _tab?.ActiveTool;
        RectangleTool.IsVisible = !_shapesInMenu || tool == AnnotationKind.Rectangle;
        EllipseTool.IsVisible = !_shapesInMenu || tool == AnnotationKind.Ellipse;
        LineTool.IsVisible = !_shapesInMenu || tool == AnnotationKind.Line;
        ArrowTool.IsVisible = !_shapesInMenu || tool == AnnotationKind.Arrow;
    }

    /// <summary>A colour of the drawing tools: the style of the tool that is on, or the colour of the selected drawing.</summary>
    private void ChooseDrawColor(DrawColor color)
    {
        if (_tab is not { } tab)
        {
            return;
        }

        if (_style == StyleMode.Draw)
        {
            tab.SetStyleCommand.Execute(tab.ActiveStyle with { Color = color });
        }
        else if (_style == StyleMode.DrawSelection)
        {
            tab.SetSelectionColorCommand.Execute(color);
        }
    }

    /// <summary>A stroke width: the style of the tool that is on, or the width of the selected drawing.</summary>
    private void ChooseWidth(StrokeWidth width)
    {
        if (_tab is not { } tab)
        {
            return;
        }

        if (_style == StyleMode.Draw)
        {
            tab.SetStyleCommand.Execute(tab.ActiveStyle with { Width = width });
        }
        else if (_style == StyleMode.DrawSelection)
        {
            tab.SetSelectionWidthCommand.Execute(width);
        }
    }

    private void AddSwatch(MarkupColor color)
    {
        var (swatch, check) = MakeSwatch("", color.ToString(), AnnotationPalette.ColorOf(color), Strings.ColorName(color));
        ColorGroup.Children.Add(swatch);
        _swatches[color] = swatch;
        _checks[color] = check;
    }

    private void AddDrawSwatch(DrawColor color)
    {
        var (swatch, check) = MakeSwatch("Draw", color.ToString(), DrawPalette.ColorOf(color), Strings.DrawColorName(color));
        swatch.Command = _chooseDrawColor;
        swatch.CommandParameter = color;
        DrawColorGroup.Children.Add(swatch);
        _drawSwatches[color] = swatch;
        _drawChecks[color] = check;
    }

    /// <summary>A swatch: a 28 px button with an 18 px field in the colour (content of the PDF, not a token) and a check mark.</summary>
    private static (ActionToggleButton Swatch, StrokeIcon Check) MakeSwatch(string prefix, string name, AnnotationColor rgb, string label)
    {
        var check = new StrokeIcon { Name = prefix + "Check" + name, IsVisible = false };
        check.Classes.Add("swatchCheck");
        check.Classes.Set("onDark", CheckTokenFor(rgb) != "Search.On");
        var fill = new Border
        {
            Name = prefix + "Fill" + name,
            Background = new ImmutableSolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)),
            Child = check,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        fill.Classes.Add("swatchFill");
        var swatch = new ActionToggleButton { Name = prefix + "Swatch" + name, Content = fill };
        swatch.Classes.Add("swatch");
        ToolTip.SetTip(swatch, label);
        AutomationProperties.SetName(swatch, label);
        return (swatch, check);
    }

    /// <summary>A stroke width: a button like a swatch with a line of that thickness.</summary>
    private void AddWidth(StrokeWidth width)
    {
        var glyph = new Border
        {
            Name = "WidthGlyph" + width,
            Height = StrokeWidths.PointsOf(width),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.Classes.Add("widthGlyph");
        var button = new ActionToggleButton { Name = "Width" + width, Content = glyph, Command = _chooseWidth, CommandParameter = width };
        button.Classes.Add("swatch");
        var label = Strings.StrokeWidthName(width);
        ToolTip.SetTip(button, label);
        AutomationProperties.SetName(button, label);
        WidthGroup.Children.Add(button);
        _widths[width] = button;
    }

    /// <summary>
    /// Moves the keyboard focus within a group (Left, Right with wrap-around, Home, End) over the buttons shown in the row;
    /// Enter or Space chooses.
    /// </summary>
    private static void MoveIn(Panel group, Key key)
    {
        var buttons = group.Children.OfType<ActionToggleButton>().Where(button => button.IsVisible).ToArray();
        var count = buttons.Length;
        if (count == 0)
        {
            return;
        }

        var current = Array.FindIndex(buttons, button => button.IsFocused);
        if (current < 0)
        {
            current = Math.Max(0, Array.FindIndex(buttons, button => button.IsChecked == true));
        }

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
    /// Too narrow for everything (design system 6, narrow window; 7.7), step by step until it fits: first the hint "Esc ends"
    /// goes, then the labels of the tools (icons with tooltips stay), then the text of the mode name (its icon stays), then
    /// Undo and Redo, after them the colours and widths, then the four shapes (never the tool that is on) move into the menu
    /// "⋯", and last the icon of the mode goes. Select, freehand, the text markups, the tool that is on and "Done" always stay
    /// in the row.
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
        _shapesInMenu = level >= 6;
        ModeLabel.IsVisible = level < 7;
        UndoButton.IsVisible = !_undoInMenu;
        RedoButton.IsVisible = !_undoInMenu;
        MoreButton.IsVisible = _undoInMenu;
        ApplyVisibility();
    }

    private double Needs()
    {
        // Everything is measured again: a part whose measure is still valid would keep the old size of what came or went
        // inside it (a label of a tool, a button of a group). The toolbar has a few dozen parts, and this runs only when its
        // width or its state changes.
        ToolbarContent.InvalidateMeasure();
        foreach (var part in ToolbarContent.GetVisualDescendants().OfType<Layoutable>())
        {
            part.InvalidateMeasure();
        }

        ToolbarContent.Measure(Size.Infinity);
        return ToolbarContent.DesiredSize.Width - ToolbarContent.Margin.Left - ToolbarContent.Margin.Right;
    }

    /// <summary>
    /// Fills the menu "⋯" when it opens with what is not in the row: Undo and Redo (with their keys), the shapes (each with
    /// its key and whether it is on), then the colours (each with its swatch and name, the chosen one with a check mark) and
    /// the widths (the chosen one checked) that go with the tool or the selection.
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

        var shapes = Shapes.Where(IsShapeInMenu).ToArray();
        if (shapes.Length > 0)
        {
            _moreEntries.Add(new Separator());
            foreach (var shape in shapes)
            {
                var entry = MenuEntry("More" + shape, Strings.KindName(shape)!, tab.SetToolCommand, shape, _gestures.GetValueOrDefault(shape));
                entry.ToggleType = MenuItemToggleType.CheckBox;
                entry.IsChecked = tab.ActiveTool == shape;
                _moreEntries.Add(entry);
            }
        }

        if (!_colorsInMenu)
        {
            return;
        }

        if (_style is StyleMode.Markup or StyleMode.MarkupSelection)
        {
            var command = _style == StyleMode.MarkupSelection ? tab.SetSelectionMarkupColorCommand : tab.SetColorCommand;
            var chosen = _style == StyleMode.MarkupSelection ? tab.SelectionMarkupColor : tab.ActiveColor;
            _moreEntries.Add(new Separator());
            foreach (var color in AnnotationPalette.All)
            {
                var entry = MenuEntry("More" + color, Strings.ColorName(color), command, color, null);
                entry.Icon = CommentEditor.Swatch(color, chosen == color);
                _moreEntries.Add(entry);
            }
        }
        else if (_style is StyleMode.Draw or StyleMode.DrawSelection)
        {
            var (chosenColor, chosenWidth) = DrawStyle(tab);
            _moreEntries.Add(new Separator());
            foreach (var color in DrawPalette.All)
            {
                var entry = MenuEntry("MoreDraw" + color, Strings.DrawColorName(color), _chooseDrawColor, color, null);
                entry.Icon = CommentEditor.Swatch(DrawPalette.ColorOf(color), chosenColor == color);
                _moreEntries.Add(entry);
            }

            _moreEntries.Add(new Separator());
            foreach (var width in Widths)
            {
                var entry = MenuEntry("MoreWidth" + width, Strings.StrokeWidthName(width), _chooseWidth, width, null);
                entry.ToggleType = MenuItemToggleType.CheckBox;
                entry.IsChecked = chosenWidth == width;
                _moreEntries.Add(entry);
            }
        }
    }

    private void ShowLabels(bool show)
    {
        HighlightLabel.IsVisible = show;
        UnderlineLabel.IsVisible = show;
        StrikeOutLabel.IsVisible = show;
    }
}
