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
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The "Comments" panel in the window (milestone 9, task 15): its entry in the action bar and its shortcut, what an entry
/// shows, the jump, the keyboard, the hint of a document without annotations and that the panel is read-only and
/// virtualized. Every file is a copy in a temp folder; settings and history are in memory.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class CommentsPanelTests : IDisposable
{
    private const RawInputModifiers CtrlShift = RawInputModifiers.Control | RawInputModifiers.Shift;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-comments-panel-" + Guid.NewGuid().ToString("N"))).FullName;

    public CommentsPanelTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task CommentsViewModelNotCreatedAtStart()
    {
        var (window, viewModel) = Show();
        var tab = await OpenAsync(window, viewModel, "annotations-foreign.pdf");

        // Open, first sharp page shown, "Pages" opened by the wide window: nothing of the comments exists.
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages);
        Settle(window);
        Assert.False(tab.IsCommentsCreated);
        Assert.False(tab.HasEditor);
        Assert.Null(FindOrNull<CommentsPanel>(window, "CommentsContent"));

        // Ctrl+Shift+3 builds the panel and its list; a second time closes it and keeps the list.
        Press(window, Key.D3, PhysicalKey.Digit3, CtrlShift);
        Assert.Equal(SidePanelKind.Comments, tab.SidePanel);
        await WaitUntilAsync(window, () => IsShown(window, "CommentList"));
        Assert.True(tab.IsCommentsCreated);
        Assert.Equal("Comments", Find<TextBlock>(window, "PanelTitle").Text);
        var comments = tab.Comments;
        Press(window, Key.D3, PhysicalKey.Digit3, CtrlShift);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.Same(comments, tab.Comments);
    }

    [AvaloniaTheory]
    [InlineData("en", "Comments (Ctrl+Shift+3)", "Comments")]
    [InlineData("de", "Kommentare (Strg+Umschalt+3)", "Kommentare")]
    public async Task ActionBarEntryReportsToggleStateToScreenReaders(string language, string tooltip, string name)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel) = Show(width: 800);
        var tab = await OpenAsync(window, viewModel, "annotations-foreign.pdf");

        // Below "Search": 40 px, the Tabler icon "messages" in 20 px, the tooltip with the catalog's shortcut.
        var search = Find<Button>(window, "SearchButton");
        var button = Find<Button>(window, "CommentsButton");
        Assert.Equal(new Size(40, 40), button.Bounds.Size);
        var icon = Assert.Single(button.GetVisualDescendants().OfType<StrokeIcon>());
        Assert.Equal(new Size(20, 20), icon.Bounds.Size);
        Assert.Same(Resource("Pw.Icon.Messages"), icon.Data);
        Assert.True(search.TranslatePoint(default, window)!.Value.Y < button.TranslatePoint(default, window)!.Value.Y);
        Assert.Equal(tooltip, ToolTip.GetTip(button));

        // A toggle button for screen readers, off while the panel is closed, with its name and the shortcut as accelerator.
        var peer = Peer(window, "CommentsButton");
        Assert.IsType<ToggleButtonAutomationPeer>(peer);
        Assert.Equal(name, peer.GetName());
        Assert.Equal(GestureText.Of(viewModel.Commands.ById("panel.comments")!.Gesture!), peer.GetAcceleratorKey());
        Assert.Equal(ToggleState.Off, ToggleStateOf(peer));
        Assert.DoesNotContain("active", button.Classes);

        Click(window, button);
        Assert.Equal(SidePanelKind.Comments, tab.SidePanel);
        Assert.True(viewModel.IsCommentsPanelOpen);
        Assert.Equal(ToggleState.On, ToggleStateOf(peer));
        Assert.Contains("active", button.Classes);
        Assert.Equal(ToggleState.Off, ToggleStateOf(Peer(window, "SearchButton")));

        // Another panel turns it off; the screen reader's toggle opens it again, a second one closes it.
        Click(window, search);
        Assert.Equal(ToggleState.Off, ToggleStateOf(peer));
        ((IToggleProvider)peer.GetProvider<IToggleProvider>()!).Toggle();
        Settle(window);
        Assert.Equal(SidePanelKind.Comments, tab.SidePanel);
        Assert.Equal(ToggleState.On, ToggleStateOf(peer));
        ((IToggleProvider)peer.GetProvider<IToggleProvider>()!).Toggle();
        Settle(window);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.Equal(ToggleState.Off, ToggleStateOf(peer));
    }

    [AvaloniaFact]
    public async Task EntriesShowKindAsIconAndText()
    {
        var (window, viewModel) = Show();
        var tab = await OpenAsync(window, viewModel, "annotations-foreign.pdf");
        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(6) is { IsEffectivelyVisible: true });

        foreach (var (index, iconKey, kind) in new[]
        {
            (0, "Pw.Icon.Highlight", "Highlight"),
            (2, "Pw.Icon.Pencil", "Freehand"),
            (4, "Pw.Icon.Note", "Other: Text"),
            (5, "Pw.Icon.Underline", "Underline"),
            (6, "Pw.Icon.Strikethrough", "Strike-out"),
        })
        {
            var container = list.ContainerFromIndex(index)!;
            var icon = Find<StrokeIcon>(container, "CommentKindIcon");
            Assert.Same(Resource(iconKey), icon.Data);
            Assert.Equal(new Size(16, 16), icon.Bounds.Size);
            Assert.Equal(kind, Find<TextBlock>(container, "CommentKind").Text);
            Assert.True(Find<TextBlock>(container, "CommentKind").IsEffectivelyVisible);
        }

        // The first entry: page mark in type.mono, author and date, the text under it, the comment, the swatch with its name.
        var first = list.ContainerFromIndex(0)!;
        var page = Find<TextBlock>(first, "CommentPage");
        Assert.Equal("1", page.Text);
        Assert.Contains("mono", page.Classes);
        Assert.Equal(11.5, page.FontSize);
        Assert.StartsWith("Alice · ", Find<TextBlock>(first, "CommentDetails").Text, StringComparison.Ordinal);
        Assert.Contains("sentence", Find<TextBlock>(first, "CommentExcerpt").Text, StringComparison.Ordinal);
        Assert.Equal("Check this sentence", Find<TextBlock>(first, "CommentText").Text);
        var swatch = Find<Border>(first, "CommentSwatch");
        Assert.True(swatch.IsEffectivelyVisible);
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 230, 0), ((Avalonia.Media.ISolidColorBrush)swatch.Background!).Color);
        Assert.Equal("Colour #FFE600", AutomationProperties.GetName(swatch));
        Assert.NotNull(ControlAutomationPeer.CreatePeerForElement(swatch).GetName());
        Assert.Equal(comments.Items[0].AutomationName, AutomationProperties.GetName(first));
        Assert.StartsWith("Page 1: Highlight, Alice · ", comments.Items[0].AutomationName, StringComparison.Ordinal);

        // An annotation without a comment or text under it shows only what it has.
        var ink = list.ContainerFromIndex(2)!;
        Assert.False(Find<TextBlock>(ink, "CommentExcerpt").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task PanelIsReadOnlyOutsideTheMode()
    {
        var (window, viewModel) = Show();
        var tab = await OpenAsync(window, viewModel, "annotations-foreign.pdf");
        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });
        Assert.False(comments.IsEditing);

        // Nothing in the content of the panel edits or deletes: no button, no text field, no check box. The filter is a
        // list that cannot be typed into (Fluent's template has a hidden field for editable boxes). The one button is the
        // way into the mode "Comment" (task 16), not an editing tool; the edit controls of the mode do not exist yet.
        var content = Find<CommentsPanel>(window, "CommentsContent");
        var filter = Assert.Single(content.GetVisualDescendants().OfType<ComboBox>());
        Assert.False(filter.IsEditable);
        var annotate = Find<Control>(content, "AnnotateButton");
        var controls = content.GetVisualDescendants().OfType<Control>()
            .Where(control => !control.GetVisualAncestors().Contains(filter) && !ReferenceEquals(control, annotate)
                && !control.GetVisualAncestors().Contains(annotate) && control.FindAncestorOfType<ScrollBar>() is null)
            .ToArray();
        Assert.DoesNotContain(controls, control => control is Button or TextBox or CheckBox or ToggleSwitch);
        Assert.Null(FindOrNull<CommentEditor>(window, "CommentEditor"));

        // A click on an entry jumps, it changes nothing.
        Click(window, list.ContainerFromIndex(0)!);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task ClickJumpsToThePage()
    {
        var (window, viewModel) = Show();
        var tab = await OpenAsync(window, viewModel, "annotations-foreign.pdf");
        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(6) is { IsEffectivelyVisible: true });
        Assert.Equal(1, tab.CurrentPage);

        // The underline on page 2: the page becomes the current one, the entry gets the edge of the current entry.
        Click(window, list.ContainerFromIndex(5)!);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(comments.Items[5].IsCurrent);
        var row = Find<Border>(list.ContainerFromIndex(5)!, "CommentRow");
        Assert.Contains("current", row.Classes);
        Assert.Equal(new Thickness(2, 0, 0, 0), row.BorderThickness);
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), ((Avalonia.Media.ISolidColorBrush)row.BorderBrush!).Color);
        Assert.Equal(SidePanelKind.Comments, tab.SidePanel);

        // Enter and Space on an entry with the focus do the same.
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(1, tab.CurrentPage);
        Assert.True(comments.Items[0].IsCurrent);
        Assert.False(comments.Items[5].IsCurrent);
        Press(window, Key.End, PhysicalKey.End);
        Press(window, Key.Space, PhysicalKey.Space);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(comments.Items[6].IsCurrent);

        // In a narrow window the panel lies over the document and closes after the jump.
        window.Width = 800;
        Settle(window);
        Assert.True(viewModel.IsNarrowWindow);
        await WaitUntilAsync(window, () => IsShown(window, "CommentList") && list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });

        // The list was scrolled to its end above (the button "Comment" leaves it less room than all the entries need).
        list.ScrollIntoView(0);
        Settle(window);
        Click(window, list.ContainerFromIndex(0)!);
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
    }

    [AvaloniaFact]
    public async Task EmptyDocumentShowsAHint()
    {
        Strings.Culture = new CultureInfo("de");
        var (window, viewModel) = Show();
        var tab = await OpenAsync(window, viewModel, "text-basic.pdf");
        var (comments, _) = await OpenPanelAsync(window, viewModel, tab);

        await WaitUntilAsync(window, () => IsShown(window, "CommentsEmptyHint"));
        Assert.Equal("Dieses Dokument enthält keine Kommentare.", Find<TextBlock>(window, "CommentsEmptyHint").Text);
        Assert.Equal("Keine Kommentare", Find<TextBlock>(window, "CommentsSummary").Text);
        Assert.False(Find<ListBox>(window, "CommentList").IsEffectivelyVisible);
        Assert.True(comments.HasNoComments);

        // A document with annotations has no hint.
        var other = await OpenAsync(window, viewModel, "annotations.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Comments);
        await WaitUntilAsync(window, () => !other.Comments.IsLoading && IsShown(window, "CommentList"));
        Assert.False(Find<TextBlock>(window, "CommentsEmptyHint").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task LargeListStaysVirtualized()
    {
        var (window, viewModel) = Show();

        // 5,000 notes on the first page, from top to bottom.
        var annotations = Enumerable.Range(0, 5000)
            .Select(i => new PdfAnnotation(0, i, $"n{i}", AnnotationKind.Other, "Text", null, [], [], new PageRect(10, i * 0.03, 20, (i * 0.03) + 10), $"Note {i}", "Bot", null, Editable: false))
            .ToArray();
        var tab = await OpenAsync(window, viewModel, "text-basic.pdf", tab => tab.AnnotationReader = (_, page, _) =>
            Task.FromResult<IReadOnlyList<PdfAnnotation>>(page == 0 ? annotations : []));
        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });

        Assert.Equal(5000, comments.Items.Count);
        Assert.Equal("5000 comments, all on page 1", comments.Summary);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 60);

        // The end of the list is reached by the keyboard, and still only a few entries are built.
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.End, PhysicalKey.End);
        await WaitUntilAsync(window, () => FocusedIndex(window, list) == 4999);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 60);
    }

    [AvaloniaFact]
    public async Task TwoHundredStrokesStayScrollable()
    {
        var (window, viewModel) = Show();
        var tab = await OpenAsync(window, viewModel, "text-basic.pdf");

        // 200 freehand strokes without a comment on page 1, made through the editor of the tab (in the temp copy).
        var red = DrawPalette.ColorOf(DrawColor.Red);
        for (var i = 0; i < 200; i++)
        {
            var top = 60 + (i * 3);
            await tab.Editor.ApplyDrawingAsync(0, DrawingGeometry.Ink([[new DisplayPoint(40, top), new DisplayPoint(120, top + 2)]], 1), red, null, null);
        }

        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });

        // One row: "Freehand, 200 strokes", with the symbol of the kind and the chevron of a closed group.
        var group = Assert.Single(comments.Items);
        Assert.True(group.IsGroup);
        Assert.Equal("Freehand, 200 strokes", Find<TextBlock>(list.ContainerFromIndex(0)!, "CommentKind").Text);
        Assert.Same(Resource("Pw.Icon.Pencil"), Find<StrokeIcon>(list.ContainerFromIndex(0)!, "CommentKindIcon").Data);
        Assert.Same(Resource("Pw.Icon.ChevronRight"), Find<StrokeIcon>(list.ContainerFromIndex(0)!, "CommentGroupChevron").Data);
        Assert.Equal("200 comments, all on page 1", comments.Summary);

        // Enter on the row opens it: 200 rows more, still virtualized, and the keyboard reaches the end.
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => comments.Items.Count == 201);
        Assert.True(group.IsExpanded);
        Assert.Same(Resource("Pw.Icon.ChevronDown"), Find<StrokeIcon>(list.ContainerFromIndex(0)!, "CommentGroupChevron").Data);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 60);
        Press(window, Key.End, PhysicalKey.End);
        await WaitUntilAsync(window, () => FocusedIndex(window, list) == 200);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 60);

        // Home and Space close it again.
        Press(window, Key.Home, PhysicalKey.Home);
        await WaitUntilAsync(window, () => FocusedIndex(window, list) == 0);
        Press(window, Key.Space, PhysicalKey.Space);
        await WaitUntilAsync(window, () => comments.Items.Count == 1);
        Assert.False(group.IsExpanded);
    }

    [AvaloniaFact]
    public async Task AMarkupCreatedInTheViewAppearsInTheList()
    {
        var (window, viewModel) = Show();
        var tab = await OpenAsync(window, viewModel, "text-basic.pdf");
        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await WaitUntilAsync(window, () => IsShown(window, "CommentsEmptyHint"));

        // A highlight over "Hello, Pagewing" on page 1: the hint goes, the entry comes with the text under it.
        await tab.Editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(18, 52, 220, 82)], new AnnotationColor(255, 235, 59), "Greeting", null);
        await WaitUntilAsync(window, () => comments.Items.Count == 1 && IsShown(window, "CommentList"));
        Assert.False(Find<TextBlock>(window, "CommentsEmptyHint").IsEffectivelyVisible);
        Assert.Equal("1 comment on page 1", Find<TextBlock>(window, "CommentsSummary").Text);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });
        Assert.Equal("Greeting", Find<TextBlock>(list.ContainerFromIndex(0)!, "CommentText").Text);
        Assert.Contains("Pagewing", comments.Items[0].Excerpt, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task TabSwitchKeepsEachTabsList()
    {
        var (window, viewModel) = Show();
        var first = await OpenAsync(window, viewModel, "annotations-foreign.pdf");
        var (firstComments, _) = await OpenPanelAsync(window, viewModel, first);
        var second = await OpenAsync(window, viewModel, "annotations.pdf");
        var (secondComments, _) = await OpenPanelAsync(window, viewModel, second);
        var root = Find<Grid>(window, "CommentsRoot");
        Assert.Same(secondComments, root.DataContext);

        viewModel.ActivateTabCommand.Execute(first);
        Settle(window);
        Assert.Same(firstComments, Find<Grid>(window, "CommentsRoot").DataContext);
        Assert.Same(firstComments, first.Comments);
        Assert.Same(secondComments, second.Comments);
    }

    [AvaloniaTheory]
    [MemberData(nameof(TokenTests.AllThemes), MemberType = typeof(TokenTests))]
    public void PanelColoursMeetAA(string theme)
    {
        var variant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "HighContrast" => HighContrast.Variant,
            _ => ThemeVariant.Light,
        };
        double Ratio(string foreground, string background) => Wcag.Contrast(TokenTests.Token(foreground, variant), TokenTests.Token(background, variant));

        // Kind, comment, page mark, author, date and the text under the markup on the panel, on hover and on the current entry.
        foreach (var surface in new[] { "Surface.Sidebar", "Surface.Hover", "Accent.Subtle" })
        {
            Assert.True(Ratio("Text.Primary", surface) >= 4.5, $"text.primary on {surface}");
            Assert.True(Ratio("Text.Secondary", surface) >= 4.5, $"text.secondary on {surface}");
        }

        // The symbols, the edge of the current entry, the edge of the swatch and of the filter, and the filter's text.
        Assert.True(Ratio("Text.Secondary", "Surface.Sidebar") >= 3, "symbol on surface.sidebar");
        Assert.True(Ratio("Accent.Default", "Accent.Subtle") >= 3, "accent.default on accent.subtle");
        Assert.True(Ratio("Border.Strong", "Surface.Sidebar") >= 3, "border.strong on surface.sidebar");
        Assert.True(Ratio("Text.Primary", "Surface.Raised") >= 4.5, "text.primary on surface.raised");
    }

    [AvaloniaFact]
    public async Task CommentsPanelKeyboardOnlyUseWorks()
    {
        var (window, viewModel) = Show();
        var tab = await OpenAsync(window, viewModel, "annotations-foreign.pdf");
        var view = Find<DocumentView>(window, "DocumentView");
        view.Focus(NavigationMethod.Tab);
        Settle(window);

        // Ctrl+Shift+3 opens the panel; Tab reaches the filter, the arrows change it while it is closed.
        Press(window, Key.D3, PhysicalKey.Digit3, CtrlShift);
        var comments = tab.Comments;
        await WaitUntilAsync(window, () => !comments.IsLoading && IsShown(window, "FilterBox"));
        var filter = Find<ComboBox>(window, "FilterBox");
        Assert.Equal(Strings.CommentsFilter, AutomationProperties.GetName(filter));
        filter.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(CommentFilter.Highlight, comments.Filter);
        Assert.Equal(2, comments.Items.Count);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(CommentFilter.All, comments.Filter);

        // F4 opens the list; Esc closes only the list (the panel stays, the focus is on the box again).
        Press(window, Key.F4, PhysicalKey.F4);
        await WaitUntilAsync(window, () => filter.IsDropDownOpen);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(filter.IsDropDownOpen);
        Assert.Equal(SidePanelKind.Comments, tab.SidePanel);
        Assert.Same(filter, window.FocusManager!.GetFocusedElement());

        // In the list the keys move the cursor without turning the pages of the document; Enter jumps.
        var list = Find<ListBox>(window, "CommentList");
        await WaitUntilAsync(window, () => list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(1, FocusedIndex(window, list));
        Press(window, Key.PageDown, PhysicalKey.PageDown);
        Assert.Equal(1, tab.CurrentPage);
        Assert.InRange(FocusedIndex(window, list), 2, 6);
        Press(window, Key.Home, PhysicalKey.Home);
        Assert.Equal(0, FocusedIndex(window, list));
        Press(window, Key.End, PhysicalKey.End);
        Assert.Equal(6, FocusedIndex(window, list));
        Press(window, Key.PageUp, PhysicalKey.PageUp);
        Assert.InRange(FocusedIndex(window, list), 0, 5);
        Assert.Equal(1, tab.CurrentPage);
        Press(window, Key.End, PhysicalKey.End);
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(comments.Items[5].IsCurrent);
    }

    private static AutomationPeer Peer(Window window, string name) =>
        ControlAutomationPeer.CreatePeerForElement(Find<Control>(window, name));

    private static ToggleState ToggleStateOf(AutomationPeer peer) =>
        ((IToggleProvider)peer.GetProvider<IToggleProvider>()!).ToggleState;

    private static int FocusedIndex(Window window, ListBox list) =>
        window.FocusManager!.GetFocusedElement() is Control focused && focused.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item
            ? list.IndexFromContainer(item)
            : -1;

    private static async Task<(CommentsViewModel Comments, ListBox List)> OpenPanelAsync(Window window, MainViewModel viewModel, TabViewModel tab)
    {
        if (tab.SidePanel != SidePanelKind.Comments)
        {
            viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Comments);
        }

        var comments = tab.Comments;
        await WaitUntilAsync(window, () => !comments.IsLoading && (IsShown(window, "CommentList") || IsShown(window, "CommentsEmptyHint")));
        return (comments, Find<ListBox>(window, "CommentList"));
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(double width = 1024)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached(), RecentFilesService.Detached())
        {
            IsWindowsTitleBar = true,
        };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
    }

    /// <summary>Opens a copy of a test file in a new tab and waits until the tab has shown its first sharp page.</summary>
    private async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, string name, Action<TabViewModel>? configure = null)
    {
        var path = System.IO.Path.Combine(_folder, $"{viewModel.Tabs.Count}-{name}");
        File.Copy(TestData.Pdf(name), path);
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");

        // Nothing reads annotations before the panel opens, so a test can replace how they are read until then.
        configure?.Invoke(tab);
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage);
        return tab;
    }

    private static bool IsShown(Window window, string name) =>
        FindOrNull<Control>(window, name) is { IsEffectivelyVisible: true } control
        && control.GetSelfAndVisualAncestors().OfType<Visual>().All(visual => visual.Opacity >= 1);

    private static object? Resource(string key) => Application.Current!.FindResource(key);

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control)
    {
        var point = CenterOf(window, control);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers = RawInputModifiers.None)
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
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible)
        ?? root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
