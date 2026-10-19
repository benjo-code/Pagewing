using System.Runtime.Versioning;
using Pagewing.App.Platform.Windows;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Prints for real on Windows, into a file through the printer "Microsoft Print to PDF" (no dialog, no paper). The tests
/// report themselves as skipped where that printer is not installed (Windows Server) and on Linux.
/// </summary>
[Collection("Localization")]
[SupportedOSPlatform("windows")]
public sealed class WindowsPrintToPdfTests : IDisposable
{
    private const string PdfPrinter = "Microsoft Print to PDF";

    private readonly PdfEngine _engine = new();
    private readonly string _work = Directory.CreateTempSubdirectory("pagewing-winprint-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    [Fact]
    public async Task PrintsAllPagesToPdf()
    {
        var output = await PrintAsync(new PrintSettings(), null, TestContext.Current.CancellationToken);

        await using var printed = await _engine.OpenAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, printed.PageCount);
    }

    [Fact]
    public async Task PrintsARangeAndCopiesInTheRequestedOrder()
    {
        var output = await PrintAsync(new PrintSettings { Pages = PrintPageChoice.Range, Range = "2-3", Copies = 2, Collate = true }, null, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        await using var printed = await _engine.OpenAsync(output, cancellationToken: ct);
        Assert.Equal(4, printed.PageCount);
        Assert.Contains("Seite 2", await printed.GetTextAsync(0, ct), StringComparison.Ordinal);
        Assert.Contains("Seite 3", await printed.GetTextAsync(1, ct), StringComparison.Ordinal);
        Assert.Contains("Seite 2", await printed.GetTextAsync(2, ct), StringComparison.Ordinal);
        Assert.Contains("Seite 3", await printed.GetTextAsync(3, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryPageHasThePaperSizeWhateverItsOwnFormat()
    {
        var output = await PrintAsync(new PrintSettings(), null, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        await using var printed = await _engine.OpenAsync(output, cancellationToken: ct);
        var first = await printed.GetPageSizeAsync(0, ct);
        for (var i = 1; i < printed.PageCount; i++)
        {
            Assert.Equal(first, await printed.GetPageSizeAsync(i, ct));
        }
    }

    [Fact]
    public async Task PrintingReportsProgressForEveryPage()
    {
        var reports = new List<PrintProgress>();

        await PrintAsync(new PrintSettings(), new Progress(reports), TestContext.Current.CancellationToken);

        Assert.Equal(4, reports.Count);
        Assert.Equal(4, reports[^1].Done);
        Assert.Equal(4, reports[^1].Total);
        Assert.Empty(reports[^1].FailedPages);
    }

    [Fact]
    public async Task CancellingStopsTheJob()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new Progress([], _ => cancel.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrintAsync(new PrintSettings(), progress, cancel.Token));
    }

    private async Task<string> PrintAsync(PrintSettings settings, IProgress<PrintProgress>? progress, CancellationToken cancellationToken)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Printing needs Windows.");
        var output = Path.Combine(_work, "printed.pdf");
        WindowsPrintTicket ticket;
        try
        {
            ticket = WindowsPrintService.CreateTicket(PdfPrinter, settings, output);
        }
        catch (PrintException)
        {
            Assert.Skip($"The printer \"{PdfPrinter}\" is not installed.");
            throw;
        }

        using (ticket)
        {
            await using var document = await _engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: TestContext.Current.CancellationToken);
            var service = new WindowsPrintService();
            var job = new PrintJobRequest(document, TestData.Pdf("print-mixed.pdf"), "print-mixed.pdf", 0, ticket);
            await service.PrintAsync(job, progress ?? new Progress([]), cancellationToken);
        }

        return output;
    }

    private sealed class Progress(List<PrintProgress> reports, Action<PrintProgress>? onReport = null) : IProgress<PrintProgress>
    {
        public void Report(PrintProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }

            onReport?.Invoke(value);
        }
    }
}
