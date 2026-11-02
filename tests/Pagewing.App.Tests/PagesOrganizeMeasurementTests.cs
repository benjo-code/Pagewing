using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Measurement of Milestone 13 (<c>Docs/DECISIONS.md</c>, "Seiten organisieren: Messwerte und Startpfad (M13)"): what the
/// page organizer costs on large documents. Runs only with <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c> (best in Release) and
/// writes <c>pages-organize-measurements.txt</c> there; never asserts a time.
/// </summary>
/// <remarks>
/// What this shows: the time to enter the mode on a document of 1,000 pages in a headless window (software Skia, no GPU), the
/// time of the operations on the plan, and, on generated documents of 1,000 and 20,000 pages without content, the time of the
/// analysis, of the build of the working copy (with the number of jobs it puts on the PDFium thread) and of the whole checked
/// rewrite; the rewrite of a generated file of 380 MiB is a note. What it cannot show: scrolling the list on a real compositor,
/// and the cost of the new icon geometries in the start path (a start-time check on real hardware does, see the entry in
/// DECISIONS).
/// </remarks>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class PagesOrganizeMeasurementTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-organize-measure-" + Guid.NewGuid().ToString("N"))).FullName;

    public PagesOrganizeMeasurementTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    [AvaloniaFact]
    public async Task MeasuresThePageOrganizer()
    {
        var output = Environment.GetEnvironmentVariable("PAGEWING_MEASURE_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "Measurement only: set PAGEWING_MEASURE_OUT=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Pagewing page organizer measurements, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"OS {Environment.OSVersion}, {Environment.ProcessorCount} logical processors, {(IsRelease() ? "Release" : "Debug")} build of the tests, headless Avalonia with software Skia (no compositor, no GPU)");
        report.AppendLine("times in ms; 'median / max of n' where n runs were made");
        report.AppendLine();

        report.AppendLine("(1) entering the mode in a window of 1024 x 720 DIP on thousand-pages.pdf (1,000 pages): from the command to a list that shows the plan (1,000 entries) and has settled");
        report.AppendLine(await MeasureEnterAsync());
        report.AppendLine();

        report.AppendLine("(2) operations on the plan (pure, no PDFium), median / max of 30");
        foreach (var pages in new[] { 1000, PageEditLimits.MaxSlots })
        {
            report.AppendLine(MeasurePlanOperations(pages));
        }

        report.AppendLine();
        report.AppendLine("(3) the checked rewrite on generated documents of empty pages (one MediaBox each, no content): open / Analyze / Build (working copy only) / whole write (Build + rewrite + check + replace)");
        report.AppendLine("    plan: every page turned by 90 degrees, i.e. the most jobs per page; 'Build jobs' = steps reported, each step is one Get and one Set job on the PDFium thread");
        foreach (var pages in new[] { 1000, PageEditLimits.MaxSlots })
        {
            report.AppendLine(await MeasureRewriteAsync(pages));
        }

        report.AppendLine();
        report.AppendLine("(4) note: whole write of a generated file of 380 pages with an uncompressed 1 MiB image each (about 380 MiB), one page turned");
        report.AppendLine(await MeasureLargeAsync(380));

        await File.WriteAllTextAsync(System.IO.Path.Combine(output, "pages-organize-measurements.txt"), report.ToString());
    }

    private static bool IsRelease() =>
#if DEBUG
        false;
#else
        true;
