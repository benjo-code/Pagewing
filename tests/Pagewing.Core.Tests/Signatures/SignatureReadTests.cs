using System.Diagnostics;
using System.Security.Cryptography;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Signatures;

/// <summary>The PDFium side of the visible signature (milestone 12, task 5): the drawn one (ink with the subject "Signature") and the image stamp.</summary>
public sealed class SignatureReadTests : IDisposable
{
    private const string Date = "D:20261029120000Z";

    private readonly TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Copy(string fixture)
    {
        var path = Path.Combine(folder.Path, Guid.NewGuid().ToString("N")[..6] + "-" + fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    /// <summary>Random colours, an alpha that is 0 for a good part of the pixels and soft elsewhere: what a cut-out signature looks like.</summary>
    private static byte[] NoisyImage(int width, int height, int seed = 7)
    {
        var random = new Random(seed);
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 4;
                var alpha = Math.Clamp(255 - (Math.Abs(x - (width / 2)) * 3) - (Math.Abs(y - (height / 2)) * 4) + random.Next(-30, 30), 0, 255);
                bgra[o] = (byte)random.Next(256);
                bgra[o + 1] = (byte)random.Next(256);
                bgra[o + 2] = (byte)random.Next(256);
                bgra[o + 3] = (byte)alpha;
            }
        }

        return bgra;
    }

