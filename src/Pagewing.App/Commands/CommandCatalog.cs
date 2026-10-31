using System.Text;
using System.Windows.Input;
using Avalonia.Input;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Commands;

/// <summary>
/// Every command of a window with its shortcut: the only place where the shortcuts of the app are defined. The window
/// binds its keys from <see cref="All"/>, the help card lists them, the command palette offers them. Each command acts
/// on what the window shows when it runs (<see cref="MainViewModel.ActiveTab"/> at that moment) and calls the existing
/// command of the view model, so a key, a button and the palette do the same.
/// </summary>
public sealed class CommandCatalog
{
    /// <summary>Group of the commands for files and the history.</summary>
    public const string FileGroup = "file";

    /// <summary>Group of undo and redo of the changes to a document.</summary>
    public const string EditGroup = "edit";

    /// <summary>Group of zoom, layout, page theme and panels.</summary>
    public const string ViewGroup = "view";

    /// <summary>Group of moving through the document.</summary>
    public const string NavigationGroup = "nav";

    /// <summary>Group of the full-text search.</summary>
    public const string SearchGroup = "search";

    /// <summary>Group of tabs and window-wide cards.</summary>
    public const string WindowGroup = "window";

    private readonly MainViewModel _window;
    private readonly Dictionary<string, AppCommand> _byId;

    public CommandCatalog(MainViewModel window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        All = Build();
        _byId = All.ToDictionary(command => command.Id, StringComparer.Ordinal);
    }

    /// <summary>Gets the groups in the order the help card and the palette show them.</summary>
    public static IReadOnlyList<string> Groups { get; } = [FileGroup, EditGroup, ViewGroup, NavigationGroup, SearchGroup, WindowGroup];

    /// <summary>Gets every command, grouped in the order of <see cref="Groups"/>.</summary>
    public IReadOnlyList<AppCommand> All { get; }

    /// <summary>Gets the name of a group in the language of the interface.</summary>
    public static string GroupTitle(string group) => group switch
    {
        FileGroup => Strings.CommandGroupFile,
        EditGroup => Strings.CommandGroupEdit,
        ViewGroup => Strings.CommandGroupView,
        NavigationGroup => Strings.CommandGroupNavigation,
        SearchGroup => Strings.CommandGroupSearch,
        WindowGroup => Strings.CommandGroupWindow,
        _ => group,
    };

    /// <summary>Gets the command with the id, or <see langword="null"/>.</summary>
    public AppCommand? ById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Gets the command bound to exactly this gesture (its own or an alternative), or <see langword="null"/>.</summary>
    public AppCommand? ForGesture(KeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        foreach (var command in All)
        {
            foreach (var bound in command.Gestures)
            {
                if (bound.Equals(gesture))
                {
                    return command;
                }
            }
        }

        return null;
    }

    private static KeyGesture Ctrl(Key key) => new(key, KeyModifiers.Control);

    private static KeyGesture CtrlShift(Key key) => new(key, KeyModifiers.Control | KeyModifiers.Shift);

