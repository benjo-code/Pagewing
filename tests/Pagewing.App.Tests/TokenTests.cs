using System.Globalization;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class TokenTests
{
    /// <summary>Every colour token of Docs/superpowers/specs/2026-10-17-designsystem.md, section 5.1 (CSS notation, #RRGGBB or #RRGGBBAA).</summary>
    private static readonly (string Name, string Light, string Dark)[] Spec =
    [
        ("Surface.Window", "#EEEFF2", "#15171B"),
        ("Surface.Toolbar", "#F7F8FA", "#1B1E23"),
        ("Surface.Sidebar", "#F7F8FA", "#1B1E23"),
        ("Surface.Canvas", "#E3E5EA", "#0F1114"),
        ("Surface.Raised", "#FFFFFF", "#24272E"),
        ("Surface.Hover", "#E3E5EA", "#2A2E36"),
        ("Accent.Subtle", "#E2E5F8", "#262B52"),
        ("Border.Subtle", "#DCDFE5", "#2A2E36"),
        ("Border.Strong", "#7F848E", "#6E7480"),
        ("Text.Primary", "#16181D", "#E6E8EB"),
        ("Text.Secondary", "#4F545E", "#9BA1AC"),
        ("Text.Disabled", "#9399A3", "#5E636D"),
        ("Accent.Default", "#3B48C4", "#9DA7FF"),
        ("Accent.On", "#FFFFFF", "#121539"),
        ("Focus.Ring", "#3340B4", "#B4BCFF"),
        ("Page.Border", "#00000014", "#FFFFFF24"),
        ("Page.Shadow", "#0000001A", "#00000099"),
        ("Search.Hit", "#FBE38A", "#FBE38A"),
        ("Search.Current", "#F5A524", "#F5A524"),
        ("Search.On", "#16181D", "#16181D"),
        ("Danger.Default", "#B42318", "#FF8A80"),
    ];

    private static readonly string[] Surfaces =
        ["Surface.Window", "Surface.Toolbar", "Surface.Sidebar", "Surface.Canvas", "Surface.Raised", "Surface.Hover"];

    private static readonly string[] TextOnAccentSurfaces = ["Surface.Window", "Surface.Toolbar", "Surface.Sidebar", "Surface.Raised"];

    private static readonly string[] BorderSurfaces = ["Surface.Window", "Surface.Toolbar", "Surface.Raised"];

    public static TheoryData<string> Themes => ["Light", "Dark"];

    [AvaloniaFact]
    public void EveryTokenOfTheSpecExistsInBothThemes()
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var (name, _, _) in Spec)
            {
                var color = Assert.IsType<Color>(Resource($"Pw.Color.{name}", variant));
                var brush = Assert.IsType<SolidColorBrush>(Resource($"Pw.Brush.{name}", variant));
                Assert.Equal(color, brush.Color);
            }
        }
    }

    [AvaloniaFact]
    public void TokenValuesMatchTheSpec()
    {
        // Compares every token against the spec value; the spec writes alpha last (#RRGGBBAA), Avalonia reads it first.
        foreach (var (name, light, dark) in Spec)
        {
            Assert.Equal(FromCss(light), Token(name, ThemeVariant.Light));
            Assert.Equal(FromCss(dark), Token(name, ThemeVariant.Dark));
        }

        Assert.Equal(Color.FromRgb(0xEE, 0xEF, 0xF2), Token("Surface.Window", ThemeVariant.Light));
        Assert.Equal(Color.FromRgb(0x15, 0x17, 0x1B), Token("Surface.Window", ThemeVariant.Dark));
        Assert.Equal(Color.FromRgb(0x3B, 0x48, 0xC4), Token("Accent.Default", ThemeVariant.Light));
        Assert.Equal(Color.FromRgb(0x9D, 0xA7, 0xFF), Token("Accent.Default", ThemeVariant.Dark));
        Assert.Equal(Color.FromRgb(0xFB, 0xE3, 0x8A), Token("Search.Hit", ThemeVariant.Light));
        Assert.Equal(Color.FromRgb(0xFB, 0xE3, 0x8A), Token("Search.Hit", ThemeVariant.Dark));
    }

    [Fact]
    public void CssColourConversionPutsAlphaFirst()
    {
        Assert.Equal(Color.FromArgb(0x14, 0x00, 0x00, 0x00), FromCss("#00000014"));
        Assert.Equal(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), FromCss("#FFFFFF24"));
        Assert.Equal(Color.FromArgb(0xFF, 0x3B, 0x48, 0xC4), FromCss("#3B48C4"));
    }

    [AvaloniaFact]
    public void AlphaTokensKeepTheirAlphaInBothThemes()
    {
        // page.border light is black at alpha 0x14 (CSS #00000014); read as #AARRGGBB it would be an opaque blue-black.
        var lightBorder = Token("Page.Border", ThemeVariant.Light);
        Assert.Equal((byte)0x14, lightBorder.A);
        Assert.Equal((byte)0, lightBorder.R);
        Assert.Equal((byte)0, lightBorder.G);
        Assert.Equal((byte)0, lightBorder.B);

        var darkBorder = Token("Page.Border", ThemeVariant.Dark);
        Assert.Equal((byte)0x24, darkBorder.A);
        Assert.Equal((byte)0xFF, darkBorder.R);

        Assert.Equal((byte)0x1A, Token("Page.Shadow", ThemeVariant.Light).A);
        Assert.Equal((byte)0x99, Token("Page.Shadow", ThemeVariant.Dark).A);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void TextAndAccentContrastMeetsAA(string theme)
    {
        var variant = Variant(theme);
        const double minimum = 4.5;
        var pairs = new List<(string Foreground, string Background)>();

        // text.primary on all surfaces, text.secondary on all surfaces except the canvas.
        pairs.AddRange(Surfaces.Select(surface => ("Text.Primary", surface)));
        pairs.AddRange(Surfaces.Where(surface => surface != "Surface.Canvas").Select(surface => ("Text.Secondary", surface)));

        // accent.default as text on window, toolbar, sidebar and raised.
        pairs.AddRange(TextOnAccentSurfaces.Select(surface => ("Accent.Default", surface)));
        pairs.Add(("Accent.On", "Accent.Default"));
        pairs.Add(("Danger.Default", "Surface.Raised"));
        pairs.Add(("Search.On", "Search.Hit"));
        pairs.Add(("Search.On", "Search.Current"));

        foreach (var (foreground, background) in pairs)
        {
            var ratio = Wcag.Contrast(Token(foreground, variant), Token(background, variant));
            Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:F2}:1, expected at least {minimum}:1");
        }

        // Ratios quoted in the spec.
        Assert.InRange(Wcag.Contrast(Token("Search.On", variant), Token("Search.Hit", variant)), 13.85, 13.95);
        Assert.InRange(Wcag.Contrast(Token("Search.On", variant), Token("Search.Current", variant)), 8.65, 8.75);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void BordersAndFocusContrastMeets3To1(string theme)
    {
        var variant = Variant(theme);
        const double minimum = 3.0;
        var pairs = new List<(string Foreground, string Background)>();

        pairs.AddRange(BorderSurfaces.Select(surface => ("Border.Strong", surface)));
        pairs.AddRange(Surfaces.Select(surface => ("Focus.Ring", surface)));
        pairs.Add(("Accent.Default", "Accent.Subtle"));

        foreach (var (foreground, background) in pairs)
        {
            var ratio = Wcag.Contrast(Token(foreground, variant), Token(background, variant));
            Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:F2}:1, expected at least {minimum}:1");
        }

        if (variant == ThemeVariant.Dark)
        {
            // The spec names border.strong on surface.raised (dark) at 3.18:1 as the tightest pair.
            Assert.InRange(Wcag.Contrast(Token("Border.Strong", variant), Token("Surface.Raised", variant)), 3.17, 3.19);
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void AccentIsNeverYellow(string theme)
    {
        var variant = Variant(theme);

        foreach (var name in new[] { "Accent.Default", "Accent.Subtle", "Accent.On", "Focus.Ring" })
        {
            var hue = new HsvColor(Token(name, variant)).H;
            Assert.False(hue is >= 40 and <= 70, $"{theme}: {name} has hue {hue:F0} degrees, which is yellow");
        }

        Assert.NotEqual(Token("Search.Hit", variant), Token("Accent.Default", variant));
        Assert.NotEqual(Token("Search.Current", variant), Token("Accent.Default", variant));
    }

    [AvaloniaFact]
    public void FluentPaletteMapsTheSpecRoles()
    {
        var fluent = Application.Current!.Styles.OfType<FluentTheme>().Single();

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var palette = fluent.Palettes[variant];
            Assert.Equal(Token("Accent.Default", variant), palette.Accent);
            Assert.Equal(Token("Surface.Window", variant), palette.RegionColor);
            Assert.Equal(Token("Text.Primary", variant), palette.BaseHigh);
            Assert.Equal(Token("Text.Secondary", variant), palette.BaseMedium);
            Assert.Equal(Token("Danger.Default", variant), palette.ErrorText);
        }
    }

    [AvaloniaFact]
    public void MetricsMatchTheSpec()
    {
        double[] spaces = [2, 4, 8, 12, 16, 24, 32];
        for (var i = 0; i < spaces.Length; i++)
        {
            Assert.Equal(spaces[i], Assert.IsType<double>(Resource($"Pw.Space.{i}", ThemeVariant.Light)));
        }

        Assert.Equal(new CornerRadius(6), Assert.IsType<CornerRadius>(Resource("Pw.Radius.Control", ThemeVariant.Light)));
        Assert.Equal(new CornerRadius(10), Assert.IsType<CornerRadius>(Resource("Pw.Radius.Overlay", ThemeVariant.Light)));
        Assert.Equal(new CornerRadius(8, 8, 0, 0), Assert.IsType<CornerRadius>(Resource("Pw.Radius.Tab", ThemeVariant.Light)));
    }

    [AvaloniaFact]
    public void ShadowTokensExistInBothThemes()
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.IsType<BoxShadows>(Resource("Pw.Shadow.Page", variant));
            Assert.IsType<BoxShadows>(Resource("Pw.Shadow.Card", variant));
        }
    }

    internal static object Resource(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), $"Resource {key} ({variant}) is missing");
        return value!;
    }

    internal static Color Token(string name, ThemeVariant variant) =>
        Assert.IsType<Color>(Resource($"Pw.Color.{name}", variant));

    private static ThemeVariant Variant(string theme) => theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>Parses the CSS notation of the spec (#RRGGBB or #RRGGBBAA, alpha last) into an Avalonia colour.</summary>
    private static Color FromCss(string css)
    {
        Assert.StartsWith("#", css, StringComparison.Ordinal);
        var digits = css[1..];
        var r = byte.Parse(digits[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(digits[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var a = digits.Length == 8 ? byte.Parse(digits[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)255;
        return Color.FromArgb(a, r, g, b);
    }
}
