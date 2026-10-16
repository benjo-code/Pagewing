using BenchmarkDotNet.Attributes;
using Pagewing.Core.Pdf;

namespace Pagewing.Benchmarks;

/// <summary>
/// Opening documents. <see cref="OpenThousandPagesAndRenderFirstPage"/> corresponds to the speed budget
/// "open a 1000-page PDF until the first page" (rendered here at 144 dpi, without the UI).
/// </summary>
[MemoryDiagnoser]
public class OpenBenchmarks
{
    private readonly PdfEngine engine = new();
    private string smallPath = string.Empty;
    private string thousandPagesPath = string.Empty;

    [GlobalSetup]
    public async Task Setup()
    {
        smallPath = BenchmarkData.Pdf("text-basic.pdf");
        thousandPagesPath = BenchmarkData.Pdf("thousand-pages.pdf");

        // Starts the PDFium thread and loads the library, so the first measured iteration does not pay for it.
        await using var warmUp = await engine.OpenAsync(smallPath);
    }

    /// <summary>Opens and closes the small text PDF.</summary>
    [Benchmark]
    public async Task<int> OpenSmall()
    {
        await using var document = await engine.OpenAsync(smallPath);
        return document.PageCount;
    }

    /// <summary>Opens and closes the 1000-page PDF.</summary>
    [Benchmark]
    public async Task<int> OpenThousandPages()
    {
        await using var document = await engine.OpenAsync(thousandPagesPath);
        return document.PageCount;
    }

    /// <summary>Opens the 1000-page PDF and renders its first page at 144 dpi, then closes it.</summary>
    [Benchmark]
    public async Task<int> OpenThousandPagesAndRenderFirstPage()
    {
        await using var document = await engine.OpenAsync(thousandPagesPath);
        var image = await document.RenderPageAsync(0, scale: 2);
        return image.Width;
    }
}
