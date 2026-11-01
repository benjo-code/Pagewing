using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>Writing a raster page (milestone 14.1, task 2): one image and nothing else on a page of an empty target document.</summary>
public sealed class RedactRasterTests
{
    private const double Scale = 200 / 72.0;

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Renders a page of a fixture and packs it as a raster page of the same size.</summary>
    private async Task<(RasterPage Page, RenderedImage Image)> RasterOfAsync(string fixture, int pageIndex = 0, int rotation = 0)
    {
        await using var source = await engine.OpenAsync(TestData.Pdf(fixture), cancellationToken: Ct);
        var size = await source.GetPageSizeAsync(pageIndex, Ct);
        var image = await source.RenderPageAsync(pageIndex, Scale, null, Ct);
        return (new RasterPage(size.Width, size.Height, rotation, image.Width, image.Height, Tight(image)), image);
    }

    private static byte[] Tight(RenderedImage image)
    {
        var bytes = new byte[image.Width * image.Height * 4];
        for (var y = 0; y < image.Height; y++)
        {
            image.Pixels.Span.Slice(y * image.Stride, image.Width * 4).CopyTo(bytes.AsSpan(y * image.Width * 4));
        }

        return bytes;
    }

    private async Task<(PdfDocument Document, byte[] Bytes)> WriteAndReopenAsync(TempFolder folder, params RasterPage[] pages)
    {
        await using var target = await engine.CreateEmptyAsync(Ct);
        for (var i = 0; i < pages.Length; i++)
        {
            await target.AddRasterPageAsync(i, pages[i], Ct);
        }

        Assert.Equal(pages.Length, target.PageCount);
        var path = Path.Combine(folder.Path, "raster.pdf");
        await using (var stream = File.Create(path))
        {
            await target.SaveRewriteAsync(stream, Ct);
        }

        return (await engine.OpenAsync(path, cancellationToken: Ct), await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public async Task RasterPageHasOneImageAndNoText()
    {
        using var folder = new TempFolder();
        var (raster, _) = await RasterOfAsync("text-basic.pdf");
        await using var original = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: Ct);
        Assert.True((await original.GetPageTextAsync(0, Ct)).HasText);

        var (written, _) = await WriteAndReopenAsync(folder, raster);
        await using var _1 = written;

        var summary = await written.GetPageObjectSummaryAsync(0, Ct);
        Assert.Equal(new PageObjectSummary(Images: 1, Paths: 0, Texts: 0, Others: 0, Annotations: 0), summary);
        Assert.False((await written.GetPageTextAsync(0, Ct)).HasText);
        var before = await original.GetPageSizeAsync(0, Ct);
        var after = await written.GetPageSizeAsync(0, Ct);
        Assert.Equal(before.Width, after.Width, 0.01);
        Assert.Equal(before.Height, after.Height, 0.01);
    }

    [Fact]
    public async Task RasterPageKeepsTheLookOfTheOriginal()
    {
        using var folder = new TempFolder();
        var (raster, expected) = await RasterOfAsync("text-basic.pdf");

        var (written, _) = await WriteAndReopenAsync(folder, raster);
        await using var _1 = written;
        var actual = await written.RenderPageAsync(0, Scale, null, Ct);

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.True(TolerantlyEqual(expected, actual), "The raster page differs from the original beyond the tolerance of the render regression.");
    }

    [Fact]
    public async Task RasterPageWithoutAlphaWritesNoSoftMask()
    {
        using var folder = new TempFolder();
        var (raster, _) = await RasterOfAsync("text-basic.pdf");

        var (written, bytes) = await WriteAndReopenAsync(folder, raster);
        await using var _1 = written;

        var text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Image", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/SMask", text, StringComparison.Ordinal);
        Assert.Equal(0, (await written.GetPageObjectSummaryAsync(0, Ct)).Others);
    }

    [Fact]
    public async Task RotationIsWrittenOnThePage()
    {
        using var folder = new TempFolder();
        var (raster, _) = await RasterOfAsync("text-basic.pdf", rotation: 90);

        var (written, _) = await WriteAndReopenAsync(folder, raster);
        await using var _1 = written;

        Assert.Equal(90, await written.GetPageRotationAsync(0, Ct));
    }

