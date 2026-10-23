using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;

namespace Pagewing.App.Tests;

/// <summary>
/// What the tests of the update check share: a window whose view model gets a coordinator made over a fake source (or a
/// factory of the test), a status file in a temporary folder that is never the one of the user, and the helpers to wait.
/// </summary>
// IDisposable is named again: the Dispose of PrintTestHost is not virtual, and xUnit disposes through the interface,
// which would otherwise still reach the one of the base class and never this cleanup.
public abstract class UpdateTestHost : PrintTestHost, IDisposable
{
    /// <summary>The version the coordinators of these tests count as running.</summary>
    protected static readonly UpdateVersion Running = new(0, 1, 0);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-update-" + Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _owned = [];

    protected FakeUpdateSourceApp Source { get; } = new();

    /// <summary>Gets the installer the coordinators of the test hand a verified update to (unless a test passes another).</summary>
    protected FakeUpdateApplier Applier { get; } = new();

    /// <summary>Gets the folder that stands for the config folder of the user in these tests (status, updates, session).</summary>
    protected string ConfigDirectory => _directory;

    /// <summary>Gets the folder the coordinators of the test download version <paramref name="version"/> into.</summary>
    protected string StagingOf(string version) => Path.Combine(_directory, UpdateStaging.DirectoryName, version);

    /// <summary>Gets the path the coordinators of the test count as the running program.</summary>
    protected string ProcessPath => Path.Combine(_directory, "app", "Pagewing.exe");

    /// <summary>Gets the status file (<c>update.json</c>) the coordinators of the test use.</summary>
    protected string StatusPath => Path.Combine(_directory, UpdateStatusStore.FileName);

    /// <summary>Gets the window <see cref="ShowAppAsync"/> made last, set before it is shown.</summary>
    protected MainWindow? LastWindow { get; private set; }

    /// <summary>Gets how often the factory of the window made a coordinator.</summary>
    protected int CoordinatorsMade { get; private set; }

    public new void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }

        base.Dispose();
        Source.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes <paramref name="item"/> when the test ends, before the windows of <c>Track</c> are closed.</summary>
    protected void Own(IDisposable item) => _owned.Add(item);

    /// <summary>
    /// Makes a coordinator over <see cref="Source"/> whose updates go to <see cref="StagingOf"/> and to
    /// <paramref name="applier"/> (<see cref="Applier"/> without one; <paramref name="noApplier"/> for a system that has none).
    /// <paramref name="source"/> replaces <see cref="Source"/> for checking and downloading (the real source on a fake handler).
    /// </summary>
    protected UpdateCoordinator NewCoordinator(
        UserSettings? settings = null,
        TimeProvider? clock = null,
        PackageForm form = PackageForm.WindowsInstaller,
        IUpdateApplier? applier = null,
        bool noApplier = false,
        IUpdateSource? source = null)
    {
        Directory.CreateDirectory(_directory);
        var time = clock ?? TimeProvider.System;
        var from = source ?? Source;
        var checker = new UpdateChecker(from, new UpdateStatusStore(StatusPath), time, Running, Source.Keys);
        var chosen = noApplier ? null : applier ?? Applier;
        var install = new UpdateInstallSetup(from, _directory, () => chosen, time)
        {
            ProcessPath = ProcessPath,
            TargetDirectory = Path.GetDirectoryName(ProcessPath),
        };
        var coordinator = new UpdateCoordinator(checker, settings ?? UserSettings.Detached(), form, install);
        Own(coordinator);
        return coordinator;
    }

    /// <summary>Closes <paramref name="window"/> when the test ends, before the windows of <c>Track</c> are closed.</summary>
    protected void OwnWindow(Avalonia.Controls.Window window) => Own(new WindowCloser(window));

    /// <summary>Gets a factory that makes one coordinator over <see cref="Source"/> and counts the times it is asked.</summary>
    protected Func<UpdateCoordinator?> CountingFactory(UserSettings? settings = null, PackageForm form = PackageForm.WindowsInstaller)
    {
        UpdateCoordinator? made = null;
        return () =>
        {
            CoordinatorsMade++;
            return made ??= NewCoordinator(settings, form: form);
        };
    }

    /// <summary>Shows a window with the view model that has <paramref name="factory"/>; a file is opened before it is shown, as on the command line.</summary>
    protected async Task<(MainWindow Window, MainViewModel ViewModel, DocumentView View)> ShowAppAsync(
        string? file = "text-basic.pdf",
        UserSettings? settings = null,
        Func<UpdateCoordinator?>? factory = null,
        Action<MainViewModel>? beforeShow = null,
        double width = 1024,
        bool windowsTitleBar = true)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings ?? UserSettings.Detached())
        {
            IsWindowsTitleBar = windowsTitleBar,
            UpdateCoordinatorFactory = factory ?? CountingFactory(settings),
        };

        if (file is not null)
        {
            await viewModel.OpenAsync(TestData.Pdf(file));
        }

        beforeShow?.Invoke(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720 };
        Track(window, viewModel);
        LastWindow = window;
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    protected static async Task WaitSharpAsync(MainWindow window, MainViewModel viewModel)
    {
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
    }

    /// <summary>Runs the dispatcher until <paramref name="condition"/> holds (for tests without a window), at most 15 seconds.</summary>
    protected static async Task PumpUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not come true.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Runs the dispatcher for about <paramref name="milliseconds"/> (jobs of every priority run).</summary>
    protected static async Task RunForAsync(MainWindow window, int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Settle(window);
            await Task.Delay(20);
        }
    }

    private sealed class WindowCloser(Avalonia.Controls.Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
