using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdf;

public sealed class RenderTests
{
    private static readonly Bgra White = new(0xFF, 0xFF, 0xFF, 0xFF);

    private static readonly Bgra Red = new(0x00, 0x00, 0xFF, 0xFF);

    private readonly PdfEngine engine = new();

    [Fact]
    public async Task RenderPageHasExpectedSizeAndWhiteBackground()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        var size = await document.GetPageSizeAsync(0, ct);

        var image = await document.RenderPageAsync(0, 1, cancellationToken: ct);

        Assert.Equal(size.ToPixels(1), (image.Width, image.Height));
        Assert.Equal((300, 200), (image.Width, image.Height));
        Assert.Equal(image.Width * 4, image.Stride);
        Assert.Equal(image.Stride * image.Height, image.Pixels.Length);
        Assert.Equal(White, Pixel(image, 0, 0));
        Assert.Contains(AllPixels(image), pixel => pixel != White);
        Assert.All(AllPixels(image), pixel => Assert.Equal(0xFF, pixel.A));
    }

    [Fact]
    public async Task ScaleTwoDoublesTheSize()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);

        var one = await document.RenderPageAsync(0, 1, cancellationToken: ct);
        var two = await document.RenderPageAsync(0, 2, cancellationToken: ct);

        Assert.Equal((one.Width * 2, one.Height * 2), (two.Width, two.Height));
        Assert.Equal((600, 400), (two.Width, two.Height));
    }

    [Fact]
    public void ToPixelsRoundsUpAndIsAtLeastOne()
    {
        Assert.Equal((300, 200), new PdfPageSize(300, 200).ToPixels(1));
        Assert.Equal((451, 301), new PdfPageSize(300.5, 200.5).ToPixels(1.5));
        Assert.Equal((1, 1), new PdfPageSize(300, 200).ToPixels(0.0001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageSize(300, 200).ToPixels(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfPageSize(300, 200).ToPixels(double.NaN));
        Assert.Throws<PdfRenderException>(() => new PdfPageSize(300, 200).ToPixels(1e12));
    }

    [Fact]
    public async Task RenderIsDeterministic()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("vector-shapes.pdf"), cancellationToken: ct);

        var first = await document.RenderPageAsync(0, 1.25, cancellationToken: ct);
        var second = await document.RenderPageAsync(0, 1.25, cancellationToken: ct);

        AssertSameImage(first, second);
    }

    [Fact]
    public async Task TilesStitchToTheFullPage()
    {
        const int TileSize = 128;
        const double Scale = 1.5;
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("wide-page.pdf"), cancellationToken: ct);
        var full = await document.RenderPageAsync(0, Scale, cancellationToken: ct);
        Assert.Equal((900, 180), (full.Width, full.Height));

        var stitched = new byte[full.Pixels.Length];
        var tileCount = 0;
        for (var y = 0; y < full.Height; y += TileSize)
        {
            for (var x = 0; x < full.Width; x += TileSize)
            {
                // Edge tiles are smaller (900 = 7 * 128 + 4, 180 = 128 + 52).
                var rect = new PixelRect(x, y, Math.Min(TileSize, full.Width - x), Math.Min(TileSize, full.Height - y));
                var tile = await document.RenderTileAsync(0, Scale, rect, cancellationToken: ct);
                Assert.Equal((rect.Width, rect.Height), (tile.Width, tile.Height));
                for (var row = 0; row < tile.Height; row++)
                {
                    tile.Pixels.Span.Slice(row * tile.Stride, tile.Width * 4)
                        .CopyTo(stitched.AsSpan(((y + row) * full.Stride) + (x * 4)));
                }

                tileCount++;
            }
        }

        Assert.Equal(16, tileCount);
        Assert.True(full.Pixels.Span.SequenceEqual(stitched), DescribeDifference(full.Pixels.Span, stitched));
    }

    [Fact]
    public async Task RotatedPageSwapsSize()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("rotated-page.pdf"), cancellationToken: ct);

        var image = await document.RenderPageAsync(0, 1, cancellationToken: ct);

        // MediaBox 200 x 100 with /Rotate 90 is shown as 100 x 200.
        Assert.Equal((100, 200), (image.Width, image.Height));

        // The red square in the unrotated lower-left corner ends up top left after a clockwise quarter turn.
        Assert.Equal(Red, Pixel(image, 10, 10));
        Assert.Equal(White, Pixel(image, 10, 190));
        Assert.Equal(White, Pixel(image, 90, 190));
    }

    [Fact]
    public async Task CropBoxDefinesTheSize()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("cropbox.pdf"), cancellationToken: ct);

        var image = await document.RenderPageAsync(0, 1, cancellationToken: ct);

        // MediaBox 300 x 300, CropBox [50 75 250 225] gives 200 x 150.
        Assert.Equal((200, 150), (image.Width, image.Height));

        // The coloured corner squares lie outside the CropBox; only the yellowish page fill is visible there.
        Assert.DoesNotContain(AllPixels(image), pixel => pixel == Red);
        Assert.Equal(new Bgra(0x99, 0xE6, 0xE6, 0xFF), Pixel(image, 20, 20));
    }

    [Fact]
    public async Task AnnotationsAreRenderedByDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("annotations.pdf"), cancellationToken: ct);

        var byDefault = await document.RenderPageAsync(0, 1, cancellationToken: ct);
        var with = await document.RenderPageAsync(0, 1, new RenderOptions { Annotations = true }, ct);
        var without = await document.RenderPageAsync(0, 1, new RenderOptions { Annotations = false }, ct);

        AssertSameImage(byDefault, with);
        Assert.False(with.Pixels.Span.SequenceEqual(without.Pixels.Span));

        // The Square annotation (Rect [30 30 130 90], red border) appears only with annotations.
        Assert.Equal(Red, Pixel(with, 31, 200 - 60));
        Assert.Equal(White, Pixel(without, 31, 200 - 60));
    }

    [Fact]
    public async Task InvalidArgumentsThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        var valid = new PixelRect(0, 0, 10, 10);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderPageAsync(-1, 1, cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderPageAsync(document.PageCount, 1, cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderTileAsync(-1, 1, valid, cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderTileAsync(document.PageCount, 1, valid, cancellationToken: ct));

        foreach (var scale in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderPageAsync(0, scale, cancellationToken: ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderTileAsync(0, scale, valid, cancellationToken: ct));
        }

        // The page is 300 x 200 pixels at scale 1.
        foreach (var tile in new[]
        {
            new PixelRect(0, 0, 0, 10),
            new PixelRect(0, 0, 10, 0),
            new PixelRect(-1, 0, 10, 10),
            new PixelRect(0, -1, 10, 10),
            new PixelRect(291, 0, 10, 10),
            new PixelRect(0, 191, 10, 10),
            new PixelRect(300, 0, 1, 1),
            new PixelRect(int.MaxValue, 0, 1, 1),
            new PixelRect(0, 0, int.MaxValue, 1),
        })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderTileAsync(0, 1, tile, cancellationToken: ct));
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => document.RenderPageAsync(0, 1, new RenderOptions { TimeSlice = TimeSpan.FromTicks(-1) }, ct));

        // The bottom-right tile that just fits is valid.
        var corner = await document.RenderTileAsync(0, 1, new PixelRect(290, 190, 10, 10), cancellationToken: ct);
        Assert.Equal((10, 10), (corner.Width, corner.Height));
    }

    [Fact]
    public async Task HugeScaleThrowsRenderException()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);

        // 30 000 000 x 20 000 000 pixels: far above the limit, rejected before any allocation.
        await Assert.ThrowsAsync<PdfRenderException>(() => document.RenderPageAsync(0, 100000, cancellationToken: ct));

        // The page width in pixels no longer fits in an int.
        await Assert.ThrowsAsync<PdfRenderException>(() => document.RenderPageAsync(0, 1e12, cancellationToken: ct));
        await Assert.ThrowsAsync<PdfRenderException>(
            () => document.RenderTileAsync(0, 1e12, new PixelRect(0, 0, 10, 10), cancellationToken: ct));

        // The thread is still usable.
        var image = await document.RenderPageAsync(0, 1, cancellationToken: ct);
        Assert.Equal((300, 200), (image.Width, image.Height));
    }

    [Fact]
    public async Task TooLargeTileThrowsRenderException()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("wide-page.pdf"), cancellationToken: ct);

        // At scale 100 the page is 60 000 x 12 000 pixels; a 30 000 x 10 000 tile lies inside it but has
        // 300 000 000 pixels, more than the 268 435 456 allowed.
        await Assert.ThrowsAsync<PdfRenderException>(
            () => document.RenderTileAsync(0, 100, new PixelRect(0, 0, 30000, 10000), cancellationToken: ct));

        // A small tile of the same huge page renders.
        var tile = await document.RenderTileAsync(0, 100, new PixelRect(1000, 1000, 64, 64), cancellationToken: ct);
        Assert.Equal((64, 64), (tile.Width, tile.Height));
    }

    [Fact]
    public async Task ConcurrentRendersAllSucceedAndMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("wide-page.pdf"), cancellationToken: ct);
        var expectedPage = await document.RenderPageAsync(0, 2, cancellationToken: ct);
        var tileRect = new PixelRect(300, 100, 200, 100);
        var expectedTile = await document.RenderTileAsync(0, 2, tileRect, cancellationToken: ct);

        var renders = Enumerable.Range(0, 16)
            .Select(i => Task.Run(
                () =>
                {
                    var options = new RenderOptions
                    {
                        Priority = i % 2 == 0 ? PdfPriority.Interactive : PdfPriority.Background,
                        TimeSlice = i % 4 < 2 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(4),
                    };
                    return i % 3 == 0
                        ? document.RenderTileAsync(0, 2, tileRect, options, ct)
                        : document.RenderPageAsync(0, 2, options, ct);
                },
                ct))
            .ToArray();
        var results = await Task.WhenAll(renders);

        for (var i = 0; i < results.Length; i++)
        {
            AssertSameImage(i % 3 == 0 ? expectedTile : expectedPage, results[i]);
        }
    }

    [Fact]
    public async Task CancelBeforeStartDoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.RenderPageAsync(0, 1, cancellationToken: source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => document.RenderTileAsync(0, 1, new PixelRect(0, 0, 10, 10), cancellationToken: source.Token));

        var image = await document.RenderPageAsync(0, 1, cancellationToken: ct);
        Assert.Equal((300, 200), (image.Width, image.Height));
    }

    [Fact]
    public async Task CancelDuringRenderThrowsOperationCanceled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("wide-page.pdf"), cancellationToken: ct);
        var expected = await document.RenderPageAsync(0, 3, cancellationToken: ct);
        var options = new RenderOptions { TimeSlice = TimeSpan.Zero, Priority = PdfPriority.Background };

        var outcomes = new List<string>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var source = new CancellationTokenSource();
            var render = document.RenderPageAsync(0, 3, options, source.Token);
            var cancel = Task.Run(source.Cancel, ct);
            try
            {
                // Either the cancel arrives between two steps, or the render already finished.
                AssertSameImage(expected, await render);
                outcomes.Add("completed");
            }
            catch (OperationCanceledException)
            {
                Assert.True(render.IsCanceled);
                outcomes.Add("canceled");
            }

            await cancel;
        }

        Assert.Equal(20, outcomes.Count);
        AssertSameImage(expected, await document.RenderPageAsync(0, 3, options, ct));
    }

    [Fact]
    public async Task RenderAfterDisposeThrowsObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);

        await document.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.RenderPageAsync(0, 1, cancellationToken: ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => document.RenderTileAsync(0, 1, new PixelRect(0, 0, 10, 10), cancellationToken: ct));
    }

    [Fact]
    public async Task DisposeDuringRenderLetsTheRenderFinishOrFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await engine.OpenAsync(TestData.Pdf("wide-page.pdf"), cancellationToken: ct);
        var options = new RenderOptions { TimeSlice = TimeSpan.Zero, Priority = PdfPriority.Background };

        var render = document.RenderPageAsync(0, 3, options, ct);
        document.Dispose();

        // The page keeps the document open, so a started render completes; one that had not started yet
        // finds the document closed.
        try
        {
            var image = await render;
            Assert.Equal((1800, 360), (image.Width, image.Height));
        }
        catch (ObjectDisposedException)
        {
        }

        // PDFium stays usable afterwards.
        await using var other = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        var check = await other.RenderPageAsync(0, 1, cancellationToken: ct);
        Assert.Equal((300, 200), (check.Width, check.Height));
    }

    private static Bgra Pixel(RenderedImage image, int x, int y)
    {
        var span = image.Pixels.Span.Slice((y * image.Stride) + (x * 4), 4);
        return new Bgra(span[0], span[1], span[2], span[3]);
    }

    private static IEnumerable<Bgra> AllPixels(RenderedImage image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                yield return Pixel(image, x, y);
            }
        }
    }

    private static void AssertSameImage(RenderedImage expected, RenderedImage actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Stride), (actual.Width, actual.Height, actual.Stride));
        Assert.True(expected.Pixels.Span.SequenceEqual(actual.Pixels.Span), DescribeDifference(expected.Pixels.Span, actual.Pixels.Span));
    }

    private static string DescribeDifference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var differentPixels = 0;
        var maxDelta = 0;
        for (var i = 0; i < Math.Min(expected.Length, actual.Length); i += 4)
        {
            var delta = 0;
            for (var c = 0; c < 4; c++)
            {
                delta = Math.Max(delta, Math.Abs(expected[i + c] - actual[i + c]));
            }

            if (delta > 0)
            {
                differentPixels++;
                maxDelta = Math.Max(maxDelta, delta);
            }
        }

        return $"{differentPixels} of {expected.Length / 4} pixels differ, max delta {maxDelta} (lengths {expected.Length}/{actual.Length}).";
    }

    private readonly record struct Bgra(byte B, byte G, byte R, byte A);
}
