using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Signatures;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>The visible signature samples of milestone 12 for the check in other viewers (<c>packaging/README.md</c>, "Unterschriften im Fremdviewer prüfen").</summary>
public sealed partial class InteropSampleTests
{
    private const int SignatureImageWidth = 240;

    private const int SignatureImageHeight = 90;

    /// <summary>
    /// The signature samples: a drawn signature and an imported one (pixels with a soft alpha edge) on a plain, a rotated and a
    /// CropBox page, an imported one moved, a drawn one recoloured, and a drawn and an imported one on a signed document (DocMDP
    /// level 3 placeholder); each through the editor and its save, then read back. Writes <c>signature-*.pdf</c> and
    /// <c>README-signatures.txt</c> into <c>PAGEWING_INTEROP_OUT</c>.
    /// </summary>
    [Fact]
    public async Task WritesSignatureSamplesForOtherViewers()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Interop samples only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var engine = new PdfEngine();
        using var work = new TempFolder();
        var readme = new StringBuilder();
        readme.AppendLine("Visible signatures of Pagewing (milestone 12): a drawn signature is an ink annotation with the subject \"Signature\", an imported")
            .AppendLine("one a stamp annotation with one image (transparent background) and the key /PagewingSignature.")
            .AppendLine("Written by tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.Signatures.cs (PAGEWING_INTEROP_OUT).")
            .AppendLine("Check in Chrome and Acrobat Reader: the image signature shows only the ink (no white box), upright, once (also after the move),")
            .AppendLine("at the place and size listed below, the same on the rotated page and inside the CropBox; the drawn one shows its strokes.")
            .AppendLine("Acrobat lists each signature in the comment list. Save the file once in Acrobat and open it in Pagewing: is /Subj kept? (see the checklist).")
            .AppendLine("None of these is a digital signature and none is called valid or legally binding by Pagewing.")
            .AppendLine();

        await WriteAsync("signature-drawn.pdf", "text-basic.pdf", 0, "A drawn signature, 3 strokes, dark blue, 2 pt.", async (editor, size) =>
            await editor.ApplyDrawingAsync(0, DrawnSignature(size, 20, size.Height - 120, 200), Blue, null, Author));
        await WriteAsync("signature-image.pdf", "text-basic.pdf", 0, "An imported signature, 240 x 90 px, box 180 pt wide at the lower left.", async (editor, size) =>
            await editor.ApplySignatureImageAsync(0, ImageBox(size, 20, size.Height - 120, 180), SoftImage(), Author));
        await WriteAsync("signature-rotated.pdf", "rotated-page.pdf", 0, "Page with /Rotate 90; the imported signature stands upright on screen, the drawn one too.", async (editor, size) =>
        {
            await editor.ApplySignatureImageAsync(0, ImageBox(size, 20, 20, 150), SoftImage(), Author);
            await editor.ApplyDrawingAsync(0, DrawnSignature(size, 20, size.Height - 100, 150), Blue, null, Author);
        });
        await WriteAsync("signature-cropbox.pdf", "cropbox.pdf", 0, "Page with a CropBox offset; both signatures lie at the top left of what is shown.", async (editor, size) =>
        {
            await editor.ApplySignatureImageAsync(0, ImageBox(size, 10, 10, 150), SoftImage(), Author);
            await editor.ApplyDrawingAsync(0, DrawnSignature(size, 10, 80, 150), Blue, null, Author);
        });
        await WriteAsync("signature-moved.pdf", "text-basic.pdf", 0, "An imported signature created at the top and moved down by 150 pt and 40 pt to the right (one box change under the same /NM).", async (editor, size) =>
        {
            var created = (await editor.ApplySignatureImageAsync(0, ImageBox(size, 20, 20, 180), SoftImage(), Author))!;
            var to = created.Bounds with { Left = created.Bounds.Left + 40, Right = created.Bounds.Right + 40, Top = created.Bounds.Top + 150, Bottom = created.Bounds.Bottom + 150 };
            var moved = await editor.UpdateAsync(new AnnotationLocator(0, created.Index, created.Name), new AnnotationEdit(Geometry: DrawingGeometry.Rectangle(to, 1)));
            Assert.Equal(created.Name, moved.Name);
        });
        await WriteAsync("signature-recoloured.pdf", "text-basic.pdf", 0, "A drawn signature recoloured from dark blue to red (the appearance is made anew).", async (editor, size) =>
        {
            var created = (await editor.ApplyDrawingAsync(0, DrawnSignature(size, 20, 60, 200), Blue, null, Author))!;
            var recoloured = await editor.UpdateAsync(new AnnotationLocator(0, created.Index, created.Name), new AnnotationEdit(Color: new AnnotationColor(220, 30, 30)));
            Assert.Equal(new AnnotationColor(220, 30, 30), recoloured.Color);
        });
        await WriteAsync("signature-on-signed-document.pdf", "signed-docmdp-3.pdf", 0, "A drawn and an imported signature on the placeholder with a DocMDP level 3 (structure only, never valid).", async (editor, size) =>
        {
            await editor.ApplyDrawingAsync(0, DrawnSignature(size, 20, size.Height - 120, 160), Blue, null, Author);
            await editor.ApplySignatureImageAsync(0, ImageBox(size, 200, size.Height - 120, 150), SoftImage(), Author);
        });

