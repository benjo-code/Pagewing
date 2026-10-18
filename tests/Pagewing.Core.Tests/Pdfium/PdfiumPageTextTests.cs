using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

public sealed class PdfiumPageTextTests
{
    [Fact]
    public async Task CharsOfTheHeadingHaveBoxesAndBold()
    {
        // text-rich.pdf page 1: "Pagewing Handbuch" in Helvetica-Bold 24 at x = 40, baseline y = 350 (from the bottom).
        var chars = await ReadPage("text-rich.pdf", 0, geometry: true, style: true);
        var heading = chars.Where(c => !c.Generated).Take(17).ToArray();

        Assert.Equal("Pagewing Handbuch", Text(heading));
        Assert.All(heading, c =>
        {
            Assert.True(c.Bold);
            Assert.False(c.Italic);
            Assert.InRange(c.FontSize, 23.5f, 24.5f);
        });
        Assert.InRange(heading[0].Left, 38, 42);
        Assert.InRange(heading[0].Top, 20, 60);
        Assert.True(heading[0].Bottom > heading[0].Top);
        Assert.True(heading[0].Right > heading[0].Left);
    }

    /// <remarks>
    /// Observed with PDFium 157.0.8100 on page 1 of text-rich.pdf ("… für Win-" / "dows und Linux. …"):
    /// index 61 'W', 62 'i', 63 'n', 64 the line-end hyphen, 65 'd'. For index 64 <c>FPDFText_GetUnicode</c>
    /// returns U+0002 (<c>FPDFText_GetText</c> writes U+FFFE there), <c>FPDFText_IsHyphen</c> 1,
    /// <c>FPDFText_IsGenerated</c> 0, and <c>FPDFText_GetCharBox</c> the box of the drawn '-' glyph (user space
    /// x 288.45 to 291.69, y 312.58 to 313.64). No generated CR LF follows the hyphen: index 65 is already the 'd'
    /// on the next line. Every other line end is a generated CR (U+000D) plus LF (U+000A), for example 109 and 110
    /// after "bleibt"; their boxes have no area and sit at the end of the line. The '-' inside "PDF-Viewer"
    /// (index 49) is a plain U+002D without the hyphen flag.
    /// </remarks>
    [Fact]
    public async Task LineBreaksAreGeneratedAndTheHyphenIsFlagged()
    {
        var chars = await ReadPage("text-rich.pdf", 0, geometry: true, style: false);
        var text = Text(chars);

        // The hyphen is reported as '-' and the next line follows directly, without a line break.
        var win = text.IndexOf("Win-dows", StringComparison.Ordinal);
        Assert.True(win >= 0, text);
        var hyphen = chars[win + 3];
        Assert.Equal('-', hyphen.CodePoint);
        Assert.True(hyphen.Hyphen);
        Assert.False(hyphen.Generated);
        Assert.True(hyphen.Right > hyphen.Left && hyphen.Bottom > hyphen.Top);
        Assert.InRange(hyphen.Top, chars[win].Top, chars[win].Bottom);  // on the line of "Win"
        Assert.True(chars[win + 4].Top > chars[win].Bottom);            // 'd' starts the next line
        Assert.Single(chars, c => c.Hyphen);

        // A plain hyphen inside a word is not flagged.
        var pdf = text.IndexOf("PDF-Viewer", StringComparison.Ordinal);
        Assert.Equal('-', chars[pdf + 3].CodePoint);
        Assert.False(chars[pdf + 3].Hyphen);

        // Other line ends are a generated CR LF pair; the drawn characters are never generated.
        var bleibt = text.IndexOf("bleibt\r\nauch", StringComparison.Ordinal);
        Assert.True(bleibt >= 0, text);
        Assert.True(chars[bleibt + 6].Generated);
        Assert.True(chars[bleibt + 7].Generated);
        Assert.All(chars, c => Assert.Equal(c.CodePoint is '\r' or '\n', c.Generated));
        Assert.Equal(7, chars.Count(c => c.CodePoint == '\r')); // 9 lines, the hyphenated line end has none
    }

