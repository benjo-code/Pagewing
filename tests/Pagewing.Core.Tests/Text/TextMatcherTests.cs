using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Text;

public sealed class TextMatcherTests
{
    [Theory]
    [InlineData("Hello World", "world", false, 1)]
    [InlineData("Hello World", "world", true, 0)]
    [InlineData("Hello World", "World", true, 1)]
    public void MatchCaseSwitchesCaseSensitivity(string text, string query, bool matchCase, int expected)
    {
        var matches = TextMatcher.Find(Chars(text), query, new SearchOptions(MatchCase: matchCase));

        Assert.Equal(expected, matches.Count);
    }

    [Fact]
    public void LigatureCharsMatchTheirLetters()
    {
        var chars = Chars("ﬁne"); // "ﬁne" with the ligature as one char

        Assert.Equal(new TextMatch(0, 2), Assert.Single(TextMatcher.Find(chars, "fine", default)));
        Assert.Equal(new TextMatch(0, 2), Assert.Single(TextMatcher.Find(chars, "ﬁne", default)));
        Assert.Equal(new TextMatch(0, 0), Assert.Single(TextMatcher.Find(chars, "fi", default)));
    }

    [Fact]
    public void DecomposedUmlautsMatchComposedOnes()
    {
        var decomposed = Chars("müde"); // 'u' and a combining diaeresis are two chars
        var composed = Chars("müde");

        // The match ends with the last char of the page, the combining mark included.
        Assert.Equal(new TextMatch(0, 4), Assert.Single(TextMatcher.Find(decomposed, "müde", default)));
        Assert.Equal(new TextMatch(1, 2), Assert.Single(TextMatcher.Find(decomposed, "ü", default)));
        Assert.Equal(new TextMatch(0, 3), Assert.Single(TextMatcher.Find(composed, "müde", default)));
        Assert.Empty(TextMatcher.Find(decomposed, "mude", default));
    }

    [Fact]
    public void AMatchCrossesALineBreak()
    {
        var chars = Chars("hello\nworld"); // h e l l o CR LF w o r l d

        Assert.Equal(new TextMatch(0, 11), Assert.Single(TextMatcher.Find(chars, "hello world", default)));
        Assert.Equal(new TextMatch(0, 11), Assert.Single(TextMatcher.Find(chars, "  hello \t  world ", default)));
        Assert.Empty(TextMatcher.Find(chars, "helloworld", default));
    }

    [Fact]
    public void WhitespaceRunsCountAsOneSpace()
    {
        var chars = Chars("hello  \t world");

        Assert.Equal(new TextMatch(0, 13), Assert.Single(TextMatcher.Find(chars, "hello world", default)));
    }

    [Fact]
    public void AHyphenAtTheLineEndIsIgnored()
    {
        // Like PDFium: the hyphen is one char with the Hyphen trait, the next line follows with no line break.
        var chars = Chars("Win-\ndows und");
        Assert.Equal(new TextMatch(0, 7), Assert.Single(TextMatcher.Find(chars, "Windows", default)));
        Assert.Empty(TextMatcher.Find(chars, "Win-dows", default));
        Assert.Empty(TextMatcher.Find(chars, "Win dows", default));

        // A capital after the hyphen keeps the hyphen, and the line end counts as a space (documented limit).
        var compound = Chars("Bindestrich-\nWort");
        Assert.Empty(TextMatcher.Find(compound, "Bindestrich-Wort", default));
        Assert.Empty(TextMatcher.Find(compound, "BindestrichWort", default));
        Assert.Single(TextMatcher.Find(compound, "Bindestrich- Wort", default));

        // A hyphen after a digit is no hyphenation either.
        Assert.Empty(TextMatcher.Find(Chars("2-\nfach"), "2fach", default));
    }

    [Fact]
    public void AHyphenFollowedByALineBreakIsIgnoredAsWell()
    {
        // Other producers: a plain '-' (or a soft hyphen) at the end of the line, then a generated CR LF pair.
        var chars = Chars("Win-\ndows", hyphenAsLineBreak: true); // W i n - CR LF d o w s
        Assert.Equal(new TextMatch(0, 9), Assert.Single(TextMatcher.Find(chars, "windows", default)));

        var soft = Chars("Win­\ndows", hyphenAsLineBreak: true);
        Assert.Equal(new TextMatch(0, 9), Assert.Single(TextMatcher.Find(soft, "windows", default)));

        var compound = Chars("Bindestrich-\nWort", hyphenAsLineBreak: true);
        Assert.Empty(TextMatcher.Find(compound, "Bindestrich-Wort", default));

        // A hyphen inside a line is ordinary text.
        Assert.Single(TextMatcher.Find(Chars("PDF-Viewer"), "pdf-viewer", default));
        Assert.Empty(TextMatcher.Find(Chars("PDF-Viewer"), "pdfviewer", default));
    }

