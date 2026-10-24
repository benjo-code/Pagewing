using Pagewing.Core.Annotations;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class AnnotationPaletteTests
{
    [Fact]
    public void PaletteHasFiveDistinctNamedColors()
    {
        Assert.Equal(5, AnnotationPalette.All.Count);
        Assert.Equal(5, AnnotationPalette.All.Distinct().Count());
        Assert.Equal(5, AnnotationPalette.All.Select(AnnotationPalette.ColorOf).Distinct().Count());
        Assert.Equal(5, AnnotationPalette.All.Select(AnnotationPalette.NameKey).Distinct().Count());
        Assert.All(AnnotationPalette.All, c => Assert.False(string.IsNullOrWhiteSpace(AnnotationPalette.NameKey(c))));

        // The key is the name of the accessor of the UI texts (a C# name, no dots).
        Assert.Equal("MarkupColorYellow", AnnotationPalette.NameKey(MarkupColor.Yellow));
        Assert.Equal("MarkupColorOrange", AnnotationPalette.NameKey(MarkupColor.Orange));
    }

    [Fact]
    public void MatchFindsExactlyThePaletteColors()
    {
        foreach (var color in AnnotationPalette.All)
        {
            Assert.Equal(color, AnnotationPalette.Match(AnnotationPalette.ColorOf(color)));
        }

        Assert.Null(AnnotationPalette.Match(new AnnotationColor(1, 2, 3)));
    }

    [Fact]
    public void AnUndefinedColorIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AnnotationPalette.ColorOf((MarkupColor)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnnotationPalette.NameKey((MarkupColor)(-1)));
    }
}
