using Pagewing.Core.Pdf;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdf;

public sealed class OutlineTests
{
    private readonly PdfEngine engine = new();

    [Fact]
    public async Task OutlineHasTheExpectedTree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("outline.pdf"), cancellationToken: ct);

        var outline = await document.GetOutlineAsync(ct);

        Assert.Collection(
            outline,
            chapter1 =>
            {
                Assert.Equal("Kapitel 1", chapter1.Title);
                Assert.Equal(0, chapter1.PageIndex);
                Assert.True(chapter1.IsOpen);
                Assert.Collection(
                    chapter1.Children,
                    section11 =>
                    {
                        Assert.Equal("Abschnitt 1.1", section11.Title);
                        Assert.Equal(1, section11.PageIndex);
                        Assert.False(section11.IsOpen);
                        Assert.Empty(section11.Children);
                    },
                    section12 =>
                    {
                        Assert.Equal("Abschnitt 1.2", section12.Title);
                        Assert.Null(section12.PageIndex);
                        Assert.Empty(section12.Children);
                    });
            },
            chapter2 =>
            {
                // The destination comes from a GoTo action, not from /Dest.
                Assert.Equal("Kapitel 2", chapter2.Title);
                Assert.Equal(2, chapter2.PageIndex);
                Assert.False(chapter2.IsOpen);
                Assert.Single(chapter2.Children);
            },
            far => Assert.Equal("Fern", far.Title));
    }

    [Fact]
    public async Task UnicodeTitlesSurvive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("outline.pdf"), cancellationToken: ct);

        var outline = await document.GetOutlineAsync(ct);

        var about = Assert.Single(outline[1].Children);
        Assert.Equal("Über uns – 日本", about.Title);
        Assert.Equal(2, about.PageIndex);
    }

    [Fact]
    public async Task InvalidPageTargetBecomesNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("outline.pdf"), cancellationToken: ct);

        var outline = await document.GetOutlineAsync(ct);

        Assert.Equal("Fern", outline[2].Title);
        Assert.Null(outline[2].PageIndex);
    }

    [Fact]
    public async Task CyclicOutlineEndsAndReturnsEachItemOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("outline-cycle.pdf"), cancellationToken: ct);

        var outline = await document.GetOutlineAsync(ct);

        Assert.Equal(["Eins", "Zwei", "Drei"], outline.Select(item => item.Title));
        Assert.Equal([0, 1, 2], outline.Select(item => item.PageIndex));
    }

    [Fact]
    public async Task DocumentWithoutOutlineGivesAnEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("hello_world.pdf"), cancellationToken: ct);

        var outline = await document.GetOutlineAsync(ct);

        Assert.Empty(outline);
    }

    [Fact]
    public async Task OutlineOfADisposedDocumentThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await engine.OpenAsync(TestData.Pdf("outline.pdf"), cancellationToken: ct);
        document.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.GetOutlineAsync(ct));
    }

    [Fact]
    public async Task OutlineIsCancellable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("outline.pdf"), cancellationToken: ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.GetOutlineAsync(cts.Token));
    }

    [Fact]
    public async Task OutlineStopsAtTheItemLimit()
    {
        var ct = TestContext.Current.CancellationToken;

        var outline = await ReadAsync("outline.pdf", maxItems: 2, maxDepth: PdfDocument.MaxOutlineDepth, ct);

        // Depth first: "Kapitel 1" and its first child, then the limit cuts silently.
        var chapter1 = Assert.Single(outline);
        Assert.Equal("Kapitel 1", chapter1.Title);
        var section11 = Assert.Single(chapter1.Children);
        Assert.Equal("Abschnitt 1.1", section11.Title);
    }

    [Fact]
    public async Task OutlineStopsAtTheDepthLimit()
    {
        var ct = TestContext.Current.CancellationToken;

        var outline = await ReadAsync("outline.pdf", maxItems: PdfDocument.MaxOutlineItems, maxDepth: 1, ct);

        Assert.Equal(["Kapitel 1", "Kapitel 2", "Fern"], outline.Select(node => node.Title));
        Assert.All(outline, node => Assert.Empty(node.Children));
        Assert.True(outline[0].IsOpen);
    }

    [Fact]
    public void LimitsOfTheDocumentAreFixed()
    {
        Assert.Equal(20_000, PdfDocument.MaxOutlineItems);
        Assert.Equal(32, PdfDocument.MaxOutlineDepth);
    }

    private static Task<IReadOnlyList<PdfiumOutlineNode>> ReadAsync(string file, int maxItems, int maxDepth, CancellationToken ct) =>
        PdfiumThread.Shared.InvokeAsync<IReadOnlyList<PdfiumOutlineNode>>(
            () =>
            {
                using var document = PdfiumDocument.Open(PdfiumThread.Shared, TestData.Pdf(file), null);
                return document.ReadOutline(maxItems, maxDepth);
            },
            cancellationToken: ct);
}
