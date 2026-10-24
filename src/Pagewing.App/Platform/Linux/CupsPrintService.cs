using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pagewing.App.Resources;
using Pagewing.Core.Printing;

namespace Pagewing.App.Platform.Linux;

/// <summary>
/// Prints on Linux through CUPS (libcups): the file goes to the printer as it is, with the choices as job options, and
/// the PDF filter of CUPS does the page ranges, copies and scaling. An encrypted document goes as a decrypted copy in a
/// private temporary folder, which is deleted afterwards.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class CupsPrintService : IPrintService
{
    /// <inheritdoc/>
    public bool IsAvailable => CupsNative.IsAvailable;

    /// <inheritdoc/>
    public bool HasSystemDialog => false;

    /// <inheritdoc/>
    public Task<PrintTicket?> ShowDialogAsync(PrintRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("CUPS has no dialog of its own; the app shows one.");

    /// <inheritdoc/>
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();

        // The call may take seconds when a print server is slow and cannot be cancelled; the caller drops a late result.
        return Task.Run<IReadOnlyList<PrinterInfo>>(ListPrinters, CancellationToken.None).WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> SupportsDuplexAsync(string printerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(printerName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return Task.FromResult(false);
        }

        return Task.Run(() => CheckDuplex(printerName), CancellationToken.None).WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task PrintAsync(PrintJobRequest job, IProgress<PrintProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();

        var document = job.Document;
        var info = await document.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false);
        if (!info.PrintAllowed)
        {
            throw new PrintException(Strings.PrintForbidden);
        }

        var settings = job.Ticket.Settings;
        var plan = PrintPlan.Create(document.PageCount, job.CurrentPage, settings);
        var options = CupsOptions.Build(settings, plan);
        var printer = settings.Printer.Length > 0 ? settings.Printer : await DefaultPrinterAsync(cancellationToken).ConfigureAwait(false);

        string? temporaryFolder = null;
        try
        {
            var file = job.Path;
            if (info.IsEncrypted || file is null)
            {
                // CUPS cannot ask for a password, and a file with unsaved changes (no path) lacks them: it gets a copy written
                // from the document in memory, without encryption, readable by the user only.
                temporaryFolder = Directory.CreateTempSubdirectory("pagewing-print-").FullName;
                File.SetUnixFileMode(temporaryFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                file = Path.Combine(temporaryFolder, "document.pdf");
                await document.SaveDecryptedCopyAsync(file, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new PrintProgress(0, plan.PageCountToPrint, []));

            // The call reads the whole file and sends it before it returns; it cannot be cancelled once it runs.
            await Task.Run(() => Submit(printer, file, job.DocumentName, options), CancellationToken.None).ConfigureAwait(false);
            progress.Report(new PrintProgress(plan.PageCountToPrint, plan.PageCountToPrint, []));
        }
        finally
        {
            if (temporaryFolder is not null)
            {
                try
                {
                    Directory.Delete(temporaryFolder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A leftover temporary file is not worth failing a job that was sent.
                }
            }
        }
    }

    private static void EnsureAvailable()
    {
        if (!CupsNative.IsAvailable)
        {
            throw new PrintException(Strings.PrintNoService);
        }
    }

    private static async Task<string> DefaultPrinterAsync(CancellationToken cancellationToken)
    {
        var printers = await new CupsPrintService().GetPrintersAsync(cancellationToken).ConfigureAwait(false);
        return (printers.FirstOrDefault(printer => printer.IsDefault) ?? (printers.Count > 0 ? printers[0] : null))?.Name
            ?? throw new PrintException(Strings.PrintNoPrinter);
    }

    private static unsafe List<PrinterInfo> ListPrinters()
    {
        CupsDest* destinations = null;
        var count = CupsNative.cupsGetDests2(0, &destinations);
        try
        {
            var printers = new List<PrinterInfo>(Math.Max(count, 0));
            for (var i = 0; i < count; i++)
            {
                var destination = destinations + i;
                var name = Marshal.PtrToStringUTF8((nint)destination->Name);
                if (string.IsNullOrEmpty(name) || destination->Instance != null)
                {
                    // A named instance (a saved preset of a printer) is not a printer of its own.
                    continue;
                }

                printers.Add(new PrinterInfo(name, OptionValue(destination, "printer-info"), destination->IsDefault != 0));
            }

            return printers;
        }
        finally
        {
            CupsNative.cupsFreeDests(count, destinations);
        }
    }

    private static unsafe string? OptionValue(CupsDest* destination, string name)
    {
        for (var i = 0; i < destination->OptionCount; i++)
        {
            var option = destination->Options + i;
            if (Marshal.PtrToStringUTF8((nint)option->Name) == name)
            {
                var value = Marshal.PtrToStringUTF8((nint)option->Value);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static unsafe bool CheckDuplex(string printerName)
    {
        var destination = CupsNative.cupsGetNamedDest(0, printerName, null);
        if (destination == null)
        {
            return false;
        }

        try
        {
            var info = CupsNative.cupsCopyDestInfo(0, destination);
            if (info == 0)
            {
                return false;
            }

            try
            {
                return CupsNative.cupsCheckDestSupported(0, destination, info, "sides", "two-sided-long-edge") != 0;
            }
            finally
            {
                CupsNative.cupsFreeDestInfo(info);
            }
        }
        finally
        {
            CupsNative.cupsFreeDests(1, destination);
        }
    }

    private static unsafe void Submit(string printer, string file, string title, IReadOnlyList<(string Name, string Value)> options)
    {
        CupsOption* native = null;
        var count = 0;
        try
        {
            foreach (var (name, value) in options)
            {
                count = CupsNative.cupsAddOption(name, value, count, &native);
            }

            var id = CupsNative.cupsPrintFile2(0, printer, file, title, count, native);
            if (id == 0)
            {
                var message = Marshal.PtrToStringUTF8((nint)CupsNative.cupsLastErrorString());
                throw new PrintException(string.IsNullOrWhiteSpace(message) ? Strings.PrintErrorStart : message);
            }
        }
        finally
        {
            CupsNative.cupsFreeOptions(count, native);
        }
    }
}
