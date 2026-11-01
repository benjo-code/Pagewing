using System.IO.Compression;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>The raw-bytes helper of the redaction tests and the layout of the redaction fixtures it relies on.</summary>
public sealed class RawPdfTests
{
    private const string Term = "Zephyrstraße 17";
    private const string SecondTerm = "Quellmann";

    private static byte[] Fixture(string name) => File.ReadAllBytes(TestData.Pdf(name));

    /// <summary>The bytes of the object (up to its <c>endobj</c>) that contains <paramref name="marker"/>.</summary>
    private static byte[] ObjectWith(byte[] file, string marker)
    {
        var text = Encoding.Latin1.GetString(file);
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The fixture has no object with '{marker}'.");
        var start = text.LastIndexOf(" obj", at, StringComparison.Ordinal);
        var end = text.IndexOf("endobj", at, StringComparison.Ordinal);
        return file[start..end];
    }

    [Theory]
    [InlineData("Redaction fixture, page one", Term, 2)] // visible and invisible page text
    [InlineData("/Subtype /Form /BBox [0 0 150 30]", Term, 1)] // the form XObject that two pages use
    [InlineData("/PatternType 1", Term, 1)] // the tiling pattern
    [InlineData("/Name /Note", Term, 1)] // the note
    [InlineData("/IRT", Term, 1)] // the reply
    [InlineData("/FT /Tx /T (Adresse)", Term, 1)] // the form value
    [InlineData("/Title (Zephyr", Term, 1)] // the bookmark
    [InlineData("/Subject", Term, 1)] // the info dictionary
    [InlineData("/Author", SecondTerm, 1)] // the info dictionary
    [InlineData("/Type /Metadata", Term, 1)] // XMP (UTF-8)
    [InlineData("/Type /Metadata", SecondTerm, 1)] // XMP (UTF-8)
    [InlineData("/Type /EmbeddedFile", Term, 1)] // the attachment
    public void RawPdfFindsTheTermInEveryHidingPlaceOfTheFixture(string marker, string term, int count)
    {
        var file = Fixture("redact-everywhere.pdf");

        Assert.Equal(count, RawPdf.Count(ObjectWith(file, marker), term));
        Assert.True(RawPdf.ContainsAnywhere(file, term));
    }

    [Fact]
    public void TheEverywhereFixtureHoldsTheTermElevenTimesAndTheSecondTermTwice()
    {
        var file = Fixture("redact-everywhere.pdf");

        // page text and invisible text (one stream), form XObject, pattern, note, reply, form value, bookmark, info /Subject, XMP, attachment
        Assert.Equal(11, RawPdf.Count(file, Term));
        Assert.Equal(2, RawPdf.Count(file, SecondTerm)); // info /Author and XMP
    }