    [Fact]
    public async Task BoldAndItalicRunsAreMarkedInOneLine()
    {
        var chars = await ReadPage("text-rich.pdf", 0, geometry: false, style: true);
        var text = Text(chars);
        var line = text.IndexOf("Das ist fett und kursiv im Satz.", StringComparison.Ordinal);
        Assert.True(line >= 0, text);

        var fett = line + "Das ist ".Length;
        var und = fett + "fett".Length;
        var kursiv = und + " und ".Length;
        Assert.All(chars[fett..und], c => Assert.True(c.Bold && !c.Italic));
        Assert.All(chars[(und + 1)..(kursiv - 1)], c => Assert.False(c.Bold || c.Italic));
        Assert.All(chars[kursiv..(kursiv + "kursiv".Length)], c => Assert.True(c.Italic && !c.Bold));
        Assert.All(chars[line..(fett - 1)], c => Assert.False(c.Bold || c.Italic));
    }

    [Fact]
    public async Task GeometryFollowsRotationAndCropBox()
    {
        // rotated-page.pdf: MediaBox 200 x 100 with /Rotate 90, shown as 100 x 200.
        var rotated = await ReadPage("rotated-page.pdf", 0, geometry: true, style: false);
        Assert.Equal("Rotated", Text(rotated.Where(c => !c.Generated)));
        Assert.All(rotated, c => AssertInside(c, 100, 200));

        // Rotated clockwise: the baseline (user y = 40) becomes the left edge, user x runs down the page.
        Assert.InRange(rotated[0].Left, 39, 41);
        Assert.InRange(rotated[0].Top, 30, 33);
        Assert.True(rotated[1].Top > rotated[0].Top);

        // cropbox.pdf: MediaBox 300 x 300 with CropBox [50 75 250 225], shown as 200 x 150; text at x = 100.
        var cropped = await ReadPage("cropbox.pdf", 0, geometry: true, style: false);
        Assert.Equal("CropBox", Text(cropped.Where(c => !c.Generated)));
        Assert.All(cropped, c => AssertInside(c, 200, 150));
        Assert.InRange(cropped[0].Left, 48, 52);
    }

    [Fact]
    public async Task CharsWithoutGeometryOrStyleHaveZeroValues()
    {
        var chars = await ReadPage("text-rich.pdf", 0, geometry: false, style: false);

        Assert.NotEmpty(chars);
        Assert.All(chars, c =>
        {
            Assert.Equal(0, c.Left);
            Assert.Equal(0, c.Top);
            Assert.Equal(0, c.Right);
            Assert.Equal(0, c.Bottom);
            Assert.Equal(0f, c.FontSize);
            Assert.False(c.Bold);
            Assert.False(c.Italic);
        });
    }

