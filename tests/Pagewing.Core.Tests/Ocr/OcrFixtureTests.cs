using Pagewing.Core.Pdf;
using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>The scan fixtures (<see cref="OcrFixtures"/>) are what they say they are, and the engine finds their words where they stand.</summary>
public sealed class OcrFixtureTests
{
    private const int Dpi = 200;

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int NonBlank(string text) => text.Count(static c => !char.IsWhiteSpace(c));

    [Fact]
    public async Task ScanFixtureHasThreeImagePagesWithoutText()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("ocr-scan.pdf"), cancellationToken: Ct);

        Assert.Equal(3, document.PageCount);
        for (var page = 0; page < 3; page++)
        {
            var text = await document.GetPageTextAsync(page, Ct);
            Assert.Equal(0, NonBlank(text.GetText(0, text.Chars.Count)));
            Assert.Equal(360, text.Width, 0.5);
            Assert.Equal(504, text.Height, 0.5);
        }
    }

    [Fact]
    public async Task RotatedFixtureShowsTheSamePageAsTheUprightOne()
    {
        await using var upright = await engine.OpenAsync(TestData.Pdf("ocr-scan.pdf"), cancellationToken: Ct);
        await using var turned = await engine.OpenAsync(TestData.Pdf("ocr-scan-rotated.pdf"), cancellationToken: Ct);
        var a = await upright.RenderPageAsync(0, 1, cancellationToken: Ct);
        var b = await turned.RenderPageAsync(0, 1, cancellationToken: Ct);

        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var different = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var i = (y * a.Stride) + (x * 4);
                if (Math.Abs(a.Pixels.Span[i] - b.Pixels.Span[i]) > 96)
                {
                    different++;
                }
            }
        }

        Assert.True(different < a.Width * a.Height / 100, $"{different} pixels differ between the upright and the turned page");
    }

    [Fact]
    public async Task MixedFixtureHasRealTextOnPageOneAndOnlyANumberOnPageTwo()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("ocr-mixed.pdf"), cancellationToken: Ct);

        Assert.Equal(3, document.PageCount);
        var counts = new List<int>();
        for (var page = 0; page < 3; page++)
        {
            var text = await document.GetPageTextAsync(page, Ct);
            counts.Add(NonBlank(text.GetText(0, text.Chars.Count)));
        }

        Assert.True(counts[0] >= 50, $"page one has {counts[0]} characters");
        Assert.Equal(3, counts[1]);
        Assert.Equal(0, counts[2]);
    }

    [Fact]
    public async Task HostileFixtureHasTheLargestPageAndADenseSpeckPage()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("ocr-hostile.pdf"), cancellationToken: Ct);

        Assert.Equal(2, document.PageCount);
        var first = await document.GetPageSizeAsync(0, Ct);
        Assert.Equal(14400, first.Width, 0.5);
        Assert.Equal(14400, first.Height, 0.5);
        var second = await document.GetPageSizeAsync(1, Ct);
        Assert.Equal(612, second.Width, 0.5);
        Assert.Equal(792, second.Height, 0.5);
    }

    [Fact]
    public async Task EveryKnownWordHasInkInItsBox()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("ocr-scan.pdf"), cancellationToken: Ct);
        Assert.NotEmpty(OcrFixtures.KnownWords);
        foreach (var page in Enumerable.Range(0, 3))
        {
            var image = await document.RenderPageAsync(page, 2, cancellationToken: Ct);
            foreach (var word in OcrFixtures.WordsOf("ocr-scan.pdf", page))
            {
                var dark = 0;
                var total = 0;
                for (var y = (int)(word.Top * 2); y < (int)(word.Bottom * 2); y++)
                {
                    for (var x = (int)(word.Left * 2); x < (int)(word.Right * 2); x++)
                    {
                        total++;
                        if (image.Pixels.Span[(y * image.Stride) + (x * 4)] < 128)
                        {
                            dark++;
                        }
                    }
                }

                Assert.True(dark > total / 30, $"'{word.Text}' on page {page + 1} has {dark} of {total} dark pixels in its box");
            }
        }
    }

    [Theory]
    [InlineData("ocr-scan.pdf", 0, "eng")]
    [InlineData("ocr-scan.pdf", 1, "deu")]
    [InlineData("ocr-scan.pdf", 2, "eng")]
    [InlineData("ocr-scan-rotated.pdf", 0, "eng")]
    public async Task TesseractFindsTheKnownWordsWhereTheyStand(string file, int page, string language)
    {
        OcrTestLibrary.Require();
        var (gray, width, height, scale) = await OcrTestImages.RenderGrayAsync(TestData.Pdf(file), page, Dpi, Ct);
        using var recognizer = OcrEngine.Create(OcrTestLibrary.Tessdata, language);

        var found = recognizer.Recognize(gray, width, height, Dpi, Ct).Words;

        var known = OcrFixtures.WordsOf(file, page);
        var matched = 0;
        foreach (var word in known)
        {
            var box = (Left: word.Left * scale, Top: word.Top * scale, Right: word.Right * scale, Bottom: word.Bottom * scale);
            if (found.Any(candidate => candidate.Text.Trim(',', '.', ';') == word.Text.Trim(',', '.', ';')
                && Math.Abs(candidate.Left - box.Left) < 12 && Math.Abs(candidate.Right - box.Right) < 12
                && Math.Abs(candidate.Top - box.Top) < 16 && Math.Abs(candidate.Bottom - box.Bottom) < 16))
            {
                matched++;
            }
        }

        // Clean synthetic text: nearly every word is found at its place; a lone miss is a recognition slip, not a fixture error.
        Assert.True(matched >= known.Count - 1, $"{matched} of {known.Count} words of {file} page {page + 1} found: {string.Join(' ', found.Select(static w => w.Text))}");
    }
}
