using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;

namespace Pagewing.App.Platform.Windows;

/// <summary>What the Windows print dialog returned, plus the printer device context; deleted when the ticket is disposed.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPrintTicket : PrintTicket
{
    private nint _deviceContext;

    public WindowsPrintTicket(PrintSettings settings, nint deviceContext, string? outputFile = null)
        : base(settings)
    {
        _deviceContext = deviceContext;
        OutputFile = outputFile;
    }

    /// <summary>Gets the <c>HDC</c> of the printer, or 0 once the ticket is disposed.</summary>
    public nint DeviceContext => _deviceContext;

    /// <summary>Gets the file to print into instead of the printer (a "print to file" printer such as Microsoft Print to PDF), or <see langword="null"/>.</summary>
    public string? OutputFile { get; }

    protected override void Dispose(bool disposing)
    {
        var context = Interlocked.Exchange(ref _deviceContext, 0);
        if (context != 0)
        {
            Gdi.DeleteDC(context);
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Prints on Windows: the common print dialog (<c>PrintDlgEx</c>), then PDFium draws every page on the device context of
/// the printer (<c>FPDF_RenderPage</c> with <c>FPDF_PRINTING</c>). Copies and collating are done here, not by the driver.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPrintService : IPrintService
{
    private const int MaxDocumentNameLength = 255;
    private const double PointsPerInch = 72;

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    public bool HasSystemDialog => true;

    /// <summary>
    /// Creates a ticket for a printer by name without the dialog (tests, and a printer that prints into a file).
    /// </summary>
    /// <exception cref="PrintException">The printer is unknown.</exception>
    internal static WindowsPrintTicket CreateTicket(string printerName, PrintSettings settings, string? outputFile)
    {
        var context = Gdi.CreateDC("WINSPOOL", printerName, null, 0);
        if (context == 0)
        {
            throw new PrintException(Strings.PrintErrorStart, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new WindowsPrintTicket(settings with { Printer = printerName }, context, outputFile);
    }

    /// <summary>Shows the dialog; blocks the calling (UI) thread in the modal dialog of Windows, which keeps the window procedures of the app going.</summary>
    /// <inheritdoc/>
    public Task<PrintTicket?> ShowDialogAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var result = PrintDialogNative.Show(request.OwnerWindow, request.PageCount, request.Defaults);
        return Task.FromResult<PrintTicket?>(result is null
            ? null
            : new WindowsPrintTicket(result.Settings with { Scaling = request.Defaults.Scaling }, result.DeviceContext));
    }

    /// <inheritdoc/>
    /// <remarks>Windows lists the printers in its own dialog; this list is empty.</remarks>
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PrinterInfo>>([]);

    /// <inheritdoc/>
    /// <remarks>Windows asks in its own dialog; the answer here is not used.</remarks>
    public Task<bool> SupportsDuplexAsync(string printerName, CancellationToken cancellationToken) => Task.FromResult(true);

    /// <inheritdoc/>
    public Task PrintAsync(PrintJobRequest job, IProgress<PrintProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        if (job.Ticket is not WindowsPrintTicket ticket)
        {
            throw new ArgumentException("The ticket does not come from this print service.", nameof(job));
        }

        // The device context belongs to one thread for the whole job; PDFium draws on it from its own thread while this
        // one waits, so the context is never used by two threads at once. The thread waits on PDFium, never the UI thread.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Run(job, ticket, progress, cancellationToken);
                completion.SetResult();
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Pagewing.Print",
        };
        thread.Start();
        return completion.Task;
    }

    private static void Run(PrintJobRequest job, WindowsPrintTicket ticket, IProgress<PrintProgress> progress, CancellationToken cancellationToken)
    {
        var document = job.Document;
        var info = document.GetPrintInfoAsync(cancellationToken).WaitAsync(cancellationToken).GetAwaiter().GetResult();
        if (!info.PrintAllowed)
        {
            throw new PrintException(Strings.PrintForbidden);
        }

        var settings = ticket.Settings;
        var plan = PrintPlan.Create(document.PageCount, job.CurrentPage, settings);
        var context = ticket.DeviceContext;
        var dpiX = Gdi.GetDeviceCaps(context, Gdi.LogicalPixelsX);
        var dpiY = Gdi.GetDeviceCaps(context, Gdi.LogicalPixelsY);
        var areaWidth = Gdi.GetDeviceCaps(context, Gdi.HorizontalResolution);
        var areaHeight = Gdi.GetDeviceCaps(context, Gdi.VerticalResolution);
        if (dpiX <= 0 || dpiY <= 0 || areaWidth <= 0 || areaHeight <= 0)
        {
            throw new PrintException(Strings.PrintErrorStart);
        }

        var area = new PaperArea(areaWidth * PointsPerInch / dpiX, areaHeight * PointsPerInch / dpiY);

        var started = false;
        var finished = false;
        var failed = new List<int>();
        var sizes = new Dictionary<int, PdfPageSize>();
        try
        {
            StartDocument(context, job.DocumentName, ticket.OutputFile);
            started = true;

            for (var done = 0; done < plan.Sequence.Count; done++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = plan.Sequence[done];
                if (!PrintPage(job, context, index, settings, area, dpiX, dpiY, sizes, cancellationToken))
                {
                    if (!failed.Contains(index))
                    {
                        failed.Add(index);
                    }
                }

                progress.Report(new PrintProgress(done + 1, plan.Sequence.Count, failed.ToArray()));
            }

            if (Gdi.EndDoc(context) <= 0)
            {
                throw new PrintException(Strings.PrintErrorPage, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            finished = true;
        }
        finally
        {
            if (started && !finished)
            {
                Gdi.AbortDoc(context);
            }
        }
    }

    private static unsafe void StartDocument(nint context, string documentName, string? outputFile)
    {
        var name = documentName.Length > MaxDocumentNameLength ? documentName[..MaxDocumentNameLength] : documentName;
        fixed (char* namePointer = name)
        fixed (char* outputPointer = outputFile)
        {
            var info = new DocInfo
            {
                Size = sizeof(DocInfo),
                DocName = namePointer,
                Output = outputFile is null ? null : outputPointer,
            };
            if (Gdi.StartDoc(context, &info) <= 0)
            {
                throw new PrintException(Strings.PrintErrorStart, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    /// <summary>Prints one page; returns <see langword="false"/> when the page itself could not be drawn (it is skipped).</summary>
    private static bool PrintPage(
        PrintJobRequest job,
        nint context,
        int index,
        PrintSettings settings,
        PaperArea area,
        int dpiX,
        int dpiY,
        Dictionary<int, PdfPageSize> sizes,
        CancellationToken cancellationToken)
    {
        if (Gdi.StartPage(context) <= 0)
        {
            throw new PrintException(Strings.PrintErrorPage, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        var drawn = true;
        try
        {
            if (!sizes.TryGetValue(index, out var size))
            {
                size = job.Document.GetPageSizeAsync(index, cancellationToken).WaitAsync(cancellationToken).GetAwaiter().GetResult();
                sizes[index] = size;
            }

            var placement = PrintPlacement.Plan(size, area, settings.Scaling).ToDevice(dpiX, dpiY);
            job.Document.PrintPageToDeviceAsync(index, context, placement, cancellationToken).WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is PdfRenderException or InvalidDataException or ArgumentOutOfRangeException)
        {
            drawn = false;
        }

        // A page that was started is always ended, so the spooler never gets a half page.
        if (Gdi.EndPage(context) <= 0)
        {
            throw new PrintException(Strings.PrintErrorPage, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        return drawn;
    }
}
