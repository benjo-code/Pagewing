using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Text;

public sealed class PageTextTests
{
    private readonly PdfEngine engine = new();

    [Fact]
    public async Task PageTextHasCharsLinksAndSize()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        var text = await document.GetPageTextAsync(0, ct);

        Assert.Equal(0, text.PageIndex);
        Assert.Equal(400, text.Width, 0.5);
        Assert.Equal(400, text.Height, 0.5);
        Assert.True(text.HasText);
        Assert.Equal("Pagewing Handbuch", text.GetText(0, 17));
        Assert.Equal(12f, text.BodyFontSize);
        Assert.Single(text.Links, l => l.Target is UriTarget { Url: "https://example.org/docs" });
        var jump = Assert.Single(text.Links, l => l.Target is PageTarget);
        var target = (PageTarget)jump.Target;
        Assert.Equal(1, target.PageIndex);
        Assert.InRange(target.Top!.Value, 248, 252); // 400 - 150

        // PDFium 157.0.8100 finds two web addresses in the text besides the two link annotations; it reports
        // "www.example.org." as "http://www.example.org".
        Assert.Equal(4, text.Links.Count);
        Assert.Contains(text.Links, l => l.Target is UriTarget { Url: "https://example.org/pagewing" });
        Assert.Contains(text.Links, l => l.Target is UriTarget { Url: "http://www.example.org" });
        Assert.All(text.Links, l => Assert.NotEmpty(l.Rects));
    }

    [Fact]
    public async Task CharsCarryBoxesFlagsAndFontSizes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        var text = await document.GetPageTextAsync(0, ct);

        // The heading is bold 24 pt Helvetica at x = 40.
        var h = text.Chars[0];
        Assert.Equal('P', h.CodePoint);
        Assert.Equal("P", h.Text);
        Assert.True((h.Flags & TextCharTraits.Bold) != 0);
        Assert.False(h.IsGenerated);
        Assert.InRange(h.FontSize, 23.5f, 24.5f);
        Assert.InRange(h.Box.Left, 38, 42);
        Assert.True(h.Box.Width > 0 && h.Box.Height > 0);

        // Line breaks are generated CR LF pairs; the line-end hyphen is one flagged '-' and has no break after it.
        Assert.Contains(text.Chars, c => c.IsLineBreak);
        Assert.All(text.Chars, c => Assert.Equal(c.CodePoint is '\r' or '\n', c.IsLineBreak));
        var hyphen = Assert.Single(text.Chars, c => (c.Flags & TextCharTraits.Hyphen) != 0);
        Assert.Equal('-', hyphen.CodePoint);
        Assert.False(hyphen.IsGenerated);
        Assert.Contains(text.Chars, c => (c.Flags & TextCharTraits.Italic) != 0);
    }

    [Fact]
    public async Task PageWithoutTextHasNoText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("vector-shapes.pdf"), cancellationToken: ct);

        var text = await document.GetPageTextAsync(0, ct);

        Assert.False(text.HasText);
        Assert.Equal(0f, text.BodyFontSize);
        Assert.All(text.Chars, c => Assert.True(c.IsGenerated));
        Assert.True(text.Width > 0 && text.Height > 0);
    }

    [Fact]
    public async Task GetPageTextRejectsAPageOutsideTheDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetPageTextAsync(-1, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetPageTextAsync(document.PageCount, ct));
    }

    [Fact]
    public async Task CancelledRequestThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.GetPageTextAsync(0, cancelled.Token));
    }

    [Fact]
    public async Task GetPageTextAfterDisposalThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);
        await document.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.GetPageTextAsync(0, ct));
    }

    [Fact]
    public void FactoryDropsLinksThePdfEngineCannotFollow()
    {
        var rect = new (double, double, double, double)[] { (10, 20, 30, 40) };
        var links = new[]
        {
            new PdfiumLink(rect, PdfiumLinkKind.Page, 3, 120.5, null),
            new PdfiumLink(rect, PdfiumLinkKind.Page, 2, null, null),
            new PdfiumLink(rect, PdfiumLinkKind.Uri, -1, null, "https://example.org"),
            new PdfiumLink(rect, PdfiumLinkKind.Unsupported, -1, null, null),
        };

        var text = PageText.FromPdfium(5, 100, 200, [], links);

        Assert.Equal(3, text.Links.Count);
        Assert.Equal(new PageTarget(3, 120.5), text.Links[0].Target);
        Assert.Equal(new PageTarget(2, null), text.Links[1].Target);
        Assert.Equal(new UriTarget("https://example.org"), text.Links[2].Target);
        Assert.Equal(new PageRect(10, 20, 30, 40), Assert.Single(text.Links[0].Rects));
        Assert.Equal(5, text.PageIndex);
    }

    [Fact]
    public void FactoryMapsTheCharFlags()
    {
        var chars = new[]
        {
            new PdfiumChar('a', 1, 2, 3, 4, false, false, 12f, true, true),
            new PdfiumChar('-', 5, 6, 7, 8, false, true, 12f, false, false),
            new PdfiumChar('\r', 9, 9, 9, 9, true, false, 1f, false, false),
        };

        var text = PageText.FromPdfium(0, 100, 100, chars, []);

        Assert.Equal(TextCharTraits.Bold | TextCharTraits.Italic, text.Chars[0].Flags);
        Assert.Equal(new PageRect(1, 2, 3, 4), text.Chars[0].Box);
        Assert.Equal(TextCharTraits.Hyphen, text.Chars[1].Flags);
        Assert.Equal(TextCharTraits.Generated, text.Chars[2].Flags);
        Assert.True(text.Chars[2].IsLineBreak);
        Assert.False(text.Chars[1].IsLineBreak);
    }

    [Fact]
    public void BodyFontSizeIsTheMostFrequentRoundedSizeOfDrawnChars()
    {
        var chars = new List<TextChar>();
        chars.AddRange(Enumerable.Repeat(Char('a', 12.2f), 5));
        chars.AddRange(Enumerable.Repeat(Char('b', 12.3f), 3)); // 12.0 and 12.5 are different buckets: 12.2 -> 12, 12.3 -> 12.5
        chars.AddRange(Enumerable.Repeat(Char('H', 24f), 4));
        chars.AddRange(Enumerable.Repeat(new TextChar(' ', default, TextCharTraits.Generated, 1f), 50)); // generated chars do not count

        var text = new PageText(0, 100, 100, chars, []);

        Assert.True(text.HasText);
        Assert.Equal(12f, text.BodyFontSize);
    }

    [Fact]
    public void OnlyGeneratedCharsMeanNoText()
    {
        var text = new PageText(0, 100, 100, [new TextChar('\r', default, TextCharTraits.Generated, 1f)], []);

        Assert.False(text.HasText);
        Assert.Equal(0f, text.BodyFontSize);
    }

    [Fact]
    public void GetTextJoinsTheCharsOfARangeAndChecksIt()
    {
        const int Smiley = 0x1F600;
        var text = new PageText(0, 100, 100, [Char('a', 12), Char(Smiley, 12), new TextChar(-5, default, TextCharTraits.None, 12), Char('z', 12)], []);

        Assert.Equal("a" + char.ConvertFromUtf32(Smiley) + "�z", text.GetText(0, 4));
        Assert.Equal(char.ConvertFromUtf32(Smiley), text.GetText(1, 2));
        Assert.Equal(string.Empty, text.GetText(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetText(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetText(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetText(3, 2));
    }

    [Fact]
    public void PageRectCalculates()
    {
        var a = new PageRect(10, 20, 30, 60);
        var b = new PageRect(25, 10, 50, 40);

        Assert.Equal(20, a.Width);
        Assert.Equal(40, a.Height);
        Assert.True(a.Contains(10, 20));
        Assert.True(a.Contains(30, 60));
        Assert.True(a.Contains(20, 40));
        Assert.False(a.Contains(9.9, 40));
        Assert.False(a.Contains(20, 60.1));
        Assert.Equal(new PageRect(10, 10, 50, 60), a.Union(b));
        Assert.Equal(new PageRect(8, 18, 32, 62), a.Inflate(2));
        Assert.Equal(new PageRect(12, 22, 28, 58), a.Inflate(-2));
    }

    [Fact]
    public void CharAtFindsTheCharUnderThePointAndNothingBetweenLines()
    {
        var text = TwoLines();

        Assert.Equal(6, text.CharAt(42, 25)); // 'w' of "world"
        Assert.Equal(13, text.CharAt(12, 39)); // 's' of "second"
        Assert.Equal(6, text.CharAt(43, 30.5)); // within the 1 pt inflation
        Assert.Equal(-1, text.CharAt(42, 32)); // in the gap between the lines
        Assert.Equal(-1, text.CharAt(500, 25)); // right of the text
        Assert.Equal(-1, TwoLines(0).CharAt(0, 0)); // a page without chars
    }

    [Fact]
    public void CharAtPrefersTheCharThatContainsThePointOverANeighbourWithinTheInflation()
    {
        var text = TwoLines();

        // x = 45.5 is in 'o' (45..50) and within 1 pt of 'w' (40..45).
        Assert.Equal(7, text.CharAt(45.5, 25));
        Assert.Equal(6, text.CharAt(44.5, 25));
    }

    [Fact]
    public void CharAtNeverReturnsAGeneratedChar()
    {
        var chars = new List<TextChar>
        {
            new('a', new PageRect(0, 0, 5, 10), TextCharTraits.None, 10),
            new(' ', new PageRect(5, 0, 10, 10), TextCharTraits.Generated, 10),
            new('b', new PageRect(20, 0, 25, 10), TextCharTraits.None, 10),
        };
        var text = new PageText(0, 100, 100, chars, []);

        Assert.Equal(-1, text.CharAt(8, 5));
        Assert.Equal(0, text.CharAt(2, 5));
        Assert.Equal(2, text.CharAt(22, 5));
    }

    [Fact]
    public void CaretAtPicksTheNearestInsertionPosition()
    {
        var text = TwoLines();

        Assert.Equal(6, text.CaretAt(41, 25)); // left half of 'w'
        Assert.Equal(7, text.CaretAt(44, 25)); // right half of 'w'
        Assert.Equal(24, text.CaretAt(10, 500)); // below all text: the end, whatever x is
        Assert.Equal(0, text.CaretAt(190, -100)); // above all text: the start, whatever x is
        Assert.Equal(11, text.CaretAt(500, 25)); // right of line 1: after its last char, before the line break
        Assert.Equal(13, text.CaretAt(0, 41)); // left of line 2: before its first char
        Assert.Equal(24, text.CaretAt(500, 41)); // right of line 2
        Assert.Equal(13, text.CaretAt(12, 32.5)); // in the gap, nearer to line 2
        Assert.Equal(0, TwoLines(0).CaretAt(10, 10)); // a page without chars
    }

    [Fact]
    public void CaretAtUsesTheNearestColumnOnTheSameRow()
    {
        var chars = new List<TextChar>();
        AddLine(chars, "ab", 10, 20, 10);
        chars.Add(new TextChar('\r', new PageRect(20, 20, 20, 20), TextCharTraits.Generated, 1));
        chars.Add(new TextChar('\n', new PageRect(20, 20, 20, 20), TextCharTraits.Generated, 1));
        AddLine(chars, "cd", 100, 20, 10);
        var text = new PageText(0, 200, 100, chars, []);

        Assert.Equal(1, text.CaretAt(14, 25)); // between 'a' and 'b'
        Assert.Equal(5, text.CaretAt(104, 25)); // between 'c' (index 4) and 'd'
        Assert.Equal(6, text.CaretAt(190, 25));
    }

    [Fact]
    public void CaretAtFollowsVerticalTextTopToBottom()
    {
        // Text of a page rotated by 90 degrees: the chars of the line are stacked downwards.
        var chars = new List<TextChar>();
        AddVerticalLine(chars, "abc", 50, 20, down: true);
        var text = new PageText(0, 200, 200, chars, []);

        Assert.Equal(0, text.CaretAt(55, 21)); // above the centre of 'a'
        Assert.Equal(1, text.CaretAt(55, 24)); // between 'a' and 'b'
        Assert.Equal(2, text.CaretAt(55, 28));
        Assert.Equal(3, text.CaretAt(55, 100)); // beyond the end of the line
        Assert.Equal(0, text.CaretAt(55, 0)); // before its start
        Assert.Equal(3, text.CaretAt(5, 22)); // left of the only line: after the last line
        Assert.Equal(0, text.CaretAt(150, 22)); // right of it: before the first line
    }

    [Fact]
    public void CaretAtFollowsVerticalTextBottomToTop()
    {
        // Text of a page rotated by 270 degrees: the chars of the line are stacked upwards.
        var chars = new List<TextChar>();
        AddVerticalLine(chars, "abc", 50, 20, down: false);
        var text = new PageText(0, 200, 200, chars, []);

        Assert.Equal(0, text.CaretAt(55, 34)); // below the centre of 'a', which is the lowest char
        Assert.Equal(1, text.CaretAt(55, 31));
        Assert.Equal(2, text.CaretAt(55, 26));
        Assert.Equal(3, text.CaretAt(55, 0)); // beyond the end of the line
        Assert.Equal(0, text.CaretAt(5, 22)); // left of the only line: before the first line
        Assert.Equal(3, text.CaretAt(150, 22)); // right of it: after the last line
    }

    [Fact]
    public void CaretAtNearestVerticalLineIsChosenByX()
    {
        // Lines of vertical text follow each other to the left: "ab" at x 100..110, then "cd" at x 86..96.
        var chars = new List<TextChar>();
        AddVerticalLine(chars, "ab", 100, 20, down: true);
        chars.Add(new TextChar('\r', new PageRect(100, 30, 100, 30), TextCharTraits.Generated, 1));
        chars.Add(new TextChar('\n', new PageRect(100, 30, 100, 30), TextCharTraits.Generated, 1));
        AddVerticalLine(chars, "cd", 86, 20, down: true);
        var text = new PageText(0, 200, 200, chars, []);

        Assert.Equal(0, text.CaretAt(104, 22)); // in the first line
        Assert.Equal(4, text.CaretAt(90, 22)); // in the second line
        Assert.Equal(6, text.CaretAt(90, 28)); // after 'd', the end of the second line
        Assert.Equal(2, text.CaretAt(104, 28)); // after 'b', before the line break
        Assert.Equal(4, text.CaretAt(97, 22)); // in the gap, nearer to the second line
        Assert.Equal(0, text.CaretAt(99, 22)); // in the gap, nearer to the first line
        Assert.Equal(0, text.CaretAt(300, 28)); // right of all lines
        Assert.Equal(6, text.CaretAt(0, 28)); // left of all lines
    }

    [Fact]
    public void CaretAtIgnoresALeadingSingleCharSegmentWhenChoosingTheOrientation()
    {
        // A lone char (a page number, a bullet) comes first; the text after it runs top to bottom.
        var chars = new List<TextChar>();
        AddVerticalLine(chars, "x", 100, 20, down: true);
        chars.Add(new TextChar('\r', new PageRect(100, 25, 100, 25), TextCharTraits.Generated, 1));
        chars.Add(new TextChar('\n', new PageRect(100, 25, 100, 25), TextCharTraits.Generated, 1));
        AddVerticalLine(chars, "abc", 86, 20, down: true); // indexes 3 to 5
        var text = new PageText(0, 200, 200, chars, []);

        Assert.Equal(3, text.CaretAt(90, 5)); // above the text but within the lines' x: before 'a', not 0
        Assert.Equal(6, text.CaretAt(90, 100)); // after 'c'
        Assert.Equal(6, text.CaretAt(0, 22)); // left of all lines of top-to-bottom text: after the last one
        Assert.Equal(0, text.CaretAt(300, 22)); // right of them: before the first one
    }

    [Fact]
    public async Task CaretAtFollowsTheTextOfARotatedPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("rotated-page.pdf"), cancellationToken: ct);
        var text = await document.GetPageTextAsync(0, ct);

        var real = Enumerable.Range(0, text.Chars.Count).Where(i => !text.Chars[i].IsGenerated).Take(2).ToArray();
        Assert.Equal(2, real.Length);
        var a = text.Chars[real[0]].Box;
        var b = text.Chars[real[1]].Box;
        var centreX = (a.Left + a.Right) / 2;
        var centreY = (a.Top + a.Bottom) / 2;
        var stepX = (((b.Left + b.Right) / 2) - centreX) / 4; // a quarter of the way to the next char
        var stepY = (((b.Top + b.Bottom) / 2) - centreY) / 4;

        Assert.True(Math.Abs(stepY) > Math.Abs(stepX)); // the text of a page rotated by 90 degrees runs vertically
        Assert.Equal(real[0], text.CaretAt(centreX - stepX, centreY - stepY)); // before the first char
        Assert.Equal(real[0] + 1, text.CaretAt(centreX + stepX, centreY + stepY)); // after it
    }

    [Fact]
    public void WordAtSelectsTheWordAndPunctuationAloneIsOneChar()
    {
        var text = Page("Hello, world", "don't stop 42x");

        Assert.Equal((0, 5), text.WordAt(1)); // 'e' in "Hello"
        Assert.Equal((0, 5), text.WordAt(0));
        Assert.Equal((0, 5), text.WordAt(4));
        Assert.Equal((5, 6), text.WordAt(5)); // the comma
        Assert.Equal((6, 7), text.WordAt(6)); // the space
        Assert.Equal((7, 12), text.WordAt(8)); // "world"
        Assert.Equal((14, 19), text.WordAt(16)); // "don't": apostrophes belong to a word
        Assert.Equal((20, 24), text.WordAt(20)); // "stop"
        Assert.Equal((25, 28), text.WordAt(26)); // "42x": digits belong to a word
        Assert.Throws<ArgumentOutOfRangeException>(() => text.WordAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.WordAt(text.Chars.Count));
    }

    [Fact]
    public void WordAtStopsAtGeneratedChars()
    {
        var chars = new List<TextChar>
        {
            new('a', new PageRect(0, 0, 5, 10), TextCharTraits.None, 10),
            new('b', new PageRect(5, 0, 10, 10), TextCharTraits.None, 10),
            new('c', new PageRect(10, 0, 15, 10), TextCharTraits.Generated, 10),
            new('d', new PageRect(15, 0, 20, 10), TextCharTraits.None, 10),
        };
        var text = new PageText(0, 100, 100, chars, []);

        Assert.Equal((0, 2), text.WordAt(0));
        Assert.Equal((2, 3), text.WordAt(2));
        Assert.Equal((3, 4), text.WordAt(3));
    }

    [Fact]
    public void LineAtStopsAtGeneratedLineBreaks()
    {
        var text = TwoLines();

        Assert.Equal((13, 24), text.LineAt(15)); // "second line", the CR LF before it excluded
        Assert.Equal((13, 24), text.LineAt(13));
        Assert.Equal((13, 24), text.LineAt(23));
        Assert.Equal((0, 11), text.LineAt(3)); // "Hello world", the CR LF after it excluded
        Assert.Equal((11, 11), text.LineAt(11)); // a line break belongs to no line
        Assert.Throws<ArgumentOutOfRangeException>(() => text.LineAt(24));
    }

    [Fact]
    public void LineAtEndsAtAHyphen()
    {
        // PDFium reports no line break after a line-end hyphen: the hyphen is the last char of its line.
        var text = Page("Hello wor-", "ld again");

        Assert.Equal((0, 10), text.LineAt(2));
        Assert.Equal((0, 10), text.LineAt(9)); // the hyphen itself
        Assert.Equal((10, 18), text.LineAt(10));
        Assert.Equal((10, 18), text.LineAt(17));
    }

    [Fact]
    public void RectsOfGivesOneRectanglePerLine()
    {
        var text = TwoLines();

        var rects = text.RectsOf(6, 20); // "world" + CR LF + "second "

        Assert.Equal(2, rects.Count);
        Assert.Equal(new PageRect(40, 20, 65, 30), rects[0]); // ends at the last char of line 1
        Assert.Equal(new PageRect(10, 34, 45, 44), rects[1]);
        Assert.Equal([new PageRect(15, 20, 30, 30)], text.RectsOf(1, 4));
        Assert.Equal(2, text.RectsOf(0, 24).Count);
    }

    [Fact]
    public void RectsOfGivesTwoRectanglesAcrossAHyphenatedLineEnd()
    {
        var text = Page("Hello wor-", "ld again");

        var rects = text.RectsOf(6, 14); // "wor-" + "ld a"

        Assert.Equal(2, rects.Count);
        Assert.Equal(new PageRect(40, 20, 60, 30), rects[0]); // up to and including the hyphen
        Assert.Equal(new PageRect(10, 34, 30, 44), rects[1]);
    }

    [Fact]
    public void RectsOfUsesTheTightBoxesOfEachLine()
    {
        var chars = new List<TextChar>
        {
            new('o', new PageRect(10, 24, 15, 30), TextCharTraits.None, 10),
            new('l', new PageRect(15, 20, 18, 30), TextCharTraits.None, 10),
            new('\r', new PageRect(18, 30, 18, 30), TextCharTraits.Generated, 1),
            new('\n', new PageRect(18, 30, 18, 30), TextCharTraits.Generated, 1),
            new('x', new PageRect(10, 36, 15, 44), TextCharTraits.None, 10),
        };
        var text = new PageText(0, 100, 100, chars, []);

        Assert.Equal([new PageRect(10, 20, 18, 30), new PageRect(10, 36, 15, 44)], text.RectsOf(0, 5));
    }

    [Fact]
    public void RectsOfCoversAGeneratedSpaceInsideALineButNotAtItsEnds()
    {
        var chars = new List<TextChar>
        {
            new(' ', new PageRect(0, 20, 5, 30), TextCharTraits.Generated, 10),
            new('a', new PageRect(5, 20, 10, 30), TextCharTraits.None, 10),
            new(' ', new PageRect(10, 20, 20, 30), TextCharTraits.Generated, 10),
            new('b', new PageRect(20, 20, 25, 30), TextCharTraits.None, 10),
            new(' ', new PageRect(25, 20, 30, 30), TextCharTraits.Generated, 10),
        };
        var text = new PageText(0, 100, 100, chars, []);

        Assert.Equal([new PageRect(5, 20, 25, 30)], text.RectsOf(0, 5));
    }

    [Fact]
    public void RectsOfAnEmptyRangeIsEmpty()
    {
        var text = TwoLines();

        Assert.Empty(text.RectsOf(5, 5));
        Assert.Empty(text.RectsOf(11, 13)); // only the line break
        Assert.Empty(text.RectsOf(24, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.RectsOf(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.RectsOf(0, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.RectsOf(3, 2));
    }

    [Fact]
    public void LinkAtReturnsTheLinkUnderThePoint()
    {
        var first = new PageLink([new PageRect(10, 10, 50, 20), new PageRect(10, 20, 30, 30)], new UriTarget("https://example.org"));
        var second = new PageLink([new PageRect(60, 10, 90, 20)], new PageTarget(2, null));
        var text = new PageText(0, 100, 100, [], [first, second]);

        Assert.Same(first, text.LinkAt(20, 15));
        Assert.Same(first, text.LinkAt(20, 25)); // second rectangle of the first link
        Assert.Same(second, text.LinkAt(70, 15));
        Assert.Null(text.LinkAt(40, 25));
        Assert.Null(text.LinkAt(0, 0));
    }

    [Fact]
    public void LinkAtPrefersTheEarlierLinkWhereTheyOverlap()
    {
        var annotation = new PageLink([new PageRect(10, 10, 50, 20)], new PageTarget(1, null));
        var detected = new PageLink([new PageRect(30, 10, 70, 20)], new UriTarget("http://www.example.org"));
        var text = new PageText(0, 100, 100, [], [annotation, detected]);

        Assert.Same(annotation, text.LinkAt(40, 15)); // links come annotations first, so an annotation wins
        Assert.Same(detected, text.LinkAt(60, 15));
    }

    [Fact]
    public void TextSelectionOrdersAnchorAndFocus()
    {
        var anchor = new TextPosition(2, 10);
        var focus = new TextPosition(1, 50);

        var backwards = new TextSelection(anchor, focus);
        Assert.Equal(focus, backwards.Start);
        Assert.Equal(anchor, backwards.End);
        Assert.False(backwards.IsEmpty);

        var forwards = new TextSelection(focus, anchor);
        Assert.Equal(focus, forwards.Start);
        Assert.Equal(anchor, forwards.End);

        var sameCaret = new TextSelection(new TextPosition(3, 7), new TextPosition(3, 7));
        Assert.True(sameCaret.IsEmpty);

        var samePage = new TextSelection(new TextPosition(3, 9), new TextPosition(3, 2));
        Assert.Equal(new TextPosition(3, 2), samePage.Start);
        Assert.Equal(new TextPosition(3, 9), samePage.End);
    }

    [Fact]
    public void TextPositionsCompareByPageThenCaret()
    {
        var a = new TextPosition(1, 99);
        var b = new TextPosition(2, 0);
        var c = new TextPosition(2, 5);

        Assert.True(a < b);
        Assert.True(b < c);
        Assert.True(c > a);
        Assert.True(a <= new TextPosition(1, 99));
        Assert.True(c >= b);
        Assert.True(a.CompareTo(b) < 0);
        Assert.Equal(0, b.CompareTo(new TextPosition(2, 0)));
        Assert.Equal([a, b, c], new[] { c, a, b }.Order());
    }

    private static TextChar Char(int codePoint, float size) => new(codePoint, default, TextCharTraits.None, size);

    /// <summary>The text "Hello world" / "second line" (size 10, line distance 14, left at 10) or, with 0, nothing.</summary>
    private static PageText TwoLines(int lineCount = 2) =>
        lineCount == 0 ? new PageText(0, 200, 200, [], []) : Page("Hello world", "second line");

    /// <summary>
    /// Builds a page from lines of size 10 at x = 10, the first top at 20 and 14 points between the tops. Chars are
    /// 5 wide and 10 high. A line break is a generated CR LF pair without area, except after a line that ends with
    /// '-': that char is a hyphen and the next line follows directly, like PDFium reports it.
    /// </summary>
    private static PageText Page(params string[] lines)
    {
        var chars = new List<TextChar>();
        for (var i = 0; i < lines.Length; i++)
        {
            var top = 20 + (14 * i);
            var hyphenated = i < lines.Length - 1 && lines[i].EndsWith('-');
            AddLine(chars, lines[i], 10, top, 10, hyphenated);
            if (i < lines.Length - 1 && !hyphenated)
            {
                var end = 10 + (5 * lines[i].Length);
                var empty = new PageRect(end, top, end, top);
                chars.Add(new TextChar('\r', empty, TextCharTraits.Generated, 1));
                chars.Add(new TextChar('\n', empty, TextCharTraits.Generated, 1));
            }
        }

        return new PageText(0, 200, 200, chars, []);
    }

    /// <summary>Adds a vertical line of chars 10 wide and 5 high at x, stacked from top downwards (or, with <paramref name="down"/> false, upwards).</summary>
    private static void AddVerticalLine(List<TextChar> chars, string line, double x, double top, bool down)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var slot = down ? i : line.Length - 1 - i;
            var charTop = top + (slot * 5);
            chars.Add(new TextChar(line[i], new PageRect(x, charTop, x + 10, charTop + 5), TextCharTraits.None, 10));
        }
    }

    private static void AddLine(List<TextChar> chars, string line, double x, double top, float size, bool hyphenated = false)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var left = x + (i * size * 0.5);
            var traits = hyphenated && i == line.Length - 1 ? TextCharTraits.Hyphen : TextCharTraits.None;
            chars.Add(new TextChar(line[i], new PageRect(left, top, left + (size * 0.5), top + size), traits, size));
        }
    }
}
