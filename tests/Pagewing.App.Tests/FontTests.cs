using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class FontTests
{
    [AvaloniaTheory]
    [InlineData("Pw.Font.Sans", "Geist", 400)]
    [InlineData("Pw.Font.Sans", "Geist", 500)]
    [InlineData("Pw.Font.Sans", "Geist", 600)]
    [InlineData("Pw.Font.Mono", "Geist Mono", 400)]
    [InlineData("Pw.Font.Mono", "Geist Mono", 500)]
    public void EmbeddedFontFacesResolveWithTheirOwnWeight(string resource, string family, int weight)
    {
        var fontFamily = Assert.IsType<FontFamily>(TokenTests.Resource(resource, ThemeVariant.Light));
        var typeface = new Typeface(fontFamily, FontStyle.Normal, (FontWeight)weight);

        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));
        var actualFamily = string.IsNullOrEmpty(glyphTypeface.TypographicFamilyName) ? glyphTypeface.FamilyName : glyphTypeface.TypographicFamilyName;
        Assert.Equal(family, actualFamily);
        Assert.Equal((FontWeight)weight, glyphTypeface.Weight);
        Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph('ß', out var sharpS) && sharpS != 0, "ß is part of the subset");
        Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph('ẞ', out var capitalSharpS) && capitalSharpS != 0, "ẞ is part of the subset");
        Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph('€', out var euro) && euro != 0, "€ is part of the subset");
    }

    [AvaloniaFact]
    public void WindowsUseTheSansFontFromTheTokens()
    {
        var window = new Window { RequestedThemeVariant = ThemeVariant.Light };
        window.Show();

        Assert.Equal(TokenTests.Resource("Pw.Font.Sans", ThemeVariant.Light), window.FontFamily);
        Assert.Equal(13, window.FontSize);
        window.Close();
    }

    [AvaloniaFact]
    public void TabularFiguresGiveDigitsTheSameWidth()
    {
        var proportional = MeasureWidth(new TextBlock { Text = "1111" });
        var proportionalWide = MeasureWidth(new TextBlock { Text = "8888" });
        var tabular = MeasureWidth(Numeric("1111"));
        var tabularWide = MeasureWidth(Numeric("8888"));

        Assert.NotEqual(proportional, proportionalWide);
        Assert.Equal(tabularWide, tabular);
    }

    private static TextBlock Numeric(string text)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add("num");
        return block;
    }

    private static double MeasureWidth(TextBlock block)
    {
        var window = new Window { Content = block, RequestedThemeVariant = ThemeVariant.Light };
        window.Show();
        block.Measure(Size.Infinity);
        var width = block.DesiredSize.Width;
        window.Close();
        return width;
    }
}
