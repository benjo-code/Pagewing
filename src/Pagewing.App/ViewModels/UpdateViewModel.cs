using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core;
using Pagewing.Core.Updates;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The button "Update" in the title bar and its card "Pagewing X is available" in one window
/// (<see cref="MainViewModel.Update"/>), or the progress window of the installation (<see cref="Views.UpdateWindow"/>,
/// which has an instance of its own; in the helper of the Windows updater it follows the installation of the helper, see
/// <see cref="IUpdateFlow"/>). Follows the coordinator of the process, which all windows share: the button shows
/// as long as the coordinator knows of an update, the card opens and closes per window. "Later" closes the card and keeps
/// the button; "Install" starts the installation, once for the process, and asks for the progress window
/// (<see cref="ProgressWindowRequested"/>), which the button brings back while it runs. A form that cannot update itself
/// (<see cref="PackageForm.Deb"/>, <see cref="PackageForm.WindowsPortable"/>, <see cref="PackageForm.Unknown"/>) gets a
/// hint and the download page instead of "Install"; the page is asked for like any external address (confirmation card).
/// UI thread only.
/// </summary>
public sealed class UpdateViewModel : ViewModelBase, IDisposable
{
    private const double BytesPerMegabyte = 1024 * 1024;

    // What the progress window shows, told again whenever the coordinator changes.
    private static readonly string[] ProgressProperties =
    [
        nameof(IsInFlow), nameof(IsFailed), nameof(IsLocked), nameof(Heading), nameof(Subtitle), nameof(Steps),
        nameof(Percent), nameof(IsProgressIndeterminate), nameof(ShowsProgress), nameof(PercentText), nameof(SizeText),
        nameof(ShowsSize), nameof(FailureText), nameof(CanCancel), nameof(CanRetry), nameof(OffersDownloadPage),

        // After IsInFlow: a wait that ends with the end of the flow closes the progress window before it would show again.
        nameof(IsAwaitingAnswer), nameof(Text),
    ];

    private readonly IUpdateFlow _coordinator;
    private readonly Action<string> _requestLink;
    private readonly TimeProvider _clock;
    private UpdateState _state;
    private IReadOnlyList<UpdateStep> _steps = [];
    private bool _isCardOpen;
    private bool _disposed;
    private UpdateNote _note;
    private ITimer? _noteTimer;
    private int _noteNumber;
    private bool _isAwaitingAnswer;

    // "Install" was chosen here while the question about unsaved changes waits: the progress window shows once it starts.
    private bool _progressWindowWanted;

    /// <summary>Creates the view model and starts following <paramref name="coordinator"/> until <see cref="Dispose"/>.</summary>
    /// <param name="coordinator">The update check of the process (<see cref="UpdateCoordinator"/>), or the installation of the helper.</param>
    /// <param name="requestLink">
    /// Asks the user before an address is opened (<see cref="MainViewModel.RequestExternalLink"/>); never opens it itself.
    /// </param>
    /// <param name="clock">The clock of the note after the command (<see cref="NoteLifetime"/>); tests drive it.</param>
    public UpdateViewModel(IUpdateFlow coordinator, Action<string> requestLink, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(requestLink);
        _coordinator = coordinator;
        _requestLink = requestLink;
        _clock = clock ?? TimeProvider.System;
        _state = coordinator.State;
        _isAwaitingAnswer = coordinator.IsAwaitingAnswer;
        _steps = MakeSteps();
        ToggleCardCommand = new RelayCommand(ToggleCard);
        LaterCommand = new RelayCommand(() => IsCardOpen = false);
        CloseNoteCommand = new RelayCommand(CloseNote);
        DismissCardCommand = new RelayCommand(DismissCard);
        InstallCommand = new RelayCommand(Install, () => !_isAwaitingAnswer);
        OpenDownloadPageCommand = new RelayCommand(OpenDownloadPage);
        CancelCommand = new RelayCommand(coordinator.CancelInstall);
        RetryCommand = new RelayCommand(() => _ = coordinator.RetryAsync());
        CloseCommand = new RelayCommand(coordinator.Dismiss);
        EscapeCommand = new RelayCommand(Escape);
        coordinator.Changed += OnChanged;
    }

