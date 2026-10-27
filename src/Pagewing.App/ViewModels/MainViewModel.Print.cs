using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Printing of a window (Ctrl+P): the dialog (the system's on Windows, the card <see cref="OverlayKind.Print"/> elsewhere),
/// one job at a time with its card at the bottom of the document area, and the cleaning up when its tab leaves. The print
/// service is made on the first print, never at startup.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How long a finished job or a message stays on the card before it goes by itself.</summary>
    internal static readonly TimeSpan PrintCardLifetime = TimeSpan.FromSeconds(5);

    private IPrintService? _printService;
    private bool _printServiceMade;
    private PrintViewModel? _print;
    private PrintJobViewModel? _printJob;
    private CancellationTokenSource? _printCancellation;
    private ITimer? _printCardTimer;
    private RelayCommand? _printCommand;

    /// <summary>Gets what makes the print service; tests put a fake in. Called once, on the first print.</summary>
    internal Func<IPrintService?> PrintServiceFactory { get; set; } = PlatformPrintServices.Create;

    /// <summary>Gets what gives the native handle of the window for the dialog of the system; the window sets it.</summary>
    internal Func<nint> OwnerWindowHandle { get; set; } = static () => 0;

    /// <summary>Ctrl+P: asks what to print and prints the active tab's document.</summary>
    public ICommand PrintCommand => _printCommand ??= new RelayCommand(() => _ = PrintAsync(), CanPrint);

    /// <summary>
    /// Gets the view model of the print dialog while <see cref="OverlayKind.Print"/> is shown, or <see langword="null"/>
    /// (a new one is made for each showing).
    /// </summary>
    public PrintViewModel? Print => _print;

    /// <summary>Gets the card of the running or just finished job, or a message; <see langword="null"/> when there is none.</summary>
    public PrintJobViewModel? PrintJob
    {
        get => _printJob;
        private set
        {
            if (!ReferenceEquals(_printJob, value))
            {
                _printJob = value;
                OnPropertyChanged();
            }
        }
    }

    private IPrintService? PrintService
    {
        get
        {
            if (!_printServiceMade)
            {
                _printService = PrintServiceFactory();
                _printServiceMade = true;
            }

            return _printService;
        }
    }

    // Not while the tab saves: the save swaps the document a job would print.
    private bool CanPrint() => ActiveTab is { IsReady: true, Document: not null, IsSaving: false, IsAskedAbout: false };

    private async Task PrintAsync()
    {
        if (ActiveTab is not { IsReady: true, Document: { } document, IsSaving: false } tab)
        {
            return;
        }

        // A nudge of the selection that waits for the keys to rest is made before the job: during it the tab takes no edit.
        tab.FlushNudge();
        try
        {
            if (_printJob is { IsRunning: true })
            {
                ShowPrintMessage(Strings.PrintBusy);
                return;
            }

            if (PrintService is not { IsAvailable: true } service)
            {
                ShowPrintMessage(Strings.PrintNoService);
                return;
            }

            var info = await document.GetPrintInfoAsync();
            if (!info.PrintAllowed)
            {
                ShowPrintMessage(Strings.PrintForbidden);
                return;
            }

            var defaults = new PrintSettings
            {
                Printer = Settings.Current.LastPrinter ?? string.Empty,
                Scaling = Settings.Current.PrintScaling,
            };
            var current = Math.Max(0, tab.CurrentPage - 1);
            if (service.HasSystemDialog)
            {
                var request = new PrintRequest(document.PageCount, current, tab.FileName ?? string.Empty, OwnerWindowHandle(), defaults);
                var ticket = await service.ShowDialogAsync(request, CancellationToken.None);
                if (ticket is not null)
                {
                    // The document the tab has now: a save while the dialog was open swapped it for the written file.
                    await RunPrintJobAsync(service, tab, tab.Document ?? document, ticket);
                }

                return;
            }

            ShowPrintDialog(service, tab, document, current, defaults);
        }
        catch (PrintException ex)
        {
            ShowPrintFailure(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException or PdfRenderException or InvalidOperationException)
        {
            ShowPrintFailure(Strings.PrintErrorStart);
        }
    }

    private void ShowPrintDialog(IPrintService service, TabViewModel tab, PdfDocument document, int current, PrintSettings defaults)
    {
        var dialog = new PrintViewModel(service, document.PageCount, current, defaults);
        dialog.Cancelled += HideOverlay;
        dialog.Confirmed += ticket =>
        {
            HideOverlay();

            // The document the tab has now: a save while the dialog was open swapped it for the written file.
            _ = RunPrintJobAsync(service, tab, tab.Document ?? document, ticket);
        };
        _print?.Dispose();
        _print = dialog;
        OnPropertyChanged(nameof(Print));
        ShowOverlay(OverlayKind.Print);
        dialog.OnOpened();
    }

    private async Task RunPrintJobAsync(IPrintService service, TabViewModel tab, PdfDocument document, PrintTicket ticket)
    {
        using (ticket)
        {
            RememberPrintChoices(ticket.Settings);
            using var cancellation = new CancellationTokenSource();
            _printCancellation = cancellation;
            StopPrintCardTimer();
            var job = new PrintJobViewModel(tab, 0, cancellation.Cancel, DismissPrintJob);
            PrintJob = job;
            var progress = new JobProgress(job);

            // The job prints this document: a save would close it, so the tab cannot be saved until the job ends. A nudge
            // begun while the dialog was open is made first.
            tab.FlushNudge();
            tab.IsPrinting = true;
            try
            {
                // A value applied a moment ago (the open value of a form field, the text of a text box field) is still being
                // written: the job waits for it, so the page prints what the user typed. The tab takes no new edit meanwhile.
                await tab.WhenEditsSettledAsync();

                // With changes that are not saved (or still being made) the file on disk lacks them: no path, so a service that
                // hands a file to the printing system (CUPS) writes one from the document in memory instead.
                var path = tab.AsksBeforeClosing ? null : tab.FullPath;
                var request = new PrintJobRequest(document, path, tab.FileName ?? string.Empty, Math.Max(0, tab.CurrentPage - 1), ticket);
                await service.PrintAsync(request, progress, cancellation.Token);
                job.Complete(progress.FailedPages);
            }
            catch (OperationCanceledException)
            {
                job.Cancelled();
            }
            catch (PrintException ex)
            {
                job.Fail(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException or PdfRenderException or InvalidOperationException or ArgumentOutOfRangeException or FormatException)
            {
                job.Fail(Strings.PrintErrorStart);
            }
            finally
            {
                _printCancellation = null;
                tab.IsPrinting = false;
            }

            if (!job.IsFailed && !_disposed && ReferenceEquals(_printJob, job))
            {
                StartPrintCardTimer(job);
            }
        }
    }

    /// <summary>
    /// Takes the reports of a job from any thread. The failed pages are kept at once, not posted with the rest, because the
    /// job's end reads them right after the service returned, which may be before a posted report ran on the UI thread.
    /// </summary>
    private sealed class JobProgress(PrintJobViewModel job) : IProgress<PrintProgress>
    {
        private volatile IReadOnlyList<int> _failedPages = [];

        public IReadOnlyList<int> FailedPages => _failedPages;

        public void Report(PrintProgress value)
        {
            _failedPages = value.FailedPages;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => job.Report(value));
        }
    }

    private void RememberPrintChoices(PrintSettings settings)
    {
        var printer = settings.Printer.Length > 0 ? settings.Printer : null;
        if (printer is null && Settings.Current.PrintScaling == settings.Scaling)
        {
            return;
        }

        Settings.Update(current => current with { LastPrinter = printer ?? current.LastPrinter, PrintScaling = settings.Scaling });
    }

    private void ShowPrintMessage(string message)
    {
        StopPrintCardTimer();
        var notice = new PrintJobViewModel(message, DismissPrintJob);
        PrintJob = notice;
        StartPrintCardTimer(notice);
    }

    private void ShowPrintFailure(string message)
    {
        StopPrintCardTimer();
        var job = new PrintJobViewModel(null, 0, static () => { }, DismissPrintJob);
        job.Fail(message);
        PrintJob = job;
    }

    private void DismissPrintJob(PrintJobViewModel job)
    {
        if (ReferenceEquals(_printJob, job) && !job.IsRunning)
        {
            StopPrintCardTimer();
            PrintJob = null;
        }
    }

    private void StartPrintCardTimer(PrintJobViewModel job)
    {
        StopPrintCardTimer();
        _printCardTimer = Clock.CreateTimer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => DismissPrintJob(job)),
            null,
            PrintCardLifetime,
            Timeout.InfiniteTimeSpan);
    }

    private void StopPrintCardTimer()
    {
        _printCardTimer?.Dispose();
        _printCardTimer = null;
    }

    /// <summary>A tab leaves the window (closed or moved): a job that prints it is cancelled, and so is its dialog.</summary>
    private void CancelPrintingOf(TabViewModel tab)
    {
        if (_printJob is { IsRunning: true } job && ReferenceEquals(job.Tab, tab))
        {
            _printCancellation?.Cancel();
        }

        if (Overlay == OverlayKind.Print)
        {
            HideOverlay();
        }
    }

    private void DisposePrinting()
    {
        _printCancellation?.Cancel();
        StopPrintCardTimer();
        _print?.Dispose();
    }
}
