using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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

    private static ThemeVariant Variant(string theme) => theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static Window Show(Window window, ThemeVariant variant)
    {
        window.RequestedThemeVariant = variant;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
