using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class TabsTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private string? _tempDirectory;

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        if (_tempDirectory is not null)
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task OpeningASecondFileAddsATabRightOfTheActiveOne()
    {
        var (window, viewModel) = Show();
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        var second = await OpenReadyAsync(window, viewModel, "text-basic.pdf");
        Assert.Equal([first, second], viewModel.Tabs);

        // A third file opened while the first tab is active lands between the two.
        viewModel.ActivateTabCommand.Execute(first);
        var third = await OpenReadyAsync(window, viewModel, "vector-shapes.pdf");

        Assert.Equal([first, third, second], viewModel.Tabs);
        Assert.Same(third, viewModel.ActiveTab);
        Assert.True(third.IsActive);
        Assert.False(first.IsActive);
        Assert.False(second.IsActive);
        Assert.Equal("vector-shapes.pdf", Find<MiddleEllipsisTextBlock>(window, "TabName").Text);
    }

    [AvaloniaFact]
    public async Task OpeningAnOpenFileActivatesItsTab()
    {
        var (window, viewModel) = Show();
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        var second = await OpenReadyAsync(window, viewModel, "text-basic.pdf");
        var document = first.Session.Document;

        await viewModel.OpenAsync(TestData.Pdf("hello_world.pdf"));
        Settle(window);

        // No new tab, no new open: the tab of the file comes to the front with its document.
        Assert.Equal([first, second], viewModel.Tabs);
        Assert.Same(first, viewModel.ActiveTab);
        Assert.Same(document, first.Session.Document);

        // The same file given with another spelling of its path is the same file.
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, TestData.Pdf("text-basic.pdf"));
        await viewModel.OpenAsync(relative);
        Settle(window);
        Assert.Equal(2, viewModel.Tabs.Count);
        Assert.Same(second, viewModel.ActiveTab);

        // An empty path does nothing.
        await viewModel.OpenAsync(string.Empty);
        Assert.Equal(2, viewModel.Tabs.Count);
    }

    [AvaloniaFact]
    public async Task OpeningAFileWhoseTabFailedTriesAgainInThatTab()
    {
        var opens = 0;
        var engine = new PdfEngine();
        var (window, viewModel) = Show(factory: () => new ViewerSession((path, password, cancellationToken) =>
        {
            Interlocked.Increment(ref opens);
            return engine.OpenAsync(path, password, cancellationToken);
        }));
        var path = Path.Combine(TempDirectory(), "later.pdf");
        await viewModel.OpenAsync(path);
        var failed = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => failed.State == ViewerState.Failed);
        var other = await OpenReadyAsync(window, viewModel, "text-basic.pdf");
        Assert.Equal(2, Volatile.Read(ref opens));

        // Still missing: the same tab tries again, comes to the front and shows the error again; no second tab.
        await viewModel.OpenAsync(path);
        await WaitUntilAsync(window, () => failed.State == ViewerState.Failed);
        Assert.Equal(3, Volatile.Read(ref opens));
        Assert.Equal([failed, other], viewModel.Tabs);
        Assert.Same(failed, viewModel.ActiveTab);
        Assert.Equal(PdfOpenError.FileNotFound, failed.Error);

        // Readable now (for example copied or unlocked meanwhile): the same tab opens it.
        viewModel.ActivateTabCommand.Execute(other);
        File.Copy(TestData.Pdf("hello_world.pdf"), path);
        await viewModel.OpenAsync(path);
        await WaitUntilAsync(window, () => failed.State == ViewerState.Ready);
        Assert.Equal(4, Volatile.Read(ref opens));
        Assert.Equal([failed, other], viewModel.Tabs);
        Assert.Same(failed, viewModel.ActiveTab);
        Assert.Equal(path, failed.FullPath);

        // An open document is not opened again: its tab only comes to the front.
        viewModel.ActivateTabCommand.Execute(other);
        await viewModel.OpenAsync(path);
        Settle(window);
        Assert.Same(failed, viewModel.ActiveTab);
        Assert.Equal(4, Volatile.Read(ref opens));
    }

    [AvaloniaFact]
    public async Task OpeningAFileWhoseTabAsksForAPasswordOnlyActivatesIt()
    {
        var opens = 0;
        var engine = new PdfEngine();
        var (window, viewModel) = Show(factory: () => new ViewerSession((path, password, cancellationToken) =>
        {
            Interlocked.Increment(ref opens);
            return engine.OpenAsync(path, password, cancellationToken);
        }));
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        var locked = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => locked.State == ViewerState.PasswordRequired);
        var other = await OpenReadyAsync(window, viewModel, "text-basic.pdf");

        // The password card stays as it is (with whatever was typed into it); nothing is opened again.
        locked.Password = "typed";
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        Settle(window);
        Assert.Equal([locked, other], viewModel.Tabs);
        Assert.Same(locked, viewModel.ActiveTab);
        Assert.Equal(ViewerState.PasswordRequired, locked.State);
        Assert.Equal("typed", locked.Password);
        Assert.Equal(2, Volatile.Read(ref opens));
    }

    [AvaloniaFact]
    public async Task ClosingTheActiveTabActivatesTheRightNeighbour()
    {
        var (window, viewModel) = Show();
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        var second = await OpenReadyAsync(window, viewModel, "text-basic.pdf");
        var third = await OpenReadyAsync(window, viewModel, "vector-shapes.pdf");
        viewModel.ActivateTabCommand.Execute(second);
        Settle(window);

        viewModel.CloseActiveTabCommand.Execute(null);
        Settle(window);

        Assert.Equal([first, third], viewModel.Tabs);
        Assert.Same(third, viewModel.ActiveTab);
        Assert.True(third.IsActive);
        Assert.Equal(ViewerState.Empty, second.Session.State);
        Assert.Equal("vector-shapes.pdf", Find<MiddleEllipsisTextBlock>(window, "TabName").Text);

        // The document view follows: it shows the document of the new active tab.
        var view = Find<DocumentView>(window, "DocumentView");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, third.Session.Document));

        // Closing the rightmost tab activates its left neighbour; CloseTabCommand closes the tab it is given.
        viewModel.CloseTabCommand.Execute(third);
        Settle(window);
        Assert.Equal([first], viewModel.Tabs);
        Assert.Same(first, viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task ClosingTheLastTabShowsTheStartScreen()
    {
        var (window, viewModel) = Show();
        var tab = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        Assert.False(viewModel.IsEmpty);
        Assert.True(viewModel.HasTabs);

        Find<Button>(window, "CloseTabButton").Command!.Execute(null);
        Settle(window);

        Assert.Empty(viewModel.Tabs);
        Assert.Null(viewModel.ActiveTab);
        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.HasTabs);
        Assert.Equal(ViewerState.Empty, tab.Session.State);
        Assert.True(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "AppTitle").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "Tab").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "DocumentArea").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "StatusBarRow").IsEffectivelyVisible);
        Assert.Null(Find<DocumentView>(window, "DocumentView").Document);
    }

    [AvaloniaFact]
    public async Task ClosingALoadingTabDisposesItsSession()
    {
        // The open waits until the test lets it go on and then opens the file even when it was cancelled meanwhile,
        // so a result arrives after the tab was closed.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var engine = new PdfEngine();
        var (window, viewModel) = Show(factory: () => new ViewerSession(async (path, password, cancellationToken) =>
        {
            started.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            cancelled = cancellationToken.IsCancellationRequested;
            return await engine.OpenAsync(path, password, CancellationToken.None).ConfigureAwait(false);
        }));

        var opening = viewModel.OpenAsync(TestData.Pdf("hello_world.pdf"));
        Settle(window);
        var tab = Assert.Single(viewModel.Tabs);
        Assert.Equal(ViewerState.Loading, tab.State);
        Assert.True(started.Task.IsCompleted);
        var changes = 0;
        tab.Session.Changed += () => Interlocked.Increment(ref changes);

        tab.CloseCommand.Execute(null);
        Settle(window);
        Assert.Empty(viewModel.Tabs);
        Assert.True(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
        Interlocked.Exchange(ref changes, 0);

        // The session cancelled the request, drops the late result and raises nothing more.
        gate.SetResult();
        await opening.WaitAsync(Timeout);
        await Task.Delay(100);
        Settle(window);
        Assert.True(cancelled);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.Equal(ViewerState.Empty, tab.Session.State);
        Assert.Null(tab.Session.Document);
        Assert.Empty(viewModel.Tabs);
    }

    [AvaloniaFact]
    public async Task ClosingATabWithAPasswordPromptWorks()
    {
        var (window, viewModel) = Show();
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        var locked = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => locked.State == ViewerState.PasswordRequired);
        Assert.True(Find<Control>(window, "PasswordCard").IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "TabLockIcon").IsEffectivelyVisible);
        locked.Password = "typed";

        Find<Button>(window, "CloseTabButton").Command!.Execute(null);
        Settle(window);

        Assert.Equal([first], viewModel.Tabs);
        Assert.Same(first, viewModel.ActiveTab);
        Assert.Equal(ViewerState.Empty, locked.Session.State);
        Assert.False(Find<Control>(window, "PasswordCard").IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "StatusBar").IsEffectivelyVisible);
        Assert.Equal(string.Empty, Find<TextBox>(window, "PasswordField").Text ?? string.Empty);
    }

    [AvaloniaFact]
    public async Task ErrorCardOpenOtherReplacesTheDocumentInItsTab()
    {
        var picker = new FakePicker(TestData.Pdf("text-basic.pdf"));
        var (window, viewModel) = Show(picker);
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        await viewModel.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));
        var failed = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => failed.State == ViewerState.Failed);
        Assert.True(Find<Control>(window, "TabErrorIcon").IsEffectivelyVisible);

        Find<Button>(window, "ErrorOpenOtherButton").Command!.Execute(null);
        await WaitUntilAsync(window, () => failed.State == ViewerState.Ready);

        Assert.Equal(1, picker.Calls);
        Assert.Equal([first, failed], viewModel.Tabs);
        Assert.Same(failed, viewModel.ActiveTab);
        Assert.Equal(TestData.Pdf("text-basic.pdf"), failed.FullPath);
        Assert.False(Find<Control>(window, "ErrorCard").IsEffectivelyVisible);

        // A cancelled dialog leaves the tab as it is; a failing one shows the error card in this tab.
        picker.Result = null;
        failed.OpenOtherCommand.Execute(null);
        Settle(window);
        Assert.Equal(ViewerState.Ready, failed.State);
        picker.Failure = new InvalidOperationException("No file chooser portal.");
        failed.OpenOtherCommand.Execute(null);
        await WaitUntilAsync(window, () => failed.State == ViewerState.Failed);
        Assert.Equal(2, viewModel.Tabs.Count);
        Assert.Equal(PdfOpenError.Unknown, failed.Error);
        Assert.Contains("System.InvalidOperationException: No file chooser portal.", failed.ErrorDetailsText, StringComparison.Ordinal);
        Assert.Equal(ViewerState.Ready, first.State);
    }

    [AvaloniaFact]
    public async Task CancelOnThePasswordCardClosesTheTab()
    {
        var (window, viewModel) = Show();
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        var locked = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => locked.State == ViewerState.PasswordRequired);

        Find<Button>(window, "PasswordCancelButton").Command!.Execute(null);
        Settle(window);

        Assert.Equal([first], viewModel.Tabs);
        Assert.Same(first, viewModel.ActiveTab);
        Assert.Equal(ViewerState.Empty, locked.Session.State);
        Assert.False(Find<Control>(window, "PasswordCard").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task DroppingThreeFilesOpensThreeTabs()
    {
        var (window, viewModel) = Show();
        string[] names = ["hello_world.pdf", "text-basic.pdf", "vector-shapes.pdf"];
        var items = new List<IStorageItem>();
        foreach (var name in names)
        {
            items.Add(await FileAsync(window, TestData.Pdf(name)));
        }

        Drop(window, [.. items]);
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 3 && viewModel.Tabs.All(tab => tab.State == ViewerState.Ready));

        // In the order of the drop; the last one is active.
        Assert.Equal(names.Select(TestData.Pdf), viewModel.Tabs.Select(tab => tab.FullPath));
        Assert.Same(viewModel.Tabs[2], viewModel.ActiveTab);
        Assert.Equal("vector-shapes.pdf", Find<MiddleEllipsisTextBlock>(window, "TabName").Text);
    }

    [AvaloniaFact]
    public async Task DroppingMoreThanTwentyFilesOpensTwenty()
    {
        var (window, viewModel) = Show();
        var items = new List<IStorageItem>();
        var paths = new List<string>();
        for (var i = 0; i < MainViewModel.MaxFilesPerOpen + 5; i++)
        {
            var path = CopyWithName($"file-{i:00}.pdf");
            paths.Add(path);
            items.Add(await FileAsync(window, path));
        }

        Drop(window, [.. items]);
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == MainViewModel.MaxFilesPerOpen && viewModel.Tabs.All(tab => tab.State == ViewerState.Ready));

        Assert.Equal(20, MainViewModel.MaxFilesPerOpen);
        Assert.Equal(paths.Take(MainViewModel.MaxFilesPerOpen), viewModel.Tabs.Select(tab => tab.FullPath));
        Assert.Same(viewModel.Tabs[^1], viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task ADirectoryOrEmptyFileGetsItsOwnErrorTab()
    {
        var (window, viewModel) = Show();
        var folder = Directory.CreateDirectory(Path.Combine(TempDirectory(), "folder")).FullName;
        var empty = Path.Combine(TempDirectory(), "empty.pdf");
        File.WriteAllBytes(empty, []);
        var valid = TestData.Pdf("hello_world.pdf");

        Drop(window, await FolderAsync(window, folder), await FileAsync(window, empty), await FileAsync(window, valid));
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 3 && viewModel.Tabs.All(tab => tab.State is ViewerState.Ready or ViewerState.Failed));

        var (folderTab, emptyTab, validTab) = (viewModel.Tabs[0], viewModel.Tabs[1], viewModel.Tabs[2]);
        Assert.Equal(ViewerState.Failed, folderTab.State);
        Assert.Equal(PdfOpenError.CannotRead, folderTab.Error);
        Assert.Equal(ViewerState.Failed, emptyTab.State);
        Assert.Equal(empty, emptyTab.FullPath);
        Assert.Equal(ViewerState.Ready, validTab.State);

        // Each failure is shown in its own tab, the others are not affected.
        viewModel.ActivateTabCommand.Execute(emptyTab);
        Settle(window);
        Assert.True(Find<Control>(window, "ErrorCard").IsEffectivelyVisible);
        Assert.Contains("empty.pdf", Find<TextBlock>(window, "ErrorCause").Text, StringComparison.Ordinal);
        viewModel.ActivateTabCommand.Execute(validTab);
        Settle(window);
        Assert.False(Find<Control>(window, "ErrorCard").IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "StatusBar").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task NextTabCommandCyclesThroughTheTabs()
    {
        var (window, viewModel) = Show();
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        var second = await OpenReadyAsync(window, viewModel, "text-basic.pdf");
        var third = await OpenReadyAsync(window, viewModel, "vector-shapes.pdf");

        viewModel.NextTabCommand.Execute(null);
        Assert.Same(first, viewModel.ActiveTab);
        viewModel.NextTabCommand.Execute(null);
        Assert.Same(second, viewModel.ActiveTab);
        viewModel.NextTabCommand.Execute(null);
        Assert.Same(third, viewModel.ActiveTab);

        viewModel.PreviousTabCommand.Execute(null);
        Assert.Same(second, viewModel.ActiveTab);
        viewModel.PreviousTabCommand.Execute(null);
        viewModel.PreviousTabCommand.Execute(null);
        Assert.Same(third, viewModel.ActiveTab);
        Assert.Equal([false, false, true], viewModel.Tabs.Select(tab => tab.IsActive));
    }

    [AvaloniaFact]
    public async Task WindowTitleFollowsTheActiveTab()
    {
        var (window, viewModel) = Show();
        Assert.Equal(Strings.AppName, window.Title);

        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        Assert.Equal($"hello_world.pdf – {Strings.AppName}", window.Title);
        await OpenReadyAsync(window, viewModel, "text-basic.pdf");
        Assert.Equal($"text-basic.pdf – {Strings.AppName}", viewModel.WindowTitle);
        Assert.Equal(viewModel.WindowTitle, window.Title);

        viewModel.ActivateTabCommand.Execute(first);
        Settle(window);
        Assert.Equal($"hello_world.pdf – {Strings.AppName}", window.Title);

        // A document replaced in the active tab renames the window.
        await first.OpenAsync(TestData.Pdf("vector-shapes.pdf"));
        await WaitUntilAsync(window, () => first.State == ViewerState.Ready);
        Assert.Equal($"vector-shapes.pdf – {Strings.AppName}", window.Title);

        viewModel.CloseTabCommand.Execute(first);
        viewModel.CloseActiveTabCommand.Execute(null);
        Settle(window);
        Assert.Equal(Strings.AppName, window.Title);
    }

    [AvaloniaFact]
    public async Task OnExternalOpenOpensTabsAndRequestsActivation()
    {
        var (window, viewModel) = Show();
        var activations = 0;
        viewModel.ActivationRequested += () => activations++;
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        string[] handedOver = [TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf")];

        // As the single-instance listener does it: from a pool thread, in this process.
        await Task.Run(() => viewModel.OnExternalOpen(handedOver));
        await WaitUntilAsync(window, () => activations == 1);
        await WaitUntilAsync(window, () => viewModel.Tabs.All(tab => tab.State == ViewerState.Ready));

        Assert.Equal([TestData.Pdf("hello_world.pdf"), .. handedOver], viewModel.Tabs.Select(tab => tab.FullPath));
        Assert.Same(viewModel.Tabs[2], viewModel.ActiveTab);
        Assert.Same(first, viewModel.Tabs[0]);
    }

    [AvaloniaFact]
    public async Task EachTabKeepsItsOwnPageAndZoom()
    {
        var (window, viewModel) = Show();
        var view = Find<DocumentView>(window, "DocumentView");
        var a = await OpenReadyAsync(window, viewModel, "thousand-pages.pdf");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document));
        a.Zoom = 2;
        Settle(window);
        a.GoToPage(40);
        Settle(window);
        Assert.Equal(40, a.CurrentPage);

        var b = await OpenReadyAsync(window, viewModel, "text-basic.pdf");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, b.Session.Document));

        // The new tab starts at 100 % on its first page, the zoom and the page of the inactive tab stay.
        Assert.Equal(1.0, b.Zoom);
        Assert.Equal(1.0, view.Layout!.Zoom);
        Assert.Equal(1, b.CurrentPage);
        Assert.Equal(b.ZoomText, Find<TextBlock>(window, "ZoomValueText").Text);
        b.Zoom = 1.25;
        Settle(window);
        Assert.Equal(2.0, a.Zoom);
        Assert.Equal(40, a.CurrentPage);

        // Back to A: 200 % in the view model, the layout and the status bar; B keeps its own zoom.
        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document));
        Assert.Equal(2.0, a.Zoom);
        Assert.Equal(2.0, view.Layout!.Zoom);
        Assert.Equal(a.ZoomText, Find<TextBlock>(window, "ZoomValueText").Text);
        Assert.Equal(1.25, b.Zoom);
    }

    [AvaloniaFact]
    public async Task InactiveTabsHaveTheInactiveBudget()
    {
        var (window, viewModel) = Show();
        var first = await OpenReadyAsync(window, viewModel, "hello_world.pdf");
        Assert.True(first.Session.IsActive);
        Assert.Equal(TabBudget.ActiveBytes, first.Session.Cache!.BudgetBytes);

        var second = await OpenReadyAsync(window, viewModel, "text-basic.pdf");

        Assert.False(first.IsActive);
        Assert.False(first.Session.IsActive);
        Assert.Equal(TabBudget.InactiveBytes, first.Session.Cache!.BudgetBytes);
        Assert.True(second.Session.IsActive);
        Assert.Equal(TabBudget.ActiveBytes, second.Session.Cache!.BudgetBytes);

        viewModel.ActivateTabCommand.Execute(first);
        Assert.True(first.Session.IsActive);
        Assert.Equal(TabBudget.ActiveBytes, first.Session.Cache!.BudgetBytes);
        Assert.False(second.Session.IsActive);
        Assert.Equal(TabBudget.InactiveBytes, second.Session.Cache!.BudgetBytes);
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(IFilePicker? picker = null, Func<ViewerSession>? factory = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(factory ?? (() => new ViewerSession(new PdfEngine())), picker ?? new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720 };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
    }

    /// <summary>Opens a test file in a new tab and waits until it is ready; returns that tab.</summary>
    private static async Task<TabViewModel> OpenReadyAsync(Window window, MainViewModel viewModel, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready);
        return tab;
    }

    private string CopyWithName(string fileName)
    {
        var path = Path.Combine(TempDirectory(), fileName);
        File.Copy(TestData.Pdf("hello_world.pdf"), path, overwrite: true);
        return path;
    }

    private string TempDirectory()
    {
        _tempDirectory ??= Directory.CreateTempSubdirectory("pagewing-app-tests-").FullName;
        return _tempDirectory;
    }

    private static async Task<IStorageItem> FileAsync(Window window, string path) =>
        await window.StorageProvider.TryGetFileFromPathAsync(path) ?? throw new InvalidOperationException($"No storage file for {path}.");

    private static async Task<IStorageItem> FolderAsync(Window window, string path) =>
        await window.StorageProvider.TryGetFolderFromPathAsync(path) ?? throw new InvalidOperationException($"No storage folder for {path}.");

    private static void Drop(Window window, params IStorageItem[] items)
    {
        var data = new DataTransfer();
        foreach (var item in items)
        {
            data.Add(DataTransferItem.CreateFile(item));
        }

        var point = new Point(window.Width / 2, window.Height / 2);
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The window did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private sealed class FakePicker(string? result) : IFilePicker
    {
        public string? Result { get; set; } = result;

        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        public Task<string?> PickPdfAsync()
        {
            Calls++;
            return Failure is { } failure ? Task.FromException<string?>(failure) : Task.FromResult(Result);
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
