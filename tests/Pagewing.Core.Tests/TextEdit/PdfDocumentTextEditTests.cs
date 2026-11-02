using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.TextEdit;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>Reading the runs of a page and replacing one (milestone 16, Spec 3.2 to 3.4).</summary>
public sealed class PdfDocumentTextEditTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Copy(TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    // A save writes a new random file identifier into the trailer; everything else of an untouched document is the same bytes.
    private static async Task<string> StateAsync(PdfDocument document) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.Encoding.Latin1.GetString(await SaveAsync(document)), @"/ID\s*\[<[0-9A-Fa-f]+><[0-9A-Fa-f]+>\]", "/ID[]");

    private static async Task<TextRun> RunAsync(PdfDocument document, int page, string text)
    {
        var runs = await document.GetTextRunsAsync(page, Ct);
        return runs.Single(r => r.Text.TrimEnd() == text);
    }

    private static async Task<byte[]> SaveAsync(PdfDocument document)
    {
        using var stream = new MemoryStream();
        await document.SaveIncrementalAsync(stream, Ct);
        return stream.ToArray();
    }

    private static async Task<string> TextOfAsync(PdfDocument document, int page)
    {
        var text = await document.GetPageTextAsync(page, Ct);
        return text.GetText(0, text.Chars.Count);
    }

    private async Task<PdfDocument> ReopenAsync(TempFolder folder, byte[] bytes)
    {
        var path = Path.Combine(folder.Path, "reopened-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, bytes, Ct);
        return await engine.OpenAsync(path, cancellationToken: Ct);
    }

    [Theory]
    [InlineData("text-edit-subset.pdf")]
    [InlineData("text-edit-standard.pdf")]
    [InlineData("text-edit-full.pdf")]
    [InlineData("text-edit-styles.pdf")]
    [InlineData("text-edit-rotated.pdf")]
    public async Task ReadsTheKnownRunsOfEveryFixture(string file)
    {
        await using var document = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: Ct);
        foreach (var known in TextEditFixtures.KnownRuns.Where(r => r.File == file))
        {
            var runs = await document.GetTextRunsAsync(known.Page, Ct);
            var run = runs.Single(r => r.Text.TrimEnd() == known.Text.TrimEnd());

            Assert.False(run.IsLocked, $"{file}: \"{known.Text}\" is locked: {run.Lock}");
            Assert.True(run.Bounds.Inflate(known.Size * 0.6).Contains(known.Left, known.Baseline), $"{file}: \"{known.Text}\" is at {run.Bounds}, expected the origin {known.Left},{known.Baseline}");
            Assert.Equal(known.FontKind is "EmbeddedSubset" or "EmbeddedFull" ? TextRunFontKind.Embedded : TextRunFontKind.Standard14, run.FontKind);
            Assert.Equal(known.Size, run.FontSize, 0.01);
            Assert.Equal(1, run.ObjectCount);
        }
    }

    [Fact]
    public async Task ColourAndSizeOfARunAreRead()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("text-edit-styles.pdf"), cancellationToken: Ct);

        var red = await RunAsync(document, 0, "1,250.00");

        Assert.Equal(new Pagewing.Core.Annotations.AnnotationColor(255, 0, 0), red.Color);
        Assert.Equal("Helvetica-Bold", red.FontName);
    }

    [Fact]
    public async Task LockedRunsCarryTheirReason()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("text-edit-locked.pdf"), cancellationToken: Ct);
        var first = await document.GetTextRunsAsync(0, Ct);
        var second = await document.GetTextRunsAsync(1, Ct);
        var third = await document.GetTextRunsAsync(2, Ct);

        Assert.Null(first.Single(static r => r.Text == "Editable control line").Lock);
        Assert.Null(first.Single(static r => r.Text == "Stroked outline text").Lock);
        Assert.Equal(TextEditLockReason.InvisibleText, first.Single(static r => r.Text == "Invisible text layer").Lock);
        Assert.Equal(TextEditLockReason.MarkedContent, first.Single(static r => r.Text == "Replacement").Lock);
        Assert.Equal(TextEditLockReason.FormXObject, first.Single(static r => r.Text.StartsWith("Shared header", StringComparison.Ordinal)).Lock);
        Assert.Equal(TextEditLockReason.FormXObject, first.Single(static r => r.Text.StartsWith("Nested form", StringComparison.Ordinal)).Lock);
        Assert.Equal(TextEditLockReason.UnsupportedRenderMode, first.Single(static r => r.Text == "Clipping text mode").Lock);
        Assert.Equal(TextEditLockReason.VerticalWriting, first.Single(static r => r.Text == "Vertical").Lock);
        Assert.Null(second.Single(static r => r.Text == "Editable on page two").Lock);
        Assert.Equal(TextEditLockReason.Type3Font, third.Single(static r => r.Text == "ABBA").Lock);
        Assert.Equal(TextRunFontKind.Type3, third.Single(static r => r.Text == "ABBA").FontKind);

        // A page with a Type 3 font cannot be written again by PDFium: no line of it is editable.
        Assert.Equal(TextEditLockReason.Type3Font, third.Single(static r => r.Text == "Line beside Type 3").Lock);
    }

    [Fact]
    public async Task ReplacingWithPresentCharactersKeepsTheFont()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Invoice total: 1,250 EUR");

        var result = await document.ReplaceTextRunAsync(run, "Invoice total: 1,205 EUR", TextEditMode.KeepFont, Ct);

        Assert.Equal(TextEditOutcome.KeepFont, result.Decision!.Outcome);
        Assert.NotNull(result.After);
        Assert.Equal(run.FontName, result.After!.FontName);
        Assert.Equal(TextRunFontKind.Embedded, result.After.FontKind);
        Assert.Equal("Invoice total: 1,205 EUR", result.After.Text);
        var all = await TextOfAsync(document, 0);
        Assert.Contains("Invoice total: 1,205 EUR", all, StringComparison.Ordinal);
        Assert.DoesNotContain("1,250", all, StringComparison.Ordinal);
        Assert.Contains("Kontoauszug März", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCharactersUseGeistOnlyForThem()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Invoice total: 1,250 EUR");

        var result = await document.ReplaceTextRunAsync(run, "Invoice total: 1,259 EUR", TextEditMode.KeepFont, Ct);

        Assert.Equal(TextEditOutcome.SubstituteMissing, result.Decision!.Outcome);
        Assert.Equal(["9"], result.Decision.Missing);
        Assert.Equal(3, result.After!.ObjectCount);
        Assert.Equal("Invoice total: 1,259 EUR", await TextOfLineAsync(document, 0, "Invoice"));
        var runs = await document.GetTextRunsAsync(0, Ct);
        var pieces = runs.Where(r => r.Bounds.Top < run.Bounds.Bottom + 1 && r.Bounds.Bottom > run.Bounds.Top - 1 && r.Bounds.Left < 200).OrderBy(static r => r.Bounds.Left).ToArray();
        Assert.Equal(["Invoice total: 1,25", "9", " EUR"], pieces.Select(static r => r.Text));
        Assert.Equal(run.FontName, pieces[0].FontName);
        Assert.NotEqual(run.FontName, pieces[1].FontName);
        Assert.Equal(run.FontName, pieces[2].FontName);
    }

    private static async Task<string> TextOfLineAsync(PdfDocument document, int page, string startsWith)
    {
        var all = await TextOfAsync(document, page);
        return all.ReplaceLineEndings("\n").Split('\n').First(l => l.StartsWith(startsWith, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WholeRunInGeist()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-standard.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Quarterly report 2026");

        var result = await document.ReplaceTextRunAsync(run, "Quarterly report 2027", TextEditMode.WholeRunInGeist, Ct);

        Assert.Equal(TextEditOutcome.WholeRunInGeist, result.Decision!.Outcome);
        Assert.Equal(1, result.After!.ObjectCount);
        Assert.NotEqual("Helvetica", result.After.FontName);
        Assert.Contains("Quarterly report 2027", await TextOfAsync(document, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldTextIsGoneFromTheNewRevisionNotJustCovered()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-standard.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Quarterly report 2026");
        _ = await document.ReplaceTextRunAsync(run, "Quarterly report 2027", TextEditMode.KeepFont, Ct);
        var bytes = await SaveAsync(document);

        await using var reopened = await ReopenAsync(folder, bytes);
        var all = await TextOfAsync(reopened, 0);
        Assert.Equal(1, all.Split("Quarterly report 2027").Length - 1);
        Assert.DoesNotContain("Quarterly report 2026", all, StringComparison.Ordinal);

        // The content of the new revision (the last page content) has the new text and not the old one; the first revision, the beginning of the file, has the old.
        var streams = TextEditProbe.ContentStreams(bytes);
        Assert.Contains("Quarterly report 2027", TextEditProbe.ShownText(streams[^1]), StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly report 2026", TextEditProbe.ShownText(streams[^1]), StringComparison.Ordinal);
        Assert.Contains("Quarterly report 2026", TextEditProbe.ShownText(streams[0]), StringComparison.Ordinal);
        var original = await File.ReadAllBytesAsync(TestData.Pdf("text-edit-standard.pdf"), Ct);
        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original));
    }

    [Theory]
    [InlineData("text-edit-subset.pdf", "Invoice total: 1,250 EUR", "Invoice total: 1,205 EUR")]
    [InlineData("text-edit-subset.pdf", "Invoice total: 1,250 EUR", "Invoice total: 1,259 EUR")]
    [InlineData("text-edit-standard.pdf", "Dear customer, thank you", "Dear customer, many thanks")]
    [InlineData("text-edit-standard.pdf", "Quarterly report 2026", "")]
    public async Task TheRestOfThePageRendersIdentical(string file, string oldText, string newText)
    {
        using var folder = new TempFolder();
        await using var original = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: Ct);
        await using var document = await engine.OpenAsync(Copy(folder, file), cancellationToken: Ct);
        var run = await RunAsync(document, 0, oldText);

        var result = await document.ReplaceTextRunAsync(run, newText, TextEditMode.KeepFont, Ct);

        var a = await original.RenderPageAsync(0, 1, cancellationToken: Ct);
        var b = await document.RenderPageAsync(0, 1, cancellationToken: Ct);
        Assert.Equal(a.Width, b.Width);
        var changed = result.BoundsBefore.Union(result.BoundsAfter).Inflate(3);
        var different = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (changed.Contains(x, y))
                {
                    continue;
                }

                var i = (y * a.Stride) + (x * 4);
                if (!a.Pixels.Span.Slice(i, 4).SequenceEqual(b.Pixels.Span.Slice(i, 4)))
                {
                    different++;
                }
            }
        }

        Assert.Equal(0, different);
    }

    [Fact]
    public async Task TheTailMovesByTheWidthDifference()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Invoice total: 1,250 EUR");
        var before = await document.GetPageTextAsync(0, Ct);
        var atBefore = before.GetText(0, before.Chars.Count).IndexOf("EUR", StringComparison.Ordinal);

        _ = await document.ReplaceTextRunAsync(run, "Invoice total: 1,250,000 EUR", TextEditMode.KeepFont, Ct);

        var after = await document.GetPageTextAsync(0, Ct);
        var atAfter = after.GetText(0, after.Chars.Count).IndexOf("EUR", StringComparison.Ordinal);
        var shift = after.Chars[atAfter].Box.Left - before.Chars[atBefore].Box.Left;
        Assert.True(shift > 15, $"the tail moved by {shift}");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(before.Chars[atBefore + i].Box.Left + shift, after.Chars[atAfter + i].Box.Left, 0.15);
        }

        // The start stays.
        Assert.Equal(before.Chars[0].Box.Left, after.Chars[0].Box.Left, 0.05);
    }

    [Fact]
    public async Task EmptyTextRemovesTheRun()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-styles.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Pagewing");

        var result = await document.ReplaceTextRunAsync(run, string.Empty, TextEditMode.KeepFont, Ct);

        Assert.Equal(TextEditOutcome.Remove, result.Decision!.Outcome);
        Assert.Null(result.After);
        Assert.DoesNotContain("Pagewing", await TextOfAsync(document, 0), StringComparison.Ordinal);
        Assert.Contains("Total:", await TextOfAsync(document, 0), StringComparison.Ordinal);

        // Undo brings the same run back, exactly as it was.
        var restored = await document.RestoreTextRunAsync(0, run.ObjectIndex, null, result.Stash, Ct);
        Assert.Equal(run.Fingerprint, restored.After!.Fingerprint);
        Assert.Contains("Pagewing", await TextOfAsync(document, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoPutsTheOldRunBackExactlyAndRedoTheNewOne()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Invoice total: 1,250 EUR");
        var edited = await document.ReplaceTextRunAsync(run, "Invoice total: 1,259 EUR", TextEditMode.KeepFont, Ct);

        var undone = await document.RestoreTextRunAsync(0, edited.After!.ObjectIndex, edited.After, edited.Stash, Ct);
        Assert.Equal(run.Fingerprint, undone.After!.Fingerprint);
        Assert.Contains("Invoice total: 1,250 EUR", await TextOfAsync(document, 0), StringComparison.Ordinal);

        var redone = await document.RestoreTextRunAsync(0, undone.After.ObjectIndex, undone.After, undone.Stash, Ct);
        Assert.Equal(edited.After.Fingerprint, redone.After!.Fingerprint);
        Assert.Contains("Invoice total: 1,259 EUR", await TextOfAsync(document, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageWithCharacterSpacingOnAnotherLineCannotBeRewritten()
    {
        // PDFium writes no Tc or Tw: rewriting the page would move the characters of the line that is not edited. The copy shows it, the document stays.
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-styles.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 1, "Tracked one");
        var before = await StateAsync(document);

        var thrown = await Assert.ThrowsAsync<TextRunLockedException>(() => document.ReplaceTextRunAsync(run, "Tracked ones", TextEditMode.KeepFont, Ct));

        Assert.Equal(TextEditLockReason.PageNotRewritable, thrown.Reason);
        Assert.Equal(before, await StateAsync(document));
    }

    [Fact]
    public async Task RotatedPageAndTextStayUpright()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-rotated.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Turned page line");

        var result = await document.ReplaceTextRunAsync(run, "Turned page lines of text", TextEditMode.KeepFont, Ct);

        Assert.Equal("Turned page lines of text", result.After!.Text);
        var text = await document.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);
        var at = all.IndexOf("Turned page lines of text", StringComparison.Ordinal);
        var boxes = text.Chars.Skip(at).Take("Turned page lines of text".Length).Select(static c => c.Box).ToArray();
        for (var i = 1; i < boxes.Length; i++)
        {
            Assert.True(boxes[i].Left > boxes[i - 1].Left - 0.01);
            // The same band on every system: the boxes differ with the substitute font (Linux has another Helvetica), a wrongly turned line would differ by tens of points.
            Assert.Equal(boxes[0].Bottom, boxes[i].Bottom, 7.0);
        }

        Assert.True(result.BoundsAfter.Right > result.BoundsBefore.Right);
        Assert.Equal(result.BoundsBefore.Left, result.BoundsAfter.Left, 1.0);
        Assert.Contains("Upright in user space", all, StringComparison.Ordinal);
        Assert.Contains("Slanted text", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleFingerprintIsRefusedAndNothingChanges()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Invoice total: 1,250 EUR");
        _ = await document.ReplaceTextRunAsync(run, "Invoice total: 1,205 EUR", TextEditMode.KeepFont, Ct);
        var state = await StateAsync(document);

        await Assert.ThrowsAsync<TextRunStaleException>(() => document.ReplaceTextRunAsync(run, "Invoice total: 1,999 EUR", TextEditMode.KeepFont, Ct));
        await Assert.ThrowsAsync<TextRunStaleException>(() => document.ReplaceTextRunAsync(run with { ObjectIndex = 1 }, "x", TextEditMode.KeepFont, Ct));

        Assert.Equal(state, await StateAsync(document));
    }

    [Fact]
    public async Task SavingTwiceWithoutAChangeGivesTheSameBytes()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct);

        var first = await StateAsync(document);
        var second = await StateAsync(document);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ARejectedTextLeavesTheDocumentByteIdentical()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct);
        var run = await RunAsync(document, 0, "Invoice total: 1,250 EUR");
        var before = await StateAsync(document);

        var thrown = await Assert.ThrowsAsync<TextEditRejectedException>(() => document.ReplaceTextRunAsync(run, "Invoice total: ж", TextEditMode.KeepFont, Ct));

        Assert.Equal(["ж"], thrown.Decision.Unsupported);
        Assert.Equal(before, await StateAsync(document));
        var unchanged = await document.ReplaceTextRunAsync(run, run.Text, TextEditMode.KeepFont, Ct);
        Assert.Equal(TextEditOutcome.Unchanged, unchanged.Decision!.Outcome);
        Assert.Equal(0, unchanged.Stash);
        Assert.Equal(before, await StateAsync(document));
    }

    [Fact]
    public async Task GeistIsEmbeddedOnlyWhenNeeded()
    {
        using var folder = new TempFolder();
        var size = new FileInfo(TestData.Pdf("text-edit-subset.pdf")).Length;
        long kept;

        await using (var keep = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct))
        {
            var run = await RunAsync(keep, 0, "Invoice total: 1,250 EUR");
            _ = await keep.ReplaceTextRunAsync(run, "Invoice total: 1,205 EUR", TextEditMode.KeepFont, Ct);
            kept = (await SaveAsync(keep)).Length - size;
        }

        File.Delete(Path.Combine(folder.Path, "text-edit-subset.pdf"));
        await using var geist = await engine.OpenAsync(Copy(folder, "text-edit-subset.pdf"), cancellationToken: Ct);
        var other = await RunAsync(geist, 0, "Invoice total: 1,250 EUR");
        _ = await geist.ReplaceTextRunAsync(other, "Invoice total: 1,259 EUR", TextEditMode.KeepFont, Ct);
        var withGeist = (await SaveAsync(geist)).Length - size;

        // Every incremental save of PDFium writes the objects it loaded again, the font program of the page included (about the size of the subset);
        // Geist (about 25 KB after Flate) comes on top of that only when a character needs it.
        Assert.True(kept < 20_000, $"a correction in the font of the run grew the file by {kept} bytes");
        Assert.True(withGeist - kept is > 15_000 and < 60_000, $"Geist added {withGeist - kept} bytes");
    }

    [Fact]
    public async Task RunsAboveTheLimitAreLockedNotTruncated()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("text-edit-hostile.pdf"), cancellationToken: Ct);

        var grid = await document.GetTextRunsAsync(0, Ct);
        var line = await document.GetTextRunsAsync(1, Ct);

        Assert.Equal(TextEditFixtures.HostileRunCount, grid.Count);
        Assert.All(grid, static r => Assert.Equal(TextEditLockReason.TooManyRuns, r.Lock));
        Assert.Single(line);
        Assert.Equal(TextEditFixtures.HostileRunLength, line[0].Text.Length);
        Assert.Equal(TextEditLockReason.RunTooLong, line[0].Lock);
    }

    [Fact]
    public async Task InvisibleTextAndOcrLayerAreLocked()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(Copy(folder, "text-basic.pdf"), cancellationToken: Ct);
        _ = await document.AddOcrLayerAsync(0, [new OcrPageWord("Hidden", 20, 100, 90, 120, 90)], Ct);

        var runs = await document.GetTextRunsAsync(0, Ct);

        var layer = runs.Single(static r => r.Text == "Hidden");
        Assert.Equal(TextEditLockReason.InvisibleText, layer.Lock);
        Assert.Contains(runs, static r => r.Text.StartsWith("Hello", StringComparison.Ordinal) && r.Lock is null);
        await Assert.ThrowsAsync<TextRunLockedException>(() => document.ReplaceTextRunAsync(layer, "Visible", TextEditMode.KeepFont, Ct));
    }

    [Fact]
    public async Task CoverageFollowsTheRuleOfTheSpike()
    {
        await using var subset = await engine.OpenAsync(TestData.Pdf("text-edit-subset.pdf"), cancellationToken: Ct);
        await using var standard = await engine.OpenAsync(TestData.Pdf("text-edit-standard.pdf"), cancellationToken: Ct);
        var type0 = await RunAsync(subset, 0, "Invoice total: 1,250 EUR");
        var simple = await RunAsync(subset, 0, "Kontoauszug März");
        var helvetica = await RunAsync(standard, 0, "Quarterly report 2026");

        var embedded = await subset.GetFontCoverageAsync(type0, "Ie9zé".Select(static c => (int)c), Ct);
        var hidden = await subset.GetFontCoverageAsync(simple, "obäf".Select(static c => (int)c), Ct);
        var plain = await standard.GetFontCoverageAsync(helvetica, "xzä€ł".Select(static c => (int)c), Ct);

        // An embedded font gives only what it draws on this page: I and e are in the line, 9, z and é are not.
        Assert.True(embedded.Covers('I') && embedded.Covers('e'));
        Assert.False(embedded.Covers('9') || embedded.Covers('z') || embedded.Covers('é'));
        Assert.True(hidden.Covers('o') && hidden.Covers('ä'));
        Assert.False(hidden.Covers('b') || hidden.Covers('f'));

        // A standard font gives what its encoding has and reads back as itself.
        Assert.True(plain.Covers('x') && plain.Covers('z') && plain.Covers('ä') && plain.Covers('€'));
        Assert.False(plain.Covers('ł'));
    }
}