    [Fact]
    public void WholeWordIgnoresPartsOfWords()
    {
        var chars = Chars("cat catalog cat.");

        Assert.Equal(2, TextMatcher.Find(chars, "cat", new SearchOptions(WholeWord: true)).Count);
        Assert.Equal(3, TextMatcher.Find(chars, "cat", default).Count);
        Assert.Empty(TextMatcher.Find(Chars("concat"), "cat", new SearchOptions(WholeWord: true)));
        Assert.Single(TextMatcher.Find(Chars("bobcat"), "cat", default));
    }

    [Fact]
    public void WholeWordSkipsAPartAndFindsTheWordBehindIt()
    {
        var matches = TextMatcher.Find(Chars("catcat cat"), "cat", new SearchOptions(WholeWord: true));

        Assert.Equal(new TextMatch(7, 9), Assert.Single(matches));
    }

    [Fact]
    public void MatchesDoNotOverlap()
    {
        var matches = TextMatcher.Find(Chars("aaaa"), "aa", default);

        Assert.Equal([new TextMatch(0, 1), new TextMatch(2, 3)], matches);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" \t\n ")]
    public void EmptyAndWhitespaceQueriesFindNothing(string query)
    {
        Assert.Empty(TextMatcher.Find(Chars("a b c"), query, default));
    }

    [Fact]
    public void APageWithoutCharsHasNoMatches()
    {
        Assert.Empty(TextMatcher.Find([], "a", default));
    }

    [Fact]
    public void DiacriticsAreNotIgnored()
    {
        Assert.Empty(TextMatcher.Find(Chars("über"), "uber", default));
        Assert.Single(TextMatcher.Find(Chars("Über"), "über", default));
    }

    [Fact]
    public void SnippetStaysInTheLinesOfTheMatch()
    {
        var chars = Chars("first line\nsay hello world now\nlast");
        var match = Assert.Single(TextMatcher.Find(chars, "hello", default));

        Assert.Equal(new SearchSnippet("say ", "hello", " world now"), TextMatcher.SnippetOf(chars, match.FirstChar, match.LastChar));
    }

    [Fact]
    public void SnippetHasUpTo30CharsOnEachSide()
    {
        var before = new string('a', 40) + " ";
        var after = " " + new string('b', 40);
        var chars = Chars(before + "x" + after);
        var match = Assert.Single(TextMatcher.Find(chars, "x", default));

        var snippet = TextMatcher.SnippetOf(chars, match.FirstChar, match.LastChar);

        Assert.Equal(before[^30..], snippet.Before);
        Assert.Equal("x", snippet.Match);
        Assert.Equal(after[..30], snippet.After);
    }

    [Fact]
    public void SnippetOfAMatchOverSeveralLinesJoinsThemLikeTheSearchText()
    {
        var chars = Chars("ein Win-\ndows und\nLinux ist");

        var windows = Assert.Single(TextMatcher.Find(chars, "windows", default));
        Assert.Equal(new SearchSnippet("ein ", "Windows", " und"), TextMatcher.SnippetOf(chars, windows.FirstChar, windows.LastChar));

        var across = Assert.Single(TextMatcher.Find(chars, "und linux", default));
        Assert.Equal(new SearchSnippet("dows ", "und Linux", " ist"), TextMatcher.SnippetOf(chars, across.FirstChar, across.LastChar));

        var crlf = Chars("ein Win-\ndows und", hyphenAsLineBreak: true);
        var match = Assert.Single(TextMatcher.Find(crlf, "windows", default));
        Assert.Equal(new SearchSnippet("ein ", "Windows", " und"), TextMatcher.SnippetOf(crlf, match.FirstChar, match.LastChar));
    }

    /// <summary>
    /// Builds the chars of a text without geometry. '\n' is a generated CR LF pair, except right after a '-': like
    /// PDFium reports it, the '-' then gets the <see cref="TextCharTraits.Hyphen"/> trait and the next line follows
    /// with no line break. With <paramref name="hyphenAsLineBreak"/> a '-' (or U+00AD) before '\n' stays a plain char
    /// followed by the CR LF pair, as other producers write it.
    /// </summary>
    private static TextChar[] Chars(string text, bool hyphenAsLineBreak = false)
    {
        var chars = new List<TextChar>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lineEnd = i + 1 < text.Length && text[i + 1] == '\n';
            if (c == '\n')
            {
                if (!hyphenAsLineBreak && i > 0 && text[i - 1] == '-')
                {
                    continue;
                }

                chars.Add(new TextChar('\r', default, TextCharTraits.Generated, 1));
                chars.Add(new TextChar('\n', default, TextCharTraits.Generated, 1));
            }
            else if (c == '-' && lineEnd && !hyphenAsLineBreak)
            {
                chars.Add(new TextChar('-', default, TextCharTraits.Hyphen, 12));
            }
            else
            {
                chars.Add(new TextChar(c, default, TextCharTraits.None, 12));
            }
        }

        return [.. chars];
    }
}
