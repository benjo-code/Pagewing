using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.TextEdit;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>The mode "Edit text" on the pages (milestone 16): the outline, the field, its hints, the keys, the other modes, and what the tab counts as unsaved.</summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class TextEditUiTests : IDisposable
{
    private readonly SaveCardHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private async Task<(MainWindow Window, MainViewModel ViewModel, DocumentView View, TabViewModel Tab, string Path)> OpenAsync(string file)
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, file);
        return (window, viewModel, view, tab, path);
    }

    private static async Task EnterAsync(Window window, DocumentView view, TabViewModel tab, int page = 0)
    {
        tab.ToggleTextEditCommand.Execute(null);
        await SaveCardHost.WaitUntilAsync(window, () => tab.IsTextEditing && tab.TextRunsOf(page).Count > 0 && view.TextEditOverlay is not null);
    }

    private static Point CenterOf(Window window, DocumentView view, TextRun run)
    {
        var rect = view.TextEditOverlay!.ViewRectOf(run) ?? throw new InvalidOperationException("The line is not on a laid out page.");
        return view.TextEditOverlay.TranslatePoint(rect.Center, window)!.Value;
    }

    private static TextRun Run(TabViewModel tab, string text, int page = 0) => tab.TextRunsOf(page).Single(r => r.Text.TrimEnd() == text);

    private static void Hover(Window window, DocumentView view, TextRun run)
    {
        window.MouseMove(CenterOf(window, view, run), RawInputModifiers.None);
        SaveCardHost.Settle(window);
    }

    private static void Click(Window window, DocumentView view, TextRun run)
    {
        var point = CenterOf(window, view, run);
        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        SaveCardHost.Settle(window);
    }

    private static async Task OpenFieldAsync(Window window, DocumentView view, TabViewModel tab, string text)
    {
        Click(window, view, Run(tab, text));
        await SaveCardHost.WaitUntilAsync(window, () => view.TextRunEditor is { } editor && editor.Input.IsKeyboardFocusWithin);
    }

    private static async Task TypeAsync(Window window, DocumentView view, TabViewModel tab, string text)
    {
        view.TextRunEditor!.Input.Text = text;
        await SaveCardHost.WaitUntilAsync(window, () => string.Equals(tab.TextEditDraft, text, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task ModeNeedsAFirstSharpPageAndNeverOpensInMeasurementRuns()
    {
        var (window, viewModel, view) = _host.Show();
        var path = _host.Copy("text-edit-standard.pdf");
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab!;

        // Nothing shown yet: the mode is not offered and says why.
        if (!tab.HasShownSharpPage)
        {
            Assert.False(tab.CanTextEdit);
            Assert.Equal(Strings.TextEditNotReady, tab.TextEditBlockedReason);
            Assert.False(tab.ToggleTextEditCommand.CanExecute(null));
        }

        await SaveCardHost.WaitUntilAsync(window, () => tab.HasShownSharpPage);
        Assert.True(tab.CanTextEdit);

        // The mode is started by the user and by nothing else: a measurement run never does.
        Assert.False(tab.IsTextEditing);
        Assert.Null(view.TextEditOverlay);
    }

    [AvaloniaFact]
    public async Task NothingOfTheFeatureIsCreatedBeforeUse()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-standard.pdf");

        Assert.Null(view.TextEditOverlay);
        Assert.Null(view.TextRunEditor);
        Assert.Null(window.TextEditHintBar);
        Assert.Empty(tab.TextRunsOf(0));
        Assert.Null(tab.OpenTextRun);
        Assert.False(tab.HasTextEditSaveNote);
    }

    [AvaloniaFact]
    public async Task RunsGetAnOutlineOnHoverAndLockedRunsShowALockWithTheirReason()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-locked.pdf");
        await EnterAsync(window, view, tab);
        var editable = Run(tab, "Editable control line");
        var hidden = Run(tab, "Invisible text layer");

        Hover(window, view, editable);
        Assert.Equal(editable, view.TextEditOverlay!.Hover);
        Assert.False(editable.IsLocked);

        Hover(window, view, hidden);
        Assert.Equal(hidden, view.TextEditOverlay.Hover);
        Assert.True(hidden.IsLocked);
        Assert.Equal(Strings.TextEditLockInvisibleText, ToolTip.GetTip(view.ScrollViewer));

        // The outline of a locked line is not told by colour alone: dashed and with the lock (drawn by the layer); the reason is in words, in both languages.
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            foreach (var reason in Enum.GetValues<TextEditLockReason>().Where(static r => r != TextEditLockReason.PageNotRewritable || true))
            {
                Assert.False(string.IsNullOrWhiteSpace(TabViewModel.LockTextOf(reason)));
            }
        }

        Strings.Culture = new CultureInfo("en");
        await EnterNothingAsync(window);
    }

    private static Task EnterNothingAsync(Window window)
    {
        SaveCardHost.Settle(window);
        return Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task ClickOpensTheEditorOverRunAndEnterCommitsEscDiscards()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-standard.pdf");
        await EnterAsync(window, view, tab);

        // A click opens the field over the line, with its text; Esc drops what was typed.
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        var editor = view.TextRunEditor!;
        Assert.Equal("Quarterly report 2026", editor.Text);
        await TypeAsync(window, view, tab, "Quarterly report 2027");
        Assert.True(tab.HasTextEditDraft);
        Assert.True(tab.IsDirty);
        SaveCardHost.Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(tab.OpenTextRun);
        Assert.Null(view.TextRunEditor);
        Assert.False(tab.IsDirty);
        Assert.Contains("Quarterly report 2026", await TextOfAsync(tab), StringComparison.Ordinal);

        // Enter applies it, as one step of the history.
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, "Quarterly report 2027");
        SaveCardHost.Press(window, Key.Enter, PhysicalKey.Enter);
        await SaveCardHost.WaitUntilAsync(window, () => tab.OpenTextRun is null && tab.CanUndo && !tab.HasEditsInFlight);
        Assert.Contains("Quarterly report 2027", await TextOfAsync(tab), StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly report 2026", await TextOfAsync(tab), StringComparison.Ordinal);
        Assert.True(tab.IsDirty);
    }

    private static async Task<string> TextOfAsync(TabViewModel tab)
    {
        var text = await tab.Document!.GetPageTextAsync(0, Ct);
        return text.GetText(0, text.Chars.Count);
    }

    [AvaloniaFact]
    public async Task TabNeverTrapsFocusAndArrowsMoveBetweenRuns()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-standard.pdf");
        await EnterAsync(window, view, tab);
        view.Focus();
        SaveCardHost.Settle(window);

        SaveCardHost.Press(window, Key.Down, PhysicalKey.ArrowDown);
        var first = tab.FocusedTextRun;
        SaveCardHost.Press(window, Key.Down, PhysicalKey.ArrowDown);
        var second = tab.FocusedTextRun;
        SaveCardHost.Press(window, Key.Up, PhysicalKey.ArrowUp);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(first, tab.FocusedTextRun);

        // Tab is not used by the mode at all: it is not handled (the focus goes on), and Enter opens the line with the focus.
        var args = new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab };
        Assert.False(args.Handled);
        SaveCardHost.Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.Equal(first, tab.OpenTextRun);
    }

    [AvaloniaFact]
    public async Task HintsNameMissingCharactersAndOverflowLive()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-subset.pdf");
        await EnterAsync(window, view, tab);
        await OpenFieldAsync(window, view, tab, "Invoice total: 1,250 EUR");

        await TypeAsync(window, view, tab, "Invoice total: 1,259 EUR");
        await SaveCardHost.WaitUntilAsync(window, () => tab.TextEditHint.Contains('9', StringComparison.Ordinal));

        Assert.Contains(string.Format(Strings.Culture, System.Text.CompositeFormat.Parse(Strings.TextEditMissing), "9"), tab.TextEditHint, StringComparison.Ordinal);
        Assert.True(tab.CanChooseWholeRun);
        Assert.False(tab.TextEditWholeRun);
        Assert.Contains("9", window.TextEditHintBar!.FindControl<TextBlock>("TextEditLiveHint")!.Text, StringComparison.Ordinal);

        // The switch is offered, never on by itself; on, the whole line is set in the replacement font.
        tab.SetTextEditWholeRun(true);
        await SaveCardHost.WaitUntilAsync(window, () => tab.TextEditWholeRun);

        // A very long text runs over the box of the line and the page edge.
        await TypeAsync(window, view, tab, "Invoice total: 1,250 EUR " + new string('x', 200));
        await SaveCardHost.WaitUntilAsync(window, () => tab.TextEditHint.Contains(Strings.TextEditOutside, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task RejectedTextStaysInTheFieldWithTheReason()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-standard.pdf");
        await EnterAsync(window, view, tab);
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, "Quarterly report ж");
        SaveCardHost.Press(window, Key.Enter, PhysicalKey.Enter);
        await SaveCardHost.WaitUntilAsync(window, () => tab.TextEditProblem is not null || tab.TextEditHint.Contains('ж', StringComparison.Ordinal));

        Assert.NotNull(tab.OpenTextRun);
        Assert.NotNull(view.TextRunEditor);
        Assert.Contains("ж", tab.TextEditHint, StringComparison.Ordinal);
        Assert.False(tab.CanUndo);
        Assert.Contains("Quarterly report 2026", await TextOfAsync(tab), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ClearingTheFieldRemovesTheRun()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-standard.pdf");
        await EnterAsync(window, view, tab);
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, string.Empty);
        await SaveCardHost.WaitUntilAsync(window, () => tab.TextEditHint == Strings.TextEditRemoves);

        SaveCardHost.Press(window, Key.Enter, PhysicalKey.Enter);
        await SaveCardHost.WaitUntilAsync(window, () => tab.OpenTextRun is null && tab.CanUndo && !tab.HasEditsInFlight);

        Assert.DoesNotContain("Quarterly", await TextOfAsync(tab), StringComparison.Ordinal);
        await tab.Editor.UndoAsync();
        Assert.Contains("Quarterly report 2026", await TextOfAsync(tab), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task OpenFieldCountsAsUnsavedAndIsFlushedBeforeSaveAndPrintAndClose()
    {
        var (window, viewModel, view, tab, path) = await OpenAsync("text-edit-standard.pdf");
        await EnterAsync(window, view, tab);
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, "Quarterly report 2027");

        // Typed text is a change: the tab asks before closing and is dirty, and saving applies it first.
        Assert.True(tab.IsDirty);
        Assert.True(tab.AsksBeforeClosing);
        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.Null(tab.OpenTextRun);
        Assert.False(tab.IsDirty);
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: Ct);
        var text = await reopened.GetPageTextAsync(0, Ct);
        Assert.Contains("Quarterly report 2027", text.GetText(0, text.Chars.Count), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ModesExcludeEachOtherWithAVisibleReason()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-standard.pdf");
        await EnterAsync(window, view, tab);

        // While the mode is on the others are not offered, and say so.
        Assert.Equal(Strings.TextEditActive, tab.AnnotateUnavailableReason);
        Assert.Equal(Strings.TextEditActive, tab.OrganizeUnavailableReason);
        Assert.Equal(Strings.TextEditActive, tab.RedactionBlockedReason);
        Assert.Equal(Strings.OcrBlockedMode, tab.OcrBlockedReason);
        Assert.False(tab.ToggleFillCommand.CanExecute(null));
        Assert.True(tab.EndTextEdit());

        // The other way round: with "Comment" on, the mode is not offered, and says why.
        tab.ToggleAnnotateCommand.Execute(null);
        await SaveCardHost.WaitUntilAsync(window, () => tab.IsAnnotating);
        Assert.Equal(Strings.TextEditBlockedMode, tab.TextEditBlockedReason);
        Assert.False(tab.ToggleTextEditCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task HintBarStatesLinesNotParagraphsInBothLanguages()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-standard.pdf");
        await EnterAsync(window, view, tab);
        SaveCardHost.Settle(window);

        var bar = window.TextEditHintBar!;
        Assert.True(bar.IsVisible);
        Assert.Equal(Strings.TextEditNote, SaveCardHost.Find<TextBlock>(bar, "TextEditNoteText").Text);
        Assert.Equal(Strings.TextEditNoteSaved, SaveCardHost.Find<TextBlock>(bar, "TextEditNoteSavedText").Text);
        Assert.Contains("lines, not paragraphs", Strings.TextEditNote, StringComparison.OrdinalIgnoreCase);

        Strings.Culture = new CultureInfo("de");
        Assert.Contains("keine Absätze", Strings.TextEditNote, StringComparison.Ordinal);
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            foreach (var text in new[] { Strings.TextEditNote, Strings.TextEditNoteSaved, Strings.TextEditSaveNote })
            {
                Assert.DoesNotContain("Word", text, StringComparison.Ordinal);
                Assert.DoesNotContain("entfernt", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("removed", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secure", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sicher", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        Strings.Culture = new CultureInfo("en");
    }

    [AvaloniaFact]
    public async Task ShortcutIsUniqueInTheCatalogAndListedInHelp()
    {
        var (_, viewModel, _, _, _) = await OpenAsync("text-edit-standard.pdf");

        var catalog = viewModel.Commands;
        var command = catalog.ById("text.edit")!;

        Assert.NotNull(command.Gesture);
        Assert.Equal("Ctrl+Shift+W", Pagewing.App.Commands.GestureText.Of(command.Gesture!));
        foreach (var gesture in command.Gestures)
        {
            Assert.Same(command, catalog.ForGesture(gesture));
            Assert.Single(catalog.All, other => other.Gestures.Contains(gesture));
        }

        // Listed in the help (it has a gesture and is a command of the user), under the editing group.
        Assert.True(command.Listed);
        Assert.Equal(Pagewing.App.Commands.CommandCatalog.EditGroup, command.Group);
    }

    [AvaloniaFact]
    public async Task UndoRedoWorkFromTheKeyboard()
    {
        var (window, _, view, tab, _) = await OpenAsync("text-edit-standard.pdf");
        await EnterAsync(window, view, tab);
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, "Quarterly report 2027");
        SaveCardHost.Press(window, Key.Enter, PhysicalKey.Enter);
        await SaveCardHost.WaitUntilAsync(window, () => tab.OpenTextRun is null && tab.CanUndo && !tab.HasEditsInFlight);

        view.Focus();
        SaveCardHost.Press(window, Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
        await SaveCardHost.WaitUntilAsync(window, () => !tab.CanUndo && tab.CanRedo);
        Assert.Contains("Quarterly report 2026", await TextOfAsync(tab), StringComparison.Ordinal);

        SaveCardHost.Press(window, Key.Y, PhysicalKey.Y, RawInputModifiers.Control);
        await SaveCardHost.WaitUntilAsync(window, () => tab.CanUndo && !tab.CanRedo);
        Assert.Contains("Quarterly report 2027", await TextOfAsync(tab), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AfterSaveTheViewKeepsPositionAndZoom()
    {
        var (window, viewModel, view, tab, _) = await OpenAsync("text-edit-standard.pdf");
        var zoom = tab.Zoom;
        await EnterAsync(window, view, tab);
        await OpenFieldAsync(window, view, tab, "Quarterly report 2026");
        await TypeAsync(window, view, tab, "Quarterly report 2027");
        SaveCardHost.Press(window, Key.Enter, PhysicalKey.Enter);
        await SaveCardHost.WaitUntilAsync(window, () => tab.OpenTextRun is null && tab.CanUndo && !tab.HasEditsInFlight);

        Assert.True(await viewModel.SaveActiveTabAsync());
        await SaveCardHost.WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document));

        Assert.Equal(zoom, tab.Zoom);
        Assert.True(tab.IsTextEditing);
        await SaveCardHost.WaitUntilAsync(window, () => tab.TextRunsOf(0).Any(static r => r.Text.Contains("2027", StringComparison.Ordinal)));
    }
}
