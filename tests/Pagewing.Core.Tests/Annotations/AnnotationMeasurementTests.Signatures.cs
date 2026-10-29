using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Signatures;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>
/// Measurements of Milestone 12 (<c>Docs/DECISIONS.md</c>, "Unterschrift: Messwerte (M12)"): the job of a signature on placing, the
/// growth of the file for a drawn and an imported one (the image once and five times), the save and the render of a page with ten
/// signatures (light and dark) and the files for the scroll measurement. Runs only with <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c>
/// (best in Release: <c>dotnet test tests/Pagewing.Core.Tests -c Release --filter-class "*AnnotationMeasurementTests"</c>); writes
/// <c>signature-measurements.txt</c> and the scroll files into that folder. Measures, never asserts a time. The time from the click
/// to the visible signature is the job here plus one tile render (see the render lines); the interaction itself is not measured.
/// </summary>
public sealed partial class AnnotationMeasurementTests
{
    private const int SignatureRuns = 20;

    private const int SignaturesOnAPage = 10;

    [Fact]
    public async Task MeasuresSignatures()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Measurement only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Pagewing signature measurements, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"OS {Environment.OSVersion}, {Environment.ProcessorCount} logical processors, {(IsRelease() ? "Release" : "Debug")} build of the tests");
        report.AppendLine();

        var engine = new PdfEngine();
        using var work = new TempFolder();

        report.AppendLine("(a) The job on placing: AnnotationEditor.ApplyDrawingAsync (drawn) and ApplySignatureImageAsync (image, packed for the history);");
        report.AppendLine(CultureInfo.InvariantCulture, $"    {SignatureRuns} runs after one warm-up, each undone (not timed) before the next; median / min / max in ms");
        foreach (var fixture in new[] { "text-basic.pdf", "text-dense.pdf" })
        {
            report.AppendLine(await MeasureSignatureJobAsync(engine, work, fixture));
        }

        report.AppendLine();
        report.AppendLine("(b) Growth of the file per signature (text-basic.pdf, incremental save of a fresh document, bytes added to the original)");
        report.AppendLine(await MeasureSignatureGrowthAsync(engine, work));

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"(c) Save (AnnotationEditor.SaveAsync) and render of a page with {SignaturesOnAPage} signatures (5 drawn + 5 images of 600 x 240 px): the render light and dark, median of 10 after one warm-up, ms");
        report.AppendLine(await MeasureSignaturePageAsync(engine, work, output));

        await File.WriteAllTextAsync(Path.Combine(output, "signature-measurements.txt"), report.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
    }

    /// <summary>600 x 240 px of dark ink with a soft edge on a transparent background: what a photographed signature gives after the cut-out.</summary>
    private static SignatureImage MeasuredImage(int width = 600, int height = 240)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var curve = (height * 0.55) - (height * 0.3 * Math.Sin(x / (double)width * Math.PI * 4)) + (height * 0.1 * Math.Sin(x / 17.0));
                var alpha = (byte)Math.Clamp(255 * (7 - Math.Abs(y - curve)) / 3, 0, 255);
                var o = ((y * width) + x) * 4;
                bgra[o] = alpha == 0 ? (byte)255 : (byte)140;
                bgra[o + 1] = alpha == 0 ? (byte)255 : (byte)40;
                bgra[o + 2] = alpha == 0 ? (byte)255 : (byte)20;
                bgra[o + 3] = alpha;
            }
        }

        return new SignatureImage(width, height, bgra);
    }

    private static DrawingGeometry MeasuredDrawn(Random random, double left, double top) =>
        DrawingGeometry.Signature([StrokeSimplifier.Smooth(StrokeSimplifier.Simplify(HandStroke(random, 300, left, top, 160, 60), 0.5)), StrokeSimplifier.Smooth(StrokeSimplifier.Simplify(HandStroke(random, 120, left + 20, top + 10, 120, 30), 0.5))], 2);

    private static async Task<string> MeasureSignatureJobAsync(PdfEngine engine, TempFolder work, string fixture)
    {
        using var session = await OpenAsync(engine, Copy(work, fixture, "signature-job-" + fixture));
        var editor = new AnnotationEditor(session);
        var drawn = MeasuredDrawn(new Random(7), 40, 300);
        var image = MeasuredImage();
        var box = new PageRect(40, 100, 220, 172);
        var shapes = new (string Label, Func<Task<PdfAnnotation?>> Place)[]
        {
            ("drawn, 2 strokes", () => editor.ApplyDrawingAsync(0, drawn, new AnnotationColor(20, 40, 140), null, "Measure")),
            ("image 600 x 240 px", () => editor.ApplySignatureImageAsync(0, box, image, "Measure")),
        };

        var lines = new StringBuilder();
        foreach (var (label, place) in shapes)
        {
            var times = new List<double>();
            for (var run = 0; run <= SignatureRuns; run++)
            {
                var started = Stopwatch.StartNew();
                Assert.NotNull(await place());
                var elapsed = started.Elapsed.TotalMilliseconds;
                if (run > 0)
                {
                    times.Add(elapsed);
                }

                await editor.UndoAsync();
            }

            lines.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    {fixture,-15} {label,-20} {Stats(times)}"));
        }

        return lines.ToString().TrimEnd();
    }

    private static async Task<string> MeasureSignatureGrowthAsync(PdfEngine engine, TempFolder work)
    {
        var originalLength = new FileInfo(TestData.Pdf("text-basic.pdf")).Length;
        var lines = new StringBuilder();
        foreach (var (label, count, make) in new (string, int, Func<AnnotationEditor, int, Task<PdfAnnotation?>>)[]
        {
            ("drawn signature", 1, (editor, i) => editor.ApplyDrawingAsync(0, MeasuredDrawn(new Random(8 + i), 40, 300), new AnnotationColor(20, 40, 140), null, null)),
            ("image signature (600 x 240 px)", 1, (editor, _) => editor.ApplySignatureImageAsync(0, new PageRect(40, 100, 220, 172), MeasuredImage(), null)),
            ("image signature (600 x 240 px)", 5, (editor, i) => editor.ApplySignatureImageAsync(0, new PageRect(40, 40 + (i * 90), 220, 112 + (i * 90)), MeasuredImage(), null)),
        })
        {
            var path = Copy(work, "text-basic.pdf", $"growth-signature-{count}-{label.Length}.pdf");
            using var session = await OpenAsync(engine, path);
            var editor = new AnnotationEditor(session);
            for (var i = 0; i < count; i++)
            {
                Assert.NotNull(await make(editor, i));
            }

            await editor.WhenIdleAsync();
            Assert.True((await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct)).Adopted);
            var added = new FileInfo(path).Length - originalLength;
            lines.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    {count} x {label,-32}: {added,8} bytes added = {added / 1024.0,7:0.0} KiB ({added / (double)count / 1024.0,7:0.0} KiB each)"));
        }

        return lines.ToString().TrimEnd();
    }

    private static async Task<string> MeasureSignaturePageAsync(PdfEngine engine, TempFolder work, string output)
    {
        var lines = new StringBuilder();
        foreach (var (fixture, scrollName) in new[] { ("text-basic.pdf", (string?)null), ("text-dense.pdf", "scroll-text-dense-10-signatures.pdf") })
        {
            var path = Copy(work, fixture, "signature-page-" + fixture);
            var originalLength = new FileInfo(path).Length;
            using var session = await OpenAsync(engine, path);
            var document = session.Document!;
            var editor = new AnnotationEditor(session);
            var size = await document.GetPageSizeAsync(0, Ct);
            var light0 = await RenderMedianAsync(document, 1, PageTheme.Light);
            var dark0 = await RenderMedianAsync(document, 1, PageTheme.Dark);
            var random = new Random(9);
            for (var i = 0; i < SignaturesOnAPage; i++)
            {
                var page = fixture == "text-dense.pdf" ? i % document.PageCount : 0;
                var top = 20 + ((i % 5) * ((size.Height - 120) / 5));
                if (i % 2 == 0)
                {
                    Assert.NotNull(await editor.ApplyDrawingAsync(page, MeasuredDrawn(random, 30, top), new AnnotationColor(20, 40, 140), null, null));
                }
                else
                {
                    Assert.NotNull(await editor.ApplySignatureImageAsync(page, new PageRect(size.Width - 230, top, size.Width - 30, top + 80), MeasuredImage(), null));
                }
            }

            await editor.WhenIdleAsync();
            var light = await RenderMedianAsync(document, 1, PageTheme.Light);
            var dark = await RenderMedianAsync(document, 1, PageTheme.Dark);
            lines.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    {fixture,-15} {size.Width:0} x {size.Height:0} pt, scale 1.0: without signatures light {light0:0.0} | dark {dark0:0.0}; with {SignaturesOnAPage}: light {light:0.0} | dark {dark:0.0}"));
            var save = await SaveOnceAsync(engine, editor, path);
            lines.AppendLine(string.Create(CultureInfo.InvariantCulture, $"      save: {save} | file {originalLength / 1024.0:0.0} KiB -> {new FileInfo(path).Length / 1024.0:0.0} KiB"));
            if (scrollName is not null)
            {
                File.Copy(path, Path.Combine(output, scrollName), overwrite: true);
            }
        }

        lines.AppendLine("      file for --measure-scroll next to this report: scroll-text-dense-10-signatures.pdf (with scroll-text-dense.pdf and scroll-dark.json of the drawing measurement)");
        return lines.ToString().TrimEnd();
    }
}
