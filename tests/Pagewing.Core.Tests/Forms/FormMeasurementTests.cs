using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// Measurements of Milestone 11 (<c>Docs/DECISIONS.md</c>, "Formulare: Messwerte und Startpfad (M11)"): what the form
/// environment costs in the open job, the first sharp page of a document with a form against the same document without the
/// form environment, <c>FPDF_FFLDraw</c> per page and tile, the time from a written field value to the new sharp image of its
/// page, the save of a filled form, and the render time of 50 consecutive pages with and without widgets. Runs only with
/// <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c> (best in Release:
/// <c>dotnet test tests/Pagewing.Core.Tests -c Release --filter-class "*FormMeasurementTests"</c>); writes
/// <c>form-measurements.txt</c> into that folder, all other work happens in a temp folder. Measures, never asserts a time.
/// </summary>
/// <remarks>
/// "Without the form environment" is the same file with the key <c>/AcroForm</c> renamed to a key PDFium ignores (same length,
/// so the cross-reference table stays valid): the widgets are still annotations, but the catalog has no form, so PDFium
/// reports no form type and Pagewing makes no environment.
/// </remarks>
public sealed class FormMeasurementTests
{
    private const int Runs = 15;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MeasuresTheFormPaths()
    {
        var output = Environment.GetEnvironmentVariable(AnnotationMeasurementTests.OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Measurement only: set {AnnotationMeasurementTests.OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Pagewing form measurements, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"OS {Environment.OSVersion}, {Environment.ProcessorCount} logical processors, {(IsRelease() ? "Release" : "Debug")} build of the tests");
        report.AppendLine("median / min / max in ms over the runs given; \"form\" = with the form environment, \"plain\" = the same file without it (/AcroForm renamed)");
        report.AppendLine();

        var engine = new PdfEngine();
        using var work = new TempFolder();
        var cases = new (string Label, int Pages, int FieldsPerPage)[]
        {
            ("1 field", 1, 1),
            ("100 fields", 1, 100),
            ("2000 fields, 20 pages x 100", 20, 100),
            ("2000 fields, 1 page", 1, 2000),
        };
        var files = new List<(string Label, string Form, string Plain, int Pages)>();
        foreach (var (label, pages, perPage) in cases)
        {
            var name = $"form-{pages}x{perPage}.pdf";
            var form = FormTestPdfs.WriteLargeForm(work, name, pages, perPage);
            files.Add((label, form, WithoutForm(form), pages));
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"(1) Open job: PdfEngine.OpenAsync (the open job on the PDFium thread, includes FPDF_GetFormType and FPDFDOC_InitFormFillEnvironment for \"form\"); {Runs} runs after one warm-up");
        foreach (var (label, form, plain, _) in files)
        {
            var withForm = await TimeOpenAsync(engine, form);
            var without = await TimeOpenAsync(engine, plain);
            report.AppendLine(CultureInfo.InvariantCulture, $"    {label,-30} form {Stats(withForm)} | plain {Stats(without)} | median difference {Median(withForm) - Median(without),7:0.00}");
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"(2) First sharp page: open, then RenderPageAsync(page 0, scale 1.0, light) as the first job; the render of a fresh document includes the first FORM_OnAfterLoadPage (loads ALL fields of the document) and FPDF_FFLDraw. Warm = the same render again. {Runs} runs");
        foreach (var (label, form, plain, _) in files)
        {
            var (firstForm, warmForm) = await TimeFirstRenderAsync(engine, form);
            var (firstPlain, warmPlain) = await TimeFirstRenderAsync(engine, plain);
            report.AppendLine(CultureInfo.InvariantCulture, $"    {label,-30} first: form {Stats(firstForm)} | plain {Stats(firstPlain)} | median difference {Median(firstForm) - Median(firstPlain),7:0.00}");
            report.AppendLine(CultureInfo.InvariantCulture, $"    {string.Empty,-30} warm : form {Stats(warmForm)} | plain {Stats(warmPlain)} | median difference {Median(warmForm) - Median(warmPlain),7:0.00}  (= FPDF_FFLDraw and its widget lookup on a warm page)");
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"(3) FPDF_FFLDraw per tile: warm RenderTileAsync (256 x 256 px at scale 1.0, x = 300 for the 64 px overlap) of page 0; {Runs * 2} runs; and FFLDraw per page from (2)");
        foreach (var (label, form, plain, _) in files)
        {
            var withForm = await TimeTileAsync(engine, form);
            var without = await TimeTileAsync(engine, plain);
            report.AppendLine(CultureInfo.InvariantCulture, $"    {label,-30} tile: form {Stats(withForm)} | plain {Stats(without)} | median difference {Median(withForm) - Median(without),7:0.00}");
        }

        report.AppendLine();
        report.AppendLine("(4) Written value to the new sharp image: AnnotationEditor.SetFormValueAsync start -> PagesInvalidated -> all sharp tiles of the page in the cache;");
        report.AppendLine("    viewport 1280 x 800 DIP at the top of the document, scaling 1, plan of ViewportPlanner as the view makes it; each run undone (and rendered again) before the next");
        report.AppendLine(await MeasureWriteToSharpAsync(engine, "forms-basic.pdf, field Name", TestData.Pdf("forms-basic.pdf"), work, "Name"));
        report.AppendLine(await MeasureWriteToSharpAsync(engine, "100 fields, field F0_0", files[1].Form, work, "F0_0"));
        report.AppendLine(await MeasureWriteToSharpAsync(engine, "2000 fields (20 x 100), field F0_0", files[2].Form, work, "F0_0"));

        report.AppendLine();
        report.AppendLine("(5) Save of a filled form: 200 text fields on 20 pages, every field written through AnnotationEditor.SetFormValueAsync, then AnnotationEditor.SaveAsync (DocumentSaver, incremental)");
        report.AppendLine(await MeasureSaveAsync(engine, work));

        report.AppendLine();
        report.AppendLine("(6) Render time of 50 consecutive pages (RenderPageAsync, scale 1.0, light, each page the first time, in page order); p50 / p99 / max in ms");
        var form50 = FormTestPdfs.WriteLargeForm(work, "form-50x30.pdf", 50, 30);
        var plain50 = WithoutForm(form50);
        report.AppendLine(await MeasureConsecutiveAsync(engine, "50-page form, 30 fields per page (1,500 fields)", form50));
        report.AppendLine(await MeasureConsecutiveAsync(engine, "the same file without the form environment", plain50));
        report.AppendLine(await MeasureConsecutiveAsync(engine, "thousand-pages.pdf, pages 1 to 50 (no widgets)", TestData.Pdf("thousand-pages.pdf")));

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"(7) Background read after the first sharp page: PdfDocument.GetFormInfoAsync (the hint scan of all pages) and GetFormFieldsAsync(page 0); {Runs} runs, each on a fresh document whose first page was rendered (the first call of each)");
        foreach (var (label, form, _, _) in files)
        {
            var (info, fields) = await TimeReadsAsync(engine, form);
            report.AppendLine(CultureInfo.InvariantCulture, $"    {label,-30} GetFormInfoAsync {Stats(info)} | GetFormFieldsAsync(0) {Stats(fields)}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "form-measurements.txt"), report.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
    }

    private static bool IsRelease() =>
        typeof(FormMeasurementTests).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    /// <summary>Writes a copy of <paramref name="path"/> whose catalog has no form: <c>/AcroForm</c> becomes <c>/AcroFoXm</c> (same length).</summary>
    private static string WithoutForm(string path)
    {
        var text = File.ReadAllText(path, Encoding.Latin1);
        var plain = text.Replace("/AcroForm", "/AcroFoXm", StringComparison.Ordinal);
        Assert.NotEqual(text, plain);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "plain-" + Path.GetFileName(path));
        File.WriteAllText(target, plain, Encoding.Latin1);
        return target;
    }

    private static async Task<List<double>> TimeOpenAsync(PdfEngine engine, string path)
    {
        var times = new List<double>();
        for (var run = 0; run <= Runs; run++)
        {
            var watch = Stopwatch.StartNew();
            var document = await engine.OpenAsync(path, cancellationToken: Ct);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            await document.DisposeAsync();
            if (run > 0)
            {
                times.Add(elapsed);
            }
        }

        return times;
    }

    private static async Task<(List<double> First, List<double> Warm)> TimeFirstRenderAsync(PdfEngine engine, string path)
    {
        var first = new List<double>();
        var warm = new List<double>();
        for (var run = 0; run <= Runs; run++)
        {
            await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
            var watch = Stopwatch.StartNew();
            _ = await document.RenderPageAsync(0, 1.0, cancellationToken: Ct);
            var firstMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            _ = await document.RenderPageAsync(0, 1.0, cancellationToken: Ct);
            var warmMs = watch.Elapsed.TotalMilliseconds;
            if (run > 0)
            {
                first.Add(firstMs);
                warm.Add(warmMs);
            }
        }

        return (first, warm);
    }

    private static async Task<List<double>> TimeTileAsync(PdfEngine engine, string path)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var tile = new PixelRect(300, 40, 256, 256);
        _ = await document.RenderTileAsync(0, 1.0, tile, cancellationToken: Ct);
        _ = await document.RenderTileAsync(0, 1.0, tile, cancellationToken: Ct);
        var times = new List<double>();
        for (var run = 0; run < Runs * 2; run++)
        {
            var watch = Stopwatch.StartNew();
            _ = await document.RenderTileAsync(0, 1.0, tile, cancellationToken: Ct);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        return times;
    }

    private static async Task<(List<double> Info, List<double> Fields)> TimeReadsAsync(PdfEngine engine, string path)
    {
        // A fresh document per run: both reads are kept by the document after the first one.
        var info = new List<double>();
        var fields = new List<double>();
        for (var run = 0; run <= Runs; run++)
        {
            await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
            _ = await document.RenderPageAsync(0, 1.0, cancellationToken: Ct);
            var watch = Stopwatch.StartNew();
            _ = await document.GetFormInfoAsync(Ct);
            var infoMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            _ = await document.GetFormFieldsAsync(0, Ct);
            var fieldsMs = watch.Elapsed.TotalMilliseconds;
            if (run > 0)
            {
                info.Add(infoMs);
                fields.Add(fieldsMs);
            }
        }

        return (info, fields);
    }

    private static async Task<string> MeasureWriteToSharpAsync(PdfEngine engine, string label, string source, TempFolder work, string fieldName)
    {
        var path = Path.Combine(work.Path, Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(source));
        File.Copy(source, path);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        Assert.Equal(ViewerState.Ready, session.State);
        var document = session.Document!;
        var scheduler = session.Scheduler!;
        var cache = session.Cache!;
        var sizes = await document.GetPageSizesAsync(0, document.PageCount, cancellationToken: Ct);
        var layout = new DocumentLayout(document.PageCount, sizes[0]);
        layout.SetSizes(0, sizes);
        layout.Zoom = 1.0;
        var plan = ViewportPlanner.Plan(layout, new DipRect(0, 0, 1280, 800), 1);
        var sharp = plan.Where(r => !r.Key.IsPreview && r.Key.Page == 0).Select(r => r.Key).ToHashSet();
        Assert.NotEmpty(sharp);

        var watch = new Stopwatch();
        Probe? probe = null;
        scheduler.TileReady += key => Volatile.Read(ref probe)?.OnTile(key, watch.Elapsed);
        session.PagesInvalidated += pages =>
        {
            if (pages.Contains(0) && Volatile.Read(ref probe) is { } current)
            {
                current.Invalidated = watch.Elapsed;

                // The view plans again on the UI thread after the event; here a pool thread takes that place.
                ThreadPool.QueueUserWorkItem(_ => scheduler.Update(plan));
            }
        };

        // The page as the view shows it before the write.
        var first = new Probe(sharp, cache);
        Volatile.Write(ref probe, first);
        scheduler.Update(plan);
        await first.All.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Volatile.Write(ref probe, null);

        var editor = new AnnotationEditor(session);
        var toInvalidated = new List<double>();
        var toReturned = new List<double>();
        var toFirstTile = new List<double>();
        var toAllTiles = new List<double>();
        for (var run = 0; run <= Runs; run++)
        {
            var current = new Probe(sharp, cache);
            Volatile.Write(ref probe, current);
            watch.Restart();
            Assert.NotNull(await editor.SetFormValueAsync(fieldName, FormFieldState.OfText("Messung " + run.ToString(CultureInfo.InvariantCulture))));
            var returned = watch.Elapsed;
            await current.All.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            if (run > 0)
            {
                toInvalidated.Add(current.Invalidated.TotalMilliseconds);
                toReturned.Add(returned.TotalMilliseconds);
                toFirstTile.Add(current.FirstTile.TotalMilliseconds);
                toAllTiles.Add(current.AllTiles.TotalMilliseconds);
            }

            // Undo, and wait until the page is sharp again, so every run starts from the same kind of state.
            var undo = new Probe(sharp, cache);
            Volatile.Write(ref probe, undo);
            await editor.UndoAsync();
            await undo.All.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        Volatile.Write(ref probe, null);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"    {label,-38} {sharp.Count,2} tiles | invalidated {Stats(toInvalidated)} | write returned {Stats(toReturned)} | first sharp tile {Stats(toFirstTile)} | all sharp tiles {Stats(toAllTiles)}");
    }

    private static async Task<string> MeasureSaveAsync(PdfEngine engine, TempFolder work)
    {
        var source = FormTestPdfs.WriteLargeForm(work, "form-save-20x10.pdf", 20, 10);
        var length = new FileInfo(source).Length;
        using var session = new ViewerSession(engine);
        await session.OpenAsync(source);
        Assert.Equal(ViewerState.Ready, session.State);
        var editor = new AnnotationEditor(session);

        var writes = new List<double>();
        var total = Stopwatch.StartNew();
        for (var page = 0; page < 20; page++)
        {
            for (var n = 0; n < 10; n++)
            {
                var watch = Stopwatch.StartNew();
                Assert.NotNull(await editor.SetFormValueAsync($"F{page}_{n}", FormFieldState.OfText($"Wert {page}-{n}")));
                writes.Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        var fillMs = total.Elapsed.TotalMilliseconds;
        var save = Stopwatch.StartNew();
        var result = await editor.SaveAsync(engine, source, overwriteChangedOriginal: false, progress: null, Ct);
        var saveMs = save.Elapsed.TotalMilliseconds;
        Assert.True(result.Adopted);
        var saved = new FileInfo(source).Length;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"    {length / 1024.0,6:0.0} KiB -> {saved / 1024.0,6:0.0} KiB | 200 writes: first {writes[0],6:0.0} ms, median {Median(writes),6:0.00} ms, max {writes.Max(),6:0.0} ms, all {fillMs,7:0.0} ms | save {saveMs,7:0.0} ms");
    }

    private static async Task<string> MeasureConsecutiveAsync(PdfEngine engine, string label, string path)
    {
        var times = new List<double>();
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        for (var page = 0; page < 50; page++)
        {
            var watch = Stopwatch.StartNew();
            _ = await document.RenderPageAsync(page, 1.0, cancellationToken: Ct);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        var sorted = times.Order().ToArray();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"    {label,-52} p50 {Percentile(sorted, 0.50),6:0.00} | p99 {Percentile(sorted, 0.99),6:0.00} | max {sorted[^1],6:0.00} | first page {times[0],6:0.00} | sum {times.Sum(),7:0.0}");
    }

    private static double Percentile(double[] sorted, double fraction) =>
        sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1)];

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string Stats(List<double> values) =>
        string.Create(CultureInfo.InvariantCulture, $"{Median(values),7:0.00} / {values.Min(),7:0.00} / {values.Max(),7:0.00}");

    /// <summary>Watches the cache for the sharp tiles of one page after one change.</summary>
    private sealed class Probe(HashSet<TileKey> sharp, RenderCache cache)
    {
        private int first;

        public TaskCompletionSource All { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Invalidated { get; set; }

        public TimeSpan FirstTile { get; private set; }

        public TimeSpan AllTiles { get; private set; }

        public void OnTile(TileKey key, TimeSpan at)
        {
            if (!sharp.Contains(key))
            {
                return;
            }

            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                FirstTile = at;
            }

            if (!All.Task.IsCompleted && sharp.All(cache.Contains))
            {
                AllTiles = at;
                All.TrySetResult();
            }
        }
    }
}
