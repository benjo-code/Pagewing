using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Signatures;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Signatures;

/// <summary>Creating, changing, saving and reading visible signatures (drawn and image) through <see cref="PdfDocument"/> (milestone 12, task 6).</summary>
public sealed class SignatureRoundTripTests : IDisposable
{
    private const double Scale = 4;

    private static readonly AnnotationColor Black = new(0, 0, 0);

    private static readonly AnnotationColor Red = new(220, 30, 30);

    private readonly TempFolder folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    /// <summary>100 x 40 px: an opaque red block in the top left quarter, everything else fully transparent (white): upright, it is red at the top left.</summary>
    private static SignatureImage BlockImage()
    {
        var bgra = new byte[100 * 40 * 4];
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                var o = ((y * 100) + x) * 4;
                var block = x < 50 && y < 20;
                bgra[o] = block ? (byte)0 : (byte)255;
                bgra[o + 1] = block ? (byte)0 : (byte)255;
                bgra[o + 2] = block ? (byte)200 : (byte)255;
                bgra[o + 3] = block ? (byte)255 : (byte)0;
            }
        }

        return new SignatureImage(100, 40, bgra);
    }

    /// <summary>Random colours and an alpha of 0 for many pixels and soft values elsewhere, so that a read-back is compared on real data.</summary>
    private static SignatureImage NoisyImage(int width = 200, int height = 80, int seed = 11)
    {
        var random = new Random(seed);
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = ((y * width) + x) * 4;
                bgra[o] = (byte)random.Next(256);
                bgra[o + 1] = (byte)random.Next(256);
                bgra[o + 2] = (byte)random.Next(256);
                bgra[o + 3] = (byte)Math.Clamp(255 - (Math.Abs(x - (width / 2)) * 3) - (Math.Abs(y - (height / 2)) * 4) + random.Next(-30, 30), 0, 255);
            }
        }

        return new SignatureImage(width, height, bgra);
    }

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, Guid.NewGuid().ToString("N")[..6] + "-" + name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private Task<PdfDocument> OpenCopyAsync(string name) => OpenAsync(Copy(name));

    private static AnnotationLocator LocatorOf(PdfAnnotation annotation) => new(annotation.Page, annotation.Index, annotation.Name);

    private static void AssertSamePixels(SignatureImage expected, SignatureImage actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (var i = 0; i < expected.Bgra.Length; i += 4)
        {
            Assert.Equal(expected.Bgra[i + 3], actual.Bgra[i + 3]);
            if (expected.Bgra[i + 3] > 0)
            {
                Assert.Equal(expected.Bgra[i], actual.Bgra[i]);
                Assert.Equal(expected.Bgra[i + 1], actual.Bgra[i + 1]);
                Assert.Equal(expected.Bgra[i + 2], actual.Bgra[i + 2]);
            }
        }
    }

    [Fact]
    public async Task DrawnSignatureSurvivesSaveAndReload()
    {
        IReadOnlyList<DisplayPoint>[] strokes =
        [
            [new(30, 80), new(60, 30.5), new(90, 90), new(130.25, 40)],
            [new(30, 100), new(130, 100)],
        ];
        var geometry = DrawingGeometry.Signature(strokes, 2);
        string saved;
        PdfAnnotation created;
        await using (var document = await OpenCopyAsync("text-basic.pdf"))
        {
            created = await document.CreateDrawingAsync(0, geometry, Black, "My signature", "Ada", Ct);
            Assert.Equal(AnnotationKind.Signature, created.Kind);
            Assert.Equal("Ink", created.Subtype);
            Assert.False(created.IsSignatureImage);
            Assert.True(created.Editable);
            Assert.True(created.GeometryEditable);
            Assert.Equal(Black, created.Color);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, annotation.Name);
        Assert.Equal(AnnotationKind.Signature, annotation.Kind);
        Assert.Equal(Black, annotation.Color);
        Assert.Equal("My signature", annotation.Contents);
        Assert.Equal("Ada", annotation.Author);
        Assert.True(annotation.Editable);
        Assert.True(annotation.GeometryEditable);
        Assert.NotNull(annotation.Drawing);
        Assert.Equal(AnnotationKind.Signature, annotation.Drawing.Kind);
        Assert.Equal(2, annotation.Drawing.Width, 6);
        Assert.Equal([4, 2], annotation.Drawing.Strokes.Select(s => s.Count));
        Assert.Equal(130.25, annotation.Drawing.Strokes[0][3].X, 3);
        Assert.Equal(annotation.Drawing, annotation.SelectionShape);
    }

    [Fact]
    public async Task DrawnSignatureCanBeRecolouredAndMovedAsOneNamedAnnotation()
    {
        var geometry = DrawingGeometry.Signature([[new(30, 80), new(60, 30), new(90, 90), new(130, 40)]], 2);
        await using var document = await OpenCopyAsync("text-basic.pdf");
        var created = await document.CreateDrawingAsync(0, geometry, Black, null, null, Ct);

        var recoloured = await document.UpdateAnnotationAsync(LocatorOf(created), new AnnotationEdit(Color: Red), Ct);
        var moved = await document.UpdateAnnotationAsync(LocatorOf(recoloured), new AnnotationEdit(Geometry: DrawingTransform.Move(recoloured.Drawing!, 20, 15, new PageRect(0, 0, 300, 200))), Ct);

        var listed = Assert.Single(await document.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, listed.Name);
        Assert.Equal(moved.Name, listed.Name);
        Assert.Equal(AnnotationKind.Signature, listed.Kind);
        Assert.Equal(Red, listed.Color);
        Assert.Equal(50, listed.Drawing!.Strokes[0][0].X, 3);
        Assert.Equal(95, listed.Drawing.Strokes[0][0].Y, 3);
    }

    [Fact]
    public async Task ImageSignatureSurvivesSaveAndReload()
    {
        var image = NoisyImage();
        var box = new PageRect(30, 40, 110, 72);
        string saved;
        PdfAnnotation created;
        await using (var document = await OpenCopyAsync("text-basic.pdf"))
        {
            created = await document.CreateSignatureImageAsync(0, box, image, "Ada", Ct);

            // As the app sees it right after placing (before anything is saved): an own signature that can be selected and moved.
            Assert.Equal(AnnotationKind.Signature, created.Kind);
            Assert.Equal("Stamp", created.Subtype);
            Assert.True(created.IsSignatureImage);
            Assert.True(created.Editable);
            Assert.True(created.GeometryEditable);
            Assert.Null(created.Drawing);
            Assert.NotNull(created.SelectionShape);
            Assert.Equal(AnnotationKind.Rectangle, created.SelectionShape.Kind);
            Assert.Equal("Ada", created.Author);
            Assert.Matches("^[0-9a-f]{32}$", created.Name);
            AssertRectNear(box, created.Bounds, 1.0 / 16);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, annotation.Name);
        Assert.Equal(AnnotationKind.Signature, annotation.Kind);
        Assert.True(annotation.IsSignatureImage);
        Assert.True(annotation.Editable);
        Assert.True(annotation.GeometryEditable);
        AssertRectNear(box, annotation.Bounds, 1.0 / 16);
        AssertSamePixels(image, await reopened.ReadSignatureImageAsync(LocatorOf(annotation), Ct));
    }

    [Fact]
    public async Task ImageMovesAndScalesKeepingAspect()
    {
        // The image is 2.5 : 1. A box of 100 x 60 is not: the signature takes the largest box of its aspect inside it, around its centre.
        var image = NoisyImage();
        await using var document = await OpenCopyAsync("text-basic.pdf");
        var created = await document.CreateSignatureImageAsync(0, new PageRect(30, 40, 110, 72), image, null, Ct);

        var changed = await document.UpdateAnnotationAsync(LocatorOf(created), new AnnotationEdit(Geometry: DrawingGeometry.Rectangle(new PageRect(120, 90, 220, 150), 1)), Ct);

        var listed = Assert.Single(await document.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, listed.Name);
        Assert.Equal(changed.Name, listed.Name);
        Assert.Equal(2.5, listed.Bounds.Width / listed.Bounds.Height, 2);
        Assert.Equal(100, listed.Bounds.Width, 1);
        Assert.Equal(170, (listed.Bounds.Left + listed.Bounds.Right) / 2, 1);
        Assert.Equal(120, (listed.Bounds.Top + listed.Bounds.Bottom) / 2, 1);
        Assert.True(listed.IsSignatureImage);
        AssertSamePixels(image, await document.ReadSignatureImageAsync(LocatorOf(listed), Ct));

        // Saved and reloaded, it is still one and the same, still its own.
        var saved = await SaveAsync(document, folder);
        await using var reopened = await OpenAsync(saved);
        var again = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        Assert.True(again.IsSignatureImage);
        AssertRectNear(listed.Bounds, again.Bounds, 1.0 / 16);
    }

    [Fact]
    public async Task ImageAppearanceHasPrintFlagAndAppearanceObject()
    {
        var copy = Copy("text-basic.pdf");
        var original = await File.ReadAllBytesAsync(copy, Ct);
        string saved;
        await using (var document = await OpenAsync(copy))
        {
            await document.CreateSignatureImageAsync(0, new PageRect(30, 40, 110, 72), NoisyImage(), null, Ct);
            saved = await SaveAsync(document, folder);
        }

        var appended = Appended(original, await File.ReadAllBytesAsync(saved, Ct));

        Assert.Matches(@"/Subtype\s*/Stamp", appended);
        Assert.Matches(@"/F\s*4(?!\d)", appended);
        Assert.Matches(@"/Subtype\s*/Image", appended);
        Assert.Contains("/SMask", appended, StringComparison.Ordinal);
        Assert.Contains("/PagewingSignature", appended, StringComparison.Ordinal);
        Assert.Matches(@"/AP\s*<<\s*/N\s*\d+\s+0\s+R", appended);
        Assert.Matches(@"/Subtype\s*/Form", appended);
    }

    [Theory]
    [InlineData("rotated-page.pdf", 10, 20, 90, 52)]
    [InlineData("cropbox.pdf", 20, 30, 100, 62)]
    [InlineData("text-basic.pdf", 150, 150, 230, 182)]
    public async Task SignatureStaysUprightAndInPlaceOnRotatedAndCroppedPages(string fixture, double left, double top, double right, double bottom)
    {
        // Upright and in place: the red block is at the top left of the box, the rest of the box shows the page.
        var box = new PageRect(left, top, right, bottom);
        string saved;
        RenderedImage before;
        await using (var plain = await OpenCopyAsync(fixture))
        {
            before = await plain.RenderPageAsync(0, Scale, cancellationToken: Ct);
        }

        await using (var document = await OpenCopyAsync(fixture))
        {
            await document.CreateSignatureImageAsync(0, box, BlockImage(), null, Ct);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        AssertRectNear(box, annotation.Bounds, 1.0 / 16);
        var rendered = await reopened.RenderPageAsync(0, Scale, cancellationToken: Ct);
        var width = box.Width;
        var height = box.Height;
        var block = MeanColor(rendered, Scale, new PageRect(left + 1, top + 1, left + (width / 2) - 1, top + (height / 2) - 1));
        Assert.True(block.R > 180 && block.G < 40 && block.B < 40, $"The block is not red at the top left: {block}");
        foreach (var other in new[]
        {
            new PageRect(left + (width / 2) + 1, top + 1, right - 1, top + (height / 2) - 1),
            new PageRect(left + 1, top + (height / 2) + 1, left + (width / 2) - 1, bottom - 1),
            new PageRect(left + (width / 2) + 1, top + (height / 2) + 1, right - 1, bottom - 1),
        })
        {
            var mean = MeanColor(rendered, Scale, other);
            var page = MeanColor(before, Scale, other);
            Assert.True(Math.Abs(mean.R - page.R) < 3 && Math.Abs(mean.G - page.G) < 3 && Math.Abs(mean.B - page.B) < 3, $"{other} is not the page: {mean} instead of {page}");
        }
    }

    [Fact]
    public async Task BoxIsClampedToThePage()
    {
        await using var document = await OpenCopyAsync("text-basic.pdf");

        // Partly beyond the top left corner: moved in, same size.
        var moved = await document.CreateSignatureImageAsync(0, new PageRect(-20, -10, 60, 22), NoisyImage(), null, Ct);
        AssertRectNear(new PageRect(0, 0, 80, 32), moved.Bounds, 1.0 / 16);

        // Beyond the bottom right corner.
        var corner = await document.CreateSignatureImageAsync(0, new PageRect(250, 180, 330, 212), NoisyImage(), null, Ct);
        AssertRectNear(new PageRect(220, 168, 300, 200), corner.Bounds, 1.0 / 16);

        // Larger than the page (300 x 200): scaled down to fit, aspect kept.
        var huge = await document.CreateSignatureImageAsync(0, new PageRect(-100, -100, 900, 300), NoisyImage(), null, Ct);
        Assert.Equal(2.5, huge.Bounds.Width / huge.Bounds.Height, 2);
        Assert.True(huge.Bounds.Left >= -1e-3 && huge.Bounds.Top >= -1e-3 && huge.Bounds.Right <= 300 + 1e-3 && huge.Bounds.Bottom <= 200 + 1e-3, huge.Bounds.ToString());
        Assert.Equal(300, huge.Bounds.Width, 1);

        // Moving it by an edit is clamped the same way.
        var pushed = await document.UpdateAnnotationAsync(LocatorOf(moved), new AnnotationEdit(Geometry: DrawingGeometry.Rectangle(new PageRect(280, 190, 360, 222), 1)), Ct);
        AssertRectNear(new PageRect(220, 168, 300, 200), pushed.Bounds, 1.0 / 16);
    }

    [Fact]
    public async Task ForeignStampCannotBeChanged()
    {
        await using var document = await OpenCopyAsync(StampsForeign);

        var listed = await document.GetAnnotationsAsync(0, Ct);

        Assert.Equal([0, 1, 2, 3], listed.Select(a => a.Index));
        foreach (var stamp in listed.Take(3))
        {
            Assert.Equal(AnnotationKind.Other, stamp.Kind);
            Assert.False(stamp.Editable);
            Assert.False(stamp.GeometryEditable);
            Assert.False(stamp.IsSignatureImage);
            await Assert.ThrowsAsync<InvalidOperationException>(() => document.DeleteAnnotationAsync(LocatorOf(stamp), Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => document.UpdateAnnotationAsync(LocatorOf(stamp), new AnnotationEdit(Contents: "x"), Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => document.ReadSignatureImageAsync(LocatorOf(stamp), Ct));
        }

        // The ink with the subject "Signature" is a drawn signature (editable like any ink), but not one Pagewing can rewrite (/BS).
        var ink = listed[3];
        Assert.Equal(AnnotationKind.Signature, ink.Kind);
        Assert.False(ink.IsSignatureImage);
        Assert.True(ink.Editable);
        Assert.False(ink.GeometryEditable);
    }

    [Fact]
    public async Task ColourOfAnImageCannotBeChangedButItsCommentCan()
    {
        await using var document = await OpenCopyAsync("text-basic.pdf");
        var created = await document.CreateSignatureImageAsync(0, new PageRect(30, 40, 110, 72), NoisyImage(), null, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => document.UpdateAnnotationAsync(LocatorOf(created), new AnnotationEdit(Color: Red), Ct));
        var commented = await document.UpdateAnnotationAsync(LocatorOf(created), new AnnotationEdit(Contents: "Signed for the lease"), Ct);

        Assert.Equal("Signed for the lease", commented.Contents);
        Assert.True(commented.IsSignatureImage);
        Assert.True(commented.GeometryEditable);
        var listed = Assert.Single(await document.GetAnnotationsAsync(0, Ct));
        Assert.Equal("Signed for the lease", listed.Contents);
        Assert.True(listed.IsSignatureImage);
    }

    [Fact]
    public async Task ImageIsRecreatedUnderTheSameNameAfterDeleting()
    {
        // What undo of a deletion does (task 7): read the pixels, delete, create again with the name.
        var image = NoisyImage();
        await using var document = await OpenCopyAsync("text-basic.pdf");
        var created = await document.CreateSignatureImageAsync(0, new PageRect(30, 40, 110, 72), image, "Ada", Ct);
        var pixels = await document.ReadSignatureImageAsync(LocatorOf(created), Ct);
        await document.DeleteAnnotationAsync(LocatorOf(created), Ct);
        Assert.Empty(await document.GetAnnotationsAsync(0, Ct));

        var again = await document.CreateSignatureImageAsync(0, created.Bounds, pixels, "Ada", created.Name, Ct);

        Assert.Equal(created.Name, again.Name);
        AssertRectNear(created.Bounds, again.Bounds, 1.0 / 16);
        AssertSamePixels(image, await document.ReadSignatureImageAsync(LocatorOf(again), Ct));
    }

    [Fact]
    public async Task EmptyOrBadInputIsRefused()
    {
        await using var document = await OpenCopyAsync("text-basic.pdf");
        var empty = new SignatureImage(10, 10, new byte[400]);
        var image = NoisyImage();

        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateSignatureImageAsync(0, new PageRect(30, 40, 110, 72), empty, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateSignatureImageAsync(0, new PageRect(30, 40, 30, 72), image, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateSignatureImageAsync(0, new PageRect(30, 40, double.NaN, 72), image, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.CreateSignatureImageAsync(5, new PageRect(30, 40, 110, 72), image, null, Ct));
        Assert.Empty(await document.GetAnnotationsAsync(0, Ct));
    }

    [Fact]
    public async Task ExistingBytesStayThePrefix()
    {
        var copy = Copy("text-basic.pdf");
        var original = await File.ReadAllBytesAsync(copy, Ct);
        string saved;
        await using (var document = await OpenAsync(copy))
        {
            await document.CreateDrawingAsync(0, DrawingGeometry.Signature([[new(30, 80), new(60, 30), new(90, 90)]], 2), Black, null, null, Ct);
            await document.CreateSignatureImageAsync(0, new PageRect(30, 120, 110, 152), NoisyImage(), null, Ct);
            saved = await SaveAsync(document, folder);
        }

        var appended = Appended(original, await File.ReadAllBytesAsync(saved, Ct));

        Assert.NotEmpty(appended);
    }

    [Fact]
    public async Task LimitIsReported()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < PdfDocument.MaxAnnotations; i++)
        {
            builder.Append("<< /Subtype /Text /Rect [10 10 20 20] >> ");
        }

        var full = WritePdf(folder, "full.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{builder}] >>",
        ]);
        await using var document = await OpenAsync(full);

        await Assert.ThrowsAsync<PdfAnnotationLimitException>(() => document.CreateSignatureImageAsync(0, new PageRect(30, 40, 110, 72), NoisyImage(), null, Ct));
        await Assert.ThrowsAsync<PdfAnnotationLimitException>(() => document.CreateDrawingAsync(0, DrawingGeometry.Signature([[new(30, 80), new(60, 30)]], 2), Black, null, null, Ct));
        Assert.Equal(PdfDocument.MaxAnnotations, (await document.GetAnnotationsAsync(0, Ct)).Count);
    }

    private static string Appended(byte[] original, byte[] saved)
    {
        Assert.True(saved.Length > original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "The original bytes were changed.");
        return Encoding.Latin1.GetString(saved, original.Length, saved.Length - original.Length);
    }
}
