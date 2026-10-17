using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class MainWindowTests : IDisposable
{
    private const string LongName = "Sehr langer Dateiname für die Jahresabrechnung 2025 final v3.pdf";

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly Dictionary<MainViewModel, ViewerSession> _sessions = [];
    private string? _tempDirectory;

    public void Dispose()
    {
        // Windows close before their sessions are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            var item = _cleanup[i];
            item.Dispose();
        }

        if (_tempDirectory is not null)
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public void OpensWithTheStartScreenAndNoTab()
    {
        var (window, viewModel) = Show();

        Assert.Equal(ViewerState.Empty, viewModel.State);
        Assert.True(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
        Assert.True(Find<Button>(window, "ChooseFileButton").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "Tab").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "StatusBar").IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "AppTitle").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MinimumSizeIs640By480(bool windowsTitleBar)
    {
        // Both variants are forced, so the test means the same on Windows and Linux runners.
        var (window, viewModel) = Show(640, 480, windowsTitleBar);
        await OpenAsync(window, viewModel, CopyWithName(LongName));

        Assert.Equal(640, window.MinWidth);
        Assert.Equal(480, window.MinHeight);

        // At the minimum size the title bar (or tab row) and the status bar still fit into the window. Only the
        // Windows title bar keeps room for the caption buttons; under the Linux system title bar there is none.
        Assert.Equal(windowsTitleBar, Find<Control>(window, "CaptionButtonsSpace").IsEffectivelyVisible);
        string[] mustFit = windowsTitleBar
            ? ["Tab", "OpenButton", "DragArea", "CaptionButtonsSpace", "ZoomInButton"]
            : ["Tab", "OpenButton", "DragArea", "ZoomInButton"];
        foreach (var name in mustFit)
        {
            var control = Find<Control>(window, name);
            Assert.True(control.IsEffectivelyVisible, $"{name} is hidden");
            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;
            Assert.True(right <= 640.5, $"{name} ends at {right}, beyond the window width");
        }
    }

    [AvaloniaFact]
    public async Task TabShowsTheFileNameWithTheFullPathAsTooltip()
    {
        var (window, viewModel) = Show();
        var path = CopyWithName(LongName);
        await OpenAsync(window, viewModel, path);

        var tab = Find<Control>(window, "Tab");
        var name = Find<MiddleEllipsisTextBlock>(window, "TabName");

        Assert.Equal(ViewerState.Ready, viewModel.State);
        Assert.True(tab.IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
        Assert.Equal(LongName, viewModel.FileName);
        Assert.Equal(Path.GetFullPath(path), viewModel.FullPath);
        Assert.Equal(LongName, name.Text);
        Assert.Equal(viewModel.FullPath, ToolTip.GetTip(tab));

        // The long name is cut in the middle, and the tab keeps to 280 px.
        Assert.StartsWith("Sehr", name.DisplayText, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", name.DisplayText, StringComparison.Ordinal);
        Assert.Contains('…', name.DisplayText);
        Assert.True(tab.Bounds.Width <= 280, $"tab width {tab.Bounds.Width}");
        Assert.True(tab.Bounds.Width >= 120, $"tab width {tab.Bounds.Width}");
        Assert.Equal(new CornerRadius(8, 8, 0, 0), Assert.IsType<Border>(tab).CornerRadius);

        // Closing the tab returns to the start screen.
        Find<Button>(window, "CloseTabButton").Command!.Execute(null);
        Settle(window);
        Assert.Equal(ViewerState.Empty, viewModel.State);
        Assert.False(tab.IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task StatusBarWordsDisappearBelow760Pixels()
    {
        var (window, viewModel) = Show(700, 600);
        await OpenAsync(window, viewModel, TestData.Pdf("text-basic.pdf"));

        Assert.True(Find<Control>(window, "StatusBar").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "PageLabel").IsEffectivelyVisible);
        foreach (var name in new[] { "PreviousPageButton", "PageField", "PageCountText", "NextPageButton", "ZoomOutButton", "ZoomValueText", "ZoomInButton" })
        {
            Assert.True(Find<Control>(window, name).IsEffectivelyVisible, $"{name} is hidden at 700 px");
        }

        // The icons keep their names as tooltips.
        Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(Find<Control>(window, "PreviousPageButton")) as string));

        window.Width = 760;
        Settle(window);
        Assert.True(Find<Control>(window, "PageLabel").IsEffectivelyVisible);
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.True(viewModel.PageCount > 0);
        Assert.Equal("1", Find<TextBox>(window, "PageField").Text);
        Assert.Equal(Strings.PageOf.Replace("{0}", viewModel.PageCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), Find<TextBlock>(window, "PageCountText").Text);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DragAreaOfTheTitleBarStaysAtLeast48PixelsWide(bool windowsTitleBar)
    {
        var (window, viewModel) = Show(640, 480, windowsTitleBar);
        var dragArea = Find<Control>(window, "DragArea");

        Assert.True(dragArea.Bounds.Width >= 48, $"empty window: drag area {dragArea.Bounds.Width} px");

        await OpenAsync(window, viewModel, CopyWithName(LongName + LongName));

        Assert.True(Find<Control>(window, "Tab").IsEffectivelyVisible);
        Assert.True(dragArea.Bounds.Width >= 48, $"with a tab: drag area {dragArea.Bounds.Width} px");
        Assert.Equal(windowsTitleBar, Find<Control>(window, "CaptionButtonsSpace").IsEffectivelyVisible);
        Assert.Equal(windowsTitleBar, window.ExtendClientAreaToDecorationsHint);
        Assert.Equal(40, Find<Control>(window, "TitleBar").Bounds.Height);

        // Our theme for the caption buttons Avalonia draws on Windows (no overlay title, no fullscreen button).
        var decorations = Assert.IsType<ControlTheme>(window.WindowDecorationsTheme);
        Assert.Equal(typeof(WindowDrawnDecorations), decorations.TargetType);
        Assert.NotNull(decorations.BasedOn);

        // The system moves the window from the drag area; the tab and its buttons take their own input.
        Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(dragArea));
        foreach (var name in new[] { "Tab", "CloseTabButton", "OpenButton" })
        {
            Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(Find<Control>(window, name)));
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryIconOnlyButtonHasATooltipAndAnAutomationName(bool windowsTitleBar)
    {
        var (window, viewModel) = Show(1024, 720, windowsTitleBar);
        AssertIconButtonsAreNamed(window, minimum: 0);

        await OpenAsync(window, viewModel, TestData.Pdf("text-basic.pdf"));

        // Open, close tab, previous and next page, zoom out and in.
        AssertIconButtonsAreNamed(window, minimum: 6);
    }

    [AvaloniaFact]
    public void GermanAndEnglishUiShowDifferentStartTitles()
    {
        Strings.Culture = new CultureInfo("de");
        var (german, _) = Show();
        var germanTitle = Find<TextBlock>(german, "StartTitle").Text;
        var germanButton = Find<Button>(german, "ChooseFileButton");

        Strings.Culture = new CultureInfo("en");
        var (english, _) = Show();
        var englishTitle = Find<TextBlock>(english, "StartTitle").Text;

        Assert.Equal("Dokument öffnen", germanTitle);
        Assert.Equal("Open a document", englishTitle);
        Assert.Contains(germanButton.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Datei auswählen");
        Assert.Contains(germanButton.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Strg+O");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void SecondaryTextOnTheCanvasMeetsAA(string theme)
    {
        // The start screen shows text.secondary on surface.canvas, a pair the design system lists as unchecked.
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var ratio = Wcag.Contrast(TokenTests.Token("Text.Secondary", variant), TokenTests.Token("Surface.Canvas", variant));

        Assert.True(ratio >= 4.5, $"{theme}: text.secondary on surface.canvas is {ratio:F2}:1");
    }

    private static void AssertIconButtonsAreNamed(Window window, int minimum)
    {
        var iconButtons = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .Where(button => !button.GetVisualDescendants().OfType<TextBlock>().Any(block => !string.IsNullOrWhiteSpace(block.Text)))
            .ToList();

        Assert.True(iconButtons.Count >= minimum, $"{iconButtons.Count} icon buttons, expected at least {minimum}");
        foreach (var button in iconButtons)
        {
            var tip = ToolTip.GetTip(button) as string;
            var name = AutomationProperties.GetName(button);
            Assert.False(string.IsNullOrWhiteSpace(tip), $"{button.Name} has no tooltip");
            Assert.False(string.IsNullOrWhiteSpace(name), $"{button.Name} has no automation name");
        }
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(double width = 1024, double height = 720, bool? windowsTitleBar = null)
    {
        var session = new ViewerSession(new PdfEngine());
        _cleanup.Add(session);
        var viewModel = new MainViewModel(session);
        _sessions.Add(viewModel, session);
        if (windowsTitleBar is { } value)
        {
            viewModel.IsWindowsTitleBar = value;
        }

        var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
    }

    private async Task OpenAsync(Window window, MainViewModel viewModel, string path)
    {
        await _sessions[viewModel].OpenAsync(path);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        // Session events are posted to the UI thread; the narrow-window rule reacts to the size of the layout.
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private string CopyWithName(string fileName)
    {
        _tempDirectory ??= Directory.CreateTempSubdirectory("pagewing-app-tests-").FullName;
        var path = Path.Combine(_tempDirectory, fileName);
        File.Copy(TestData.Pdf("hello_world.pdf"), path, overwrite: true);
        return path;
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
