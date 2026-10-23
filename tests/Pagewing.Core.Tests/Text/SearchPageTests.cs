using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Text;

public sealed class SearchPageTests
{
    private readonly PdfEngine engine = new();

    [Fact]
    public async Task FindsEveryOccurrenceOnAPageWithPositions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        // Heading, first sentence and the address in the URL line.
        var hits = await document.SearchPageAsync(0, "pagewing", default, ct);

        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal(0, h.PageIndex));
        Assert.Equal(0, hits[0].FirstChar);
        Assert.Equal(7, hits[0].LastChar);
        var rect = Assert.Single(hits[0].Rects);
        Assert.InRange(rect.Left, 38, 42);
        Assert.InRange(rect.Top, 20, 60);
        Assert.True(rect.Width > 50 && rect.Height > 10);
        Assert.Equal(new SearchSnippet("", "Pagewing", " Handbuch"), hits[0].Snippet);
        Assert.Equal(new SearchSnippet("", "Pagewing", " ist ein schneller PDF-Viewer "), hits[1].Snippet);
        Assert.Equal("pagewing", hits[2].Snippet.Match);
        Assert.EndsWith("https://example.org/", hits[2].Snippet.Before, StringComparison.Ordinal);
        Assert.True(hits[0].LastChar < hits[1].FirstChar && hits[1].LastChar < hits[2].FirstChar);

        var text = await document.GetPageTextAsync(0, ct);
        Assert.All(hits, h => Assert.Equal(h.Rects, text.RectsOf(h.FirstChar, h.LastChar + 1)));
    }

    [Fact]
    public async Task MatchCaseOnlyFindsTheCapitalisedOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        var hits = await document.SearchPageAsync(0, "Pagewing", new SearchOptions(MatchCase: true), ct);
        var second = await document.SearchPageAsync(1, "Pagewing", new SearchOptions(MatchCase: true), ct);

        Assert.Equal(2, hits.Count);
        var hit = Assert.Single(second);
        Assert.Equal(1, hit.PageIndex);
        Assert.Equal(new SearchSnippet("Ziel: ", "Pagewing", " am Ende."), hit.Snippet);
    }

    [Fact]
    public async Task FindsAWordSplitByAHyphenatedLineBreak()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        var hit = Assert.Single(await document.SearchPageAsync(0, "Windows", default, ct));

        Assert.Equal(2, hit.Rects.Count);
        Assert.True(hit.Rects[1].Top > hit.Rects[0].Bottom - 1); // the second line lies below the first
        Assert.Equal("Windows", hit.Snippet.Match);
        Assert.Equal(" und Linux. Er startet sofort ", hit.Snippet.After);
    }

    [Fact]
    public async Task FindsAcrossFontChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        var hit = Assert.Single(await document.SearchPageAsync(0, "fett und kursiv", default, ct));

        Assert.Single(hit.Rects);
        Assert.Equal(new SearchSnippet("Das ist ", "fett und kursiv", " im Satz."), hit.Snippet);
    }

    [Fact]
    public async Task WholeWordDoesNotFindPartsOfWords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        Assert.NotEmpty(await document.SearchPageAsync(0, "zweit", default, ct));
        Assert.Empty(await document.SearchPageAsync(0, "zweit", new SearchOptions(WholeWord: true), ct));
        Assert.Single(await document.SearchPageAsync(0, "Zweite", new SearchOptions(MatchCase: true, WholeWord: true), ct));
    }

    [Fact]
    public async Task RotatedPageHitsLieOnTheText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("rotated-page.pdf"), cancellationToken: ct);
        var size = await document.GetPageSizeAsync(0, ct);

        var hit = Assert.Single(await document.SearchPageAsync(0, "Rotated", default, ct));

        var rect = Assert.Single(hit.Rects);
        Assert.InRange(rect.Left, 0, size.Width);
        Assert.InRange(rect.Right, 0, size.Width);
        Assert.InRange(rect.Top, 0, size.Height);
        Assert.InRange(rect.Bottom, 0, size.Height);
        Assert.True(rect.Height > rect.Width); // the page is rotated by 90 degrees, so the text runs vertically
    }

    [Fact]
    public async Task PageWithoutTextHasNoHits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("vector-shapes.pdf"), cancellationToken: ct);

        Assert.Empty(await document.SearchPageAsync(0, "a", default, ct));
    }

    [Fact]
    public async Task UmlautsAreFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);

        Assert.Single(await document.SearchPageAsync(1, "größe", default, ct));
        Assert.Single(await document.SearchPageAsync(1, "ÄÖÜ", default, ct));
        Assert.Empty(await document.SearchPageAsync(1, "GRÖSSE", default, ct)); // no folding of ß to ss
    }

    [Fact]
    public async Task InvalidPageIndexThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.SearchPageAsync(2, "a", default, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.SearchPageAsync(-1, "a", default, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => document.SearchPageAsync(0, null!, default, ct));
    }

    [Fact]
    public async Task CancelledSearchThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.SearchPageAsync(0, "Pagewing", default, cancelled.Token));
    }
}
