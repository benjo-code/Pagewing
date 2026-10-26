using Pagewing.Core.Annotations;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>The line layout of a text box (<see cref="TextBoxLayout"/>), with made-up glyph widths (milestone 10).</summary>
public sealed class TextBoxLayoutTests
{
    private const char NoBreakSpace = (char)0xA0;

    private const char EnDash = (char)0x2013;

    // Every glyph 0.5 em wide, a space 0.25 em; at 10 pt a letter is 5 pt, a space 2.5 pt.
    private static readonly FakeMetrics Metrics = new();

    [Fact]
    public void WrapsAtSpacesToTheBoxWidth()
    {
        // 30 pt for the text (38 pt box less 2 x 4 pt padding): "aaa bbb" is 32.5 pt, so "bbb" goes to the next line.
        var layout = TextBoxLayout.Lay("aaa bbb ccc", 10, 38, Metrics);

        Assert.Equal(["aaa", "bbb", "ccc"], layout.Lines.Select(l => l.Text));
        Assert.All(layout.Lines, line => Assert.Equal(15, line.Width, 9));
    }

    [Fact]
    public void WordsThatFitStayOnOneLine()
    {
        // "aa bb" is 22.5 pt; the box leaves 30 pt.
        var layout = TextBoxLayout.Lay("aa bb", 10, 38, Metrics);

        var line = Assert.Single(layout.Lines);
        Assert.Equal("aa bb", line.Text);
        Assert.Equal(22.5, line.Width, 9);
    }

    [Fact]
    public void ExplicitLineBreaksStartNewLinesAndKeepEmptyOnes()
    {
        var layout = TextBoxLayout.Lay("a\n\nb", 10, 100, Metrics);

        Assert.Equal(["a", string.Empty, "b"], layout.Lines.Select(l => l.Text));
    }

    [Fact]
    public void ALongWordBreaksBetweenCharacters()
    {
        // Six letters fit into 30 pt.
        var layout = TextBoxLayout.Lay("abcdefghijklmn", 10, 38, Metrics);

        Assert.Equal(["abcdef", "ghijkl", "mn"], layout.Lines.Select(l => l.Text));
    }

    [Fact]
    public void TrailingSpacesHangAndDoNotStartTheNextLine()
    {
        // "aaaaaa" fills the 30 pt exactly; the spaces after it hang, "b" starts the next line.
        var layout = TextBoxLayout.Lay("aaaaaa   b", 10, 38, Metrics);

        Assert.Equal(["aaaaaa", "b"], layout.Lines.Select(l => l.Text));
        Assert.Equal(30, layout.Lines[0].Width, 9);
    }

    [Fact]
    public void LeadingSpacesOfAParagraphStay()
    {
        var layout = TextBoxLayout.Lay("  a", 10, 100, Metrics);

        var line = Assert.Single(layout.Lines);
        Assert.Equal("  a", line.Text);
        Assert.Equal(10, line.Width, 9);
    }

    [Fact]
    public void BreaksAfterAHyphenOrDash()
    {
        // "aaa-bbb" is 35 pt and does not fit into 30 pt; it breaks after the hyphen, keeping it on the first line.
        Assert.Equal(["aaa-", "bbb"], TextBoxLayout.Lay("aaa-bbb", 10, 38, Metrics).Lines.Select(l => l.Text));
        var dash = EnDash.ToString();
        Assert.Equal(["aaa" + dash, "bbb"], TextBoxLayout.Lay("aaa" + dash + "bbb", 10, 38, Metrics).Lines.Select(l => l.Text));
    }

    [Fact]
    public void ANonBreakingSpaceDoesNotBreak()
    {
        var space = NoBreakSpace.ToString();
        var layout = TextBoxLayout.Lay("aaa" + space + "bbb", 10, 38, Metrics);

        Assert.Equal(["aaa" + space + "bb", "b"], layout.Lines.Select(l => l.Text));
    }

    [Fact]
    public void LinesStartAtThePaddingAndFollowTheLineHeight()
    {
        // Ascent 0.8 em and descent 0.2 em: at 10 pt the first baseline is 4 + 8 pt down, each line 10 pt below the last.
        var layout = TextBoxLayout.Lay("a\nb\nc", 10, 100, Metrics);

        Assert.All(layout.Lines, line => Assert.Equal(TextBoxLayout.Padding, line.X));
        Assert.Equal([12d, 22d, 32d], layout.Lines.Select(l => l.Baseline));
        Assert.Equal(10, layout.LineHeight, 9);
        Assert.Equal((2 * TextBoxLayout.Padding) + 30, layout.ContentHeight, 9);
    }

    [Fact]
    public void EmptyTextHasOneEmptyLine()
    {
        var layout = TextBoxLayout.Lay(string.Empty, 10, 100, Metrics);

        var line = Assert.Single(layout.Lines);
        Assert.Equal(string.Empty, line.Text);
        Assert.Equal((2 * TextBoxLayout.Padding) + 10, layout.ContentHeight, 9);
    }

    [Fact]
    public void ATinyBoxStillPutsOneCharacterOnEachLine()
    {
        // No room inside the padding at all: every line still takes one glyph, so the layout always ends.
        var layout = TextBoxLayout.Lay("abc", 10, 4, Metrics);

        Assert.Equal(["a", "b", "c"], layout.Lines.Select(l => l.Text));
    }

    [Fact]
    public void CarriageReturnsCountAsLineBreaks()
    {
        Assert.Equal(["a", "b", "c"], TextBoxLayout.Lay("a\r\nb\rc", 10, 100, Metrics).Lines.Select(l => l.Text));
    }

    [Fact]
    public void RefusesSizesThatAreNotPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoxLayout.Lay("a", 0, 100, Metrics));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoxLayout.Lay("a", double.NaN, 100, Metrics));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoxLayout.Lay("a", 10, double.PositiveInfinity, Metrics));
    }

    private sealed class FakeMetrics : IGlyphMetrics
    {
        public double Ascent => 0.8;

        public double Descent => 0.2;

        public double AdvanceOf(char character) => character is ' ' or NoBreakSpace ? 0.25 : 0.5;
    }
}
