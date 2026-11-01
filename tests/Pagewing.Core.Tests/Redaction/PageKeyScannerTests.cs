using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The scan for the page keys PDFium cannot remove (milestone 14, task 7). It reads the bytes PDFium wrote with a tokenizer and
/// fails closed: whatever it cannot read for sure is a refusal "could not be checked", never "nothing found".
/// </summary>
public sealed class PageKeyScannerTests
{
    private const string Catalog = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    /// <summary>A small file without a cross-reference section (the scan does not use one): the objects, then a trailer.</summary>
    private static byte[] Tiny(string objects, string trailer = "/Root 1 0 R") =>
        Latin1("%PDF-1.7\n%âãÏÓ\n" + objects + "trailer\n<< " + trailer + " >>\nstartxref\n0\n%%EOF\n");

    /// <summary>A catalog, a page tree of two pages (objects 3 and 4) and the given extra entries in the dictionary of each page.</summary>
    private static byte[] TwoPages(string firstPage = "", string secondPage = "", string extraObjects = "") =>
        Tiny(
            Catalog
            + "2 0 obj\n<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>\nendobj\n"
            + $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] {firstPage} >>\nendobj\n"
            + $"4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] {secondPage} >>\nendobj\n"
            + extraObjects);

    private static PageKeyScan Scan(byte[] file, int pages)
    {
        using var stream = new MemoryStream(file);
        return PageKeyScanner.Scan(stream, pages, Ct);
    }

    private async Task<(byte[] Bytes, int Pages)> RewriteAsync(string path)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        using var output = new MemoryStream();
        await document.SaveRewriteAsync(output, Ct);
        return (output.ToArray(), document.PageCount);
    }

    [Fact]
    public async Task PdfiumWritesPageDictionariesUncompressed()
    {
        // The scan reads the bytes PDFium writes and gives up on an object stream. This is the guard for the assumption behind it:
        // PDFium writes page dictionaries as plain objects, also for a file whose page tree was in object streams. If this breaks
        // after a PDFium update, the "cannot be checked" branch would be the normal case (every redaction refused): check the
        // assumption anew, never loosen the test. What PDFium does write is a cross-reference stream when the source had one
        // (found by the first run, see DECISIONS); the scan reads that, and a file rebuilt into the empty document has a classic table.
        foreach (var name in new[] { "redact-objstm.pdf", "redact-structure.pdf", "text-rich.pdf", "forms-basic.pdf", "annotations-foreign.pdf", "redact-everywhere.pdf" })
        {
            var (bytes, _) = await RewriteAsync(TestData.Pdf(name));
            var text = Encoding.Latin1.GetString(bytes);
            Assert.DoesNotContain("/ObjStm", text, StringComparison.Ordinal);
            Assert.Contains("/Type/Page", text.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            if (name != "redact-objstm.pdf")
            {
                Assert.DoesNotContain("/XRef", text, StringComparison.Ordinal);
            }
        }

        // The pages of redact-objstm.pdf in the new document: a classic table, nothing compressed.
        var rebuilt = Encoding.Latin1.GetString(await RebuildAsync(TestData.Pdf("redact-objstm.pdf")));
        Assert.DoesNotContain("/ObjStm", rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("/XRef", rebuilt, StringComparison.Ordinal);
        Assert.Contains("xref", rebuilt, StringComparison.Ordinal);
    }

    private async Task<byte[]> RebuildAsync(string path)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        using var output = new MemoryStream();
        await new RedactionExecutor(engine).RebuildAsync(document, output, Ct);
        return output.ToArray();
    }

    [Fact]
    public async Task PageKeyScanReadsEveryFileThatPdfiumWritesFromTheTestPdfs()
    {
        var read = 0;
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(TestData.Pdf("text-basic.pdf"))!, "*.pdf").Order())
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("encrypted_", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] bytes;
            int pages;
            try
            {
                (bytes, pages) = await RewriteAsync(path);
            }
            catch (PdfOpenException)
            {
                continue; // not a PDF, empty, or broken on purpose
            }

            var scan = Scan(bytes, pages);
            Assert.True(scan.WasChecked, $"{name}: {scan.NotChecked}");
            read++;
            var keys = scan.Findings.SelectMany(static f => f.Keys).Distinct().Order().ToArray();
            if (name == "redact-structure.pdf")
            {
                Assert.Equal(["/AA", "/Metadata", "/PieceInfo", "/Thumb"], keys);
            }
            else if (name == "redact-objstm.pdf")
            {
                Assert.Equal(["/PieceInfo", "/Thumb"], keys);
            }
        }

        Assert.True(read > 30);
    }

    [Fact]
    public void PageKeyScanIgnoresFontNamesContainingAA()
    {
        // The name of an embedded subset font starts with six letters; /AAAAAA+Geist contains "/AA" and is no page key.
        var file = TwoPages(
            "/Resources << /Font << /F1 5 0 R >> >>",
            string.Empty,
            "5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAA+Geist /FontDescriptor 6 0 R >>\nendobj\n"
            + "6 0 obj\n<< /Type /FontDescriptor /FontName /AAAAAA+Geist /Flags 4 /Thumbnail /AAA /ThumbX /Metadata1 >>\nendobj\n");
        var scan = Scan(file, 2);
        Assert.True(scan.IsClean, scan.NotChecked);
    }

    [Fact]
    public async Task PageKeyScanIgnoresTheEmbeddedFontOfAFileThatPdfiumWrote()
    {
        var (bytes, pages) = await RewriteAsync(TestData.Pdf("redact-fonts.pdf"));
        Assert.Contains("+Geist", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.True(Scan(bytes, pages).IsClean);
    }

    [Fact]
    public async Task PageKeyScanFailsClosedOnObjectStreams()
    {
        var bytes = await File.ReadAllBytesAsync(TestData.Pdf("redact-objstm.pdf"), Ct);
        var scan = Scan(bytes, 2);

        Assert.False(scan.WasChecked);
        Assert.False(scan.IsClean);
        Assert.Empty(scan.Findings);
        Assert.Contains("object stream", scan.NotChecked, StringComparison.Ordinal);
        var refusal = scan.ToRefusal();
        Assert.NotNull(refusal);
        Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, refusal.Kind);
        Assert.Equal("The page keys could not be checked.", refusal.Reason);
        Assert.Equal(PageKeyScanner.UnverifiedReason, refusal.Reason);
        Assert.True(refusal.PageKeysUnverified);
        Assert.Empty(refusal.Pages);
    }

    [Fact]
    public void ARefusalToldApartByItsFlagNeverByItsPagesOrText()
    {
        // The discriminator (R10): "could not be checked" has no pages, but so would a file-wide refusal of another kind; a finding has pages.
        var found = Scan(TwoPages(secondPage: "/Thumb 1 0 R"), 2).ToRefusal();
        Assert.NotNull(found);
        Assert.False(found.PageKeysUnverified);
        Assert.Equal([1], found.Pages);

        var unverified = Scan(TwoPages(), 3).ToRefusal();
        Assert.NotNull(unverified);
        Assert.True(unverified.PageKeysUnverified);
        Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, unverified.Kind);
    }

    [Fact]
    public void TheScanReportsTheDocumentLevelKeysOfTheCatalogAndTheTrailer()
    {
        var file = Tiny(
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R /Names << >> /PageLabels << >> /OpenAction 7 0 R /Metadata 7 0 R /AA << >> /AcroForm << >> /Perms << >> >>\nendobj\n"
            + "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n"
            + "3 0 obj\n<< /Type /Page /Parent 2 0 R /Names << >> /Outlines 1 0 R >>\nendobj\n",
            trailer: "/Root 1 0 R /Info 9 0 R");

        var scan = Scan(file, 1);

        Assert.True(scan.WasChecked, scan.NotChecked);
        Assert.Equal(["/Info", "/Metadata", "/Outlines", "/Names", "/PageLabels", "/OpenAction", "/AA", "/AcroForm", "/Perms"], scan.DocumentKeys);

        // The keys of a page are no document keys, and a catalog without them has none; the page keys are told apart as before.
        var plain = Scan(TwoPages(firstPage: "/Names << >> /Outlines 1 0 R /Thumb 1 0 R"), 2);
        Assert.Empty(plain.DocumentKeys);
        Assert.Equal(["/Thumb"], plain.Findings.Single().Keys);
    }

    [Fact]
    public async Task TheRebuiltFileHasNoDocumentLevelKeysAndPdfiumsRewriteOfTheOriginalHasThem()
    {
        var (original, pages) = await RewriteAsync(TestData.Pdf("redact-everywhere.pdf"));
        var kept = Scan(original, pages).DocumentKeys;
        foreach (var key in new[] { "/Info", "/Metadata", "/Outlines", "/Names", "/AcroForm" })
        {
            Assert.Contains(key, kept);
        }

        foreach (var name in new[] { "redact-everywhere.pdf", "outline.pdf", "forms-basic.pdf", "forms-signed.pdf", "text-rich.pdf", "annotations-foreign.pdf" })
        {
            var scan = Scan(await RebuildAsync(TestData.Pdf(name)), (await RewriteAsync(TestData.Pdf(name))).Pages);
            Assert.True(scan.WasChecked, name);
            Assert.Empty(scan.DocumentKeys);
        }
    }

    [Fact]
    public void StrictModeFailsClosedOnACrossReferenceStreamAndTheDefaultReadsIt()
    {
        // R9: the check of the final output refuses what it cannot be sure of; the early scan of the working copy reads the stream.
        var file = Latin1(
            "%PDF-1.7\n" + Catalog
            + "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n"
            + "3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n"
            + "4 0 obj\n<< /Type /XRef /Size 5 /Root 1 0 R /W [1 1 1] /Length 3 >>\nstream\nabc\nendstream\nendobj\nstartxref\n0\n%%EOF\n");
        using var lenient = new MemoryStream(file);
        Assert.True(PageKeyScanner.Scan(lenient, 1, Ct).IsClean);

        using var strict = new MemoryStream(file);
        var scan = PageKeyScanner.Scan(strict, 1, allowCrossReferenceStream: false, cancellationToken: Ct);
        Assert.False(scan.WasChecked);
        Assert.False(scan.IsClean);
        Assert.Contains("cross-reference stream", scan.NotChecked, StringComparison.Ordinal);
        Assert.True(scan.ToRefusal()?.PageKeysUnverified);

        // A classic table is read in both modes.
        using var classic = new MemoryStream(TwoPages());
        Assert.True(PageKeyScanner.Scan(classic, 2, allowCrossReferenceStream: false, cancellationToken: Ct).IsClean);
    }

    [Fact]
    public void PageKeyScanReadsACrossReferenceStreamWhenAllObjectsArePlain()
    {
        // What PDFium writes for a source with a cross-reference stream: the stream's dictionary is the trailer.
        var file = Latin1(
            "%PDF-1.7\n" + Catalog
            + "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n"
            + "3 0 obj\n<< /Type /Page /Parent 2 0 R /Thumb 1 0 R >>\nendobj\n"
            + "4 0 obj\n<< /Type /XRef /Size 5 /Root 1 0 R /W [1 1 1] /Length 3 >>\nstream\nabc\nendstream\nendobj\nstartxref\n0\n%%EOF\n");
        var scan = Scan(file, 1);
        Assert.True(scan.WasChecked, scan.NotChecked);
        Assert.Equal([0], scan.Findings.Select(static f => f.Page));

        // Without a catalog anywhere the scan has nothing to walk.
        Assert.False(Scan(Latin1("%PDF-1.7\n4 0 obj\n<< /Type /XRef /Size 5 /Length 0 >>\nstream\n\nendstream\nendobj\n"), 0).WasChecked);
    }

    [Fact]
    public void PageKeyScanFailsClosedOnAnObjectStreamAnywhere()
    {
        var file = Tiny(Catalog + "2 0 obj\n<< /Type /Pages /Count 0 /Kids [] >>\nendobj\n3 0 obj\n<< /Type /ObjStm /N 0 /First 0 /Length 0 >>\nstream\n\nendstream\nendobj\n");
        var scan = Scan(file, 0);
        Assert.False(scan.WasChecked);
        Assert.Contains("object stream", scan.NotChecked, StringComparison.Ordinal);
    }

    [Fact]
    public void PageKeyScanFailsClosedWhenThePageCountDiffers()
    {
        var file = TwoPages();
        Assert.True(Scan(file, 2).IsClean);
        foreach (var wrong in new[] { 0, 1, 3 })
        {
            var scan = Scan(file, wrong);
            Assert.False(scan.WasChecked);
            Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, scan.ToRefusal()?.Kind);
            Assert.Equal(PageKeyScanner.UnverifiedReason, scan.ToRefusal()?.Reason);
        }

        // A page dictionary that the tree does not reach is not counted as a page and not looked at: the check refuses.
        var orphan = TwoPages(extraObjects: "9 0 obj\n<< /Type /Page /Thumb 1 0 R >>\nendobj\n");
        Assert.False(Scan(orphan, 2).WasChecked);
    }

    [Fact]
    public void PageKeyScanFailsClosedWhenThePageTreeCannotBeWalked()
    {
        // A kid that is not in the file, a cycle, no trailer root, a root that is not a catalog with a tree.
        var missingKid = Tiny(Catalog + "2 0 obj\n<< /Type /Pages /Count 1 /Kids [7 0 R] >>\nendobj\n");
        var cycle = Tiny(Catalog + "2 0 obj\n<< /Type /Pages /Count 1 /Kids [2 0 R] >>\nendobj\n");
        var noRoot = Tiny(Catalog + "2 0 obj\n<< /Type /Pages /Count 0 /Kids [] >>\nendobj\n", trailer: "/Size 3");
        var wrongRoot = Tiny(Catalog + "2 0 obj\n<< /Type /Pages /Count 0 /Kids [] >>\nendobj\n", trailer: "/Root 2 0 R");
        var kidNotAPage = Tiny(Catalog + "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n3 0 obj\n<< /Type /Font >>\nendobj\n");
        foreach (var (name, file, pages) in new[] { ("missing kid", missingKid, 1), ("cycle", cycle, 1), ("no root", noRoot, 0), ("wrong root", wrongRoot, 0), ("kid is not a page", kidNotAPage, 1) })
        {
            var scan = Scan(file, pages);
            Assert.False(scan.WasChecked, name);
            Assert.NotNull(scan.NotChecked);
        }
    }

    [Fact]
    public void PageKeyScanFailsClosedOnSyntaxItCannotTrust()
    {
        var truncated = Latin1("%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R");
        var openString = TwoPages("/Foo (never closed");
        var defined = TwoPages(extraObjects: "3 0 obj\n<< /Type /Page /Thumb 1 0 R >>\nendobj\n");
        var indirectLength = TwoPages(extraObjects: "5 0 obj\n<< /Length 6 0 R >>\nstream\nabc\nendstream\nendobj\n6 0 obj\n3\nendobj\n");
        var wrongLength = TwoPages(extraObjects: "5 0 obj\n<< /Length 2 >>\nstream\nabcdef\nendstream\nendobj\n");
        var tooLong = TwoPages(extraObjects: "5 0 obj\n<< /Length 999999 >>\nstream\nabc\nendstream\nendobj\n");
        var deep = TwoPages("/Foo " + new string('[', 300) + new string(']', 300));
        foreach (var (name, file) in new[] { ("truncated", truncated), ("open string", openString), ("defined twice", defined), ("indirect length", indirectLength), ("wrong length", wrongLength), ("too long", tooLong), ("deep", deep) })
        {
            var scan = Scan(file, 2);
            Assert.False(scan.WasChecked, name);
            Assert.False(scan.IsClean, name);
        }
    }

    [Fact]
    public void PageKeyScanTestsOnlyTheTopLevelKeysOfPageDictionaries()
    {
        // The keys are in an annotation, a resource dictionary, a group, an array and as a value: none of them is a key of the page.
        var file = TwoPages(
            "/Annots [<< /Subtype /Link /Thumb 1 /AA << /E << /S /JavaScript /JS (x) >> >> /PieceInfo (x) /Metadata null >>] /Resources << /Thumb 1 /Metadata 2 >> /Group << /AA 3 /PieceInfo 4 >> /Other [/Thumb /AA /Metadata /PieceInfo]",
            "/Foo /Thumb /Bar /AA");
        var scan = Scan(file, 2);
        Assert.True(scan.IsClean, scan.NotChecked);
        Assert.Null(scan.ToRefusal());
    }

    [Fact]
    public void PageKeyScanSkipsStringsCommentsAndStreamData()
    {
        // Text that looks like an object with a page key in a string, a hex string, a comment and the data of a stream.
        var fake = "5 0 obj\n<< /Type /Page /Thumb 1 0 R >>\nendobj\ntrailer << /Root 9 0 R >>";
        var file = TwoPages(
            $"/Foo ({fake}) /Bar <2F5468756D62> % /Thumb 1 0 R\n /Baz (a\\) /AA (b))",
            string.Empty,
            $"7 0 obj\n<< /Length {fake.Length} >>\nstream\n{fake}\nendstream\nendobj\n");
        var scan = Scan(file, 2);
        Assert.True(scan.IsClean, scan.NotChecked);
    }

    [Fact]
    public void PageKeyScanDecodesNameEscapes()
    {
        var file = TwoPages(secondPage: "/T#68umb 5 0 R /Piece#49nfo << >>");
        var scan = Scan(file, 2);
        Assert.True(scan.WasChecked);
        var finding = Assert.Single(scan.Findings);
        Assert.Equal(1, finding.Page);
        Assert.Equal(["/Thumb", "/PieceInfo"], finding.Keys);
    }

    [Theory]
    [InlineData("/Thumb 5 0 R", "/Thumb")]
    [InlineData("/PieceInfo << /Pagewing << /Private (x) >> >>", "/PieceInfo")]
    [InlineData("/Metadata 5 0 R", "/Metadata")]
    [InlineData("/AA << /O << /S /JavaScript /JS (x) >> >>", "/AA")]
    public void PageKeyScanFindsEachKeyWithItsPageNumber(string entry, string key)
    {
        var scan = Scan(TwoPages(secondPage: entry), 2);

        var finding = Assert.Single(scan.Findings);
        Assert.Equal(1, finding.Page);
        Assert.Equal([key], finding.Keys);
        var refusal = scan.ToRefusal();
        Assert.NotNull(refusal);
        Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, refusal.Kind);
        Assert.Equal([1], refusal.Pages);
        Assert.Contains(key + " on page 2", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageKeyScanFindsTheKeysOfAFileThatPdfiumRewroteFromObjectStreams()
    {
        var (bytes, pages) = await RewriteAsync(TestData.Pdf("redact-objstm.pdf"));
        var scan = Scan(bytes, pages);

        Assert.True(scan.WasChecked, scan.NotChecked);
        var finding = Assert.Single(scan.Findings);
        Assert.Equal(1, finding.Page);
        Assert.Equal(["/Thumb", "/PieceInfo"], finding.Keys);
    }

    [Fact]
    public void PageKeyScanChecksTheRefusalOfTheExecutor()
    {
        using var clean = new MemoryStream(TwoPages());
        using var dirty = new MemoryStream(TwoPages("/AA << >>"));
        Assert.Null(RedactionExecutor.PageKeyRefusal(clean, 2));
        Assert.Equal([0], RedactionExecutor.PageKeyRefusal(dirty, 2)?.Pages);
    }

    [Fact]
    public void PageKeyScanRejectsAStreamItCannotSeek()
    {
        using var stream = new NonSeekable(TwoPages());
        Assert.Throws<ArgumentException>(() => PageKeyScanner.Scan(stream, 2, Ct));
    }

    [Fact]
    public void PageKeyScanCanBeCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(TwoPages());
        Assert.Throws<OperationCanceledException>(() => PageKeyScanner.Scan(stream, 2, cancellation.Token));
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
