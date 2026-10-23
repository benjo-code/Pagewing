using System.Globalization;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

public sealed class PdfiumRenderWorkTests
{
    private static readonly TimeSpan NoPause = TimeSpan.MaxValue;

    [Fact]
    public async Task InterleavedTileRendersMatchSequentialRenders()
    {
        var thread = PdfiumThread.Shared;

        // Two different tiles of the same page, both crossing the gradient (a shading object, after which
        // PDFium checks the pause callback) and the annotation-free ruler.
        var left = new PdfiumPixelRect(100, 60, 500, 180);
        var right = new PdfiumPixelRect(600, 60, 500, 180);
        var outcome = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("wide-page.pdf"), null);
                var expectedLeft = RenderToEnd(document, 0, 2, left, NoPause);
                var expectedRight = RenderToEnd(document, 0, 2, right, NoPause);

                using var first = new PdfiumRenderWork(document, 0, 2, left, annotations: true, TimeSpan.Zero);
                using var second = new PdfiumRenderWork(document, 0, 2, right, annotations: true, TimeSpan.Zero);
                var order = new List<string>();
                bool firstDone = false, secondDone = false;
                while (!firstDone || !secondDone)
                {
                    if (!firstDone)
                    {
                        firstDone = first.Step();
                        order.Add("first");
                    }

                    if (!secondDone)
                    {
                        secondDone = second.Step();
                        order.Add("second");
                    }
                }

                return (expectedLeft, expectedRight, Left: first.Complete(), Right: second.Complete(), first.StepCount, second.StepCount, order);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        AssertSameImage(outcome.expectedLeft, outcome.Left);
        AssertSameImage(outcome.expectedRight, outcome.Right);
        Assert.False(outcome.Left.Pixels.Span.SequenceEqual(outcome.Right.Pixels.Span));

