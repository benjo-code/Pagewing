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

    private static TextChar Char(int codePoint, float size) => new(codePoint, default, TextCharTraits.None, size);
}
