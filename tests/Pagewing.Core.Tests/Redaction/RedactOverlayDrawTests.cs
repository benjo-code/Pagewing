using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// Overlay text drawn as path objects (milestone 14.1, task 15): no text and no font in the file, nothing outside the marked rectangle, upright
/// on a turned page with a crop box, tiled and sized to fit, above the image of a page that became an image, counted when it is left out
/// (a treated image, no room, the limit of the document) with the same numbers in the analysis and when applying. The files are copies in a
/// temp folder.
/// </summary>
public sealed class RedactOverlayDrawTests : IDisposable
{
    // Page 300 x 200 pt, "secret value" at (20, 150) and "public line" at (20, 100) in Helvetica 12: the first line stands at y 41 to 53 from the top.
    private static readonly PageRect SecretArea = new(15, 38, 110, 56);
    private const string Secret = "BT /F1 12 Tf 20 150 Td (secret value) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET";

    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();
    private int counter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    // ------------------------------------------------------------------ helpers

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(folder.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private static RedactionMark Mark(int page, PageRect area, string? text) =>
        new(Guid.NewGuid(), RedactionKind.Area, page, [area], null, false, text);

    private static RedactionPlan PlanOf(params RedactionMark[] marks) => RedactionPlan.Empty.WithRange(marks);

    /// <summary>What the analysis said, what building the plan did page by page, and the checked file that was written (when the test asked for it).</summary>
    private sealed record Applied(RedactionAnalysis Analysis, IReadOnlyList<PageRedactionImpact> Impacts, string Target, RedactionApplyResult? Result);

    private async Task<Applied> ApplyAsync(string path, RedactionPlan plan, bool write = true)
    {
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var executor = new RedactionExecutor(engine);
        var analysis = await executor.AnalyzeAsync(plan, shown, path, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        IReadOnlyList<PageRedactionImpact> impacts;
        await using (var build = await executor.BuildAsync(plan, shown, path, null, null, Ct))
        {
            impacts = build.Impacts;
        }

        var target = Path.Combine(folder.Path, $"result-{++counter}.pdf");
        RedactionApplyResult? result = null;
        if (write)
        {
            result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, target), null, Ct);
            Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        }

        return new Applied(analysis, impacts, target, result);
    }

    /// <summary>The file as Latin-1 text, and every Flate stream unpacked behind it: what a search of the bytes would see.</summary>
    private static string AllText(byte[] file)
    {
        var plain = Encoding.Latin1.GetString(file);
        var text = new StringBuilder(plain);
        var at = 0;
        while ((at = plain.IndexOf("stream", at, StringComparison.Ordinal)) >= 0)
        {
            var start = at + "stream".Length;
            if (start < plain.Length && plain[start] == '\r')
            {
                start++;
            }

            if (start < plain.Length && plain[start] == '\n')
            {
                start++;
            }

            var end = plain.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            try
            {
                using var input = new MemoryStream(file, start, end - start);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                text.Append(Encoding.Latin1.GetString(output.ToArray()));
            }
            catch (InvalidDataException)
            {
                // Not a Flate stream.
            }

            at = end + "endstream".Length;
        }

        return text.ToString();
    }

    private static bool Inside(PageRect box, PageRect area, double slack = RedactionVerifier.PathSlack) =>
        box.Left >= area.Left - slack && box.Right <= area.Right + slack && box.Top >= area.Top - slack && box.Bottom <= area.Bottom + slack;

