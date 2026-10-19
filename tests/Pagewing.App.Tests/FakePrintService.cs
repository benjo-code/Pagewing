using Pagewing.Core.Printing;

namespace Pagewing.App.Tests;

/// <summary>A print service that prints nothing: the app tests drive the dialog, the job and the cards with it.</summary>
public sealed class FakePrintService : IPrintService
{
    private readonly List<PrintJobRequest> _jobs = [];

    public bool Available { get; set; } = true;

    public bool SystemDialog { get; set; }

    public bool Duplex { get; set; } = true;

    public IReadOnlyList<PrinterInfo> Printers { get; set; } =
    [
        new("Office", "Second floor", false),
        new("Plotter", null, true),
    ];

    /// <summary>Gets the ticket the system dialog returns; <see langword="null"/> is "Cancel".</summary>
    public PrintTicket? DialogTicket { get; set; }

    public PrintRequest? LastRequest { get; private set; }

    /// <summary>Gets what the printer list waits for before it answers, or <see langword="null"/> to answer at once.</summary>
    public TaskCompletionSource? PrintersGate { get; set; }

    /// <summary>Gets what <see cref="PrintAsync"/> does; the default reports every page and finishes.</summary>
    public Func<PrintJobRequest, IProgress<PrintProgress>, CancellationToken, Task>? OnPrint { get; set; }

    public IReadOnlyList<PrintJobRequest> Jobs => _jobs;

    public bool IsAvailable => Available;

    public bool HasSystemDialog => SystemDialog;

    public Task<PrintTicket?> ShowDialogAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(DialogTicket);
    }

    public async Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken)
    {
        if (PrintersGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return Printers;
    }

    public Task<bool> SupportsDuplexAsync(string printerName, CancellationToken cancellationToken) => Task.FromResult(Duplex && printerName == "Office");

    public async Task PrintAsync(PrintJobRequest job, IProgress<PrintProgress> progress, CancellationToken cancellationToken)
    {
        _jobs.Add(job);
        if (OnPrint is { } custom)
        {
            await custom(job, progress, cancellationToken);
            return;
        }

        var plan = PrintPlan.Create(job.Document.PageCount, job.CurrentPage, job.Ticket.Settings);
        for (var i = 0; i < plan.Sequence.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new PrintProgress(i + 1, plan.Sequence.Count, []));
        }
    }
}
