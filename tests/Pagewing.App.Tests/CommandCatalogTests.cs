using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class CommandCatalogTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // The ids the catalog has at least (plan of milestone 6, task 9).
    private static readonly string[] RequiredIds =
    [
        "file.open", "file.closeTab", "tab.next", "tab.previous", "view.zoomIn", "view.zoomOut", "view.fitPage",
        "view.actualSize", "view.fitWidth", "view.layout.single", "view.layout.continuous", "view.layout.spread",
        "view.layout.spreadCover", "view.pageTheme.toggle", "nav.goToPage", "nav.previousPage", "nav.nextPage", "nav.back",
        "nav.forward", "panel.pages", "panel.contents", "panel.comments", "search.open", "search.next", "search.previous", "palette.show",
        "settings.show", "help.show", "app.about", "recent.clear", "app.checkForUpdates", "annotate.mode", "annotate.highlight",
        "annotate.underline", "annotate.strikeout", "annotate.select", "annotate.ink", "annotate.rectangle", "annotate.ellipse",
        "annotate.line", "annotate.arrow", "form.fill", "form.highlight",
    ];

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public CommandCatalogTests() => Strings.Culture = new CultureInfo("en");

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
    public void EveryCommandHasATitleInBothLanguages()
    {
        var viewModel = NewViewModel(new CountingPicker());
        var catalog = viewModel.Commands;
        Assert.Equal(catalog.All.Count, catalog.All.Select(command => command.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var id in RequiredIds)
        {
            Assert.True(catalog.ById(id) is not null, $"{id} is missing");
        }

        foreach (var command in catalog.All)
        {
            Assert.Contains(command.Group, CommandCatalog.Groups);
            Strings.Culture = new CultureInfo("en");
            var english = command.Title();
            var englishGroup = CommandCatalog.GroupTitle(command.Group);
            Strings.Culture = new CultureInfo("de");
            var german = command.Title();
            var germanGroup = CommandCatalog.GroupTitle(command.Group);

            Assert.False(string.IsNullOrWhiteSpace(english), $"{command.Id} has no English title");
            Assert.False(string.IsNullOrWhiteSpace(german), $"{command.Id} has no German title");
            Assert.NotEqual(english, german);
            Assert.NotEqual(englishGroup, germanGroup);
        }

        // Only the meanings of Esc at window level stay out of the lists: the link card's, the save cards', the overlays', then the update
        // card's (with the note after "Check for updates").
        Assert.Equal(["link.cancel", "save.cancel", "overlay.hide", "update.dismiss"], catalog.All.Where(command => !command.Listed).Select(command => command.Id));
        Assert.Equal(["file", "edit", "view", "nav", "search", "window"], CommandCatalog.Groups);
        Strings.Culture = new CultureInfo("de");
        Assert.Equal(["Datei", "Bearbeiten", "Ansicht", "Navigation", "Suche", "Fenster"], CommandCatalog.Groups.Select(CommandCatalog.GroupTitle));
    }

    [AvaloniaTheory]
    [InlineData("en", "Save", "Save as …", "Undo", "Redo", "Edit")]
    [InlineData("de", "Speichern", "Speichern unter …", "Rückgängig", "Wiederholen", "Bearbeiten")]
    public void EveryNewCommandHasATitleInBothLanguages(string language, string save, string saveAs, string undo, string redo, string editGroup)
    {
        var catalog = NewViewModel(new CountingPicker()).Commands;
        Strings.Culture = new CultureInfo(language);

        Assert.Equal(save, catalog.ById("file.save")!.Title());
        Assert.Equal(saveAs, catalog.ById("file.saveAs")!.Title());
        Assert.Equal(undo, catalog.ById("edit.undo")!.Title());
        Assert.Equal(redo, catalog.ById("edit.redo")!.Title());
        Assert.Equal(editGroup, CommandCatalog.GroupTitle(CommandCatalog.EditGroup));

        // Saving belongs to the files, undo and redo to their own group; all four are listed (help card, palette).
        Assert.Equal(CommandCatalog.FileGroup, catalog.ById("file.save")!.Group);
        Assert.Equal(CommandCatalog.FileGroup, catalog.ById("file.saveAs")!.Group);
        Assert.Equal(CommandCatalog.EditGroup, catalog.ById("edit.undo")!.Group);
        Assert.Equal(CommandCatalog.EditGroup, catalog.ById("edit.redo")!.Group);
        Assert.All(["file.save", "file.saveAs", "edit.undo", "edit.redo"], id => Assert.True(catalog.ById(id)!.Listed));
    }

    [AvaloniaFact]
    public void NewShortcutsDoNotCollide()
    {
        var catalog = NewViewModel(new CountingPicker()).Commands;
        var expected = new (string Id, string[] Gestures)[]
        {
            ("file.save", ["Ctrl+S"]),
            ("file.saveAs", ["Ctrl+Shift+S"]),
            ("edit.undo", ["Ctrl+Z"]),
            ("edit.redo", ["Ctrl+Shift+Z", "Ctrl+Y"]),
            ("panel.comments", ["Ctrl+Shift+D3"]),
            ("annotate.mode", ["Ctrl+Shift+K"]),
            ("annotate.highlight", ["Ctrl+Shift+H"]),
            ("annotate.underline", ["Ctrl+Shift+U"]),
            ("annotate.strikeout", ["Ctrl+Shift+X"]),
            ("annotate.select", ["Ctrl+Shift+M"]),
            ("annotate.ink", ["Ctrl+Shift+I"]),
            ("annotate.rectangle", ["Ctrl+Shift+R"]),
            ("annotate.ellipse", ["Ctrl+Shift+E"]),
            ("annotate.line", ["Ctrl+Shift+L"]),
            ("annotate.arrow", ["Ctrl+Shift+A"]),
        };

        foreach (var (id, gestures) in expected)
        {
            var command = catalog.ById(id)!;
            Assert.Equal(gestures, command.Gestures.Select(gesture => gesture.ToString()));
            foreach (var gesture in command.Gestures)
            {
                // No other command has the key (NoGestureBelongsToTwoCommands checks the whole catalog).
                Assert.Same(command, catalog.ForGesture(gesture));
                Assert.Single(catalog.All, other => other.Gestures.Contains(gesture));
            }
        }

        // The shortcut texts as the help card shows them; undo and redo leave their keys to a focused text field.
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Strg+S", GestureText.Of(catalog.ById("file.save")!.Gesture!));
        Assert.Equal("Strg+Umschalt+S", GestureText.Of(catalog.ById("file.saveAs")!.Gesture!));
        Assert.Equal("Strg+Z", GestureText.Of(catalog.ById("edit.undo")!.Gesture!));
        Assert.Equal("Strg+Umschalt+Z", GestureText.Of(catalog.ById("edit.redo")!.Gesture!));
        Assert.Equal("Strg+Umschalt+3", GestureText.Of(catalog.ById("panel.comments")!.Gesture!));
        Assert.Equal("Kommentare ein- oder ausblenden", catalog.ById("panel.comments")!.Title());
        Assert.Equal(CommandCatalog.ViewGroup, catalog.ById("panel.comments")!.Group);
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Show or hide comments", catalog.ById("panel.comments")!.Title());
        Assert.True(catalog.ById("edit.undo")!.YieldsToTextInput);
        Assert.True(catalog.ById("edit.redo")!.YieldsToTextInput);
        Assert.False(catalog.ById("file.save")!.YieldsToTextInput);
    }

    [AvaloniaFact]
    public async Task SaveAndUndoAreOnlyOfferedWhenTheyCanRun()
    {
        var (window, viewModel, view, _) = Show(new CountingPicker());
        var catalog = viewModel.Commands;
        bool Offered(string id) => catalog.ById(id) is { } command && command.CanExecute() && (command.Offered?.Invoke() ?? true);

        // Without a tab nothing of it runs.
        Assert.All(["file.save", "file.saveAs", "edit.undo", "edit.redo"], id => Assert.False(Offered(id)));

        // An open document can be saved under another name; nothing to save, undo or redo yet, and no editor is made for asking.
        var tab = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Assert.True(Offered("file.saveAs"));
        Assert.False(Offered("file.save"));
        Assert.False(Offered("edit.undo"));
        Assert.False(Offered("edit.redo"));
        Assert.False(tab.HasEditor);

        // A change: save and undo. (The document is only changed in memory here; nothing is saved.)
        await tab.Editor.ApplyMarkupAsync(0, Core.Annotations.AnnotationKind.Highlight, [new PageRect(72, 72, 220, 90)], new Core.Annotations.AnnotationColor(255, 230, 0), null, null);
        await WaitUntilAsync(window, () => tab.CanUndo);
        Assert.True(Offered("file.save"));
        Assert.True(Offered("edit.undo"));
        Assert.False(Offered("edit.redo"));

        // Undone: redo, and still unsaved (the history moved since the file was opened).
        catalog.ById("edit.undo")!.Execute();
        await WaitUntilAsync(window, () => tab.CanRedo);
        Assert.False(Offered("edit.undo"));
        Assert.True(Offered("edit.redo"));
        Assert.True(Offered("file.save"));
        Assert.Empty(await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));

        // Redone with its second key.
        view.Focus();
        Press(window, Key.Y, PhysicalKey.Y, Ctrl);
        await WaitUntilAsync(window, () => tab.CanUndo && !tab.CanRedo);
        Assert.Single(await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task CtrlZInTextFieldsIsNotConsumed()
    {
        var (window, viewModel, view, _) = Show(new CountingPicker());
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await tab.Editor.ApplyMarkupAsync(0, Core.Annotations.AnnotationKind.Highlight, [new PageRect(72, 72, 220, 90)], new Core.Annotations.AnnotationColor(255, 230, 0), null, null);
        await WaitUntilAsync(window, () => tab.CanUndo);

        // The page has its links as annotations too.
        var marked = (await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count;

        // The first stop of a key event is the window (tunnel): handled there means a key binding took the key.
        bool? takenByABinding = null;
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => takenByABinding ??= e.Handled, RoutingStrategies.Tunnel, handledEventsToo: true);
        bool Pressed(Key key, PhysicalKey physical, RawInputModifiers modifiers)
        {
            takenByABinding = null;
            Press(window, key, physical, modifiers);
            return takenByABinding ?? throw new InvalidOperationException("The key did not arrive.");
        }

        // The search field undoes its own typing; the markup stays.
        Press(window, Key.F, PhysicalKey.F, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "SearchField" });
        var search = (TextBox)Focused(window)!;
        window.KeyTextInput("abc");
        Settle(window);
        Assert.Equal("abc", search.Text);
        Assert.False(Pressed(Key.Z, PhysicalKey.Z, Ctrl));
        Assert.NotEqual("abc", search.Text);
        Assert.False(Pressed(Key.Y, PhysicalKey.Y, Ctrl));
        Assert.False(Pressed(Key.Z, PhysicalKey.Z, Ctrl | Shift));
        Assert.True(tab.CanUndo);
        Assert.Equal(marked, (await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count);

        // So does the page field.
        Press(window, Key.G, PhysicalKey.G, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PageField" });
        Assert.False(Pressed(Key.Z, PhysicalKey.Z, Ctrl));
        Assert.True(tab.CanUndo);

        // In the document the same key undoes the markup.
        view.Focus();
        Settle(window);
        Assert.True(Pressed(Key.Z, PhysicalKey.Z, Ctrl));
        await WaitUntilAsync(window, () => !tab.CanUndo);
        Assert.Equal(marked - 1, (await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count);
    }

    [AvaloniaFact]
    public void NoGestureBelongsToTwoCommands()
    {
        var catalog = NewViewModel(new CountingPicker()).Commands;
        var owners = new Dictionary<(Key, KeyModifiers), string>();
        foreach (var command in catalog.All)
        {
            Assert.Equal(command.Gestures.Count(), command.Gestures.Distinct().Count());
            foreach (var gesture in command.Gestures)
            {
                // Avalonia matches the plus and minus of the number pad as those of the main block (KeyGesture.Matches),
                // which only the alternatives of one command may share.
                // Esc alone has four unlisted commands, which the window asks in turn: the link card first, the save cards, then an
                // overlay, then the update card or its note.
                var key = (Normalized(gesture.Key), gesture.KeyModifiers);
                if (owners.TryGetValue(key, out var owner))
                {
                    var escape = gesture.Equals(new KeyGesture(Key.Escape)) && owner == "link.cancel" && command.Id is "save.cancel" or "overlay.hide" or "update.dismiss";
                    Assert.True(owner == command.Id || escape, $"{gesture} belongs to {owner} and {command.Id}");
                }
                else
                {
                    owners[key] = command.Id;
                }

                Assert.Same(catalog.ById(owners[key]), catalog.ForGesture(gesture));
            }
        }

        Assert.Null(catalog.ForGesture(new KeyGesture(Key.Q, KeyModifiers.Control)));
        Assert.Equal(
            ["Ctrl+Shift+OemPlus", "Ctrl+Add"],
            catalog.ById("view.zoomIn")!.AlternativeGestures.Select(gesture => gesture.ToString()));
        Assert.Equal(["Ctrl+Subtract"], catalog.ById("view.zoomOut")!.AlternativeGestures.Select(gesture => gesture.ToString()));
        Assert.Equal(["Ctrl+NumPad0"], catalog.ById("view.fitPage")!.AlternativeGestures.Select(gesture => gesture.ToString()));
        Assert.Equal(["Ctrl+NumPad1"], catalog.ById("view.actualSize")!.AlternativeGestures.Select(gesture => gesture.ToString()));
        Assert.Equal(["Ctrl+NumPad2"], catalog.ById("view.fitWidth")!.AlternativeGestures.Select(gesture => gesture.ToString()));
    }

    [AvaloniaFact]
    public void CommandGesturesAreUniqueAcrossCommands()
    {
        var catalog = NewViewModel(new CountingPicker()).Commands;

        // Every gesture of every command, the alternatives included, has one owner in the whole catalog; only Esc is shared by
        // the unlisted meanings of the window, which it asks in turn. (Ctrl+Shift+E belongs to annotate.ellipse, so the mode
        // "Fill in form" takes F and its highlighting J.)
        var owners = new Dictionary<(Key, KeyModifiers), List<string>>();
        foreach (var command in catalog.All)
        {
            foreach (var gesture in command.Gestures)
            {
                var key = (Normalized(gesture.Key), gesture.KeyModifiers);
                if (!owners.TryGetValue(key, out var ids))
                {
                    owners[key] = ids = [];
                }

                if (!ids.Contains(command.Id))
                {
                    ids.Add(command.Id);
                }
            }
        }

        foreach (var ((key, modifiers), ids) in owners)
        {
            if (key == Key.Escape && modifiers == KeyModifiers.None)
            {
                Assert.Equal(["link.cancel", "save.cancel", "overlay.hide", "update.dismiss"], ids);
                continue;
            }

            Assert.True(ids.Count == 1, $"{modifiers}+{key} belongs to {string.Join(", ", ids)}");
        }

        Assert.Equal(["Ctrl+Shift+F"], catalog.ById("form.fill")!.Gestures.Select(gesture => gesture.ToString()));
        Assert.Equal(["Ctrl+Shift+J"], catalog.ById("form.highlight")!.Gestures.Select(gesture => gesture.ToString()));
        Assert.Equal("annotate.ellipse", catalog.ForGesture(new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift))?.Id);
    }

    [AvaloniaFact]
    public async Task EveryFormerWindowKeyBindingStillWorks()
    {
        var picker = new CountingPicker();
        var (window, viewModel, view, _) = Show(picker);
        var basic = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var rich = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var area = new[] { new PageRect(0, 0, 10, 10) };
        var search = rich.Search;
        search.Query = "Pagewing";
        await WaitUntilAsync(window, () => search.State == SearchState.Done && search.Hits.Count == 4);

        // Three tabs (basic, rich, hello), so the next and the previous tab of the first one are different tabs.
        var hello = await OpenAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Equal(new[] { basic, rich, hello }, viewModel.Tabs);

        async Task ShowRichAsync()
        {
            if (!ReferenceEquals(viewModel.ActiveTab, rich))
            {
                viewModel.ActivateTabCommand.Execute(rich);
                await WaitUntilAsync(window, () => ReferenceEquals(view.Document, rich.Session.Document) && view.Layout is not null);
            }

            await FocusAsync(window, view);
        }

        async Task ShowBasicAsync()
        {
            viewModel.ActivateTabCommand.Execute(basic);
            await WaitUntilAsync(window, () => ReferenceEquals(view.Document, basic.Session.Document) && view.Layout is not null);
            await FocusAsync(window, view);
        }

        async Task FreeZoomAsync()
        {
            await ShowRichAsync();
            rich.Zoom = 1;
            Settle(window);
            Assert.Equal(ZoomMode.Free, rich.ZoomMode);
        }

        async Task ZoomModeAsync(ZoomMode mode)
        {
            await ShowRichAsync();
            rich.SetZoomModeCommand.Execute(mode);
            Settle(window);
            Assert.Equal(mode, rich.ZoomMode);
        }

        async Task PageAsync(int page)
        {
            await ShowRichAsync();
            rich.GoToPage(page);
            Settle(window);
            Assert.Equal(page, rich.CurrentPage);
        }

        async Task HitAsync(int index)
        {
            await ShowRichAsync();
            search.Choose(search.Hits[index]);
            Settle(window);
            Assert.Equal(index, search.CurrentIndex);
        }

        var tabsBefore = 0;

        // The 24 key bindings MainWindow.axaml had before the catalog, each with what it visibly does.
        var table = new (string Name, Key Key, PhysicalKey Physical, RawInputModifiers Modifiers, Func<Task> Arrange, Func<bool> Effect)[]
        {
            ("Ctrl+O", Key.O, PhysicalKey.O, Ctrl, ShowRichAsync, () => picker.Calls == 1),
            ("Ctrl+Tab", Key.Tab, PhysicalKey.Tab, Ctrl, ShowBasicAsync, () => ReferenceEquals(viewModel.ActiveTab, rich) && viewModel.Tabs.IndexOf(viewModel.ActiveTab) == 1),
            ("Ctrl+Shift+Tab", Key.Tab, PhysicalKey.Tab, Ctrl | Shift, ShowBasicAsync, () => ReferenceEquals(viewModel.ActiveTab, hello) && viewModel.Tabs.IndexOf(viewModel.ActiveTab) == 2),
            ("Ctrl+OemPlus", Key.OemPlus, PhysicalKey.Equal, Ctrl, FreeZoomAsync, () => rich.Zoom > 1),
            ("Ctrl+Shift+OemPlus", Key.OemPlus, PhysicalKey.Equal, Ctrl | Shift, FreeZoomAsync, () => rich.Zoom > 1),
            ("Ctrl+Add", Key.Add, PhysicalKey.NumPadAdd, Ctrl, FreeZoomAsync, () => rich.Zoom > 1),
            ("Ctrl+OemMinus", Key.OemMinus, PhysicalKey.Minus, Ctrl, FreeZoomAsync, () => rich.Zoom < 1),
            ("Ctrl+Subtract", Key.Subtract, PhysicalKey.NumPadSubtract, Ctrl, FreeZoomAsync, () => rich.Zoom < 1),
            ("Ctrl+D0", Key.D0, PhysicalKey.Digit0, Ctrl, () => ZoomModeAsync(ZoomMode.ActualSize), () => rich.ZoomMode == ZoomMode.FitPage),
            ("Ctrl+NumPad0", Key.NumPad0, PhysicalKey.NumPad0, Ctrl, () => ZoomModeAsync(ZoomMode.ActualSize), () => rich.ZoomMode == ZoomMode.FitPage),
            ("Ctrl+D1", Key.D1, PhysicalKey.Digit1, Ctrl, () => ZoomModeAsync(ZoomMode.FitWidth), () => rich.ZoomMode == ZoomMode.ActualSize),
            ("Ctrl+NumPad1", Key.NumPad1, PhysicalKey.NumPad1, Ctrl, () => ZoomModeAsync(ZoomMode.FitWidth), () => rich.ZoomMode == ZoomMode.ActualSize),
            ("Ctrl+D2", Key.D2, PhysicalKey.Digit2, Ctrl, () => ZoomModeAsync(ZoomMode.FitPage), () => rich.ZoomMode == ZoomMode.FitWidth),
            ("Ctrl+NumPad2", Key.NumPad2, PhysicalKey.NumPad2, Ctrl, () => ZoomModeAsync(ZoomMode.FitPage), () => rich.ZoomMode == ZoomMode.FitWidth),
            ("Ctrl+G", Key.G, PhysicalKey.G, Ctrl, ShowRichAsync, () => Focused(window) is TextBox { Name: "PageField" }),
            ("PageDown", Key.PageDown, PhysicalKey.PageDown, None, () => PageAsync(1), () => rich.CurrentPage == 2),
            ("PageUp", Key.PageUp, PhysicalKey.PageUp, None, () => PageAsync(2), () => rich.CurrentPage == 1),
            ("Ctrl+F", Key.F, PhysicalKey.F, Ctrl, ShowRichAsync, () => rich.SidePanel == SidePanelKind.Search && Focused(window) is TextBox { Name: "SearchField" }),
            ("F3", Key.F3, PhysicalKey.F3, None, () => HitAsync(0), () => search.CurrentIndex == 1),
            ("Shift+F3", Key.F3, PhysicalKey.F3, Shift, () => HitAsync(1), () => search.CurrentIndex == 0),
            ("Alt+Left", Key.Left, PhysicalKey.ArrowLeft, Alt,
                async () =>
                {
                    await PageAsync(1);
                    rich.FollowLink(new PageLink(area, new PageTarget(1, null)));
                    Settle(window);
                    Assert.Equal(2, rich.CurrentPage);
                },
                () => rich.CurrentPage == 1 && rich.CanGoForward),
            ("Alt+Right", Key.Right, PhysicalKey.ArrowRight, Alt, ShowRichAsync, () => rich.CurrentPage == 2 && !rich.CanGoForward),
            ("Escape", Key.Escape, PhysicalKey.Escape, None,
                async () =>
                {
                    await ShowRichAsync();
                    rich.FollowLink(new PageLink(area, new UriTarget("https://example.org/docs")));
                    Settle(window);
                    Assert.NotNull(viewModel.PendingLink);
                },
                () => viewModel.PendingLink is null),
            ("Ctrl+W", Key.W, PhysicalKey.W, Ctrl,
                async () =>
                {
                    await ShowRichAsync();
                    tabsBefore = viewModel.Tabs.Count;
                },
                () => viewModel.Tabs.Count == tabsBefore - 1 && viewModel.Tabs.IndexOf(rich) < 0),
        };

        Assert.Equal(24, table.Length);
        foreach (var row in table)
        {
            await row.Arrange();
            Assert.False(row.Effect(), $"{row.Name}: the effect is there before the key");
            Press(window, row.Key, row.Physical, row.Modifiers);
            try
            {
                await WaitUntilAsync(window, row.Effect);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"{row.Name} had no effect");
            }

            // Every former gesture is now a gesture of the catalog.
            var gesture = new KeyGesture(row.Key, ModifiersOf(row.Modifiers));
            Assert.True(viewModel.Commands.ForGesture(gesture) is not null, $"{row.Name} is not in the catalog");
        }
    }

    [AvaloniaFact]
    public async Task AGestureDoesNotFireWhileTheCommandCannotRun()
    {
        var (window, viewModel, view, _) = Show(new CountingPicker());

        // The first stop of a key event is the window (tunnel): handled there means a key binding took the key.
        bool? takenByABinding = null;
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => takenByABinding ??= e.Handled, RoutingStrategies.Tunnel, handledEventsToo: true);
        bool Pressed(Key key, PhysicalKey physical, RawInputModifiers modifiers)
        {
            takenByABinding = null;
            Press(window, key, physical, modifiers);
            return takenByABinding ?? throw new InvalidOperationException("The key did not arrive.");
        }

        // Without a tab, the keys of a document go on.
        window.Focus();
        Assert.False(Pressed(Key.PageDown, PhysicalKey.PageDown, None));
        Assert.False(Pressed(Key.Left, PhysicalKey.ArrowLeft, Alt));
        Assert.False(Pressed(Key.Escape, PhysicalKey.Escape, None));

        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await FocusAsync(window, view);

        // Alt+Left without a history: the key goes on and nothing moves.
        Assert.False(tab.CanGoBack);
        Assert.False(Pressed(Key.Left, PhysicalKey.ArrowLeft, Alt));
        Assert.False(Pressed(Key.Right, PhysicalKey.ArrowRight, Alt));
        Assert.Equal(1, tab.CurrentPage);

        // With a jump behind it, the same key goes back.
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new PageTarget(1, null)));
        Settle(window);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(Pressed(Key.Left, PhysicalKey.ArrowLeft, Alt));
        Assert.Equal(1, tab.CurrentPage);

        // The window has no key binding of its own besides those of the catalog: one for each of its gestures.
        var gestures = viewModel.Commands.All.SelectMany(command => command.Gestures).ToArray();
        Assert.Equal(gestures.Length, window.KeyBindings.Count);
        Assert.All(window.KeyBindings, binding => Assert.Contains(binding.Gesture, gestures));
    }

    [AvaloniaFact]
    public async Task ShortcutsDoNotBreakTextFields()
    {
        var (window, viewModel, view, clipboard) = Show(new CountingPicker());
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8));
        Settle(window);

        Press(window, Key.F, PhysicalKey.F, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "SearchField" });
        var field = (TextBox)Focused(window)!;

        // Letters and digits go into the field, none of them is a shortcut.
        window.KeyTextInput("Pagewing 1 dk");
        Settle(window);
        Assert.Equal("Pagewing 1 dk", field.Text);

        // Ctrl+C copies the text of the field, not the selection of the document.
        Press(window, Key.A, PhysicalKey.A, Ctrl);
        Press(window, Key.C, PhysicalKey.C, Ctrl);
        var copied = string.Empty;
        await WaitUntilAsync(window, () =>
        {
            var read = window.Clipboard!.TryGetTextAsync();
            if (read.IsCompleted)
            {
                copied = read.Result ?? string.Empty;
            }

            return copied.Length > 0;
        });
        Assert.Equal("Pagewing 1 dk", copied);
        Assert.Empty(clipboard.Calls);
        Assert.True(tab.HasTextSelection);

        // Esc belongs to the field (it ends the search), not to the window.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.NotEqual(SidePanelKind.Search, tab.SidePanel);
        Assert.False(tab.Search.IsActive);

        // The page field keeps its own keys too: a number and Enter go to that page.
        Press(window, Key.G, PhysicalKey.G, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PageField" });
        window.KeyTextInput("2");
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        await WaitUntilAsync(window, () => tab.CurrentPage == 2);
    }

    [AvaloniaTheory]
    [InlineData("en", "Open file (Ctrl+O)")]
    [InlineData("de", "Datei öffnen (Strg+O)")]
    public async Task TooltipsNameTheShortcut(string language, string openFile)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, view, _) = Show(new CountingPicker());
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var catalog = viewModel.Commands;

        // Each button (or field) with a shortcut: "<title of its command> (<shortcut>)", the shortcut as the catalog has it.
        var controls = new (string Name, string Id)[]
        {
            ("ChooseFileButton", "file.open"),
            ("OpenButton", "file.open"),
            ("CloseTabButton", "file.closeTab"),
            ("SearchButton", "search.open"),
            ("PreviousPageButton", "nav.previousPage"),
            ("NextPageButton", "nav.nextPage"),
            ("ZoomOutButton", "view.zoomOut"),
            ("ZoomInButton", "view.zoomIn"),
            ("PageField", "nav.goToPage"),
            ("SettingsButton", "settings.show"),
        };

        foreach (var (name, id) in controls)
        {
            var command = catalog.ById(id)!;
            var tip = Assert.IsType<string>(ToolTip.GetTip(Find<Control>(window, name)));
            Assert.Equal($"{command.Title()} ({GestureText.Of(command.Gesture!)})", tip);
        }

        Assert.Equal(openFile, ToolTip.GetTip(Find<Control>(window, "OpenButton")));

        // The button of the command palette on purpose says what it is for ("Search or command"), not the title of its
        // command ("Command palette"); its shortcut is the catalog's all the same, in the tooltip and in its kbd.
        var palette = catalog.ById("palette.show")!;
        Assert.NotEqual(palette.Title(), Strings.PaletteButton);
        var paletteKeys = GestureText.Of(palette.Gesture!);
        Assert.Equal($"{Strings.PaletteButton} ({paletteKeys})", ToolTip.GetTip(Find<Control>(window, "PaletteButton")));
        Assert.Equal(paletteKeys, Find<TextBlock>(window, "PaletteKbdText").Text);

        // The shortcuts shown next to a choice (start screen, zoom menu) are the catalog's as well.
        Assert.Equal(GestureText.Of(catalog.ById("file.open")!.Gesture!), Strings.ChooseFileShortcut);
        Assert.Equal(GestureText.Of(catalog.ById("view.fitWidth")!.Gesture!), Strings.ZoomFitWidthShortcut);
        Assert.Equal(GestureText.Of(catalog.ById("view.fitPage")!.Gesture!), Strings.ZoomFitPageShortcut);
        Assert.Equal(GestureText.Of(catalog.ById("view.actualSize")!.Gesture!), Strings.ZoomActualSizeShortcut);
    }

    [Fact]
    public void GestureTextIsLocalized()
    {
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Strg+Umschalt+1", GestureText.Of(new KeyGesture(Key.D1, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Equal("Strg+Plus", GestureText.Of(new KeyGesture(Key.OemPlus, KeyModifiers.Control)));
        Assert.Equal("Strg+Plus", GestureText.Of(new KeyGesture(Key.Add, KeyModifiers.Control)));
        Assert.Equal("Strg+Minus", GestureText.Of(new KeyGesture(Key.Subtract, KeyModifiers.Control)));
        Assert.Equal("Strg+0", GestureText.Of(new KeyGesture(Key.NumPad0, KeyModifiers.Control)));
        Assert.Equal("Strg+Komma", GestureText.Of(new KeyGesture(Key.OemComma, KeyModifiers.Control)));
        Assert.Equal("Strg+Tab", GestureText.Of(new KeyGesture(Key.Tab, KeyModifiers.Control)));
        Assert.Equal("Strg+K", GestureText.Of(new KeyGesture(Key.K, KeyModifiers.Control)));
        Assert.Equal("Bild auf", GestureText.Of(new KeyGesture(Key.PageUp)));
        Assert.Equal("Bild ab", GestureText.Of(new KeyGesture(Key.PageDown)));
        Assert.Equal("Alt+Links", GestureText.Of(new KeyGesture(Key.Left, KeyModifiers.Alt)));
        Assert.Equal("Alt+Rechts", GestureText.Of(new KeyGesture(Key.Right, KeyModifiers.Alt)));
        Assert.Equal("Umschalt+F3", GestureText.Of(new KeyGesture(Key.F3, KeyModifiers.Shift)));
        Assert.Equal("F1", GestureText.Of(new KeyGesture(Key.F1)));
        Assert.Equal("Esc", GestureText.Of(new KeyGesture(Key.Escape)));

        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Ctrl+Shift+1", GestureText.Of(new KeyGesture(Key.D1, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Equal("Ctrl+Plus", GestureText.Of(new KeyGesture(Key.OemPlus, KeyModifiers.Control)));
        Assert.Equal("Ctrl+Minus", GestureText.Of(new KeyGesture(Key.OemMinus, KeyModifiers.Control)));
        Assert.Equal("Ctrl+Comma", GestureText.Of(new KeyGesture(Key.OemComma, KeyModifiers.Control)));
        Assert.Equal("Page Up", GestureText.Of(new KeyGesture(Key.PageUp)));
        Assert.Equal("Page Down", GestureText.Of(new KeyGesture(Key.PageDown)));
        Assert.Equal("Alt+Left", GestureText.Of(new KeyGesture(Key.Left, KeyModifiers.Alt)));
        Assert.Equal("Shift+F3", GestureText.Of(new KeyGesture(Key.F3, KeyModifiers.Shift)));
        Assert.Equal("Ctrl+Shift+D", GestureText.Of(new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift)));
    }

    [AvaloniaFact]
    public async Task TheNewShortcutsAreBound()
    {
        var (window, viewModel, view, _) = Show(new CountingPicker());
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var catalog = viewModel.Commands;
        Assert.Equal("palette.show", catalog.ForGesture(new KeyGesture(Key.K, KeyModifiers.Control))?.Id);
        Assert.Equal("settings.show", catalog.ForGesture(new KeyGesture(Key.OemComma, KeyModifiers.Control))?.Id);
        Assert.Equal("help.show", catalog.ForGesture(new KeyGesture(Key.F1))?.Id);
        Assert.Equal("panel.pages", catalog.ForGesture(new KeyGesture(Key.D1, KeyModifiers.Control | KeyModifiers.Shift))?.Id);
        Assert.Equal("panel.contents", catalog.ForGesture(new KeyGesture(Key.D2, KeyModifiers.Control | KeyModifiers.Shift))?.Id);
        Assert.Equal("panel.comments", catalog.ForGesture(new KeyGesture(Key.D3, KeyModifiers.Control | KeyModifiers.Shift))?.Id);
        Assert.Equal("view.pageTheme.toggle", catalog.ForGesture(new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift))?.Id);

        await FocusAsync(window, view);
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        viewModel.HideOverlay();
        Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
        Assert.Equal(OverlayKind.Settings, viewModel.Overlay);
        viewModel.HideOverlay();
        Press(window, Key.F1, PhysicalKey.F1, None);
        Assert.Equal(OverlayKind.Help, viewModel.Overlay);
        Assert.True(Find<HelpCard>(window, "HelpCard").IsEffectivelyVisible);
        viewModel.HideOverlay();
        Settle(window);

        // The panels: open, and the same key again closes.
        await FocusAsync(window, view);
        Press(window, Key.D1, PhysicalKey.Digit1, Ctrl | Shift);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        Press(window, Key.D2, PhysicalKey.Digit2, Ctrl | Shift);
        Assert.Equal(SidePanelKind.Contents, tab.SidePanel);
        Press(window, Key.D2, PhysicalKey.Digit2, Ctrl | Shift);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Press(window, Key.D3, PhysicalKey.Digit3, Ctrl | Shift);
        Assert.Equal(SidePanelKind.Comments, tab.SidePanel);
        Press(window, Key.D3, PhysicalKey.Digit3, Ctrl | Shift);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);

        // The page of this tab, dark and light again.
        Assert.Equal(PageTheme.Light, tab.EffectivePageTheme);
        Press(window, Key.D, PhysicalKey.D, Ctrl | Shift);
        Assert.Equal(PageTheme.Dark, tab.EffectivePageTheme);
        Press(window, Key.D, PhysicalKey.D, Ctrl | Shift);
        Assert.Equal(PageTheme.Light, tab.EffectivePageTheme);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public void HelpListsEveryCommandWithAGestureGroupedAndSearchable(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _, _) = Show(new CountingPicker());
        var catalog = viewModel.Commands;

        // Nothing of the card before it is first shown.
        Assert.Null(FindOrNull<HelpCard>(window, "HelpCard"));
        Press(window, Key.F1, PhysicalKey.F1, None);
        var card = Find<HelpCard>(window, "HelpCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal(Strings.HelpTitle, AutomationProperties.GetName(card));
        Assert.Equal(Strings.HelpTitle, Find<TextBlock>(card, "HelpTitle").Text);
        Assert.Equal(Strings.HelpFilter, AutomationProperties.GetName(Find<TextBox>(card, "HelpFilter")));
        Assert.Equal(Strings.HelpClose, AutomationProperties.GetName(Find<Button>(card, "HelpCloseButton")));

        // Every listed command with a gesture, once, in its group, the groups in the order of the catalog.
        var expected = catalog.All.Where(command => command.Listed && command.Gesture is not null).ToArray();
        Assert.DoesNotContain(expected, command => command.Id == "link.cancel");
        var rows = Rows(card);
        Assert.Equal(expected.Select(command => command.Id).Order(StringComparer.Ordinal), rows.Select(row => (string)row.Tag!).Order(StringComparer.Ordinal));
        var groups = card.GetVisualDescendants().OfType<Control>().Where(control => control.Classes.Contains("helpGroup")).ToArray();
        Assert.Equal(CommandCatalog.Groups.Where(group => expected.Any(command => command.Group == group)), groups.Select(group => (string)group.Tag!));
        foreach (var command in expected)
        {
            var row = rows.Single(row => (string)row.Tag! == command.Id);
            var group = row.GetVisualAncestors().OfType<Control>().First(control => control.Classes.Contains("helpGroup"));
            Assert.Equal(command.Group, group.Tag);
            Assert.Equal(CommandCatalog.GroupTitle(command.Group), group.GetVisualDescendants().OfType<TextBlock>().First().Text);
            var gesture = GestureText.Of(command.Gesture!);
            var texts = row.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Assert.Equal(command.Title(), texts[0].Text);

            // The gesture in the mono kbd style: Geist Mono (type.mono) in a 1 px frame.
            var kbd = texts.Single(text => text.Classes.Contains("mono"));
            Assert.Equal(gesture, kbd.Text);
            Assert.Equal(11.5, kbd.FontSize);
            var frame = kbd.GetVisualAncestors().OfType<Border>().First();
            Assert.Equal(new Thickness(1), frame.BorderThickness);
            Assert.Equal($"{command.Title()}, {gesture}", AutomationProperties.GetName(row));
        }

        // Typing filters by title and by shortcut; groups without a match go.
        var filter = Find<TextBox>(card, "HelpFilter");
        filter.Text = "F3";
        Settle(window);
        Assert.Equal(["search.next", "search.previous"], Visible(card).Order(StringComparer.Ordinal));
        Assert.Equal(["search"], groups.Where(group => group.IsEffectivelyVisible).Select(group => (string)group.Tag!));
        Assert.False(Find<TextBlock>(card, "HelpEmpty").IsEffectivelyVisible);

        var zoomIn = catalog.ById("view.zoomIn")!.Title();
        filter.Text = zoomIn.ToUpperInvariant();
        Settle(window);
        Assert.Equal(["view.zoomIn"], Visible(card));

        filter.Text = "qqqq";
        Settle(window);
        Assert.Empty(Visible(card));
        Assert.True(Find<TextBlock>(card, "HelpEmpty").IsEffectivelyVisible);
        Assert.Equal(Strings.HelpEmpty, Find<TextBlock>(card, "HelpEmpty").Text);

        filter.Text = string.Empty;
        Settle(window);
        Assert.Equal(expected.Length, Visible(card).Count);
    }

    [AvaloniaFact]
    public async Task HelpOpensWithF1AndClosesWithEscAndReturnsTheFocus()
    {
        var (window, viewModel, view, _) = Show(new CountingPicker());
        await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Assert.Null(FindOrNull<HelpCard>(window, "HelpCard"));

        // From the document: F1, the focus in the filter, Esc, the focus back in the document.
        await FocusAsync(window, view);
        Press(window, Key.F1, PhysicalKey.F1, None);
        var card = Find<HelpCard>(window, "HelpCard");
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "HelpFilter" });

        // Tab stays on the card: filter, close button, filter.
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.Same(Find<Button>(card, "HelpCloseButton"), Focused(window));
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.Same(Find<TextBox>(card, "HelpFilter"), Focused(window));

        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(card.IsVisible);
        await WaitUntilAsync(window, () => view.IsFocused);

        // From the search field, closed with the button: the focus goes back to the field.
        Press(window, Key.F, PhysicalKey.F, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "SearchField" });
        var field = Focused(window);
        Press(window, Key.F1, PhysicalKey.F1, None);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "HelpFilter" });
        Assert.Same(card, Find<HelpCard>(window, "HelpCard"));
        ClickButton(window, Find<Button>(card, "HelpCloseButton"));
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(card.IsVisible);
        await WaitUntilAsync(window, () => ReferenceEquals(Focused(window), field));

        // The card does not cover the title bar: its buttons stay reachable.
        Press(window, Key.F1, PhysicalKey.F1, None);
        var openButton = Find<Button>(window, "OpenButton");
        var point = openButton.TranslatePoint(new Point(openButton.Bounds.Width / 2, openButton.Bounds.Height / 2), window)!.Value;
        Assert.True(window.InputHitTest(point) is Visual hit && (ReferenceEquals(hit, openButton) || openButton.IsVisualAncestorOf(hit)));
    }

    [AvaloniaFact]
    public void OnlyOneOverlayAtATime()
    {
        var (window, viewModel, _, _) = Show(new CountingPicker());
        Assert.Equal(OverlayKind.None, viewModel.Overlay);

        // The keys and the commands lead to the same overlays; each one replaces the one before.
        Press(window, Key.F1, PhysicalKey.F1, None);
        Assert.Equal(OverlayKind.Help, viewModel.Overlay);
        var card = Find<HelpCard>(window, "HelpCard");
        Assert.True(card.IsVisible);

        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Assert.False(card.IsVisible);

        viewModel.ShowSettingsCommand.Execute(null);
        Assert.Equal(OverlayKind.Settings, viewModel.Overlay);
        viewModel.ShowHelpCommand.Execute(null);
        Settle(window);
        Assert.Equal(OverlayKind.Help, viewModel.Overlay);
        Assert.True(card.IsVisible);
        viewModel.ShowPaletteCommand.Execute(null);
        Settle(window);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Assert.False(card.IsVisible);

        viewModel.HideOverlay();
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        viewModel.ShowOverlay(OverlayKind.Help);
        viewModel.HideOverlayCommand.Execute(null);
        Settle(window);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(card.IsVisible);
        Assert.Single(window.GetVisualDescendants().OfType<HelpCard>());
    }

    [AvaloniaFact]
    public async Task TheKeyOfAnOverlayShowsAndHidesIt()
    {
        var (window, viewModel, view, _) = Show(new CountingPicker());
        await OpenAsync(window, viewModel, view, "text-rich.pdf");

        // F1, Ctrl+K and Ctrl+Comma each a second time close what the first one opened, wherever the focus is.
        foreach (var (key, physical, modifiers, kind) in new[]
        {
            (Key.F1, PhysicalKey.F1, None, OverlayKind.Help),
            (Key.K, PhysicalKey.K, Ctrl, OverlayKind.Palette),
            (Key.OemComma, PhysicalKey.Comma, Ctrl, OverlayKind.Settings),
        })
        {
            await FocusAsync(window, view);
            Press(window, key, physical, modifiers);
            Assert.Equal(kind, viewModel.Overlay);
            Press(window, key, physical, modifiers);
            Assert.Equal(OverlayKind.None, viewModel.Overlay);
            await WaitUntilAsync(window, () => view.IsFocused);

            // Also after the focus left the overlay.
            Press(window, key, physical, modifiers);
            await FocusAsync(window, view);
            if (kind != OverlayKind.Palette)
            {
                Assert.Equal(kind, viewModel.Overlay);
                Press(window, key, physical, modifiers);
            }

            Assert.Equal(OverlayKind.None, viewModel.Overlay);
        }

        // Another key replaces the overlay instead of closing it.
        Press(window, Key.F1, PhysicalKey.F1, None);
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Press(window, Key.F1, PhysicalKey.F1, None);
        Assert.Equal(OverlayKind.Help, viewModel.Overlay);
    }

    [AvaloniaFact]
    public async Task EscClosesAnOverlayAsAFallbackWithoutTakingEscOtherwise()
    {
        var (window, viewModel, view, _) = Show(new CountingPicker());
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8));

        // The help card is open, the focus went back to the document: Esc closes the card, nothing else.
        Press(window, Key.F1, PhysicalKey.F1, None);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "HelpFilter" });
        await FocusAsync(window, view);
        Assert.Equal(OverlayKind.Help, viewModel.Overlay);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.True(tab.HasTextSelection);
        Assert.True(view.IsFocused);

        // A waiting address goes first: Esc drops it, the next Esc closes the card.
        Press(window, Key.F1, PhysicalKey.F1, None);
        await FocusAsync(window, view);
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget("https://example.org/docs")));
        Settle(window);
        Assert.NotNull(viewModel.PendingLink);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Null(viewModel.PendingLink);
        Assert.Equal(OverlayKind.Help, viewModel.Overlay);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);

        // Without an overlay Esc belongs to the document again: it clears the selection, then ends the search.
        var search = tab.Search;
        search.Query = "Pagewing";
        await WaitUntilAsync(window, () => search.State == SearchState.Done && search.Hits.Count == 4);
        await FocusAsync(window, view);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.HasTextSelection);
        Assert.True(search.IsActive);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(search.IsActive);

        // And in the search field it ends the search.
        Press(window, Key.F, PhysicalKey.F, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "SearchField" });
        window.KeyTextInput("Pagewing");
        await WaitUntilAsync(window, () => search.State == SearchState.Done && search.Hits.Count == 4);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(search.IsActive);
        Assert.NotEqual(SidePanelKind.Search, tab.SidePanel);
    }

    [AvaloniaFact]
    public async Task FocusGoesBackToWhereItWasBeforeTheLastOverlay()
    {
        var (window, viewModel, view, _) = Show(new CountingPicker());
        await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await FocusAsync(window, view);

        // Ctrl+K, then Ctrl+F (the focus goes to the search field, the palette closes), then F1 and Esc: the search field.
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Press(window, Key.F, PhysicalKey.F, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "SearchField" });
        var field = Focused(window);
        Press(window, Key.F1, PhysicalKey.F1, None);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "HelpFilter" });
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => ReferenceEquals(Focused(window), field));

        // From the search field: Ctrl+K, F1 replaces the palette, Esc: back to the field, not to the palette's input.
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Press(window, Key.F1, PhysicalKey.F1, None);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "HelpFilter" });
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        await WaitUntilAsync(window, () => ReferenceEquals(Focused(window), field));

        // The settings card replaces the palette and takes the focus; Esc gives it back to the field.
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
        Assert.Equal(OverlayKind.Settings, viewModel.Overlay);
        await WaitUntilAsync(window, () => Focused(window) is ComboBox { Name: "ThemeChoice" });
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => ReferenceEquals(Focused(window), field));
    }

    private const RawInputModifiers None = RawInputModifiers.None;
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;
    private const RawInputModifiers Shift = RawInputModifiers.Shift;
    private const RawInputModifiers Alt = RawInputModifiers.Alt;

    private static Key Normalized(Key key) => key switch
    {
        Key.Add => Key.OemPlus,
        Key.Subtract => Key.OemMinus,
        Key.Decimal => Key.OemPeriod,
        _ => key,
    };

    private static KeyModifiers ModifiersOf(RawInputModifiers modifiers) =>
        (modifiers.HasFlag(Ctrl) ? KeyModifiers.Control : KeyModifiers.None)
        | (modifiers.HasFlag(Shift) ? KeyModifiers.Shift : KeyModifiers.None)
        | (modifiers.HasFlag(Alt) ? KeyModifiers.Alt : KeyModifiers.None);

    private static Control[] Rows(HelpCard card) =>
        card.GetVisualDescendants().OfType<Control>().Where(control => control.Classes.Contains("helpRow")).ToArray();

    private static List<string> Visible(HelpCard card) =>
        Rows(card).Where(row => row.IsEffectivelyVisible).Select(row => (string)row.Tag!).ToList();

    private static object? Focused(Window window) => window.FocusManager!.GetFocusedElement();

    private static async Task FocusAsync(Window window, DocumentView view)
    {
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    private static void ClickButton(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Settle(window);
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

    private MainViewModel NewViewModel(IFilePicker picker)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), picker) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        return viewModel;
    }

    private (Window Window, MainViewModel ViewModel, DocumentView View, RecordingClipboard Clipboard) Show(IFilePicker picker)
    {
        var viewModel = NewViewModel(picker);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = Find<DocumentView>(window, "DocumentView");
        var clipboard = new RecordingClipboard();
        view.Clipboard = clipboard;
        return (window, viewModel, view, clipboard);
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
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    /// <summary>A file picker that counts how often its dialog was asked for and is always cancelled.</summary>
    private sealed class CountingPicker : IFilePicker
    {
        public int Calls { get; private set; }

        public Task<string?> PickPdfAsync()
        {
            Calls++;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PickSavePdfAsync(string suggestedName, string? folder) => Task.FromResult<string?>(null);
    }

    /// <summary>A clipboard of the document view that only records what it is given.</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Calls { get; } = [];

        public Task SetTextAsync(string plainText, string? htmlFragment)
        {
            Calls.Add(plainText);
            return Task.CompletedTask;
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
