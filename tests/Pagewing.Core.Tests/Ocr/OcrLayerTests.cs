using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>Pagewing's invisible text layer and what the engine needs of a page: facts, render, write, read, remove.</summary>
public sealed class OcrLayerTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OcrPageWord[] WordsOf(string file, int page) =>
        [.. OcrFixtures.WordsOf(file, page).Select(static w => new OcrPageWord(w.Text, w.Left, w.Top, w.Right, w.Bottom, 90))];

    private static string Copy(TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    [Fact]
    public async Task InfoCountsTextWithoutWhitespaceAndWithoutTheOwnLayer()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-basic.pdf"), cancellationToken: Ct);

        var plain = await document.GetOcrPageInfoAsync(0, Ct);
        Assert.Equal(14, plain.TextCharacters); // "Hello," and "Pagewing"
        Assert.False(plain.HasOwnLayer);

        var written = await document.AddOcrLayerAsync(0, [new OcrPageWord("Extra", 20, 100, 80, 120, 90), new OcrPageWord("Words", 90, 100, 150, 120, 90)], Ct);
        var withLayer = await document.GetOcrPageInfoAsync(0, Ct);

        Assert.Equal(2, written.Written);
        Assert.True(withLayer.HasOwnLayer);
        Assert.Equal(14, withLayer.TextCharacters);
    }

    [Fact]
    public async Task VisibleTextThatCarriesTheMarkIsNeverTakenForTheLayerNorRemoved()
    {
        // Our layer is invisible text with the mark; a file whose visible text carries the same mark name must not lose it in a redo run.
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "marked-visible.pdf");
        await File.WriteAllBytesAsync(path, MarkedVisibleText(), Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        var info = await document.GetOcrPageInfoAsync(0, Ct);
        var removed = await document.RemoveOcrLayerAsync(0, Ct);

        Assert.False(info.HasOwnLayer);
        Assert.Equal(7, info.TextCharacters);
        Assert.Equal(0, removed);
        Assert.Empty(await document.ReadOcrLayerAsync(0, Ct));
        var text = await document.GetPageTextAsync(0, Ct);
        Assert.Contains("Visible", text.GetText(0, text.Chars.Count), StringComparison.Ordinal);
    }

    private static byte[] MarkedVisibleText()
    {
        const string Content = "/PagewingOcr BMC BT /F1 18 Tf 50 700 Td (Visible) Tj ET EMC";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Content.Length} >>\nstream\n{Content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var text = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = text.Length;
        text.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        text.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(text.ToString());
    }

    [Theory]
    [InlineData("ocr-scan.pdf")]
    [InlineData("ocr-scan-rotated.pdf")]
    public async Task InfoFindsTheImageResolutionAndCoverage(string file)
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, file), cancellationToken: Ct);

        var info = await document.GetOcrPageInfoAsync(0, Ct);

        Assert.Equal(0, info.TextCharacters);
        Assert.False(info.HasOwnLayer);
        Assert.NotNull(info.ImageDpi);
        Assert.Equal(200, info.ImageDpi!.Value, 1);
        Assert.InRange(info.ImageCoverage, 0.98, 1.0);
    }

    [Fact]
    public async Task InfoOfAPageWithoutAnImageHasNoResolution()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: Ct);

        var info = await document.GetOcrPageInfoAsync(0, Ct);

        Assert.Null(info.ImageDpi);
        Assert.Equal(0, info.ImageCoverage);
    }

    [Theory]
    [InlineData("ocr-scan.pdf")]
    [InlineData("ocr-scan-rotated.pdf")]
    public async Task RenderForOcrHasTheDisplayedSizeAtTheRequestedDpi(string file)
    {
        await using var document = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: Ct);
        var size = await document.GetPageSizeAsync(0, Ct);
        var expected = OcrResolution.Resolve(size.Width, size.Height, 200, 200);

        var image = await document.RenderPageForOcrAsync(0, 200, Ct);

        Assert.Equal(expected.WidthPx, image.Width);
        Assert.Equal(expected.HeightPx, image.Height);
        Assert.Equal(1000, image.Width);
        Assert.Equal(1400, image.Height);
    }

    [Fact]
    public async Task RenderForOcrDrawsNoAnnotations()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("annotations-foreign.pdf"), cancellationToken: Ct);
        var withAnnotations = await document.RenderPageAsync(0, 1, new RenderOptions { Annotations = true }, Ct);
        var without = await document.RenderPageAsync(0, 1, new RenderOptions { Annotations = false }, Ct);
        var forOcr = await document.RenderPageForOcrAsync(0, 72, Ct);

        Assert.False(withAnnotations.Pixels.Span.SequenceEqual(without.Pixels.Span), "the fixture shows no annotation, the test says nothing");
        Assert.True(without.Pixels.Span.SequenceEqual(forOcr.Pixels.Span));
    }

    [Theory]
    [InlineData("ocr-scan.pdf", 0)]
    [InlineData("ocr-scan.pdf", 1)]
    [InlineData("ocr-scan.pdf", 2)]
    [InlineData("ocr-scan-rotated.pdf", 0)]
    public async Task AddedLayerIsFoundBySearchAtTheKnownPosition(string file, int page)
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, file), cancellationToken: Ct);
        var known = OcrFixtures.WordsOf(file, page);

        var written = await document.AddOcrLayerAsync(page, WordsOf(file, page), Ct);

        Assert.Equal(known.Count, written.Written);
        Assert.Equal(0, written.Skipped);
        var text = await document.GetPageTextAsync(page, Ct);
        var all = text.GetText(0, text.Chars.Count);
        foreach (var word in known)
        {
            // A word can stand more than once on a page ("München"): one of its places has to be the known one.
            var tolerance = (word.Bottom - word.Top) / 2;
            var found = false;
            for (var start = all.IndexOf(word.Text, StringComparison.Ordinal); start >= 0 && !found; start = all.IndexOf(word.Text, start + 1, StringComparison.Ordinal))
            {
                var rects = text.RectsOf(start, start + word.Text.Length);
                found = Math.Abs(rects.Min(static r => r.Left) - word.Left) <= tolerance
                    && Math.Abs(rects.Max(static r => r.Right) - word.Right) <= tolerance
                    && Math.Abs(rects.Min(static r => r.Top) - word.Top) <= tolerance
                    && Math.Abs(rects.Max(static r => r.Bottom) - word.Bottom) <= tolerance;
            }

            Assert.True(found, $"'{word.Text}' is not found at its place ({word.Left:0.#},{word.Top:0.#},{word.Right:0.#},{word.Bottom:0.#}) in {file} page {page + 1}");
        }
    }

    [Fact]
    public async Task AddingTheLayerLeavesThePageRenderBitIdentical()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "ocr-scan.pdf"), cancellationToken: Ct);
        var before = await document.RenderPageAsync(1, 1, cancellationToken: Ct);

        await document.AddOcrLayerAsync(1, WordsOf("ocr-scan.pdf", 1), Ct);
        var after = await document.RenderPageAsync(1, 1, cancellationToken: Ct);

        Assert.True(before.Pixels.Span.SequenceEqual(after.Pixels.Span), "the layer changed a pixel of the page");
    }

    [Fact]
    public async Task ReadBackReturnsWhatWasWritten()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "ocr-scan-rotated.pdf"), cancellationToken: Ct);
        var words = WordsOf("ocr-scan-rotated.pdf", 0);
        await document.AddOcrLayerAsync(0, words, Ct);

        var read = await document.ReadOcrLayerAsync(0, Ct);

        Assert.Equal(words.Select(static w => w.Text), read.Select(static w => w.Text));
        for (var i = 0; i < words.Length; i++)
        {
            Assert.Equal(words[i].Left, read[i].Left, 0.2);
            Assert.Equal(words[i].Top, read[i].Top, 0.2);
            Assert.Equal(words[i].Right, read[i].Right, 0.2);
            Assert.Equal(words[i].Bottom, read[i].Bottom, 0.2);
        }
    }

    [Fact]
    public async Task WritingWhatWasReadDoesNotDrift()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "ocr-scan.pdf"), cancellationToken: Ct);
        await document.AddOcrLayerAsync(0, WordsOf("ocr-scan.pdf", 0), Ct);
        var first = await document.ReadOcrLayerAsync(0, Ct);

        for (var round = 0; round < 3; round++)
        {
            var read = await document.ReadOcrLayerAsync(0, Ct);
            await document.RemoveOcrLayerAsync(0, Ct);
            await document.AddOcrLayerAsync(0, read, Ct);
        }

        var last = await document.ReadOcrLayerAsync(0, Ct);
        Assert.Equal(first.Count, last.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Left, last[i].Left, 0.05);
            Assert.Equal(first[i].Top, last[i].Top, 0.05);
            Assert.Equal(first[i].Right, last[i].Right, 0.05);
            Assert.Equal(first[i].Bottom, last[i].Bottom, 0.05);
        }
    }

    [Fact]
    public async Task RemoveTakesOnlyTheOwnLayer()
    {
        // A file with invisible text of another origin (no mark), then our layer on top of it.
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "foreign.pdf");
        var thread = PdfiumThread.Shared;
        var bytes = await thread.InvokeAsync(
            () =>
            {
                var source = Path.Combine(folder.Path, "foreign-source.pdf");
                File.WriteAllBytes(source, SpikePdf.Scan(1, 360, 504));
                using var opened = PdfiumDocument.Open(thread, source, null);
                LayerSpike.AddLayers(opened, new Dictionary<int, IReadOnlyList<SpikeWord>> { [0] = [new("Foreign", 40, 40, 140, 64)] }, marked: false);
                using var stream = new MemoryStream();
                opened.SaveRewrite(stream);
                return stream.ToArray();
            },
            cancellationToken: Ct);
        await File.WriteAllBytesAsync(path, bytes, Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        Assert.False((await document.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer);
        await document.AddOcrLayerAsync(0, [new OcrPageWord("Ours", 40, 100, 120, 124, 90)], Ct);

        var removed = await document.RemoveOcrLayerAsync(0, Ct);

        Assert.Equal(1, removed);
        var text = await document.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);
        Assert.Contains("Foreign", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Ours", all, StringComparison.Ordinal);
        Assert.False((await document.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer);
        Assert.Empty(await document.ReadOcrLayerAsync(0, Ct));
    }

    [Fact]
    public async Task CharactersOutsideTheFontAreSkippedAndCounted()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "ocr-scan.pdf"), cancellationToken: Ct);

        var result = await document.AddOcrLayerAsync(
            0,
            [new OcrPageWord("Привет", 80, 100, 160, 124, 90), new OcrPageWord("Größe", 180, 100, 260, 124, 90), new OcrPageWord("日本語", 280, 100, 340, 124, 90)],
            Ct);

        Assert.Equal(1, result.Written);
        Assert.Equal(2, result.Skipped);
        var read = await document.ReadOcrLayerAsync(0, Ct);
        Assert.Equal(["Größe"], read.Select(static w => w.Text));
    }

    [Fact]
    public async Task EmptyWordListWritesNothing()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "ocr-scan.pdf"), cancellationToken: Ct);

        var none = await document.AddOcrLayerAsync(0, [], Ct);
        var unusable = await document.AddOcrLayerAsync(0, [new OcrPageWord(" ", 10, 10, 20, 20, 90), new OcrPageWord("dot", 50, 50, 50, 60, 90)], Ct);

        Assert.Equal(new AddOcrLayerResult(0, 0), none);
        Assert.Equal(new AddOcrLayerResult(0, 0), unusable);
        Assert.False((await document.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer);
    }

    [Fact]
    public async Task APageThatDoesNotExistIsRefused()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("ocr-scan.pdf"), cancellationToken: Ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetOcrPageInfoAsync(3, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.AddOcrLayerAsync(-1, [], Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.RenderPageForOcrAsync(0, 0, Ct));
    }

    [Fact]
    public async Task ADisposedDocumentRefusesEveryCall()
    {
        var document = await engine.OpenAsync(TestData.Pdf("ocr-scan.pdf"), cancellationToken: Ct);
        await document.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.GetOcrPageInfoAsync(0, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.RemoveOcrLayerAsync(0, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.ReadOcrLayerAsync(0, Ct));
    }
}
