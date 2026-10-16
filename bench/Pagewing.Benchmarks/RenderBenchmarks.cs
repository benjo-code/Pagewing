using BenchmarkDotNet.Attributes;
using Pagewing.Core.Pdf;

namespace Pagewing.Benchmarks;

/// <summary>
/// Rendering a page and a tile of an already open document. The test page is 300 x 300 pt, so at 200 % it is
/// 600 x 600 px and a 512 px tile fits in it.
/// </summary>
[MemoryDiagnoser]
public class RenderBenchmarks
{
    private readonly PdfEngine engine = new();
    private PdfDocument document = null!;

    [GlobalSetup]
    public async Task Setup() => document = await engine.OpenAsync(BenchmarkData.Pdf("vector-shapes.pdf"));

    [GlobalCleanup]
    public async Task Cleanup() => await document.DisposeAsync();

    /// <summary>Renders the whole first page at 144 dpi (scale 2).</summary>
    [Benchmark]
    public async Task<int> RenderFirstPage144Dpi()
    {
        var image = await document.RenderPageAsync(0, scale: 2);
        return image.Width;
    }

    /// <summary>Renders a 512 x 512 px tile inside the first page at 200 %.</summary>
    /// <remarks>200 % means two pixels per point; the tile is the one at x = 88, y = 88 (centered in the 600 x 600 px page).</remarks>
    [Benchmark]
    public async Task<int> RenderTile512At200Percent()
    {
        var image = await document.RenderTileAsync(0, scale: 2, new PixelRect(88, 88, 512, 512));
        return image.Width;
    }
}
