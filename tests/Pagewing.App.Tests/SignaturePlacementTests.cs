using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Signatures;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Choosing a saved signature and placing it (milestone 12, task 11): the tool with its ghost at the pointer, a click that places,
/// the keyboard way into the centre of the visible page, what ends the tool without placing, and what a placed signature is: selected,
/// movable, with colour and width only when it is drawn.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class SignaturePlacementTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly AnnotationColor Black = new(0, 0, 0);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateTempSubdirectory("pagewing-signature-place-").FullName;
    private SignatureStore? _store;

    public SignaturePlacementTests() => Strings.Culture = new CultureInfo("en");

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

    private SavedSignature AddDrawn(DrawColor color = DrawColor.Blue) =>
        Store.AddDrawn("Drawn", [[new(0, 80), new(40, 10), new(80, 90), new(120, 20)], [new(10, 95), new(110, 95)]], color);

    private SavedSignature AddImage()
    {
        var bgra = new byte[100 * 40 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 120;
            bgra[i + 1] = 30;
            bgra[i + 2] = 30;
            bgra[i + 3] = 255;
        }

        var png = SignatureImageCodec.EncodePng(new SignatureImage(100, 40, bgra));
        return Store.AddImage("Image", png, 2.5);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClickPlacesAtThePointerOnARotatedPage(bool image)
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "rotated-page.pdf");
        var signature = image ? AddImage() : AddDrawn();

        await viewModel.ChooseSignatureAsync(signature, fromKeyboard: false);
        Settle(window);
        Assert.True(tab.IsAnnotating);
        Assert.NotNull(tab.PendingSignature);
        Assert.NotNull(view.SignatureGhost);

        // The page is 100 x 200 points as shown (rotated); the click is at (50, 100) of it.
        Click(window, ScreenPoint(view, window, 0, new Point(50, 100)));
        var created = await WaitForAnnotationAsync(window, tab, 0);

        Assert.Equal(AnnotationKind.Signature, created.Kind);
        Assert.Equal(image, created.IsSignatureImage);

        // The box of the signature: the image's bounds, or the extent of the strokes (the bounds of an ink also hold half the pen).
        var box = image ? created.Bounds : created.Drawing!.Box;
        Assert.Equal(100d / 3, box.Width, 0);
        Assert.Equal(50, (box.Left + box.Right) / 2, 0);
        Assert.Equal(100, (box.Top + box.Bottom) / 2, 0);
        Assert.Equal(signature.Aspect, box.Width / box.Height, 1);
        if (!image)
        {
            Assert.Equal(new AnnotationColor(30, 80, 220), created.Color);
        }

        // The tool ended at the click: the ghost is gone and the new signature is selected with the select tool on.
        Assert.Null(tab.PendingSignature);
        Assert.True(view.SignatureGhost is null or { IsVisible: false });
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is { Kind: AnnotationKind.Signature });
        Assert.True(tab.IsSelectTool);
        Assert.Equal(created.Name, tab.SelectedAnnotation!.Name);
    }

    [AvaloniaFact]
    public async Task KeyboardChoicePlacesInTheCentreOfTheVisiblePage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var signature = AddDrawn();

        await viewModel.ChooseSignatureAsync(signature, fromKeyboard: true);
        var created = await WaitForAnnotationAsync(window, tab, 0);

        Assert.Equal(AnnotationKind.Signature, created.Kind);
        Assert.Equal(0, created.Page);
        var centre = CanvasPoint(view, 0, new Point((created.Bounds.Left + created.Bounds.Right) / 2, (created.Bounds.Top + created.Bounds.Bottom) / 2));
        Assert.True(new Rect(view.Canvas.Bounds.Size).Contains(centre), $"the signature lands at {centre}, outside what is visible");
        Assert.Null(tab.PendingSignature);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is { Kind: AnnotationKind.Signature });
    }

    [AvaloniaFact]
    public async Task EscDiscardsTheGhost()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await viewModel.ChooseSignatureAsync(AddDrawn(), fromKeyboard: false);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(150, 100)));
        Settle(window);
        Assert.NotNull(view.SignatureGhost);
        Assert.True(view.SignatureGhost!.IsVisible);

        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Null(tab.PendingSignature);
        Assert.False(view.SignatureGhost.IsVisible);
        Assert.True(tab.IsAnnotating, "Esc ends only the tool first");
        Assert.Empty(await tab.Session.Document!.GetAnnotationsAsync(0, Ct));

        // A click now places nothing.
        Click(window, ScreenPoint(view, window, 0, new Point(150, 100)));
        await Task.Delay(100, Ct);
        Assert.Empty(await tab.Session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [AvaloniaFact]
    public async Task TabSwitchDiscardsTheGhost()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await viewModel.ChooseSignatureAsync(AddDrawn(), fromKeyboard: false);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(150, 100)));
        Settle(window);
        Assert.NotNull(first.PendingSignature);

        var second = await OpenCopyAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Same(second, viewModel.ActiveTab);

        Assert.Null(first.PendingSignature);
        Assert.Null(second.PendingSignature);
        Assert.True(view.SignatureGhost is null or { IsVisible: false });
    }

    [AvaloniaFact]
    public async Task SavingDiscardsTheGhost()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await viewModel.ChooseSignatureAsync(AddDrawn(), fromKeyboard: false);
        Assert.NotNull(tab.PendingSignature);

        tab.IsSaving = true;
        Settle(window);

        Assert.Null(tab.PendingSignature);
        Assert.False(tab.CanDraw);

        // While it saves a choice does nothing either.
        await viewModel.ChooseSignatureAsync(AddDrawn(), fromKeyboard: false);
        Assert.Null(tab.PendingSignature);
    }

    [AvaloniaFact]
    public async Task PlacedSignatureIsSelectedAndMovableWithArrowKeys()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await viewModel.ChooseSignatureAsync(AddImage(), fromKeyboard: true);
        var created = await WaitForAnnotationAsync(window, tab, 0);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is { Kind: AnnotationKind.Signature });
        view.Focus();
        Settle(window);
        Assert.True(tab.CanMoveSelection);

        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        tab.FlushNudge();
        await tab.WhenEditsSettledAsync();
        var moved = Assert.Single(await tab.Session.Document!.GetAnnotationsAsync(0, Ct));

        Assert.True(moved.IsSignatureImage);
        Assert.Equal(created.Bounds.Left + 2, moved.Bounds.Left, 0);
        Assert.Equal(created.Bounds.Top + 1, moved.Bounds.Top, 0);
        Assert.Equal(created.Bounds.Width, moved.Bounds.Width, 1);

        // The aspect ratio stays when a corner is dragged (the box the user drags is not what the signature takes).
        await tab.ReplaceSelectionGeometryAsync(DrawingGeometry.Rectangle(new PageRect(moved.Bounds.Left, moved.Bounds.Top, moved.Bounds.Left + 150, moved.Bounds.Top + 100), 1));
        var resized = Assert.Single(await tab.Session.Document.GetAnnotationsAsync(0, Ct));
        Assert.Equal(2.5, resized.Bounds.Width / resized.Bounds.Height, 1);
    }

    [AvaloniaFact]
    public async Task DeletingAnImageWhenTheHistoryIsFullSaysSoAndKeepsTheSignature()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await viewModel.ChooseSignatureAsync(AddImage(), fromKeyboard: true);
        var created = await WaitForAnnotationAsync(window, tab, 0);
        var hints = new List<string>();
        tab.DrawingRefused += hints.Add;
        tab.Editor.ImageBudget = 1;

        await tab.RunEditAsync(editor => editor.DeleteAsync(new AnnotationLocator(0, created.Index, created.Name)));

        Assert.Equal([Strings.SignatureHistoryFull], hints);
        Assert.Single(await tab.Session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [AvaloniaFact]
    public async Task ASignatureJustSavedInTheDialogIsTheOneToPlace()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await tab.BeginAnnotateAsync();
        viewModel.ShowNewSignature();
        Settle(window);
        var card = viewModel.SignatureCard!;
        card.AddStroke([new(10, 80), new(40, 20), new(70, 90), new(110, 30)]);

        await card.SaveAsync();
        Settle(window);

        var saved = Assert.Single(Store.Items);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Equal(saved.Id, tab.PendingSignature?.Signature.Id);
    }

    [AvaloniaFact]
    public async Task ColourAndWidthOfADrawnOneCanBeChanged()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await viewModel.ChooseSignatureAsync(AddDrawn(DrawColor.Black), fromKeyboard: true);
        var created = await WaitForAnnotationAsync(window, tab, 0);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is { Kind: AnnotationKind.Signature });
        Assert.True(tab.CanStyleSelection);
        Assert.Equal(DrawColor.Black, tab.SelectionDrawColor);
        Assert.Equal(StrokeWidth.Medium, tab.SelectionStrokeWidth);

        tab.SetSelectionColorCommand.Execute(DrawColor.Red);
        await tab.WhenEditsSettledAsync();
        tab.SetSelectionWidthCommand.Execute(StrokeWidth.Thick);
        await tab.WhenEditsSettledAsync();

        var changed = Assert.Single(await tab.Session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, changed.Name);
        Assert.Equal(AnnotationKind.Signature, changed.Kind);
        Assert.Equal(new AnnotationColor(220, 30, 30), changed.Color);
        Assert.Equal(4, changed.Drawing!.Width, 3);
    }

    [AvaloniaFact]
    public async Task ImageSignatureOffersNeitherColourNorWidth()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await viewModel.ChooseSignatureAsync(AddImage(), fromKeyboard: true);
        await WaitForAnnotationAsync(window, tab, 0);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is { IsSignatureImage: true });

        Assert.False(tab.CanStyleSelection);
        Assert.Null(tab.SelectionDrawColor);
        Assert.Null(tab.SelectionStrokeWidth);
        Assert.True(tab.CanMoveSelection);

        // A colour command does nothing to it (it would be refused by the document): no step, the signature as it was.
        var version = tab.Editor.Version;
        tab.SetSelectionColorCommand.Execute(DrawColor.Red);
        tab.SetSelectionWidthCommand.Execute(StrokeWidth.Thick);
        await tab.WhenEditsSettledAsync();
        Assert.Equal(version, tab.Editor.Version);
    }

    [AvaloniaFact]
    public async Task NothingRunsBeforeTheFirstSharpPage()
    {
        // A window-less view model never shows a page: its tab has none, however long we wait.
        var loads = 0;
        using var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached());
        viewModel.SignatureStoreProvider = () =>
        {
            loads++;
            return Store;
        };
        var signature = AddDrawn();
        var loadsAfterSetUp = loads;
        await viewModel.OpenAsync(Copy("text-basic.pdf"));
        var tab = viewModel.ActiveTab!;
        Assert.False(tab.HasShownSharpPage);

        Assert.False(viewModel.InsertSignatureCommand.CanExecute(null));
        await viewModel.ChooseSignatureAsync(signature, fromKeyboard: true);

        Assert.Null(tab.PendingSignature);
        Assert.False(tab.IsAnnotating);
        Assert.Equal(loadsAfterSetUp, loads);
        Assert.Empty(await tab.Session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [AvaloniaFact]
    public async Task CommandIsNotRunnableWithoutAnnotationRights()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        tab.PermissionReader = (_, _) => Task.FromResult(false);
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.CanAnnotate);

        Assert.False(tab.IsAnnotating);
        Assert.False(viewModel.InsertSignatureCommand.CanExecute(null));
        await viewModel.ChooseSignatureAsync(AddDrawn(), fromKeyboard: true);
        Assert.False(tab.IsAnnotating);
        Assert.Null(tab.PendingSignature);
        Assert.Empty(await tab.Session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [AvaloniaFact]
    public async Task CommandFromTheFormModeSwitchesTheMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        tab.ToggleFillCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsFilling);
        Assert.False(tab.IsAnnotating);

        // With an empty library the command opens the dialog; the mode of annotating is on by then.
        Assert.True(viewModel.InsertSignatureCommand.CanExecute(null));
        viewModel.InsertSignatureCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.Signature);

        Assert.True(tab.IsAnnotating);
        Assert.False(tab.IsFilling);
        var entry = viewModel.Commands.ById("annotate.signature");
        Assert.NotNull(entry);
        Assert.Equal(Strings.CommandInsertSignature, entry.Title());
    }

    [AvaloniaFact]
    public async Task WithSignaturesTheCommandAsksForTheMenu()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        AddDrawn();
        var asked = 0;
        viewModel.SignatureMenuRequested += () => asked++;

        viewModel.InsertSignatureCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsAnnotating);

        Assert.Equal(1, asked);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
    }

    [AvaloniaFact]
    public async Task LibraryIsReadOnlyWhenTheMenuOpens()
    {
        var loads = 0;
        var directory = Path.Combine(_folder, "never-created", "signatures");
        var (window, viewModel, view) = Show();
        viewModel.SignatureStoreProvider = () =>
        {
            loads++;
            return SignatureStore.Load(directory);
        };
        await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Equal(0, loads);

        var items = viewModel.LoadSignatures();
        var again = viewModel.LoadSignatures();

        Assert.Empty(items);
        Assert.Empty(again);
        Assert.Equal(1, loads);
        Assert.False(Directory.Exists(Path.Combine(_folder, "never-created")), "reading the library must not create its folder");
    }

    [AvaloniaFact]
    public void TheCatalogListsTheCommandWithItsShortcut()
    {
        var (_, viewModel, _) = Show();

        var entry = viewModel.Commands.ById("annotate.signature");

        Assert.NotNull(entry);
        Assert.Equal(Strings.CommandInsertSignature, entry.Title());
        Assert.Equal(CommandCatalog.EditGroup, entry.Group);
        Assert.Equal(new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Shift), entry.Gesture);
        Assert.Single(viewModel.Commands.All, command => Equals(command.Gesture, entry.Gesture));
    }

    private static async Task<PdfAnnotation> WaitForAnnotationAsync(Window window, TabViewModel tab, int page)
    {
        PdfAnnotation? found = null;
        await WaitUntilAsync(window, () =>
        {
            var task = tab.Session.Document!.GetAnnotationsAsync(page, Ct);
            task.Wait(Ct);
            found = task.Result.FirstOrDefault(a => a.Kind == AnnotationKind.Signature);
            return found is not null;
        });
        return found!;
    }

    private static Point ScreenPoint(DocumentView view, Window window, int page, Point pagePoint) =>
        view.Canvas.TranslatePoint(CanvasPoint(view, page, pagePoint), window)!.Value;

    private static Point CanvasPoint(DocumentView view, int page, Point pagePoint)
    {
        var layout = view.Layout!;
        var rect = layout.GetPageRect(page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        return new Point(rect.X - view.Canvas.Viewport.X + (pagePoint.X * scale), rect.Y - view.Canvas.Viewport.Y + (pagePoint.Y * scale));
    }

    private static void Click(Window window, Point point)
    {
        Settle(window);
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
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

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private string Copy(string fixture)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8] + "-" + fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        viewModel.SignatureStoreProvider = () => Store;
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, window.GetVisualDescendants().OfType<DocumentView>().First(v => v.Name == "DocumentView"));
    }

    private async Task<TabViewModel> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(Copy(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
