using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>
/// Measurements of text recognition (milestone 15, task 11). Only with <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c> (and the native library for the engine
/// figures); they write <c>ocr-measurements.txt</c> and never check a time.
/// </summary>
public sealed class OcrMeasurementTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task MeasuresTheEngineTheLayerAndAThousandPages()
    {
        var output = Environment.GetEnvironmentVariable("PAGEWING_MEASURE_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "Measurement only: set PAGEWING_MEASURE_OUT=<folder>.");
        OcrTestLibrary.Require();
        Directory.CreateDirectory(output!);
        var report = new StringBuilder();

        // The library: load time and the memory one engine holds.
        var memoryBefore = Process.GetCurrentProcess().WorkingSet64;
        var clock = Stopwatch.StartNew();
        using var one = OcrEngine.Create(OcrTestLibrary.Tessdata, "eng");
        report.AppendLine(CultureInfo.InvariantCulture, $"load of the library and one engine (eng): {clock.ElapsedMilliseconds} ms");
        var (gray, width, height, _) = await OcrTestImages.RenderGrayAsync(TestData.Pdf("ocr-scan.pdf"), 0, 200, Ct);
        clock.Restart();
        _ = one.Recognize(gray, width, height, 200, Ct);
        report.AppendLine(CultureInfo.InvariantCulture, $"first page (200 dpi, {width}x{height}): {clock.ElapsedMilliseconds} ms");
        report.AppendLine(CultureInfo.InvariantCulture, $"working set growth with one engine after a page: {(Process.GetCurrentProcess().WorkingSet64 - memoryBefore) / (1024 * 1024)} MB");

        // Pages per second with the real engine, one to four workers, on the three pages of the scan (repeated by choosing them over and over).
        foreach (var jobs in new[] { 1, 2, 4 })
        {
            await using var document = await engine.OpenAsync(TestData.Pdf("ocr-scan.pdf"), cancellationToken: Ct);
            clock.Restart();
            var result = await OcrRunner.RunAsync(document, new OcrOptions("eng", OcrMode.Force, 0, null, jobs), new TesseractEngineFactory(OcrTestLibrary.Tessdata), null, Ct);
            var seconds = clock.Elapsed.TotalSeconds;
            report.AppendLine(CultureInfo.InvariantCulture, $"jobs {jobs}: {result.Pages.Count} pages in {seconds:0.00} s = {result.Pages.Count / seconds:0.0} pages/s, {result.Report.Words} words");
        }

        // The growth of the file: the layer of three pages written and saved.
        var path = Path.Combine(folder.Path, "ocr-scan.pdf");
        File.Copy(TestData.Pdf("ocr-scan.pdf"), path);
        var original = new FileInfo(path).Length;
        using (var session = new ViewerSession(engine))
        {
            await session.OpenAsync(path);
            var result = await OcrRunner.RunAsync(session.Document!, new OcrOptions("eng", OcrMode.Skip, 0, null, 2), new TesseractEngineFactory(OcrTestLibrary.Tessdata), null, Ct);
            var editor = new AnnotationEditor(session);
            await editor.ApplyOcrAsync(result, replace: false);
            await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"file growth for three recognized pages: {new FileInfo(path).Length - original} bytes (original {original} bytes)");

        // The run over a thousand pages with an engine that answers at once: what the runner itself costs (rendering, scheduling, mapping).
        await using var thousand = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), cancellationToken: Ct);
        var fake = new FakeOcrFactory(_ => new OcrRecognition([new OcrWord("word", 100, 100, 300, 150, 90)]));
        var peakBefore = GC.GetTotalMemory(forceFullCollection: true);
        clock.Restart();
        var many = await OcrRunner.RunAsync(thousand, new OcrOptions("eng", OcrMode.Force, 150, null, 4), fake, null, Ct);
        report.AppendLine(CultureInfo.InvariantCulture, $"runner over {many.Pages.Count} pages with an instant engine (150 dpi, 4 jobs): {clock.Elapsed.TotalSeconds:0.0} s; managed memory growth {(GC.GetTotalMemory(forceFullCollection: true) - peakBefore) / (1024 * 1024)} MB");

        await File.WriteAllTextAsync(Path.Combine(output, "ocr-measurements.txt"), report.ToString(), Ct);
    }
}