    /// <summary>Changes an annotation behind the back of the code under test (what another program would do).</summary>
    private static void Raw(PdfiumPage page, int index, Func<nint, int> change)
    {
        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        try
        {
            Assert.NotEqual(0, change(annotation));
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    private static Task<T> OnPdfiumAsync<T>(Func<T> work) => PdfiumThread.Shared.InvokeAsync(work, cancellationToken: Ct);

    /// <summary>Opens <paramref name="path"/>, lets <paramref name="edit"/> change page 1 and saves incrementally into the same path.</summary>
    private static async Task SaveEditedAsync(string path, Action<PdfiumDocument, PdfiumPage> edit)
    {
        var bytes = await OnPdfiumAsync(() =>
        {
            using var document = PdfiumDocument.Open(PdfiumThread.Shared, path, null);
            using (var page = document.LoadPage(0))
            {
                edit(document, page);
            }

            using var stream = new MemoryStream();
            document.SaveIncremental(stream);
            return stream.ToArray();
        });
        await File.WriteAllBytesAsync(path, bytes, Ct);
    }

    private static Task<T> ReadAsync<T>(string path, Func<PdfiumDocument, PdfiumPage, T> read) => OnPdfiumAsync(() =>
    {
        using var document = PdfiumDocument.Open(PdfiumThread.Shared, path, null);
        using var page = document.LoadPage(0);
        return read(document, page);
    });

    private static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Squiggle() =>
    [
        [(30, 80), (60, 30), (90, 90), (130, 40)],
        [(30, 100), (130, 100)],
    ];

    [Fact]
    public async Task InkWithSignatureSubjectIsRecognisedAfterReload()
    {
        var path = Copy("text-basic.pdf");
        await SaveEditedAsync(path, (document, page) =>
        {
            var index = PdfiumDrawings.Create(page, new PdfiumDrawing(PdfiumDrawingKind.Signature, Squiggle(), default, 2), (0, 0, 120), null, null, "sig-ink", Date);
            Assert.True(PdfiumAnnotations.GenerateAppearance(document, 0, index));
        });

        var drawing = await ReadAsync(path, (_, page) => PdfiumDrawings.ReadGeometry(page, 0));

        Assert.NotNull(drawing);
        Assert.Equal(PdfiumDrawingKind.Signature, drawing.Kind);
        Assert.Equal(2, drawing.Strokes.Count);
        Assert.Equal([4, 2], drawing.Strokes.Select(s => s.Count));
        Assert.Equal(2, drawing.Width, 3);
        Assert.Equal(30, drawing.Strokes[0][0].X, 1);
        Assert.Equal(80, drawing.Strokes[0][0].Y, 1);
    }

    [Fact]
    public async Task InkWithSignatureSubjectAndSeveralStrokesIsASignature()
    {
        // The ink of stamps-foreign.pdf has the subject and three strokes: a signature has no stroke count, any ink with the
        // subject is one (the form is the strokes of the pad). A plain ink and the other drawings stay what they are.
        var stamps = await ReadAsync(TestData.Pdf(StampsForeign), (_, page) => PdfiumDrawings.ReadGeometry(page, 3));
        var plain = await ReadAsync(TestData.Pdf(DrawingsForeign), (_, page) => PdfiumDrawings.ReadGeometry(page, 0));

        Assert.NotNull(stamps);
        Assert.Equal(PdfiumDrawingKind.Signature, stamps.Kind);
        Assert.Equal(3, stamps.Strokes.Count);
        Assert.NotNull(plain);
        Assert.Equal(PdfiumDrawingKind.Ink, plain.Kind);
    }

    [Fact]
    public async Task ForeignStampsAreListedButNotOwn()
    {
        var results = await ReadAsync(TestData.Pdf(StampsForeign), (document, page) =>
        {
            var listed = PdfiumAnnotations.Read(page, 100).Select(a => (a.Index, a.SubtypeName)).ToList();
            var own = Enumerable.Range(0, 4).Select(i => PdfiumSignatures.IsOwnImage(document, page, i)).ToList();
            var images = Enumerable.Range(0, 4).Select(i => PdfiumSignatures.ReadImage(document, page, i) is not null).ToList();
            return (listed, own, images);
        });

        Assert.Equal([(0, "Stamp"), (1, "Stamp"), (2, "Stamp"), (3, "Ink")], results.listed);
        Assert.Equal([false, false, false, false], results.own);

        // The stamp without a mark carries a 4 x 4 image: it can be read as an image, the others cannot.
        Assert.Equal([false, true, false, false], results.images);
    }

    [Theory]
    [InlineData("text-basic.pdf")]
    [InlineData("rotated-page.pdf")]
    [InlineData("cropbox.pdf")]
    public async Task OwnImageStampIsRecognisedAfterReload(string fixture)
    {
        var path = Copy(fixture);
        var image = NoisyImage(200, 80);
        int index = -1;
        await SaveEditedAsync(path, (document, page) =>
            index = PdfiumSignatures.CreateImage(document, page, (20, 20, 100, 52), 200, 80, image, "Alice", "sig-image", Date));

        var result = await ReadAsync(path, (document, page) => (
            PdfiumSignatures.IsOwnImage(document, page, index),
            PdfiumAnnotations.SubtypeOf(page, index),
            PdfiumAnnotations.ReadAt(page, index)));

        Assert.True(result.Item1);
        Assert.Equal(FpdfConstants.AnnotStamp, result.Item2);
        Assert.Equal("sig-image", result.Item3!.Name);
        Assert.Equal("Alice", result.Item3.Author);
        Assert.Equal(20, result.Item3.Bounds.Left, 0);
        Assert.Equal(52, result.Item3.Bounds.Bottom, 0);
    }

    [Fact]
    public async Task AlteredImageLosesTheMark()
    {
        // The key says "these pixels", the image is another one (a program replaced the image and left the key).
        var path = Copy("text-basic.pdf");
        var original = NoisyImage(100, 40, 1);
        var other = NoisyImage(100, 40, 2);
        await SaveEditedAsync(path, (document, page) =>
        {
            var index = PdfiumSignatures.CreateImage(document, page, (20, 20, 100, 52), 100, 40, other, null, "sig-image", Date);
            Raw(page, index, annotation => PdfiumAnnotations.WriteString(annotation, "PagewingSignature\0"u8, "1 " + PdfiumSignatures.Fingerprint(original)));
        });

        var own = await ReadAsync(path, (document, page) => PdfiumSignatures.IsOwnImage(document, page, 0));

        Assert.False(own);
    }

    [Fact]
    public async Task ImageWithAForeignFlagOrKeyIsNotOwn()
    {
        var path = Copy("text-basic.pdf");
        var image = NoisyImage(100, 40);
        await SaveEditedAsync(path, (document, page) =>
        {
            PdfiumSignatures.CreateImage(document, page, (20, 20, 100, 52), 100, 40, image, null, "plain", Date);
            var flagged = PdfiumSignatures.CreateImage(document, page, (120, 20, 200, 52), 100, 40, image, null, "locked", Date);
            var replied = PdfiumSignatures.CreateImage(document, page, (120, 80, 200, 112), 100, 40, image, null, "popup", Date);
            Raw(page, flagged, annotation => Fpdf.FPDFAnnot_SetFlags(annotation, FpdfConstants.AnnotFlagPrint | FpdfConstants.AnnotFlagHidden));
            Raw(page, replied, annotation => PdfiumAnnotations.WriteString(annotation, "Popup\0"u8, "x"));
        });

        var own = await ReadAsync(path, (document, page) => Enumerable.Range(0, 3).Select(i => PdfiumSignatures.IsOwnImage(document, page, i)).ToList());

        Assert.Equal([true, false, false], own);
    }

    [Fact]
    public async Task ReadImageReturnsTheSamePixels()
    {
        var path = Copy("rotated-page.pdf");
        var image = NoisyImage(200, 80);
        await SaveEditedAsync(path, (document, page) =>
            PdfiumSignatures.CreateImage(document, page, (10, 20, 90, 52), 200, 80, image, null, "sig-image", Date));

        var (first, second, still) = await ReadAsync(path, (document, page) =>
            (PdfiumSignatures.ReadImage(document, page, 0), PdfiumSignatures.ReadImage(document, page, 0), PdfiumSignatures.IsOwnImage(document, page, 0)));

        Assert.NotNull(first);
        Assert.Equal((200, 80), (first.Value.Width, first.Value.Height));
        for (var i = 0; i < image.Length; i += 4)
        {
            Assert.Equal(image[i + 3], first.Value.Bgra[i + 3]);
            if (image[i + 3] > 0)
            {
                Assert.Equal(image[i], first.Value.Bgra[i]);
                Assert.Equal(image[i + 1], first.Value.Bgra[i + 1]);
                Assert.Equal(image[i + 2], first.Value.Bgra[i + 2]);
            }
        }

        // Reading leaves the stamp as it was: the same result again, still its own.
        Assert.NotNull(second);
        Assert.True(first.Value.Bgra.AsSpan().SequenceEqual(second.Value.Bgra));
        Assert.True(still);
    }

    [Fact]
    public async Task HostileImageSizeIsRefused()
    {
        var hash = Convert.ToHexString(SHA256.HashData(new byte[4])).ToLowerInvariant();
        string Stamp(int width, int height) => WritePdf(folder, $"hostile-{width}x{height}.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Stamp /Rect [10 10 110 60] /F 4 /NM (hostile) /PagewingSignature (1 {hash}) /AP << /N 5 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Resources << /XObject << /FXX1 6 0 R >> >> /Length 28 >>\nstream\nq 100 0 0 50 0 0 cm /FXX1 Do Q\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 10 >>\nstream\n0123456789\nendstream",
        ]);

        // Control: the same stamp with an image of a size within the limit is read (its pixels are not the marked ones, so it is not own).
        var (controlOwn, control) = await ReadAsync(Stamp(10, 1), (document, page) => (PdfiumSignatures.IsOwnImage(document, page, 0), PdfiumSignatures.ReadImage(document, page, 0)));
        Assert.False(controlOwn);
        Assert.NotNull(control);
        Assert.Equal((10, 1), (control.Value.Width, control.Value.Height));

        foreach (var (width, height) in new[] { (7000, 7000), (1201, 2), (2, 1201), (60_000, 60_000) })
        {
            var watch = Stopwatch.StartNew();
            var (own, image) = await ReadAsync(Stamp(width, height), (document, page) => (PdfiumSignatures.IsOwnImage(document, page, 0), PdfiumSignatures.ReadImage(document, page, 0)));

            Assert.False(own);
            Assert.Null(image);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{width}x{height} took {watch.Elapsed}");
        }
    }

    [Fact]
    public async Task CreateImageRefusesBadInputAndLeavesNothing()
    {
        var path = Copy("text-basic.pdf");
        var image = NoisyImage(10, 10);

        var (counts, errors) = await ReadAsync(path, (document, page) =>
        {
            var before = Fpdf.FPDFPage_GetAnnotCount(page.Access());
            Func<int>[] bad =
            [
                () => PdfiumSignatures.CreateImage(document, page, (20, 20, 100, 50), 0, 10, image, null, "x", Date),
                () => PdfiumSignatures.CreateImage(document, page, (20, 20, 100, 50), 10, 10, image.AsSpan(0, 39), null, "x", Date),
                () => PdfiumSignatures.CreateImage(document, page, (20, 20, 100, 50), 1201, 1, new byte[1201 * 4], null, "x", Date),
                () => PdfiumSignatures.CreateImage(document, page, (100, 20, 100, 50), 10, 10, image, null, "x", Date),
                () => PdfiumSignatures.CreateImage(document, page, (20, 20, double.NaN, 50), 10, 10, image, null, "x", Date),
            ];
            var errors = bad.Select(f => Record.Exception(() => f())).ToList();
            return ((before, Fpdf.FPDFPage_GetAnnotCount(page.Access())), errors);
        });

        Assert.Equal(counts.before, counts.Item2);
        Assert.All(errors, e => Assert.True(e is ArgumentException, e?.ToString()));
    }

    [Theory]
    [InlineData("text-basic.pdf")]
    [InlineData("rotated-page.pdf")]
    public async Task SetBoxMovesAndScalesTheStamp(string fixture)
    {
        var path = Copy(fixture);
        var image = NoisyImage(200, 80);
        await SaveEditedAsync(path, (document, page) =>
            PdfiumSignatures.CreateImage(document, page, (20, 20, 100, 52), 200, 80, image, null, "sig-image", Date));
        await SaveEditedAsync(path, (_, page) => PdfiumSignatures.SetBox(page, 0, (30, 60, 90, 60 + 24), "D:20261029130000Z"));

        var result = await ReadAsync(path, (document, page) => (PdfiumAnnotations.ReadAt(page, 0)!.Bounds, PdfiumSignatures.IsOwnImage(document, page, 0), PdfiumSignatures.ReadImage(document, page, 0)));

        Assert.Equal((30, 60, 90, 84), (Math.Round(result.Bounds.Left), Math.Round(result.Bounds.Top), Math.Round(result.Bounds.Right), Math.Round(result.Bounds.Bottom)));
        Assert.True(result.Item2);
        Assert.NotNull(result.Item3);
        Assert.Equal((200, 80), (result.Item3.Value.Width, result.Item3.Value.Height));
    }
}
