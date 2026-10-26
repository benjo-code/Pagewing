using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>
/// Measurements of Milestone 9 (<c>Docs/DECISIONS.md</c>, "Annotieren und Speichern: Messwerte (M9)"): the time from a markup
/// to the new sharp image of its page, the duration of a save and how long it occupies the PDFium thread, and the files for
/// the scroll measurement with and without markups. Runs only with <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c> (best in
/// Release: <c>dotnet test tests/Pagewing.Core.Tests -c Release --filter-class "*AnnotationMeasurementTests"</c>); writes
/// <c>annotation-measurements.txt</c> and the scroll files into that folder, all other work happens in a temp folder.
/// Measures, never asserts a time.
/// </summary>
public sealed partial class AnnotationMeasurementTests
{
    /// <summary>The environment variable that names the output folder.</summary>
    public const string OutputVariable = "PAGEWING_MEASURE_OUT";

    /// <summary>The number of pages of the generated large file (each with an uncompressed RGB image of about 1 MiB).</summary>
    private const int LargePages = 380;

    private const int Runs = 15;

    private static readonly AnnotationColor Yellow = AnnotationPalette.ColorOf(MarkupColor.Yellow);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MeasuresMarkupSaveAndScrollFiles()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Measurement only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Pagewing annotation measurements, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"OS {Environment.OSVersion}, {Environment.ProcessorCount} logical processors, {(IsRelease() ? "Release" : "Debug")} build of the tests");
        report.AppendLine();

        var engine = new PdfEngine();
        using var work = new TempFolder();

        report.AppendLine("(a) Markup to new sharp page image: ApplyMarkupAsync start -> PagesInvalidated -> all sharp tiles of the page in the cache");
        report.AppendLine("    viewport 1280 x 800 DIP at the top of the document, render scaling 1, plan of ViewportPlanner as the view makes it;");
        report.AppendLine(CultureInfo.InvariantCulture, $"    {Runs} runs after one warm-up, each run undone (and rendered again) before the next; median / min / max in ms");
        foreach (var fixture in new[] { "text-basic.pdf", "text-dense.pdf" })
        {
            foreach (var zoom in new[] { 1.0, 2.0 })
            {
                var line = await MeasureMarkupToSharpAsync(engine, work, fixture, zoom);
                report.AppendLine(CultureInfo.InvariantCulture, $"    {fixture,-15} {zoom * 100,4:0} %  {line}");
            }
        }

        report.AppendLine();
        report.AppendLine("(b) Save (AnnotationEditor.SaveAsync over DocumentSaver, \"Save\" to the opened path) and the PDFium thread meanwhile");
        report.AppendLine("    probe: an empty interactive job every 2 ms during the save, its wait = how long the thread was busy before it;");
        report.AppendLine("    render: RenderPageAsync (interactive, 100 % of another page) started as soon as PDFium writes; baseline without a save");
        report.AppendLine(await MeasureSaveAsync(engine, "text-dense.pdf", Copy(work, "text-dense.pdf"), markups: 1, saves: 3));
        report.AppendLine(await MeasureSaveAsync(engine, "text-dense.pdf", Copy(work, "text-dense.pdf", "text-dense-10.pdf"), markups: 10, saves: 3));
        var large = WriteLargePdf(work.Path, LargePages);
        try
        {
            report.AppendLine(await MeasureSaveAsync(engine, $"large-{LargePages}-pages", large, markups: 1, saves: 3));
        }
        finally
        {
            await TryDeleteAsync(large);
        }

        report.AppendLine();
        report.AppendLine("(c) Files for --measure-scroll (written next to this report)");
        report.AppendLine(await WriteScrollFilesAsync(engine, work, output));

        await File.WriteAllTextAsync(Path.Combine(output, "annotation-measurements.txt"), report.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
    }

    private static bool IsRelease() =>
        typeof(AnnotationMeasurementTests).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    private static string Copy(TempFolder work, string fixture, string? name = null)
    {
        var path = Path.Combine(work.Path, name ?? fixture);
        File.Copy(TestData.Pdf(fixture), path, overwrite: true);
        return path;
    }

    private static async Task<ViewerSession> OpenAsync(PdfEngine engine, string path)
    {
        var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        Assert.Equal(ViewerState.Ready, session.State);
        return session;
    }

    /// <summary>The rectangles of the first <paramref name="length"/> characters of a page (its first line or less).</summary>
    private static async Task<IReadOnlyList<PageRect>> FirstCharsAsync(PdfDocument document, int page, int length)
    {
        var text = await document.GetPageTextAsync(page, Ct);
        var all = text.GetText(0, text.Chars.Count);
        var end = all.IndexOfAny(['\r', '\n']);
        end = end < 0 ? Math.Min(length, all.Length) : Math.Min(length, end);
        var rects = text.RectsOf(0, end);
        Assert.NotEmpty(rects);
        return rects;
    }

    private static async Task<string> MeasureMarkupToSharpAsync(PdfEngine engine, TempFolder work, string fixture, double zoom)
    {
        using var session = await OpenAsync(engine, Copy(work, fixture, $"sharp-{zoom * 100:0}-{fixture}"));
        var document = session.Document!;
        var scheduler = session.Scheduler!;
        var cache = session.Cache!;
        var sizes = await document.GetPageSizesAsync(0, document.PageCount, cancellationToken: Ct);
        var layout = new DocumentLayout(document.PageCount, sizes[0]);
        layout.SetSizes(0, sizes);
        layout.Zoom = zoom;
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

        // The page as the view shows it before the markup.
        await RenderAllAsync();

        var editor = new AnnotationEditor(session);
        var rects = await FirstCharsAsync(document, 0, 12);
        var toInvalidated = new List<double>();
        var toApplied = new List<double>();
        var toFirstTile = new List<double>();
        var toAllTiles = new List<double>();
        for (var run = 0; run <= Runs; run++)
        {
            var current = new Probe(sharp, cache);
            Volatile.Write(ref probe, current);
            watch.Restart();
            Assert.NotNull(await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null));
            var applied = watch.Elapsed;
            await current.All.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            if (run > 0)
            {
                toInvalidated.Add(current.Invalidated.TotalMilliseconds);
                toApplied.Add(applied.TotalMilliseconds);
                toFirstTile.Add(current.FirstTile.TotalMilliseconds);
                toAllTiles.Add(current.AllTiles.TotalMilliseconds);
            }

            // Undo, and wait until the page is sharp again, so every run starts from the same state.
            var undo = new Probe(sharp, cache);
            Volatile.Write(ref probe, undo);
            await editor.UndoAsync();
            await undo.All.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        Volatile.Write(ref probe, null);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sharp.Count,2} tiles | invalidated {Stats(toInvalidated)} | apply returned {Stats(toApplied)} | first sharp tile {Stats(toFirstTile)} | all sharp tiles {Stats(toAllTiles)}");

        async Task RenderAllAsync()
        {
            var first = new Probe(sharp, cache);
            Volatile.Write(ref probe, first);
            scheduler.Update(plan);
            await first.All.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Volatile.Write(ref probe, null);
        }
    }

    private static async Task<string> MeasureSaveAsync(PdfEngine engine, string label, string path, int markups, int saves)
    {
        var lines = new StringBuilder();
        using var session = await OpenAsync(engine, path);
        for (var save = 0; save < saves; save++)
        {
            lines.AppendLine(await MeasureOneSaveAsync(engine, session, label, path, markups, save));
        }

        return lines.ToString().TrimEnd();
    }

    private static async Task<string> MeasureOneSaveAsync(PdfEngine engine, ViewerSession session, string label, string path, int markups, int round)
    {
        var length = new FileInfo(path).Length;
        var document = session.Document!;
        var other = Math.Min(5, document.PageCount - 1);

        // Warm the page that is rendered during the save, then take the baseline of that render without a save.
        _ = await document.RenderPageAsync(other, 1, cancellationToken: Ct);
        var baseline = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var started = Stopwatch.StartNew();
            _ = await document.RenderPageAsync(other, 1, cancellationToken: Ct);
            baseline.Add(started.Elapsed.TotalMilliseconds);
        }

        var editor = new AnnotationEditor(session);
        for (var i = 0; i < markups; i++)
        {
            var page = ((i * document.PageCount / markups) + round) % document.PageCount;
            var rects = await FirstCharsAsync(document, page, 10);
            Assert.NotNull(await editor.ApplyMarkupAsync(page, AnnotationKind.Highlight, rects, Yellow, null, null));
        }

        using var stop = new CancellationTokenSource();
        var waits = new List<double>();
        var probing = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var asked = Stopwatch.StartNew();
                await PdfiumThread.Shared.InvokeAsync(static () => { }, PdfiumPriority.Interactive, Ct);
                waits.Add(asked.Elapsed.TotalMilliseconds);
                await Task.Delay(2, Ct);
            }
        });

        // Progress 0 comes just before the PDFium job of the write, the first value above 0 from inside it (PDFium copies),
        // 1 after the temporary file is flushed to disk.
        var watch = Stopwatch.StartNew();
        var progress = new Phases(watch);
        var save = editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress, Ct);
        await progress.Writing.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var renderStarted = watch.Elapsed;
        double renderLatency;
        try
        {
            _ = await document.RenderPageAsync(other, 1, cancellationToken: Ct);
            renderLatency = (watch.Elapsed - renderStarted).TotalMilliseconds;
        }
        catch (ObjectDisposedException)
        {
            // The save replaced the document before the render ran.
            renderLatency = double.NaN;
        }

        var result = await save;
        var saveMs = watch.Elapsed.TotalMilliseconds;
        await stop.CancelAsync();
        await probing;
        Assert.True(result.Adopted);

        var busy = waits.Where(w => w > 5).ToList();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"    {label,-20} {length / 1048576.0,6:0.0} MiB, {markups,2} markup(s), save {round + 1}: total {saveMs,7:0.0} ms (checks before the write {progress.Start.TotalMilliseconds,6:0.0}, write and flush {(progress.End - progress.Start).TotalMilliseconds,6:0.0}, prefix check, reopen, render check, replace and adopt {saveMs - progress.End.TotalMilliseconds,6:0.0}) | probe waits n={waits.Count}, max {waits.DefaultIfEmpty().Max(),6:0.0} ms, sum over 5 ms {busy.Sum(),6:0.0} ms | render started while PDFium writes: {renderLatency,6:0.0} ms (baseline median {Median(baseline):0.0} ms)");
    }

    private static async Task<string> WriteScrollFilesAsync(PdfEngine engine, TempFolder work, string output)
    {
        var plain = Path.Combine(output, "scroll-text-dense.pdf");
        File.Copy(TestData.Pdf("text-dense.pdf"), plain, overwrite: true);

        var marked = Copy(work, "text-dense.pdf", "scroll-text-dense-200-markups.pdf");
        AnnotationKind[] kinds = [AnnotationKind.Highlight, AnnotationKind.Underline, AnnotationKind.StrikeOut];
        using (var session = await OpenAsync(engine, marked))
        {
            var editor = new AnnotationEditor(session);
            var document = session.Document!;
            for (var page = 0; page < document.PageCount; page++)
            {
                var rects = await FirstCharsAsync(document, page, 40);
                var color = AnnotationPalette.ColorOf(AnnotationPalette.All[page % AnnotationPalette.All.Count]);
                Assert.NotNull(await editor.ApplyMarkupAsync(page, kinds[page % kinds.Length], rects, color, page % 10 == 0 ? $"Comment {page + 1}" : null, "Measure"));
            }

            await editor.SaveAsync(engine, marked, overwriteChangedOriginal: false, progress: null, Ct);
        }

        var target = Path.Combine(output, "scroll-text-dense-200-markups.pdf");
        File.Copy(marked, target, overwrite: true);
        var dark = Path.Combine(output, "scroll-dark.json");
        await File.WriteAllTextAsync(dark, "{\n  \"Theme\": \"Dark\",\n  \"PageAppearance\": \"FollowTheme\"\n}\n", Ct);
        return string.Create(CultureInfo.InvariantCulture, $"    {Path.GetFileName(plain)} ({new FileInfo(plain).Length} bytes), {Path.GetFileName(target)} ({new FileInfo(target).Length} bytes, 200 markups, one per page), {Path.GetFileName(dark)} (dark theme, dark page)");
    }

    /// <summary>Writes a PDF of <paramref name="pages"/> pages, each with an uncompressed 600 x 580 RGB image and a line of text.</summary>
    private static string WriteLargePdf(string folder, int pages)
    {
        const int Width = 600;
        const int Height = 580;
        var path = Path.Combine(folder, $"large-{pages}-pages.pdf");
        var offsets = new List<long>();
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            void Write(string text) => file.Write(Encoding.Latin1.GetBytes(text));
            void Begin(int number)
            {
                while (offsets.Count < number)
                {
                    offsets.Add(0);
                }

                offsets[number - 1] = file.Position;
                Write(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n"));
            }

            Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
            Begin(1);
            Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            var kids = string.Join(' ', Enumerable.Range(0, pages).Select(i => string.Create(CultureInfo.InvariantCulture, $"{4 + (i * 3)} 0 R")));
            Begin(2);
            Write(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>\nendobj\n"));
            Begin(3);
            Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
            var row = new byte[Width * 3];
            for (var i = 0; i < pages; i++)
            {
                var page = 4 + (i * 3);
                var content = string.Create(CultureInfo.InvariantCulture, $"q 400 0 0 387 50 280 cm /Im0 Do Q BT /F1 18 Tf 50 240 Td (Large page {i + 1} of {pages}) Tj ET\n");
                Begin(page);
                Write(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 700] /Resources << /Font << /F1 3 0 R >> /XObject << /Im0 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>\nendobj\n"));
                Begin(page + 1);
                Write(string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n"));
                Begin(page + 2);
                Write(string.Create(CultureInfo.InvariantCulture, $"<< /Type /XObject /Subtype /Image /Width {Width} /Height {Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {Width * Height * 3} >>\nstream\n"));
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        row[(x * 3) + 0] = (byte)(x + i);
                        row[(x * 3) + 1] = (byte)(y ^ i);
                        row[(x * 3) + 2] = (byte)(x ^ y);
                    }

                    file.Write(row);
                }

                Write("\nendstream\nendobj\n");
            }

            var xref = file.Position;
            var table = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n"));
            foreach (var offset in offsets)
            {
                table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
            }

            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Write(table.ToString());
        }

        return path;
    }

    /// <summary>Deletes the large file; the document closes it on the PDFium thread a moment after its session is disposed.</summary>
    private static async Task TryDeleteAsync(string path)
    {
        for (var attempt = 0; attempt < 50 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                await Task.Delay(100, Ct);
            }
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string Stats(List<double> values) =>
        string.Create(CultureInfo.InvariantCulture, $"{Median(values),6:0.0} / {values.Min(),6:0.0} / {values.Max(),6:0.0}");

    /// <summary>The times of the progress of a save: 0 (before the write), the first value above 0 (PDFium writes), 1 (flushed).</summary>
    private sealed class Phases(Stopwatch watch) : IProgress<double>
    {
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Start { get; private set; }

        public TimeSpan End { get; private set; }

        public void Report(double value)
        {
            if (value <= 0)
            {
                Start = watch.Elapsed;
            }
            else if (value >= 1)
            {
                End = watch.Elapsed;
                Writing.TrySetResult();
            }
            else
            {
                Writing.TrySetResult();
            }
        }
    }

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
