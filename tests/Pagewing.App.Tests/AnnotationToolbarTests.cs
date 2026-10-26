using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The toolbar of the mode "Comment" (milestone 9, task 16; design system 6 point 4, 7.7 and 7.10): names, contrast,
/// states that are not told by colour alone, the keyboard and a narrow window.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class AnnotationToolbarTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public AnnotationToolbarTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaTheory]
    [InlineData("en", "Yellow", "Green", "Blue", "Pink", "Orange")]
    [InlineData("de", "Gelb", "Grün", "Blau", "Rosa", "Orange")]
    public async Task ColourButtonsAreNamedAndMeetThreeToOneContrast(string language, string yellow, string green, string blue, string pink, string orange)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, tab, toolbar, _) = await ShowInModeAsync();

        // The colours of the markups are shown with a markup tool.
        tab.SetToolCommand.Execute(AnnotationKind.Highlight);
        Settle(window);
        string[] names = [yellow, green, blue, pink, orange];

        // One button per colour of the palette, in its order, named for screen readers and in the tooltip.
        var group = Find<StackPanel>(toolbar, "ColorGroup");
        var swatches = group.Children.OfType<ActionToggleButton>().ToArray();
        Assert.Equal(AnnotationPalette.All.Count, swatches.Length);
        for (var i = 0; i < swatches.Length; i++)
        {
            var color = AnnotationPalette.All[i];
            Assert.Same(toolbar.SwatchOf(color), swatches[i]);
            Assert.Equal(names[i], AutomationProperties.GetName(swatches[i]));
            Assert.Equal(names[i], ToolTip.GetTip(swatches[i]));
            Assert.IsType<ToggleButtonAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(swatches[i]));

            // The swatch is the colour of the markup (content of the PDF) with a border.strong edge.
            var fill = Find<Border>(swatches[i], "Fill" + color);
            var rgb = AnnotationPalette.ColorOf(color);
            Assert.Equal(Color.FromRgb(rgb.R, rgb.G, rgb.B), ((ISolidColorBrush)fill.Background!).Color);
            Assert.Equal(TokenTests.Token("Border.Strong", ThemeVariant.Light), ((ISolidColorBrush)fill.BorderBrush!).Color);
            Assert.Equal(new Thickness(1), fill.BorderThickness);
        }

        Assert.Equal(Strings.AnnotateColorsName, AutomationProperties.GetName(group));
        Assert.Equal(44, toolbar.Bounds.Height);

        // In every theme: the edge of a swatch, the ring of the chosen one and the focus ring against the toolbar, and
        // the check mark against each colour of the palette, at least 3:1.
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, HighContrast.Variant })
        {
            var surface = TokenTests.Token("Surface.Toolbar", variant);
            Assert.True(Wcag.Contrast(TokenTests.Token("Border.Strong", variant), surface) >= 3, $"border.strong on surface.toolbar ({variant})");
            Assert.True(Wcag.Contrast(TokenTests.Token("Text.Primary", variant), surface) >= 3, $"ring on surface.toolbar ({variant})");
            Assert.True(Wcag.Contrast(TokenTests.Token("Focus.Ring", variant), surface) >= 3, $"focus.ring on surface.toolbar ({variant})");
            foreach (var color in AnnotationPalette.All)
            {
                var rgb = AnnotationPalette.ColorOf(color);
                Assert.True(Wcag.Contrast(TokenTests.Token("Search.On", variant), Color.FromRgb(rgb.R, rgb.G, rgb.B)) >= 3, $"check on {color} ({variant})");
            }
        }

        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public async Task SelectedToolAndColourAreNotOnlyShownByColour()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();

        // Wide enough for the labels of the markup tools next to the colours of the tool (they go first when it is narrower).
        window.Width = 1280;
        Settle(window);
        tab.SetToolCommand.Execute(AnnotationKind.Underline);
        Settle(window);

        // The chosen tool: on for screen readers, an accent frame and an accent label; the others neither.
        var underline = Find<ActionToggleButton>(toolbar, "UnderlineTool");
        var highlight = Find<ActionToggleButton>(toolbar, "HighlightTool");
        Assert.Equal(ToggleState.On, ToggleStateOf(underline));
        Assert.Equal(ToggleState.Off, ToggleStateOf(highlight));
        Assert.Contains("active", underline.Classes);
        Assert.Equal(new Thickness(1), underline.BorderThickness);
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), ((ISolidColorBrush)underline.BorderBrush!).Color);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)highlight.BorderBrush!).Color);
        Assert.Equal(Strings.CommandUnderline, AutomationProperties.GetName(underline));
        Assert.Equal("Underline (Ctrl+Shift+U)", ToolTip.GetTip(underline));
        Assert.Equal("Ctrl+Shift+U", AutomationProperties.GetAcceleratorKey(underline));
        Assert.True(Find<TextBlock>(toolbar, "UnderlineLabel").IsEffectivelyVisible);

        // The chosen colour (blue, that of the underline): on, a ring and a check mark; the others none.
        var blue = toolbar.SwatchOf(MarkupColor.Blue);
        var yellow = toolbar.SwatchOf(MarkupColor.Yellow);
        Assert.Equal(ToggleState.On, ToggleStateOf(blue));
        Assert.Equal(ToggleState.Off, ToggleStateOf(yellow));
        Assert.True(toolbar.CheckOf(MarkupColor.Blue).IsEffectivelyVisible);
        Assert.False(toolbar.CheckOf(MarkupColor.Yellow).IsVisible);
        Assert.Equal(TokenTests.Token("Text.Primary", ThemeVariant.Light), ((ISolidColorBrush)blue.BorderBrush!).Color);
        Assert.Equal(new Thickness(2), blue.BorderThickness);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)yellow.BorderBrush!).Color);

        // A click on a colour chooses it for the tool.
        Click(window, toolbar.SwatchOf(MarkupColor.Orange));
        Assert.Equal(MarkupColor.Orange, tab.ActiveColor);
        Assert.True(toolbar.CheckOf(MarkupColor.Orange).IsEffectivelyVisible);
        Assert.False(toolbar.CheckOf(MarkupColor.Blue).IsVisible);
    }

    [AvaloniaFact]
    public async Task ToolbarIsKeyboardOperable()
    {
        var (window, tab, toolbar, view) = await ShowInModeAsync();
        var highlight = Find<ActionToggleButton>(toolbar, "HighlightTool");
        highlight.Focus(NavigationMethod.Tab);
        Settle(window);

        // Space on a tool chooses it, Space again turns it off; Tab goes to the next tool.
        Press(window, Key.Space, PhysicalKey.Space);
        Assert.Equal(AnnotationKind.Highlight, tab.ActiveTool);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(Find<ActionToggleButton>(toolbar, "UnderlineTool").IsFocused);
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(AnnotationKind.Underline, tab.ActiveTool);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(Find<ActionToggleButton>(toolbar, "StrikeOutTool").IsFocused);

        // The colours are one stop of Tab; the arrows move within them, with wrap-around; Enter chooses.
        Press(window, Key.Tab, PhysicalKey.Tab);
        var focused = Focused(window);
        var swatches = Find<StackPanel>(toolbar, "ColorGroup").Children.OfType<ActionToggleButton>().ToArray();
        Assert.Contains(focused, swatches);
        swatches[0].Focus(NavigationMethod.Directional);
        Settle(window);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.True(swatches[^1].IsFocused);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.True(swatches[1].IsFocused);
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(MarkupColor.Green, tab.ActiveColor);
        Press(window, Key.End, PhysicalKey.End);
        Assert.True(swatches[^1].IsFocused);
        Press(window, Key.Home, PhysicalKey.Home);
        Assert.True(swatches[0].IsFocused);

        // Tab leaves the colours: undo and redo cannot run yet (nothing to undo), so the next stop is "Done"; Enter ends the
        // mode and the keys go back to the document.
        Press(window, Key.Tab, PhysicalKey.Tab);
        var done = Find<Button>(toolbar, "DoneButton");
        Assert.True(done.IsFocused);
        Assert.Equal(Strings.AnnotateDone, AutomationProperties.GetName(done));
        Assert.Equal(Strings.CommandUndo, AutomationProperties.GetName(Find<Button>(toolbar, "UndoButton")));
        Assert.Equal(Strings.CommandRedo, AutomationProperties.GetName(Find<Button>(toolbar, "RedoButton")));
        Assert.Equal(Strings.AnnotateToolbarName, AutomationProperties.GetName(toolbar));
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.False(tab.IsAnnotating);
        Assert.True(view.IsFocused);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void ToolbarColoursMeetAA(string theme)
    {
        var variant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "HighContrast" => HighContrast.Variant,
            _ => ThemeVariant.Light,
        };
        double Ratio(string foreground, string background) => Wcag.Contrast(TokenTests.Token(foreground, variant), TokenTests.Token(background, variant));

        // Mode name and the labels of the tools, the hint, a hovered button.
        Assert.True(Ratio("Text.Primary", "Surface.Toolbar") >= 4.5, "text.primary on surface.toolbar");
        Assert.True(Ratio("Text.Secondary", "Surface.Toolbar") >= 4.5, "text.secondary on surface.toolbar");
        Assert.True(Ratio("Text.Primary", "Surface.Hover") >= 4.5, "text.primary on surface.hover");

        // The icon of the mode, the chosen tool (its label, icon and frame on accent.subtle) and "Done".
        Assert.True(Ratio("Accent.Default", "Surface.Toolbar") >= 3, "accent.default icon on surface.toolbar");
        Assert.True(Ratio("Accent.Default", "Accent.Subtle") >= 4.5, "accent.default label on accent.subtle");
        Assert.True(Ratio("Accent.On", "Accent.Default") >= 4.5, "accent.on on accent.default");
    }

    [AvaloniaFact]
    public async Task NarrowToolbarDropsTheHintThenTheLabels()
    {
        var (window, _, toolbar, _) = await ShowInModeAsync();
        Assert.True(Find<TextBlock>(toolbar, "EscHint").IsEffectivelyVisible);
        Assert.True(Find<TextBlock>(toolbar, "HighlightLabel").IsEffectivelyVisible);
        Assert.False(Find<Button>(toolbar, "MoreButton").IsVisible);

        // At the smallest width of the window the hint and the labels go; the name of the mode and every tool stay.
        window.Width = 640;
        Settle(window);
        Settle(window);
        Assert.False(Find<TextBlock>(toolbar, "EscHint").IsVisible);
        Assert.False(Find<TextBlock>(toolbar, "HighlightLabel").IsVisible);
        Assert.Equal("Highlight (Ctrl+Shift+H)", ToolTip.GetTip(Find<ActionToggleButton>(toolbar, "HighlightTool")));
        await AssertEverythingReachableAsync(window, toolbar);

        // Wide again: everything is back.
        window.Width = 1200;
        Settle(window);
        Settle(window);
        Assert.True(Find<TextBlock>(toolbar, "EscHint").IsEffectivelyVisible);
        Assert.True(Find<TextBlock>(toolbar, "HighlightLabel").IsEffectivelyVisible);
        Assert.True(Find<TextBlock>(toolbar, "ModeName").IsEffectivelyVisible);
        Assert.False(Find<Button>(toolbar, "MoreButton").IsVisible);
    }

    [AvaloniaTheory]
    [InlineData("en", 640, false)]
    [InlineData("de", 640, false)]
    [InlineData("en", 900, true)]
    [InlineData("de", 900, true)]
    public async Task EveryToolStaysReachableInANarrowDocumentArea(string language, double width, bool widePanel)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        var viewModel = (MainViewModel)window.DataContext!;
        window.Width = width;
        Settle(window);
        if (widePanel)
        {
            // The widest panel beside the document in the narrowest window that docks it: the document area is 448 px.
            viewModel.PanelWidth = MainViewModel.MaxPanelWidth;
            viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Comments);
            await WaitUntilAsync(window, () => !viewModel.IsNarrowWindow && Find<Control>(window, "PanelHost").IsEffectivelyVisible);
        }

        Settle(window);
        Settle(window);
        await AssertEverythingReachableAsync(window, toolbar);
        if (widePanel)
        {
            // There everything that does not fit is in the menu "⋯": undo, redo, the colours and the shapes.
            Assert.True(toolbar.IsUndoInMenu);
            Assert.True(toolbar.AreColorsInMenu);
            Assert.True(toolbar.AreShapesInMenu);
            Assert.False(Find<TextBlock>(toolbar, "ModeName").IsVisible);
        }

        // With a tool on (its colours shown), every tool stays reachable as well, the tool itself in the row.
        tab.SetToolCommand.Execute(AnnotationKind.Underline);
        Settle(window);
        Settle(window);
        await AssertEverythingReachableAsync(window, toolbar);
        tab.SetToolCommand.Execute(AnnotationKind.Arrow);
        Settle(window);
        Settle(window);
        await AssertEverythingReachableAsync(window, toolbar);
    }

    [AvaloniaFact]
    public async Task ToolButtonsHaveAutomationNamesAndStateNotOnlyColor()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        foreach (var (name, label, keys, icon, choose, isOn) in Tools(tab))
        {
            var button = Find<ActionToggleButton>(toolbar, name);

            // Named for screen readers, the shortcut in the tooltip and as accelerator key, a toggle button with its icon.
            Assert.Equal(label, AutomationProperties.GetName(button));
            Assert.Equal($"{label} ({keys})", ToolTip.GetTip(button));
            Assert.Equal(keys, AutomationProperties.GetAcceleratorKey(button));
            Assert.IsType<ToggleButtonAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(button));
            Assert.Same(Application.Current!.FindResource(icon), Find<StrokeIcon>(button, name + "Icon").Data);
            Assert.Equal(ToggleState.Off, ToggleStateOf(button));
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)button.BorderBrush!).Color);

            // Chosen: on for screen readers and a 1 px accent frame (a shape, not only the surface colour).
            Click(window, button);
            Assert.True(isOn(), $"{name} is chosen by a click");
            Assert.Equal(ToggleState.On, ToggleStateOf(button));
            Assert.Contains("active", button.Classes);
            Assert.Equal(new Thickness(1), button.BorderThickness);
            Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), ((ISolidColorBrush)button.BorderBrush!).Color);

            // Every other tool is off.
            foreach (var other in Tools(tab).Where(other => other.Name != name))
            {
                Assert.Equal(ToggleState.Off, ToggleStateOf(Find<ActionToggleButton>(toolbar, other.Name)));
            }

            // A second click turns it off again.
            Click(window, button);
            Assert.False(isOn(), $"{name} is off after a second click");
            Assert.Equal(ToggleState.Off, ToggleStateOf(button));
            choose();
            Settle(window);
            Assert.Equal(ToggleState.On, ToggleStateOf(button));
        }

        Assert.Equal(Strings.AnnotateToolsName, AutomationProperties.GetName(Find<StackPanel>(toolbar, "ToolGroup")));
    }

    [AvaloniaFact]
    public async Task ColorButtonsMeetThreeToOne()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        tab.SetToolCommand.Execute(AnnotationKind.Rectangle);
        Settle(window);
        var group = Find<StackPanel>(toolbar, "DrawColorGroup");
        Assert.True(group.IsEffectivelyVisible);
        Assert.Equal(Strings.AnnotateColorsName, AutomationProperties.GetName(group));
        var swatches = group.Children.OfType<ActionToggleButton>().ToArray();
        Assert.Equal(DrawPalette.All.Count, swatches.Length);
        for (var i = 0; i < swatches.Length; i++)
        {
            var color = DrawPalette.All[i];
            var rgb = DrawPalette.ColorOf(color);
            Assert.Same(toolbar.DrawSwatchOf(color), swatches[i]);
            Assert.Equal(Strings.DrawColorName(color), AutomationProperties.GetName(swatches[i]));
            Assert.Equal(Strings.DrawColorName(color), ToolTip.GetTip(swatches[i]));
            Assert.IsType<ToggleButtonAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(swatches[i]));
            var fill = Find<Border>(swatches[i], "DrawFill" + color);
            Assert.Equal(Color.FromRgb(rgb.R, rgb.G, rgb.B), ((ISolidColorBrush)fill.Background!).Color);
            Assert.Equal(TokenTests.Token("Border.Strong", ThemeVariant.Light), ((ISolidColorBrush)fill.BorderBrush!).Color);

            // The check mark has the colour of its token in every theme, chosen for the colour of the swatch.
            var token = AnnotationToolbar.CheckTokenFor(rgb);
            Assert.Equal(TokenTests.Token(token, ThemeVariant.Light), ((ISolidColorBrush)toolbar.DrawCheckOf(color).Brush!).Color);
        }

        // The widths: three buttons, named with their size, a ring when chosen.
        var widths = Find<StackPanel>(toolbar, "WidthGroup");
        Assert.True(widths.IsEffectivelyVisible);
        Assert.Equal(Strings.AnnotateWidthsName, AutomationProperties.GetName(widths));
        foreach (var width in new[] { StrokeWidth.Thin, StrokeWidth.Medium, StrokeWidth.Thick })
        {
            var button = toolbar.WidthButtonOf(width);
            Assert.Equal(Strings.StrokeWidthName(width), AutomationProperties.GetName(button));
            Assert.Equal(Strings.StrokeWidthName(width), ToolTip.GetTip(button));
            Assert.Equal(width == StrokeWidth.Medium ? ToggleState.On : ToggleState.Off, ToggleStateOf(button));
            Assert.Equal(width == StrokeWidth.Medium ? TokenTests.Token("Text.Primary", ThemeVariant.Light) : Colors.Transparent, ((ISolidColorBrush)button.BorderBrush!).Color);
        }

        // In every theme: edge, ring and focus ring against the toolbar, the glyph of a width, and the check mark against
        // each colour of the drawing palette, at least 3:1.
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, HighContrast.Variant })
        {
            var surface = TokenTests.Token("Surface.Toolbar", variant);
            Assert.True(Wcag.Contrast(TokenTests.Token("Border.Strong", variant), surface) >= 3, $"border.strong on surface.toolbar ({variant})");
            Assert.True(Wcag.Contrast(TokenTests.Token("Text.Primary", variant), surface) >= 3, $"ring and width glyph on surface.toolbar ({variant})");
            Assert.True(Wcag.Contrast(TokenTests.Token("Focus.Ring", variant), surface) >= 3, $"focus.ring on surface.toolbar ({variant})");
            foreach (var color in DrawPalette.All)
            {
                var rgb = DrawPalette.ColorOf(color);
                var check = TokenTests.Token(AnnotationToolbar.CheckTokenFor(rgb), variant);
                Assert.True(Wcag.Contrast(check, Color.FromRgb(rgb.R, rgb.G, rgb.B)) >= 3, $"check on {color} ({variant})");
            }
        }
    }

    [AvaloniaFact]
    public async Task KeyboardReachesEveryTool()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        string[] order = ["SelectTool", "InkTool", "RectangleTool", "EllipseTool", "LineTool", "ArrowTool", "TextBoxTool", "HighlightTool", "UnderlineTool", "StrikeOutTool"];

        // Tab goes through every tool in this order.
        Find<ActionToggleButton>(toolbar, order[0]).Focus(NavigationMethod.Tab);
        Settle(window);
        for (var i = 1; i < order.Length; i++)
        {
            Press(window, Key.Tab, PhysicalKey.Tab);
            Assert.True(Find<ActionToggleButton>(toolbar, order[i]).IsFocused, $"Tab reaches {order[i]}");
        }

        // The arrows move within the tools (with wrap-around), Home and End go to the ends.
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.True(Find<ActionToggleButton>(toolbar, order[0]).IsFocused);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.True(Find<ActionToggleButton>(toolbar, order[^1]).IsFocused);
        Press(window, Key.Home, PhysicalKey.Home);
        Assert.True(Find<ActionToggleButton>(toolbar, order[0]).IsFocused);

        // Enter turns the select tool on; Right, Right and Space choose the rectangle.
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.True(tab.IsSelectTool);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.True(Find<ActionToggleButton>(toolbar, "RectangleTool").IsFocused);
        Press(window, Key.Space, PhysicalKey.Space);
        Assert.Equal(AnnotationKind.Rectangle, tab.ActiveTool);
        Assert.False(tab.IsSelectTool);

        // After the tools: the colours of the drawing tool (one stop), the widths (one stop), then "Done".
        Press(window, Key.End, PhysicalKey.End);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.Contains(Focused(window), Find<StackPanel>(toolbar, "DrawColorGroup").Children);
        Press(window, Key.End, PhysicalKey.End);
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(DrawColor.Orange, tab.ActiveStyle.Color);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.Contains(Focused(window), Find<StackPanel>(toolbar, "WidthGroup").Children);
        Press(window, Key.Home, PhysicalKey.Home);
        Assert.True(toolbar.WidthButtonOf(StrokeWidth.Thin).IsFocused);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Press(window, Key.Space, PhysicalKey.Space);
        Assert.Equal(new ToolStyle(DrawColor.Orange, StrokeWidth.Thick), tab.StyleOf(AnnotationKind.Rectangle));
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(Find<Button>(toolbar, "DoneButton").IsFocused);
    }

    [AvaloniaFact]
    public async Task SwatchesFollowTheActiveToolKind()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();

        // A text markup tool: its five colours, no colours or widths of the drawing tools.
        tab.SetToolCommand.Execute(AnnotationKind.Highlight);
        Settle(window);
        Assert.True(Find<StackPanel>(toolbar, "ColorGroup").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(toolbar, "DrawColorGroup").IsVisible);
        Assert.False(Find<StackPanel>(toolbar, "WidthGroup").IsVisible);

        // A drawing tool: its colours and widths, the colour of its style chosen (red at first).
        tab.SetToolCommand.Execute(AnnotationKind.Rectangle);
        Settle(window);
        Assert.False(Find<StackPanel>(toolbar, "ColorGroup").IsVisible);
        Assert.True(Find<StackPanel>(toolbar, "DrawColorGroup").IsEffectivelyVisible);
        Assert.True(Find<StackPanel>(toolbar, "WidthGroup").IsEffectivelyVisible);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.DrawSwatchOf(DrawColor.Red)));
        Assert.True(toolbar.DrawCheckOf(DrawColor.Red).IsEffectivelyVisible);
        Assert.False(toolbar.DrawCheckOf(DrawColor.Blue).IsVisible);
        Assert.Equal(new Thickness(2), toolbar.DrawSwatchOf(DrawColor.Red).BorderThickness);
        Assert.Equal(TokenTests.Token("Text.Primary", ThemeVariant.Light), ((ISolidColorBrush)toolbar.DrawSwatchOf(DrawColor.Red).BorderBrush!).Color);

        // A click on a colour changes the style of that tool only; the tool stays (no switch to a markup tool).
        Click(window, toolbar.DrawSwatchOf(DrawColor.Blue));
        Assert.Equal(AnnotationKind.Rectangle, tab.ActiveTool);
        Assert.Equal(new ToolStyle(DrawColor.Blue, StrokeWidth.Medium), tab.StyleOf(AnnotationKind.Rectangle));
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Medium), tab.StyleOf(AnnotationKind.Ink));
        Assert.True(toolbar.DrawCheckOf(DrawColor.Blue).IsEffectivelyVisible);
        Assert.False(toolbar.DrawCheckOf(DrawColor.Red).IsVisible);
        Assert.Equal(MarkupColor.Yellow, tab.ActiveColor);

        // Another drawing tool shows its own style.
        tab.SetToolCommand.Execute(AnnotationKind.Ellipse);
        Settle(window);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.DrawSwatchOf(DrawColor.Red)));
        Assert.Equal(ToggleState.Off, ToggleStateOf(toolbar.DrawSwatchOf(DrawColor.Blue)));

        // Back to a markup tool: its colours again.
        tab.SetToolCommand.Execute(AnnotationKind.Underline);
        Settle(window);
        Assert.True(Find<StackPanel>(toolbar, "ColorGroup").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(toolbar, "DrawColorGroup").IsVisible);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.SwatchOf(MarkupColor.Blue)));
    }

    [AvaloniaFact]
    public async Task WidthButtonsChangeTheToolStyle()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        Settle(window);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.WidthButtonOf(StrokeWidth.Medium)));

        Click(window, toolbar.WidthButtonOf(StrokeWidth.Thick));
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Thick), tab.StyleOf(AnnotationKind.Ink));
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Medium), tab.StyleOf(AnnotationKind.Line));
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.WidthButtonOf(StrokeWidth.Thick)));
        Assert.Equal(ToggleState.Off, ToggleStateOf(toolbar.WidthButtonOf(StrokeWidth.Medium)));
        Assert.Equal(new Thickness(2), toolbar.WidthButtonOf(StrokeWidth.Thick).BorderThickness);
        Assert.Equal(TokenTests.Token("Text.Primary", ThemeVariant.Light), ((ISolidColorBrush)toolbar.WidthButtonOf(StrokeWidth.Thick).BorderBrush!).Color);
        Assert.Equal(AnnotationKind.Ink, tab.ActiveTool);

        // Each width shows a line of its thickness (the glyph grows with the width).
        var thin = Find<Border>(toolbar.WidthButtonOf(StrokeWidth.Thin), "WidthGlyphThin");
        var thick = Find<Border>(toolbar.WidthButtonOf(StrokeWidth.Thick), "WidthGlyphThick");
        Assert.True(thick.Bounds.Height > thin.Bounds.Height);

        // The line tool remembers its own width.
        tab.SetToolCommand.Execute(AnnotationKind.Line);
        Settle(window);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.WidthButtonOf(StrokeWidth.Medium)));
    }

    [AvaloniaFact]
    public async Task SelectionRecolourIsOneUndoStep()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync(copy: true);
        var created = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(100, 100, 200, 160), 2));
        await SelectAsync(window, tab, new DisplayPoint(100, 130), created.Name);

        // The selected drawing: its colour and width, the controls act on it (not on the style of a tool).
        Assert.True(Find<StackPanel>(toolbar, "DrawColorGroup").IsEffectivelyVisible);
        Assert.True(Find<StackPanel>(toolbar, "WidthGroup").IsEffectivelyVisible);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.DrawSwatchOf(DrawColor.Red)));
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.WidthButtonOf(StrokeWidth.Medium)));
        var version = tab.EditVersion;

        Click(window, toolbar.DrawSwatchOf(DrawColor.Blue));
        var blue = await WaitForAsync(window, tab, created.Name, a => a.Color == DrawPalette.ColorOf(DrawColor.Blue));
        Assert.Equal(version + 1, tab.EditVersion);
        Assert.Equal(2, blue.Drawing!.Width);
        Assert.Equal(created.Drawing!.Box, blue.Drawing.Box);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Color == DrawPalette.ColorOf(DrawColor.Blue));
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.DrawSwatchOf(DrawColor.Blue)));

        Click(window, toolbar.WidthButtonOf(StrokeWidth.Thick));
        var thick = await WaitForAsync(window, tab, created.Name, a => a.Drawing?.Width == 4);
        Assert.Equal(version + 2, tab.EditVersion);
        Assert.Equal(DrawPalette.ColorOf(DrawColor.Blue), thick.Color);
        Assert.Equal(created.Drawing.Box, thick.Drawing!.Box);

        // The style of the tools is untouched.
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Medium), tab.StyleOf(AnnotationKind.Rectangle));

        // Each change is one step: undo brings back the width, then the colour.
        tab.UndoCommand.Execute(null);
        var undone = await WaitForAsync(window, tab, created.Name, a => a.Drawing?.Width == 2);
        Assert.Equal(DrawPalette.ColorOf(DrawColor.Blue), undone.Color);
        tab.UndoCommand.Execute(null);
        await WaitForAsync(window, tab, created.Name, a => a.Color == DrawPalette.ColorOf(DrawColor.Red) && a.Drawing?.Width == 2);

        // A selected highlight: the colours of the markup act on it, its colour chosen.
        var highlight = await tab.Editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(260, 300, 360, 314)], AnnotationPalette.ColorOf(MarkupColor.Green), null, null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        await SelectAsync(window, tab, new DisplayPoint(300, 307), highlight!.Name);
        Assert.True(Find<StackPanel>(toolbar, "ColorGroup").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(toolbar, "WidthGroup").IsVisible);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.SwatchOf(MarkupColor.Green)));
        var before = tab.EditVersion;
        Click(window, toolbar.SwatchOf(MarkupColor.Pink));
        await WaitForAsync(window, tab, highlight.Name, a => a.Color == AnnotationPalette.ColorOf(MarkupColor.Pink));
        Assert.Equal(before + 1, tab.EditVersion);
        Assert.Equal(MarkupColor.Yellow, tab.ActiveColor);
        Assert.True(tab.IsSelectTool);
    }

    [AvaloniaFact]
    public async Task FastColourClicksEndOnTheLastColour()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync(copy: true);
        var created = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(100, 100, 200, 160), 2));
        await SelectAsync(window, tab, new DisplayPoint(100, 130), created.Name);
        var version = tab.EditVersion;

        // Blue, and straight back to red before the first change is made (no turn of the dispatcher in between).
        toolbar.DrawSwatchOf(DrawColor.Blue).Command!.Execute(DrawColor.Blue);
        Assert.Equal(DrawColor.Blue, tab.SelectionDrawColor);
        toolbar.DrawSwatchOf(DrawColor.Red).Command!.Execute(DrawColor.Red);
        Assert.Equal(DrawColor.Red, tab.SelectionDrawColor);

        // Both changes are made, in this order: the drawing ends red, two steps of the history; the toolbar shows red.
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.EditVersion == version + 2);
        var final = await WaitForAsync(window, tab, created.Name, _ => true);
        Assert.Equal(DrawPalette.ColorOf(DrawColor.Red), final.Color);
        Assert.Equal(DrawPalette.ColorOf(DrawColor.Red), tab.SelectedAnnotation!.Color);
        Settle(window);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.DrawSwatchOf(DrawColor.Red)));
        Assert.Equal(ToggleState.Off, ToggleStateOf(toolbar.DrawSwatchOf(DrawColor.Blue)));

        // The same for a selected highlight.
        var highlight = await tab.Editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(260, 300, 360, 314)], AnnotationPalette.ColorOf(MarkupColor.Yellow), null, null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        await SelectAsync(window, tab, new DisplayPoint(300, 307), highlight!.Name);
        tab.SetSelectionMarkupColorCommand.Execute(MarkupColor.Pink);
        tab.SetSelectionMarkupColorCommand.Execute(MarkupColor.Yellow);
        Assert.Equal(MarkupColor.Yellow, tab.SelectionMarkupColor);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Equal(AnnotationPalette.ColorOf(MarkupColor.Yellow), (await WaitForAsync(window, tab, highlight.Name, _ => true)).Color);
    }

    [AvaloniaFact]
    public async Task FastWidthClicksEndOnTheLastWidth()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync(copy: true);
        var created = await DrawAsync(window, tab, DrawingGeometry.Arrow(new DisplayPoint(100, 100), new DisplayPoint(220, 160), 2));
        await SelectAsync(window, tab, new DisplayPoint(160, 130), created.Name);
        var version = tab.EditVersion;

        // Thick, and straight back to medium before the first change is made.
        toolbar.WidthButtonOf(StrokeWidth.Thick).Command!.Execute(StrokeWidth.Thick);
        Assert.Equal(StrokeWidth.Thick, tab.SelectionStrokeWidth);
        toolbar.WidthButtonOf(StrokeWidth.Medium).Command!.Execute(StrokeWidth.Medium);
        Assert.Equal(StrokeWidth.Medium, tab.SelectionStrokeWidth);

        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.EditVersion == version + 2);
        var final = await WaitForAsync(window, tab, created.Name, _ => true);
        Assert.Equal(2, final.Drawing!.Width);
        Assert.Equal(AnnotationKind.Arrow, final.Kind);
        Assert.Equal(2, tab.SelectedAnnotation!.Drawing!.Width);
        Settle(window);
        Assert.Equal(ToggleState.On, ToggleStateOf(toolbar.WidthButtonOf(StrokeWidth.Medium)));
        Assert.Equal(ToggleState.Off, ToggleStateOf(toolbar.WidthButtonOf(StrokeWidth.Thick)));
    }

    [AvaloniaFact]
    public async Task LockedSelectionOffersNoColourControls()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync("drawings-foreign.pdf", copy: true);
        var foreign = (await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Single(a => a.Name == "ink-0001");
        var stroke = foreign.Drawing!.Strokes[0];
        await SelectAsync(window, tab, new DisplayPoint((stroke[0].X + stroke[1].X) / 2, (stroke[0].Y + stroke[1].Y) / 2), foreign.Name);
        Assert.False(tab.CanMoveSelection);

        // No colours, no widths; a lock with the reason in words for screen readers and in its tooltip.
        Assert.False(Find<StackPanel>(toolbar, "ColorGroup").IsVisible);
        Assert.False(Find<StackPanel>(toolbar, "DrawColorGroup").IsVisible);
        Assert.False(Find<StackPanel>(toolbar, "WidthGroup").IsVisible);
        var locked = Find<Control>(toolbar, "SelectionLock");
        Assert.True(locked.IsEffectivelyVisible);
        Assert.Equal(tab.SelectionLockReason, AutomationProperties.GetName(locked));
        Assert.Equal(tab.SelectionLockReason, ToolTip.GetTip(locked));
        Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(locked));

        // Nothing selected: the lock goes.
        tab.ClearSelection();
        Settle(window);
        Assert.False(locked.IsVisible);
    }

    [AvaloniaFact]
    public async Task ToolbarShowsNothingExtraWithoutToolOrSelection()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        void AssertNothingExtra()
        {
            Assert.False(Find<StackPanel>(toolbar, "ColorGroup").IsVisible);
            Assert.False(Find<StackPanel>(toolbar, "DrawColorGroup").IsVisible);
            Assert.False(Find<StackPanel>(toolbar, "WidthGroup").IsVisible);
            Assert.False(Find<Control>(toolbar, "SelectionLock").IsVisible);
            Assert.False(Find<Border>(toolbar, "ColorSeparator").IsVisible);
        }

        // In the mode without a tool.
        Assert.Null(tab.ActiveTool);
        AssertNothingExtra();

        // The select tool without a selection.
        tab.SelectToolCommand.Execute(null);
        Settle(window);
        AssertNothingExtra();

        // A tool turned on and off again.
        tab.SetToolCommand.Execute(AnnotationKind.Highlight);
        Settle(window);
        Assert.True(Find<StackPanel>(toolbar, "ColorGroup").IsEffectivelyVisible);
        tab.SetToolCommand.Execute(AnnotationKind.Highlight);
        Settle(window);
        AssertNothingExtra();
    }

    [AvaloniaFact]
    public async Task ShapesMoveIntoTheOverflowAtTheNarrowestLevel()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        string[] shapes = ["RectangleTool", "EllipseTool", "LineTool", "ArrowTool", "TextBoxTool"];

        // In a window of 800 px without a panel every tool fits in the row; since the text box (M10) the smallest window
        // (640 px) has the shapes and the text box in the menu already.
        window.Width = 800;
        Settle(window);
        Settle(window);
        Assert.False(toolbar.AreShapesInMenu);
        Assert.All(shapes, name => Assert.True(Find<ActionToggleButton>(toolbar, name).IsEffectivelyVisible, name));
        window.Width = 640;
        Settle(window);
        Settle(window);
        Assert.True(toolbar.AreShapesInMenu);

        // In the narrowest document area the shapes go into the menu "⋯"; select, freehand, the markups and "Done" stay.
        await NarrowestAsync(window);
        Assert.True(toolbar.AreShapesInMenu);
        Assert.All(shapes, name => Assert.False(Find<ActionToggleButton>(toolbar, name).IsVisible, name));
        foreach (var name in new[] { "SelectTool", "InkTool", "HighlightTool", "UnderlineTool", "StrikeOutTool", "DoneButton", "MoreButton" })
        {
            Assert.True(Find<Button>(toolbar, name).IsEffectivelyVisible, name);
        }

        var menu = await OpenMoreAsync(window, toolbar);
        Assert.Equal(
            [Strings.ToolRectangle, Strings.ToolEllipse, Strings.ToolLine, Strings.ToolArrow, Strings.ToolTextBox],
            menu.Items.OfType<MenuItem>().Where(entry => entry.Name is "MoreRectangle" or "MoreEllipse" or "MoreLine" or "MoreArrow" or "MoreTextBox").Select(entry => (string)entry.Header!));

        // A shape of the menu chooses its tool.
        var line = menu.Items.OfType<MenuItem>().Single(entry => entry.Name == "MoreLine");
        line.Command!.Execute(line.CommandParameter);
        Assert.Equal(AnnotationKind.Line, tab.ActiveTool);
        menu.Hide();
        Settle(window);
    }

    [AvaloniaFact]
    public async Task TheActiveToolStaysInTheRowEvenWhenItIsAShape()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        await NarrowestAsync(window);
        tab.EllipseCommand.Execute(null);
        Settle(window);
        Settle(window);

        // The chosen shape is in the row (on, framed), the other shapes in the menu.
        var ellipse = Find<ActionToggleButton>(toolbar, "EllipseTool");
        Assert.True(ellipse.IsEffectivelyVisible);
        Assert.Equal(ToggleState.On, ToggleStateOf(ellipse));
        Assert.False(toolbar.IsShapeInMenu(AnnotationKind.Ellipse));
        Assert.True(toolbar.IsShapeInMenu(AnnotationKind.Rectangle));
        Assert.False(Find<ActionToggleButton>(toolbar, "RectangleTool").IsVisible);
        await AssertEverythingReachableAsync(window, toolbar);

        // Another shape from the menu: it takes the place in the row, the ellipse goes into the menu.
        var menu = await OpenMoreAsync(window, toolbar);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), entry => entry.Name == "MoreEllipse");
        var rectangle = menu.Items.OfType<MenuItem>().Single(entry => entry.Name == "MoreRectangle");
        rectangle.Command!.Execute(rectangle.CommandParameter);
        menu.Hide();
        Settle(window);
        Settle(window);
        Assert.True(Find<ActionToggleButton>(toolbar, "RectangleTool").IsEffectivelyVisible);
        Assert.False(ellipse.IsVisible);
        Assert.True(toolbar.IsShapeInMenu(AnnotationKind.Ellipse));

        // "Done" is whole and the row ends before it.
        var done = Find<Button>(toolbar, "DoneButton");
        Assert.True(done.TranslatePoint(new Point(done.Bounds.Width, 0), toolbar)!.Value.X <= toolbar.Bounds.Width);
    }

    [AvaloniaFact]
    public async Task OverflowEntriesShowCheckedStateAndShortcut()
    {
        var (window, tab, toolbar, _) = await ShowInModeAsync();
        await NarrowestAsync(window);
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        Settle(window);
        Settle(window);

        var menu = await OpenMoreAsync(window, toolbar);
        var entries = menu.Items.OfType<MenuItem>().ToArray();
        foreach (var (name, label, keys) in new[]
        {
            ("MoreRectangle", Strings.ToolRectangle, "Ctrl+Shift+R"),
            ("MoreEllipse", Strings.ToolEllipse, "Ctrl+Shift+E"),
            ("MoreLine", Strings.ToolLine, "Ctrl+Shift+L"),
            ("MoreArrow", Strings.ToolArrow, "Ctrl+Shift+A"),
        })
        {
            var entry = entries.Single(e => e.Name == name);
            Assert.Equal(label, entry.Header);
            Assert.Equal(label, AutomationProperties.GetName(entry));
            Assert.Equal(MenuItemToggleType.CheckBox, entry.ToggleType);
            Assert.False(entry.IsChecked);
            Assert.Equal(keys, GestureText.Of(entry.InputGesture!));
            Assert.True(entry.IsEnabled);
        }

        // The style of the freehand tool is in the menu as well, nothing greyed out: the colours as swatches with their
        // names, the chosen one with a check mark (as the colours of the markups), the widths checked.
        foreach (var color in DrawPalette.All)
        {
            var entry = entries.Single(e => e.Name == "MoreDraw" + color);
            Assert.Equal(Strings.DrawColorName(color), entry.Header);
            Assert.Equal(Strings.DrawColorName(color), AutomationProperties.GetName(entry));
            var swatch = Assert.IsAssignableFrom<Panel>(entry.Icon);
            Assert.Equal(color == DrawColor.Red, swatch.Children.OfType<StrokeIcon>().Any());
            Assert.True(entry.IsEnabled);
        }

        foreach (var width in new[] { StrokeWidth.Thin, StrokeWidth.Medium, StrokeWidth.Thick })
        {
            var entry = entries.Single(e => e.Name == "MoreWidth" + width);
            Assert.Equal(Strings.StrokeWidthName(width), entry.Header);
            Assert.Equal(MenuItemToggleType.CheckBox, entry.ToggleType);
            Assert.Equal(width == StrokeWidth.Medium, entry.IsChecked);
            Assert.True(entry.IsEnabled);
        }

        // Choosing a width there changes the style of the tool.
        var thin = entries.Single(e => e.Name == "MoreWidthThin");
        thin.Command!.Execute(thin.CommandParameter);
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Thin), tab.StyleOf(AnnotationKind.Ink));
        menu.Hide();
        Settle(window);
    }

    private static IEnumerable<(string Name, string Label, string Keys, string Icon, Action Choose, Func<bool> IsOn)> Tools(TabViewModel tab) =>
    [
        ("SelectTool", Strings.ToolSelect, "Ctrl+Shift+M", "Pw.Icon.Pointer", () => tab.SelectToolCommand.Execute(null), () => tab.IsSelectTool),
        ("InkTool", Strings.ToolInk, "Ctrl+Shift+I", "Pw.Icon.Pencil", () => tab.SetToolCommand.Execute(AnnotationKind.Ink), () => tab.ActiveTool == AnnotationKind.Ink),
        ("RectangleTool", Strings.ToolRectangle, "Ctrl+Shift+R", "Pw.Icon.Square", () => tab.SetToolCommand.Execute(AnnotationKind.Rectangle), () => tab.ActiveTool == AnnotationKind.Rectangle),
        ("EllipseTool", Strings.ToolEllipse, "Ctrl+Shift+E", "Pw.Icon.Circle", () => tab.SetToolCommand.Execute(AnnotationKind.Ellipse), () => tab.ActiveTool == AnnotationKind.Ellipse),
        ("LineTool", Strings.ToolLine, "Ctrl+Shift+L", "Pw.Icon.Line", () => tab.SetToolCommand.Execute(AnnotationKind.Line), () => tab.ActiveTool == AnnotationKind.Line),
        ("ArrowTool", Strings.ToolArrow, "Ctrl+Shift+A", "Pw.Icon.ArrowUpRight", () => tab.SetToolCommand.Execute(AnnotationKind.Arrow), () => tab.ActiveTool == AnnotationKind.Arrow),
        ("TextBoxTool", Strings.ToolTextBox, "Ctrl+Shift+T", "Pw.Icon.Typography", () => tab.SetToolCommand.Execute(AnnotationKind.TextBox), () => tab.ActiveTool == AnnotationKind.TextBox),
        ("HighlightTool", Strings.CommandHighlight, "Ctrl+Shift+H", "Pw.Icon.Highlight", () => tab.SetToolCommand.Execute(AnnotationKind.Highlight), () => tab.ActiveTool == AnnotationKind.Highlight),
        ("UnderlineTool", Strings.CommandUnderline, "Ctrl+Shift+U", "Pw.Icon.Underline", () => tab.SetToolCommand.Execute(AnnotationKind.Underline), () => tab.ActiveTool == AnnotationKind.Underline),
        ("StrikeOutTool", Strings.CommandStrikeOut, "Ctrl+Shift+X", "Pw.Icon.Strikethrough", () => tab.SetToolCommand.Execute(AnnotationKind.StrikeOut), () => tab.ActiveTool == AnnotationKind.StrikeOut),
    ];

    /// <summary>The narrowest document area with a panel docked beside it: a 900 px window with the widest panel (448 px).</summary>
    private static async Task NarrowestAsync(MainWindow window)
    {
        var viewModel = (MainViewModel)window.DataContext!;
        window.Width = 900;
        Settle(window);
        viewModel.PanelWidth = MainViewModel.MaxPanelWidth;
        if (viewModel.ActiveTab?.SidePanel != SidePanelKind.Comments)
        {
            viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Comments);
        }

        await WaitUntilAsync(window, () => !viewModel.IsNarrowWindow && Find<Control>(window, "PanelHost").IsEffectivelyVisible);
        Settle(window);
        Settle(window);
    }

    private static async Task<MenuFlyout> OpenMoreAsync(MainWindow window, AnnotationToolbar toolbar)
    {
        var more = Find<Button>(toolbar, "MoreButton");
        Assert.True(more.IsEffectivelyVisible);
        more.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);
        var menu = Assert.IsType<MenuFlyout>(more.Flyout);
        await WaitUntilAsync(window, () => menu.IsOpen);
        return menu;
    }

    /// <summary>Makes a drawing on page 1 through the tab, as a stroke of a drawing tool would, and returns it as read.</summary>
    private static async Task<PdfAnnotation> DrawAsync(MainWindow window, TabViewModel tab, DrawingGeometry geometry)
    {
        var before = (await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Select(a => a.Name).ToHashSet();
        await tab.ApplyDrawingAsync(0, geometry);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        return (await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Single(a => !before.Contains(a.Name));
    }

    /// <summary>Selects the annotation at <paramref name="point"/> of page 1 with the select tool.</summary>
    private static async Task SelectAsync(MainWindow window, TabViewModel tab, DisplayPoint point, string? name)
    {
        if (!tab.IsSelectTool)
        {
            tab.SelectToolCommand.Execute(null);
        }

        await tab.SelectAtAsync(0, point, 4);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == name);
        Settle(window);
    }

    private static async Task<PdfAnnotation> WaitForAsync(MainWindow window, TabViewModel tab, string? name, Func<PdfAnnotation, bool> condition)
    {
        PdfAnnotation? found = null;
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (!tab.HasEditsInFlight && tab.Session.Document is { } document)
            {
                found = (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).SingleOrDefault(a => a.Name == name);
                if (found is not null && condition(found))
                {
                    return found;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The annotation {name} did not change as expected: {found}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Every control of the toolbar is either whole inside it (not under "Done", not clipped) and on the way of Tab, or an
    /// entry of the menu "⋯", which Tab reaches and Enter opens.
    /// </summary>
    private static async Task AssertEverythingReachableAsync(MainWindow window, AnnotationToolbar toolbar)
    {
        var area = Find<StackPanel>(toolbar, "ToolsArea");
        var areaRight = area.TranslatePoint(new Point(area.Bounds.Width, 0), toolbar)!.Value.X;
        var done = Find<Button>(toolbar, "DoneButton");
        var doneLeft = done.TranslatePoint(default, toolbar)!.Value.X;
        Assert.True(done.TranslatePoint(new Point(done.Bounds.Width, 0), toolbar)!.Value.X <= toolbar.Bounds.Width, "Done is whole");
        Assert.True(areaRight <= doneLeft + 0.5, "the tools end before Done");
        var tab = (TabViewModel)toolbar.DataContext!;

        // Select, freehand and the three markups always stay in the row, and so does the tool that is on; a shape is in the
        // row or in the menu "⋯".
        var inRow = new List<Control>
        {
            Find<ActionToggleButton>(toolbar, "SelectTool"),
            Find<ActionToggleButton>(toolbar, "InkTool"),
            Find<ActionToggleButton>(toolbar, "HighlightTool"),
            Find<ActionToggleButton>(toolbar, "UnderlineTool"),
            Find<ActionToggleButton>(toolbar, "StrikeOutTool"),
        };
        var shapesInMenu = new List<AnnotationKind>();
        foreach (var shape in new[] { AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow })
        {
            if (toolbar.IsShapeInMenu(shape))
            {
                Assert.NotEqual(shape, tab.ActiveTool);
                shapesInMenu.Add(shape);
            }
            else
            {
                inRow.Add(Find<ActionToggleButton>(toolbar, shape + "Tool"));
            }
        }

        if (!toolbar.AreColorsInMenu && Find<StackPanel>(toolbar, "ColorGroup").IsVisible)
        {
            inRow.AddRange(AnnotationPalette.All.Select(color => (Control)toolbar.SwatchOf(color)));
        }

        if (!toolbar.AreColorsInMenu && Find<StackPanel>(toolbar, "DrawColorGroup").IsVisible)
        {
            inRow.AddRange(DrawPalette.All.Select(color => (Control)toolbar.DrawSwatchOf(color)));
        }

        if (!toolbar.IsUndoInMenu)
        {
            inRow.Add(Find<Button>(toolbar, "UndoButton"));
            inRow.Add(Find<Button>(toolbar, "RedoButton"));
        }

        foreach (var control in inRow)
        {
            Assert.True(control.IsEffectivelyVisible, $"{control.Name} is shown");
            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), toolbar)!.Value.X;
            Assert.True(right <= areaRight + 0.5, $"{control.Name} ends at {right}, the row at {areaRight}");
        }

        // Tab from the first tool reaches every tool of the row, the colours (once) when they are shown, the menu "⋯" when it
        // is there, and "Done" (undo and redo cannot run yet, so Tab skips them).
        var more = Find<Button>(toolbar, "MoreButton");
        Assert.Equal(toolbar.IsUndoInMenu, more.IsEffectivelyVisible);
        Find<ActionToggleButton>(toolbar, "SelectTool").Focus(NavigationMethod.Tab);
        Settle(window);
        var visited = new List<object?> { Focused(window) };
        for (var i = 0; i < 20 && !done.IsFocused; i++)
        {
            Press(window, Key.Tab, PhysicalKey.Tab);
            visited.Add(Focused(window));
        }

        Assert.True(done.IsFocused, "Tab reaches Done");
        foreach (var tool in inRow.OfType<ActionToggleButton>().Where(button => button.Name?.EndsWith("Tool", StringComparison.Ordinal) == true))
        {
            Assert.Contains(tool, visited);
        }

        if (!toolbar.AreColorsInMenu && (Find<StackPanel>(toolbar, "ColorGroup").IsVisible || Find<StackPanel>(toolbar, "DrawColorGroup").IsVisible))
        {
            Assert.Contains(visited, focused => focused is ActionToggleButton { Name: { } name } && name.Contains("Swatch", StringComparison.Ordinal));
        }

        if (!more.IsEffectivelyVisible)
        {
            return;
        }

        Assert.Contains(more, visited);
        Assert.Equal(Strings.AnnotateMore, AutomationProperties.GetName(more));
        more.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);
        var menu = Assert.IsType<MenuFlyout>(more.Flyout);
        await WaitUntilAsync(window, () => menu.IsOpen);
        var names = menu.Items.OfType<MenuItem>().Select(entry => (string)entry.Header!).ToArray();
        Assert.Contains(Strings.CommandUndo, names);
        Assert.Contains(Strings.CommandRedo, names);
        foreach (var shape in shapesInMenu)
        {
            Assert.Contains(menu.Items.OfType<MenuItem>(), entry => entry.Name == "More" + shape);
        }

        if (toolbar.AreColorsInMenu && tab.ActiveTool is { } markup && TabViewModel.IsMarkupKind(markup))
        {
            Assert.All(AnnotationPalette.All, color => Assert.Contains(Strings.ColorName(color), names));

            // A colour of the menu chooses it, as the swatch would.
            var green = menu.Items.OfType<MenuItem>().Single(entry => (string)entry.Header! == Strings.ColorName(MarkupColor.Green));
            green.Command!.Execute(green.CommandParameter);
            Assert.Equal(MarkupColor.Green, tab.ActiveColor);
        }

        menu.Hide();
        Settle(window);
    }

    private static ToggleState ToggleStateOf(Control control) =>
        ((IToggleProvider)ControlAutomationPeer.CreatePeerForElement(control).GetProvider<IToggleProvider>()!).ToggleState;

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control)
    {
        var point = CenterOf(window, control);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Settle(window);
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The window did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    /// <summary>
    /// A window with a test file (text-rich.pdf unless named) in the mode "Comment" (no tool), its toolbar shown; with
    /// <paramref name="copy"/> a copy in a temp folder, for tests that change it.
    /// </summary>
    private async Task<(MainWindow Window, TabViewModel Tab, AnnotationToolbar Toolbar, DocumentView View)> ShowInModeAsync(string fixture = "text-rich.pdf", bool copy = false)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = Find<DocumentView>(window, "DocumentView");

        // Without a copy only read: nothing is changed or saved.
        var path = TestData.Pdf(fixture);
        if (copy)
        {
            var folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-toolbar-" + Guid.NewGuid().ToString("N"))).FullName;
            // Removed last, after the window and the tab let go of the file.
            _cleanup.Insert(0, new FolderRemover(folder));
            var target = System.IO.Path.Combine(folder, fixture);
            File.Copy(path, target);
            path = target;
        }

        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        tab.BeginAnnotateCommand.Execute(null);
        AnnotationToolbar? toolbar = null;
        await WaitUntilAsync(window, () => (toolbar = window.GetVisualDescendants().OfType<AnnotationToolbar>().FirstOrDefault()) is { IsEffectivelyVisible: true });
        return (window, tab, toolbar!, view);
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }

    private sealed class FolderRemover(string folder) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A document may still hold a file for a moment; the system cleans the temp folder later.
            }
        }
    }
}
