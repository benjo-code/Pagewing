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
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
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
        var (window, _, toolbar, _) = await ShowInModeAsync();
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

        // At the smallest width of the window the name of the mode and "Done" stay whole inside the toolbar.
        window.Width = 640;
        Settle(window);
        Settle(window);
        Assert.False(Find<TextBlock>(toolbar, "EscHint").IsVisible);
        var done = Find<Button>(toolbar, "DoneButton");
        var name = Find<TextBlock>(toolbar, "ModeName");
        Assert.True(done.IsEffectivelyVisible && name.IsEffectivelyVisible);
        var doneRight = done.TranslatePoint(new Point(done.Bounds.Width, 0), toolbar)!.Value.X;
        Assert.True(doneRight <= toolbar.Bounds.Width, $"Done ends at {doneRight} of {toolbar.Bounds.Width}");
        var lastSwatch = toolbar.SwatchOf(MarkupColor.Orange);
        var swatchRight = lastSwatch.TranslatePoint(new Point(lastSwatch.Bounds.Width, 0), toolbar)!.Value.X;
        var doneLeft = done.TranslatePoint(default, toolbar)!.Value.X;
        Assert.True(swatchRight < doneLeft, $"the colours end at {swatchRight}, Done starts at {doneLeft}");
        Assert.False(Find<TextBlock>(toolbar, "HighlightLabel").IsVisible);
        Assert.Equal("Highlight (Ctrl+Shift+H)", ToolTip.GetTip(Find<ActionToggleButton>(toolbar, "HighlightTool")));

        // Wide again: everything is back.
        window.Width = 1200;
        Settle(window);
        Settle(window);
        Assert.True(Find<TextBlock>(toolbar, "EscHint").IsEffectivelyVisible);
        Assert.True(Find<TextBlock>(toolbar, "HighlightLabel").IsEffectivelyVisible);
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

    /// <summary>A window with text-rich.pdf in the mode "Comment" (no tool), its toolbar shown.</summary>
    private async Task<(MainWindow Window, TabViewModel Tab, AnnotationToolbar Toolbar, DocumentView View)> ShowInModeAsync()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = Find<DocumentView>(window, "DocumentView");

        // Only read: nothing is changed or saved.
        await viewModel.OpenAsync(TestData.Pdf("text-rich.pdf"));
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
}