    [Fact]
    public void TheCoveredFixtureStillHoldsTheTermUnderTheRectangle()
    {
        var file = Fixture("redact-covered.pdf");

        Assert.Equal(1, RawPdf.Count(file, Term));
        Assert.Contains("0 g 15 112 260 24 re f", Encoding.Latin1.GetString(file), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRevisionsFixtureHoldsTheTermOnlyInTheFirstRevision()
    {
        var file = Fixture("redact-revisions.pdf");
        var text = Encoding.Latin1.GetString(file);
        var firstEnd = text.IndexOf("%%EOF\n", StringComparison.Ordinal) + "%%EOF\n".Length;

        Assert.Equal(1, RawPdf.Count(file, Term));
        Assert.Equal(1, RawPdf.Count(file[..firstEnd], Term));
        Assert.Equal(0, RawPdf.Count(file[firstEnd..], Term));
        Assert.Equal(2, text.Split("%%EOF").Length - 1); // two revisions
        Assert.Contains("/Prev ", text[firstEnd..], StringComparison.Ordinal);
    }

    [Fact]
    public void RawPdfCountsUtf16Strings()
    {
        byte[] Join(params byte[][] parts) => [.. parts.SelectMany(static p => p)];
        var ascii = Encoding.ASCII.GetBytes("(Quellmann)");
        var utf16Be = Join([0xFE, 0xFF], Encoding.BigEndianUnicode.GetBytes(SecondTerm));
        var utf16Le = Join([0xFF, 0xFE], Encoding.Unicode.GetBytes(SecondTerm));

        Assert.Equal(1, RawPdf.Count(ascii, SecondTerm));
        Assert.Equal(1, RawPdf.Count(utf16Be, SecondTerm));
        Assert.Equal(1, RawPdf.Count(utf16Le, SecondTerm));
        Assert.Equal(3, RawPdf.Count(Join(ascii, utf16Be, utf16Le), SecondTerm));

        // Non-ASCII: single byte (PDFDocEncoding), UTF-8 and both UTF-16 forms are four different byte strings.
        var latin1 = Encoding.Latin1.GetBytes(Term);
        var utf8 = Encoding.UTF8.GetBytes(Term);
        Assert.Equal(4, RawPdf.Count(Join(latin1, utf8, Encoding.BigEndianUnicode.GetBytes(Term), Encoding.Unicode.GetBytes(Term)), Term));
        Assert.Equal(0, RawPdf.Count(Encoding.ASCII.GetBytes("Zephyrstrasse 17 and quellmann"), Term));
        Assert.Equal(0, RawPdf.Count(Encoding.ASCII.GetBytes("quellmann"), SecondTerm)); // case-sensitive
        Assert.Equal(1, RawPdf.Count(Encoding.ASCII.GetBytes("aaa"), "aa")); // does not overlap
        Assert.Throws<ArgumentException>(() => RawPdf.Count([1, 2, 3], string.Empty));
    }

    [Fact]
    public void RawPdfFindsATermInsideAFlateStream()
    {
        var plain = Encoding.Latin1.GetBytes($"BT /F1 12 Tf ({Term}) Tj ET");
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(plain);
        }

        var stream = packed.ToArray();
        var file = new List<byte>();
        file.AddRange(Encoding.Latin1.GetBytes($"%PDF-1.7\n1 0 obj\n<< /Filter /FlateDecode /Length {stream.Length} >>\nstream\n"));
        file.AddRange(stream);
        file.AddRange(Encoding.Latin1.GetBytes($"\nendstream\nendobj\n2 0 obj\n<< /Length 4 >>\nstream\n({SecondTerm})\nendstream\nendobj\n3 0 obj\n<< /Filter /FlateDecode /Length 5 >>\nstream\nnoise\nendstream\nendobj\n"));
        var bytes = file.ToArray();

        Assert.Equal(0, RawPdf.Count(bytes, Term));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "Not here"));
        Assert.True(RawPdf.ContainsAnywhere(bytes, Term));
        var inflated = RawPdf.InflateAllStreams(bytes);
        Assert.Equal(1, RawPdf.Count(inflated, Term));
        Assert.Equal(1, RawPdf.Count(inflated, SecondTerm)); // the stream without a filter stays as it is
        Assert.Contains("noise", Encoding.Latin1.GetString(inflated), StringComparison.Ordinal); // so does a stream that is no zlib data
        Assert.StartsWith("%PDF-1.7\n1 0 obj", Encoding.Latin1.GetString(inflated), StringComparison.Ordinal);
    }

    [Fact]
    public void RawPdfRefusesAFlateBombInsteadOfPassingTheFile()
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i <= RawPdf.MaxInflatedStream / zeros.Length; i++)
            {
                zlib.Write(zeros);
            }
        }

        var stream = packed.ToArray();
        var file = new List<byte>(Encoding.Latin1.GetBytes("1 0 obj\n<< /Filter /FlateDecode >>\nstream\n"));
        file.AddRange(stream);
        file.AddRange(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));

        Assert.Throws<InvalidDataException>(() => RawPdf.InflateAllStreams([.. file]));
    }

    [Theory]
    [InlineData("redact-everywhere.pdf", 2)]
    [InlineData("redact-covered.pdf", 1)]
    [InlineData("redact-revisions.pdf", 1)]
    [InlineData("redact-image.pdf", 1)]
    [InlineData("redact-rotated.pdf", 1)]
    public async Task PdfiumOpensTheRedactionFixtures(string name, int pages)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await new PdfEngine().OpenAsync(TestData.Pdf(name), cancellationToken: ct);

        Assert.Equal(pages, document.PageCount);
    }

    [Fact]
    public async Task PdfiumReadsTheTermWhereTheFixtureShowsItAndNotWhereItIsHidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new PdfEngine();

        await using (var everywhere = await engine.OpenAsync(TestData.Pdf("redact-everywhere.pdf"), cancellationToken: ct))
        {
            var one = await everywhere.GetTextAsync(0, ct);
            var two = await everywhere.GetTextAsync(1, ct);
            Assert.Contains($"Adresse: {Term}", one, StringComparison.Ordinal);
            Assert.True(one.Split(Term).Length - 1 >= 3, "visible text, form XObject and invisible text"); // the pattern is not page text
            Assert.Contains(Term, two, StringComparison.Ordinal); // the shared form XObject
            var metadata = await everywhere.GetMetadataAsync(ct);
            Assert.Equal(SecondTerm, metadata.Author);
            Assert.Equal(Term, metadata.Subject);
        }

        await using (var covered = await engine.OpenAsync(TestData.Pdf("redact-covered.pdf"), cancellationToken: ct))
        {
            Assert.Contains(Term, await covered.GetTextAsync(0, ct), StringComparison.Ordinal);
        }

        await using (var revisions = await engine.OpenAsync(TestData.Pdf("redact-revisions.pdf"), cancellationToken: ct))
        {
            var text = await revisions.GetTextAsync(0, ct);
            Assert.DoesNotContain("Zephyr", text, StringComparison.Ordinal);
            Assert.Contains("Address removed", text, StringComparison.Ordinal);
            Assert.Contains("Public line stays visible", text, StringComparison.Ordinal);
        }

        await using (var rotated = await engine.OpenAsync(TestData.Pdf("redact-rotated.pdf"), cancellationToken: ct))
        {
            Assert.Contains($"Adresse: {Term}", await rotated.GetTextAsync(0, ct), StringComparison.Ordinal);
            Assert.Equal(new PdfPageSize(280, 360), await rotated.GetPageSizeAsync(0, ct)); // CropBox 360 x 280, turned by 90 degrees
        }

        await using (var image = await engine.OpenAsync(TestData.Pdf("redact-image.pdf"), cancellationToken: ct))
        {
            Assert.Equal(new PdfPageSize(420, 170), await image.GetPageSizeAsync(0, ct));
        }
    }
}