    /// <summary>
    /// Raised when the progress window should show (or come to the front): "Install" was chosen, or the button was pressed
    /// while an installation runs or waits with its error.
    /// </summary>
    public event Action? ProgressWindowRequested;

    /// <summary>Gets where the update is (<see cref="UpdateStage.None"/> while nothing newer is known).</summary>
    public UpdateStage Stage => _state.Stage;

    /// <summary>Gets the version on offer ("0.2.0"), or an empty text without one.</summary>
    public string VersionText => _state.Version?.ToString() ?? string.Empty;

    /// <summary>Gets whether the button "Update" shows: whenever an update is known, whatever its stage.</summary>
    public bool ShowButton => _state.Stage != UpdateStage.None;

    /// <summary>Gets whether this installation can update itself ("Install"); otherwise the card shows the hint.</summary>
    public bool CanInstall => _coordinator.Form is PackageForm.WindowsInstaller or PackageForm.AppImage;

    /// <summary>Gets the title of the card, "Pagewing 0.2.0 is available".</summary>
    public string Title => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateCardTitle), VersionText);

    /// <summary>
    /// Gets the text of the card: the running version (<see cref="AppInfo.Version"/>, as the About card shows it), then what
    /// "Install" does or, for a form that cannot update itself, the hint that points to the download page.
    /// While the installation waits for the answer about unsaved changes, it says so instead.
    /// </summary>
    public string Text => _isAwaitingAnswer ? Strings.UpdateAwaitingAnswer
        : string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateCardHave), AppInfo.Version) + " " + (CanInstall ? Strings.UpdateCardInstallText : Hint);

    /// <summary>Gets or sets whether the card of this window is open; it closes by itself when the offer goes.</summary>
    public bool IsCardOpen
    {
        get => _isCardOpen;
        set
        {
            if (SetProperty(ref _isCardOpen, value && ShowButton && !IsInFlow && !_disposed) && _isCardOpen)
            {
                // The offer takes the place of a note.
                CloseNote();
            }
        }
    }

    /// <summary>How long the note after the command "Check for updates" stays before it goes by itself.</summary>
    public static TimeSpan NoteLifetime { get; } = TimeSpan.FromSeconds(8);

    /// <summary>Gets the note in the place of the card after the command (<see cref="ShowCheckOutcome"/>), or <see cref="UpdateNote.None"/>.</summary>
    public UpdateNote Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value))
            {
                OnPropertyChanged(nameof(IsNoteOpen));
                OnPropertyChanged(nameof(IsNoteUpToDate));
                OnPropertyChanged(nameof(IsNoteFailed));
                OnPropertyChanged(nameof(NoteText));
                OnPropertyChanged(nameof(CardName));
            }
        }
    }

    /// <summary>Gets whether the note shows (in the place of the card).</summary>
    public bool IsNoteOpen => _note != UpdateNote.None;

    /// <summary>Gets whether the note says that Pagewing is up to date (the check mark).</summary>
    public bool IsNoteUpToDate => _note == UpdateNote.UpToDate;

    /// <summary>Gets whether the note says that the check failed (the warning symbol).</summary>
    public bool IsNoteFailed => _note == UpdateNote.CheckFailed;

    /// <summary>Gets the text of the note: up to date with the running version, or that the check failed.</summary>
    public string NoteText => _note switch
    {
        UpdateNote.UpToDate => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateNoteUpToDate), AppInfo.Version),
        UpdateNote.CheckFailed => Strings.UpdateNoteFailed,
        _ => string.Empty,
    };

    /// <summary>Gets the accessible name of the card: the text of the note while it shows, else the title of the offer.</summary>
    public string CardName => IsNoteOpen ? NoteText : Title;

    /// <summary>"Close" on the note.</summary>
    public ICommand CloseNoteCommand { get; }

    /// <summary>Esc on the card: closes the offer ("Later") or the note.</summary>
    public ICommand DismissCardCommand { get; }

    /// <summary>Gets whether the card or the note of this window shows (Esc of the window closes it).</summary>
    public bool CanDismissCard => IsCardOpen || IsNoteOpen;

    /// <summary>The button: opens the card, or closes it when it is open; while an installation runs, shows its window.</summary>
    public ICommand ToggleCardCommand { get; }

    /// <summary>"Later" and Esc: closes the card; the button stays.</summary>
    public ICommand LaterCommand { get; }

    /// <summary>"Install": closes the card, starts the installation and shows its progress window.</summary>
    public ICommand InstallCommand { get; }

    /// <summary>"Open download page": closes the card (or the message of the window) and asks the user before the release page is opened.</summary>
    public ICommand OpenDownloadPageCommand { get; }

    /// <summary>"Cancel" in the progress window: stops the download or the check; the offer stays.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>"Try again" after a failure.</summary>
    public ICommand RetryCommand { get; }

    /// <summary>"Close" after a failure: the window closes, the offer stays.</summary>
    public ICommand CloseCommand { get; }

    /// <summary>Esc in the progress window: cancels a download, closes a failure, does nothing while installing.</summary>
    public ICommand EscapeCommand { get; }

    /// <summary>Gets whether an installation runs or waits with its failure: the time of the progress window.</summary>
    public bool IsInFlow => IsFlowStage(_state.Stage);

    /// <summary>
    /// Gets whether the installation waits for the answer about unsaved changes (the card of a window asks, before the
    /// installation starts or right before the installer): the card says so and "Install" does nothing meanwhile.
    /// </summary>
    public bool IsAwaitingAnswer => _isAwaitingAnswer;

    /// <summary>Gets whether the installation failed.</summary>
    public bool IsFailed => _state.Stage == UpdateStage.Failed;

    /// <summary>Gets whether the installation is past cancelling (the installer runs): the window cannot be closed.</summary>
    public bool IsLocked => _state.Stage is UpdateStage.Ready or UpdateStage.Installing or UpdateStage.Restarting;

    /// <summary>Gets the title of the progress window, "Updating Pagewing", or the one of a failure.</summary>
    public string Heading => IsFailed ? Strings.UpdateWindowFailedTitle : Strings.UpdateWindowTitle;

    /// <summary>Gets the line under it: running and new version, and that the window should stay open.</summary>
    public string Subtitle => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateWindowSubtitle), AppInfo.Version, VersionText);

    /// <summary>Gets the four steps with their state.</summary>
    public IReadOnlyList<UpdateStep> Steps => _steps;

    /// <summary>Gets how much of the file has arrived, in whole percent (100 once it is being checked).</summary>
    public int Percent => _state switch
    {
        { Stage: UpdateStage.Downloading, File: { Size: > 0 } file } => (int)Math.Min(100, _state.BytesReceived * 100 / file.Size),
        { Stage: UpdateStage.Verifying } => 100,
        _ => 0,
    };

    /// <summary>Gets whether the bar runs without a value: while the manifest is fetched and while the installer works.</summary>
    public bool IsProgressIndeterminate => _state.Stage is UpdateStage.Ready or UpdateStage.Installing or UpdateStage.Restarting
        || (_state.Stage == UpdateStage.Downloading && _state.File is null);

    /// <summary>Gets whether the bar shows (not after a failure, where the message takes its place).</summary>
    public bool ShowsProgress => IsInFlow && !IsFailed;

    /// <summary>Gets the percent as text ("46%", German "46 %").</summary>
    public string PercentText => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateProgressPercent), Percent);

    /// <summary>Gets whether the sizes show: while the file is downloaded and checked.</summary>
    public bool ShowsSize => _state.Stage is UpdateStage.Downloading or UpdateStage.Verifying && _state.File is not null;

    /// <summary>Gets the sizes ("31 of 68 MB"): received rounded down, the whole rounded up.</summary>
    public string SizeText
    {
        get
        {
            if (_state.File is not { Size: > 0 } file)
            {
                return string.Empty;
            }

            var received = Math.Floor(Math.Min(_state.BytesReceived, file.Size) / BytesPerMegabyte);
            var total = Math.Max(1, Math.Ceiling(file.Size / BytesPerMegabyte));
            return string.Format(
                Strings.Culture,
                CompositeFormat.Parse(Strings.UpdateProgressSize),
                received.ToString("0", Strings.Culture),
                total.ToString("0", Strings.Culture));
        }
    }

    /// <summary>Gets what went wrong, in the words of the installer when it gave some.</summary>
    public string FailureText => !IsFailed ? string.Empty : _coordinator.FailureMessage ?? _state.Error switch
    {
        UpdateFailure.Withdrawn or UpdateFailure.Superseded => Strings.UpdateErrorWithdrawn,
        UpdateFailure.Rejected => Strings.UpdateErrorRejected,
        UpdateFailure.Checksum => Strings.UpdateErrorChecksum,
        UpdateFailure.Storage => Strings.UpdateErrorStorage,
        UpdateFailure.NoInstaller => Strings.UpdateErrorNoInstaller,
        UpdateFailure.InstallFailed => Strings.UpdateErrorInstallFailed,
        _ => Strings.UpdateErrorUnreachable,
    };

    /// <summary>Gets whether "Cancel" shows: until the step "Install".</summary>
    public bool CanCancel => _state.Stage is UpdateStage.Downloading or UpdateStage.Verifying;

    /// <summary>Gets whether "Try again" shows: after a failure that may go away.</summary>
    public bool CanRetry => _coordinator.CanRetry;

    /// <summary>Gets whether the failure points to the download page.</summary>
    public bool OffersDownloadPage => IsFailed && _coordinator.FailureOffersDownloadPage;

    /// <summary>Gets the installation this view model follows (the progress window makes its own view model over it).</summary>
    internal IUpdateFlow Coordinator => _coordinator;

    /// <summary>Gets what asks the user before an address is opened (for the view model of the progress window).</summary>
    internal Action<string> RequestLink => _requestLink;

    private string Hint => _coordinator.Form switch
    {
        PackageForm.Deb => Strings.UpdateHintDeb,
        PackageForm.WindowsPortable => Strings.UpdateHintPortable,
        _ => Strings.UpdateHintUnknown,
    };

    /// <summary>Stops following the coordinator, so a closed window is neither told nor kept by it. May be called more than once.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _coordinator.Changed -= OnChanged;
            IsCardOpen = false;
            CloseNote();
        }
    }

    /// <summary>
    /// Shows what the command "Check for updates" found, only after the command (the check in the background stays
    /// silent): an update opens the card (or brings the progress window back while an installation runs); up to date and a
    /// failed check show the note in the place of the card, which goes with Esc, "Close" or after <see cref="NoteLifetime"/>.
    /// </summary>
    internal void ShowCheckOutcome(UpdateCheckOutcome outcome)
    {
        if (_disposed)
        {
            return;
        }

        if (outcome == UpdateCheckOutcome.Available && ShowButton)
        {
            if (IsInFlow)
            {
                ProgressWindowRequested?.Invoke();
                return;
            }

            IsCardOpen = true;
            return;
        }

        IsCardOpen = false;
        _noteTimer?.Dispose();
        var number = ++_noteNumber;
        Note = outcome == UpdateCheckOutcome.Failed ? UpdateNote.CheckFailed : UpdateNote.UpToDate;
        _noteTimer = _clock.CreateTimer(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (number == _noteNumber)
                {
                    CloseNote();
                }
            }),
            null,
            NoteLifetime,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Esc of the window: closes the card or the note of this window.</summary>
    internal void DismissCard()
    {
        IsCardOpen = false;
        CloseNote();
    }

    private void CloseNote()
    {
        _noteTimer?.Dispose();
        _noteTimer = null;
        Note = UpdateNote.None;
    }

    private static bool IsFlowStage(UpdateStage stage) => stage is not (UpdateStage.None or UpdateStage.Available);

    // Raised on the UI thread by the coordinator, which swallows what a handler throws: this one must not throw.
    private void OnChanged()
    {
        if (_disposed)
        {
            return;
        }

        var old = _state;
        var wasAwaiting = _isAwaitingAnswer;
        _state = _coordinator.State;
        _isAwaitingAnswer = _coordinator.IsAwaitingAnswer;
        if (wasAwaiting != _isAwaitingAnswer && InstallCommand is RelayCommand install)
        {
            install.RaiseCanExecuteChanged();
        }
        if (old.Stage != _state.Stage)
        {
            _steps = MakeSteps();
            OnPropertyChanged(nameof(Stage));
            OnPropertyChanged(nameof(ShowButton));
        }

        if (old.Version != _state.Version)
        {
            OnPropertyChanged(nameof(VersionText));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(CardName));
        }

        foreach (var name in ProgressProperties)
        {
            OnPropertyChanged(name);
        }

        if (!ShowButton || IsInFlow)
        {
            IsCardOpen = false;
        }

        // "Install" of this window waited for the answer: the installation has started now (its window shows), or it was
        // cancelled (nothing shows).
        if (_progressWindowWanted && !_isAwaitingAnswer)
        {
            _progressWindowWanted = false;
            if (IsInFlow)
            {
                ProgressWindowRequested?.Invoke();
            }
        }
    }

    private IReadOnlyList<UpdateStep> MakeSteps()
    {
        // The step that runs (or failed); the ones before it are done, the ones after it open.
        var current = _state.Stage switch
        {
            UpdateStage.Downloading => 0,
            UpdateStage.Verifying => 1,
            UpdateStage.Ready or UpdateStage.Installing => 2,
            UpdateStage.Restarting => 3,
            UpdateStage.Failed => _coordinator.FailedAt switch
            {
                UpdateStage.Downloading => 0,
                UpdateStage.Verifying => 1,
                _ => 2,
            },
            _ => -1,
        };
        string[] labels = [Strings.UpdateStepDownload, Strings.UpdateStepVerify, Strings.UpdateStepInstall, Strings.UpdateStepRestart];
        return
        [
            .. labels.Select((label, index) => new UpdateStep(
                label,
                index < current ? UpdateStepState.Done
                : index > current ? UpdateStepState.Open
                : IsFailed ? UpdateStepState.Failed
                : UpdateStepState.Running)),
        ];
    }

    private void ToggleCard()
    {
        CloseNote();
        if (IsInFlow)
        {
            IsCardOpen = false;
            ProgressWindowRequested?.Invoke();
            return;
        }

        IsCardOpen = !IsCardOpen;
    }

    private void Install()
    {
        if (!CanInstall || _isAwaitingAnswer)
        {
            return;
        }

        IsCardOpen = false;
        _ = _coordinator.BeginInstallAsync();
        if (IsInFlow)
        {
            ProgressWindowRequested?.Invoke();
        }
        else if (_isAwaitingAnswer)
        {
            // The windows are asked about unsaved changes first; the progress window comes when the installation starts.
            _progressWindowWanted = true;
        }
    }

    private void OpenDownloadPage()
    {
        IsCardOpen = false;
        _coordinator.Dismiss();
        _requestLink(UpdateTrust.ReleasesPageUrl);
    }

    private void Escape()
    {
        if (CanCancel)
        {
            _coordinator.CancelInstall();
        }
        else if (IsFailed)
        {
            _coordinator.Dismiss();
        }
    }
}
