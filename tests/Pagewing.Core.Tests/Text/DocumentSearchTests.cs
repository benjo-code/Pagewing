using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Text;

public sealed class DocumentSearchTests
{
    private readonly PdfEngine engine = new();

    private static async Task<List<SearchPageResult>> CollectAsync(IAsyncEnumerable<SearchPageResult> results)
    {
        var list = new List<SearchPageResult>();
        await foreach (var result in results)
        {
            list.Add(result);
        }

        return list;
    }

    [Fact]
    public async Task SearchesFromTheStartPageAndWrapsAround()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);

        var results = await CollectAsync(DocumentSearch.SearchAsync(document, "Needle", default, 100, ct));

        Assert.Equal(200, results.Count);
        var expectedOrder = Enumerable.Range(100, 100).Concat(Enumerable.Range(0, 100));
        Assert.Equal(expectedOrder, results.Select(r => r.PageIndex));
        var pagesWithHits = results.Where(r => r.Hits.Count > 0).Select(r => r.PageIndex).ToList();
        Assert.Equal(20, pagesWithHits.Count);
        Assert.All(pagesWithHits, p => Assert.Equal(9, p % 10));
        Assert.All(results, r => Assert.All(r.Hits, h => Assert.Equal(r.PageIndex, h.PageIndex)));
    }

    [Fact]
    public async Task FindsAllTwentyNeedlesInTheDenseDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);

        var results = await CollectAsync(DocumentSearch.SearchAsync(document, "Needle", default, 0, ct));

        var total = results.Sum(r => r.Hits.Count);
        var last = results[^1];
        Assert.Equal(20, total);
        Assert.Equal(200, last.PagesDone);
        Assert.Equal(200, last.PageCount);
        Assert.False(last.LimitReached);
    }

    [Fact]
    public async Task ResultsCarryProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);

        var results = await CollectAsync(DocumentSearch.SearchAsync(document, "Needle", default, 50, ct));

        for (var i = 0; i < results.Count; i++)
        {
            Assert.Equal(i + 1, results[i].PagesDone);
            Assert.Equal(document.PageCount, results[i].PageCount);
        }

        Assert.Equal(document.PageCount, results[^1].PagesDone);
    }

    [Fact]
    public async Task StopsAtTheHitLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);

        var results = await CollectAsync(DocumentSearch.SearchAsync(document, "a", default, 0, ct));

        var last = results[^1];
        Assert.Equal(DocumentSearch.MaxHits, results.Sum(r => r.Hits.Count));
        Assert.True(last.LimitReached);
        Assert.True(last.PagesDone < 200);
        Assert.All(results.Take(results.Count - 1), r => Assert.False(r.LimitReached));
    }

    [Fact]
    public async Task CancellationStopsTheSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var result in DocumentSearch.SearchAsync(document, "Needle", default, 0, cts.Token))
            {
                seen++;
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task SearchRunsAtBackgroundPriorityAndLetsRendersThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);

        // The search is consumed without delay; once its first result is in, a render is queued. Searches are
        // background work, so the render must finish while the search is still far from done.
        var firstResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchedPages = 0;
        var search = Task.Run(async () =>
        {
            await foreach (var result in DocumentSearch.SearchAsync(document, "a", new SearchOptions(MatchCase: true), 0, ct))
            {
                Volatile.Write(ref searchedPages, result.PagesDone);
                firstResult.TrySetResult();
            }
        }, ct);

        await firstResult.Task.WaitAsync(ct);
        var image = await document.RenderPageAsync(0, 0.25, cancellationToken: ct);
        var pagesDoneAtRender = Volatile.Read(ref searchedPages);
        var searchFinishedBeforeRender = search.IsCompleted;

        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.False(searchFinishedBeforeRender);
        Assert.True(pagesDoneAtRender < document.PageCount, $"The search had already searched {pagesDoneAtRender} of {document.PageCount} pages.");

        await search;
    }

    [Fact]
    public async Task ADocumentWithoutTextYieldsNoHits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("vector-shapes.pdf"), cancellationToken: ct);

        var results = await CollectAsync(DocumentSearch.SearchAsync(document, "a", default, 0, ct));

        Assert.Equal(document.PageCount, results.Count);
        Assert.All(results, r => Assert.Empty(r.Hits));
        Assert.Equal(document.PageCount, results[^1].PagesDone);
        Assert.All(results, r => Assert.False(r.LimitReached));
    }

    [Fact]
    public async Task ADisposedDocumentThrowsObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);
        await document.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => CollectAsync(DocumentSearch.SearchAsync(document, "Needle", default, 0, ct)));
    }
}
