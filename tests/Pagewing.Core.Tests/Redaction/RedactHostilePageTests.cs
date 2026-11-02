using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// Pages built to cost the redaction run too much (milestone 14.1, final review): an image under a very long clipping path, a page full
/// of images under many rectangles, an overlay text that would take the page past the object limit. Each limit is exercised through the
/// same function the analysis and applying share. The files are written to the temp folder and never touch the test data.
/// </summary>
public sealed class RedactHostilePageTests
{
    private const int ImageSide = 96;

    // The area lies over the middle of the image (user space 120..170 by 70..120, from the top of the 200 point page 80..130).
    private static readonly PageRect OverTheImage = new(120, 80, 170, 130);

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Writes a page of 300 x 200 points whose content may draw a 96 x 96 pixel RGB image as <c>/Im1</c>.</summary>
    private static string Write(string content)
    {
        var pdf = new VerifierPdf(content);
        var pixels = new byte[ImageSide * ImageSide * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 96 /Height 96 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels);
        pdf.ResourceEntries = $"/XObject << /Im1 {image} 0 R >>";
        var path = Path.Combine(Path.GetTempPath(), $"pw-hostile-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf.Build());
        return path;
    }

    /// <summary>The image drawn under a clipping path of the given number of rectangles, each larger than the one before (so PDFium keeps all of them).</summary>
    private static string ClippedImage(int clipPaths)
    {
        var clips = string.Concat(Enumerable.Range(0, clipPaths).Select(static i => FormattableString.Invariant($"{100 - i} {50 - i} {96 + (2 * i)} {96 + (2 * i)} re W n\n")));
        return $"q\n{clips}96 0 0 96 100 50 cm /Im1 Do Q";
    }

    private static IReadOnlyList<PageRect> TinyAreas(int count) =>
        [.. Enumerable.Range(0, count).Select(static i => new PageRect(110 + ((i % 25) * 3), 80 + ((i / 25) * 3), 111.5 + ((i % 25) * 3), 81.5 + ((i / 25) * 3)))];

    private async Task<PageRedactionImpact> RedactAsync(string path, IReadOnlyList<PageRect> areas, bool apply)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        return apply
            ? await document.RedactPageAsync(0, areas, RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct)
            : await document.AnalyzeRedactionAsync(0, areas, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
    }

    [Fact]
    public async Task AFewClipRectanglesAroundAnImageStillAllowTreating()
    {
        var path = Write(ClippedImage(10));
        try
        {
            var impact = await RedactAsync(path, [OverTheImage], apply: true);
            Assert.Null(impact.Refusal);
            Assert.Equal(1, impact.ImageAreasTreated);
            Assert.Equal(0, impact.ImageAreasSolid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AClipPathOfManyRectanglesCountsAsClippedWithoutReadingThemAll()
    {
        var path = Write(ClippedImage(PdfiumRedactionObjects.MaxClipPaths + 6));
        try
        {
            foreach (var apply in new[] { false, true })
            {
                var impact = await RedactAsync(path, [OverTheImage], apply);
                Assert.Null(impact.Refusal);
                Assert.Equal(0, impact.ImageAreasTreated);
                Assert.Equal(1, impact.ImageAreasSolid);
                Assert.Equal([ImageSolidReason.Clipped], impact.ImageSolidReasons);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ManyImagesUnderManyRectanglesWithinTheBoxBudgetAreNotRefused()
    {
        var path = Write(string.Concat(Enumerable.Repeat("q 96 0 0 96 100 50 cm /Im1 Do Q\n", 100)));
        try
        {
            var impact = await RedactAsync(path, TinyAreas(100), apply: false);
            Assert.Null(impact.Refusal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnImagePileUnderManyRectanglesIsRefusedBeforeItHoldsMillionsOfBoxes()
    {
        // 450 images on one spot under 450 rectangles that all touch every one of them: 202,500 pairs, more than the budget of one page.
        Assert.True(450 * 450 > PdfiumRedactionRun.MaxBoxesPerPage);
        var path = Write(string.Concat(Enumerable.Repeat("q 96 0 0 96 100 50 cm /Im1 Do Q\n", 450)));
        try
        {
            foreach (var apply in new[] { false, true })
            {
                var impact = await RedactAsync(path, TinyAreas(450), apply);
                Assert.NotNull(impact.Refusal);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnOverlayThatWouldTakeThePageOverTheObjectLimitIsCountedAsOverTheLimit()
    {
        var areas = Enumerable.Range(0, 10).Select(static i => (Source: i, Rect: new PdfiumRedactionArea(10 + (i * 20), 10, 25 + (i * 20), 30))).ToList();
        var work = new RedactionOverlayWork(Enumerable.Repeat<string?>("X", 10).ToList(), new RedactionOverlayStyle(), RedactionFill.Black, OverlayBudget.Full);
        var thread = PdfiumThread.Shared;
        var (plenty, tight, none) = await thread.InvokeAsync(
            () =>
            {
                var spec = work.ToSpec(thread);
                return (
                    PdfiumRedactionOverlay.Decide(spec, areas, static _ => false),
                    PdfiumRedactionOverlay.Decide(spec, areas, static _ => false, pathRoom: 3),
                    PdfiumRedactionOverlay.Decide(spec, areas, static _ => false, pathRoom: -5));
            },
            cancellationToken: Ct);

        Assert.Equal(10, plenty.Drawn);
        Assert.Equal(0, plenty.OverLimit);
        Assert.Equal(3, tight.Drawn);
        Assert.Equal(7, tight.OverLimit);
        Assert.Equal(7, tight.NotFitting);
        Assert.False(tight.LimitReached);
        Assert.Equal(0, none.Drawn);
        Assert.Equal(10, none.OverLimit);
    }

    [Fact]
    public async Task OverlayTextWithNoGlyphPartsIsNotDrawn()
    {
        // A text of white space only has a shape without parts: it is counted as not fitting, never as drawn.
        var areas = new List<(int Source, PdfiumRedactionArea Rect)> { (0, new PdfiumRedactionArea(10, 10, 80, 40)) };
        var work = new RedactionOverlayWork(["   "], new RedactionOverlayStyle(), RedactionFill.Black, OverlayBudget.Full);
        var thread = PdfiumThread.Shared;
        var plan = await thread.InvokeAsync(() => PdfiumRedactionOverlay.Decide(work.ToSpec(thread), areas, static _ => false), cancellationToken: Ct);
        Assert.Equal(0, plan.Drawn);
        Assert.Equal(1, plan.NotFitting);
    }

    [Fact]
    public void EveryReasonForTheSolidFillMapsOneToOne()
    {
        var all = Enum.GetValues<PdfiumImageSolidReason>();
        var mapped = all.Select(static r => PdfDocument.SolidReasonOf(r)).ToList();
        Assert.Equal(Enum.GetValues<ImageSolidReason>().Length, all.Length);
        Assert.Equal(all.Length, mapped.Distinct().Count());
        foreach (var reason in all)
        {
            Assert.Equal(reason.ToString(), PdfDocument.SolidReasonOf(reason).ToString());
        }

        Assert.Throws<InvalidOperationException>(static () => PdfDocument.SolidReasonOf((PdfiumImageSolidReason)99));
    }
}
