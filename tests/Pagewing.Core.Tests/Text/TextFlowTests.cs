using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Text;

public sealed class TextFlowTests
{
    [Fact]
    public void PlainJoinsLinesOfAParagraphAndKeepsParagraphs()
    {
        // Three tight lines (4 pt apart), a larger gap (14 pt), two tight lines.
        var page = Page(0, (20, 10, "Erste Zeile"), (34, 10, "zusammen"), (48, 10, "weiter."), (72, 10, "Zweiter Absatz"), (86, 10, "hier."));
        var slices = All(page);

        Assert.Equal("Erste Zeile zusammen weiter.\n\nZweiter Absatz hier.", TextFlow.Plain(slices));
    }

    [Fact]
    public void PlainMeasuresTheGapFromTheBaselineNotFromTightGlyphBoxes()
    {
        // Tight glyph boxes of 12 pt text, baselines 16 pt apart (em boxes 4 pt apart): the glyph boxes of the two
        // lines are 7 pt apart, more than 0.5 x 12, yet the lines belong to one paragraph. The descender of "g" must
        // not move the baseline.
        var chars = new List<TextChar>
        {
            new('x', new PageRect(10, 94, 16, 100), TextCharTraits.None, 12),
            new('g', new PageRect(16, 94, 22, 103), TextCharTraits.None, 12),
            new('x', new PageRect(22, 94, 28, 100), TextCharTraits.None, 12),
            new('\r', new PageRect(28, 100, 28, 100), TextCharTraits.Generated, 1),
            new('\n', new PageRect(28, 100, 28, 100), TextCharTraits.Generated, 1),
            new('a', new PageRect(10, 110, 16, 116), TextCharTraits.None, 12),
            new('\r', new PageRect(16, 116, 16, 116), TextCharTraits.Generated, 1),
            new('\n', new PageRect(16, 116, 16, 116), TextCharTraits.Generated, 1),
            new('b', new PageRect(10, 144, 16, 152), TextCharTraits.None, 12), // baseline 152: 36 pt below, a paragraph
        };
        var page = new PageText(0, 200, 200, chars, []);

        Assert.Equal("xgx a\n\nb", TextFlow.Plain(All(page)));
    }

    [Fact]
    public void PlainRemovesAHyphenAtTheLineEndBeforeALowercaseLetter()
    {
        // Like PDFium: the hyphen is the last char of its line and the next line follows with no line break.
        var page = Page(0, (20, 10, "Pagewing für Win-"), (34, 10, "dows und Linux."));

        Assert.Equal("Pagewing für Windows und Linux.", TextFlow.Plain(All(page)));
    }

    [Fact]
    public void PlainKeepsTheHyphenBeforeACapitalLetterAndAfterADigit()
    {
        var capital = Page(0, (20, 10, "Bindestrich-"), (34, 10, "Wort"));
        var digit = Page(0, (20, 10, "A1-"), (34, 10, "b"));

        Assert.Equal("Bindestrich- Wort", TextFlow.Plain(All(capital)));
        Assert.Equal("A1- b", TextFlow.Plain(All(digit)));
    }

    [Fact]
    public void PlainRemovesASoftHyphenOrAPlainHyphenBeforeALineBreak()
    {
        // Hyphens without the Hyphen trait, followed by a generated CR LF.
        var soft = Page(0, (20, 10, "Tren­|"), (34, 10, "nung"));
        var plain = Page(0, (20, 10, "Bin-|"), (34, 10, "dung"));

        Assert.Equal("Trennung", TextFlow.Plain(All(soft)));
        Assert.Equal("Bindung", TextFlow.Plain(All(plain)));
    }

    [Fact]
    public void PlainExpandsLigatures()
    {
        var page = Page(0, (20, 10, "ﬁve oﬃce ﬀ ﬂ ﬄ ﬅ ﬆ"));

        Assert.Equal("five office ff fl ffl st st", TextFlow.Plain(All(page)));
    }

    [Fact]
    public void PlainKeepsMultipleSpacesAsOne()
    {
        var chars = new List<TextChar>();
        AddText(chars, "a  b", 10, 20, 10);
        chars.Add(new TextChar(' ', new PageRect(30, 20, 35, 30), TextCharTraits.Generated, 1));
        AddText(chars, " c ", 35, 20, 10);
        var page = new PageText(0, 200, 200, chars, []);

        Assert.Equal("a b c", TextFlow.Plain(All(page)));
    }

