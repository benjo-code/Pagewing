using System.Globalization;
using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Printing;

namespace Pagewing.App.ViewModels;

/// <summary>Where a print job is.</summary>
public enum PrintJobState
{
    /// <summary>The job is running.</summary>
    Running,

    /// <summary>The job went to the printer.</summary>
    Done,

    /// <summary>The job failed, or a page could not be printed.</summary>
    Failed,

    /// <summary>A message only, without a job (no printer, printing forbidden, a job already running).</summary>
    Notice,
}

/// <summary>
/// What the print card at the bottom of the document area shows: the progress of the job of a window, how it ended, or a
/// message. "Cancel" stops a running job; once it is over the same button reads "Close". UI thread only.
/// </summary>
public sealed class PrintJobViewModel : ViewModelBase
{
    private const int MaxFailedPagesShown = 5;

    private readonly Action _cancel;
    private readonly Action<PrintJobViewModel> _dismiss;
    private PrintJobState _state;
    private string _text;
    private int _done;
    private int _total;

    /// <summary>Creates the view model of a job that starts running.</summary>
    /// <param name="tab">The tab the job prints; closing or moving it cancels the job.</param>
    /// <param name="total">The pages of the job.</param>
    /// <param name="cancel">Stops the job.</param>
    /// <param name="dismiss">Removes the card.</param>
    public PrintJobViewModel(TabViewModel? tab, int total, Action cancel, Action<PrintJobViewModel> dismiss)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentNullException.ThrowIfNull(dismiss);
        Tab = tab;
        _total = total;
        _cancel = cancel;
        _dismiss = dismiss;
        _state = PrintJobState.Running;
        _text = Strings.PrintSending;
        ActionCommand = new RelayCommand(Act);
    }

    /// <summary>Creates a message without a job.</summary>
    internal PrintJobViewModel(string message, Action<PrintJobViewModel> dismiss)
        : this(null, 0, static () => { }, dismiss)
    {
        _state = PrintJobState.Notice;
        _text = message;
    }

    /// <summary>Gets the tab the job prints, or <see langword="null"/> for a message.</summary>
    public TabViewModel? Tab { get; }

    /// <summary>Gets where the job is.</summary>
    public PrintJobState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(ActionText));
                OnPropertyChanged(nameof(ShowsProgress));
            }
        }
    }

    /// <summary>Gets whether the job is still running.</summary>
    public bool IsRunning => _state == PrintJobState.Running;

    /// <summary>Gets whether the card shows a failure (with the warning symbol and the text in the danger colour).</summary>
    public bool IsFailed => _state == PrintJobState.Failed;

    /// <summary>Gets whether the card shows the bar of a running job.</summary>
    public bool ShowsProgress => _state == PrintJobState.Running && _total > 0;

    /// <summary>Gets the text of the card.</summary>
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    /// <summary>Gets the pages handled so far.</summary>
    public int Done
    {
        get => _done;
        private set => SetProperty(ref _done, value);
    }

    /// <summary>Gets the pages of the job.</summary>
    public int Total
    {
        get => _total;
        private set => SetProperty(ref _total, value);
    }

    /// <summary>Gets the text of the button: "Cancel printing" while the job runs, "Close" afterwards.</summary>
    public string ActionText => IsRunning ? Strings.PrintJobCancel : Strings.PrintJobClose;

    /// <summary>The button of the card: cancels a running job, otherwise removes the card.</summary>
    public ICommand ActionCommand { get; }

    /// <summary>Takes a report of the job; ignored once the job is over.</summary>
    public void Report(PrintProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!IsRunning)
        {
            return;
        }

        Total = progress.Total;
        Done = progress.Done;
        OnPropertyChanged(nameof(ShowsProgress));
        Text = progress.Done == 0 || progress.Done >= progress.Total
            ? Strings.PrintSending
            : string.Format(Strings.Culture, CompositeFormat.Parse(Strings.PrintRunning), progress.Done + 1, progress.Total);
    }

    /// <summary>The job went to the printer; pages that could not be printed are named.</summary>
    public void Complete(IReadOnlyList<int> failedPages)
    {
        ArgumentNullException.ThrowIfNull(failedPages);
        if (!IsRunning)
        {
            return;
        }

        if (failedPages.Count == 0)
        {
            Done = Total;
            Text = Strings.PrintDone;
            State = PrintJobState.Done;
        }
        else
        {
            Text = FailedPagesText(failedPages);
            State = PrintJobState.Failed;
        }
    }

    /// <summary>The job was cancelled by the user.</summary>
    public void Cancelled()
    {
        if (IsRunning)
        {
            Text = Strings.PrintCancelled;
            State = PrintJobState.Notice;
        }
    }

    /// <summary>The job failed as a whole.</summary>
    public void Fail(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (IsRunning)
        {
            Text = message;
            State = PrintJobState.Failed;
        }
    }

    private static string FailedPagesText(IReadOnlyList<int> failedPages)
    {
        var pages = failedPages.Select(page => page + 1).Order().ToList();
        var shown = string.Join(", ", pages.Take(MaxFailedPagesShown).Select(page => page.ToString(CultureInfo.InvariantCulture)));
        if (pages.Count > MaxFailedPagesShown)
        {
            shown += " …";
        }

        var format = pages.Count == 1 ? Strings.PrintFailedPage : Strings.PrintFailedPages;
        return string.Format(Strings.Culture, CompositeFormat.Parse(format), shown);
    }

    private void Act()
    {
        if (IsRunning)
        {
            _cancel();
        }
        else
        {
            _dismiss(this);
        }
    }
}
