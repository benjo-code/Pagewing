using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.TextEdit;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>
/// Measurements of text editing (milestone 16, task 7). Only with <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c>; they write <c>text-edit-measurements.txt</c>
/// and never check a time.
/// </summary>
public sealed class TextEditMeasurementTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task MeasuresReadingAndReplacingRuns()
    {
        var output = Environment.GetEnvironmentVariable("PAGEWING_MEASURE_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "Measurement only: set PAGEWING_MEASURE_OUT=<folder>.");
        Directory.CreateDirectory(output!);
        var report = new StringBuilder();

        // Reading the runs of a page: a page of text, a page with many objects, the hostile page.
        foreach (var (file, page) in new[] { ("text-edit-subset.pdf", 0), ("text-dense.pdf", 0), ("text-edit-hostile.pdf", 0), ("text-edit-hostile.pdf", 1) })
        {
            await using var document = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: Ct);
            _ = await document.GetTextRunsAsync(page, Ct);
            var clock = Stopwatch.StartNew();
            var runs = await document.GetTextRunsAsync(page, Ct);
            report.AppendLine(CultureInfo.InvariantCulture, $"reading the runs of {file} page {page + 1}: {runs.Count} runs, {clock.ElapsedMilliseconds} ms");
        }

        // Replacing a run in one job: with and without Geist, and what the file grows by.
        foreach (var (newText, label) in new[] { ("Invoice total: 1,205 EUR", "all characters in the font"), ("Invoice total: 1,259 EUR", "one character in Geist") })
        {
            var path = Path.Combine(folder.Path, label.Replace(' ', '-') + ".pdf");
            File.Copy(TestData.Pdf("text-edit-subset.pdf"), path, overwrite: true);
            await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
            var run = (await document.GetTextRunsAsync(0, Ct)).Single(r => r.Text.TrimEnd() == "Invoice total: 1,250 EUR");
            var clock = Stopwatch.StartNew();
            _ = await document.ReplaceTextRunAsync(run, newText, TextEditMode.KeepFont, Ct);
            var replaced = clock.ElapsedMilliseconds;
            using var stream = new MemoryStream();
            clock.Restart();
            await document.SaveIncrementalAsync(stream, Ct);
            report.AppendLine(CultureInfo.InvariantCulture, $"replacing one run ({label}): {replaced} ms in one job, saving {clock.ElapsedMilliseconds} ms, file grew by {stream.Length - new FileInfo(TestData.Pdf("text-edit-subset.pdf")).Length} bytes");
        }

        await File.WriteAllTextAsync(Path.Combine(output!, "text-edit-measurements.txt"), report.ToString(), Ct);
    }
}