    [Fact]
    public async Task RangeReadMatchesTheFullRead()
    {
        var thread = PdfiumThread.Shared;
        var (all, range) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("text-rich.pdf"), null);
                using var page = document.LoadPage(0);
                using var text = page.LoadTextPage();
                return (PdfiumPageText.ReadChars(page, text, geometry: true, style: true), PdfiumPageText.ReadChars(page, text, 5, 10));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(all[5..15], range);
    }

    [Fact]
    public async Task EmptyPageHasNoCharsAndNoLinks()
    {
        var thread = PdfiumThread.Shared;
        var (chars, links) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("vector-shapes.pdf"), null);
                using var page = document.LoadPage(0);
                using var text = page.LoadTextPage();
                return (PdfiumPageText.ReadChars(page, text, geometry: true, style: true), PdfiumPageText.ReadLinks(document, page, text));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(chars);
        Assert.Empty(links);
    }

    [Fact]
    public async Task LinksOfThePageAreAnchorsAndWebAddresses()
    {
        var links = await ReadLinks("text-rich.pdf", 0);

        var uri = Assert.Single(links, l => l.Kind == PdfiumLinkKind.Uri && l.Uri == "https://example.org/docs");
        var uriRect = Assert.Single(uri.Rects);
        Assert.InRange(uriRect.Left, 39, 41);
        Assert.InRange(uriRect.Top, 227, 229);     // 400 - 172
        Assert.InRange(uriRect.Right, 139, 141);
        Assert.InRange(uriRect.Bottom, 243, 245);  // 400 - 156
        Assert.Equal(-1, uri.PageIndex);
        Assert.Null(uri.Top);

        var page = Assert.Single(links, l => l.Kind == PdfiumLinkKind.Page);
        Assert.Equal(1, page.PageIndex);
        Assert.NotNull(page.Top);
        Assert.InRange(page.Top.Value, 248, 252);  // 400 - 150
        Assert.Null(page.Uri);
        var pageRect = Assert.Single(page.Rects);
        Assert.InRange(pageRect.Top, 257, 259);    // 400 - 142

        // Web addresses PDFium detects in the text, after the link annotations. PDFium 157.0.8100 reports
        // "www.example.org." as "http://www.example.org" (scheme added, sentence period dropped).
        var web = links.Skip(2).ToArray();
        Assert.Equal(["https://example.org/pagewing", "http://www.example.org"], web.Select(l => l.Uri));
        Assert.All(web, l =>
        {
            Assert.Equal(PdfiumLinkKind.Uri, l.Kind);
            Assert.Equal(-1, l.PageIndex);
            var rect = Assert.Single(l.Rects);
            Assert.InRange(rect.Top, 195, 215); // the line at baseline y = 190
        });
        Assert.Equal(4, links.Length);
    }

    [Fact]
    public void WebAddressAreasUnderALinkAnnotationAreDropped()
    {
        // No test PDF has a web address under a link annotation, so the merge rule is checked with synthetic links.
        PdfiumLink[] anchors = [new([(10, 10, 100, 20)], PdfiumLinkKind.Uri, -1, null, "https://example.org/a")];
        PdfiumLink[] web =
        [
            new([(50, 12, 120, 18), (10, 30, 60, 40)], PdfiumLinkKind.Uri, -1, null, "https://example.org/b"), // one area under the anchor
            new([(20, 11, 30, 19)], PdfiumLinkKind.Uri, -1, null, "https://example.org/c"),                     // only area under the anchor
            new([(100, 10, 150, 20)], PdfiumLinkKind.Uri, -1, null, "https://example.org/d"),                   // touches the edge only
        ];

        var links = PdfiumPageText.Merge(anchors, web);

        Assert.Equal(["https://example.org/a", "https://example.org/b", "https://example.org/d"], links.Select(l => l.Uri));
        Assert.Equal([(10, 30, 60, 40)], links[1].Rects);
        Assert.Equal([(100, 10, 150, 20)], links[2].Rects);
    }

    [Fact]
    public async Task ForeignThreadCallThrows()
    {
        var thread = PdfiumThread.Shared;
        var ct = TestContext.Current.CancellationToken;
        var (document, page, text) = await thread.InvokeAsync(
            () =>
            {
                var openedDocument = PdfiumDocument.Open(thread, TestData.Pdf("text-rich.pdf"), null);
                var loadedPage = openedDocument.LoadPage(0);
                return (openedDocument, loadedPage, loadedPage.LoadTextPage());
            },
            cancellationToken: ct);
        try
        {
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumPageText.ReadChars(page, text, geometry: true, style: true));
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumPageText.ReadChars(page, text, 0, 1));
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumPageText.ReadLinks(document, page, text));
        }
        finally
        {
            text.Dispose();
            page.Dispose();
            document.Dispose();
        }
    }

    private static Task<PdfiumChar[]> ReadPage(string file, int pageIndex, bool geometry, bool style)
    {
        var thread = PdfiumThread.Shared;
        return thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf(file), null);
                using var page = document.LoadPage(pageIndex);
                using var text = page.LoadTextPage();
                return PdfiumPageText.ReadChars(page, text, geometry, style);
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static Task<PdfiumLink[]> ReadLinks(string file, int pageIndex)
    {
        var thread = PdfiumThread.Shared;
        return thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf(file), null);
                using var page = document.LoadPage(pageIndex);
                using var text = page.LoadTextPage();
                return PdfiumPageText.ReadLinks(document, page, text);
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string Text(IEnumerable<PdfiumChar> chars) =>
        string.Concat(chars.Select(c => char.ConvertFromUtf32(c.CodePoint)));

    private static void AssertInside(PdfiumChar c, double width, double height)
    {
        Assert.InRange(c.Left, 0, width);
        Assert.InRange(c.Right, c.Left, width);
        Assert.InRange(c.Top, 0, height);
        Assert.InRange(c.Bottom, c.Top, height);
        Assert.True(c.Right > c.Left && c.Bottom > c.Top);
    }
}
