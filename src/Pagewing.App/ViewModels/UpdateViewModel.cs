using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core;
using Pagewing.Core.Updates;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The button "Update" in the title bar and its card "Pagewing X is available" in one window
/// (<see cref="MainViewModel.Update"/>). Follows the coordinator of the process, which all windows share: the button
/// shows as long as the coordinator knows of an update, the card opens and closes per window. "Later" closes the card
/// and keeps the button. A form that cannot update itself (<see cref="PackageForm.Deb"/>,
/// <see cref="PackageForm.WindowsPortable"/>, <see cref="PackageForm.Unknown"/>) gets a hint and the download page
/// instead of "Install"; the page is asked for like any external address (confirmation card). UI thread only.
/// </summary>
public sealed class UpdateViewModel : ViewModelBase, IDisposable
{
    private readonly UpdateCoordinator _coordinator;
    private readonly Action<string> _requestLink;
    private UpdateState _state;
    private bool _isCardOpen;
    private bool _disposed;

    /// <summary>Creates the view model and starts following <paramref name="coordinator"/> until <see cref="Dispose"/>.</summary>
    /// <param name="coordinator">The update check of the process.</param>
    /// <param name="requestLink">
    /// Asks the user before an address is opened (<see cref="MainViewModel.RequestExternalLink"/>); never opens it itself.
    /// </param>
    public UpdateViewModel(UpdateCoordinator coordinator, Action<string> requestLink)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(requestLink);
        _coordinator = coordinator;
        _requestLink = requestLink;
        _state = coordinator.State;
        ToggleCardCommand = new RelayCommand(() => IsCardOpen = !IsCardOpen);
        LaterCommand = new RelayCommand(() => IsCardOpen = false);
        InstallCommand = new RelayCommand(Install);
        OpenDownloadPageCommand = new RelayCommand(OpenDownloadPage);
        coordinator.Changed += OnChanged;
    }

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
    /// </summary>
    public string Text => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateCardHave), AppInfo.Version) + " " + (CanInstall ? Strings.UpdateCardInstallText : Hint);

    /// <summary>Gets or sets whether the card of this window is open; it closes by itself when the offer goes.</summary>
    public bool IsCardOpen
    {
        get => _isCardOpen;
        set => SetProperty(ref _isCardOpen, value && ShowButton && !_disposed);
    }

    /// <summary>The button: opens the card, or closes it when it is open.</summary>
    public ICommand ToggleCardCommand { get; }

    /// <summary>"Later" and Esc: closes the card; the button stays.</summary>
    public ICommand LaterCommand { get; }

    /// <summary>"Install": closes the card and starts the installation (download, check, install; Milestone 8.1, task 9).</summary>
    public ICommand InstallCommand { get; }

    /// <summary>"Open download page": closes the card and asks the user before the release page is opened.</summary>
    public ICommand OpenDownloadPageCommand { get; }

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
        }
    }

    // Raised on the UI thread by the coordinator, which swallows what a handler throws: this one must not throw.
    private void OnChanged()
    {
        if (_disposed)
        {
            return;
        }

        var old = _state;
        _state = _coordinator.State;
        if (old.Stage != _state.Stage)
        {
            OnPropertyChanged(nameof(Stage));
            OnPropertyChanged(nameof(ShowButton));
        }

        if (old.Version != _state.Version)
        {
            OnPropertyChanged(nameof(VersionText));
            OnPropertyChanged(nameof(Title));
        }

        if (!ShowButton)
        {
            IsCardOpen = false;
        }
    }

    private void Install()
    {
        if (!CanInstall)
        {
            return;
        }

        IsCardOpen = false;
        _ = _coordinator.BeginInstallAsync();
    }

    private void OpenDownloadPage()
    {
        IsCardOpen = false;
        _requestLink(UpdateTrust.ReleasesPageUrl);
    }
}
