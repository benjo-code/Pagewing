using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.App.ViewModels;

/// <summary>Where a card of extracting, splitting or exporting is.</summary>
public enum PageOutputState
{
    /// <summary>The user chooses the way, the target and the options.</summary>
    Setup,

    /// <summary>The card reads or checks something before it shows the review (what the new files lose, what is in the way).</summary>
    Checking,

    /// <summary>The card shows the files, what they lose and what is in the way; "Create files" starts.</summary>
    Review,

    /// <summary>The files are being written; the bar moves and "Cancel" stops the run at the next file.</summary>
    Running,

    /// <summary>The run ended: written, canceled or failed; <see cref="PageOutputViewModel.ResultText"/> says which.</summary>
    Finished,
}

/// <summary>
/// What the cards of "Extract selected pages", "Split document" and "Export pages as images" share (milestone 13, spec 5.3):
/// the target folder, the one choice that lets existing files be replaced (off), the run on the thread pool with its progress
/// and its cancel, and the words for what ended it. They read the open document without the plan and write only new files;
/// the original is never touched. The card is the job: it ends when the card is closed, and a running job keeps the card
/// (<see cref="IsRunning"/>). UI thread only; <see cref="Dispose"/> cancels a running job.
/// </summary>
public abstract class PageOutputViewModel : ViewModelBase, IDisposable
{
    // More files than this in a sentence end in an ellipsis; the card stays readable.
    private const int MaxNamesInText = 5;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<string?, Task<string?>> _pickFolder;
    private readonly RelayCommand _chooseFolderCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _closeCommand;
    private PageOutputState _state;
    private string _folder;
    private string? _folderError;
    private bool _replaceExisting;
    private double _progress;
    private string? _resultText;
    private bool _isError;
    private string? _writtenText;
    private bool _running;
    private bool _picking;

