using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Text;

/// <summary>Pins the hit counts the search benchmarks (<c>bench/Pagewing.Benchmarks/TextBenchmarks.cs</c>) rely on.</summary>
public sealed class SearchBenchmarkDataTests
{
    private readonly PdfEngine engine = new();

    private async Task<List<SearchPageResult>> SearchAsync(string file, string query, SearchOptions options)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: ct);
        var results = new List<SearchPageResult>();
        await foreach (var result in DocumentSearch.SearchAsync(document, query, options, 0, ct))
        {
            results.Add(result);
        }

        return results;
    }

    [Fact]
    public async Task ThousandPagesFindsPage99AndPages990To999()
    {
        var results = await SearchAsync("thousand-pages.pdf", "Page 99", default);

        Assert.Equal(11, results.Sum(r => r.Hits.Count));
        var pages = results.Where(r => r.Hits.Count > 0).Select(r => r.PageIndex).ToList();
        Assert.Equal(Enumerable.Range(98, 1).Concat(Enumerable.Range(989, 10)), pages);
        Assert.False(results[^1].LimitReached);
    }

    [Fact]
    public async Task DenseDocumentFindsTwentyNeedles()
    {
        var results = await SearchAsync("text-dense.pdf", "Needle", default);

        Assert.Equal(20, results.Sum(r => r.Hits.Count));
        Assert.False(results[^1].LimitReached);
    }

    [Fact]
    public async Task DenseDocumentHitsTheLimitForTheLetterA()
    {
        var results = await SearchAsync("text-dense.pdf", "a", new SearchOptions(MatchCase: false));

        Assert.Equal(DocumentSearch.MaxHits, results.Sum(r => r.Hits.Count));
        Assert.True(results[^1].LimitReached);
        Assert.All(results.Take(results.Count - 1), r => Assert.False(r.LimitReached));
    }
}