#endif

    private static string Ms(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        return string.Create(CultureInfo.InvariantCulture, $"{sorted[sorted.Length / 2]:F1} / {sorted[^1]:F1}");
    }

    private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private async Task<string> MeasureEnterAsync()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        await viewModel.OpenAsync(TestData.Pdf("thousand-pages.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage);
        tab.SidePanel = SidePanelKind.Pages;
        await WaitUntilAsync(window, () => FindList(window, tab) is { IsEffectivelyVisible: true } list && list.Thumbnails.Count == 1000);

        var enter = new List<double>();
        var leave = new List<double>();
        for (var run = 0; run < 6; run++)
        {
            var start = Stopwatch.GetTimestamp();
            tab.OrganizeCommand.Execute(null);
            await WaitUntilAsync(window, () => FindList(window, tab) is { } list && list.Classes.Contains("organizing") && list.Thumbnails.Count == 1000 && list.Thumbnails.Any(item => item.Bitmap is not null));
            var elapsed = Elapsed(start);
            if (run > 0)
            {
                enter.Add(elapsed);
            }

            start = Stopwatch.GetTimestamp();
            tab.DiscardPageEditsCommand.Execute(null);
            await WaitUntilAsync(window, () => FindList(window, tab) is { } list && !list.Classes.Contains("organizing") && list.Thumbnails.Count == 1000);
            if (run > 0)
            {
                leave.Add(Elapsed(start));
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"    enter (until the first thumbnail of the plan is drawn): {Ms(enter)} (5 runs after one warm-up), leave: {Ms(leave)}");
    }

    private static ThumbnailList? FindList(Window window, TabViewModel tab) =>
        window.GetVisualDescendants().OfType<ThumbnailList>().FirstOrDefault(list => list.Name == "ThumbnailList" && ReferenceEquals(list.DataContext, tab));

    private static string MeasurePlanOperations(int pages)
    {
        const int Runs = 30;
        var all = Enumerable.Range(0, pages).ToArray();
        var half = Enumerable.Range(0, pages).Where(i => i % 2 == 0).ToArray();
        var ten = Enumerable.Range(0, 10).ToArray();
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"    {pages} pages:");
        var identity = PageEditPlan.Identity(pages);
        void Time(string name, Func<PageEditPlan> op)
        {
            var values = new List<double>();
            for (var i = 0; i < Runs; i++)
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    _ = op();
                }
                catch (PageEditException ex) when (ex.Error == PageEditError.LimitExceeded)
                {
                    // A plan at the limit has no room to grow: the refusal is the result.
                    line.Append(CultureInfo.InvariantCulture, $" {name} refused at the limit ({Elapsed(start):F2} ms);");
                    return;
                }

                values.Add(Elapsed(start));
            }

            line.Append(CultureInfo.InvariantCulture, $" {name} {Ms(values)};");
        }

        Time("Identity", () => PageEditPlan.Identity(pages));
        Time("Rotate(all)", () => identity.Rotate(all, 90));
        Time("Delete(half)", () => identity.Delete(half));
        Time("Move(10 to end)", () => identity.Move(ten, pages));
        Time("Duplicate(10)", () => identity.Duplicate(ten));
        Time("InsertBlank", () => identity.InsertBlank(pages / 2, new BlankPage(595, 842)));
        return line.ToString();
    }

    private async Task<string> MeasureRewriteAsync(int pages)
    {
        var path = System.IO.Path.Combine(_folder, $"empty-{pages}.pdf");
        WriteEmptyPages(path, pages);
        var engine = new PdfEngine();
        var plan = PageEditPlan.Identity(pages).Rotate(Enumerable.Range(0, pages).ToArray(), 90);
        var none = new Dictionary<PageSourceId, PageSourceFile>();
        var noDocuments = new Dictionary<PageSourceId, PdfDocument>();

        var start = Stopwatch.GetTimestamp();
        await using var shown = await engine.OpenAsync(path, null, CancellationToken.None);
        var open = Elapsed(start);

        start = Stopwatch.GetTimestamp();
        var analysis = await PageEditExecutor.AnalyzeAsync(plan, shown, noDocuments, CancellationToken.None);
        var analyze = Elapsed(start);

        var reports = new Counter();
        start = Stopwatch.GetTimestamp();
        var working = await new PageEditExecutor(engine).BuildAsync(plan, new PageSourceFile(path, null), none, reports, CancellationToken.None);
        var build = Elapsed(start);
        await working.DisposeAsync();

        var target = System.IO.Path.Combine(_folder, $"empty-{pages}-turned.pdf");
        var writeReports = new Counter();
        start = Stopwatch.GetTimestamp();
        var saved = await PageEditApplier.WriteAsync(engine, shown, path, null, openedStamp: null, new PageApplyRequest(plan, target, none), writeReports, CancellationToken.None);
        var write = Elapsed(start);
        var writtenPages = saved.Reopened.PageCount;
        await saved.Reopened.DisposeAsync();

        return string.Create(CultureInfo.InvariantCulture, $"    {pages} pages ({new FileInfo(path).Length / 1024} KiB -> {new FileInfo(target).Length / 1024} KiB): open {open:F0} / Analyze {analyze:F0} ({analysis.Losses.Count} losses) / Build {build:F0} ({reports.Count} progress reports, {pages * 2} Get/Set jobs for the turns) / whole write {write:F0} ({writeReports.Count} reports, {writtenPages} pages read back)");
    }

    private async Task<string> MeasureLargeAsync(int pages)
    {
        var path = WriteLargePdf(_folder, pages);
        try
        {
            var engine = new PdfEngine();
            var plan = PageEditPlan.Identity(pages).Rotate([0], 90);
            var target = System.IO.Path.Combine(_folder, "large-turned.pdf");
            await using var shown = await engine.OpenAsync(path, null, CancellationToken.None);
            var times = new List<double>();
            for (var run = 0; run < 2; run++)
            {
                var start = Stopwatch.GetTimestamp();
                var saved = await PageEditApplier.WriteAsync(engine, shown, path, null, openedStamp: null, new PageApplyRequest(plan, target, new Dictionary<PageSourceId, PageSourceFile>()), progress: null, CancellationToken.None);
                times.Add(Elapsed(start));
                await saved.Reopened.DisposeAsync();
                File.Delete(target);
            }

            return string.Create(CultureInfo.InvariantCulture, $"    {pages} pages, {new FileInfo(path).Length / (1024 * 1024)} MiB: whole write {times[0]:F0} then {times[1]:F0} (two runs, in this order)");
        }
        finally
        {
            for (var attempt = 0; attempt < 50 && File.Exists(path); attempt++)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    /// <summary>Writes a PDF of <paramref name="pages"/> empty pages (a MediaBox only) with a correct cross-reference table.</summary>
    private static void WriteEmptyPages(string path, int pages)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Object(string body)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => string.Create(CultureInfo.InvariantCulture, $"{3 + i} 0 R")))}] /Count {pages} >>"));
        for (var i = 0; i < pages; i++)
        {
            Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] >>");
        }

        var xref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text.ToString()));
    }

    /// <summary>Writes a PDF of <paramref name="pages"/> pages, each with an uncompressed 600 x 580 RGB image and a line of text.</summary>
    private static string WriteLargePdf(string folder, int pages)
    {
        const int Width = 600;
        const int Height = 580;
        var path = System.IO.Path.Combine(folder, $"large-{pages}-pages.pdf");
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

            Write("%PDF-1.7\n");
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

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The window did not reach the expected state.");
            }

            await Task.Delay(1);
        }
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    private sealed class Counter : IProgress<double>
    {
        public int Count { get; private set; }

        public void Report(double value) => Count++;
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
