using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// Measurement of Milestone 14 (<c>Docs/DECISIONS.md</c>, "Schwärzen: Messwerte (M14)"): what applying a redaction costs on a document
/// of 200 pages. Runs only with <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c> (best in Release:
/// <c>dotnet test tests/Pagewing.Core.Tests -c Release --filter-class "*RedactMeasurementTests"</c>) and writes
/// <c>redact-measurements.txt</c> there; all other work happens in a temp folder. Measures, never asserts a time.
/// </summary>
/// <remarks>
/// What this shows: the analysis, the build (working copy, redacting the pages, render check, rebuild), the whole checked write
/// (build, rewrite, check of the written file, replace), and, on the written file alone, the check (reading the result back,
/// text rule, pixel comparison) and the raw-bytes scan. What it cannot show: the cost on a large file with big images (the
/// rewrite is one job on the PDFium thread), the memory of PDFium alone (the peak working set is that of the whole test process),
/// and the start-time check (it needs the CI or a hardware runner).
/// </remarks>
public sealed class RedactMeasurementTests
{
    private const int Runs = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MeasuresTheRedaction()
    {
        var output = Environment.GetEnvironmentVariable(AnnotationMeasurementTests.OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Measurement only: set {AnnotationMeasurementTests.OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Pagewing redaction measurements, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"OS {Environment.OSVersion}, {Environment.ProcessorCount} logical processors, {(IsRelease() ? "Release" : "Debug")} build of the tests");
        report.AppendLine(CultureInfo.InvariantCulture, $"times in ms, median / min / max over {Runs} runs; text-dense.pdf: 200 pages of 612 x 792 pt, 45 lines each, \"Needle\" on every tenth page");
        report.AppendLine();

        var engine = new PdfEngine();
        using var work = new TempFolder();
        var source = Path.Combine(work.Path, "text-dense.pdf");
        File.Copy(TestData.Pdf("text-dense.pdf"), source);

        report.AppendLine("(1) 20 marks: every \"Needle\" (case sensitive, \"Everywhere\"), i.e. 20 of 200 pages change");
        report.AppendLine(await MeasureAsync(engine, work, source, NeedlePlanAsync));
        report.AppendLine();
        report.AppendLine("(2) 200 marks: one area over lines of text on every page, i.e. all 200 pages change (the most work the plan can ask of the pages)");
        report.AppendLine(await MeasureAsync(engine, work, source, EveryPagePlanAsync));
        report.AppendLine();
        report.AppendLine("note: 'peak working set' is the peak of the whole test process (managed and native memory, PDFium included) up to that point; it never goes down between the cases.");

        await File.WriteAllTextAsync(Path.Combine(output, "redact-measurements.txt"), report.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
    }

    private static bool IsRelease() =>
        typeof(RedactMeasurementTests).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private static string Ms(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        return string.Create(CultureInfo.InvariantCulture, $"{sorted[sorted.Length / 2]:F0} / {sorted[0]:F0} / {sorted[^1]:F0}");
    }

    private static async Task<RedactionPlan> NeedlePlanAsync(PdfDocument document)
    {
        var plan = RedactionPlan.Empty;
        for (var page = 0; page < document.PageCount; page++)
        {
            var text = await document.GetPageTextAsync(page, Ct);
            foreach (var match in TextMatcher.Find(text.Chars, "Needle", new SearchOptions(MatchCase: true)))
            {
                var boxes = text.Chars.Skip(match.FirstChar).Take(match.LastChar - match.FirstChar + 1).Where(static c => !c.IsGenerated).Select(static c => c.Box).ToArray();
                var union = boxes.Aggregate(static (a, b) => new PageRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
                plan = plan.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, page, [union], "Needle", Everywhere: true));
            }
        }

        Assert.Equal(20, plan.Marks.Count);
        return plan;
    }

    private static Task<RedactionPlan> EveryPagePlanAsync(PdfDocument document)
    {
        var plan = RedactionPlan.Empty;
        for (var page = 0; page < document.PageCount; page++)
        {
            plan = plan.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, page, [new PageRect(40, 100, 400, 140)], null, Everywhere: false));
        }