        await File.WriteAllTextAsync(Path.Combine(output, "README-signatures.txt"), readme.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
        Assert.Empty(Directory.GetFiles(output, "*.tmp"));

        async Task WriteAsync(string name, string source, int pageIndex, string what, Func<AnnotationEditor, PdfPageSize, Task> annotate)
        {
            var copy = Path.Combine(work.Path, name);
            File.Copy(TestData.Pdf(source), copy);
            var target = Path.Combine(output, name);
            File.Delete(target);
            using (var session = new ViewerSession(engine))
            {
                await session.OpenAsync(copy);
                Assert.Equal(ViewerState.Ready, session.State);
                session.Document!.Clock = new FixedClock(Now);
                var editor = new AnnotationEditor(session);
                await annotate(editor, await session.Document.GetPageSizeAsync(pageIndex, Ct));
                await editor.SaveAsync(engine, target, overwriteChangedOriginal: false, progress: null, Ct);
            }

            // Read back: every signature is Pagewing's own, an image one with its pixels.
            await using var reopened = await OpenAsync(target);
            var signatures = (await reopened.GetAnnotationsAsync(pageIndex, Ct)).Where(a => a.Kind == AnnotationKind.Signature).ToList();
            Assert.NotEmpty(signatures);
            readme.AppendLine(CultureInfo.InvariantCulture, $"{name} (from {source}, page {pageIndex + 1}): {what}");
            foreach (var signature in signatures)
            {
                Assert.True(signature.Editable, $"{name}: {signature.Name} is not editable.");
                if (signature.IsSignatureImage)
                {
                    Assert.NotNull(await reopened.ReadSignatureImageAsync(new AnnotationLocator(pageIndex, signature.Index, signature.Name), Ct));
                }

                readme.AppendLine(CultureInfo.InvariantCulture, $"  - {(signature.IsSignatureImage ? "image" : "drawn")}, box {signature.Bounds.Left:0.#} {signature.Bounds.Top:0.#} {signature.Bounds.Right:0.#} {signature.Bounds.Bottom:0.#} (pt from the top left)");
            }
        }
    }

    private static readonly AnnotationColor Blue = new(20, 40, 140);

    /// <summary>The box of an image signature of <paramref name="width"/> points at the given top left, the aspect of <see cref="SoftImage"/>.</summary>
    private static PageRect ImageBox(PdfPageSize size, double left, double top, double width)
    {
        var height = width * SignatureImageHeight / SignatureImageWidth;
        var l = Math.Clamp(left, 0, Math.Max(0, size.Width - width));
        var t = Math.Clamp(top, 0, Math.Max(0, size.Height - height));
        return new PageRect(l, t, l + width, t + height);
    }

    /// <summary>A three-stroke signature (a loop, a flourish and a dash) of <paramref name="width"/> points at the given top left, in display points.</summary>
    private static DrawingGeometry DrawnSignature(PdfPageSize size, double left, double top, double width)
    {
        var height = width * 0.4;
        var l = Math.Clamp(left, 0, Math.Max(0, size.Width - width));
        var t = Math.Clamp(top, 0, Math.Max(0, size.Height - height));
        var loop = new List<DisplayPoint>();
        for (var i = 0; i <= 40; i++)
        {
            var a = i / 40.0 * Math.Tau * 1.5;
            loop.Add(new DisplayPoint(l + (width * (0.12 + (0.1 * Math.Cos(a)) + (0.02 * i / 4))), t + (height * (0.5 + (0.4 * Math.Sin(a))))));
        }

        var flourish = new List<DisplayPoint>();
        for (var i = 0; i <= 30; i++)
        {
            var x = i / 30.0;
            flourish.Add(new DisplayPoint(l + (width * (0.3 + (0.6 * x))), t + (height * (0.55 - (0.25 * Math.Sin(x * Math.PI * 2))))));
        }

        IReadOnlyList<DisplayPoint> dash = [new DisplayPoint(l + (width * 0.3), t + (height * 0.95)), new DisplayPoint(l + (width * 0.95), t + (height * 0.9))];
        return DrawingGeometry.Signature([loop, flourish, dash], 2);
    }

    /// <summary>
    /// 240 x 90 px: a dark blue ink curve with a soft alpha edge on a fully transparent background (the colour of transparent pixels is
    /// white, which a viewer that ignores the alpha channel would show as a white box).
    /// </summary>
    private static SignatureImage SoftImage()
    {
        var bgra = new byte[SignatureImageWidth * SignatureImageHeight * 4];
        for (var y = 0; y < SignatureImageHeight; y++)
        {
            for (var x = 0; x < SignatureImageWidth; x++)
            {
                var curveY = (SignatureImageHeight * 0.55) - (SignatureImageHeight * 0.3 * Math.Sin(x / (double)SignatureImageWidth * Math.PI * 3));
                var distance = Math.Abs(y - curveY);
                var alpha = (byte)Math.Clamp(255 * (3.5 - distance) / 2, 0, 255);
                var o = ((y * SignatureImageWidth) + x) * 4;
                bgra[o] = alpha == 0 ? (byte)255 : (byte)140;
                bgra[o + 1] = alpha == 0 ? (byte)255 : (byte)40;
                bgra[o + 2] = alpha == 0 ? (byte)255 : (byte)20;
                bgra[o + 3] = alpha;
            }
        }

        return new SignatureImage(SignatureImageWidth, SignatureImageHeight, bgra);
    }
}
