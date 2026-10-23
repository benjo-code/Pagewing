using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// What screen readers learn about states that are visible (Milestone 6, design system 7.2, 7.3 and 7.10): the panel
/// entries of the action bar are toggle buttons that are on while their panel is open, the active tab is the selected
/// tab item, the help card rows and the palette rows are exposed with their names and the palette's chosen row with its
/// selection. Everything is read through the automation peers, as UI Automation and AT-SPI read it.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class AutomationStateTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly string[] PanelEntries = ["PagesButton", "ContentsButton", "SearchButton"];

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public AutomationStateTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task TheOpenPanelEntryReportsToggledOn()
    {
        var (window, viewModel, view) = Show(800);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");

        // Without a panel every entry is a toggle button that is off; the settings entry is a plain button.
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        foreach (var name in PanelEntries)
        {
            var peer = Peer(window, name);
            Assert.IsType<ToggleButtonAutomationPeer>(peer);
            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            Assert.Equal(ToggleState.Off, ToggleStateOf(window, name));
        }

        var settings = Peer(window, "SettingsButton");
        Assert.Null(settings.GetProvider<IToggleProvider>());
        Assert.NotNull(settings.GetProvider<IInvokeProvider>());

        // A click opens "Pages": its entry is on, the others stay off.
        Click(window, Find<Button>(window, "PagesButton"));
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        Assert.Equal([ToggleState.On, ToggleState.Off, ToggleState.Off], PanelEntries.Select(name => ToggleStateOf(window, name)));

        // Another entry moves the state with the panel.
        Click(window, Find<Button>(window, "ContentsButton"));
        Assert.Equal(SidePanelKind.Contents, tab.SidePanel);
        Assert.Equal([ToggleState.Off, ToggleState.On, ToggleState.Off], PanelEntries.Select(name => ToggleStateOf(window, name)));

        // Ctrl+F opens the search: "Search" is on.
        Press(window, Key.F, PhysicalKey.F, RawInputModifiers.Control);
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
        Assert.Equal([ToggleState.Off, ToggleState.Off, ToggleState.On], PanelEntries.Select(name => ToggleStateOf(window, name)));

        // A screen reader's toggle runs the same command as a click.
        Toggle(window, "PagesButton");
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        Assert.Equal([ToggleState.On, ToggleState.Off, ToggleState.Off], PanelEntries.Select(name => ToggleStateOf(window, name)));
        Toggle(window, "PagesButton");
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.All(PanelEntries, name => Assert.Equal(ToggleState.Off, ToggleStateOf(window, name)));
    }

    [AvaloniaFact]
    public async Task ClosingThePanelReportsToggledOff()
    {
        var (window, viewModel, view) = Show(800);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        var pages = Find<Button>(window, "PagesButton");
        var changes = new List<(object? Old, object? New)>();
        Peer(window, "PagesButton").PropertyChanged += (_, e) =>
        {
            if (e.Property == TogglePatternIdentifiers.ToggleStateProperty)
            {
                changes.Add((e.OldValue, e.NewValue));
            }
        };

        // Opened by a click, closed by the close button of the panel: the entry reports off, and a screen reader hears it.
        Click(window, pages);
        Assert.Equal(ToggleState.On, ToggleStateOf(window, "PagesButton"));
        await WaitUntilAsync(window, () => FindOrNull<Button>(window, "ClosePanelButton") is { IsEffectivelyVisible: true });
        Click(window, Find<Button>(window, "ClosePanelButton"));
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.Equal(ToggleState.Off, ToggleStateOf(window, "PagesButton"));
        Assert.Equal([(ToggleState.Off, ToggleState.On), (ToggleState.On, ToggleState.Off)], changes);

        // The state never drifts from the panel: the next click opens it again and reports on, the one after closes it.
        Click(window, pages);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        Assert.Equal(ToggleState.On, ToggleStateOf(window, "PagesButton"));
        Click(window, pages);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.Equal(ToggleState.Off, ToggleStateOf(window, "PagesButton"));

        // Closed by its shortcut (Ctrl+Shift+1 twice) and by the command: off each time.
        Press(window, Key.D1, PhysicalKey.Digit1, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(ToggleState.On, ToggleStateOf(window, "PagesButton"));
        Press(window, Key.D1, PhysicalKey.Digit1, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.Equal(ToggleState.Off, ToggleStateOf(window, "PagesButton"));
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        Assert.Equal(ToggleState.On, ToggleStateOf(window, "PagesButton"));

        // A tab without a panel: every entry is off; back on the first tab, its panel is on again.
        var second = await OpenAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Equal(SidePanelKind.None, second.SidePanel);
        Assert.All(PanelEntries, name => Assert.Equal(ToggleState.Off, ToggleStateOf(window, name)));
        viewModel.ActivateTabCommand.Execute(tab);
        Settle(window);
        Assert.Equal(ToggleState.On, ToggleStateOf(window, "PagesButton"));

        // Closing the tab that has the panel leaves the remaining tab's state: off.
        viewModel.CloseTabCommand.Execute(tab);
        Settle(window);
        Assert.All(PanelEntries, name => Assert.Equal(ToggleState.Off, ToggleStateOf(window, name)));
        Click(window, pages);
        Assert.Equal(SidePanelKind.Pages, second.SidePanel);
        Assert.Equal(ToggleState.On, ToggleStateOf(window, "PagesButton"));
    }

    [AvaloniaFact]
    public async Task TheActiveTabReportsSelected()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenAsync(window, viewModel, view, "outline.pdf");
        var second = await OpenAsync(window, viewModel, view, "hello_world.pdf");

        // Each tab is a tab item named after its file; the active one (the second) is selected, the other is not.
        var active = TabPeer(window, second);
        var inactive = TabPeer(window, first);
        foreach (var peer in new[] { active, inactive })
        {
            Assert.Equal(AutomationControlType.TabItem, peer.GetAutomationControlType());
            Assert.True(peer.IsControlElement());
            Assert.True(peer.IsContentElement());
        }

        Assert.Equal("hello_world.pdf", active.GetName());
        Assert.Equal("outline.pdf", inactive.GetName());
        Assert.True(active.GetProvider<ISelectionItemProvider>()!.IsSelected);
        Assert.False(inactive.GetProvider<ISelectionItemProvider>()!.IsSelected);

        // The strip is the tab list: one selected tab, the active one, and it is the container of its tabs.
        var strip = ControlAutomationPeer.CreatePeerForElement(Find<TabStripPanel>(window, "TabPanel"));
        Assert.Equal(AutomationControlType.Tab, strip.GetAutomationControlType());
        var selection = strip.GetProvider<ISelectionProvider>()!;
        Assert.False(selection.CanSelectMultiple);
        Assert.True(selection.IsSelectionRequired);
        Assert.Same(active, Assert.Single(selection.GetSelection()));
        Assert.Same(selection, active.GetProvider<ISelectionItemProvider>()!.SelectionContainer);
        Assert.Contains(active, strip.GetChildren());
        Assert.Contains(inactive, strip.GetChildren());
    }

    [AvaloniaFact]
    public async Task SwitchingTabsMovesTheSelection()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenAsync(window, viewModel, view, "outline.pdf");
        var second = await OpenAsync(window, viewModel, view, "hello_world.pdf");
        var names = new List<object?>();
        TabPeer(window, second).PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.NameProperty)
            {
                names.Add(e.NewValue);
            }
        };
        var selectionChanges = 0;
        ControlAutomationPeer.CreatePeerForElement(Find<TabStripPanel>(window, "TabPanel")).PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectionPatternIdentifiers.SelectionProperty)
            {
                selectionChanges++;
            }
        };

        // Ctrl+Tab activates the first tab: it is the selected one now, named after its file; the second is not.
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.Same(first, viewModel.ActiveTab);
        AssertSelection(window, selected: first, other: second);

        // The view of the active tab stays one control: it tells a screen reader that its name changed, and the tab list
        // that its selection changed (on AT-SPI the only selection signal).
        Assert.Equal(["outline.pdf"], names);
        Assert.Equal(1, selectionChanges);

        // A screen reader's "select" on the other tab activates it.
        TabPeer(window, second).GetProvider<ISelectionItemProvider>()!.Select();
        Settle(window);
        Assert.Same(second, viewModel.ActiveTab);
        AssertSelection(window, selected: second, other: first);
        Assert.Equal(2, selectionChanges);

        // Selecting the selected tab changes nothing.
        TabPeer(window, second).GetProvider<ISelectionItemProvider>()!.Select();
        Settle(window);
        Assert.Same(second, viewModel.ActiveTab);
        Assert.Equal(2, selectionChanges);
    }

    [AvaloniaTheory]
    [InlineData("en", "Pages (Ctrl+Shift+1)", "Contents (Ctrl+Shift+2)", "Search (Ctrl+F)")]
    [InlineData("de", "Seiten (Strg+Umschalt+1)", "Inhalt (Strg+Umschalt+2)", "Suchen (Strg+F)")]
    public async Task TooltipsNameThePanelShortcuts(string language, string pages, string contents, string search)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, view) = Show(800);
        await OpenAsync(window, viewModel, view, "outline.pdf");
        var catalog = viewModel.Commands;

        Assert.Equal(pages, ToolTip.GetTip(Find<Button>(window, "PagesButton")));
        Assert.Equal(contents, ToolTip.GetTip(Find<Button>(window, "ContentsButton")));
        Assert.Equal(search, ToolTip.GetTip(Find<Button>(window, "SearchButton")));

        // The shortcuts are the catalog's; the names stay what the entries are, the shortcut is their accelerator key.
        foreach (var (name, id, label) in new[]
        {
            ("PagesButton", "panel.pages", Strings.PagesTooltip),
            ("ContentsButton", "panel.contents", Strings.ContentsTooltip),
        })
        {
            var gesture = GestureText.Of(catalog.ById(id)!.Gesture!);
            Assert.Equal($"{label} ({gesture})", ToolTip.GetTip(Find<Button>(window, name)));
            Assert.Equal(label, Peer(window, name).GetName());
            Assert.Equal(gesture, Peer(window, name).GetAcceleratorKey());
        }

        // "Search" is named after its panel like the others; its shortcut is read once, as the accelerator key.
        Assert.Equal(Strings.SearchPanelTitle, Peer(window, "SearchButton").GetName());
        Assert.Equal(GestureText.Of(catalog.ById("search.open")!.Gesture!), Peer(window, "SearchButton").GetAcceleratorKey());
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task LooksAreUnchanged(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel, view) = Show(800, variant);
        await OpenAsync(window, viewModel, view, "outline.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);

        // The entries keep the look of the icon buttons (the theme and styles of Button, not those of ToggleButton,
        // whose checked state would fill them with the accent colour) and their "active" class.
        var entries = PanelEntries.Select(name => Find<Button>(window, name)).ToArray();
        var settings = Find<Button>(window, "SettingsButton");
        foreach (var entry in entries)
        {
            Assert.IsAssignableFrom<ToggleButton>(entry);
            Assert.Equal(typeof(Button), entry.StyleKey);
            Assert.Contains("icon", entry.Classes);
            Assert.Contains("action", entry.Classes);
            Assert.Equal(settings.Bounds.Size, entry.Bounds.Size);
        }

        Assert.Contains("active", entries[0].Classes);
        Assert.Contains(":checked", entries[0].Classes);

        var subtle = TokenTests.Token("Accent.Subtle", variant);
        var surface = TokenTests.Token("Surface.Window", variant);
        using (var frame = new FramePixels(window.CaptureRenderedFrame()!))
        {
            // The checked entry: accent.subtle behind it, exactly as the active entry before; the unchecked ones lie on
            // the action bar like the settings entry.
            var active = entries[0].TranslatePoint(new Point(5, 5), window)!.Value;
            Assert.Equal(subtle, frame.GetColor((int)active.X, (int)active.Y));
            foreach (var control in entries.Skip(1).Append(settings))
            {
                var point = control.TranslatePoint(new Point(5, 5), window)!.Value;
                Assert.Equal(surface, frame.GetColor((int)point.X, (int)point.Y));
            }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public void HelpRowsAreExposedWithTheirShortcut(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _) = Show();
        Press(window, Key.F1, PhysicalKey.F1, RawInputModifiers.None);
        var card = Find<HelpCard>(window, "HelpCard");
        var catalog = viewModel.Commands;

        // The card title is the top heading, every group title a heading below it.
        Assert.Equal(1, ControlAutomationPeer.CreatePeerForElement(Find<TextBlock>(card, "HelpTitle")).GetHeadingLevel());
        var titles = card.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Classes.Contains("helpGroupTitle")).ToArray();
        Assert.NotEmpty(titles);
        Assert.All(titles, title => Assert.Equal(2, ControlAutomationPeer.CreatePeerForElement(title).GetHeadingLevel()));

        // Each row is one element a screen reader reads: "<title>, <shortcut>"; its texts are not read again.
        var rows = card.GetVisualDescendants().OfType<Control>().Where(control => control.Classes.Contains("helpRow")).ToArray();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var command = catalog.ById((string)row.Tag!)!;
            var peer = ControlAutomationPeer.CreatePeerForElement(row);
            Assert.True(peer.IsContentElement());
            Assert.True(peer.IsControlElement());
            Assert.Equal(AutomationControlType.Text, peer.GetAutomationControlType());
            Assert.Equal($"{command.Title()}, {GestureText.Of(command.Gesture!)}", peer.GetName());
            Assert.All(
                row.GetVisualDescendants().OfType<TextBlock>(),
                text => Assert.False(ControlAutomationPeer.CreatePeerForElement(text).IsControlElement()));
        }

        var zoomIn = rows.Single(row => (string)row.Tag! == "view.zoomIn");
        Assert.Equal(language == "en" ? "Zoom in, Ctrl+Plus" : "Vergrößern, Strg+Plus", ControlAutomationPeer.CreatePeerForElement(zoomIn).GetName());
    }

    [AvaloniaFact]
    public async Task ThePaletteReportsTheChosenRow()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Press(window, Key.K, PhysicalKey.K, RawInputModifiers.Control);
        var palette = viewModel.Palette!;
        var paletteView = Find<CommandPalette>(window, "CommandPalette");
        var rows = paletteView.ItemRows;
        Assert.True(rows.Count > 2);

        // The entries are list items with their names; the chosen one (the first) is selected, the others are not.
        Assert.Equal(0, palette.SelectedIndex);
        var peers = rows.Select(ControlAutomationPeer.CreatePeerForElement).ToArray();
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(AutomationControlType.ListItem, peers[i].GetAutomationControlType());
            Assert.True(peers[i].IsControlElement());
            Assert.Equal(paletteView.ItemOf(rows[i])!.AutomationName, peers[i].GetName());
            Assert.Equal(i == 0, peers[i].GetProvider<ISelectionItemProvider>()!.IsSelected);
        }

        // The group headings are headings, not list items, and cannot be selected.
        foreach (var heading in paletteView.Headings)
        {
            var root = heading.GetVisualAncestors().OfType<Control>().First(control => control.Classes.Contains("paletteSlot"));
            var peer = ControlAutomationPeer.CreatePeerForElement(root);
            Assert.Equal(heading.Text, peer.GetName());
            Assert.Equal(AutomationControlType.Text, peer.GetAutomationControlType());
            Assert.Equal(2, peer.GetHeadingLevel());
            Assert.Null(peer.GetProvider<ISelectionItemProvider>());
        }

        // Down moves the choice: the second row is selected now, the first is not, and both tell it.
        var input = ControlAutomationPeer.CreatePeerForElement(Find<TextBox>(window, "PaletteInput"));
        Assert.Equal(peers[0].GetName(), input.GetHelpText());
        var helpTexts = new List<(object? Old, object? New)>();
        input.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.HelpTextProperty)
            {
                helpTexts.Add((e.OldValue, e.NewValue));
            }
        };
        var changes = new List<(int Row, object? New)>();
        for (var i = 0; i < 2; i++)
        {
            var row = i;
            peers[i].PropertyChanged += (_, e) =>
            {
                if (e.Property == SelectionItemPatternIdentifiers.IsSelectedProperty)
                {
                    changes.Add((row, e.NewValue));
                }
            };
        }

        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal(1, palette.SelectedIndex);
        Assert.False(peers[0].GetProvider<ISelectionItemProvider>()!.IsSelected);
        Assert.True(peers[1].GetProvider<ISelectionItemProvider>()!.IsSelected);
        Assert.Equal([(0, (object?)false), (1, true)], changes.OrderBy(change => change.Row));

        // The input, which keeps the focus, names the chosen entry as its help text and tells that it changed (UI
        // Automation: HelpText, AT-SPI: accessible-description).
        Assert.Equal(peers[1].GetName(), input.GetHelpText());
        Assert.Equal([((object?)peers[0].GetName(), (object?)peers[1].GetName())], helpTexts);

        // A screen reader's "select" chooses a row as a click does, without running it.
        peers[2].GetProvider<ISelectionItemProvider>()!.Select();
        Settle(window);
        Assert.Equal(2, palette.SelectedIndex);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Assert.Equal(peers[2].GetName(), input.GetHelpText());
        Assert.Equal(2, helpTexts.Count);
        Assert.Equal(peers[2].GetName(), helpTexts[1].New);
    }

    private static void AssertSelection(Window window, TabViewModel selected, TabViewModel other)
    {
        var on = TabPeer(window, selected);
        var off = TabPeer(window, other);
        Assert.Equal(selected.FileName, on.GetName());
        Assert.Equal(other.FileName, off.GetName());
        Assert.True(on.GetProvider<ISelectionItemProvider>()!.IsSelected);
        Assert.False(off.GetProvider<ISelectionItemProvider>()!.IsSelected);
        var strip = ControlAutomationPeer.CreatePeerForElement(Find<TabStripPanel>(window, "TabPanel"));
        Assert.Same(on, Assert.Single(strip.GetProvider<ISelectionProvider>()!.GetSelection()));
    }

    private static AutomationPeer TabPeer(Window window, TabViewModel tab) =>
        ControlAutomationPeer.CreatePeerForElement(window.GetVisualDescendants().OfType<TabView>().Single(view => ReferenceEquals(view.DataContext, tab)));

    private static AutomationPeer Peer(Window window, string name) => ControlAutomationPeer.CreatePeerForElement(Find<Control>(window, name));

    private static ToggleState ToggleStateOf(Window window, string name) => Peer(window, name).GetProvider<IToggleProvider>()!.ToggleState;

    private static void Toggle(Window window, string name)
    {
        Peer(window, name).GetProvider<IToggleProvider>()!.Toggle();
        Settle(window);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(double width = 1024, ThemeVariant? theme = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = theme ?? ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a file in a new tab, waits until the view shows it and closes the panel the window opens by itself.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
    }

    private static void Click(Window window, Control control)
    {
        Settle(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
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
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
