using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class ControlStyleTests
{
    public static TheoryData<string> Themes => ["Light", "Dark"];

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void WindowAndTextUseTheThemeTokens(string theme)
    {
        var variant = Variant(theme);
        var text = new TextBlock { Text = "Text" };
        var window = Show(new Window { Content = text }, variant);

        Assert.Equal(TokenTests.Token("Surface.Window", variant), Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
        Assert.Equal(TokenTests.Token("Text.Primary", variant), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void PrimaryButtonUsesAccentAndSecondaryUsesBorderStrong(string theme)
    {
        var variant = Variant(theme);
        var primary = new Button { Content = "Open" };
        primary.Classes.Add("primary");
        var secondary = new Button { Content = "Cancel" };
        var window = Show(new Window { Content = new StackPanel { Children = { primary, secondary } } }, variant);

        Assert.Equal(TokenTests.Token("Accent.Default", variant), Assert.IsAssignableFrom<ISolidColorBrush>(primary.Background).Color);
        Assert.Equal(TokenTests.Token("Accent.On", variant), Assert.IsAssignableFrom<ISolidColorBrush>(primary.Foreground).Color);

        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Assert.IsAssignableFrom<ISolidColorBrush>(secondary.Background).Color);
        Assert.Equal(TokenTests.Token("Border.Strong", variant), Assert.IsAssignableFrom<ISolidColorBrush>(secondary.BorderBrush).Color);
        Assert.Equal(new Thickness(1), secondary.BorderThickness);
        Assert.Equal(new CornerRadius(6), secondary.CornerRadius);
        window.Close();
    }

    [AvaloniaFact]
    public void IconButtonIsFlatAndFollowsTheForegroundWithItsIcon()
    {
        var icon = new StrokeIcon { Data = (Geometry)TokenTests.Resource("Pw.Icon.Plus", ThemeVariant.Light), Width = 18, Height = 18 };
        var button = new Button { Content = icon };
        button.Classes.Add("icon");
        var window = Show(new Window { Content = button }, ThemeVariant.Light);

        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
        Assert.Equal(new Thickness(0), button.BorderThickness);
        Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 32);
        window.Close();
    }

    [AvaloniaFact]
    public void TextBoxUsesRaisedSurfaceAndStrongBorder()
    {
        var box = new TextBox();
        var window = Show(new Window { Content = box }, ThemeVariant.Dark);

        Assert.Equal(TokenTests.Token("Surface.Raised", ThemeVariant.Dark), Assert.IsAssignableFrom<ISolidColorBrush>(box.Background).Color);
        Assert.Equal(TokenTests.Token("Border.Strong", ThemeVariant.Dark), Assert.IsAssignableFrom<ISolidColorBrush>(box.BorderBrush).Color);
        Assert.Equal(new CornerRadius(6), box.CornerRadius);
        Assert.NotNull(box.FocusAdorner);
        window.Close();
    }

    [AvaloniaFact]
    public void ToolTipUsesRaisedSurfaceAndSubtleBorder()
    {
        var tip = new ToolTip { Content = "Open file (Ctrl+O)" };
        var window = Show(new Window { Content = new Border { Child = tip } }, ThemeVariant.Light);

        Assert.Equal(TokenTests.Token("Surface.Raised", ThemeVariant.Light), Assert.IsAssignableFrom<ISolidColorBrush>(tip.Background).Color);
        Assert.Equal(TokenTests.Token("Border.Subtle", ThemeVariant.Light), Assert.IsAssignableFrom<ISolidColorBrush>(tip.BorderBrush).Color);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("title", 22, 28, 600)]
    [InlineData("subtitle", 16, 22, 600)]
    [InlineData("strong", 13, 18, 600)]
    [InlineData("body", 13, 18, 400)]
    [InlineData("caption", 12, 16, 400)]
    [InlineData("mono", 11.5, 16, 400)]
    public void TypeRolesMatchTheSpec(string role, double size, double lineHeight, int weight)
    {
        var text = new TextBlock { Text = "Text" };
        text.Classes.Add(role);
        var window = Show(new Window { Content = text }, ThemeVariant.Light);

        Assert.Equal(size, text.FontSize);
        Assert.Equal(lineHeight, text.LineHeight);
        Assert.Equal((FontWeight)weight, text.FontWeight);
        var expectedFamily = TokenTests.Resource(role == "mono" ? "Pw.Font.Mono" : "Pw.Font.Sans", ThemeVariant.Light);
        Assert.Equal(expectedFamily, text.FontFamily);
        window.Close();
    }

    public static TheoryData<string, string, string> PrimaryStates()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            foreach (var surface in new[] { "Window", "Toolbar", "Sidebar", "Canvas", "Raised" })
            {
                foreach (var state in new[] { "Hover", "Pressed" })
                {
                    data.Add(theme, surface, state);
                }
            }
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(PrimaryStates))]
    public void PrimaryButtonStatesKeepAAContrastOnEverySurface(string theme, string surface, string state)
    {
        var variant = Variant(theme);
        var opacity = Assert.IsType<double>(TokenTests.Resource($"Pw.Opacity.Primary{state}", variant));
        var behind = TokenTests.Token($"Surface.{surface}", variant);

        // Opacity is set on the content presenter, so background and text both fade into the surface.
        var background = Blend(TokenTests.Token("Accent.Default", variant), behind, opacity);
        var text = Blend(TokenTests.Token("Accent.On", variant), behind, opacity);

        var ratio = Wcag.Contrast(background, text);
        Assert.True(ratio >= 4.5, $"{theme}/{surface}/{state} at opacity {opacity}: {ratio:F2}:1");
        Assert.True(opacity < 1, "the state must differ from the resting button");
    }

    [AvaloniaFact]
    public void FluentCornerRadiiFollowTheMetrics()
    {
        Assert.Equal(new CornerRadius(6), Assert.IsType<CornerRadius>(TokenTests.Resource("ControlCornerRadius", ThemeVariant.Light)));
        Assert.Equal(new CornerRadius(10), Assert.IsType<CornerRadius>(TokenTests.Resource("OverlayCornerRadius", ThemeVariant.Light)));
    }

    [AvaloniaFact]
    public void FocusRingShowsOnlyForKeyboardFocus()
    {
        var button = new Button { Content = "Focus", Margin = new Thickness(40), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var other = new TextBox { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        var window = Show(new Window { Width = 240, Height = 160, Content = new Panel { Children = { button, other } } }, ThemeVariant.Light);
        var origin = button.TranslatePoint(default, window)!.Value;
        var left = (int)Math.Round(origin.X);
        var middle = (int)Math.Round(origin.Y + (button.Bounds.Height / 2));
        var ring = TokenTests.Token("Focus.Ring", ThemeVariant.Light);
        var background = TokenTests.Token("Surface.Window", ThemeVariant.Light);

        button.Focus(NavigationMethod.Pointer);
        Dispatcher.UIThread.RunJobs();
        using (var pointerFrame = new FramePixels(window.CaptureRenderedFrame()!))
        {
            Assert.Equal(background, pointerFrame.GetColor(left - 2, middle));
        }

        other.Focus();
        button.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        using (var keyboardFrame = new FramePixels(window.CaptureRenderedFrame()!))
        {
            // 2 px ring from 3 to 1 px outside the button, with a 1 px gap that keeps the window colour.
            Assert.Equal(ring, keyboardFrame.GetColor(left - 2, middle));
            Assert.Equal(ring, keyboardFrame.GetColor(left - 3, middle));
            Assert.Equal(background, keyboardFrame.GetColor(left - 1, middle));
            Assert.Equal(background, keyboardFrame.GetColor(left - 5, middle));
        }

        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void MenusUseTheRaisedSurfaceASubtleBorderAndTheOverlayRadius(string theme)
    {
        var variant = Variant(theme);
        var (window, button, menu) = ShowMenu(variant);

        Click(window, button);
        var presenter = Assert.Single(window.GetVisualDescendants().OfType<MenuFlyoutPresenter>());
        AssertMenuSurface(presenter, variant);
        var entry = menu.Items.OfType<MenuItem>().First();
        Assert.Equal(TokenTests.Token("Text.Primary", variant), Assert.IsAssignableFrom<ISolidColorBrush>(entry.Foreground).Color);

        // The entry the menu selected when it opened has surface.hover, and no ring: it was opened with the pointer.
        Assert.True(entry.IsSelected);
        var root = LayoutRoot(entry);
        Assert.Equal(TokenTests.Token("Surface.Hover", variant), Assert.IsAssignableFrom<ISolidColorBrush>(root.Background).Color);
        Assert.Equal(default, root.BorderThickness);
        menu.Hide();
        Dispatcher.UIThread.RunJobs();

        // The context menu looks the same.
        var context = new ContextMenu { ItemsSource = new[] { new MenuItem { Header = "Close" } } };
        context.Open(button);
        Dispatcher.UIThread.RunJobs();
        AssertMenuSurface(context, variant);
        context.Close();
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void MenuTextAndFocusRingKeepTheirContrastOnTheMenuSurfaces(string theme)
    {
        var variant = Variant(theme);
        foreach (var surface in new[] { "Surface.Raised", "Surface.Hover" })
        {
            var behind = TokenTests.Token(surface, variant);

            // Entries, and the shortcuts of the zoom menu (text.secondary).
            Assert.True(Wcag.Contrast(TokenTests.Token("Text.Primary", variant), behind) >= 4.5, $"text.primary on {surface}");
            Assert.True(Wcag.Contrast(TokenTests.Token("Text.Secondary", variant), behind) >= 4.5, $"text.secondary on {surface}");
            Assert.True(Wcag.Contrast(TokenTests.Token("Focus.Ring", variant), behind) >= 3, $"focus.ring on {surface}");
        }
    }

    [AvaloniaFact]
    public void MenuFocusRingShowsOnlyWhileTheKeyboardMovesInTheMenu()
    {
        MenuKeyboardFocus.EnsureRegistered();
        var variant = ThemeVariant.Light;
        var (window, button, menu) = ShowMenu(variant);
        var ring = TokenTests.Token("Focus.Ring", variant);
        var hover = TokenTests.Token("Surface.Hover", variant);

        // Opened with the pointer: the selected entry has no ring.
        Click(window, button);
        var entries = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(hover, EdgeColor(window, entries[0]));

        // An arrow key moves the selection: the ring shows on the selected entry, the text stays where it was.
        var textBefore = HeaderOrigin(window, entries[1]);
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.True(entries[1].IsSelected);
        Assert.Equal(ring, EdgeColor(window, entries[1]));
        Assert.Equal(new Thickness(2), LayoutRoot(entries[1]).BorderThickness);
        Assert.Equal(textBefore, HeaderOrigin(window, entries[1]));

        // The pointer moving over the menu takes the ring away.
        var presenter = Assert.Single(window.GetVisualDescendants().OfType<MenuFlyoutPresenter>());
        window.MouseMove(presenter.TranslatePoint(new Point(presenter.Bounds.Width / 2, 4), window)!.Value);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(MenuKeyboardFocus.KeyboardClass, presenter.Classes);
        Assert.Equal(default, LayoutRoot(entries[1]).BorderThickness);

        // A key press, then closing the menu: opened again with the pointer, it shows no ring.
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Contains(MenuKeyboardFocus.KeyboardClass, presenter.Classes);
        menu.Hide();
        Dispatcher.UIThread.RunJobs();
        Click(window, button);
        Assert.DoesNotContain(MenuKeyboardFocus.KeyboardClass, Assert.Single(window.GetVisualDescendants().OfType<MenuFlyoutPresenter>()).Classes);
        Assert.All(menu.Items.OfType<MenuItem>(), entry => Assert.Equal(default, LayoutRoot(entry).BorderThickness));
        window.Close();
    }

    private static (Window Window, Button Button, MenuFlyout Menu) ShowMenu(ThemeVariant variant)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.Bottom };
        foreach (var text in new[] { "Single page", "Continuous", "Two pages" })
        {
            menu.Items.Add(new MenuItem { Header = text });
        }

        var button = new Button { Content = "Layout", Flyout = menu, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var window = Show(new Window { Width = 400, Height = 300, Content = button }, variant);
        return (window, button, menu);
    }

    private static void AssertMenuSurface(TemplatedControl menu, ThemeVariant variant)
    {
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Assert.IsAssignableFrom<ISolidColorBrush>(menu.Background).Color);
        Assert.Equal(TokenTests.Token("Border.Subtle", variant), Assert.IsAssignableFrom<ISolidColorBrush>(menu.BorderBrush).Color);
        Assert.Equal(new Thickness(1), menu.BorderThickness);
        Assert.Equal(new CornerRadius(10), menu.CornerRadius);
    }

    private static Border LayoutRoot(MenuItem entry) =>
        entry.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot");

    private static Point HeaderOrigin(Window window, MenuItem entry) =>
        entry.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_HeaderPresenter").TranslatePoint(default, window)!.Value;

    /// <summary>The colour 1 px inside the left edge of an entry, halfway down (inside a 2 px ring).</summary>
    private static Color EdgeColor(Window window, MenuItem entry)
    {
        var point = entry.TranslatePoint(new Point(1, entry.Bounds.Height / 2), window)!.Value;
        using var frame = new FramePixels(window.CaptureRenderedFrame()!);
        return frame.GetColor((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey)
    {
        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static Color Blend(Color top, Color behind, double opacity) => Color.FromRgb(
        (byte)Math.Round((top.R * opacity) + (behind.R * (1 - opacity))),
        (byte)Math.Round((top.G * opacity) + (behind.G * (1 - opacity))),
        (byte)Math.Round((top.B * opacity) + (behind.B * (1 - opacity))));

    private static ThemeVariant Variant(string theme) => theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static Window Show(Window window, ThemeVariant variant)
    {
        window.RequestedThemeVariant = variant;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