    private static string LayoutTitle(string layout) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(Strings.LayoutTooltip), layout);

    private AppCommand[] Build() =>
    [
        // File. Ctrl+O opens the dialog in every state of the window, as before.
        Window("file.open", () => Strings.OpenFile, FileGroup, Ctrl(Key.O), _window.OpenCommand),
        Window("file.closeTab", () => Strings.CloseTabTooltip, FileGroup, Ctrl(Key.W), _window.CloseActiveTabCommand),

        // Ctrl+S only while there is something to save; "Save as" once the document is open. Neither while the tab prints.
        Tab("file.save", () => Strings.CommandSave, FileGroup, Ctrl(Key.S), tab => tab.SaveCommand),
        Tab("file.saveAs", () => Strings.CommandSaveAs, FileGroup, CtrlShift(Key.S), tab => tab.SaveAsCommand),
        Window("file.print", () => Strings.CommandPrint, FileGroup, Ctrl(Key.P), _window.PrintCommand),
        new(
            "recent.clear", () => Strings.CommandClearRecent, FileGroup, null,
            () => _window.ClearRecentFiles is not null,
            () => _window.ClearRecentFiles?.Invoke()),

        // Edit: undo and redo of the annotations, only while there is a step. A text field keeps these keys for its own text
        // (YieldsToTextInput), as it keeps Ctrl+C.
        Tab("edit.undo", () => Strings.CommandUndo, EditGroup, Ctrl(Key.Z), tab => tab.UndoCommand) with { YieldsToTextInput = true },
        Tab("edit.redo", () => Strings.CommandRedo, EditGroup, CtrlShift(Key.Z), tab => tab.RedoCommand)
            with { AlternativeGestures = [Ctrl(Key.Y)], YieldsToTextInput = true },

        // The mode "Comment" (design system 7.7): on and off, and the three markups, which start the mode and turn a text
        // selection into a markup at once. Only once the document shows a sharp page and allows annotations (the palette
        // offers them only then); the Esc of the mode belongs to the document, the toolbar and the panel, not to the window.
        Tab("annotate.mode", () => Strings.CommandAnnotateMode, EditGroup, CtrlShift(Key.K), tab => tab.ToggleAnnotateCommand),
        Tab("annotate.highlight", () => Strings.CommandHighlight, EditGroup, CtrlShift(Key.H), tab => tab.HighlightCommand),
        Tab("annotate.underline", () => Strings.CommandUnderline, EditGroup, CtrlShift(Key.U), tab => tab.UnderlineCommand),
        Tab("annotate.strikeout", () => Strings.CommandStrikeOut, EditGroup, CtrlShift(Key.X), tab => tab.StrikeOutCommand),

        // The tools of the mode "Comment" for drawing (milestone 10): the select tool and freehand, rectangle, oval, line and
        // arrow, and the text box. Like the markups they start the mode with their tool and run only where the mode can run. The
        // keys are Ctrl+Shift with the letter of the tool (M move, I ink, R, E, L, A, T text), none of them a key of a text field.
        Tab("annotate.select", () => Strings.CommandSelectTool, EditGroup, CtrlShift(Key.M), tab => tab.ChooseSelectToolCommand),
        Tab("annotate.ink", () => Strings.CommandInk, EditGroup, CtrlShift(Key.I), tab => tab.InkCommand),
        Tab("annotate.rectangle", () => Strings.CommandRectangle, EditGroup, CtrlShift(Key.R), tab => tab.RectangleCommand),
        Tab("annotate.ellipse", () => Strings.CommandEllipse, EditGroup, CtrlShift(Key.E), tab => tab.EllipseCommand),
        Tab("annotate.line", () => Strings.CommandLine, EditGroup, CtrlShift(Key.L), tab => tab.LineCommand),
        Tab("annotate.arrow", () => Strings.CommandArrow, EditGroup, CtrlShift(Key.A), tab => tab.ArrowCommand),
        Tab("annotate.textbox", () => Strings.CommandTextBox, EditGroup, CtrlShift(Key.T), tab => tab.TextBoxCommand),

        // Insert signature (milestone 12): starts the mode, then the menu of the saved signatures, or the dialog when there is none.
        Window("annotate.signature", () => Strings.CommandInsertSignature, EditGroup, CtrlShift(Key.G), _window.InsertSignatureCommand),

        // The mode "Fill in form" (milestone 11): on and off, only for a document with a form once it shows a sharp page; the
        // highlighting of the fields while the mode is on. Ctrl+Shift+E is the oval's, so the mode takes F and the highlighting
        // J. Next and previous field are buttons of the toolbar only (Tab and Shift+Tab belong to the field), and the Esc of
        // the mode belongs to the document and the toolbar, as for "Comment".
        Tab("form.fill", () => Strings.FormModeName, EditGroup, CtrlShift(Key.F), tab => tab.ToggleFillCommand, ready: true),
        Tab("form.highlight", () => Strings.FormCommandHighlight, EditGroup, CtrlShift(Key.J), tab => tab.ToggleHighlightCommand),

        // View: zoom (main block, number pad, and Shift+= which is "+" on US layouts), zoom modes, layouts, page, panels.
        Tab("view.zoomIn", () => Strings.CommandZoomIn, ViewGroup, Ctrl(Key.OemPlus), tab => tab.ZoomInCommand)
            with { AlternativeGestures = [CtrlShift(Key.OemPlus), Ctrl(Key.Add)] },
        Tab("view.zoomOut", () => Strings.CommandZoomOut, ViewGroup, Ctrl(Key.OemMinus), tab => tab.ZoomOutCommand)
            with { AlternativeGestures = [Ctrl(Key.Subtract)] },
        Tab("view.fitPage", () => Strings.ZoomFitPage, ViewGroup, Ctrl(Key.D0), tab => tab.SetZoomModeCommand, ZoomMode.FitPage)
            with { AlternativeGestures = [Ctrl(Key.NumPad0)] },
        Tab("view.actualSize", () => Strings.ZoomActualSize, ViewGroup, Ctrl(Key.D1), tab => tab.SetZoomModeCommand, ZoomMode.ActualSize)
            with { AlternativeGestures = [Ctrl(Key.NumPad1)] },
        Tab("view.fitWidth", () => Strings.ZoomFitWidth, ViewGroup, Ctrl(Key.D2), tab => tab.SetZoomModeCommand, ZoomMode.FitWidth)
            with { AlternativeGestures = [Ctrl(Key.NumPad2)] },
        Layout("view.layout.single", () => LayoutTitle(Strings.LayoutSingle), PageLayoutMode.SinglePage),
        Layout("view.layout.continuous", () => LayoutTitle(Strings.LayoutContinuous), PageLayoutMode.Continuous),
        Layout("view.layout.spread", () => LayoutTitle(Strings.LayoutTwoPages), PageLayoutMode.TwoPages),
        Layout("view.layout.spreadCover", () => LayoutTitle(Strings.LayoutTwoPagesCover), PageLayoutMode.TwoPagesWithCover),
        Tab("view.pageTheme.toggle", () => Strings.CommandTogglePageTheme, ViewGroup, CtrlShift(Key.D), tab => tab.TogglePageThemeCommand, ready: true),
        Panel("panel.pages", () => Strings.CommandPagesPanel, CtrlShift(Key.D1), SidePanelKind.Pages),
        Panel("panel.contents", () => Strings.CommandContentsPanel, CtrlShift(Key.D2), SidePanelKind.Contents),
        Panel("panel.comments", () => Strings.CommandCommentsPanel, CtrlShift(Key.D3), SidePanelKind.Comments),

        // Navigation: the page field, page by page, back and forward along the link jumps (only while there is one).
        Tab("nav.goToPage", () => Strings.CommandGoToPage, NavigationGroup, Ctrl(Key.G), tab => tab.FocusPageFieldCommand),
        Tab("nav.previousPage", () => Strings.CommandPreviousPage, NavigationGroup, new KeyGesture(Key.PageUp), tab => tab.PreviousPageCommand),
        Tab("nav.nextPage", () => Strings.CommandNextPage, NavigationGroup, new KeyGesture(Key.PageDown), tab => tab.NextPageCommand),
        Tab("nav.back", () => Strings.CommandBack, NavigationGroup, new KeyGesture(Key.Left, KeyModifiers.Alt), tab => tab.BackCommand),
        Tab("nav.forward", () => Strings.CommandForward, NavigationGroup, new KeyGesture(Key.Right, KeyModifiers.Alt), tab => tab.ForwardCommand),

        // Search: Ctrl+F opens the panel (also without a tab the key is taken, as before); F3 steps, also with the panel closed.
        Window("search.open", () => Strings.CommandSearch, SearchGroup, Ctrl(Key.F), _window.OpenSearchCommand)
            with { Offered = () => _window.ActiveTab is not null },
        Tab("search.next", () => Strings.CommandSearchNext, SearchGroup, new KeyGesture(Key.F3), tab => tab.SearchNextCommand)
            with { Offered = HasSearchHits },
        Tab("search.previous", () => Strings.CommandSearchPrevious, SearchGroup, new KeyGesture(Key.F3, KeyModifiers.Shift), tab => tab.SearchPreviousCommand)
            with { Offered = HasSearchHits },

        // Window: tabs with wrap-around, the cards over the window. The keys of the cards show or hide them (a second F1
        // closes the help card also when the focus has left it).
        Window("tab.next", () => Strings.CommandNextTab, WindowGroup, Ctrl(Key.Tab), _window.NextTabCommand)
            with { Offered = () => _window.Tabs.Count > 1 },
        Window("tab.previous", () => Strings.CommandPreviousTab, WindowGroup, CtrlShift(Key.Tab), _window.PreviousTabCommand)
            with { Offered = () => _window.Tabs.Count > 1 },
        Window("palette.show", () => Strings.CommandPalette, WindowGroup, Ctrl(Key.K), _window.ShowPaletteCommand),
        Window("settings.show", () => Strings.CommandSettings, WindowGroup, Ctrl(Key.OemComma), _window.ShowSettingsCommand),
        Window("help.show", () => Strings.HelpTitle, WindowGroup, new KeyGesture(Key.F1), _window.ShowHelpCommand),

        // No key: the about card is for looking up the version and the licences, not for every day.
        Window("app.about", () => Strings.CommandAbout, WindowGroup, null, _window.ShowAboutCommand),

        // No key either: offered whenever the system has a service that can set it, also after "Don't ask again".
        new("app.setDefault", () => Strings.CommandSetDefaultApp, WindowGroup, null, _window.CanSetDefaultApp, _window.SetDefaultAppFromCommand),

        // No key either: on request only, also with the check in the background switched off. Offered where the app has an
        // updater (not in a measurement run).
        new("app.checkForUpdates", () => Strings.CommandCheckForUpdates, WindowGroup, null, _window.CanCheckForUpdates, _window.CheckForUpdatesFromCommand),

        // Esc drops an address that waits for confirmation, wherever the focus is; without one the key goes on. Not listed:
        // it is the key of the link card, not a command of its own.
        Window("link.cancel", () => Strings.LinkConfirmCancel, WindowGroup, new KeyGesture(Key.Escape), _window.CancelLinkCommand) with { Listed = false },

        // Then Esc answers "Cancel" on a card of the save flow shown in the active tab (save before closing, signature,
        // another file at the path, a failure), wherever the focus is. Not listed: it is the key of those cards.
        Window("save.cancel", () => Strings.SaveChoiceCancel, WindowGroup, new KeyGesture(Key.Escape), _window.CancelPendingSaveCommand) with { Listed = false },

        // Then Esc hides an overlay whose own Esc does not get the key (the focus has left it); without an overlay the key
        // goes on to the focused element (the document, the search field). Not listed either. The window binds the keys in
        // this order, so a waiting address goes first.
        new("overlay.hide", () => Strings.CommandCloseOverlay, WindowGroup, new KeyGesture(Key.Escape),
            () => _window.Overlay != OverlayKind.None, _window.HideOverlay, Listed: false),

        // Then Esc closes the update card or the note after "Check for updates", also when the focus is elsewhere (the card
        // opened with the mouse, the note never takes the focus). Not listed either.
        new("update.dismiss", () => Strings.CommandCloseUpdateCard, WindowGroup, new KeyGesture(Key.Escape),
            () => _window.Update is { CanDismissCard: true }, _window.DismissUpdateCard, Listed: false),
    ];

    /// <summary>Whether the active tab has a search with matches (without making its search): F3 does something.</summary>
    private bool HasSearchHits() => _window.ActiveTab is { IsSearchCreated: true } tab && tab.Search.Hits.Count > 0;

    /// <summary>A command of the window's view model, whatever the active tab.</summary>
    private static AppCommand Window(string id, Func<string> title, string group, KeyGesture? gesture, ICommand command) =>
        new(id, title, group, gesture, () => command.CanExecute(null), () =>
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        });

    /// <summary>
    /// A command of the active tab, looked up when it runs; without a tab it cannot run. With <paramref name="ready"/>
    /// it also needs an open document.
    /// </summary>
    private AppCommand Tab(string id, Func<string> title, string group, KeyGesture? gesture, Func<TabViewModel, ICommand> command, object? parameter = null, bool ready = false)
    {
        bool CanRun(out ICommand? target)
        {
            target = _window.ActiveTab is { } tab && (!ready || tab.IsReady) ? command(tab) : null;
            return target?.CanExecute(parameter) == true;
        }

        return new(id, title, group, gesture, () => CanRun(out _), () =>
        {
            if (CanRun(out var target))
            {
                target!.Execute(parameter);
            }
        });
    }

    private AppCommand Layout(string id, Func<string> title, PageLayoutMode mode) =>
        Tab(id, title, ViewGroup, null, tab => tab.SetLayoutModeCommand, mode, ready: true);

    /// <summary>Opens a panel in the active tab, or closes it when it is the open one, like its entry of the action bar.</summary>
    private AppCommand Panel(string id, Func<string> title, KeyGesture gesture, SidePanelKind kind) =>
        new(id, title, ViewGroup, gesture, () => _window.ActiveTab is not null, () =>
        {
            if (_window.ActiveTab is not null)
            {
                _window.ToggleSidePanelCommand.Execute(kind);
            }
        });
}
