using BenchmarkDotNet.Attributes;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.Benchmarks;

/// <summary>Extracting text from, and searching in, already open documents.</summary>
[MemoryDiagnoser]
public class TextBenchmarks
{
    private readonly PdfEngine engine = new();
    private PdfDocument document = null!;
    private PdfDocument thousandPages = null!;
    private PdfDocument denseDocument = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        document = await engine.OpenAsync(BenchmarkData.Pdf("text-basic.pdf"));
        thousandPages = await engine.OpenAsync(BenchmarkData.Pdf("thousand-pages.pdf"));
        denseDocument = await engine.OpenAsync(BenchmarkData.Pdf("text-dense.pdf"));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await document.DisposeAsync();
        await thousandPages.DisposeAsync();
        await denseDocument.DisposeAsync();
    }

    /// <summary>Extracts the text of the first page.</summary>
    [Benchmark]
    public async Task<string> GetTextOfFirstPage() => await document.GetTextAsync(0);

    /// <summary>Searches 1000 pages for "Page 99" (11 hits: page 99 and pages 990 to 999).</summary>
    [Benchmark]
    public Task<int> SearchThousandPages() => CountHitsAsync(thousandPages, "Page 99", default);

    /// <summary>Searches 200 text-dense pages for "Needle" (20 hits, one on every 10th page).</summary>
    [Benchmark]
    public Task<int> SearchDenseDocument() => CountHitsAsync(denseDocument, "Needle", default);

    /// <summary>Searches the dense pages for "a" until the search stops at <see cref="DocumentSearch.MaxHits"/> (hit-heavy pages).</summary>
    [Benchmark]
    public Task<int> SearchManyHitsDenseDocument() => CountHitsAsync(denseDocument, "a", new SearchOptions(MatchCase: false));

    private static async Task<int> CountHitsAsync(PdfDocument target, string query, SearchOptions options)
    {
        var hits = 0;
        await foreach (var result in DocumentSearch.SearchAsync(target, query, options, 0))
        {
            hits += result.Hits.Count;
        }

        return hits;
    }
}