    [Fact]
    public void RawKeepsLineBreaksAsNewlines()
    {
        var page = Page(0, (20, 10, "Hello"), (34, 10, "world"));
        var hyphenated = Page(0, (20, 10, "Win-"), (34, 10, "dows ﬁ"));

        Assert.Equal("Hello\nworld", TextFlow.Raw(All(page)));
        Assert.Equal("Win-\ndows ﬁ", TextFlow.Raw(All(hyphenated))); // nothing joined, nothing expanded
    }

    [Fact]
    public void RawSeparatesPagesAndSlicesByANewline()
    {
        var first = Page(0, (20, 10, "Ende der"));
        var second = Page(1, (20, 10, "Seite."));

        Assert.Equal("Ende der\nSeite.", TextFlow.Raw([new TextSlice(first, 0, first.Chars.Count), new TextSlice(second, 0, second.Chars.Count)]));
    }

    [Fact]
    public void HtmlMarksBoldItalicAndHeadings()
    {
        // The heading is 24 pt over a body of 12 pt and bold; "fett" is bold, "kursiv" italic.
        var page = Page(
            0,
            (20, 24, "*Titel*"),
            (60, 12, "Das ist *fett* und _kursiv_ und"),
            (76, 12, "*_beides_* hier."));

        Assert.Equal(
            "<h1>Titel</h1>\n<p>Das ist <b>fett</b> und <i>kursiv</i> und <b><i>beides</i></b> hier.</p>\n",
            TextFlow.Html(All(page)));
    }

    [Fact]
    public void HtmlSpacesKeepTheMarkupInsideARun()
    {
        // The spaces between the marked words are plain; they take the markup of the char before them, but a run
        // does not end with a space.
        var page = Page(0, (20, 12, "ein *sehr* *fetter* _und_ _schiefer_ Satz"));

        Assert.Equal("<p>ein <b>sehr fetter</b> <i>und schiefer</i> Satz</p>\n", TextFlow.Html(All(page)));
    }

    [Fact]
    public void HtmlChoosesTheHeadingLevelByTheRatioToTheBodySize()
    {
        var page = Page(
            0,
            (20, 16, "Zwei"), // 1.33 x 12
            (50, 14, "Drei"), // 1.17 x 12
            (80, 13, "Keine"), // 1.08 x 12
            (104, 12, "Text der Seite ist hier im Körper."),
            (120, 12, "Und noch mehr Text der Seite."));

        Assert.Equal(
            "<h2>Zwei</h2>\n<h3>Drei</h3>\n<p>Keine</p>\n<p>Text der Seite ist hier im Körper. Und noch mehr Text der Seite.</p>\n",
            TextFlow.Html(All(page)));
    }

