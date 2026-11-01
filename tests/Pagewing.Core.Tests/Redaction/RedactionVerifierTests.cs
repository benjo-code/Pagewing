using System.IO.Compression;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The raw-bytes scanner and the check of a redaction result (milestone 14, task 6). The most important test is the one that a
/// file that only covers its text with a black rectangle fails the check; the others show where a text can hide and that the scan
/// reads what it can and says what it cannot.
/// </summary>
public sealed class RedactionVerifierTests
{
    private const string Term = "Zephyrstraße 17";

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    private static int Count(byte[] file, string term, out RawScanNotes notes, RawScanLimits? limits = null)
    {
        using var stream = new MemoryStream(file);
        return RawContentScanner.Count(stream, term, limits ?? RawScanLimits.Default, out notes, Ct);
    }

    private static int Count(string file, string term) => Count(Latin1(file), term, out _);

    private static byte[] StreamObject(string dictionary, byte[] data) =>
        Concat(Latin1($"7 0 obj\n<< {dictionary} /Length {data.Length} >>\nstream\n"), data, Latin1("\nendstream\nendobj\n"));

    private static string Copy(TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private static string Write(TempFolder folder, string name, byte[] bytes)
    {
        var path = Path.Combine(folder.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static RedactionPlan PlanOf(int page, PageRect area, string? term = null, bool everywhere = false, RedactionFill? fill = null) =>
        RedactionPlan.Empty.WithFill(fill ?? RedactionFill.Black).With(new RedactionMark(Guid.NewGuid(), term is null ? RedactionKind.Area : RedactionKind.Text, page, [area], term, everywhere));

    private static RedactionExpectation Expect(RedactionPlan plan, params RemovedText[] removed) => new(plan, removed, plan.Fill);

    private static readonly PageRect EmptyArea = new(250, 150, 290, 190);

    private async Task<RedactionCheck> VerifyAsync(string path, RedactionExpectation expectation, RawScanLimits? limits = null)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        await using var stream = File.OpenRead(path);
        return await RedactionVerifier.VerifyAsync(document, stream, expectation, limits ?? RawScanLimits.Default, Ct);
    }

    private static string PageWith(string text) => $"BT /F1 12 Tf 20 150 Td ({text}) Tj ET";

    // ---- The scanner: encodings and syntax ----

    [Fact]
    public void HexStringAndOctalEscapeAreFound()
    {
        var hex = Convert.ToHexString(Latin1(Term));
        Assert.Equal(2, Count($"<< /A <{hex}> /B (Zephyrstra\\337e 17) >>", Term));

        // An odd number of digits is padded with a zero, white space between the digits is ignored, a comment is not a string.
        Assert.Equal(1, Count("<< /A <48 65 6C 6C 6F 2> >>", "Hello "));
        Assert.Equal(1, Count("<< /A (Zephyrstra\\337e\\0401\\067) >>", Term));
    }

    [Fact]
    public void NameWithHashEscapesIsFound() =>
        Assert.Equal(1, Count("<< /Zephyrstra#DFe#2017 true >>", Term));

    [Fact]
    public void LineContinuationAndLineEndsInStringsAreHandled()
    {
        Assert.Equal(2, Count("<< /A (Zephyr\\\nstra\\337e 17) /B (Zephyr\\\r\nstra\\337e 17) >>", Term));
        Assert.Equal(1, Count("<< /A (Zephyrstra\\337e \\061\\067) >>", Term));
    }

    [Fact]
    public void TermSplitAcrossTjArrayPiecesIsFound()
    {
        const string content = "BT /F1 12 Tf [(Zeph) -20 (yrstra) 30 (\\337e 17)] TJ ET";
        Assert.Equal(1, Count(content, Term));

        // The same in a compressed content stream.
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /FlateDecode", VerifierPdf.Zlib(Latin1(content))));
        Assert.Equal(1, Count(file, Term, out var notes));
        Assert.Empty(notes.Items);
    }

    [Fact]
    public void TermSplitOverTjOperatorsOfOneTextObjectIsFound() =>
        Assert.Equal(1, Count("BT /F1 12 Tf (Zephyr) Tj 0 -14 Td (stra\\337e 17) Tj ET", Term));

    [Fact]
    public void Utf16AndPdfDocEncodingAreFound()
    {
        var be = Encoding.BigEndianUnicode.GetBytes("Müller");
        var le = Encoding.Unicode.GetBytes("Müller");
        var file = Concat(
            Latin1("<< /A ("),
            [0xFE, 0xFF],
            be,
            Latin1(") /B ("),
            be,
            Latin1($") /C <FFFE{Convert.ToHexString(le)}> >>"));
        Assert.Equal(3, Count(file, "Müller", out _));

        // The euro sign is 0xA0 in PDFDocEncoding, 0x80 in Windows-ANSI; XMP holds UTF-8.
        var text = Concat(Latin1("<< /A (Betrag \\24050) /B (Betrag \\20050) >>\n% "), Encoding.UTF8.GetBytes("Betrag €50"), Latin1("\n"));
        Assert.Equal(3, Count(text, "Betrag €50", out _));
    }

    [Fact]
    public void NfdVariantOfAPlannedTermIsFound()
    {
        const string composed = "Müller";
        const string decomposed = "Müller";
        var utf8Decomposed = Encoding.UTF8.GetBytes($"% {decomposed}\n");
        Assert.Equal(1, Count(utf8Decomposed, composed, out _));
        var utf8Composed = Encoding.UTF8.GetBytes($"% {composed}\n");
        Assert.Equal(1, Count(utf8Composed, decomposed, out _));

        // The decomposed form in a UTF-16 string of a metadata field.
        var utf16 = Concat(Latin1("<< /Author (þÿ"), Encoding.BigEndianUnicode.GetBytes(decomposed), Latin1(") >>"));
        Assert.Equal(1, Count(utf16, composed, out _));
    }

    [Fact]
    public void OccurrenceInACommentIsFoundAsRawBytes() =>
        Assert.Equal(1, Count("% Zephyrstraße 17\n<< >>", Term));

    [Fact]
    public void ThePlaceOfAnOccurrenceIsCountedOnceNotOncePerView()
    {
        // One text object, one array and one string each hold the term; every view (raw bytes, string, array, text object) finds each.
        const string content = "BT [(Zephyrstra\\337e 17)] TJ ET BT [(Zephyrstraße 17)] TJ ET BT (Zephyrstraße 17) Tj ET";
        Assert.Equal(3, Count(content, Term));
        Assert.Equal(2, Count("abcdabcd", "abcd"));
        Assert.Equal(1, Count("aaaa", "aaa"));
        Assert.Equal(2, Count("abababab", "abab"));
    }

    [Fact]
    public void TheKeywordStreamInsideAStringOrCommentIsNotAStreamStart()
    {
        var file = Latin1("1 0 obj\n<< /A (stream\nnot data) >>\nendobj\n% stream\n2 0 obj\n<< /B (Zephyrstraße 17) >>\nendobj\n");
        Assert.Equal(1, Count(file, Term, out var notes));
        Assert.Empty(notes.Items);
    }

    [Fact]
    public void SeveralTermsAreCountedInOnePass()
    {
        var file = Latin1("<< /A (Alpha) /B (Beta) /C (Alpha Beta) >>");
        using var stream = new MemoryStream(file);
        var result = RawContentScanner.Scan(stream, ["Alpha", "Beta", "Gamma"], RawScanLimits.Default, Ct);
        Assert.Equal([2L, 2L, 0L], result.Counts);
    }

    [Fact]
    public void EmptyAndGarbageFilesAreScannedWithoutFailing()
    {
        Assert.Equal(0, Count([], Term, out var notes));
        Assert.Empty(notes.Items);
        var garbage = new byte[100_000];
        new Random(7).NextBytes(garbage);
        Assert.Equal(0, Count(garbage, Term, out _));
        Assert.Throws<ArgumentException>(() => Count(garbage, string.Empty, out _));
    }

    [Fact]
    public void CancellationStopsTheScan()
    {
        using var stream = new MemoryStream(new byte[1_000_000]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RawContentScanner.Count(stream, Term, RawScanLimits.Default, out _, cancelled.Token));
    }

    // ---- The scanner: streams ----

    [Fact]
    public void ObjectStreamContentIsScanned()
    {
        var packed = VerifierPdf.Zlib(Latin1("5 0 << /Subject (Zephyrstra\\337e 17) /Author <5A657068797273747261DF652031 37> >>"));
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Type /ObjStm /N 1 /First 4 /Filter /FlateDecode", packed));
        Assert.Equal(2, Count(file, Term, out var notes));
        Assert.Equal(1, notes.StreamsScanned);
    }

    [Fact]
    public void StoredDeflateBlocksAreCountedOnce()
    {
        // The text is in the file bytes (a stored block) and in the unpacked stream: still one place.
        var packed = VerifierPdf.Zlib(Latin1(PageWith("Zephyrstra\\337e 17")), CompressionLevel.NoCompression);
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /FlateDecode", packed));
        Assert.Equal(1, Count(file, Term, out _));
        var packedRaw = VerifierPdf.Zlib(Latin1("BT (Zephyrstraße 17) Tj ET"), CompressionLevel.NoCompression);
        Assert.Equal(1, Count(Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /FlateDecode", packedRaw)), Term, out _));
    }

    [Fact]
    public void FilterChainsAreUnpacked()
    {
        var content = Latin1(PageWith("Zephyrstra\\337e 17"));
        var flate = VerifierPdf.Zlib(content);
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(flate) + ">");
        var file = Concat(
            Latin1("%PDF-1.7\n"),
            StreamObject("/Filter [/ASCII85Decode /FlateDecode]", VerifierPdf.Ascii85(flate)),
            StreamObject("/Filter [/ASCIIHexDecode /FlateDecode]", hex),
            StreamObject("/Filter /RunLengthDecode", VerifierPdf.RunLength(content)),
            StreamObject("/Filter [/A85 /Fl]", VerifierPdf.Ascii85(flate)));
        Assert.Equal(4, Count(file, Term, out var notes));
        Assert.Empty(notes.Items);
        Assert.Equal(4, notes.StreamsScanned);
    }

    [Fact]
    public void LzwStreamFromTheSpecificationIsUnpacked()
    {
        // The example of the PDF specification: these codes decode to "-----A---B".
        byte[] codes = [0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01];
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /LZWDecode", codes));
        Assert.Equal(1, Count(file, "A---B", out var notes));
        Assert.Empty(notes.Items);
        Assert.Equal(1, notes.StreamsScanned);
    }

    [Fact]
    public void CorruptZlibTailIsScannedPartiallyAndReported()
    {
        // A stored block with the term, then a block of a type that does not exist: what comes before the damage must still be searched.
        var text = Latin1("BT (Zephyrstra\\337e 17) Tj ET");
        var length = (ushort)text.Length;
        var damaged = Concat([0x78, 0x01, 0x00, (byte)length, (byte)(length >> 8), (byte)~length, (byte)(~length >> 8)], text, [0x07]);
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /FlateDecode", damaged));
        Assert.Equal(1, Count(file, Term, out var notes));
        var note = Assert.Single(notes.Items);
        Assert.Equal(RawScanNoteKind.UnreadableStream, note.Kind);
        Assert.True(note.CouldHideText);
        Assert.True(notes.CouldHideText);
    }

    [Fact]
    public void TruncatedZlibStreamIsReported()
    {
        var whole = VerifierPdf.Zlib(Latin1(PageWith("Zephyrstra\\337e 17") + new string(' ', 30000)), CompressionLevel.NoCompression);
        var truncated = whole[..(whole.Length - 20000)];
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /FlateDecode", truncated));
        Assert.Equal(1, Count(file, Term, out var notes));
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream && n.CouldHideText);
    }

    [Fact]
    public void ADataStreamThatIsNotZlibFallsBackToRawBytesAndIsReported()
    {
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /FlateDecode", Latin1("BT (Zephyrstraße 17) Tj ET")));
        Assert.Equal(1, Count(file, Term, out var notes));
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream && n.CouldHideText);
    }

    [Fact]
    public void RawScanLimitsArePinned()
    {
        Assert.Equal(128L * 1024 * 1024, RedactionLimits.MaxInflatedStream);
        Assert.Equal(1024L * 1024 * 1024, RedactionLimits.MaxInflatedTotal);
        Assert.Equal(RedactionLimits.MaxInflatedStream, RawScanLimits.Default.MaxInflatedStream);
        Assert.Equal(RedactionLimits.MaxInflatedTotal, RawScanLimits.Default.MaxInflatedTotal);
        Assert.Equal(4, RedactionLimits.MinRawCheckLength);
        Assert.Equal(2_000, RedactionVerifier.MaxCheckedTexts);
    }

    [Fact]
    public void InflateBombIsStoppedAndReportedAsUnreadable()
    {
        // 1 GiB of zeros packs to about a megabyte. The scan stops at the limit of one stream, in constant memory, and says so.
        byte[] bomb;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(Latin1("BT (Zephyrstraße 17) Tj ET"));
                var zeros = new byte[1024 * 1024];
                for (var i = 0; i < 1024; i++)
                {
                    zlib.Write(zeros);
                }
            }

            bomb = output.ToArray();
        }

        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /FlateDecode", bomb));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = Count(file, Term, out var notes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1, count); // the text before the bomb is found
        var note = Assert.Single(notes.Items);
        Assert.Equal(RawScanNoteKind.StreamTooLarge, note.Kind);
        Assert.True(note.CouldHideText);
        Assert.InRange(notes.InflatedBytes, RedactionLimits.MaxInflatedStream, RedactionLimits.MaxInflatedStream);
        Assert.True(allocated < 64L * 1024 * 1024, $"The scan allocated {allocated} bytes.");
    }

    [Fact]
    public void TheLimitOfAllStreamsTogetherStopsLaterStreams()
    {
        byte[] OneMegabyte()
        {
            var data = new byte[1024 * 1024];
            Latin1("Zephyrstraße 17").CopyTo(data, 0);
            return VerifierPdf.Zlib(data);
        }

        var packed = OneMegabyte();
        var file = Concat(
            Latin1("%PDF-1.7\n"),
            StreamObject("/Filter /FlateDecode", packed),
            StreamObject("/Filter /FlateDecode", packed),
            StreamObject("/Filter /FlateDecode", packed),
            StreamObject("/Filter /FlateDecode", packed));
        var limits = new RawScanLimits(MaxInflatedStream: 1024 * 1024, MaxInflatedTotal: (5 * 1024 * 1024) / 2);
        var count = Count(file, Term, out var notes, limits);

        Assert.Equal(3, count); // two whole streams and the start of the third
        var note = Assert.Single(notes.Items);
        Assert.Equal(RawScanNoteKind.TotalLimitReached, note.Kind);
        Assert.Equal(2, note.Count); // the third (cut) and the fourth (not unpacked)
        Assert.True(note.CouldHideText);
    }

    [Fact]
    public void UnknownFilterIsListedInTheNotes()
    {
        var file = Concat(
            Latin1("%PDF-1.7\n"),
            StreamObject("/Filter /FooDecode", Latin1("BT (Zephyrstraße 17) Tj ET")),
            StreamObject("/Filter /Crypt", Latin1("data")),
            StreamObject("/Filter /DCTDecode", [0xFF, 0xD8, 0xFF]));
        Assert.Equal(1, Count(file, Term, out var notes)); // the bytes are searched as they are
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnsupportedFilter && n.Detail == "FooDecode" && n.CouldHideText);
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnsupportedFilter && n.Detail == "Crypt" && n.CouldHideText);
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnsupportedFilter && n.Detail == "DCTDecode" && !n.CouldHideText);
        Assert.Equal(3, notes.Lines().Count);
    }

    [Fact]
    public void ImageStreamsAreNotUnpackedAndCannotHideText()
    {
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Type /XObject /Subtype /Image /Width 1 /Height 1 /Filter /FlateDecode", VerifierPdf.Zlib([1, 2, 3])));
        Assert.Equal(0, Count(file, Term, out var notes));
        var note = Assert.Single(notes.Items);
        Assert.Equal(RawScanNoteKind.ImageData, note.Kind);
        Assert.False(note.CouldHideText);
        Assert.Equal(0, notes.StreamsScanned);
    }

    [Fact]
    public void AnEncryptEntryIsReported()
    {
        Assert.Equal(0, Count(Latin1("trailer\n<< /Size 3 /Root 1 0 R /Encrypt 2 0 R >>\n"), Term, out var notes));
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.Encrypted && n.CouldHideText);
    }

    [Fact]
    public async Task InventoryCountsWhatTheCleanupRemoves()
    {
        await using var stream = File.OpenRead(TestData.Pdf("redact-everywhere.pdf"));
        var inventory = RawContentScanner.Inventory(stream, Ct);
        Assert.Equal(1, inventory.MetadataKeys);
        Assert.Equal(1, inventory.EmbeddedFiles);
        Assert.Equal(1, inventory.OutlineItems);
        Assert.Equal(0, inventory.JavaScriptActions);
        Assert.Equal(0, inventory.PageLabelKeys);
        Assert.True(inventory.Complete);

        await using var structure = File.OpenRead(TestData.Pdf("redact-structure.pdf"));
        var inventory2 = RawContentScanner.Inventory(structure, Ct);
        Assert.Equal(2, inventory2.JavaScriptActions);
        Assert.Equal(1, inventory2.PageLabelKeys);
        Assert.True(inventory2.MetadataKeys >= 1);
    }

    // ---- The check: files that leak must fail ----

    [Fact]
    public async Task CoveredButLiveTextFailsTheCheck()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-covered.pdf");

        // The rectangle of the fixture covers 15,112 to 275,136 in user space on a page 200 high; the engine did not touch the text.
        var area = new PageRect(15, 64, 275, 88);
        var plan = PlanOf(0, area, "Adresse: " + Term, everywhere: false);
        var check = await VerifyAsync(path, Expect(plan, new RemovedText(0, "Adresse: " + Term, Everywhere: false, OccurrencesBefore: 1)));

        Assert.False(check.IsClean);
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.CharInsideArea && f.Page == 0);
        Assert.NotEmpty(check.NotChecked);

        // With "everywhere" the raw bytes fail as well, and no detail repeats the text.
        var everywhere = await VerifyAsync(path, Expect(PlanOf(0, area, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)));
        Assert.Contains(everywhere.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        Assert.DoesNotContain(everywhere.Findings, static f => f.Detail.Contains("Zephyr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TermLeftInAnAttachmentFailsTheRawCheck()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Public line"));
        var attachment = pdf.AddStream("/Type /EmbeddedFile", Latin1("Attachment text: " + Term + "\n"));
        var spec = pdf.Add($"<< /Type /Filespec /F (notes.txt) /EF << /F {attachment} 0 R >> >>");
        pdf.CatalogEntries = $"/Names << /EmbeddedFiles << /Names [(notes.txt) {spec} 0 R] >> >>";
        var path = Write(folder, "attachment.pdf", pdf.Build());

        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)));

        Assert.False(check.IsClean);
        Assert.DoesNotContain(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent); // the page text is clean
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent && f.Page is null);
    }

    [Fact]
    public async Task TermLeftInMetadataFailsTheRawCheck()
    {
        using var folder = new TempFolder();
        var info = new VerifierPdf(PageWith("Public line"));
        var infoId = info.Add($"<< /Subject ({Term}) >>");
        info.TrailerEntries = $"/Info {infoId} 0 R";
        var infoPath = Write(folder, "info.pdf", info.Build());

        var xml = new VerifierPdf(PageWith("Public line"));
        var xmp = xml.AddStream("/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes($"<x:xmpmeta><dc:subject>{Term}</dc:subject></x:xmpmeta>"));
        xml.CatalogEntries = $"/Metadata {xmp} 0 R";
        var xmpPath = Write(folder, "xmp.pdf", xml.Build());

        var packed = new VerifierPdf(PageWith("Public line"));
        packed.AddStream("/Type /ObjStm /N 1 /First 4 /Filter /FlateDecode", VerifierPdf.Zlib(Latin1($"9 0 << /Subject <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(Term))}> >>")));
        var packedPath = Write(folder, "objstm.pdf", packed.Build());

        foreach (var path in new[] { infoPath, xmpPath, packedPath })
        {
            var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)));
            Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        }
    }

    [Fact]
    public async Task TermInOlderRevisionFailsTheRawCheck()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-revisions.pdf");

        // The file is its own "result": PDFium shows only the new revision, but the old bytes are still in the file.
        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: false), new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 1)));

        Assert.False(check.IsClean);
        Assert.DoesNotContain(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
    }

    [Fact]
    public async Task CharInsideAMarkAreaFailsThePositionCheck()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "text-basic.pdf");

        // "Hello, Pagewing" is at 20,120 on a page 200 high, 24 pt: the area covers part of it.
        var check = await VerifyAsync(path, Expect(PlanOf(0, new PageRect(30, 50, 120, 90))));

        var finding = Assert.Single(check.Findings);
        Assert.Equal(RedactionFindingKind.CharInsideArea, finding.Kind);
        Assert.Equal(0, finding.Page);

        // The same area elsewhere on the page leaves the check clean.
        var clean = await VerifyAsync(path, Expect(PlanOf(0, new PageRect(200, 150, 290, 190))));
        Assert.True(clean.IsClean);
    }

    [Fact]
    public async Task UnoverwrittenImagePixelFailsThePixelCheck()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image.pdf");

        // The first image (128 x 128 pt at x = 10, y = 16 from the bottom of a page 170 high) is as it was.
        var plan = PlanOf(0, new PageRect(30, 40, 90, 100));
        var check = await VerifyAsync(path, Expect(plan));

        Assert.False(check.IsClean);
        var finding = Assert.Single(check.Findings);
        Assert.Equal(RedactionFindingKind.ImagePixelNotOverwritten, finding.Kind);
        Assert.Equal(0, finding.Page);
    }

    [Fact]
    public async Task RealRedactionOfAnImagePassesTheChecks()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image.pdf");
        var area = new PageRect(30, 40, 90, 100);
        var output = Path.Combine(folder.Path, "out.pdf");
        await using (var document = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
            await using var stream = File.Create(output);
            await document.SaveRewriteAsync(stream, Ct);
        }

        Assert.True((await VerifyAsync(output, Expect(PlanOf(0, area)))).IsClean);

        // A white fill is not what was written.
        var white = await VerifyAsync(output, Expect(PlanOf(0, area, fill: RedactionFill.White)));
        Assert.Contains(white.Findings, static f => f.Kind == RedactionFindingKind.ImagePixelNotOverwritten);
    }

    [Fact]
    public async Task RealRedactionOfTextOnARotatedPagePassesTheChecks()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-rotated.pdf");
        var output = Path.Combine(folder.Path, "out.pdf");
        RedactionPlan plan;
        await using (var document = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            var text = await document.GetPageTextAsync(0, Ct);
            var match = Assert.Single(TextMatcher.Find(text.Chars, Term, new SearchOptions(MatchCase: true)));
            var boxes = Enumerable.Range(match.FirstChar, match.LastChar - match.FirstChar + 1).Select(i => text.Chars[i].Box).ToList();
            var area = boxes.Aggregate(static (a, b) => a.Union(b)).Inflate(1);
            plan = PlanOf(0, area, Term, everywhere: true);
            var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
            Assert.Null(impact.Refusal);
            await using var stream = File.Create(output);
            await document.SaveRewriteAsync(stream, Ct);
        }

        var check = await VerifyAsync(output, Expect(plan, new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)));
        Assert.True(check.IsClean, string.Join(" | ", check.Findings.Select(static f => $"{f.Kind}: {f.Detail}")));
        Assert.Contains(check.NotChecked, static line => line.Contains("subset fonts", StringComparison.Ordinal));
    }

    // ---- The check: the rules ----

    [Fact]
    public async Task CleanFileIsClean()
    {
        using var folder = new TempFolder();
        var path = Write(folder, "clean.pdf", new VerifierPdf(PageWith("Public line only")).Build());

        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)));

        Assert.True(check.IsClean, string.Join(" | ", check.Findings.Select(static f => f.Detail)));
        Assert.Empty(check.Findings);
        Assert.NotEmpty(check.NotChecked); // it always says what it does not check
    }

    [Fact]
    public async Task OccurrenceElsewhereDoesNotFailAnUnmarkedTerm()
    {
        using var folder = new TempFolder();
        var path = Write(folder, "five.pdf", new VerifierPdf(PageWith("one"), PageWith("two"), PageWith("three"), PageWith("four"), PageWith("Name: " + Term)).Build());

        // The term was on two places and one was removed (not "everywhere"): the one on page five stays, in the text and in the bytes.
        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term), new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 2)));
        Assert.True(check.IsClean, string.Join(" | ", check.Findings.Select(static f => f.Detail)));

        // Nothing was removed although something should have been: the text is still there too often.
        var notRemoved = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term), new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 1)));
        Assert.Contains(notRemoved.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent && f.Page == 4);
    }

    [Fact]
    public async Task EverywhereDemandsZeroOccurrences()
    {
        using var folder = new TempFolder();
        var path = Write(folder, "five.pdf", new VerifierPdf(PageWith("one"), PageWith("two"), PageWith("three"), PageWith("four"), PageWith("Name: " + Term)).Build());

        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 2)));

        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);

        // A mark that is "everywhere" in the plan counts even when no removed text names it.
        var fromPlan = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true)));
        Assert.Contains(fromPlan.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);
    }

    [Fact]
    public async Task ShortTermsSkipTheRawCheckAndSayWhy()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Public line"));
        var infoId = pdf.Add("<< /Subject (Abc and Abc) >>");
        pdf.TrailerEntries = $"/Info {infoId} 0 R";
        var path = Write(folder, "short.pdf", pdf.Build());

        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, "Abc", everywhere: true), new RemovedText(0, "Abc", Everywhere: true, OccurrencesBefore: 1)));

        Assert.DoesNotContain(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        Assert.Contains(check.NotChecked, static line => line.Contains("shorter than 4 characters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RawRuleAllowsAtMostTheExtractedCount()
    {
        using var folder = new TempFolder();

        // One place on the page, one in the Info dictionary: two in the bytes, one in the text.
        var two = new VerifierPdf(PageWith("Name: " + Term));
        var infoId = two.Add($"<< /Subject ({Term}) >>");
        two.TrailerEntries = $"/Info {infoId} 0 R";
        var twoPath = Write(folder, "two.pdf", two.Build());
        var one = Write(folder, "one.pdf", new VerifierPdf(PageWith("Name: " + Term)).Build());

        var removed = new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 2);
        var over = await VerifyAsync(twoPath, Expect(PlanOf(0, EmptyArea, Term), removed));
        Assert.Contains(over.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        Assert.DoesNotContain(over.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);

        var equal = await VerifyAsync(one, Expect(PlanOf(0, EmptyArea, Term), removed));
        Assert.True(equal.IsClean, string.Join(" | ", equal.Findings.Select(static f => f.Detail)));
    }

    [Fact]
    public async Task UnknownFilterIsListedInNotChecked()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Public line"));
        pdf.AddStream("/Filter /FooDecode", Latin1("opaque"));
        var path = Write(folder, "foo.pdf", pdf.Build());

        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)));

        Assert.Contains(check.NotChecked, static line => line.Contains("FooDecode", StringComparison.Ordinal));
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.UnreadableStream);
    }

    [Fact]
    public async Task InflateBombInAFileIsAFindingAndTheCheckStaysFast()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Public line"));
        var zeros = new byte[64 * 1024 * 1024];
        pdf.AddStream("/Filter /FlateDecode", VerifierPdf.Zlib(zeros, CompressionLevel.Fastest));
        var path = Write(folder, "bomb.pdf", pdf.Build());

        var limits = new RawScanLimits(MaxInflatedStream: 4 * 1024 * 1024, MaxInflatedTotal: 8 * 1024 * 1024);
        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)), limits);

        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.UnreadableStream);
        Assert.Contains(check.NotChecked, static line => line.Contains("limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextsTheCheckCannotUseAreListedAndNeverGuessed()
    {
        using var folder = new TempFolder();
        var path = Write(folder, "clean.pdf", new VerifierPdf(PageWith("Public line only")).Build());

        var check = await VerifyAsync(
            path,
            Expect(
                PlanOf(0, EmptyArea),
                new RemovedText(0, "   ", Everywhere: false, OccurrencesBefore: 0),
                new RemovedText(0, "Gl�ck", Everywhere: false, OccurrencesBefore: 0),
                new RemovedText(0, new string('x', 1500), Everywhere: false, OccurrencesBefore: 0)));

        var finding = Assert.Single(check.Findings);
        Assert.Equal(RedactionFindingKind.RemovedTextUnverifiable, finding.Kind);
        Assert.Contains(check.NotChecked, static line => line.Contains("white space", StringComparison.Ordinal));
        Assert.Contains(check.NotChecked, static line => line.Contains("U+FFFD", StringComparison.Ordinal));
        Assert.Contains(check.NotChecked, static line => line.Contains("longer than the limit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("text-rich.pdf")]
    [InlineData("redact-fonts.pdf")]
    [InlineData("redact-image-kinds.pdf")]
    [InlineData("redact-image.pdf")]
    [InlineData("redact-hidden.pdf")]
    [InlineData("redact-control.pdf")]
    public async Task AFileWrittenByPdfiumRaisesNoFalseFinding(string name)
    {
        // What PDFium writes (fonts, images, maps, packed content) must not make the check fail or leave streams it cannot read.
        using var folder = new TempFolder();
        var output = Path.Combine(folder.Path, "out.pdf");
        await using (var document = await engine.OpenAsync(TestData.Pdf(name), cancellationToken: Ct))
        {
            await using var stream = File.Create(output);
            await document.SaveRewriteAsync(stream, Ct);
        }

        var check = await VerifyAsync(output, Expect(PlanOf(0, new PageRect(-20, -20, -10, -10), "Absent Qxzv", everywhere: true), new RemovedText(0, "Absent Qxzv", Everywhere: true, OccurrencesBefore: 1)));

        Assert.True(check.IsClean, string.Join(" | ", check.Findings.Select(static f => $"{f.Kind}: {f.Detail}")));
    }

    [Fact]
    public async Task APlanBeyondThePagesOfTheResultIsAFinding()
    {
        using var folder = new TempFolder();
        var path = Write(folder, "clean.pdf", new VerifierPdf(PageWith("Public line only")).Build());

        var check = await VerifyAsync(path, Expect(PlanOf(3, EmptyArea)));

        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.PageChangedOutsideAreas && f.Page == 3);
    }

    // ---- Fix round 1: false-negative paths ----

    [Fact]
    public async Task RemainingLegitimateCopiesAreNamedBecauseTheFileBytesCannotTellThemFromALeak()
    {
        // redact-fonts.pdf shows the term in nine lines, several in fonts with their own code mapping: the page text counts them, the bytes do not.
        var path = TestData.Pdf("redact-fonts.pdf");
        int extracted;
        await using (var document = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            var text = await document.GetPageTextAsync(0, Ct);
            extracted = TextMatcher.Find(text.Chars, Term, new SearchOptions(MatchCase: true)).Count;
        }

        int raw;
        await using (var stream = File.OpenRead(path))
        {
            raw = RawContentScanner.Count(stream, Term, RawScanLimits.Default, out _, Ct);
        }

        Assert.True(extracted > 0 && raw < extracted, $"The fixture must hide copies from the raw view ({raw} raw, {extracted} extracted).");

        var check = await VerifyAsync(path, Expect(PlanOf(0, new PageRect(-20, -20, -10, -10), Term), new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: extracted + 1)));

        Assert.DoesNotContain(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent); // the rule passes ...
        var line = Assert.Single(check.NotChecked, static l => l.Contains("cannot tell a leaked copy", StringComparison.Ordinal)); // ... and says what it cannot see
        Assert.Contains($" {extracted} ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Zephyr", line, StringComparison.Ordinal);

        // With "everywhere" nothing may remain, so there is no such line.
        var everywhere = await VerifyAsync(path, Expect(PlanOf(0, new PageRect(-20, -20, -10, -10), Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: extracted)));
        Assert.DoesNotContain(everywhere.NotChecked, static l => l.Contains("cannot tell a leaked copy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALegitimateCopyAndALeakedCopyAreCaughtByTheRuleAndTheLineStillWarns()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Name: " + Term));
        var attachment = pdf.AddStream("/Type /EmbeddedFile", Latin1(Term));
        pdf.CatalogEntries = $"/Names << /EmbeddedFiles << /Names [(a.txt) << /EF << /F {attachment} 0 R >> >>] >> >>";
        var path = Write(folder, "legit-and-leak.pdf", pdf.Build());

        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term), new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 2)));

        // Raw 2, extracted 1: the rule catches this one; where the legitimate copy is invisible to the raw view it would not (see above).
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.RawBytesStillPresent);
        Assert.Contains(check.NotChecked, static l => l.Contains("cannot tell a leaked copy", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIndirectFilterIsReportedAsUnreadable()
    {
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter 8 0 R", MiniZ()));
        Assert.Equal(0, Count(file, Term, out var notes));
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream && n.CouldHideText);
    }

    [Fact]
    public void AnObjKeywordInsideTheDictionaryMakesTheStreamUnreadableInsteadOfHidingTheFilter()
    {
        // "(x obj)" after the /Filter looks like an object header; the last header wins, so /Filter used to fall out of the dictionary and the
        // packed stream was taken for plain data without a word. Two headers since the last endobj mean the dictionary is not known.
        var hostile = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter /FlateDecode /Note (x obj) /Other (y 9 obj)", MiniZ()));
        Count(hostile, Term, out var notes);
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream && n.CouldHideText);

        // An earlier, finished object in the same bytes is no ambiguity.
        var plain = Concat(Latin1("%PDF-1.7\n3 0 obj\n<< /A 1 >>\nendobj\n"), StreamObject("/Filter /FlateDecode", MiniZ()));
        Count(plain, Term, out var plainNotes);
        Assert.DoesNotContain(plainNotes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream);
    }

    [Fact]
    public void AFilterArrayWithAnIndirectElementIsReportedAsUnreadable()
    {
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject("/Filter [/FlateDecode 8 0 R]", MiniZ()), StreamObject("/Filter [8 0 R]", MiniZ()));
        Count(file, Term, out var notes);
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream && n.CouldHideText && n.Count == 2);
    }

    [Fact]
    public void ADictionaryBeyondTheTokenCapIsReportedAsUnreadable()
    {
        var big = string.Concat(Enumerable.Repeat("1 ", 5000));
        var file = Concat(Latin1("%PDF-1.7\n"), StreamObject($"/Big [{big}] /Filter /FlateDecode", MiniZ()));
        Count(file, Term, out var notes);
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream && n.CouldHideText);
    }

    [Fact]
    public void ADictionaryLargerThanTheRingIsReportedAsUnreadable()
    {
        var pad = new string('x', 20000);
        var file = Concat(Latin1("%PDF-1.7\n7 0 obj\n<< /Filter /FlateDecode /Pad (" + pad + ") /Length 4 >>\nstream\n"), MiniZ(), Latin1("\nendstream\nendobj\n"));
        Count(file, Term, out var notes);
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream && n.CouldHideText);
    }

    [Fact]
    public void AStreamWithoutAnObjectHeaderIsReportedAsUnreadable()
    {
        var file = Concat(Latin1("<< /Filter /FlateDecode /Length 4 >>\nstream\n"), MiniZ(), Latin1("\nendstream\n"));
        Count(file, Term, out var notes);
        Assert.Contains(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream && n.CouldHideText);
    }

    [Fact]
    public void EmptyFilterEntriesAndKnownFiltersAreNotReported()
    {
        var file = Concat(
            Latin1("%PDF-1.7\n"),
            StreamObject("/Filter []", Latin1("plain")),
            StreamObject("/Filter [/FlateDecode]", MiniZ()),
            StreamObject("/Filter /FlateDecode /DecodeParms << /Predictor 1 >>", MiniZ()));
        Count(file, Term, out var notes);
        Assert.DoesNotContain(notes.Items, static n => n.Kind == RawScanNoteKind.UnreadableStream);
    }

    [Fact]
    public async Task AnUnreadableFilterInAFileFailsTheCheck()
    {
        using var folder = new TempFolder();
        var pdf = new VerifierPdf(PageWith("Public line"));
        pdf.AddStream("/Filter 8 0 R", MiniZ());
        var path = Write(folder, "indirect.pdf", pdf.Build());

        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)));

        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.UnreadableStream);
    }

    [Fact]
    public async Task ATextWithTheReplacementCharacterIsAFindingWithPageAndCountButNeverTheText()
    {
        using var folder = new TempFolder();
        var path = Write(folder, "clean.pdf", new VerifierPdf(PageWith("Public line"), PageWith("Second")).Build());

        var check = await VerifyAsync(
            path,
            Expect(
                PlanOf(1, EmptyArea),
                new RemovedText(1, "Gl�ck", Everywhere: false, OccurrencesBefore: 1),
                new RemovedText(1, "Stra�e", Everywhere: false, OccurrencesBefore: 1),
                new RemovedText(0, "A\0B", Everywhere: false, OccurrencesBefore: 1)));

        Assert.False(check.IsClean);
        var onSecond = Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.RemovedTextUnverifiable && f.Page == 1);
        Assert.Contains("2 ", onSecond.Detail, StringComparison.Ordinal);
        Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.RemovedTextUnverifiable && f.Page == 0);
        Assert.All(check.Findings, static f => Assert.DoesNotContain("Gl", f.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBuilderCountsLikeTheVerifierDocumentWide()
    {
        // redact-everywhere.pdf shows the term three times on page one and once on page two. Used as its own "result" with one place
        // claimed as removed, the verifier must see exactly the built count: at most (built - 1) would be expected.
        var path = TestData.Pdf("redact-everywhere.pdf");
        RemovedText built;
        await using (var original = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            built = await RemovedText.FromOriginalAsync(original, 0, Term, everywhere: false, Ct);
        }

        Assert.Equal(4, built.OccurrencesBefore);
        Assert.Equal(0, built.Page);
        Assert.Equal(Term, built.Text);
        Assert.False(built.Everywhere);

        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term), built));
        var finding = Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);
        Assert.Contains("still occurs 4 time(s)", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("at most 3 would", finding.Detail, StringComparison.Ordinal);

        // A text that is not in the document counts 0, and a text without content is refused.
        await using var again = await engine.OpenAsync(path, cancellationToken: Ct);
        var absent = await RemovedText.FromOriginalAsync(again, 0, Term + new string('q', 1200), everywhere: false, Ct);
        Assert.Equal(0, absent.OccurrencesBefore);
        await Assert.ThrowsAsync<ArgumentException>(() => RemovedText.FromOriginalAsync(again, 0, "  ", everywhere: false, Ct));
    }

    [Fact]
    public async Task WrongOccurrencesBeforeAreDocumentedByTheirEffect()
    {
        using var folder = new TempFolder();

        // Two copies in the original (pages one and five), nothing removed in the result: a leak. The honest number fails the check.
        var leak = Write(folder, "leak.pdf", new VerifierPdf(PageWith("Name: " + Term), PageWith("b"), PageWith("c"), PageWith("d"), PageWith("Name: " + Term)).Build());
        var plan = PlanOf(0, EmptyArea, Term);

        var honest = await VerifyAsync(leak, Expect(plan, new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 2)));
        Assert.Contains(honest.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);

        // An OVERSTATED number lets the leak pass: this is why the caller must build the number with FromOriginalAsync.
        var overstated = await VerifyAsync(leak, Expect(plan, new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 3)));
        Assert.DoesNotContain(overstated.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);

        // An UNDERSTATED number fails a correct result.
        var correct = Write(folder, "correct.pdf", new VerifierPdf(PageWith("a"), PageWith("b"), PageWith("c"), PageWith("d"), PageWith("Name: " + Term)).Build());
        var understated = await VerifyAsync(correct, Expect(plan, new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 1)));
        Assert.Contains(understated.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);
    }

    [Fact]
    public async Task TheReportNamesWhatTheRawViewDoesNotCoverBeyondTheEncodings()
    {
        using var folder = new TempFolder();
        var path = Write(folder, "clean.pdf", new VerifierPdf(PageWith("Public line only")).Build());
        var check = await VerifyAsync(path, Expect(PlanOf(0, EmptyArea, Term, everywhere: true), new RemovedText(0, Term, Everywhere: true, OccurrencesBefore: 1)));
        Assert.Contains(check.NotChecked, static l => l.Contains("different text objects", StringComparison.Ordinal) && l.Contains("entities", StringComparison.Ordinal) && l.Contains("case", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBatchBuilderGivesTheSameCountsAsTheSingleOne()
    {
        await using var original = await engine.OpenAsync(TestData.Pdf("redact-everywhere.pdf"), cancellationToken: Ct);
        var batch = await RemovedText.FromOriginalAsync(
            original,
            [new RemovedText(0, Term, Everywhere: false, OccurrencesBefore: 99), new RemovedText(1, "Quellmann", Everywhere: true, OccurrencesBefore: 99)],
            Ct);
        var single = await RemovedText.FromOriginalAsync(original, 0, Term, everywhere: false, Ct);
        Assert.Equal(single.OccurrencesBefore, batch[0].OccurrencesBefore);
        Assert.Equal(1, batch[1].Page);
        Assert.True(batch[1].Everywhere);
        Assert.Equal(0, batch[1].OccurrencesBefore); // only in Info and XMP, not in the page text
    }

    // ---- Pages that became images (milestone 14.1, task 4) ----

    private static readonly PageRect RasterArea = new(50, 50, 150, 100);

    /// <summary>
    /// A one-page file whose page is a full-page image of the given gray value (30 x 20 pixels, RGB) and whatever else the test adds
    /// to the content, to the page dictionary and to the resources: a crafted "result" that the check must tell from a real one.
    /// </summary>
    private static byte[] RasterPdf(byte gray, string extraContent = "", string pageEntries = "", Func<VerifierPdf, string>? extraResources = null)
    {
        var pdf = new VerifierPdf("q 300 0 0 200 0 0 cm /Im1 Do Q " + extraContent) { PageEntries = pageEntries };
        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 30 /Height 20 /ColorSpace /DeviceRGB /BitsPerComponent 8", Enumerable.Repeat(gray, 30 * 20 * 3).ToArray());
        pdf.ResourceEntries = $"/XObject << /Im1 {image} 0 R {extraResources?.Invoke(pdf)} >>";
        return pdf.Build();
    }

    private static RedactionExpectation RasterExpect(params int[] rasterPages)
    {
        var plan = PlanOf(0, RasterArea);
        foreach (var page in rasterPages)
        {
            plan = plan.WithRasterPage(page, on: true);
        }

        return new RedactionExpectation(plan, [], plan.Fill, rasterPages);
    }

    private static RedactionFinding SingleRasterFinding(RedactionCheck check)
    {
        Assert.False(check.IsClean);
        return Assert.Single(check.Findings, static f => f.Kind == RedactionFindingKind.RasterPageInvalid);
    }

    [Fact]
    public async Task CraftedRasterPageThatIsOnlyAFilledImagePassesTheChecks()
    {
        using var folder = new TempFolder();

        // The baseline of the mutation tests below: a black image under a black area is a valid raster page.
        var check = await VerifyAsync(Write(folder, "good.pdf", RasterPdf(gray: 0)), RasterExpect(0));

        Assert.True(check.IsClean, string.Join('\n', check.Findings.Select(static f => f.Detail)));
    }

    [Fact]
    public async Task RasterPageWithLeftoverTextFailsTheCheck()
    {
        using var folder = new TempFolder();

        // Text OUTSIDE the area: no other check looks at it (no term was removed, no character lies in the area).
        var outside = await VerifyAsync(Write(folder, "outside.pdf", RasterPdf(0, extraContent: "BT /F1 12 Tf 20 20 Td (leftover) Tj ET")), RasterExpect(0));
        var finding = SingleRasterFinding(outside);
        Assert.Equal(0, finding.Page);
        Assert.Contains("text object", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("leftover", finding.Detail, StringComparison.Ordinal);

        // Text INSIDE the area is a raster-page finding, not the position check of a page that has text (a raster page has none).
        var inside = await VerifyAsync(Write(folder, "inside.pdf", RasterPdf(0, extraContent: "BT /F1 12 Tf 60 120 Td (leftover) Tj ET")), RasterExpect(0));
        SingleRasterFinding(inside);
        Assert.DoesNotContain(inside.Findings, static f => f.Kind == RedactionFindingKind.CharInsideArea);

        // The same file is clean when its page is not a raster page: the new branch alone makes the difference.
        var notRaster = await VerifyAsync(Write(folder, "notraster.pdf", RasterPdf(0, extraContent: "BT /F1 12 Tf 20 20 Td (leftover) Tj ET")), new RedactionExpectation(PlanOf(0, RasterArea), [], RedactionFill.Black));
        Assert.True(notRaster.IsClean);
    }

    [Fact]
    public async Task RasterPageWithAnnotationOrLinkFails()
    {
        using var folder = new TempFolder();
        var annotations = new Dictionary<string, string>
        {
            ["comment"] = "<< /Type /Annot /Subtype /Square /Rect [10 10 40 40] /C [1 0 0] >>",
            ["link"] = "<< /Type /Annot /Subtype /Link /Rect [10 10 40 40] /Border [0 0 0] /A << /S /URI /URI (http://example.org/) >> >>",
            ["field"] = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /Rect [10 10 80 30] >>",
        };

        foreach (var (name, annotation) in annotations)
        {
            var path = Write(folder, name + ".pdf", RasterPdf(0, pageEntries: $"/Annots [{annotation}]"));
            var finding = SingleRasterFinding(await VerifyAsync(path, RasterExpect(0)));
            Assert.Equal(0, finding.Page);
            Assert.Contains("annotation", finding.Detail, StringComparison.Ordinal);
            Assert.Contains("links and form fields", finding.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RasterPageWithTwoImagesFails()
    {
        using var folder = new TempFolder();

        // The second image lies away from the area and is as black as the first, so the pixel check has nothing to say.
        var two = RasterPdf(0, extraContent: "q 80 0 0 80 200 10 cm /Im2 Do Q", extraResources: pdf => $"/Im2 {pdf.AddStream("/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[16])} 0 R");
        var finding = SingleRasterFinding(await VerifyAsync(Write(folder, "two.pdf", two), RasterExpect(0)));
        Assert.Contains("2 image", finding.Detail, StringComparison.Ordinal);

        // No image at all is as wrong as two.
        var none = new VerifierPdf("0 g").Build();
        var missing = SingleRasterFinding(await VerifyAsync(Write(folder, "none.pdf", none), RasterExpect(0)));
        Assert.Contains("0 image", missing.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RasterPageWithUnfilledAreaFails()
    {
        using var folder = new TempFolder();

        // A white image under a black area: the structure is right, the pixel check of the areas is what fails.
        var check = await VerifyAsync(Write(folder, "unfilled.pdf", RasterPdf(gray: 255)), RasterExpect(0));

        Assert.False(check.IsClean);
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.ImagePixelNotOverwritten && f.Page == 0);
        Assert.DoesNotContain(check.Findings, static f => f.Kind == RedactionFindingKind.RasterPageInvalid);

        // The same image is right for a white fill.
        var white = PlanOf(0, RasterArea, fill: RedactionFill.White).WithRasterPage(0, on: true);
        var ok = await VerifyAsync(Write(folder, "white.pdf", RasterPdf(gray: 255)), new RedactionExpectation(white, [], white.Fill, [0]));
        Assert.True(ok.IsClean);
    }

    [Fact]
    public async Task RasterPageWithPathOutsideAreasFails()
    {
        using var folder = new TempFolder();

        // Until the overlay text exists (task 15 lets paths inside the mark rectangles through) a raster page has no path at all,
        // so a path outside the areas fails; task 15 adapts the second case to a path inside an area.
        var outside = SingleRasterFinding(await VerifyAsync(Write(folder, "outside.pdf", RasterPdf(0, extraContent: "0 0 1 rg 5 5 20 20 re f")), RasterExpect(0)));
        Assert.Contains("path object", outside.Detail, StringComparison.Ordinal);

        var inside = SingleRasterFinding(await VerifyAsync(Write(folder, "inside.pdf", RasterPdf(0, extraContent: "0 0 1 rg 60 110 20 20 re f")), RasterExpect(0)));
        Assert.Contains("path object", inside.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RasterPageWithAFormObjectFails()
    {
        using var folder = new TempFolder();
        var withForm = RasterPdf(0, extraContent: "q 1 0 0 1 200 10 cm /Fm1 Do Q", extraResources: pdf => $"/Fm1 {pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 10 10]", VerifierPdf.Latin1("0 0 1 1 re f"))} 0 R");

        var finding = SingleRasterFinding(await VerifyAsync(Write(folder, "form.pdf", withForm), RasterExpect(0)));

        Assert.Contains("other object", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RasterPageThatCannotBeCountedFailsTheCheck()
    {
        using var folder = new TempFolder();

        // More objects than the reader counts: the check must not read "could not count" as "nothing wrong".
        var hostile = RasterPdf(0, extraContent: string.Concat(Enumerable.Repeat("0 0 1 1 re f\n", 100_001)));
        var finding = SingleRasterFinding(await VerifyAsync(Write(folder, "hostile.pdf", hostile), RasterExpect(0)));

        Assert.Equal(0, finding.Page);
        Assert.Contains("could not be counted", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RasterPageMissingFromTheResultFails()
    {
        using var folder = new TempFolder();

        var check = await VerifyAsync(Write(folder, "short.pdf", RasterPdf(0)), RasterExpect(0, 3));

        var finding = SingleRasterFinding(check);
        Assert.Equal(3, finding.Page);
        Assert.Contains("fewer pages", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealRasterPagePassesTheChecks()
    {
        using var folder = new TempFolder();
        var path = Write(folder, "two.pdf", new VerifierPdf("BT /F1 12 Tf 20 150 Td (secret value) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET", "BT /F1 12 Tf 20 150 Td (second page) Tj ET").Build());
        var plan = PlanOf(0, RasterArea).WithRasterPage(0, on: true);
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        var target = Path.Combine(folder.Path, "result.pdf");

        var result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, target), null, Ct);

        // The check the applier ran, and the verifier run again on the written file: both are clean.
        await using var reopened = result.Saved.Reopened;
        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        Assert.Equal(new PageObjectSummary(Images: 1, Paths: 0, Texts: 0, Others: 0, Annotations: 0), await reopened.GetPageObjectSummaryAsync(0, Ct));
        Assert.True((await VerifyAsync(target, new RedactionExpectation(plan, [], plan.Fill, [0]))).IsClean);

        // The same file fails when it is judged as a result in which page 2 (which kept its text) must have become an image.
        var wrong = SingleRasterFinding(await VerifyAsync(target, new RedactionExpectation(plan.WithRasterPage(1, on: true), [], plan.Fill, [0, 1])));
        Assert.Equal(1, wrong.Page);

        // The report names the pages under "Checked" and what stays unchecked.
        var report = RedactionReportText.Of(analysis, result.Check);
        Assert.Contains("Pages turned into images: page 1", report, StringComparison.Ordinal);
        Assert.Contains("links and form fields", report, StringComparison.Ordinal);
        Assert.Contains("outside the marked areas", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportNamesWhatIsNotCheckedOnRasterPages()
    {
        using var folder = new TempFolder();

        var raster = await VerifyAsync(Write(folder, "good.pdf", RasterPdf(0)), RasterExpect(0));
        var item = Assert.Single(raster.Items, static i => i.Kind == RedactionNotCheckedKind.RasterOutsideAreas);
        Assert.Equal(1, item.Count);
        Assert.Contains("without OCR", item.Text, StringComparison.Ordinal);
        Assert.Contains(item.Text, raster.NotChecked);

        // The row belongs to raster pages only: a check without any does not claim it.
        var plain = await VerifyAsync(Copy(folder, "text-basic.pdf"), Expect(PlanOf(0, new PageRect(200, 150, 290, 190))));
        Assert.DoesNotContain(plain.Items, static i => i.Kind == RedactionNotCheckedKind.RasterOutsideAreas);
        Assert.DoesNotContain(plain.NotChecked, static l => l.Contains("OCR", StringComparison.Ordinal));
    }

    private static byte[] MiniZ() => VerifierPdf.Zlib(Latin1("BT (Zephyrstraße 17) Tj ET"));
}
