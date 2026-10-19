using Pagewing.Core.Pdf;

namespace Pagewing.Core.Printing;

/// <summary>A printer the system offers.</summary>
/// <param name="Name">The name the system knows it by.</param>
/// <param name="Description">A longer description such as the location, or <see langword="null"/>.</param>
/// <param name="IsDefault">Whether it is the printer of the system.</param>
/// <param name="SupportsDuplex">Whether it prints on both sides.</param>
public sealed record PrinterInfo(string Name, string? Description, bool IsDefault, bool SupportsDuplex);

/// <summary>What the print dialog needs to know.</summary>
/// <param name="PageCount">Pages of the document.</param>
/// <param name="CurrentPage">The page the user is looking at, 0-based.</param>
/// <param name="DocumentName">The file name, shown to the printer as the name of the job.</param>
/// <param name="OwnerWindow">The native handle of the window that owns the dialog, or 0.</param>
/// <param name="Defaults">What the dialog starts with.</param>
public sealed record PrintRequest(int PageCount, int CurrentPage, string DocumentName, nint OwnerWindow, PrintSettings Defaults);

/// <summary>
/// What the user confirmed. A service may derive from it to carry platform state (a device context); the owner of the
/// ticket disposes it after the job, whether it ran, failed or was cancelled.
/// </summary>
public class PrintTicket : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="PrintTicket"/> class.</summary>
    /// <param name="settings">What the user chose.</param>
    public PrintTicket(PrintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings.Normalized();
    }

    /// <summary>Gets what the user chose.</summary>
    public PrintSettings Settings { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases what the ticket holds.</summary>
    /// <param name="disposing">Whether called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>A job to print.</summary>
/// <param name="Document">The open document.</param>
/// <param name="Path">The file the document was opened from, or <see langword="null"/>.</param>
/// <param name="DocumentName">The file name, shown to the printer as the name of the job.</param>
/// <param name="CurrentPage">The page the user is looking at, 0-based.</param>
/// <param name="Ticket">What the user confirmed.</param>
public sealed record PrintJobRequest(PdfDocument Document, string? Path, string DocumentName, int CurrentPage, PrintTicket Ticket);

/// <summary>Prints on the printers of the system. Windows and Linux have their own implementation in the app.</summary>
public interface IPrintService
{
    /// <summary>Gets whether printing works on this system (on Linux: CUPS is installed).</summary>
    bool IsAvailable { get; }

    /// <summary>Gets whether the system shows its own dialog (<see cref="ShowDialogAsync"/>); otherwise the app does, using <see cref="GetPrintersAsync"/>.</summary>
    bool HasSystemDialog { get; }

    /// <summary>Shows the dialog of the system.</summary>
    /// <returns>What the user confirmed, or <see langword="null"/> when they cancelled.</returns>
    /// <exception cref="PrintException">The dialog could not be shown.</exception>
    /// <exception cref="NotSupportedException">The system has no dialog (<see cref="HasSystemDialog"/> is <see langword="false"/>).</exception>
    Task<PrintTicket?> ShowDialogAsync(PrintRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the printers; may take seconds when a print server is slow.</summary>
    /// <exception cref="PrintException">The printers could not be listed.</exception>
    Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken);

    /// <summary>Prints the job; reports after each page. Cancelling stops between two pages.</summary>
    /// <exception cref="PrintException">The job failed as a whole.</exception>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    Task PrintAsync(PrintJobRequest job, IProgress<PrintProgress> progress, CancellationToken cancellationToken);
}