    /// <summary>Creates the card for the document of <paramref name="source"/>.</summary>
    /// <param name="source">The open document with its file and the selection of the list.</param>
    /// <param name="pickFolder">Shows the folder dialog (starting in the folder it is given); <see langword="null"/> when cancelled.</param>
    /// <param name="initial">The state the card starts in.</param>
    private protected PageOutputViewModel(PageOutputSource source, Func<string?, Task<string?>> pickFolder, PageOutputState initial)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pickFolder);
        Source = source;
        _pickFolder = pickFolder;
        _state = initial;
        _folder = source.DocumentFolder;
        _chooseFolderCommand = new RelayCommand(() => _ = ChooseFolderAsync(), () => _state is PageOutputState.Setup && !_picking);
        _cancelCommand = new RelayCommand(Cancel);
        _closeCommand = new RelayCommand(() => Closed?.Invoke());
    }

    /// <summary>Raised when the user closes the card (also when "Cancel" is used without a run).</summary>
    internal event Action? Closed;

    /// <summary>Gets what the card reads: the open document, its file, the selection.</summary>
    internal PageOutputSource Source { get; }

    /// <summary>Gets whether the card has been disposed (the window went, the tab went, the card was closed).</summary>
    protected bool IsDisposed { get; private set; }

    /// <summary>Gets where the card is.</summary>
    public PageOutputState State
    {
        get => _state;
        protected set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsSetup));
                OnPropertyChanged(nameof(IsChecking));
                OnPropertyChanged(nameof(IsReview));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(ShowsProgress));
                _chooseFolderCommand.RaiseCanExecuteChanged();
                OnStateChanged();
            }
        }
    }

    /// <summary>Gets whether the user chooses the options.</summary>
    public bool IsSetup => _state == PageOutputState.Setup;

    /// <summary>Gets whether the card reads or checks something.</summary>
    public bool IsChecking => _state == PageOutputState.Checking;

    /// <summary>Gets whether the card shows the review.</summary>
    public bool IsReview => _state == PageOutputState.Review;

    /// <summary>Gets whether the files are being written; the window keeps the card while this is true.</summary>
    public bool IsRunning => _state == PageOutputState.Running;

    /// <summary>Gets whether the run ended.</summary>
    public bool IsFinished => _state == PageOutputState.Finished;

    /// <summary>Gets whether the bar is shown (a run, or the check before the review).</summary>
    public bool ShowsProgress => _state is PageOutputState.Running;

    /// <summary>Gets the name of the open document without its folder.</summary>
    public string FileName => Source.Name;

    /// <summary>Gets or sets the folder the new files are written into; starts as the folder of the open document.</summary>
    public string Folder
    {
        get => _folder;
        set
        {
            if (SetProperty(ref _folder, value ?? string.Empty))
            {
                FolderError = null;
                OnFolderChanged();
            }
        }
    }

    /// <summary>Gets why the folder cannot be used or chosen (it is missing, the dialog failed), or <see langword="null"/>.</summary>
    public string? FolderError
    {
        get => _folderError;
        private protected set => SetProperty(ref _folderError, value);
    }

    /// <summary>
    /// Gets or sets whether files that already exist at a target are replaced. Off by default; without it nothing that exists is
    /// ever overwritten, and the card says which files are in the way.
    /// </summary>
    public bool ReplaceExisting
    {
        get => _replaceExisting;
        set
        {
            if (SetProperty(ref _replaceExisting, value))
            {
                OnReplaceExistingChanged();
            }
        }
    }

    /// <summary>Gets how far the run is (0 to 1); never goes back.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    /// <summary>Gets the progress as a percentage for the words beside the bar.</summary>
    public string ProgressText => _progress.ToString("P0", Strings.Culture);

    /// <summary>Gets how the run ended (written, canceled, or why it failed), or <see langword="null"/> before it ended.</summary>
    public string? ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    /// <summary>Gets whether the run failed (cancel is no failure).</summary>
    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    /// <summary>Gets which files were written when a run was canceled or failed (so the user knows what exists), else <see langword="null"/>.</summary>
    public string? WrittenText
    {
        get => _writtenText;
        private set => SetProperty(ref _writtenText, value);
    }

    /// <summary>Chooses the target folder with the folder dialog.</summary>
    public ICommand ChooseFolderCommand => _chooseFolderCommand;

    /// <summary>"Cancel": stops a running job (the card then says what was written), else closes the card.</summary>
    public ICommand CancelCommand => _cancelCommand;

    /// <summary>"Close": closes the card.</summary>
    public ICommand CloseCommand => _closeCommand;

    /// <summary>Cancels a running job and lets go of the card. The document is not the card's: it stays open.</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _cancellation.Cancel();
        if (!_running)
        {
            _cancellation.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Stops a running job at its next step (the files before it stay); the card ends in <see cref="PageOutputState.Finished"/>.</summary>
    internal void CancelRun()
    {
        if (_running && !_cancellation.IsCancellationRequested)
        {
            _cancellation.Cancel();
        }
    }

    /// <summary>Called when <see cref="State"/> changed (derived cards raise their commands).</summary>
    private protected virtual void OnStateChanged()
    {
    }

    /// <summary>Called when <see cref="Folder"/> changed.</summary>
    private protected virtual void OnFolderChanged()
    {
    }

    /// <summary>Called when <see cref="ReplaceExisting"/> changed.</summary>
    private protected virtual void OnReplaceExistingChanged()
    {
    }

    /// <summary>The words for an exception that ended a run (never the text of the exception, which may name a path).</summary>
    private protected abstract string Describe(Exception exception);

    /// <summary>The words for a run that wrote <paramref name="paths"/>.</summary>
    private protected abstract string SuccessText(IReadOnlyList<string> paths);

    /// <summary>Gets the token of the card; canceled by "Cancel" and by <see cref="Dispose"/>.</summary>
    private protected CancellationToken Token => _cancellation.Token;

    /// <summary>Ends the card with an error before anything ran (a check refused it).</summary>
    private protected void Fail(string text)
    {
        IsError = true;
        ResultText = text;
        WrittenText = null;
        State = PageOutputState.Finished;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool with progress (posted to the UI thread, never going back) and the files it
    /// has written so far; ends the card in <see cref="PageOutputState.Finished"/> with the result. A run while one is running,
    /// or on a disposed card, does nothing (single flight).
    /// </summary>
    private protected async Task RunAsync(Func<IProgress<double>, ICollection<string>, CancellationToken, Task<IReadOnlyList<string>>> work)
    {
        if (IsDisposed || _running || _state == PageOutputState.Running)
        {
            return;
        }

        _running = true;
        Progress = 0;
        IsError = false;
        ResultText = null;
        WrittenText = null;
        State = PageOutputState.Running;

        // Appended to by the worker only; read here after the task ended, which orders the two.
        var written = new List<string>();
        var token = _cancellation.Token;
        var progress = new UiProgress(this);
        try
        {
            var paths = await Task.Run(() => work(progress, written, token), CancellationToken.None);
            if (!IsDisposed)
            {
                Progress = 1;
                ResultText = SuccessText(paths);
                State = PageOutputState.Finished;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || token.IsCancellationRequested)
        {
            // Canceled by the user (the failure of a part that was canceled counts the same): what was written stays, and is told.
            if (!IsDisposed)
            {
                ResultText = Strings.PageOutputCanceled;
                WrittenText = WrittenTextOf(written);
                State = PageOutputState.Finished;
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                IsError = true;
                ResultText = Describe(ex);
                WrittenText = WrittenTextOf(written);
                State = PageOutputState.Finished;
            }
        }
        finally
        {
            _running = false;
            if (IsDisposed)
            {
                _cancellation.Dispose();
            }
        }
    }

    /// <summary>The words for an exception that every card can meet; <see langword="null"/> for any other.</summary>
    private protected static string? DescribeCommon(Exception exception) => exception switch
    {
        ObjectDisposedException => Strings.PageOutputErrorGone,
        PageEditException or InvalidDataException => Strings.PageOutputErrorNotPossible,
        PdfOpenException => Strings.PageOutputErrorSourceChanged,
        SaveFailureException saved => SaveReasonText(saved.Reason),
        IOException or UnauthorizedAccessException => Strings.SaveErrorWriteFailed,
        _ => null,
    };

    /// <summary>The cause of a failed write in everyday words.</summary>
    private protected static string SaveReasonText(SaveFailureReason reason) => reason switch
    {
        SaveFailureReason.NotWritable => Strings.SaveErrorNotWritable,
        SaveFailureReason.DiskFull => Strings.SaveErrorDiskFull,
        SaveFailureReason.WriteFailed => Strings.SaveErrorWriteFailed,
        SaveFailureReason.VerificationFailed => Strings.SaveErrorVerificationFailed,
        SaveFailureReason.SourceModified or SaveFailureReason.OriginalChanged => Strings.PageOutputErrorSourceChanged,
        SaveFailureReason.EncryptedNotSupported => Strings.SaveErrorEncrypted,
        SaveFailureReason.TargetExists => Strings.PageOutputErrorTargetIsSource,
        _ => Strings.SaveErrorUnknown,
    };

    /// <summary>Formats a text with the interface language.</summary>
    private protected static string Format(string format, params object[] arguments) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), arguments);

    /// <summary>The file names of <paramref name="paths"/>, a few of them, as one list for a sentence.</summary>
    private protected static string NamesOf(IEnumerable<string> paths)
    {
        var names = paths.Select(Path.GetFileName).Where(static name => !string.IsNullOrEmpty(name)).ToList();
        var shown = string.Join(", ", names.Take(MaxNamesInText));
        return names.Count > MaxNamesInText ? shown + ", …" : shown;
    }

    private static string WrittenTextOf(List<string> written) =>
        written.Count == 0
            ? Strings.PageOutputNothingWritten
            : Format(Strings.PageOutputWritten, written.Count.ToString("N0", Strings.Culture), NamesOf(written));

    private void Cancel()
    {
        if (_running)
        {
            CancelRun();
        }
        else
        {
            Closed?.Invoke();
        }
    }

    private async Task ChooseFolderAsync()
    {
        _picking = true;
        _chooseFolderCommand.RaiseCanExecuteChanged();
        try
        {
            var chosen = await _pickFolder(Directory.Exists(_folder) ? _folder : Source.DocumentFolder);
            if (!string.IsNullOrEmpty(chosen) && !IsDisposed && _state == PageOutputState.Setup)
            {
                Folder = chosen;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dialog itself failed (no folder chooser portal on Linux, for example): the folder stays, and it is said.
            if (!IsDisposed && _state == PageOutputState.Setup)
            {
                FolderError = Strings.PageOutputPickFailed;
            }
        }
        finally
        {
            _picking = false;
            _chooseFolderCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Reports from any thread; the bar moves on the UI thread and never back.</summary>
    private sealed class UiProgress(PageOutputViewModel owner) : IProgress<double>
    {
        public void Report(double value)
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!owner.IsDisposed && owner._state == PageOutputState.Running)
                    {
                        owner.Progress = Math.Max(owner._progress, Math.Clamp(value, 0, 1));
                    }
                });
            }
            catch (Exception)
            {
                // The dispatcher is shutting down: nobody sees the progress.
            }
        }
    }
}

/// <summary>What a card of extracting, splitting or exporting reads: the document the user sees, its file and the selection.</summary>
/// <param name="Document">The open document.</param>
/// <param name="File">Its file, with password and the stamp taken when it was opened (never shown).</param>
/// <param name="Selection">The 0-based pages selected in the list, ascending, empty without a selection.</param>
/// <param name="Engine">Opens the working copies and the written files.</param>
internal sealed record PageOutputSource(PdfDocument Document, PageSourceFile File, IReadOnlyList<int> Selection, PdfEngine Engine)
{
    /// <summary>Gets the number of pages of the document.</summary>
    public int PageCount => Document.PageCount;

    /// <summary>Gets the name of the file without its folder.</summary>
    public string Name => Path.GetFileName(File.Path);

    /// <summary>Gets the name of the file without its folder and extension (what new file names start with).</summary>
    public string Stem => Path.GetFileNameWithoutExtension(File.Path);

    /// <summary>Gets the folder of the file (where the target folder starts).</summary>
    public string DocumentFolder => Path.GetDirectoryName(File.Path) ?? string.Empty;
}