        return Task.FromResult(plan);
    }

    private static async Task<string> MeasureAsync(PdfEngine engine, TempFolder work, string source, Func<PdfDocument, Task<RedactionPlan>> makePlan)
    {
        var analyze = new List<double>();
        var build = new List<double>();
        var whole = new List<double>();
        var check = new List<double>();
        var raw = new List<double>();
        var rawOriginal = new List<double>();
        var details = new StringBuilder();
        var executor = new RedactionExecutor(engine);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
        long length = 0;

        for (var run = 0; run < Runs; run++)
        {
            await using var shown = await engine.OpenAsync(source, null, Ct);
            var plan = await makePlan(shown);
            var target = Path.Combine(work.Path, $"redacted-{Guid.NewGuid():N}.pdf");

            var start = Stopwatch.GetTimestamp();
            var analysis = await executor.AnalyzeAsync(plan, shown, source, null, Ct);
            analyze.Add(Elapsed(start));
            Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));

            RedactionExpectation expectation;
            start = Stopwatch.GetTimestamp();
            await using (var built = await executor.BuildAsync(plan, shown, source, null, null, Ct))
            {
                build.Add(Elapsed(start));
                expectation = built.Expectation;
            }

            start = Stopwatch.GetTimestamp();
            var result = await RedactionApplier.WriteAsync(engine, shown, source, null, null, new RedactionApplyRequest(plan, target), null, Ct);
            whole.Add(Elapsed(start));
            await using (result.Saved.Reopened)
            {
                Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
                length = new FileInfo(target).Length;

                await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                start = Stopwatch.GetTimestamp();
                var verified = await RedactionVerifier.VerifyAsync(result.Saved.Reopened, stream, expectation, Ct);
                check.Add(Elapsed(start));
                Assert.True(verified.IsClean, RedactionRefusalText.Of(verified));

                stream.Position = 0;
                start = Stopwatch.GetTimestamp();
                var left = RawContentScanner.Count(stream, "Needle", RawScanLimits.Default, out _, Ct);
                raw.Add(Elapsed(start));
                if (run == 0)
                {
                    details.AppendLine(CultureInfo.InvariantCulture, $"    removed texts counted: {expectation.Removed.Count}; 'Needle' left in the raw bytes of the result: {left}");
                }
            }

            await using (var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var start2 = Stopwatch.GetTimestamp();
                _ = RawContentScanner.Count(original, "Needle", RawScanLimits.Default, out _, Ct);
                rawOriginal.Add(Elapsed(start2));
            }

            File.Delete(target);
        }

        var peak = Process.GetCurrentProcess();
        peak.Refresh();
        var pagesPerSecond = whole.Select(static ms => 200 / (ms / 1000)).ToArray();
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"    Analyze (working copy, pass, render check, inventory): {Ms(analyze)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"    Build (working copy, pass, render check, rebuild): {Ms(build)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"    whole Apply (Build + rewrite + check of the written file + replace): {Ms(whole)}; = {pagesPerSecond.Order().ElementAt(pagesPerSecond.Length / 2):F0} pages/s (median, all 200 pages of the file)");
        text.AppendLine(CultureInfo.InvariantCulture, $"    check of the written file alone (RedactionVerifier: read back, text rule, raw bytes, pixels): {Ms(check)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"    raw-bytes scan alone, result ({length / 1024.0 / 1024:F1} MiB): {Ms(raw)}; original: {Ms(rawOriginal)}");
        text.Append(details);
        text.AppendLine(CultureInfo.InvariantCulture, $"    working set before {workingSetBefore / 1024.0 / 1024:F0} MiB; peak working set of the process {peak.PeakWorkingSet64 / 1024.0 / 1024:F0} MiB");
        return text.ToString().TrimEnd();
    }
}