    private static PageRect Union(IEnumerable<PageRect> boxes) =>
        boxes.Aggregate(static (a, b) => new PageRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));

    /// <summary>How many pixels of a rendered page have all three channels at least <paramref name="level"/>, inside the box (points).</summary>
    private static int LightPixels(RenderedImage image, PageRect box, double scale, int level = 200)
    {
        var span = image.Pixels.Span;
        var count = 0;
        for (var y = (int)(box.Top * scale); y < Math.Min(image.Height, (int)(box.Bottom * scale)); y++)
        {
            for (var x = (int)(box.Left * scale); x < Math.Min(image.Width, (int)(box.Right * scale)); x++)
            {
                var at = (y * image.Stride) + (x * 4);
                if (span[at] >= level && span[at + 1] >= level && span[at + 2] >= level)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static async Task<RenderedImage> RenderAsync(PdfDocument document, int page, double scale) =>
        await document.RenderPageAsync(page, scale, new RenderOptions { Priority = PdfPriority.Background }, Ct);

    /// <summary>The bounds of the paths on a page other than the opaque areas themselves (a path as large as an area is that area).</summary>
    private static IReadOnlyList<PageRect> OverlayBounds(PageObjectSummary summary, params PageRect[] areas) =>
        [.. summary.PathBounds!.Where(b => !areas.Any(a => Math.Abs(b.Left - a.Left) < 0.5 && Math.Abs(b.Top - a.Top) < 0.5 && Math.Abs(b.Right - a.Right) < 0.5 && Math.Abs(b.Bottom - a.Bottom) < 0.5))];

    /// <summary>The layout the engine will draw for a text in an area, with the real glyph widths (read on the PDFium thread).</summary>
    private static async Task<OverlayPlacement> LayoutAsync(string text, PageRect area, RedactionOverlayStyle style)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () => RedactionOverlayLayout.Fit(text, area, style, RedactionOverlayLayout.DefaultFontSize, TextBoxFont.MetricsOf(TextBoxFont.Table(thread))),
            cancellationToken: Ct);
    }

    // ------------------------------------------------------------------ shapes, not text

    [Fact]
    public async Task OverlayIsPathsNotText()
    {
        var path = Write("secret.pdf", new VerifierPdf(Secret).Build());

        var plain = await ApplyAsync(path, PlanOf(Mark(0, SecretArea, null)));
        var drawn = await ApplyAsync(path, PlanOf(Mark(0, SecretArea, "REDACTED")));

        await using var without = plain.Result!.Saved.Reopened;
        await using var with = drawn.Result!.Saved.Reopened;
        var before = await without.GetPageObjectSummaryAsync(0, Ct);
        var after = await with.GetPageObjectSummaryAsync(0, Ct);

        // The same text objects: the overlay adds none. It adds path objects and nothing else.
        Assert.Equal(before.Texts, after.Texts);
        Assert.True(after.Paths > before.Paths, "The overlay text is not on the page.");
        Assert.Equal(before.Images, after.Images);
        Assert.Equal(before.Others, after.Others);
        Assert.Equal(before.Annotations, after.Annotations);

        var pageText = string.Concat((await with.GetPageTextAsync(0, Ct)).Chars.Select(static c => c.Text));
        Assert.DoesNotContain("REDACTED", pageText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", pageText, StringComparison.Ordinal);
        Assert.Contains("public line", pageText, StringComparison.Ordinal);

        // Nothing of it in the bytes, packed or unpacked, and no font of the overlay in the file.
        var bytes = AllText(await File.ReadAllBytesAsync(drawn.Target, Ct));
        Assert.DoesNotContain("REDACTED", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("Geist", bytes, StringComparison.OrdinalIgnoreCase);

        // The check says what it does not read.
        Assert.Contains(drawn.Result.Check.Items, static i => i.Kind == RedactionNotCheckedKind.OverlayTextNotRead);
        Assert.DoesNotContain(plain.Result.Check.Items, static i => i.Kind == RedactionNotCheckedKind.OverlayTextNotRead);
    }

    [Fact]
    public async Task OverlayStaysInsideTheMarkedRectangle()
    {
        var path = Write("secret.pdf", new VerifierPdf(Secret).Build());

        // A second mark reaches out of the page on the left: its text stays inside the part that is on the page.
        var reaching = new PageRect(-40, 120, 70, 150);
        var clipped = new PageRect(0, 120, 70, 150);
        var applied = await ApplyAsync(path, PlanOf(Mark(0, SecretArea, "REDACTED"), Mark(0, reaching, "OUT")));

        await using var written = applied.Result!.Saved.Reopened;
        var summary = await written.GetPageObjectSummaryAsync(0, Ct);
        Assert.Equal(summary.Paths, summary.PathBounds!.Count);
        Assert.All(summary.PathBounds, box => Assert.True(Inside(box, SecretArea) || Inside(box, clipped), $"A path reaches outside the marked areas: {box}."));
        var shapes = OverlayBounds(summary, SecretArea, clipped);
        Assert.Contains(shapes, box => Inside(box, SecretArea));
        Assert.Contains(shapes, box => Inside(box, clipped));

        // What was drawn lies inside by the margin of the layout, not by the slack of the check.
        Assert.All(shapes, box => Assert.True(Inside(box, SecretArea, -0.5) || Inside(box, clipped, -0.5), $"A shape touches the edge of its area: {box}."));
    }

    [Fact]
    public async Task OverlayOnRotatedPageReadsUpright()
    {
        // A page turned by 90 degrees with a crop box that is not the media box; the same display area on a flat page of the displayed size is the reference.
        var turned = new VerifierPdf(" ") { PageEntries = "/Rotate 90 /CropBox [10 10 290 190]" };
        var flat = new VerifierPdf(" ") { MediaBox = "0 0 180 280" };
        var area = new PageRect(20, 40, 150, 100);
        var plan = PlanOf(Mark(0, area, "TURN"));

        var onTurned = await ApplyAsync(Write("turned.pdf", turned.Build()), plan);
        var onFlat = await ApplyAsync(Write("flat.pdf", flat.Build()), plan);

        await using var turnedDocument = onTurned.Result!.Saved.Reopened;
        await using var flatDocument = onFlat.Result!.Saved.Reopened;
        var a = await RenderAsync(turnedDocument, 0, 2);
        var b = await RenderAsync(flatDocument, 0, 2);
        Assert.Equal((b.Width, b.Height), (a.Width, a.Height));

        // The text is white on the black area, and it is there.
        var text = LightPixels(b, area, 2);
        Assert.True(text > 60, $"The reference shows only {text} light pixels.");
        Assert.True(LightPixels(a, area, 2) > 60);

        // Upright and in the same place: apart from the edges of the glyphs the two renders are the same.
        var spanA = a.Pixels.Span;
        var spanB = b.Pixels.Span;
        var different = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var ia = (y * a.Stride) + (x * 4);
                var ib = (y * b.Stride) + (x * 4);
                if (Math.Abs(spanA[ia] - spanB[ib]) > 80)
                {
                    different++;
                }
            }
        }

        Assert.True(different * 7 < text, $"{different} pixels differ from the flat page where {text} are text: the overlay does not read the same way up.");

        // The paths of the written file sit in the displayed area, whatever the turn and the crop box.
        var summary = await turnedDocument.GetPageObjectSummaryAsync(0, Ct);
        Assert.All(summary.PathBounds!, box => Assert.True(Inside(box, area), $"{box} is outside {area}."));
    }

    [Theory]
    [InlineData(180, "0 0 280 180")]
    [InlineData(270, "0 0 180 280")]
    public async Task OverlayOnPagesTurnedBy180And270ReadsUpright(int turn, string flatBox)
    {
        // The text "TURN" is not symmetric either way: a mirrored or upside-down overlay differs from the flat reference.
        var turned = new VerifierPdf(" ") { PageEntries = $"/Rotate {turn} /CropBox [10 10 290 190]" };
        var flat = new VerifierPdf(" ") { MediaBox = flatBox };
        var area = new PageRect(20, 40, 150, 100);
        var plan = PlanOf(Mark(0, area, "TURN"));

        var onTurned = await ApplyAsync(Write("turned.pdf", turned.Build()), plan);
        var onFlat = await ApplyAsync(Write("flat.pdf", flat.Build()), plan);

        await using var turnedDocument = onTurned.Result!.Saved.Reopened;
        await using var flatDocument = onFlat.Result!.Saved.Reopened;
        var a = await RenderAsync(turnedDocument, 0, 2);
        var b = await RenderAsync(flatDocument, 0, 2);
        Assert.Equal((b.Width, b.Height), (a.Width, a.Height));
        var text = LightPixels(b, area, 2);
        Assert.True(text > 60, $"The reference shows only {text} light pixels.");
        var spanA = a.Pixels.Span;
        var spanB = b.Pixels.Span;
        var different = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (Math.Abs(spanA[(y * a.Stride) + (x * 4)] - spanB[(y * b.Stride) + (x * 4)]) > 80)
                {
                    different++;
                }
            }
        }

        Assert.True(different * 7 < text, $"{different} pixels differ from the flat page where {text} are text.");
        var summary = await turnedDocument.GetPageObjectSummaryAsync(0, Ct);
        Assert.All(summary.PathBounds!, box => Assert.True(Inside(box, area), $"{box} is outside {area}."));
    }

    [Fact]
    public async Task EveryRectangleOfAMarkGetsTheTextAndIsCountedByItself()
    {
        var path = Write("secret.pdf", new VerifierPdf(Secret).Build());
        var second = new PageRect(150, 90, 280, 120);
        var tiny = new PageRect(200, 150, 206, 154);

        // One mark, two rectangles: both get the text, and the count is one for each rectangle.
        var both = await ApplyAsync(path, PlanOf(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [SecretArea, second], null, false, "REDACTED")));
        Assert.Equal(2, Assert.Single(both.Impacts).OverlaysDrawn);
        Assert.Equal(2, Assert.Single(both.Analysis.Pages).OverlaysDrawn);
        await using (var written = both.Result!.Saved.Reopened)
        {
            var summary = await written.GetPageObjectSummaryAsync(0, Ct);
            var shapes = OverlayBounds(summary, SecretArea, second);
            Assert.Contains(shapes, box => Inside(box, SecretArea, -0.5));
            Assert.Contains(shapes, box => Inside(box, second, -0.5));
            Assert.All(shapes, box => Assert.True(Inside(box, SecretArea) || Inside(box, second)));
        }

        // A third rectangle without room: it is counted by itself, the others keep their text.
        var mixed = await ApplyAsync(path, PlanOf(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [SecretArea, tiny, second], null, false, "REDACTED")));
        var done = Assert.Single(mixed.Impacts);
        Assert.Equal((2, 1), (done.OverlaysDrawn, done.OverlaysNotFitting));
        Assert.Equal((2, 1), (mixed.Analysis.Pages[0].OverlaysDrawn, mixed.Analysis.Pages[0].OverlaysNotFitting));
    }

    [Fact]
    public async Task RepeatCoversTheAreaAndAutoSizeFits()
    {
        var path = Write("blank.pdf", new VerifierPdf(" ").Build());

        // Repeat: as many tiles as the layout gives, one path each, spread over the whole area.
        var big = new PageRect(20, 20, 260, 120);
        var repeat = new RedactionOverlayStyle(Repeat: true);
        var expected = await LayoutAsync("Zeile", big, repeat);
        Assert.True(expected.Fits);
        Assert.True(expected.Lines.Count >= 12);
        var tiled = await ApplyAsync(path, PlanOf(Mark(0, big, "Zeile")).WithOverlayStyle(repeat));
        await using (var written = tiled.Result!.Saved.Reopened)
        {
            var summary = await written.GetPageObjectSummaryAsync(0, Ct);
            var shapes = OverlayBounds(summary, big);
            Assert.Equal(expected.Lines.Count, shapes.Count);
            var span = Union(shapes);
            Assert.True(span.Right - span.Left >= 0.8 * (big.Right - big.Left), $"The tiles cover {span.Right - span.Left} of {big.Right - big.Left} pt across.");
            Assert.True(span.Bottom - span.Top >= 0.7 * (big.Bottom - big.Top), $"The tiles cover {span.Bottom - span.Top} of {big.Bottom - big.Top} pt down.");
            Assert.All(shapes, box => Assert.True(Inside(box, big)));
        }

        Assert.Equal(expected.Lines.Count, tiled.Analysis.Pages[0].OverlayPaths);

        // Automatic size: the text is set as large as the area allows, once, and fits.
        var wide = new PageRect(20, 20, 160, 60);
        var single = await ApplyAsync(path, PlanOf(Mark(0, wide, "FITS")));
        await using (var written = single.Result!.Saved.Reopened)
        {
            var summary = await written.GetPageObjectSummaryAsync(0, Ct);
            var shapes = OverlayBounds(summary, wide);
            Assert.NotEmpty(shapes);
            var span = Union(shapes);
            Assert.True(Inside(span, wide, -0.5), $"{span} is not inside {wide}.");
            Assert.True(span.Bottom - span.Top >= 0.4 * (wide.Bottom - wide.Top), $"The text is only {span.Bottom - span.Top} pt high in an area {wide.Bottom - wide.Top} pt high.");
            Assert.True(((span.Left + span.Right) / 2) is > 85 and < 95, "The text is not centred across.");
        }
    }

    [Fact]
    public async Task RenderCheckOutsideAreasIsUnaffected()
    {
        var path = Write("secret.pdf", new VerifierPdf(Secret).Build());
        var plan = PlanOf(Mark(0, SecretArea, "REDACTED"));

        // The checked write (which renders the page against the original outside the marks) passes with the overlay ...
        var applied = await ApplyAsync(path, plan);
        Assert.Contains(applied.Result!.Check.Items, static i => i.Kind == RedactionNotCheckedKind.ApplyRender);

        // ... and looking at it by hand, the pixels outside the area and its margin are those of the original.
        await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
        await using var written = applied.Result.Saved.Reopened;
        var before = await RenderAsync(original, 0, 2);
        var after = await RenderAsync(written, 0, 2);
        var result = RedactionRenderCheck.Compare(before, after, 2, [SecretArea]);
        Assert.True(result.Passes, $"{result.Different} pixels differ outside the area.");
        Assert.True(LightPixels(after, SecretArea, 2) > 0);
    }

    // ------------------------------------------------------------------ colour

    [Fact]
    public void AutoColorKeepsContrastOnWhiteBlackAndFreeFill()
    {
        Assert.Equal(RedactionFill.White, RedactionOverlayColor.Choose(RedactionFill.Black, null));
        Assert.Equal(RedactionFill.Black, RedactionOverlayColor.Choose(RedactionFill.White, null));

        // Every grey and a spread of free colours: the colour that is chosen reads on the fill at 4.5:1 or better.
        var worst = double.MaxValue;
        for (var level = 0; level <= 255; level++)
        {
            var fill = new RedactionFill((byte)level, (byte)level, (byte)level);
            worst = Math.Min(worst, RedactionOverlayColor.Contrast(RedactionOverlayColor.Choose(fill, null), fill));
        }

        var random = new Random(14);
        for (var i = 0; i < 20_000; i++)
        {
            var fill = new RedactionFill((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            var chosen = RedactionOverlayColor.Choose(fill, null);
            Assert.True(chosen == RedactionFill.White || chosen == RedactionFill.Black);
            var contrast = RedactionOverlayColor.Contrast(chosen, fill);
            Assert.True(contrast >= RedactionOverlayColor.MinContrast, $"{fill.ToHex()} gets {chosen.ToHex()} at {contrast:0.00}:1.");
            Assert.True(contrast >= RedactionOverlayColor.Contrast(chosen == RedactionFill.White ? RedactionFill.Black : RedactionFill.White, fill));
            worst = Math.Min(worst, contrast);
        }

        Assert.True(worst >= 4.5);

        // The presets of the colour field: dark ones get white, the light grey gets black.
        Assert.Equal(RedactionFill.White, RedactionOverlayColor.Choose(RedactionFill.Presets[0], null));
        Assert.Equal(RedactionFill.Black, RedactionOverlayColor.Choose(RedactionFill.Presets[2], null));

        // A colour the user chose stands, even below 4.5:1 (the interface warns, it does not forbid).
        var grey = new RedactionFill(0x30, 0x30, 0x30);
        Assert.Equal(grey, RedactionOverlayColor.Choose(RedactionFill.Black, grey));
        Assert.True(RedactionOverlayColor.Contrast(grey, RedactionFill.Black) < RedactionOverlayColor.MinContrast);
        Assert.Equal(21, RedactionOverlayColor.Contrast(RedactionFill.White, RedactionFill.Black), 6);
    }

    [Fact]
    public async Task TheColourOfTheTextIsOnThePage()
    {
        var path = Write("secret.pdf", new VerifierPdf(Secret).Build());

        // Black fill: white text. White fill: black text. A chosen colour: that colour.
        foreach (var (fill, style, expectLight) in new (RedactionFill, RedactionOverlayStyle, bool)[]
        {
            (RedactionFill.Black, new RedactionOverlayStyle(), true),
            (RedactionFill.White, new RedactionOverlayStyle(), false),
            (RedactionFill.Black, new RedactionOverlayStyle(TextColor: new RedactionFill(255, 0, 0)), false),
        })
        {
            var applied = await ApplyAsync(path, PlanOf(Mark(0, SecretArea, "REDACTED")).WithFill(fill).WithOverlayStyle(style));
            await using var written = applied.Result!.Saved.Reopened;
            var image = await RenderAsync(written, 0, 2);
            var light = LightPixels(image, SecretArea, 2);
            var area = (int)(2 * 2 * (SecretArea.Right - SecretArea.Left) * (SecretArea.Bottom - SecretArea.Top));
            if (expectLight)
            {
                Assert.True(light > 40 && light < area / 2, $"{light} light pixels of {area}.");
            }
            else if (fill == RedactionFill.White)
            {
                // Black text on white: most of the area is light, and some of it is not.
                Assert.InRange(light, area * 6 / 10, area - 40);
            }
            else
            {
                // Red text on black: no light pixel at all, but red ones.
                Assert.Equal(0, light);
                Assert.True(RedPixels(image, SecretArea, 2) > 40);
            }
        }
    }

    private static int RedPixels(RenderedImage image, PageRect box, double scale)
    {
        var span = image.Pixels.Span;
        var count = 0;
        for (var y = (int)(box.Top * scale); y < (int)(box.Bottom * scale); y++)
        {
            for (var x = (int)(box.Left * scale); x < (int)(box.Right * scale); x++)
            {
                var at = (y * image.Stride) + (x * 4);
                if (span[at + 2] > 200 && span[at + 1] < 60 && span[at] < 60)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ------------------------------------------------------------------ left out, and counted

    [Fact]
    public async Task OverlayIsLeftOutOverTreatedImagesAndWhereItDoesNotFitAndCounted()
    {
        var path = Copy("redact-image-blocks.pdf");
        const double height = 330;
        var gradient = new PageRect(46, height - 274, 118, height - 202);      // over image 0
        var plainRectangle = new PageRect(100, height - 146, 170, height - 90); // over the red rectangle on the bare page
        var tiny = new PageRect(300, height - 308, 312, height - 300);          // bare page, 12 x 8 pt
        var plan = PlanOf(Mark(0, gradient, "X"), Mark(0, plainRectangle, "OK"), Mark(0, tiny, "Does not fit here"))
            .WithImageTreatment(RedactionImageTreatment.Pixelate);

        var applied = await ApplyAsync(path, plan);

        var analysed = Assert.Single(applied.Analysis.Pages);
        var done = Assert.Single(applied.Impacts);
        Assert.Equal(1, analysed.OverlaysNotShown);
        Assert.Equal(1, analysed.OverlaysNotFitting);
        Assert.Equal(1, analysed.OverlaysDrawn);
        Assert.Equal(0, analysed.OverlaysOverLimit);
        Assert.True(analysed.OverlayPaths > 0);

        // The analysis says what applying does, number for number.
        Assert.Equal((analysed.OverlaysNotShown, analysed.OverlaysNotFitting, analysed.OverlaysDrawn, analysed.OverlaysOverLimit, analysed.OverlayPaths, analysed.OverlaySegments),
            (done.OverlaysNotShown, done.OverlaysNotFitting, done.OverlaysDrawn, done.OverlaysOverLimit, done.OverlayPaths, done.OverlaySegments));
        Assert.Equal(1, applied.Analysis.OverlaysNotShown);
        Assert.Equal(1, applied.Analysis.OverlaysNotFitting);

        // The written page: nothing drawn over the pixelated image, text on the plain area, and the report names both.
        await using var written = applied.Result!.Saved.Reopened;
        var summary = await written.GetPageObjectSummaryAsync(0, Ct);
        Assert.DoesNotContain(summary.PathBounds!, box => Inside(box, gradient) && !(Math.Abs(box.Left - gradient.Left) < 0.5 && Math.Abs(box.Right - gradient.Right) < 0.5));
        Assert.Contains(summary.PathBounds!, box => Inside(box, plainRectangle, -0.5));
        Assert.DoesNotContain(summary.PathBounds!, box => Inside(box, tiny, -0.5));
        var report = RedactionReportText.Of(applied.Analysis);
        Assert.Contains("1 area gets no overlay text because a pixelated or blurred image lies under it", report, StringComparison.Ordinal);
        Assert.Contains("1 overlay text does not fit its area", report, StringComparison.Ordinal);
        Assert.DoesNotContain("limit for the overlay text", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlayStopsAtTheLimitOfThePageAndAnalysisSaysSo()
    {
        // One page of 1500 x 1000 pt with four areas of 700 x 450 pt, each tiled with a text: one area takes about 56,000 segments, a page may
        // take 150,000, so the first two areas get their text and the others stay filled without. The document is not at its limit.
        var path = Write("page.pdf", new VerifierPdf(" ") { MediaBox = "0 0 1500 1000" }.Build());
        PageRect[] areas = [new(30, 30, 730, 480), new(770, 30, 1470, 480), new(30, 520, 730, 970), new(770, 520, 1470, 970)];
        var plan = PlanOf([.. areas.Select(area => Mark(0, area, "Zeile"))]).WithOverlayStyle(new RedactionOverlayStyle(Repeat: true));

        var applied = await ApplyAsync(path, plan);

        var analysed = Assert.Single(applied.Analysis.Pages);
        var done = Assert.Single(applied.Impacts);
        Assert.Equal(2, done.OverlaysDrawn);
        Assert.Equal(2, done.OverlaysOverLimit);
        Assert.Equal(2, done.OverlaysNotFitting);
        Assert.False(done.OverlayLimitReached);
        Assert.True(done.OverlaySegments <= RedactionLimits.MaxOverlaySegmentsPerPage);
        Assert.True(done.OverlayPaths <= RedactionLimits.MaxOverlayPathsPerPage);
        Assert.Equal((analysed.OverlaysDrawn, analysed.OverlaysNotFitting, analysed.OverlaysOverLimit, analysed.OverlayPaths, analysed.OverlaySegments, analysed.OverlayLimitReached),
            (done.OverlaysDrawn, done.OverlaysNotFitting, done.OverlaysOverLimit, done.OverlayPaths, done.OverlaySegments, done.OverlayLimitReached));

        // The file holds what was counted: a path for each tile, and the opaque area of each of the four marks. The text is in the first two areas.
        await using var written = applied.Result!.Saved.Reopened;
        var summary = await written.GetPageObjectSummaryAsync(0, Ct);
        Assert.Equal(4 + done.OverlayPaths, summary.Paths);
        var shapes = OverlayBounds(summary, areas);
        Assert.Equal(done.OverlayPaths, shapes.Count);
        Assert.All(shapes, box => Assert.True(Inside(box, areas[0]) || Inside(box, areas[1])));

        var report = RedactionReportText.Of(applied.Analysis);
        Assert.Contains("2 overlay texts do not fit their areas", report, StringComparison.Ordinal);
        Assert.Contains("2 of them because the limit for the overlay text was reached", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlayStopsAtTheLimitOfTheDocumentAndAnalysisSaysSo()
    {
        // Nine pages of 1500 x 1000 pt with four areas of 520 x 340 pt each, tiled: a page takes about 125,000 segments (under its limit), the
        // document 1,000,000, so the eighth or ninth page gets the last of it and the pages after it nothing.
        var path = Write("pages.pdf", new VerifierPdf(" ", " ", " ", " ", " ", " ", " ", " ", " ") { MediaBox = "0 0 1500 1000" }.Build());
        PageRect[] areas = [new(60, 60, 580, 400), new(660, 60, 1180, 400), new(60, 500, 580, 840), new(660, 500, 1180, 840)];
        var plan = PlanOf([.. Enumerable.Range(0, 9).SelectMany(page => areas.Select(area => Mark(page, area, "Zeile")))]).WithOverlayStyle(new RedactionOverlayStyle(Repeat: true));

        var applied = await ApplyAsync(path, plan);

        var pages = applied.Impacts;
        Assert.Equal(9, pages.Count);
        for (var i = 0; i < 9; i++)
        {
            var a = applied.Analysis.Pages[i];
            var d = pages[i];
            Assert.Equal((a.OverlaysDrawn, a.OverlaysNotFitting, a.OverlaysOverLimit, a.OverlayPaths, a.OverlaySegments, a.OverlayLimitReached),
                (d.OverlaysDrawn, d.OverlaysNotFitting, d.OverlaysOverLimit, d.OverlayPaths, d.OverlaySegments, d.OverlayLimitReached));
        }

        Assert.True(pages.Sum(static p => p.OverlayPaths) <= RedactionLimits.MaxOverlayPaths);
        Assert.True(pages.Sum(static p => p.OverlaySegments) <= RedactionLimits.MaxOverlaySegments);
        Assert.True(pages.Sum(static p => p.OverlaysDrawn) is > 24 and < 36);
        Assert.Equal(36, pages.Sum(static p => p.OverlaysDrawn + p.OverlaysOverLimit));
        Assert.Equal(pages.Sum(static p => p.OverlaysOverLimit), pages.Sum(static p => p.OverlaysNotFitting));
        Assert.True(pages[^1].OverlayLimitReached);
        Assert.False(pages[0].OverlayLimitReached);

        // Once the limit of the document is reached nothing after it is drawn: the areas that come later are all over the limit.
        var firstOver = pages.Select(static (p, i) => (p, i)).First(static x => x.p.OverlaysOverLimit > 0).i;
        Assert.True(pages[firstOver].OverlayLimitReached);
        for (var i = firstOver + 1; i < 9; i++)
        {
            Assert.Equal(0, pages[i].OverlaysDrawn);
            Assert.Equal(4, pages[i].OverlaysOverLimit);
        }

        // The file holds what was counted (a path for each tile, and the opaque area of each mark), not more.
        await using var written = applied.Result!.Saved.Reopened;
        var paths = 0;
        for (var i = 0; i < 9; i++)
        {
            paths += (await written.GetPageObjectSummaryAsync(i, Ct)).Paths;
        }

        Assert.Equal(36 + pages.Sum(static p => p.OverlayPaths), paths);
        Assert.Contains("because the limit for the overlay text was reached", RedactionReportText.Of(applied.Analysis), StringComparison.Ordinal);
    }

    private static (string Path, RedactionPlan Plan) GridOf(Func<string, string> write)
    {
        var path = write("grid.pdf");
        var marks = new List<RedactionMark>();
        for (var row = 0; marks.Count < 500; row++)
        {
            for (var column = 0; column < 50 && marks.Count < 500; column++)
            {
                marks.Add(Mark(0, new PageRect(column * 60, row * 12, (column * 60) + 60, (row * 12) + 12), "Zeile"));
            }
        }

        return (path, PlanOf([.. marks]).WithOverlayStyle(new RedactionOverlayStyle(Repeat: true)));
    }

    [Fact]
    public async Task FiveHundredAreasWithRepeatStayWithinTheLimits()
    {
        // No time is asserted here (the PDFium thread is shared with other tests; the times are in MeasuresTheOverlay, which runs only
        // with PAGEWING_MEASURE_OUT): the counts are what must hold, and a generous bound stands for "no hang".
        var (path, plan) = GridOf(name => Write(name, new VerifierPdf(" ") { MediaBox = "0 0 3000 2000" }.Build()));
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var clock = Stopwatch.StartNew();
        await using var build = await new RedactionExecutor(engine).BuildAsync(plan, shown, path, null, null, Ct);

        var impact = Assert.Single(build.Impacts);
        Assert.Equal(500, impact.OverlaysDrawn + impact.OverlaysNotFitting);
        // About 163,000 segments are asked for and a page takes 150,000: the last areas stay filled without text, and the report says why.
        Assert.True(impact.OverlaysDrawn is > 400 and < 500, $"{impact.OverlaysDrawn} of 500 areas got their text.");
        Assert.Equal(impact.OverlaysNotFitting, impact.OverlaysOverLimit);
        Assert.InRange(impact.OverlayPaths, 500, RedactionLimits.MaxOverlayPathsPerPage);
        Assert.True(impact.OverlaySegments <= RedactionLimits.MaxOverlaySegmentsPerPage);
        Assert.True(impact.OverlayPaths <= RedactionLimits.MaxOverlayPaths && impact.OverlaySegments <= RedactionLimits.MaxOverlaySegments);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"Building took {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    [Fact]
    public async Task MeasuresTheOverlay()
    {
        var output = Environment.GetEnvironmentVariable(AnnotationMeasurementTests.OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Measurement only: set {AnnotationMeasurementTests.OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;
        var report = new StringBuilder();
        report.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Overlay text measurements, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}"));

        // (1) 500 areas with a repeated text: the build, and the longest single job a probe on the shared PDFium thread saw.
        var (path, plan) = GridOf(name => Write(name, new VerifierPdf(" ") { MediaBox = "0 0 3000 2000" }.Build()));
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        await PdfiumThread.Shared.InvokeAsync(() => TextBoxFont.Table(PdfiumThread.Shared), cancellationToken: Ct);
        using var stop = new CancellationTokenSource();
        var probe = Task.Run(
            async () =>
            {
                long worst = 0;
                while (!stop.IsCancellationRequested)
                {
                    var asked = Stopwatch.GetTimestamp();
                    await PdfiumThread.Shared.InvokeAsync(() => 0, PdfiumPriority.Interactive, CancellationToken.None);
                    worst = Math.Max(worst, Stopwatch.GetTimestamp() - asked);
                    await Task.Delay(3, CancellationToken.None);
                }

                return Stopwatch.GetElapsedTime(0, worst);
            },
            CancellationToken.None);
        var clock = Stopwatch.StartNew();
        await using (var build = await new RedactionExecutor(engine).BuildAsync(plan, shown, path, null, null, Ct))
        {
            clock.Stop();
            await stop.CancelAsync();
            var longest = await probe;
            var impact = Assert.Single(build.Impacts);
            report.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"500 areas, repeated text: build {clock.Elapsed.TotalMilliseconds:0} ms, longest single PDFium job {longest.TotalMilliseconds:0} ms, {impact.OverlayPaths} paths, {impact.OverlaySegments} segments"));
        }

        // (2) The check of a page that became an image with 10,000 overlay paths: reading the summary (object i takes i steps in PDFium).
        var rasterPdf = new VerifierPdf("q 300 0 0 200 0 0 cm /Im1 Do Q 0 0 1 rg " + string.Concat(Enumerable.Repeat("60 110 1 1 re f\n", RedactionLimits.MaxOverlayPathsPerPage)));
        var image = rasterPdf.AddStream("/Type /XObject /Subtype /Image /Width 30 /Height 20 /ColorSpace /DeviceRGB /BitsPerComponent 8", new byte[30 * 20 * 3]);
        rasterPdf.ResourceEntries = $"/XObject << /Im1 {image} 0 R >>";
        var raster = Write("raster-10000.pdf", rasterPdf.Build());
        await using var document = await engine.OpenAsync(raster, cancellationToken: Ct);
        var times = new List<double>();
        for (var run = 0; run < 3; run++)
        {
            var start = Stopwatch.GetTimestamp();
            var summary = await document.GetPageObjectSummaryAsync(0, Ct);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Assert.Equal(RedactionLimits.MaxOverlayPathsPerPage, summary.Paths);
        }

        report.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"summary of a raster page with {RedactionLimits.MaxOverlayPathsPerPage} paths (3 runs): {string.Join(" / ", times.Select(static t => t.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))} ms"));
        await File.WriteAllTextAsync(Path.Combine(output, "redact-overlay-measurements.txt"), report.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
    }

    // ------------------------------------------------------------------ pages that became images

    private string TwoPageFile() =>
        Write("two.pdf", new VerifierPdf(Secret, "BT /F1 12 Tf 20 150 Td (secret value) Tj ET BT /F1 12 Tf 20 100 Td (second page) Tj ET").Build());

    [Fact]
    public async Task OverlayOnRasterPageSitsAboveTheImage()
    {
        var path = TwoPageFile();
        var plan = PlanOf(Mark(0, SecretArea, "GONE"), Mark(0, new PageRect(200, 10, 206, 14), "Does not fit here")).WithRasterPage(0, on: true);

        var applied = await ApplyAsync(path, plan);

        var info = Assert.Single(applied.Analysis.Raster!);
        Assert.Equal(1, info.OverlaysDrawn);
        Assert.Equal(1, info.OverlaysNotFitting);
        Assert.Equal(0, info.OverlaysOverLimit);
        Assert.True(info.OverlayPaths > 0);
        Assert.Equal(1, applied.Analysis.OverlaysNotFitting);

        await using var written = applied.Result!.Saved.Reopened;
        var summary = await written.GetPageObjectSummaryAsync(0, Ct);

        // One image, no text, no annotation; the paths are the text, all inside the first area.
        Assert.Equal(1, summary.Images);
        Assert.Equal(0, summary.Texts);
        Assert.Equal(0, summary.Others);
        Assert.Equal(0, summary.Annotations);
        Assert.Equal(info.OverlayPaths, summary.Paths);
        Assert.All(summary.PathBounds!, box => Assert.True(Inside(box, SecretArea, -0.5), $"{box} is not inside the area."));

        // Above the image: the render shows white text on the black area of the image.
        var image = await RenderAsync(written, 0, 2);
        var light = LightPixels(image, SecretArea, 2);
        Assert.InRange(light, 30, (int)(2 * 2 * 95 * 18) / 2);
        Assert.Equal(0, (await TextOf(written, 0)).Length);
        Assert.Contains("second page", await TextOf(written, 1), StringComparison.Ordinal);

        // The same plan without the overlay writes no path at all on the page.
        var plain = await ApplyAsync(path, PlanOf(Mark(0, SecretArea, null)).WithRasterPage(0, on: true));
        await using var plainWritten = plain.Result!.Saved.Reopened;
        Assert.Equal(0, (await plainWritten.GetPageObjectSummaryAsync(0, Ct)).Paths);
    }

    private static async Task<string> TextOf(PdfDocument document, int page) =>
        string.Concat((await document.GetPageTextAsync(page, Ct)).Chars.Select(static c => c.Text)).Trim();

    [Fact]
    public async Task RasterOverlayIsDecidedLikeAnyOtherAndTheLimitHoldsForPagesThatBecomeImages()
    {
        // A page redacted in place and one that becomes an image, with the same four large areas: both take from one budget (the page
        // redacted in place first), both stop at the limit of a page.
        var path = Write("two-pages.pdf", new VerifierPdf(" ", " ") { MediaBox = "0 0 1500 1000" }.Build());
        PageRect[] areas = [new(30, 30, 730, 480), new(770, 30, 1470, 480), new(30, 520, 730, 970), new(770, 520, 1470, 970)];
        var marks = Enumerable.Range(0, 2).SelectMany(page => areas.Select(area => Mark(page, area, "Zeile"))).ToArray();
        var plan = PlanOf(marks).WithOverlayStyle(new RedactionOverlayStyle(Repeat: true)).WithRasterPage(1, on: true).WithRasterDpi(72);

        var applied = await ApplyAsync(path, plan);

        var inPlace = Assert.Single(applied.Analysis.Pages);
        var raster = Assert.Single(applied.Analysis.Raster!);
        Assert.Equal(0, inPlace.Page);
        Assert.Equal(1, raster.Page);
        Assert.Equal((inPlace.OverlaysDrawn, inPlace.OverlaysOverLimit, inPlace.OverlayPaths, inPlace.OverlaySegments),
            (applied.Impacts[0].OverlaysDrawn, applied.Impacts[0].OverlaysOverLimit, applied.Impacts[0].OverlayPaths, applied.Impacts[0].OverlaySegments));
        Assert.Equal((2, 2), (inPlace.OverlaysDrawn, inPlace.OverlaysOverLimit));
        Assert.Equal((2, 2), (raster.OverlaysDrawn, raster.OverlaysOverLimit));
        Assert.Equal(4, applied.Analysis.OverlaysNotFitting);
        Assert.Equal(4, applied.Analysis.OverlaysOverLimit);
        Assert.True(inPlace.OverlayPaths + raster.OverlayPaths <= RedactionLimits.MaxOverlayPaths);
        Assert.True(inPlace.OverlaySegments + raster.OverlaySegments <= RedactionLimits.MaxOverlaySegments);
        Assert.True(raster.OverlaySegments <= RedactionLimits.MaxOverlaySegmentsPerPage);

        // What the analysis counted for the page that became an image is what is written there.
        await using var written = applied.Result!.Saved.Reopened;
        var summary = await written.GetPageObjectSummaryAsync(1, Ct);
        Assert.Equal(raster.OverlayPaths, summary.Paths);
        Assert.Equal(1, summary.Images);
        Assert.All(summary.PathBounds!, box => Assert.True(Inside(box, areas[0]) || Inside(box, areas[1])));
    }

    // ------------------------------------------------------------------ the pieces that make the budget

    [Fact]
    public void TheBudgetOfTheOverlayIsPinned()
    {
        Assert.Equal(20_000, RedactionLimits.MaxOverlayPaths);
        Assert.Equal(1_000_000, RedactionLimits.MaxOverlaySegments);
        Assert.Equal(10_000, RedactionLimits.MaxOverlayPathsPerPage);
        Assert.Equal(150_000, RedactionLimits.MaxOverlaySegmentsPerPage);
        Assert.True(RedactionLimits.MaxOverlayPathsPerPage <= RedactionLimits.MaxOverlayPaths);
        Assert.True(RedactionLimits.MaxOverlaySegmentsPerPage <= RedactionLimits.MaxOverlaySegments);

        // The check of a page that became an image reads the bounds of at least as many paths as the overlay of a document may draw, and the
        // objects of a page stay well under what the walks over a page accept.
        Assert.True(RedactionLimits.MaxOverlayPathsPerPage <= PdfiumRedactionRaster.MaxPathBounds);
        Assert.True(RedactionLimits.MaxOverlayPaths < PdfiumRedaction.MaxObjects);
    }
}
