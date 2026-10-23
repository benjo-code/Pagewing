using System.Globalization;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The restart after an update (<c>--restore-session</c>): the windows and tabs come back as they were, read once from a
/// session file in a temporary folder (never the user's), the first file of the first window early like a file of the
/// command line and everything else after its first sharp tile.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class SessionRestoreTests : IDisposable
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly PdfEngine _engine = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-session-" + Guid.NewGuid().ToString("N"));
    private int _opens;

    public SessionRestoreTests() => Directory.CreateDirectory(_directory);

    private string FilePath => Path.Combine(_directory, SessionStore.FileName);

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public async Task RestoresTabsAndPositionsOfOneWindow()
    {
        var far = new RememberedView(new ViewState(new PageAnchor(499, 0), 0), 2, ZoomMode.Free, PageLayoutMode.Continuous);
        var behind = new RememberedView(new ViewState(new PageAnchor(9, 0.5), 0), 1.5, ZoomMode.Free, PageLayoutMode.TwoPages);
        Save(Window(1, Tab("hello_world.pdf"), Tab("thousand-pages.pdf", far), Tab("text-dense.pdf", behind)));
        var workspace = CreateWorkspace();

        var viewModel = StartLikeProgram(workspace)!;

        // The first frame shows only the tab that was active, opened early; the others come after its first sharp tile.
        var first = Assert.Single(viewModel.Tabs);
        Assert.Equal(TestData.Pdf("thousand-pages.pdf"), first.Session.Path);
        await WaitUntilAsync(workspace, () => viewModel.Tabs.Count == 3 && viewModel.Tabs.All(tab => tab.State == ViewerState.Ready));

        // The order and the active tab as before, and each tab where it was read.
        Assert.Equal(
            [TestData.Pdf("hello_world.pdf"), TestData.Pdf("thousand-pages.pdf"), TestData.Pdf("text-dense.pdf")],
            viewModel.Tabs.Select(tab => tab.Session.Path));
        Assert.Same(first, viewModel.ActiveTab);
        Assert.Same(first, viewModel.Tabs[1]);
        Assert.False(viewModel.Tabs[0].IsActive);
        Assert.False(viewModel.Tabs[2].IsActive);
        await WaitUntilAsync(workspace, () => first.HasShownSharpPage && first.CurrentPage == 500);
        Assert.Equal(2, first.Zoom);
        Assert.Equal(ZoomMode.Free, first.ZoomMode);

        var restored = viewModel.Tabs[2];
        Assert.Equal(new PageAnchor(9, 0.5), restored.SavedView?.Anchor);
        Assert.Equal(1.5, restored.Zoom);
        Assert.Equal(PageLayoutMode.TwoPages, restored.LayoutMode);
        Assert.Null(viewModel.Tabs[0].SavedView);
        Assert.Equal(3, Volatile.Read(ref _opens));
    }

    [AvaloniaFact]
    public async Task RestoresSeveralWindowsAsBefore()
    {
        Save(
            Window(0, Tab("hello_world.pdf")),
            Window(1, Tab("text-basic.pdf"), Tab("vector-shapes.pdf")),
            Window(0, Tab("text-rich.pdf"), Tab("hello_world.pdf")));
        var workspace = CreateWorkspace();

        var viewModel = StartLikeProgram(workspace)!;

        // Further windows open only after the first sharp image of the first window.
        Assert.Same(viewModel, Assert.Single(workspace.Windows));
        await WaitUntilAsync(
            workspace,
            () => workspace.Windows.Count == 3 && workspace.Windows.Sum(window => window.Tabs.Count) == 5
                && workspace.Windows.All(window => window.Tabs.All(tab => tab.State == ViewerState.Ready)));
        Assert.True(viewModel.ActiveTab!.HasShownSharpPage);

        Assert.Same(viewModel, workspace.Windows[0]);
        Assert.Equal([TestData.Pdf("hello_world.pdf")], workspace.Windows[0].Tabs.Select(tab => tab.Session.Path));
        Assert.Equal([TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf")], workspace.Windows[1].Tabs.Select(tab => tab.Session.Path));
        Assert.Equal(TestData.Pdf("vector-shapes.pdf"), workspace.Windows[1].ActiveTab!.Session.Path);
        Assert.Equal([TestData.Pdf("text-rich.pdf"), TestData.Pdf("hello_world.pdf")], workspace.Windows[2].Tabs.Select(tab => tab.Session.Path));
        Assert.Equal(TestData.Pdf("text-rich.pdf"), workspace.Windows[2].ActiveTab!.Session.Path);
        Assert.All(workspace.Windows, window => Assert.True(workspace.WindowOf(window).IsVisible));
    }

    [AvaloniaFact]
    public async Task SkipsMissingFilesSilently()
    {
        var missing = Path.Combine(_directory, "moved.pdf");
        Save(
            new SessionWindow([new SessionTab(missing, null), Tab("hello_world.pdf"), new SessionTab(missing + "2", null)], ActiveIndex: 2),
            new SessionWindow([new SessionTab(missing + "3", null)], ActiveIndex: 0),
            Window(0, Tab("text-basic.pdf")));
        var workspace = CreateWorkspace();

        var viewModel = StartLikeProgram(workspace)!;
        await WaitUntilAsync(
            workspace,
            () => workspace.Windows.Count == 2 && workspace.Windows.All(window => window.Tabs.All(tab => tab.State == ViewerState.Ready)));
        await SettleForAWhileAsync(workspace);

        // No error card, no window for the missing files; the right neighbour of the missing active tab is gone too, so
        // the tab on its left is the active one.
        Assert.Equal(2, workspace.Windows.Count);
        Assert.Equal(TestData.Pdf("hello_world.pdf"), Assert.Single(viewModel.Tabs).Session.Path);
        Assert.Equal(TestData.Pdf("text-basic.pdf"), Assert.Single(workspace.Windows[1].Tabs).Session.Path);
        Assert.DoesNotContain(workspace.Windows.SelectMany(window => window.Tabs), tab => tab.IsFailed);
        Assert.Equal(2, Volatile.Read(ref _opens));
    }

    [AvaloniaFact]
    public async Task AskForThePasswordOfAProtectedPdf()
    {
        Save(
            Window(0, Tab("encrypted_hello_world_r3.pdf"), Tab("hello_world.pdf")),
            Window(0, Tab("hello_world.pdf"), Tab("encrypted_hello_world_r6.pdf")));
        var workspace = CreateWorkspace();

        var viewModel = StartLikeProgram(workspace)!;

        // The first file asks for its password (no sharp tile will come), so the rest does not wait for one.
        await WaitUntilAsync(
            workspace,
            () => workspace.Windows.Count == 2 && workspace.Windows.All(window => window.Tabs.Count == 2)
                && workspace.Windows.All(window => window.Tabs.All(tab => tab.State is ViewerState.PasswordRequired or ViewerState.Ready)));
        var locked = viewModel.Tabs[0];
        var lockedBehind = workspace.Windows[1].Tabs[1];
        Assert.Equal(ViewerState.PasswordRequired, locked.State);
        Assert.Equal(ViewerState.PasswordRequired, lockedBehind.State);
        Assert.Same(locked, viewModel.ActiveTab);
        Assert.Equal(ViewerState.Ready, viewModel.Tabs[1].State);

        // It opens as it would at any other time once the password is given.
        locked.Password = UserPassword;
        locked.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(workspace, () => locked.State == ViewerState.Ready);
    }

    [AvaloniaFact]
    public async Task ATabThatWasStillLoadingComesBack()
    {
        // Before the update: one tab is open, the other still loading.
        var gate = new TaskCompletionSource();
        var before = CreateWorkspace(path => path.EndsWith("thousand-pages.pdf", StringComparison.Ordinal) ? gate.Task : Task.CompletedTask);
        var window = before.OpenWindow();
        await window.OpenAsync(TestData.Pdf("hello_world.pdf"));
        _ = window.OpenAsync(TestData.Pdf("thousand-pages.pdf"));
        await WaitUntilAsync(before, () => window.Tabs.Count == 2 && window.Tabs[1].State == ViewerState.Loading);

        var snapshot = before.CaptureSession();
        gate.SetResult();

        // It is remembered with its path, without a view.
        var captured = Assert.Single(snapshot.Windows);
        Assert.Equal(TestData.Pdf("thousand-pages.pdf"), captured.Active.Path);
        Assert.Null(captured.Active.View);
        Assert.True(new SessionStore(FilePath).TrySave(snapshot));

        // After the update it opens.
        var workspace = CreateWorkspace();
        var viewModel = StartLikeProgram(workspace)!;
        await WaitUntilAsync(workspace, () => viewModel.Tabs.Count == 2 && viewModel.Tabs.All(tab => tab.State == ViewerState.Ready));
        Assert.Equal(TestData.Pdf("thousand-pages.pdf"), viewModel.ActiveTab!.Session.Path);
        Assert.Equal(TestData.Pdf("hello_world.pdf"), viewModel.Tabs[0].Session.Path);
    }

    [AvaloniaFact]
    public async Task NoFirstOpenCardAfterARestore()
    {
        Save(Window(0, Tab("text-basic.pdf")));
        var service = new FakeDefaultAppService();
        var workspace = CreateWorkspace(defaultAppService: service);

        var viewModel = StartLikeProgram(workspace)!;
        await WaitUntilAsync(workspace, () => viewModel.ActiveTab is { HasShownSharpPage: true });
        await SettleForAWhileAsync(workspace);

        Assert.Equal(0, service.StatusCalls);
        Assert.Null(viewModel.DefaultAppPrompt);

        // The same file opened without a restore asks (the check above is not vacuous).
        var control = new FakeDefaultAppService();
        var other = CreateWorkspace(defaultAppService: control);
        var session = CreateSession();
        _ = session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var otherWindow = other.OpenWindowFor(session);
        await WaitUntilAsync(other, () => otherWindow.DefaultAppPrompt is not null);
        Assert.Equal(1, control.StatusCalls);
    }

    [Fact]
    public void StartWithoutTheSwitchNeverReadsTheSessionFile()
    {
        Save(Window(0, Tab("hello_world.pdf")));
        var written = File.ReadAllText(FilePath);
        var stores = 0;
        var checks = 0;
        SessionStore Store()
        {
            stores++;
            return new SessionStore(FilePath);
        }

        bool Exists(string path)
        {
            checks++;
            return File.Exists(path);
        }

        Assert.Null(Program.LoadSession(StartupOptions.Parse([]), Store, Exists));
        Assert.Null(Program.LoadSession(StartupOptions.Parse([TestData.Pdf("hello_world.pdf")]), Store, Exists));
        Assert.Null(Program.LoadSession(StartupOptions.Parse(["--measure-startup", TestData.Pdf("hello_world.pdf"), "--restore-session"]), Store, Exists));
        Assert.Null(Program.LoadSession(new StartupOptions([], TestData.Pdf("hello_world.pdf"), null, null, null, RestoreSession: true), Store, Exists));

        Assert.Equal(0, stores);
        Assert.Equal(0, checks);
        Assert.Equal(written, File.ReadAllText(FilePath));
        Assert.Equal([FilePath], Directory.GetFiles(_directory));
    }

    [Fact]
    public void TheFileIsConsumedOnce()
    {
        Save(Window(0, Tab("hello_world.pdf")));
        var options = StartupOptions.Parse(["--restore-session"]);

        var restored = Program.LoadSession(options, () => new SessionStore(FilePath), File.Exists);

        Assert.Equal(TestData.Pdf("hello_world.pdf"), Assert.Single(restored!.Windows).Active.Path);
        Assert.False(File.Exists(FilePath));
        Assert.Null(Program.LoadSession(options, () => new SessionStore(FilePath), File.Exists));

        // A session whose files are all gone restores nothing, and is consumed as well.
        Save(new SessionWindow([new SessionTab(Path.Combine(_directory, "gone.pdf"), null)], 0));
        Assert.Null(Program.LoadSession(options, () => new SessionStore(FilePath), File.Exists));
        Assert.False(File.Exists(FilePath));
    }

    private static SessionTab Tab(string name, RememberedView? view = null) => new(TestData.Pdf(name), view);

    private static SessionWindow Window(int activeIndex, params SessionTab[] tabs) => new(tabs, activeIndex);

    private void Save(params SessionWindow[] windows) =>
        Assert.True(new SessionStore(FilePath).TrySave(new SessionSnapshot(windows)));

    /// <summary>
    /// What <see cref="Program.Main"/> and <see cref="App"/> do with <c>--restore-session</c>: read the session once, start
    /// opening the active file of the first window early, then let the workspace restore the windows.
    /// </summary>
    private MainViewModel? StartLikeProgram(Workspace workspace)
    {
        var restore = Program.LoadSession(StartupOptions.Parse(["--restore-session"]), () => new SessionStore(FilePath), File.Exists);
        if (restore is null)
        {
            return null;
        }

        var session = CreateSession();
        _ = session.OpenAsync(restore.Windows[0].Active.Path);
        return workspace.RestoreSession(restore, session);
    }

    private Workspace CreateWorkspace(Func<string, Task>? beforeOpen = null, FakeDefaultAppService? defaultAppService = null)
    {
        // The windows do not activate when they are shown, so only the tests activate them.
        var workspace = new Workspace(
            () => CreateSession(beforeOpen),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => new NoFilePicker(),
            defaultAppServiceFactory: defaultAppService is null ? null : () => defaultAppService);
        _cleanup.Add(workspace);
        return workspace;
    }

    private ViewerSession CreateSession(Func<string, Task>? beforeOpen = null) =>
        new(async (path, password, cancellationToken) =>
        {
            Interlocked.Increment(ref _opens);
            if (beforeOpen is not null)
            {
                await beforeOpen(path).WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await _engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        });

    private static async Task SettleForAWhileAsync(Workspace workspace)
    {
        for (var i = 0; i < 10; i++)
        {
            await WaitUntilAsync(workspace, static () => true);
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Workspace workspace, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                foreach (var viewModel in workspace.Windows)
                {
                    workspace.WindowOf(viewModel).UpdateLayout();
                }
            }

            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The windows did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }
}
