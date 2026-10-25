using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>
/// Measurements of Milestone 10 (<c>Docs/DECISIONS.md</c>, "Zeichnen: Messwerte (M10)"): the job of a drawing on release, the
/// thinning and smoothing of a stroke, the growth of the file, the save with 1,000 strokes, the render of a page with 1,000
/// strokes (light and dark) and the files for the scroll measurement. Runs only with <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c>
/// (best in Release: <c>dotnet test tests/Pagewing.Core.Tests -c Release --filter-class "*AnnotationMeasurementTests"</c>);
/// writes <c>drawing-measurements.txt</c> and the scroll files into that folder. Measures, never asserts a time. The cost of the
/// preview while dragging is measured in <c>Pagewing.App.Tests</c> (<c>DrawingPreviewMeasurementTests</c>).
/// </summary>
public sealed partial class AnnotationMeasurementTests
{
    private const int DrawingRuns = 20;

    private static readonly AnnotationColor Red = DrawPalette.ColorOf(DrawColor.Red);

    [Fact]
    public async Task MeasuresDrawings()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Measurement only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Pagewing drawing measurements, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"OS {Environment.OSVersion}, {Environment.ProcessorCount} logical processors, {(IsRelease() ? "Release" : "Debug")} build of the tests");
        report.AppendLine();

        var engine = new PdfEngine();
        using var work = new TempFolder();

        report.AppendLine("(a) The job on release: AnnotationEditor.ApplyDrawingAsync (PDFium creates the annotation, the generator writes its appearance, the page is read back);");
        report.AppendLine(CultureInfo.InvariantCulture, $"    {DrawingRuns} runs after one warm-up, each undone (not timed) before the next; median / min / max in ms; the shape is made before the clock starts");
        foreach (var fixture in new[] { "text-basic.pdf", "text-dense.pdf" })
        {
            report.AppendLine(await MeasureReleaseJobAsync(engine, work, fixture));
        }

        report.AppendLine();
        report.AppendLine("(b) StrokeSimplifier on the UI thread on release: Simplify (tolerance 0.5 pt) then Smooth, as DocumentView.GeometryOf does; ms, median of 20 (5 for 20,000 points) / max");
        report.AppendLine(MeasureSimplifier());

        report.AppendLine();
        report.AppendLine("(c) Growth of the file per 100 freehand strokes (text-basic.pdf, one page of 300 x 200 pt, incremental save of a fresh document, bytes added to the original)");
        report.AppendLine(await MeasureFileGrowthAsync(work));

        report.AppendLine();
        report.AppendLine("(d) Save (AnnotationEditor.SaveAsync over DocumentSaver) with 1,000 strokes of 60 raw points each (thinned and smoothed as on release, 2 pt)");
        report.AppendLine(await MeasureThousandStrokesAsync(engine, work, output));

        report.AppendLine();
        report.AppendLine("(e) Render of a page with 1,000 strokes (RenderPageAsync, interactive, whole page): light and dark, median of 10 after one warm-up, ms");
        report.AppendLine(await MeasureRenderAsync(engine, work));

        await File.WriteAllTextAsync(Path.Combine(output, "drawing-measurements.txt"), report.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
    }

    /// <summary>A hand-like stroke of <paramref name="count"/> points inside a box: two sines of random phase and a little jitter.</summary>
    private static List<DisplayPoint> HandStroke(Random random, int count, double left, double top, double width, double height)
    {
        var phase1 = random.NextDouble() * Math.Tau;
        var phase2 = random.NextDouble() * Math.Tau;
        var frequency1 = 1 + (random.NextDouble() * 3);
        var frequency2 = 2 + (random.NextDouble() * 5);
        var points = new List<DisplayPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var t = i / (count - 1.0);
            var wave = (0.6 * Math.Sin((frequency1 * Math.Tau * t) + phase1)) + (0.4 * Math.Sin((frequency2 * Math.Tau * t) + phase2));
            points.Add(new DisplayPoint(left + (width * t), top + (height / 2) + (height / 2 * wave) + ((random.NextDouble() - 0.5) * 0.4)));
        }

        return points;
    }

    private static DrawingGeometry InkOf(IReadOnlyList<DisplayPoint> raw, bool simplify, bool smooth, double width = 2)
    {
        var points = simplify ? StrokeSimplifier.Simplify(raw, 0.5) : raw;
        return DrawingGeometry.Ink([smooth ? StrokeSimplifier.Smooth(points) : points], width);
    }

    private static async Task<string> MeasureReleaseJobAsync(PdfEngine engine, TempFolder work, string fixture)
    {
        using var session = await OpenAsync(engine, Copy(work, fixture, "release-" + fixture));
        var document = session.Document!;
        var size = await document.GetPageSizeAsync(0, Ct);
        var editor = new AnnotationEditor(session);
        var random = new Random(1);
        var raw300 = HandStroke(random, 300, 20, size.Height / 3, Math.Min(size.Width - 40, 400), 80);
        var smoothed = InkOf(raw300, simplify: true, smooth: true);
        var unthinned = InkOf(raw300, simplify: false, smooth: false);
        var box = new PageRect(40, 40, 200, 120);
        var shapes = new (string Label, DrawingGeometry Geometry)[]
        {
            ($"freehand, 300 raw points -> {smoothed.Strokes[0].Count} written", smoothed),
            ("freehand, 300 raw points as they are", unthinned),
            ("rectangle", DrawingGeometry.Rectangle(box, 2)),
            ("arrow", DrawingGeometry.Arrow(new DisplayPoint(40, 40), new DisplayPoint(220, 130), 2)),
        };

        var lines = new StringBuilder();
        foreach (var (label, geometry) in shapes)
        {
            var times = new List<double>();
            for (var run = 0; run <= DrawingRuns; run++)
            {
                var started = Stopwatch.StartNew();
                Assert.NotNull(await editor.ApplyDrawingAsync(0, geometry, Red, null, "Measure"));
                var elapsed = started.Elapsed.TotalMilliseconds;
                if (run > 0)
                {
                    times.Add(elapsed);
                }

                await editor.UndoAsync();
            }

            lines.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    {fixture,-15} {label,-44} {Stats(times)}"));
        }

        return lines.ToString().TrimEnd();
    }

    private static string MeasureSimplifier()
    {
        var random = new Random(2);
        var lines = new StringBuilder();
        foreach (var count in new[] { 300, 5_000, 20_000 })
        {
            var runs = count >= 20_000 ? 5 : DrawingRuns;
            foreach (var (label, make) in StrokeShapes())
            {
                var raw = make(random, count);
                var simplify = new List<double>();
                var smooth = new List<double>();
                var total = new List<double>();
                IReadOnlyList<DisplayPoint> thinned = raw;
                IReadOnlyList<DisplayPoint> written = raw;
                for (var run = -1; run < runs; run++)
                {
                    var started = Stopwatch.GetTimestamp();
                    thinned = StrokeSimplifier.Simplify(raw, 0.5);
                    var afterSimplify = Stopwatch.GetTimestamp();
                    written = StrokeSimplifier.Smooth(thinned);
                    var afterSmooth = Stopwatch.GetTimestamp();
                    if (run >= 0)
                    {
                        simplify.Add(Stopwatch.GetElapsedTime(started, afterSimplify).TotalMilliseconds);
                        smooth.Add(Stopwatch.GetElapsedTime(afterSimplify, afterSmooth).TotalMilliseconds);
                        total.Add(Stopwatch.GetElapsedTime(started, afterSmooth).TotalMilliseconds);
                    }
                }

                lines.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    {count,6} points {label,-24} -> kept {thinned.Count,6}, written {written.Count,6} | Simplify {Median(simplify),8:0.00} / {simplify.Max(),8:0.00} | Smooth {Median(smooth),7:0.00} / {smooth.Max(),7:0.00} | together {Median(total),8:0.00} / {total.Max(),8:0.00}"));
            }
        }

        return lines.ToString().TrimEnd();
    }

    /// <summary>The shapes of a stroke measured: what a hand makes, and what could make the thinning slow.</summary>
    private static (string Label, Func<Random, int, List<DisplayPoint>> Make)[] StrokeShapes() =>
    [
        ("hand-like wave", (random, count) => HandStroke(random, count, 20, 20, 500, 300)),
        ("jittery, 0.4 pt steps", (random, count) =>
        {
            var points = new List<DisplayPoint>(count);
            for (var i = 0; i < count; i++)
            {
                points.Add(new DisplayPoint(20 + (i * 0.4), 300 + ((random.NextDouble() - 0.5) * 3)));
            }

            return points;
        }),
        ("zig-zag +-3 pt", (_, count) =>
        {
            var points = new List<DisplayPoint>(count);
            for (var i = 0; i < count; i++)
            {
                points.Add(new DisplayPoint(20 + (i * 0.3), 300 + (i % 2 == 0 ? 3 : -3)));
            }

            return points;
        }),
        ("spiral", (_, count) =>
        {
            var points = new List<DisplayPoint>(count);
            for (var i = 0; i < count; i++)
            {
                var angle = i * 0.05;
                var radius = 2 + (angle * 0.5);
                points.Add(new DisplayPoint(300 + (radius * Math.Cos(angle)), 400 + (radius * Math.Sin(angle))));
            }

            return points;
        }),
        ("growing zig-zag (worst)", (_, count) =>
        {
            // The amplitude grows with every point, so the farthest point from the chord is always the one next to the end:
            // Douglas-Peucker peels one point per round (quadratic).
            var points = new List<DisplayPoint>(count);
            for (var i = 0; i < count; i++)
            {
                points.Add(new DisplayPoint(20 + (i * 0.03), 300 + ((i % 2 == 0 ? 1 : -1) * (1 + (i * 0.02)))));
            }

            return points;
        }),
    ];

    private static async Task<string> MeasureFileGrowthAsync(TempFolder work)
    {
        var engine = new PdfEngine();
        var originalLength = new FileInfo(TestData.Pdf("text-basic.pdf")).Length;
        var lines = new StringBuilder();
        foreach (var rawPoints in new[] { 120, 600 })
        {
            foreach (var (label, simplify, smooth) in new[] { ("raw points as they are", false, false), ("Simplify 0.5", true, false), ("Simplify 0.5 + Smooth (as released)", true, true) })
            {
                var path = Copy(work, "text-basic.pdf", $"growth-{rawPoints}-{(simplify ? "s" : "r")}{(smooth ? "m" : "n")}.pdf");
                await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
                var random = new Random(3);
                var written = 0;
                for (var i = 0; i < 100; i++)
                {
                    var geometry = InkOf(HandStroke(random, rawPoints, 10 + (random.NextDouble() * 150), 10 + (random.NextDouble() * 100), 120, 60), simplify, smooth);
                    written += geometry.Strokes[0].Count;
                    await document.CreateDrawingAsync(0, geometry, Red, null, null, Ct);
                }

                var saved = await AnnotationTestFiles.SaveAsync(document, work, $"growth-saved-{rawPoints}-{(simplify ? "s" : "r")}{(smooth ? "m" : "n")}.pdf");
                var added = new FileInfo(saved).Length - originalLength;
                lines.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    {rawPoints,4} raw points per stroke, {label,-36}: {written / 100.0,6:0.0} points written per stroke, {added,8} bytes added = {added / 100.0,7:0.0} bytes per stroke ({added / 1024.0,7:0.0} KiB per 100)"));
            }
        }

        return lines.ToString().TrimEnd();
    }

    private static async Task<string> MeasureThousandStrokesAsync(PdfEngine engine, TempFolder work, string output)
    {
        var lines = new StringBuilder();
        foreach (var (fixture, spread, scrollName) in new[]
        {
            ("text-basic.pdf", false, (string?)null),
            ("text-dense.pdf", true, "scroll-text-dense-1000-strokes.pdf"),
            ("text-dense.pdf", false, "scroll-text-dense-1000-strokes-page1.pdf"),
        })
        {
            var path = Copy(work, fixture, $"thousand-{(spread ? "spread" : "one-page")}-{fixture}");
            var originalLength = new FileInfo(path).Length;
            using var session = await OpenAsync(engine, path);
            var document = session.Document!;
            var editor = new AnnotationEditor(session);
            var random = new Random(4);
            var created = new List<double>();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 1_000; i++)
            {
                var page = spread ? i % document.PageCount : 0;
                var size = await document.GetPageSizeAsync(page, Ct);
                var raw = HandStroke(random, 60, random.NextDouble() * (size.Width - 90), random.NextDouble() * (size.Height - 50), 80, 40);
                var started = Stopwatch.StartNew();
                Assert.NotNull(await editor.ApplyDrawingAsync(page, InkOf(raw, simplify: true, smooth: true), DrawPalette.ColorOf(DrawPalette.All[i % DrawPalette.All.Count]), null, "Measure"));
                created.Add(started.Elapsed.TotalMilliseconds);
            }

            var creating = watch.Elapsed;
            var read = new List<double>();
            for (var run = 0; run < 5; run++)
            {
                var started = Stopwatch.StartNew();
                var listed = await document.GetAnnotationsAsync(0, Ct);
                read.Add(started.Elapsed.TotalMilliseconds);
                Assert.True(listed.Count > 0);
            }

            var label = $"{fixture}, {(spread ? "spread over the pages" : "all on page 1")}";
            lines.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    {label}: creating 1,000 took {creating.TotalSeconds:0.00} s, per stroke median {Median(created):0.0} ms (first 100: {Median(created.Take(100).ToList()):0.0}, last 100: {Median(created.Skip(900).ToList()):0.0}, max {created.Max():0.0}); GetAnnotationsAsync(page 1) median {Median(read):0.0} ms for {(spread ? 5 : 1_000)} stroke(s) on it"));
            lines.AppendLine("      " + await SaveTwiceAsync(engine, editor, path, originalLength));
            if (scrollName is not null)
            {
                File.Copy(path, Path.Combine(output, scrollName), overwrite: true);
            }
        }

        var plain = Path.Combine(output, "scroll-text-dense.pdf");
        File.Copy(TestData.Pdf("text-dense.pdf"), plain, overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(output, "scroll-dark.json"), "{\n  \"Theme\": \"Dark\",\n  \"PageAppearance\": \"FollowTheme\"\n}\n", Ct);
        lines.AppendLine("      files for --measure-scroll next to this report: scroll-text-dense.pdf, scroll-text-dense-1000-strokes.pdf, scroll-text-dense-1000-strokes-page1.pdf, scroll-dark.json");
        return lines.ToString().TrimEnd();
    }

    /// <summary>The save of the 1,000 strokes, then the save of one more stroke into that file (what a user sees on the next Ctrl+S).</summary>
    private static async Task<string> SaveTwiceAsync(PdfEngine engine, AnnotationEditor editor, string path, long originalLength)
    {
        var first = await SaveOnceAsync(engine, editor, path);
        var length = new FileInfo(path).Length;
        var raw = HandStroke(new Random(5), 60, 20, 20, 80, 40);
        Assert.NotNull(await editor.ApplyDrawingAsync(0, InkOf(raw, simplify: true, smooth: true), Red, null, "Measure"));
        var second = await SaveOnceAsync(engine, editor, path);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"save of 1,000 strokes: {first} | file {originalLength / 1024.0:0.0} KiB -> {length / 1024.0:0.0} KiB; then one more stroke: {second} | file {new FileInfo(path).Length / 1024.0:0.0} KiB");
    }

    private static async Task<string> SaveOnceAsync(PdfEngine engine, AnnotationEditor editor, string path)
    {
        // Wait until the PDFium thread is idle, so the clock does not include the creating before it.
        await editor.WhenIdleAsync();
        var watch = Stopwatch.StartNew();
        var progress = new Phases(watch);
        var result = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress, Ct);
        var total = watch.Elapsed.TotalMilliseconds;
        Assert.True(result.Adopted);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"total {total:0.0} ms (before the write {progress.Start.TotalMilliseconds:0.0}, write and flush {(progress.End - progress.Start).TotalMilliseconds:0.0}, after {total - progress.End.TotalMilliseconds:0.0})");
    }

    private static async Task<string> MeasureRenderAsync(PdfEngine engine, TempFolder work)
    {
        var lines = new StringBuilder();
        foreach (var fixture in new[] { "text-basic.pdf", "text-dense.pdf" })
        {
            using var session = await OpenAsync(engine, Copy(work, fixture, "render-" + fixture));
            var document = session.Document!;
            var editor = new AnnotationEditor(session);
            var size = await document.GetPageSizeAsync(0, Ct);
            foreach (var scale in new[] { 1.0, 2.0 })
            {
                var light = await RenderMedianAsync(document, scale, PageTheme.Light);
                var dark = await RenderMedianAsync(document, scale, PageTheme.Dark);
                lines.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    {fixture,-15} {size.Width:0} x {size.Height:0} pt, scale {scale:0.0}, no strokes:    light {light:0.0} | dark {dark:0.0}"));
            }

            var random = new Random(6);
            for (var i = 0; i < 1_000; i++)
            {
                var raw = HandStroke(random, 60, random.NextDouble() * (size.Width - 90), random.NextDouble() * (size.Height - 50), 80, 40);
                Assert.NotNull(await editor.ApplyDrawingAsync(0, InkOf(raw, simplify: true, smooth: true), Red, null, null));
            }

            foreach (var scale in new[] { 1.0, 2.0 })
            {
                var light = await RenderMedianAsync(document, scale, PageTheme.Light);
                var dark = await RenderMedianAsync(document, scale, PageTheme.Dark);
                lines.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    {fixture,-15} {size.Width:0} x {size.Height:0} pt, scale {scale:0.0}, 1,000 strokes: light {light:0.0} | dark {dark:0.0}"));
            }
        }

        return lines.ToString().TrimEnd();
    }

    private static async Task<double> RenderMedianAsync(PdfDocument document, double scale, PageTheme theme)
    {
        var options = new RenderOptions { PageTheme = theme };
        _ = await document.RenderPageAsync(0, scale, options, Ct);
        var times = new List<double>();
        for (var run = 0; run < 10; run++)
        {
            var started = Stopwatch.StartNew();
            _ = await document.RenderPageAsync(0, scale, options, Ct);
            times.Add(started.Elapsed.TotalMilliseconds);
        }

        return Median(times);
    }
}
