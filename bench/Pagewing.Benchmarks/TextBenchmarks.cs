using BenchmarkDotNet.Attributes;
using Pagewing.Core.Pdf;

namespace Pagewing.Benchmarks;

/// <summary>Extracting text from an already open document.</summary>
[MemoryDiagnoser]
public class TextBenchmarks
{
    private readonly PdfEngine engine = new();
    private PdfDocument document = null!;

    [GlobalSetup]
    public async Task Setup() => document = await engine.OpenAsync(BenchmarkData.Pdf("text-basic.pdf"));

    [GlobalCleanup]
    public async Task Cleanup() => await document.DisposeAsync();

    /// <summary>Extracts the text of the first page.</summary>
    [Benchmark]
    public async Task<string> GetTextOfFirstPage() => await document.GetTextAsync(0);
}