    [Fact]
    public void HtmlTreatsALongLineOfLargeTextAsAParagraph()
    {
        var page = Page(0, (20, 24, new string('x', 101)), (60, 12, "Text Text Text Text Text Text Text Text Text Text Text Text Text Text Text Text Text Text Text Text Text"));

        Assert.StartsWith("<p>xxx", TextFlow.Html(All(page)), StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlEscapesMarkup()
    {
        var page = Page(0, (20, 10, "a < b & c > \"d\""));

        Assert.Equal("<p>a &lt; b &amp; c &gt; &quot;d&quot;</p>\n", TextFlow.Html(All(page)));
    }

    [Fact]
    public void SelectionAcrossPagesIsOneFlow()
    {
        // The last line of page 0 continues on page 1: joined as lines, not as paragraphs, although there is no gap to measure.
        var first = Page(0, (20, 10, "Oben."), (180, 10, "Der Satz geht"));
        var second = Page(1, (20, 10, "weiter. Ende."), (180, 10, "Fertig."));
        var third = Page(2, (20, 10, "Neu"));
        var pages = new[] { first, second, third };

        var slices = TextFlow.SlicesOf(new TextSelection(new TextPosition(0, 0), new TextPosition(2, third.Chars.Count)), p => pages[p]);

        Assert.Equal("Oben.\n\nDer Satz geht weiter. Ende.\n\nFertig.\n\nNeu", TextFlow.Plain(slices));
    }

    [Fact]
    public void SlicesOfDropsEmptyRangesAndSpansPages()
    {
        var pages = new[] { Page(0, (20, 10, "abc")), Page(1, (20, 10, "def")), Page(2, (20, 10, "ghi")), Page(3, (20, 10, "jkl")) };
        PageText PageOf(int index) => pages[index];

        // Backwards from page 3 caret 1 to page 0 caret 3: page 0 contributes nothing.
        var slices = TextFlow.SlicesOf(new TextSelection(new TextPosition(3, 1), new TextPosition(0, 3)), PageOf);

        Assert.Equal(
            [new TextSlice(pages[1], 0, 3), new TextSlice(pages[2], 0, 3), new TextSlice(pages[3], 0, 1)],
            slices);
        Assert.Equal([new TextSlice(pages[1], 1, 2)], TextFlow.SlicesOf(new TextSelection(new TextPosition(1, 1), new TextPosition(1, 2)), PageOf));
        Assert.Empty(TextFlow.SlicesOf(new TextSelection(new TextPosition(2, 2), new TextPosition(2, 2)), PageOf));
        Assert.Equal("def\nghi\nj", TextFlow.Raw(slices));
    }

    [Fact]
    public void EmptyAndInvisibleSelectionsGiveEmptyText()
    {
        var page = Page(0, (20, 10, "Hello"), (34, 10, "world"));

        Assert.Equal(string.Empty, TextFlow.Plain([]));
        Assert.Equal(string.Empty, TextFlow.Html([]));
        Assert.Equal(string.Empty, TextFlow.Raw([new TextSlice(page, 5, 7)])); // only the CR LF
        Assert.Equal(string.Empty, TextFlow.Plain([new TextSlice(page, 5, 7)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextFlow.Plain([new TextSlice(page, 3, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextFlow.Raw([new TextSlice(page, 0, page.Chars.Count + 1)]));
    }

    private static TextSlice[] All(PageText page) => [new TextSlice(page, 0, page.Chars.Count)];

    /// <summary>
    /// Builds a page from lines at x = 10: each line has a top and a font size, its chars are half the size wide and
    /// the size high (so the box bottom is the baseline). In the text '*' toggles bold and '_' toggles italic; the
    /// markers take no char. Lines are separated by a generated CR LF pair without area and of size 1, except after a
    /// line that ends with '-': that char is a hyphen (trait <see cref="TextCharTraits.Hyphen"/>) and the next line
    /// follows directly, like PDFium reports it. A trailing '|' is dropped and forces the CR LF pair.
    /// </summary>
    private static PageText Page(int index, params (double Top, float Size, string Text)[] lines)
    {
        var chars = new List<TextChar>();
        for (var i = 0; i < lines.Length; i++)
        {
            var (top, size, text) = lines[i];
            var forceBreak = text.EndsWith('|');
            text = forceBreak ? text[..^1] : text;
            var hyphenated = !forceBreak && i < lines.Length - 1 && text.EndsWith('-');
            var end = AddText(chars, text, 10, top, size, hyphenated);
            if (i < lines.Length - 1 && !hyphenated)
            {
                var empty = new PageRect(end, top + size, end, top + size);
                chars.Add(new TextChar('\r', empty, TextCharTraits.Generated, 1));
                chars.Add(new TextChar('\n', empty, TextCharTraits.Generated, 1));
            }
        }

        return new PageText(index, 200, 200, chars, []);
    }

    /// <summary>Adds the chars of a text; returns the right edge of the last one.</summary>
    private static double AddText(List<TextChar> chars, string text, double x, double top, float size, bool hyphenated = false)
    {
        var traits = TextCharTraits.None;
        var left = x;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '*':
                    traits ^= TextCharTraits.Bold;
                    continue;
                case '_':
                    traits ^= TextCharTraits.Italic;
                    continue;
            }

            var own = hyphenated && i == text.Length - 1 ? traits | TextCharTraits.Hyphen : traits;
            chars.Add(new TextChar(text[i], new PageRect(left, top, left + (size * 0.5), top + size), own, size));
            left += size * 0.5;
        }

        return left;
    }
}
