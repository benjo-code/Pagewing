using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>The dialog "New signature" (milestone 12, task 10): drawing, importing with cut-out, the hint, the limits.</summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed partial class SignatureCardTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateTempSubdirectory("pagewing-signature-card-").FullName;

    public SignatureCardTests()
    {
        Strings.Culture = new CultureInfo("en");
        CultureInfo.CurrentUICulture = new CultureInfo("en");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SignatureStore NewStore() => SignatureStore.Load(Path.Combine(_folder, "signatures"));

    private static IReadOnlyList<DisplayPoint> Line(double x0, double y0, double x1, double y1, int points = 40) =>
        [.. Enumerable.Range(0, points).Select(i => new DisplayPoint(x0 + ((x1 - x0) * i / (points - 1)), y0 + ((y1 - y0) * i / (points - 1)) + ((i % 2) * 0.1)))];

    /// <summary>A scan: 120 x 50 px paper with a block of dark blue ink.</summary>
    private static SignatureImage Scan(byte paper = 240)
    {
        var bgra = new byte[120 * 50 * 4];
        for (var y = 0; y < 50; y++)
        {
            for (var x = 0; x < 120; x++)
            {
                var o = ((y * 120) + x) * 4;
                var ink = x is >= 20 and < 90 && y is >= 15 and < 35;
                bgra[o] = ink ? (byte)90 : paper;
                bgra[o + 1] = ink ? (byte)20 : paper;
                bgra[o + 2] = ink ? (byte)10 : paper;
                bgra[o + 3] = 255;
            }
        }

        return new SignatureImage(120, 50, bgra);
    }

    private string WritePng(SignatureImage image, string name = "scan.png")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, SignatureImageCodec.EncodePng(image));
        return path;
    }

    private SignatureCardViewModel NewCard(SignatureStore? store = null, string? pickedPath = null)
    {
        var card = new SignatureCardViewModel(store ?? NewStore(), pickedPath is null ? null : () => Task.FromResult<string?>(pickedPath));
        return card;
    }

    [AvaloniaTheory]
    [InlineData("en", "Does not replace a handwritten signature where written form is required.", "This is an image of your signature, not a digital signature.")]
    [InlineData("de", "Ersetzt keine handschriftliche Unterschrift, wo Schriftform verlangt ist.", "Das ist ein Bild Ihrer Unterschrift, keine digitale Signatur.")]
    public void HintIsShownInBothTabsWithoutScrolling(string language, string legal, string digital)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel) = Show();
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);
        var card = Find<SignatureCard>(window, "SignatureCard");
        var box = Find<Border>(card, "SignatureBox");

        foreach (var tab in new[] { SignatureCardTab.Draw, SignatureCardTab.Image })
        {
            viewModel.SignatureCard!.Tab = tab;
            Settle(window);
            foreach (var (name, text) in new[] { ("SignatureHintLegal", legal), ("SignatureHintDigital", digital) })
            {
                var hint = Find<TextBlock>(card, name);
                Assert.Equal(text, hint.Text);
                Assert.True(hint.IsEffectivelyVisible, $"{name} is hidden in the tab {tab}");
                Assert.DoesNotContain(hint.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
                var bounds = hint.TranslatePoint(default, box)!.Value;
                Assert.True(bounds.Y >= 0 && bounds.Y + hint.Bounds.Height <= box.Bounds.Height, $"{name} lies outside the card in the tab {tab}");
            }
        }

        // Not to be clicked away for good: nothing on the card closes the hint, and nothing remembers it was hidden.
        Assert.DoesNotContain(card.GetVisualDescendants().OfType<Button>(), button => button.Name?.Contains("Hint", StringComparison.Ordinal) == true);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public void NoUiTextCallsAnythingLegallyBinding(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var texts = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string) && property.Name.StartsWith("Signature", StringComparison.Ordinal))
            .Select(property => (string)property.GetValue(null)!)
            .ToList();
        var card = NewCard();
        foreach (var problem in Enum.GetValues<SignatureProblem>())
        {
            texts.Add(SignatureCardViewModel.TextOf(problem, SignatureCardTab.Draw));
            texts.Add(SignatureCardViewModel.TextOf(problem, SignatureCardTab.Image));
        }

        texts.AddRange(Enum.GetValues<DrawColor>().Select(Strings.DrawColorName));
        Assert.True(texts.Count >= 25, $"only {texts.Count} texts");
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        foreach (var text in texts)
        {
            Assert.DoesNotContain("rechtsgültig", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legally", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(ValidWord(), text);
        }

        Assert.NotNull(card);
    }

    [GeneratedRegex(@"(?<![\p{L}])(valid|gültig)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValidWord();

    [AvaloniaFact]
    public void SingleDotCannotBeSaved()
    {
        var store = NewStore();
        var card = NewCard(store);
        Assert.False(card.SaveCommand.CanExecute(null));
        Assert.Equal(SignatureProblem.Empty, card.Problem);

        card.AddStroke([new(50, 50)]);
        card.AddStroke([new(60, 60), new(60.5, 60.2)]);

        Assert.False(card.HasContent);
        Assert.Equal(SignatureProblem.Empty, card.Problem);
        Assert.False(card.SaveCommand.CanExecute(null));
        Assert.DoesNotContain(card.Strokes, stroke => stroke.Count < 2);
        Assert.Empty(store.Items);

        // A real stroke makes it saveable.
        card.AddStroke(Line(20, 80, 200, 40));
        Assert.True(card.HasContent);
        Assert.Null(card.Problem);
        Assert.True(card.SaveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task BlankImageCannotBeSavedAndSaysWhy()
    {
        var store = NewStore();
        var white = new SignatureImage(60, 30, [.. Enumerable.Repeat((byte)255, 60 * 30 * 4)]);
        var card = NewCard(store, WritePng(white));

        card.Tab = SignatureCardTab.Image;
        Assert.Equal(SignatureProblem.NoImage, card.Problem);
        await card.ChooseFileAsync();

        Assert.Equal(SignatureProblem.Empty, card.Problem);
        Assert.Equal(Strings.SignatureProblemBlank, card.ProblemText);
        Assert.False(card.SaveCommand.CanExecute(null));
        Assert.Empty(store.Items);
        await card.SaveAsync();
        Assert.Empty(store.Items);
    }

    [AvaloniaFact]
    public void EleventhSignatureShowsTheLimit()
    {
        var store = NewStore();
        for (var i = 0; i < SignatureLimits.MaxSignatures; i++)
        {
            store.AddDrawn("S" + i, [Line(0, 0, 100, 50)], DrawColor.Black);
        }

        var card = NewCard(store);
        card.AddStroke(Line(20, 80, 200, 40));

        Assert.Equal(SignatureProblem.LibraryFull, card.Problem);
        Assert.Equal(Strings.SignatureProblemLibraryFull, card.ProblemText);
        Assert.False(card.SaveCommand.CanExecute(null));
        Assert.Equal(10, store.Items.Count);
    }

    [AvaloniaFact]
    public void PadDrawsStrokesWithTheMouseAndAClickIsNoStroke()
    {
        var (window, viewModel) = Show();
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);
        var pad = Find<Avalonia.Controls.Control>(Find<SignatureCard>(window, "SignatureCard"), "SignaturePad");
        var origin = pad.TranslatePoint(default, window)!.Value;
        Point At(double x, double y) => new(origin.X + x, origin.Y + y);

        // A click draws a dot, which is no stroke.
        window.MouseDown(At(100, 60), MouseButton.Left);
        window.MouseUp(At(100, 60), MouseButton.Left);
        Settle(window);
        Assert.Empty(viewModel.SignatureCard!.Strokes);

        // A drag is one stroke, whose points follow the mouse.
        window.MouseDown(At(40, 100), MouseButton.Left);
        for (var x = 45; x <= 240; x += 5)
        {
            window.MouseMove(At(x, 100 - ((x - 40) / 8.0)));
        }

        window.MouseUp(At(240, 75), MouseButton.Left);
        Settle(window);

        var stroke = Assert.Single(viewModel.SignatureCard.Strokes);
        Assert.True(stroke.Count >= 2);
        Assert.Equal(40, stroke[0].X, 1);
        Assert.Equal(100, stroke[0].Y, 1);
        Assert.Equal(240, stroke[^1].X, 1);
        Assert.True(viewModel.SignatureCard.HasContent);
    }

    [AvaloniaFact]
    public void DrawnStrokesAreSmoothed()
    {
        var card = NewCard();
        var raw = Line(20, 80, 220, 60, points: 120);

        card.AddStroke(raw);

        var stroke = Assert.Single(card.Strokes);
        Assert.True(stroke.Count >= 2 && stroke.Count < raw.Count / 2, $"{stroke.Count} of {raw.Count} points are left");
        Assert.Equal(raw[0], stroke[0]);
        Assert.Equal(raw[^1], stroke[^1]);

        // Undo takes the last stroke, clear all of them.
        card.AddStroke(Line(20, 100, 220, 100));
        Assert.Equal(2, card.Strokes.Count);
        card.UndoStrokeCommand.Execute(null);
        Assert.Single(card.Strokes);
        card.ClearCommand.Execute(null);
        Assert.Empty(card.Strokes);
        Assert.False(card.HasContent);
    }

    [AvaloniaFact]
    public async Task ThresholdSliderChangesThePreview()
    {
        var card = NewCard(pickedPath: WritePng(Scan()));
        card.Tab = SignatureCardTab.Image;
        await card.ChooseFileAsync();

        Assert.NotNull(card.Preview);
        Assert.Null(card.Problem);
        var automatic = card.Threshold;
        Assert.InRange(automatic, 20, 230);
        int OpaquePixels(SignatureImage image) => Enumerable.Range(0, image.Width * image.Height).Count(i => image.Bgra[(i * 4) + 3] > 200);
        var atAuto = OpaquePixels(card.Preview!);
        Assert.InRange(atAuto, 70 * 20 / 2, 70 * 20 + 400);

        // A very low threshold keeps nothing of the ink, a very high one keeps the paper as well.
        card.Threshold = 5;
        Assert.True(card.Preview is null || OpaquePixels(card.Preview) < atAuto / 4);
        card.Threshold = 255;
        Assert.True(Enumerable.Range(0, card.Preview!.Width * card.Preview.Height).Count(i => card.Preview.Bgra[(i * 4) + 3] > 8) > atAuto * 2);
        card.Threshold = automatic;
        Assert.Equal(atAuto, OpaquePixels(card.Preview!));
    }

    [AvaloniaFact]
    public async Task ProblemsOfAFileAreNamedAndLeaveNothingBehind()
    {
        var store = NewStore();
        var card = NewCard(store, pickedPath: Path.Combine(_folder, "huge.png"));
        card.Tab = SignatureCardTab.Image;

        // 60,000 x 60,000 pixels from a few bytes, a file of 11 MiB, and a text file called png.
        byte[] Head(uint w, uint h) =>
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0,
        ];
        await File.WriteAllBytesAsync(Path.Combine(_folder, "huge.png"), Head(60_000, 60_000), Ct);
        await card.ChooseFileAsync();
        Assert.Equal(SignatureProblem.TooManyPixels, card.Problem);
        Assert.Null(card.Preview);

        await File.WriteAllBytesAsync(Path.Combine(_folder, "huge.png"), new byte[SignatureLimits.MaxFileBytes + 1], Ct);
        await card.ChooseFileAsync();
        Assert.Equal(SignatureProblem.TooLarge, card.Problem);

        await File.WriteAllTextAsync(Path.Combine(_folder, "huge.png"), "this is not an image", Ct);
        await card.ChooseFileAsync();
        Assert.Equal(SignatureProblem.Unreadable, card.Problem);
        Assert.False(card.SaveCommand.CanExecute(null));

        File.Delete(Path.Combine(_folder, "huge.png"));
        await card.ChooseFileAsync();
        Assert.Equal(SignatureProblem.Unreadable, card.Problem);
        Assert.Empty(store.Items);

        // A good file afterwards clears the problem.
        var good = NewCard(store, WritePng(Scan(), "good.png"));
        good.Tab = SignatureCardTab.Image;
        await good.ChooseFileAsync();
        Assert.Null(good.Problem);
        Assert.True(good.SaveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SavingADrawnSignatureNamesItAndClosesTheCard()
    {
        var (window, viewModel) = Show();
        var store = NewStore();
        viewModel.SignatureStoreProvider = () => store;
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);
        var card = viewModel.SignatureCard!;
        SavedSignature? saved = null;
        card.Saved += s => saved = s;

        card.Name = "  Work  ";
        card.AddStroke(Line(20, 80, 200, 40));
        card.SelectedColor = card.Colors.Single(choice => choice.Value == DrawColor.Blue);
        await card.SaveAsync();
        Settle(window);

        Assert.NotNull(saved);
        var item = Assert.Single(store.Items);
        Assert.Equal("Work", item.Name);
        Assert.Equal(SavedSignatureKind.Drawn, item.Kind);
        Assert.Equal(DrawColor.Blue, item.Color);
        Assert.Equal(item.Id, saved.Id);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);

        // A blank name becomes "Signature 2" (the next number).
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);
        viewModel.SignatureCard!.AddStroke(Line(20, 80, 200, 40));
        await viewModel.SignatureCard.SaveAsync();
        Assert.Equal(["Work", "Signature 2"], store.Items.Select(s => s.Name));
    }

    [AvaloniaFact]
    public async Task SavingAnImageStoresTheCutOutAsAPng()
    {
        var store = NewStore();
        var card = NewCard(store, WritePng(Scan()));
        card.Tab = SignatureCardTab.Image;
        await card.ChooseFileAsync();
        card.Name = "Scan";

        await card.SaveAsync();

        var item = Assert.Single(store.Items);
        Assert.Equal(SavedSignatureKind.Image, item.Kind);
        var png = store.ReadPng(item.Id)!;
        var back = await SignatureImageCodec.DecodeAsync(png, Ct);

        // Trimmed to the ink (70 x 20 plus a margin), paper transparent, ink blue-ish and opaque.
        Assert.InRange(back.Width, 70, 80);
        Assert.InRange(back.Height, 20, 30);
        Assert.Equal(back.Width / (double)back.Height, item.Aspect, 2);
        Assert.True(back.Bgra[3] == 0, "the corner is not transparent");
        var centre = ((back.Height / 2 * back.Width) + (back.Width / 2)) * 4;
        Assert.True(back.Bgra[centre + 3] == 255 && back.Bgra[centre] > back.Bgra[centre + 2], "the ink is not blue");
    }

    [AvaloniaFact]
    public async Task ASaveThatFailsOnDiskNamesTheProblemAndKeepsTheCard()
    {
        var blocked = Path.Combine(_folder, "blocked");
        await File.WriteAllTextAsync(blocked, "x", Ct);
        var card = NewCard(SignatureStore.Load(blocked));
        card.AddStroke(Line(20, 80, 200, 40));
        var closed = false;
        card.Saved += _ => closed = true;

        await card.SaveAsync();

        Assert.False(closed);
        Assert.Equal(SignatureProblem.SaveFailed, card.Problem);
        Assert.Equal(Strings.SignatureProblemSaveFailed, card.ProblemText);
        Assert.True(card.HasContent);
    }

    [AvaloniaFact]
    public void CardIsCreatedOnlyOnFirstShow()
    {
        var (window, viewModel) = Show();

        Assert.Null(viewModel.SignatureCard);
        Assert.Null(window.GetVisualDescendants().OfType<SignatureCard>().FirstOrDefault());

        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);

        Assert.NotNull(viewModel.SignatureCard);
        var card = Find<SignatureCard>(window, "SignatureCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Same(viewModel.SignatureCard, card.DataContext);
        Assert.Equal(Strings.SignatureCardTitle, AutomationProperties.GetName(card));
    }

    [AvaloniaFact]
    public void EscAndCancelCloseTheCardWithoutSaving()
    {
        var (window, viewModel) = Show();
        var store = NewStore();
        viewModel.SignatureStoreProvider = () => store;
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);
        viewModel.SignatureCard!.AddStroke(Line(20, 80, 200, 40));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(window);

        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Empty(store.Items);
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);
        Assert.Empty(viewModel.SignatureCard!.Strokes);
        viewModel.SignatureCard.CancelCommand.Execute(null);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
    }

    [AvaloniaFact]
    public async Task KeyboardReachesEveryControl()
    {
        var (window, viewModel) = Show();
        viewModel.SignatureStoreProvider = NewStore;
        viewModel.SignatureImagePicker = () => Task.FromResult<string?>(null);
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);
        var card = Find<SignatureCard>(window, "SignatureCard");
        viewModel.SignatureCard!.AddStroke(Line(20, 80, 200, 40));

        void AssertReachable(params string[] names)
        {
            foreach (var name in names)
            {
                var control = Assert.IsAssignableFrom<InputElement>(card.GetVisualDescendants().OfType<Control>().First(c => c.Name == name));
                Assert.True(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Focusable && KeyboardNavigation.GetIsTabStop(control), $"{name} cannot be reached with the keyboard");
                control.Focus();
                Assert.True(control.IsFocused, $"{name} does not take the focus");
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)) && control is not TextBox, $"{name} has no accessible name");
            }
        }

        AssertReachable("SignatureCloseButton", "SignatureDrawTab", "SignatureImageTab", "SignatureName", "SignatureClearButton", "SignatureUndoButton", "SignatureInkChoice", "SignatureCancelButton", "SignatureSaveButton");
        viewModel.SignatureCard.Tab = SignatureCardTab.Image;
        Settle(window);
        AssertReachable("SignatureChooseButton", "SignatureName", "SignatureCancelButton");

        // The slider shows once an image is there.
        var scan = WritePng(Scan());
        viewModel.SignatureImagePicker = () => Task.FromResult<string?>(scan);
        await viewModel.SignatureCard.ChooseFileAsync();
        Settle(window);
        AssertReachable("SignatureThreshold", "SignatureSaveButton");
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(Find<Control>(card, "SignaturePad"))));
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ColoursMeetThreeToOne(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel) = Show(variant);
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);

        double Ratio(string a, string b) => Wcag.Contrast(TokenTests.Token(a, variant), TokenTests.Token(b, variant));
        Assert.True(Ratio("Text.Primary", "Surface.Raised") >= 4.5);
        Assert.True(Ratio("Text.Secondary", "Surface.Raised") >= 4.5, "the hint is hard to read");
        Assert.True(Ratio("Border.Strong", "Surface.Raised") >= 3, "the border of the drawing area is hard to see");
        Assert.True(Ratio("Danger.Default", "Surface.Raised") >= 4.5, "the problem text is hard to read");
        var card = Find<SignatureCard>(window, "SignatureCard");
        Assert.Equal(TokenTests.Token("Text.Secondary", variant), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(Find<TextBlock>(card, "SignatureHintLegal").Foreground).Color);
    }

    [AvaloniaFact]
    public async Task SavingWritesNothingToTheRealUserFolder()
    {
        var real = UserDataPaths.SignaturesDirectory();
        string[] Snapshot() => Directory.Exists(real) ? [.. Directory.GetFileSystemEntries(real).Order()] : ["<none>"];
        var before = Snapshot();
        var (window, viewModel) = Show();
        var store = NewStore();
        viewModel.SignatureStoreProvider = () => store;
        viewModel.ShowOverlay(OverlayKind.Signature);
        Settle(window);
        viewModel.SignatureCard!.AddStroke(Line(20, 80, 200, 40));

        await viewModel.SignatureCard.SaveAsync();

        Assert.Single(store.Items);
        Assert.Equal(before, Snapshot());
        Assert.StartsWith(_folder, Path.GetFullPath(Path.Combine(_folder, "signatures")), StringComparison.Ordinal);
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(ThemeVariant? variant = null)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        viewModel.SignatureStoreProvider = NewStore;
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720 };
        if (variant is not null)
        {
            window.RequestedThemeVariant = variant;
        }

        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
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
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
