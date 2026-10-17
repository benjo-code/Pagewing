using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The state of a window: its tabs (<see cref="Tabs"/>), each with its own document, and the active one, which the
/// document area, the status bar and the title bar show. Everything except <see cref="OnExternalOpen"/> is used on the
/// UI thread.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>The most files one open (dialog, drop, later launch) opens as tabs; the rest is ignored.</summary>
    public const int MaxFilesPerOpen = 20;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Func<ViewerSession> _sessionFactory;
    private readonly IFilePicker _picker;
    private readonly RelayCommand _closeActiveTabCommand;
    private TabViewModel? _activeTab;
    private bool _picking;
    private bool _isWindowsTitleBar = OperatingSystem.IsWindows();

    /// <param name="sessionFactory">Creates the session of each new tab; the tab owns it.</param>
    /// <param name="picker">The file dialog.</param>
    public MainViewModel(Func<ViewerSession> sessionFactory, IFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(picker);
        _sessionFactory = sessionFactory;
        _picker = picker;

        Tabs.ActiveChanged += OnActiveTabChanged;
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasTabs));
        };

        OpenCommand = new RelayCommand(() => _ = PickAndOpenAsync(null));
        _closeActiveTabCommand = new RelayCommand(() => CloseTab(ActiveTab), () => ActiveTab is not null);
        CloseActiveTabCommand = _closeActiveTabCommand;
        CloseTabCommand = new RelayCommand<TabViewModel>(CloseTab);
        ActivateTabCommand = new RelayCommand<TabViewModel>(tab => Tabs.Activate(tab));
        NextTabCommand = new RelayCommand(Tabs.ActivateNext);
        PreviousTabCommand = new RelayCommand(Tabs.ActivatePrevious);
    }

    /// <summary>Raised on the UI thread when the window should come to the front (a later launch handed over).</summary>
    public event Action? ActivationRequested;

    /// <summary>Raised when the last tab was closed and <see cref="CloseWindowWhenEmpty"/> is set.</summary>
    public event Action? CloseWindowRequested;

    /// <summary>Gets the tabs of the window, in the order of the tab strip.</summary>
    public TabList<TabViewModel> Tabs { get; } = new();

    /// <summary>Gets the active tab, or <see langword="null"/> without tabs.</summary>
    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(HasTabs));
                OnPropertyChanged(nameof(WindowTitle));
                _closeActiveTabCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether the window has no tab: it shows the start screen.</summary>
    public bool IsEmpty => Tabs.Count == 0;

    /// <summary>Gets whether the window has at least one tab: it shows the document area.</summary>
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>
    /// Gets or sets whether the last tab closes the window (<see cref="CloseWindowRequested"/>); otherwise the window
    /// returns to the start screen. Off by default.
    /// </summary>
    public bool CloseWindowWhenEmpty { get; set; }

    /// <summary>Gets the window title: "file name – Pagewing" of the active tab, or the app name without a file.</summary>
    public string WindowTitle => ActiveTab?.FileName is { } name ? $"{name} – {Strings.AppName}" : Strings.AppName;

    /// <summary>
    /// Gets or sets whether the window draws its own title bar with the tabs in it (Windows) instead of keeping the
    /// system title bar with a tab row below it (Linux, design system section 2.2). Settable for tests.
    /// </summary>
    public bool IsWindowsTitleBar
    {
        get => _isWindowsTitleBar;
        set => SetProperty(ref _isWindowsTitleBar, value);
    }

    /// <summary>Shows the file dialog and opens the chosen file in a new tab (Ctrl+O, "Choose file", "Open file").</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Closes the active tab (Ctrl+W).</summary>
    public ICommand CloseActiveTabCommand { get; }

    /// <summary>Closes the tab given as the parameter.</summary>
    public ICommand CloseTabCommand { get; }

    /// <summary>Activates the tab given as the parameter.</summary>
    public ICommand ActivateTabCommand { get; }

    /// <summary>Activates the tab to the right of the active one, wrapping around.</summary>
    public ICommand NextTabCommand { get; }

    /// <summary>Activates the tab to the left of the active one, wrapping around.</summary>
    public ICommand PreviousTabCommand { get; }

    /// <summary>
    /// Opens a file: a file that is already open in a tab activates that tab; otherwise a new tab to the right of the
    /// active one opens it.
    /// </summary>
    /// <param name="path">Path of the file; an empty path does nothing.</param>
    /// <returns>Completes when the session of the tab has a result (see <see cref="ViewerSession.OpenAsync"/>).</returns>
    public Task OpenAsync(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Task.CompletedTask;
        }

        var fullPath = FullPathOrAsGiven(path);
        foreach (var tab in Tabs)
        {
            if (string.Equals(tab.Session.Path, fullPath, PathComparison))
            {
                Tabs.Activate(tab);
                return Task.CompletedTask;
            }
        }

        return AddTab(new TabViewModel(_sessionFactory())).OpenAsync(path);
    }

    /// <summary>Opens up to <see cref="MaxFilesPerOpen"/> files in their order (see <see cref="OpenAsync"/>); the last one ends up active.</summary>
    /// <returns>Completes when every session has a result.</returns>
    public Task OpenManyAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var opens = new List<Task>();
        foreach (var path in paths.Take(MaxFilesPerOpen))
        {
            opens.Add(OpenAsync(path));
        }

        return Task.WhenAll(opens);
    }

    /// <summary>
    /// Adds a tab for a session that is already opening (the file of the command line, which <see cref="Program.Main"/>
    /// starts opening before the window exists) and activates it.
    /// </summary>
    public TabViewModel AdoptSession(ViewerSession session) => AddTab(new TabViewModel(session));

    /// <summary>
    /// Handles the files a later launch handed over (single instance). May be called on any thread: opens them on the
    /// UI thread (<see cref="OpenManyAsync"/>) and brings the window to the front; an empty list only brings it to the
    /// front.
    /// </summary>
    public void OnExternalOpen(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var copy = paths.ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            _ = OpenManyAsync(copy);
            ActivationRequested?.Invoke();
        });
    }

    /// <summary>Releases every tab and its session.</summary>
    public void Dispose()
    {
        foreach (var tab in Tabs.ToArray())
        {
            tab.Dispose();
        }
    }

    private static string FullPathOrAsGiven(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return path;
        }
    }

    private TabViewModel AddTab(TabViewModel tab)
    {
        tab.CloseRequested += () => CloseTab(tab);
        tab.OpenOtherRequested += () => _ = PickAndOpenAsync(tab);
        Tabs.Add(tab);
        return tab;
    }

    private void CloseTab(TabViewModel? tab)
    {
        if (tab is null || Tabs.IndexOf(tab) < 0)
        {
            return;
        }

        // The document view leaves the tab (with the change of the active tab) before its session is released; a
        // tab that is still loading is released the same way, the session cancels its request.
        Tabs.Close(tab);
        tab.Dispose();
        if (Tabs.Count == 0 && CloseWindowWhenEmpty)
        {
            CloseWindowRequested?.Invoke();
        }
    }

    private void OnActiveTabChanged()
    {
        var old = _activeTab;
        var next = Tabs.Active;
        if (old is not null)
        {
            old.PropertyChanged -= OnActiveTabPropertyChanged;
            old.IsActive = false;
        }

        if (next is not null)
        {
            next.IsActive = true;
            next.PropertyChanged += OnActiveTabPropertyChanged;
        }

        ActiveTab = next;
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.FileName))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    /// <summary>
    /// Shows the file dialog. The chosen file opens in a new tab, or, for "Open another file" of a tab
    /// (<paramref name="target"/>), replaces the document of that tab. A cancelled dialog changes nothing; a failing
    /// one shows the error card in the target tab, or in a new tab.
    /// </summary>
    private async Task PickAndOpenAsync(TabViewModel? target)
    {
        // Ctrl+O while the dialog is open (where the platform lets it through) does not open a second dialog.
        if (_picking)
        {
            return;
        }

        _picking = true;
        string? path;
        try
        {
            path = await _picker.PickPdfAsync();
        }
        catch (OperationCanceledException)
        {
            // Treated like a cancelled dialog: nothing changes.
            return;
        }
        catch (Exception ex)
        {
            // The dialog itself failed (for example no file chooser portal on Linux): the command must not end
            // silently in an unobserved task. Like any failed open, it shows the error card in a tab.
            if (target is null || Tabs.IndexOf(target) < 0)
            {
                target = AddTab(new TabViewModel(_sessionFactory()));
            }

            target.ShowPickerFailure(ex);
            return;
        }
        finally
        {
            _picking = false;
        }

        if (path is null)
        {
            return;
        }

        if (target is not null && Tabs.IndexOf(target) >= 0)
        {
            await target.OpenAsync(path);
        }
        else
        {
            await OpenAsync(path);
        }
    }
}