        // Several steps each, so the renders really alternated on the same page.
        Assert.True(outcome.Item5 > 2, $"first: {outcome.Item5} steps");
        Assert.True(outcome.Item6 > 2, $"second: {outcome.Item6} steps");
        Assert.Equal(["first", "second", "first", "second"], outcome.order.Take(4));
    }

    [Fact]
    public async Task DisposeAfterFirstStepLeavesPdfiumUsable()
    {
        var thread = PdfiumThread.Shared;
        var (expected, firstStepFinished, again) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("wide-page.pdf"), null);
                var reference = RenderToEnd(document, 0, 2, null, NoPause);

                var abandoned = new PdfiumRenderWork(document, 0, 2, null, annotations: true, TimeSpan.Zero);
                var finished = abandoned.Step();
                abandoned.Dispose();
                abandoned.Dispose();

                return (reference, finished, RenderToEnd(document, 0, 2, null, TimeSpan.Zero));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(firstStepFinished);
        AssertSameImage(expected, again);
    }

    [Fact]
    public async Task ZeroTimeSliceYieldsAndUnlimitedSliceRendersInOneStep()
    {
        var thread = PdfiumThread.Shared;
        var (yielding, unlimited, yieldingImage, unlimitedImage) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("wide-page.pdf"), null);
                using var zero = new PdfiumRenderWork(document, 0, 1, null, annotations: true, TimeSpan.Zero);
                using var max = new PdfiumRenderWork(document, 0, 1, null, annotations: true, NoPause);
                while (!zero.Step())
                {
                }

                while (!max.Step())
                {
                }

                return (zero.StepCount, max.StepCount, zero.Complete(), max.Complete());
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(yielding > 1, $"{yielding} steps");
        Assert.Equal(1, unlimited);
        AssertSameImage(unlimitedImage, yieldingImage);
    }

    [Fact]
    public async Task BitmapIsOpaqueBgrxOnWhite()
    {
        var thread = PdfiumThread.Shared;
        var image = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("rotated-page.pdf"), null);
                return RenderToEnd(document, 0, 1, null, NoPause);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((100, 200, 400), (image.Width, image.Height, image.Stride));
        Assert.Equal(400 * 200, image.Pixels.Length);
        for (var i = 3; i < image.Pixels.Length; i += 4)
        {
            Assert.Equal(0xFF, image.Pixels.Span[i]);
        }

        // Blue, green, red, unused: the red square (top left after /Rotate 90) and the white background.
        Assert.Equal([0x00, 0x00, 0xFF, 0xFF], image.Pixels.Span.Slice((10 * 400) + (10 * 4), 4).ToArray());
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], image.Pixels.Span.Slice((190 * 400) + (90 * 4), 4).ToArray());
    }

    [Fact]
    public async Task GlyphsCutByTheLeftTileEdgeMatchTheWholePage()
    {
        // Without TileOverlap PDFium blends a glyph whose origin lies left of the bitmap with the wrong subpixel
        // phase; at scale 1.5 most left edges through this text line differed by up to about 135 per channel.
        var thread = PdfiumThread.Shared;
        var mismatches = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("text-basic.pdf"), null);
                var failed = new List<int>();
                for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
                {
                    var full = RenderToEnd(document, pageIndex, 1.5, null, NoPause);
                    for (var x = 1; x < full.Width; x++)
                    {
                        var tileWidth = Math.Min(32, full.Width - x);
                        var tile = RenderToEnd(document, pageIndex, 1.5, new PdfiumPixelRect(x, 0, tileWidth, full.Height), NoPause);
                        for (var row = 0; row < full.Height; row++)
                        {
                            var expected = full.Pixels.Span.Slice((row * full.Stride) + (x * 4), tileWidth * 4);
                            if (!expected.SequenceEqual(tile.Pixels.Span.Slice(row * tile.Stride, tileWidth * 4)))
                            {
                                failed.Add((pageIndex * 1000) + x);
                                break;
                            }
                        }
                    }
                }

                return failed;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(mismatches);
    }

    [Fact]
    public void PagePixelSizeRoundsUpAndChecksTheRange()
    {
        Assert.Equal((300, 200), PdfiumRenderWork.PagePixelSize(300, 200, 1));
        Assert.Equal((451, 301), PdfiumRenderWork.PagePixelSize(300.5, 200.5, 1.5));
        Assert.Equal((1, 1), PdfiumRenderWork.PagePixelSize(300, 200, 1e-9));
        Assert.Equal((int.MaxValue, 1), PdfiumRenderWork.PagePixelSize(int.MaxValue, 1, 1));
        Assert.Throws<PdfiumRenderException>(() => PdfiumRenderWork.PagePixelSize(int.MaxValue + 1.0, 1, 1));
        Assert.Throws<PdfiumRenderException>(() => PdfiumRenderWork.PagePixelSize(300, 200, 1e12));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumRenderWork.PagePixelSize(300, 200, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumRenderWork.PagePixelSize(300, 200, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumRenderWork.PagePixelSize(300, 200, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumRenderWork.PagePixelSize(300, 200, double.PositiveInfinity));
    }

    [Fact]
    public void PageTooLargeMessageIsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var exception = Assert.Throws<PdfiumRenderException>(() => PdfiumRenderWork.PagePixelSize(3_000_000_000.5, 1, 1));

            Assert.Contains("(3000000000.5 pixels)", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task ConstructorChecksTileAndSizeLimitOnThePdfiumThread()
    {
        var thread = PdfiumThread.Shared;
        var path = TestData.Pdf("wide-page.pdf");
        var document = await thread.InvokeAsync(() => PdfiumDocument.Open(thread, path, null), cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            Assert.Throws<PdfiumThreadViolationException>(() => new PdfiumRenderWork(document, 0, 1, null, true, NoPause));

            await thread.InvokeAsync(
                () =>
                {
                    // The page is 600 x 120 pixels at scale 1.
                    Assert.Throws<ArgumentOutOfRangeException>(() => new PdfiumRenderWork(document, 0, 1, new PdfiumPixelRect(0, 0, 601, 1), true, NoPause));
                    Assert.Throws<ArgumentOutOfRangeException>(() => new PdfiumRenderWork(document, 0, 1, new PdfiumPixelRect(0, 120, 1, 1), true, NoPause));
                    Assert.Throws<ArgumentOutOfRangeException>(() => new PdfiumRenderWork(document, 0, 1, new PdfiumPixelRect(0, 0, 0, 1), true, NoPause));
                    Assert.Throws<ArgumentOutOfRangeException>(() => new PdfiumRenderWork(document, 0, 1, null, true, TimeSpan.FromTicks(-1)));
                    Assert.Throws<ArgumentOutOfRangeException>(() => new PdfiumRenderWork(document, 1, 1, null, true, NoPause));

                    // 60 000 x 12 000 = 720 000 000 pixels.
                    Assert.Throws<PdfiumRenderException>(() => new PdfiumRenderWork(document, 0, 100, null, true, NoPause));
                    using var largest = new PdfiumRenderWork(document, 0, 1, new PdfiumPixelRect(0, 0, 600, 120), true, NoPause);
                },
                cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            document.Dispose();
        }
    }

    /// <summary>Renders on the PDFium thread from start to end and disposes the work.</summary>
    private static PdfiumImage RenderToEnd(PdfiumDocument document, int pageIndex, double scale, PdfiumPixelRect? tile, TimeSpan slice)
    {
        using var work = new PdfiumRenderWork(document, pageIndex, scale, tile, annotations: true, slice);
        while (!work.Step())
        {
        }

        return work.Complete();
    }

    private static void AssertSameImage(PdfiumImage expected, PdfiumImage actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Stride), (actual.Width, actual.Height, actual.Stride));
        Assert.True(expected.Pixels.Span.SequenceEqual(actual.Pixels.Span), "The pixels differ.");
    }
}
