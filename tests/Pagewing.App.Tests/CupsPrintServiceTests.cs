using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Pagewing.App.Platform.Linux;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

[Collection("Localization")]
[SupportedOSPlatform("linux")]
public sealed class CupsPrintServiceTests
{
    /// <summary>Names a CUPS-PDF queue to print through for real (its output folder is <see cref="OutputVariable"/>).</summary>
    private const string PrinterVariable = "PAGEWING_TEST_CUPS_PRINTER";

    /// <summary>The folder CUPS-PDF writes its files to.</summary>
    private const string OutputVariable = "PAGEWING_TEST_CUPS_OUTPUT";

    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);

    private readonly PdfEngine _engine = new();

    [Fact]
    public void CupsStructuresHaveTheNativeSize()
    {
        Assert.Equal(16, Unsafe.SizeOf<CupsOption>());
        Assert.Equal(32, Unsafe.SizeOf<CupsDest>());
    }

    [Fact]
    public void AMissingLibraryIsReportedNotThrown()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "CUPS is the Linux print system.");

        // Either way the answer comes without an exception; without libcups the service says it is unavailable.
        var available = new CupsPrintService().IsAvailable;

        Assert.Equal(available, new CupsPrintService().IsAvailable);
    }

    [Fact]
    public async Task PrintsARangeWithCopiesThroughCups()
    {
        var (service, printer, output) = RealPrinterOrSkip();
        var ct = TestContext.Current.CancellationToken;
        var name = $"pwtest-{Guid.NewGuid():N}";
        await using var document = await _engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: ct);
        var settings = new PrintSettings { Printer = printer, Pages = PrintPageChoice.Range, Range = "1-2", Copies = 2, Collate = true };
        using var ticket = new PrintTicket(settings);
        var reports = new List<PrintProgress>();

        await service.PrintAsync(new PrintJobRequest(document, TestData.Pdf("print-mixed.pdf"), name, 0, ticket), new Collect(reports), ct);

        Assert.Equal(4, (await ReadOutputAsync(output, name, ct)).PageCount);
        Assert.Equal(2, reports.Count);
        Assert.Equal(4, reports[^1].Done);
    }

    [Fact]
    public async Task PrintsAnEncryptedFileWithoutLeavingACopy()
    {
        var (service, printer, output) = RealPrinterOrSkip();
        var ct = TestContext.Current.CancellationToken;
        var name = $"pwtest-{Guid.NewGuid():N}";
        await using var document = await _engine.OpenAsync(TestData.Pdf("encrypted_hello_world_r6.pdf"), "hôtel", ct);
        using var ticket = new PrintTicket(new PrintSettings { Printer = printer });
        var temporaryBefore = Directory.GetDirectories(Path.GetTempPath(), "pagewing-print-*").Length;

        await service.PrintAsync(new PrintJobRequest(document, TestData.Pdf("encrypted_hello_world_r6.pdf"), name, 0, ticket), new Collect([]), ct);

        var printed = await ReadOutputAsync(output, name, ct);
        Assert.Equal(document.PageCount, printed.PageCount);
        Assert.Contains("Hello", await printed.GetTextAsync(0, ct), StringComparison.Ordinal);
        Assert.Equal(temporaryBefore, Directory.GetDirectories(Path.GetTempPath(), "pagewing-print-*").Length);
    }

    [Fact]
    public async Task ADocumentThatForbidsPrintingIsRefusedAndNothingIsSent()
    {
        var (service, printer, output) = RealPrinterOrSkip();
        var ct = TestContext.Current.CancellationToken;
        var name = $"pwtest-{Guid.NewGuid():N}";
        await using var document = await _engine.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"), "hôtel", ct);
        using var ticket = new PrintTicket(new PrintSettings { Printer = printer });

        await Assert.ThrowsAsync<PrintException>(
            () => service.PrintAsync(new PrintJobRequest(document, TestData.Pdf("encrypted_hello_world_r3.pdf"), name, 0, ticket), new Collect([]), ct));

        await Task.Delay(1500, ct);
        Assert.Empty(Directory.GetFiles(output, $"{name}*"));
    }

    [Fact]
    public async Task ListsThePrinters()
    {
        var (service, printer, _) = RealPrinterOrSkip();

        var printers = await service.GetPrintersAsync(TestContext.Current.CancellationToken);

        Assert.Contains(printers, p => p.Name == printer);
    }

    [Fact]
    public async Task AnUnknownPrinterFails()
    {
        var (service, _, _) = RealPrinterOrSkip();
        var ct = TestContext.Current.CancellationToken;
        await using var document = await _engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: ct);
        using var ticket = new PrintTicket(new PrintSettings { Printer = "no-such-printer-pagewing" });

        await Assert.ThrowsAsync<PrintException>(
            () => service.PrintAsync(new PrintJobRequest(document, TestData.Pdf("print-mixed.pdf"), "x", 0, ticket), new Collect([]), ct));
    }

    private static (CupsPrintService Service, string Printer, string Output) RealPrinterOrSkip()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "CUPS is the Linux print system.");
        var printer = Environment.GetEnvironmentVariable(PrinterVariable);
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(printer) || string.IsNullOrEmpty(output), $"Set {PrinterVariable} and {OutputVariable} to print through a CUPS-PDF queue.");
        var service = new CupsPrintService();
        Assert.SkipUnless(service.IsAvailable, "libcups is not installed.");
        return (service, printer!, output!);
    }

    private async Task<PdfDocument> ReadOutputAsync(string folder, string name, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var file in Directory.GetFiles(folder, $"{name}*.pdf"))
            {
                try
                {
                    return await _engine.OpenAsync(file, cancellationToken: cancellationToken);
                }
                catch (PdfOpenException)
                {
                    // CUPS-PDF has not finished writing the file yet.
                }
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"CUPS-PDF wrote no file for {name} in {folder}.");
    }

    private sealed class Collect(List<PrintProgress> reports) : IProgress<PrintProgress>
    {
        public void Report(PrintProgress value) => reports.Add(value);
    }
}
