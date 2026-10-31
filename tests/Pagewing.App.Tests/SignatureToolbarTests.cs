using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Signatures;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>The entry "Signature" of the toolbar and the "Comments" panel (milestone 12, task 12).</summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class SignatureToolbarTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateTempSubdirectory("pagewing-signature-toolbar-").FullName;
    private static readonly string[] Texts = ["First", "Second"];

    private SignatureStore? _store;

    public SignatureToolbarTests() => Strings.Culture = new CultureInfo("en");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SignatureStore Store => _store ??= SignatureStore.Load(Path.Combine(_folder, "signatures"));

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private SavedSignature AddDrawn(string name = "Work") =>
        Store.AddDrawn(name, [[new(0, 80), new(40, 10), new(80, 90), new(120, 20)], [new(10, 95), new(110, 95)]], DrawColor.Black);

    private SavedSignature AddImage(string name = "Scan")
    {
        var bgra = new byte[100 * 40 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 120;
            bgra[i + 1] = 30;
            bgra[i + 2] = 30;
            bgra[i + 3] = 255;
        }

        return Store.AddImage(name, SignatureImageCodec.EncodePng(new SignatureImage(100, 40, bgra)), 2.5);
    }

    [AvaloniaTheory]
    [InlineData("en", 640)]
    [InlineData("de", 640)]
    [InlineData("en", 1024)]
    [InlineData("de", 1400)]
    public async Task SignatureEntryIsReachableInANarrowDocumentArea(string language, double width)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, _, toolbar) = await ShowInModeAsync();
        window.Width = width;
        Settle(window);
        Settle(window);
        AddDrawn();

        var button = Find<Button>(toolbar, "SignatureTool");
        Assert.Equal(Strings.ToolSignature, AutomationProperties.GetName(button));
        if (!toolbar.AreShapesInMenu)
        {
            // In the row, with the tools, before "Done".
            Assert.True(button.IsEffectivelyVisible);
            var done = Find<Button>(toolbar, "DoneButton");
            Assert.True(button.TranslatePoint(new Point(button.Bounds.Width, 0), toolbar)!.Value.X <= done.TranslatePoint(default, toolbar)!.Value.X);
            return;
        }

        // It leaves with the shapes, never before them, and is then a submenu of the menu "⋯".
        Assert.False(button.IsVisible);
        var more = Find<Button>(toolbar, "MoreButton");
        Assert.True(more.IsEffectivelyVisible);
        more.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);
        var menu = Assert.IsType<MenuFlyout>(more.Flyout);
        await WaitUntilAsync(window, () => menu.IsOpen);
        var submenu = Assert.Single(menu.Items.OfType<MenuItem>(), entry => entry.Name == "MoreSignature");
        Assert.Equal(Strings.ToolSignature, submenu.Header);
        Assert.Contains(submenu.Items.OfType<MenuItem>(), entry => entry.Name == "MoreSignatureNew");
        Assert.Contains(submenu.Items.OfType<MenuItem>(), entry => (string?)entry.Header == "Work");
    }

    [AvaloniaFact]
    public async Task MenuListsTheLibraryAndTheNewEntry()
    {
        var (window, viewModel, toolbar) = await ShowInModeAsync();
        window.Width = 1400;
        Settle(window);
        var drawn = AddDrawn("Work");
        var image = AddImage("Scan");
        var button = Find<Button>(toolbar, "SignatureTool");

        // Nothing of the library is read before the menu opens (it was set up through the store, not the window).
        Assert.False(toolbar.IsSignatureMenuOpen);
        button.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => toolbar.IsSignatureMenuOpen);
        var menu = toolbar.SignatureMenu!;

        Assert.Equal(Strings.SignatureMenuName, AutomationProperties.GetName(menu));
        foreach (var signature in new[] { drawn, image })
        {
            var entry = FindIn<Button>(menu, "SignatureEntry" + signature.Id);
            Assert.Equal(signature.Name, AutomationProperties.GetName(entry));
            var delete = FindIn<Button>(menu, "SignatureDelete" + signature.Id);
            Assert.Contains(signature.Name, AutomationProperties.GetName(delete));
            Assert.Contains(Strings.SignatureDelete, AutomationProperties.GetName(delete));
        }

        Assert.Equal(Strings.SignatureMenuNew, AutomationProperties.GetName(FindIn<Button>(menu, "SignatureMenuNew")));

        // Delete removes the entry and its file; the menu shows the rest.
        FindIn<Button>(toolbar.SignatureMenu!, "SignatureDelete" + drawn.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.DoesNotContain(Store.Items, s => s.Id == drawn.Id);
        Assert.Null(FindOrNull<Button>(toolbar.SignatureMenu!, "SignatureEntry" + drawn.Id));
        Assert.NotNull(FindOrNull<Button>(toolbar.SignatureMenu!, "SignatureEntry" + image.Id));

        // "New signature …" opens the dialog and closes the menu.
        FindIn<Button>(toolbar.SignatureMenu!, "SignatureMenuNew").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.Signature);
        Assert.False(toolbar.IsSignatureMenuOpen);
    }

    [AvaloniaFact]
    public async Task ChoosingAnEntryWithTheKeyboardPlacesItAtOnce()
    {
        var (window, viewModel, toolbar) = await ShowInModeAsync();
        window.Width = 1400;
        Settle(window);
        var drawn = AddDrawn("Work");
        var tab = viewModel.ActiveTab!;

        viewModel.InsertSignatureCommand.Execute(null);
        await WaitUntilAsync(window, () => toolbar.IsSignatureMenuOpen);
        var entry = FindIn<Button>(toolbar.SignatureMenu!, "SignatureEntry" + drawn.Id);

        // The pointer is not over the entry: a click of it counts as the keyboard's.
        entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is { Kind: AnnotationKind.Signature });

        Assert.Single(await tab.Session.Document!.GetAnnotationsAsync(0, Ct), a => a.Kind == AnnotationKind.Signature);
        Assert.Null(tab.PendingSignature);
        Assert.False(toolbar.IsSignatureMenuOpen);
    }

    [AvaloniaFact]
    public async Task KeyboardOnlyUseWorks()
    {
        var (window, viewModel, toolbar) = await ShowInModeAsync();
        window.Width = 1400;
        Settle(window);
        var drawn = AddDrawn("Work");
        var button = Find<Button>(toolbar, "SignatureTool");

        // Tab reaches the entry, Enter opens the menu, Tab goes through the entries, Esc closes it again.
        button.Focus(NavigationMethod.Tab);
        Settle(window);
        Assert.True(button.IsFocused);
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => toolbar.IsSignatureMenuOpen);
        var entry = FindIn<Button>(toolbar.SignatureMenu!, "SignatureEntry" + drawn.Id);
        entry.Focus(NavigationMethod.Tab);
        Settle(window);
        Assert.True(entry.IsFocused);
        foreach (var name in new[] { "SignatureDelete" + drawn.Id, "SignatureMenuNew" })
        {
            Press(window, Key.Tab, PhysicalKey.Tab);
            Assert.True(FindIn<Button>(toolbar.SignatureMenu!, name).IsFocused, $"Tab reaches {name}");
        }

        Press(window, Key.Escape, PhysicalKey.Escape);
        await WaitUntilAsync(window, () => !toolbar.IsSignatureMenuOpen);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
    }

    [AvaloniaFact]
    public async Task ListsSignaturesWithTheirOwnKind()
    {
        var tab = await OpenAsync("text-basic.pdf");
        await tab.Session.Document!.CreateDrawingAsync(0, DrawingGeometry.Signature([[new(30, 80), new(60, 30), new(90, 90)]], 2), new AnnotationColor(0, 0, 0), null, null, Ct);
        var image = new SignatureImage(10, 4, [.. Enumerable.Repeat((byte)200, 10 * 4 * 4)]);
        await tab.Session.Document.CreateSignatureImageAsync(0, new(150, 100, 250, 140), image, null, Ct);
        await tab.Session.Document.CreateDrawingAsync(0, DrawingGeometry.Ink([[new(30, 150), new(80, 190)]], 2), new AnnotationColor(220, 30, 30), null, null, Ct);
        var comments = await OpenCommentsAsync(tab);

        var signatures = comments.Items.Where(item => item.Kind == AnnotationKind.Signature).ToList();
        Assert.Equal(2, signatures.Count);
        Assert.All(signatures, item =>
        {
            Assert.Equal(Strings.ToolSignature, item.KindText);
            Assert.True(item.IsSignature);
            Assert.False(item.IsInk);
            Assert.True(item.IsDrawing);
            Assert.False(item.IsOther);
        });
        Assert.Equal(Strings.ToolInk, comments.Items.Single(item => item.IsInk).KindText);

        // The filter "Drawings" has them.
        comments.Filter = CommentFilter.Drawings;
        Assert.Equal(3, comments.Items.Count);
        Assert.Equal(2, comments.Items.Count(item => item.IsSignature));

        // An image has neither colour nor width to change in the panel; a drawn one has its colour.
        comments.SelectedItem = comments.Items.Single(item => item.Annotation.IsSignatureImage);
        Assert.False(comments.SelectedCanChangeColor);
        comments.SelectedItem = comments.Items.Single(item => item is { IsSignature: true, Annotation.IsSignatureImage: false });
        Assert.True(comments.SelectedCanChangeColor);
    }

    [AvaloniaFact]
    public async Task ForeignStampStaysOther()
    {
        var tab = await OpenAsync("stamps-foreign.pdf");
        var comments = await OpenCommentsAsync(tab);

        var stamps = comments.Items.Where(item => item.Annotation.Subtype == "Stamp").ToList();
        Assert.Equal(3, stamps.Count);
        Assert.All(stamps, item =>
        {
            Assert.True(item.IsOther);
            Assert.False(item.IsSignature);
            Assert.False(item.IsDrawing);
            Assert.Contains("Stamp", item.KindText, StringComparison.Ordinal);
        });

        // The ink with the subject "Signature" is listed as a signature (it is one; Pagewing cannot redraw it, it keeps its comment and deleting).
        var ink = comments.Items.Single(item => item.Annotation.Subtype == "Ink");
        Assert.True(ink.IsSignature);
    }

    [AvaloniaFact]
    public async Task SignatureCommentCanBeEdited()
    {
        var tab = await OpenAsync("text-basic.pdf");
        var image = new SignatureImage(10, 4, [.. Enumerable.Repeat((byte)200, 10 * 4 * 4)]);
        await tab.Session.Document!.CreateSignatureImageAsync(0, new(150, 100, 250, 140), image, null, Ct);
        await tab.Session.Document.CreateDrawingAsync(0, DrawingGeometry.Signature([[new(30, 80), new(60, 30), new(90, 90)]], 2), new AnnotationColor(0, 0, 0), null, null, Ct);
        tab.AnnotationSettings = UserSettings.Detached();
        var comments = await OpenCommentsAsync(tab);
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsAnnotating);

        foreach (var (item, text) in comments.Items.Where(i => i.IsSignature).ToList().Zip(Texts))
        {
            comments.SelectedItem = item;
            Assert.True(comments.SelectedIsEditable);
            Assert.False(comments.SelectedCommentReadOnly);
            comments.CommentDraft = text;
            comments.CommitCommentCommand.Execute(null);
            await WaitUntilAsync(() => !tab.HasEditsInFlight);
        }

        var listed = await tab.Session.Document.GetAnnotationsAsync(0, Ct);
        Assert.Equal(["First", "Second"], listed.OrderBy(a => a.Index).Select(a => a.Contents).Where(c => c is not null).Order());
        Assert.All(listed, a => Assert.Equal(AnnotationKind.Signature, a.Kind));
        Assert.Contains(listed, a => a.IsSignatureImage);
    }

    private async Task<(MainWindow Window, MainViewModel ViewModel, AnnotationToolbar Toolbar)> ShowInModeAsync()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        viewModel.SignatureStoreProvider = () => Store;
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1400, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = Find<DocumentView>(window, "DocumentView");
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8] + "-text-rich.pdf");
        File.Copy(TestData.Pdf("text-rich.pdf"), path);
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        return (window, viewModel, Find<AnnotationToolbar>(window, "AnnotationToolbar"));
    }

    private async Task<TabViewModel> OpenAsync(string name)
    {
        var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(tab);
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.Copy(TestData.Pdf(name), path);
        await tab.OpenAsync(path);
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null);
        tab.HasShownSharpPage = true;
        return tab;
    }

    private static async Task<CommentsViewModel> OpenCommentsAsync(TabViewModel tab)
    {
        tab.SidePanel = SidePanelKind.Comments;
        var comments = tab.Comments;
        await WaitUntilAsync(() => !comments.IsLoading);
        return comments;
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey)
    {
        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
        Settle(window);
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

            await Task.Delay(10, Ct);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The view model did not reach the expected state.");
            }

            await Task.Delay(10, Ct);
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
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    /// <summary>Finds a named control in the content of the signature menu (a panel that is not yet in a window's tree is searched logically).</summary>
    private static T FindIn<T>(Panel menu, string name)
        where T : Control =>
        FindOrNull<T>(menu, name) ?? menu.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the menu.");

    private static T? FindOrNull<T>(Panel menu, string name)
        where T : Control =>
        menu.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? menu.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