    [Fact]
    public async Task RasterPagesGoToTheGivenPosition()
    {
        using var folder = new TempFolder();
        var (a4, _) = await RasterOfAsync("text-basic.pdf");
        var small = new RasterPage(100, 50, 0, 10, 5, new byte[10 * 5 * 4]);

        await using var target = await engine.CreateEmptyAsync(Ct);
        await target.AddRasterPageAsync(0, a4, Ct);
        await target.AddRasterPageAsync(0, small, Ct);

        Assert.Equal(2, target.PageCount);
        var first = await target.GetPageSizeAsync(0, Ct);
        Assert.Equal(100, first.Width, 0.01);
        Assert.Equal(50, first.Height, 0.01);
    }

    [Fact]
    public async Task APageThatCannotBeAddedLeavesTheDocumentAsItWas()
    {
        await using var target = await engine.CreateEmptyAsync(Ct);
        var good = new RasterPage(100, 50, 0, 10, 5, new byte[10 * 5 * 4]);

        // Wrong pixel length, an index beyond the end, a size beyond the pixel limit and a bad rotation: all before anything is created.
        await Assert.ThrowsAsync<ArgumentException>(() => target.AddRasterPageAsync(0, good with { Bgra = new byte[10] }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => target.AddRasterPageAsync(1, good, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => target.AddRasterPageAsync(-1, good, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => target.AddRasterPageAsync(0, good with { Rotation = 45 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => target.AddRasterPageAsync(0, good with { WidthPt = 0 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => target.AddRasterPageAsync(0, good with { HeightPt = double.NaN }, Ct));
        var tooMany = good with { WidthPx = 8_000, HeightPx = 4_001 };
        var error = await Assert.ThrowsAsync<RedactionException>(() => target.AddRasterPageAsync(0, tooMany, Ct));
        Assert.Equal(RedactionError.LimitExceeded, error.Error);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => target.AddRasterPageAsync(0, good with { WidthPx = 0 }, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => target.AddRasterPageAsync(0, null!, Ct));

        Assert.Equal(0, target.PageCount);
    }

    [Fact]
    public async Task ThePixelLimitIsCheckedBeforeThePixelsAreLookedAt()
    {
        await using var target = await engine.CreateEmptyAsync(Ct);

        // 8,000 x 4,001 pixels with no pixel data at all: refused on the numbers, no 128 MiB buffer is asked for.
        var page = new RasterPage(100, 50, 0, 8_000, 4_001, []);
        var error = await Assert.ThrowsAsync<RedactionException>(() => target.AddRasterPageAsync(0, page, Ct));

        Assert.Equal(RedactionError.LimitExceeded, error.Error);
    }

    [Fact]
    public async Task ThePageOfATextFixtureHasTextAndNoImage()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: Ct);

        var summary = await document.GetPageObjectSummaryAsync(0, Ct);

        Assert.True(summary.Texts > 0);
        Assert.Equal(0, summary.Images);
    }

    [Fact]
    public async Task AnAnnotationOfAFixtureIsCounted()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("annotations-foreign.pdf"), cancellationToken: Ct);

        var summary = await document.GetPageObjectSummaryAsync(0, Ct);

        Assert.True(summary.Annotations > 0);
    }

    [Fact]
    public async Task ASummaryOfAMissingPageIsRefused()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: Ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetPageObjectSummaryAsync(99, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetPageObjectSummaryAsync(-1, Ct));
    }

    /// <summary>The "tolerant" comparison of the render regression: 8 x 8 block means, at most 40 apart (89 never), and at most 0.27 % of the blocks apart at all.</summary>
    private static bool TolerantlyEqual(RenderedImage expected, RenderedImage actual)
    {
        const int Block = 8;
        var differing = 0;
        var blocks = 0;
        for (var by = 0; by < expected.Height; by += Block)
        {
            for (var bx = 0; bx < expected.Width; bx += Block)
            {
                blocks++;
                var delta = 0.0;
                for (var channel = 0; channel < 3; channel++)
                {
                    delta = Math.Max(delta, Math.Abs(Mean(expected, bx, by, channel) - Mean(actual, bx, by, channel)));
                }

                if (delta >= 89)
                {
                    return false;
                }

                if (delta > 40)
                {
                    differing++;
                }
            }
        }

        return differing <= blocks * 0.0027;
    }

    private static double Mean(RenderedImage image, int x0, int y0, int channel)
    {
        var sum = 0L;
        var count = 0;
        for (var y = y0; y < Math.Min(image.Height, y0 + 8); y++)
        {
            for (var x = x0; x < Math.Min(image.Width, x0 + 8); x++)
            {
                sum += image.Pixels.Span[(y * image.Stride) + (x * 4) + channel];
                count++;
            }
        }

        return (double)sum / count;
    }
}
