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
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The drawing tools of the mode "Comment" (milestone 10, task 10): the state of the tools (which one is on, the select
/// tool, the style of each tool), their commands in the catalog and their texts. Nothing is drawn here; the drawing with the
/// mouse comes later. Every file is a copy in a temp folder; settings are in memory.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DrawToolsTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly AnnotationKind[] DrawingKinds =
        [AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow];

    private static readonly string[] ToolIds =
        ["annotate.select", "annotate.ink", "annotate.rectangle", "annotate.ellipse", "annotate.line", "annotate.arrow"];

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-drawtools-" + Guid.NewGuid().ToString("N"))).FullName;

    public DrawToolsTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
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
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    [AvaloniaFact]
    public async Task ToolsAreOnlyAvailableInTheMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");

        // Outside the mode no tool command runs, and nothing is chosen.
        foreach (var kind in DrawingKinds)
        {
            Assert.False(tab.SetToolCommand.CanExecute(kind), kind.ToString());
            tab.SetToolCommand.Execute(kind);
            Assert.Null(tab.ActiveTool);
        }

        Assert.False(tab.SelectToolCommand.CanExecute(null));
        tab.SelectToolCommand.Execute(null);
        Assert.False(tab.IsSelectTool);
        Assert.False(tab.SetStyleCommand.CanExecute(new ToolStyle(DrawColor.Blue, StrokeWidth.Thick)));

        // Only the kinds that have a tool (the text box too, since its task), and "other" never.
        await BeginAsync(window, tab);
        Assert.True(tab.SetToolCommand.CanExecute(AnnotationKind.TextBox));
        Assert.False(tab.SetToolCommand.CanExecute(AnnotationKind.Other));
        Assert.All(DrawingKinds, kind => Assert.True(tab.SetToolCommand.CanExecute(kind), kind.ToString()));
        Assert.True(tab.SelectToolCommand.CanExecute(null));

        // In the mode each one is chosen; choosing the one that is on turns it off.
        foreach (var kind in DrawingKinds)
        {
            tab.SetToolCommand.Execute(kind);
            Assert.Equal(kind, tab.ActiveTool);
            tab.SetToolCommand.Execute(kind);
            Assert.Null(tab.ActiveTool);
        }

        // The command of the catalog starts the mode with its tool, from outside the mode.
        tab.EndAnnotateCommand.Execute(null);
        Assert.False(tab.IsAnnotating);
        Assert.All(ToolIds, id => Assert.True(viewModel.Commands.ById(id)!.CanExecute(), id));
        viewModel.Commands.ById("annotate.rectangle")!.Execute();
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        Assert.Equal(AnnotationKind.Rectangle, tab.ActiveTool);
        Assert.False(tab.IsSelectTool);

        // Ending the mode ends the tool; the next start has none.
        tab.EndAnnotateCommand.Execute(null);
        Assert.Null(tab.ActiveTool);
        viewModel.Commands.ById("annotate.mode")!.Execute();
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        Assert.Null(tab.ActiveTool);
    }

    [AvaloniaFact]
    public async Task ToolStaysActiveAfterAStroke()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        tab.SetToolCommand.Execute(AnnotationKind.Rectangle);

        // A drawing is made with the style of the tool; the tool is still on afterwards, and so is the style.
        var style = tab.ActiveStyle;
        var geometry = DrawingGeometry.Rectangle(new PageRect(72, 72, 200, 140), StrokeWidths.PointsOf(style.Width));
        await tab.RunEditAsync(editor => editor.ApplyDrawingAsync(0, geometry, DrawPalette.ColorOf(style.Color), null, null));
        await WaitUntilAsync(window, () => tab.IsDirty && !tab.HasEditsInFlight);

        Assert.True(tab.IsAnnotating);
        Assert.Equal(AnnotationKind.Rectangle, tab.ActiveTool);
        Assert.False(tab.IsSelectTool);
        Assert.Equal(style, tab.ActiveStyle);
    }

    [AvaloniaFact]
    public async Task SelectToolExcludesDrawingTools()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.SelectToolCommand.Execute(null);
        Assert.True(tab.IsSelectTool);
        Assert.Null(tab.ActiveTool);
        Assert.Contains(nameof(TabViewModel.IsSelectTool), changed);

        // A drawing tool takes the place of the select tool, and the select tool the place of the drawing tool.
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        Assert.False(tab.IsSelectTool);
        Assert.Equal(AnnotationKind.Ink, tab.ActiveTool);
        tab.SelectToolCommand.Execute(null);
        Assert.True(tab.IsSelectTool);
        Assert.Null(tab.ActiveTool);

        // So does a text markup tool; the select tool again turns itself off.
        tab.SetToolCommand.Execute(AnnotationKind.Highlight);
        Assert.False(tab.IsSelectTool);
        Assert.Equal(AnnotationKind.Highlight, tab.ActiveTool);
        tab.SelectToolCommand.Execute(null);
        Assert.True(tab.IsSelectTool);
        tab.SelectToolCommand.Execute(null);
        Assert.False(tab.IsSelectTool);
        Assert.Null(tab.ActiveTool);

        // The commands of the catalog work the same way.
        viewModel.Commands.ById("annotate.arrow")!.Execute();
        Assert.Equal(AnnotationKind.Arrow, tab.ActiveTool);
        viewModel.Commands.ById("annotate.select")!.Execute();
        Assert.True(tab.IsSelectTool);
        Assert.Null(tab.ActiveTool);
        viewModel.Commands.ById("annotate.line")!.Execute();
        Assert.False(tab.IsSelectTool);
        Assert.Equal(AnnotationKind.Line, tab.ActiveTool);
    }

    [AvaloniaFact]
    public async Task SelectToolIsClearedWhenTheModeEnds()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");

        // The select tool of the catalog starts the mode, too.
        viewModel.Commands.ById("annotate.select")!.Execute();
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        Assert.True(tab.IsSelectTool);
        Assert.Null(tab.ActiveTool);

        tab.EndAnnotateCommand.Execute(null);
        Assert.False(tab.IsAnnotating);
        Assert.False(tab.IsSelectTool);
        Assert.False(tab.SelectToolCommand.CanExecute(null));

        // The next start has no tool at all.
        await BeginAsync(window, tab);
        Assert.False(tab.IsSelectTool);
        Assert.Null(tab.ActiveTool);

        // Done in the toolbar ends it too, and Esc.
        tab.SelectToolCommand.Execute(null);
        Assert.True(tab.IsSelectTool);
        Assert.True(tab.EscapeAnnotate());
        Assert.False(tab.IsAnnotating);
        Assert.False(tab.IsSelectTool);
    }

    [AvaloniaFact]
    public async Task StyleIsRememberedPerTool()
    {
        var settings = UserSettings.Detached();
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);

        // Every tool starts with red, medium.
        foreach (var kind in DrawingKinds)
        {
            tab.SetToolCommand.Execute(kind);
            Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Medium), tab.ActiveStyle);
        }

        var ink = new ToolStyle(DrawColor.Blue, StrokeWidth.Thick);
        var rectangle = new ToolStyle(DrawColor.Black, StrokeWidth.Thin);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        tab.SetStyleCommand.Execute(ink);
        Assert.Equal(ink, tab.ActiveStyle);
        Assert.Contains(nameof(TabViewModel.ActiveStyle), changed);
        tab.SetToolCommand.Execute(AnnotationKind.Rectangle);
        Assert.Equal(AppSettings.DefaultToolStyle, tab.ActiveStyle);
        tab.SetStyleCommand.Execute(rectangle);
        Assert.Equal(rectangle, tab.ActiveStyle);

        // Back to the first tool: its own style, in the tab and in the settings.
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        Assert.Equal(ink, tab.ActiveStyle);
        Assert.Equal(ink, settings.Current.StyleOf(AnnotationKind.Ink));
        Assert.Equal(rectangle, settings.Current.StyleOf(AnnotationKind.Rectangle));
        Assert.Equal(AppSettings.DefaultToolStyle, settings.Current.StyleOf(AnnotationKind.Arrow));
        Assert.Equal(ink, tab.StyleOf(AnnotationKind.Ink));

        // Without a tool, the style shown is that of the last drawing tool; but nothing can be changed then.
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        Assert.Null(tab.ActiveTool);
        Assert.Equal(ink, tab.ActiveStyle);
        Assert.False(tab.SetStyleCommand.CanExecute(rectangle));

        // A text markup tool is no drawing tool either.
        tab.SetToolCommand.Execute(AnnotationKind.Highlight);
        Assert.False(tab.SetStyleCommand.CanExecute(rectangle));
        Assert.Equal(ink, tab.ActiveStyle);

        // The settings change (another window): the style shown follows.
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        settings.Update(current => current.WithToolStyle(AnnotationKind.Ink, new ToolStyle(DrawColor.Green, StrokeWidth.Thin)));
        Assert.Equal(new ToolStyle(DrawColor.Green, StrokeWidth.Thin), tab.ActiveStyle);
    }

    [AvaloniaFact]
    public async Task StyleChangeKeepsTheOtherTools()
    {
        var settings = UserSettings.Detached();
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);

        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        tab.SetStyleCommand.Execute(new ToolStyle(DrawColor.Blue, StrokeWidth.Thick));
        var afterInk = settings.Current.ToolStyles;
        tab.SetToolCommand.Execute(AnnotationKind.Arrow);
        tab.SetStyleCommand.Execute(new ToolStyle(DrawColor.Orange, StrokeWidth.Thin));
        tab.SetToolCommand.Execute(AnnotationKind.Ellipse);
        tab.SetStyleCommand.Execute(new ToolStyle(DrawColor.Green, StrokeWidth.Medium));

        // Nothing the other tools had is lost, and the dictionary of the earlier settings was not edited in place.
        Assert.Equal(new ToolStyle(DrawColor.Blue, StrokeWidth.Thick), settings.Current.StyleOf(AnnotationKind.Ink));
        Assert.Equal(new ToolStyle(DrawColor.Orange, StrokeWidth.Thin), settings.Current.StyleOf(AnnotationKind.Arrow));
        Assert.Equal(new ToolStyle(DrawColor.Green, StrokeWidth.Medium), settings.Current.StyleOf(AnnotationKind.Ellipse));
        Assert.Equal(AppSettings.DefaultToolStyle, settings.Current.StyleOf(AnnotationKind.Line));
        Assert.Equal([AnnotationKind.Ink], afterInk!.Keys.ToArray());

        // The markup colours are not touched by a drawing style.
        Assert.Equal(MarkupColor.Yellow, settings.Current.HighlightColor);
    }

    [AvaloniaFact]
    public async Task UnchangedStyleDoesNotWriteSettings()
    {
        var settings = UserSettings.Detached();
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var changes = 0;
        var saves = 0;
        settings.Changed += _ => changes++;
        settings.SaveCompleted += () => saves++;

        // The style a tool has already (also the default one it never stored) changes and writes nothing.
        tab.SetToolCommand.Execute(AnnotationKind.Line);
        tab.SetStyleCommand.Execute(AppSettings.DefaultToolStyle);
        Assert.Equal(0, changes);
        Assert.Equal(0, saves);
        Assert.Null(settings.Current.ToolStyles);

        // A new style writes once; the same one again writes nothing.
        var blue = new ToolStyle(DrawColor.Blue, StrokeWidth.Thick);
        tab.SetStyleCommand.Execute(blue);
        Assert.Equal(1, changes);
        Assert.Equal(1, saves);
        tab.SetStyleCommand.Execute(new ToolStyle(DrawColor.Blue, StrokeWidth.Thick));
        Assert.Equal(1, changes);
        Assert.Equal(1, saves);

        // Choosing a tool never writes.
        tab.SetToolCommand.Execute(AnnotationKind.Line);
        tab.SetToolCommand.Execute(AnnotationKind.Arrow);
        tab.SelectToolCommand.Execute(null);
        Assert.Equal(1, saves);

        // A style with a value outside the enums is refused.
        tab.SetToolCommand.Execute(AnnotationKind.Line);
        Assert.False(tab.SetStyleCommand.CanExecute(new ToolStyle((DrawColor)99, StrokeWidth.Thin)));
        tab.SetStyleCommand.Execute(new ToolStyle(DrawColor.Red, (StrokeWidth)99));
        Assert.Equal(1, changes);
        Assert.Equal(blue, settings.Current.StyleOf(AnnotationKind.Line));
    }

    [AvaloniaFact]
    public async Task CommandsAppearInThePaletteOnlyWhenRunnable()
    {
        var (window, viewModel, view) = Show();

        // No tab: none of the tool commands runs, and none is offered.
        Assert.All(ToolIds, id => Assert.False(viewModel.Commands.ById(id)!.CanExecute(), id));
        Assert.Empty(PaletteKeys(window, viewModel).Intersect(ToolIds));
        viewModel.HideOverlay();

        // A shown document that allows comments: all six are offered, with the mode off and on.
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        Assert.Equal(ToolIds.Order(StringComparer.Ordinal), PaletteKeys(window, viewModel).Intersect(ToolIds).Order(StringComparer.Ordinal));
        viewModel.HideOverlay();
        await BeginAsync(window, tab);
        Assert.Equal(ToolIds.Order(StringComparer.Ordinal), PaletteKeys(window, viewModel).Intersect(ToolIds).Order(StringComparer.Ordinal));
        viewModel.HideOverlay();

        // A document that forbids comments: none runs, none is offered.
        var forbidden = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        forbidden.PermissionReader = (_, _) => Task.FromResult(false);
        viewModel.Commands.ById("annotate.mode")!.Execute();
        await WaitUntilAsync(window, () => !forbidden.CanAnnotate);
        Assert.False(forbidden.IsAnnotating);
        Assert.All(ToolIds, id => Assert.False(viewModel.Commands.ById(id)!.CanExecute(), id));
        Assert.Empty(PaletteKeys(window, viewModel).Intersect(ToolIds));
        viewModel.HideOverlay();

        // The help card lists them with their keys.
        Press(window, Key.F1, PhysicalKey.F1, RawInputModifiers.None);
        var card = Find<HelpCard>(window, "HelpCard");
        var rows = card.GetVisualDescendants().OfType<Control>().Where(control => control.Classes.Contains("helpRow")).Select(row => (string)row.Tag!).ToArray();
        Assert.All(ToolIds, id => Assert.Contains(id, rows));
    }

    [AvaloniaFact]
    public async Task EscFirstCancelsASelectionThenEndsTheMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");

        foreach (var tool in new AnnotationKind?[] { null, AnnotationKind.Ink, AnnotationKind.Arrow })
        {
            await BeginAsync(window, tab);
            if (tool is { } kind)
            {
                tab.SetToolCommand.Execute(kind);
            }
            else
            {
                tab.SelectToolCommand.Execute(null);
            }

            tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8));
            view.Focus();
            Settle(window);

            // The first Esc clears the text selection; the mode and the tool stay.
            Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Null(tab.TextSelection);
            Assert.True(tab.IsAnnotating);
            Assert.Equal(tool, tab.ActiveTool);
            Assert.Equal(tool is null, tab.IsSelectTool);

            // The second ends the mode, and with it the tool.
            Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(tab.IsAnnotating);
            Assert.Null(tab.ActiveTool);
            Assert.False(tab.IsSelectTool);
        }
    }

    [AvaloniaFact]
    public void CommandsHaveTitlesAndKeysThatDoNotCollide()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached());
        _cleanup.Add(viewModel);
        var catalog = viewModel.Commands;
        var expected = new (string Id, string English, string German, string Gesture)[]
        {
            ("annotate.select", "Select and move drawings", "Zeichnungen auswählen und verschieben", "Strg+Umschalt+M"),
            ("annotate.ink", "Draw freehand", "Freihand zeichnen", "Strg+Umschalt+I"),
            ("annotate.rectangle", "Draw a rectangle", "Rechteck zeichnen", "Strg+Umschalt+R"),
            ("annotate.ellipse", "Draw an oval", "Ellipse zeichnen", "Strg+Umschalt+E"),
            ("annotate.line", "Draw a line", "Linie zeichnen", "Strg+Umschalt+L"),
            ("annotate.arrow", "Draw an arrow", "Pfeil zeichnen", "Strg+Umschalt+A"),
            ("annotate.textbox", "Add a text box", "Textfeld hinzufügen", "Strg+Umschalt+T"),
        };

        foreach (var (id, english, german, gesture) in expected)
        {
            var command = catalog.ById(id)!;
            Assert.True(command.Listed);
            Assert.Equal(CommandCatalog.EditGroup, command.Group);
            Assert.False(command.YieldsToTextInput);
            Strings.Culture = new CultureInfo("en");
            Assert.Equal(english, command.Title());
            Strings.Culture = new CultureInfo("de");
            Assert.Equal(german, command.Title());
            Assert.Equal(gesture, GestureText.Of(command.Gesture!));
            Assert.Same(command, catalog.ForGesture(command.Gesture!));
            Assert.Single(catalog.All, other => other.Gestures.Contains(command.Gesture!));
        }

        Assert.DoesNotContain(catalog.All, command => command.Id.StartsWith("annotate.", StringComparison.Ordinal) && command.Gestures.Any(gesture => gesture.Key == Key.Escape));
    }

    [Fact]
    public void ColourAndWidthNamesExistInBothLanguages()
    {
        var expected = new (DrawColor Color, string English, string German)[]
        {
            (DrawColor.Red, "Red", "Rot"),
            (DrawColor.Black, "Black", "Schwarz"),
            (DrawColor.Blue, "Blue", "Blau"),
            (DrawColor.Green, "Green", "Grün"),
            (DrawColor.Orange, "Orange", "Orange"),
        };
        Assert.Equal(DrawPalette.All, expected.Select(row => row.Color));
        foreach (var (color, english, german) in expected)
        {
            Strings.Culture = new CultureInfo("en");
            Assert.Equal(english, Strings.DrawColorName(color));
            Strings.Culture = new CultureInfo("de");
            Assert.Equal(german, Strings.DrawColorName(color));
        }

        var widths = new (StrokeWidth Width, string English, string German)[]
        {
            (StrokeWidth.Thin, "Thin (1 pt)", "Dünn (1 pt)"),
            (StrokeWidth.Medium, "Medium (2 pt)", "Mittel (2 pt)"),
            (StrokeWidth.Thick, "Thick (4 pt)", "Dick (4 pt)"),
        };
        foreach (var (width, english, german) in widths)
        {
            Strings.Culture = new CultureInfo("en");
            Assert.Equal(english, Strings.StrokeWidthName(width));
            Strings.Culture = new CultureInfo("de");
            Assert.Equal(german, Strings.StrokeWidthName(width));
        }

        // The names of the tools for tooltips and screen readers.
        var tools = new (Func<string> Name, string English, string German)[]
        {
            (() => Strings.ToolSelect, "Select", "Auswählen"),
            (() => Strings.ToolInk, "Freehand", "Freihand"),
            (() => Strings.ToolRectangle, "Rectangle", "Rechteck"),
            (() => Strings.ToolEllipse, "Oval", "Ellipse"),
            (() => Strings.ToolLine, "Line", "Linie"),
            (() => Strings.ToolArrow, "Arrow", "Pfeil"),
        };
        foreach (var (name, english, german) in tools)
        {
            Strings.Culture = new CultureInfo("en");
            Assert.Equal(english, name());
            Strings.Culture = new CultureInfo("de");
            Assert.Equal(german, name());
        }
    }

    private static List<string> PaletteKeys(Window window, MainViewModel viewModel)
    {
        viewModel.ShowPaletteCommand.Execute(null);
        Settle(window);
        var palette = viewModel.Palette!;
        palette.Query = string.Empty;
        return palette.Items.Select(item => item.Key).OfType<string>().ToList();
    }

    private static async Task BeginAsync(Window window, TabViewModel tab)
    {
        if (!tab.IsAnnotating)
        {
            tab.BeginAnnotateCommand.Execute(null);
            await WaitUntilAsync(window, () => tab.IsAnnotating && FindOrNull<AnnotationToolbar>(window, "AnnotationToolbar") is { IsEffectivelyVisible: true });
        }
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
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private string Copy(string fixture, string? name = null)
    {
        var path = System.IO.Path.Combine(_folder, name ?? fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(UserSettings? settings = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings ?? UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a copy of a test file (in the temp folder) in a new tab and waits for its first sharp page.</summary>
    private async Task<TabViewModel> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(Copy(name, Guid.NewGuid().ToString("N")[..8] + "-" + name));
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
