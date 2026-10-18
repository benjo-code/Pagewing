using Pagewing.Core.Palette;
using Xunit;

namespace Pagewing.Core.Tests.Palette;

public sealed class PaletteMatcherTests
{
    [Fact]
    public void EmptyQueryMatchesEverything()
    {
        Assert.Equal(0, PaletteMatcher.Score(string.Empty, "Zoom in"));
        Assert.Equal(0, PaletteMatcher.Score("   ", "Zoom in"));
        Assert.Equal(0, PaletteMatcher.Score(" \t ", string.Empty));
        Assert.Equal(0, PaletteMatcher.Score(string.Empty, string.Empty));
    }

    [Fact]
    public void AllWordsMustMatch()
    {
        Assert.True(PaletteMatcher.Score("zoom in", "Zoom in") > 0);
        Assert.True(PaletteMatcher.Score("in zoom", "Zoom in") > 0);
        Assert.True(PaletteMatcher.Score("  zoom   in ", "Zoom in") > 0);
        Assert.Equal(-1, PaletteMatcher.Score("zoom out", "Zoom in"));
        Assert.Equal(-1, PaletteMatcher.Score("zoom page", "Zoom in"));

        // Each word on its own may lie anywhere in the text, also inside a word.
        Assert.True(PaletteMatcher.Score("oo pag", "Zoom to the whole page") > 0);
    }

    [Fact]
    public void PrefixBeatsInnerMatch()
    {
        // Text start beats word start beats the middle of a word, for texts of the same length.
        var textStart = PaletteMatcher.Score("zoom", "Zoom out");
        var wordStart = PaletteMatcher.Score("zoom", "Fit zoom");
        var inner = PaletteMatcher.Score("zoom", "Autozoom");
        Assert.True(textStart > wordStart, $"{textStart} <= {wordStart}");
        Assert.True(wordStart > inner, $"{wordStart} <= {inner}");
        Assert.True(inner > 0);

        // The position wins over the length: a long text with the word at its start beats a short one with it inside.
        Assert.True(PaletteMatcher.Score("page", "Pages panel on the left side of the window") > PaletteMatcher.Score("page", "Subpage"));

        // A word start after punctuation counts too, and the best place of a word counts when it occurs twice.
        Assert.True(PaletteMatcher.Score("pdf", "report.pdf") > PaletteMatcher.Score("pdf", "reportpdfx"));
        Assert.Equal(PaletteMatcher.Score("zoom", "Autozoom zoom"), PaletteMatcher.Score("zoom", "Abcdefgh zoom"));
    }

    [Fact]
    public void CaseIsIgnored()
    {
        Assert.Equal(PaletteMatcher.Score("zoom in", "Zoom in"), PaletteMatcher.Score("ZOOM IN", "Zoom in"));
        Assert.Equal(PaletteMatcher.Score("zoom", "zoom in"), PaletteMatcher.Score("Zoom", "ZOOM IN"));
        Assert.True(PaletteMatcher.Score("über", "ÜBER Pagewing") > 0);
    }

    [Fact]
    public void UmlautsAreKept()
    {
        Assert.True(PaletteMatcher.Score("über", "Über Pagewing") > 0);
        Assert.True(PaletteMatcher.Score("größe", "Tatsächliche Größe") > 0);

        // No folding of diacritics or spellings: "uber" and "ueber" are other words.
        Assert.Equal(-1, PaletteMatcher.Score("uber", "Über Pagewing"));
        Assert.Equal(-1, PaletteMatcher.Score("ueber", "Über Pagewing"));
        Assert.Equal(-1, PaletteMatcher.Score("grosse", "Tatsächliche Größe"));
    }

    [Fact]
    public void ShorterTextWins()
    {
        Assert.True(PaletteMatcher.Score("page", "Page") > PaletteMatcher.Score("page", "Pages panel"));
        Assert.True(PaletteMatcher.Score("next", "Next tab") > PaletteMatcher.Score("next", "Next page in the document"));
        Assert.True(PaletteMatcher.Score("tab", "Close tab") > PaletteMatcher.Score("tab", "Close the tab"));
    }

    [Fact]
    public void NoMatchIsMinusOne()
    {
        Assert.Equal(-1, PaletteMatcher.Score("xyz", "Zoom in"));
        Assert.Equal(-1, PaletteMatcher.Score("a", string.Empty));
        Assert.Equal(-1, PaletteMatcher.Score("zoom in ", "Zoom"));
        Assert.Equal(-1, PaletteMatcher.Score("Zoomin", "Zoom in"));
    }
}
