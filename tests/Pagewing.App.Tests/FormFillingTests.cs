using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Filling in a form on the page (milestone 11, task 12): the form layer (outline, star, lock, focus ring, names for screen
/// readers), the input over a field (where it lies, also on rotated pages; typing, leaving, Enter, Esc, a refused value),
/// check boxes and radio buttons, combo boxes and lists, the keyboard alone (Tab never traps the focus), locked fields, and
/// the highlight wiring. Every file is a copy in a temp folder; nothing is saved; settings are in memory.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class FormFillingTests : IDisposable
{
    private const RawInputModifiers None = RawInputModifiers.None;
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;
    private const RawInputModifiers Shift = RawInputModifiers.Shift;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-formfill-" + Guid.NewGuid().ToString("N"))).FullName;

    public FormFillingTests() => Strings.Culture = new CultureInfo("en");

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
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    [AvaloniaFact]
    public async Task ClickOnATextFieldOpensTheEditorOnTheField()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");

        // Nothing of the layer or the input before the mode.
        Assert.Null(view.FormOverlay);
        Assert.Null(view.FormFieldEditor);
        await EnterAsync(window, tab, view);
        Assert.Null(view.FormFieldEditor);

        var name = Field(tab, "Name");
        ClickField(window, view, name);
        var editor = view.FormFieldEditor ?? throw new InvalidOperationException("No input opened.");
        Assert.Equal(name.Key, editor.Field!.Key);
        Assert.Equal("Alt", editor.TextField.Text);
        Assert.True(editor.TextField.IsKeyboardFocusWithin);
        Assert.Equal(name.Key, tab.FocusedField);
        AssertInputOnField(view, editor, name);

        // A rotated page with a CropBox offset inside its MediaBox (page 1, /Rotate 90) and a page turned the other way
        // (page 2, /Rotate 270): the field lies where the page shows it, and so does its input.
        var (rotatedWindow, _, rotated, rotatedView) = await ShowAsync("forms-rotated.pdf");
        await EnterAsync(rotatedWindow, rotated, rotatedView);
        var text1 = Field(rotated, "Text1");
        AssertRectNear(new PageRect(210, 60, 232, 240), text1.Bounds, 0.5);
        ClickField(rotatedWindow, rotatedView, text1);
        var first = rotatedView.FormFieldEditor ?? throw new InvalidOperationException("No input opened on page 1.");
        Assert.Equal("Eins", first.TextField.Text);
        AssertInputOnField(rotatedView, first, text1);

        await rotated.ReadFieldsAsync(1);
        var text2 = Field(rotated, "Text2", page: 1);
        AssertRectNear(new PageRect(58, 50, 80, 230), text2.Bounds, 0.5);
        rotated.RevealArea(1, text2.Bounds, fromPanel: false);
        Settle(rotatedWindow);
        ClickField(rotatedWindow, rotatedView, text2);
        var second = rotatedView.FormFieldEditor ?? throw new InvalidOperationException("No input opened on page 2.");
        Assert.Equal(text2.Key, second.Field!.Key);
        Assert.Equal("Zwei", second.TextField.Text);
        AssertInputOnField(rotatedView, second, text2);
    }

    [AvaloniaFact]
    public async Task LeavingWithoutChangeCreatesNoStep()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);

        // Entered and left by the pointer, by Tab and by a press beside the fields; the prefilled "Alt" stays as it is.
        ClickField(window, view, Field(tab, "Name"));
        ClickField(window, view, Field(tab, "Bemerkung"));
        ClickField(window, view, Field(tab, "Kurz"));
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Click(window, ScreenPoint(view, window, 0, new Point(395, 5)));
        Settle(window);

        Assert.Null(tab.FocusedField);
        Assert.Null(view.FormFieldEditor);
        Assert.False(tab.HasFormDraft);
        Assert.False(tab.IsDirty);
        Assert.False(tab.CanUndo);
        Assert.False(tab.HasEditor);
        Assert.Equal("Alt", (await ValueAsync(tab, "Name")).Text);
    }

    [AvaloniaFact]
    public async Task TypingAndLeavingCommitsOneStep()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);

        ClickField(window, view, Field(tab, "Kurz"));
        window.KeyTextInput("abc");
        Settle(window);
        Assert.True(tab.HasFormDraft);
        Assert.Equal("abc", tab.CurrentDraft!.Text);

        // A press beside the fields leaves the field: its value is applied, one step.
        Click(window, ScreenPoint(view, window, 0, new Point(395, 5)));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.CanUndo);
        Assert.Equal("abc", (await ValueAsync(tab, "Kurz")).Text);
        Assert.Null(view.FormFieldEditor);
        Assert.False(tab.HasFormDraft);
        Assert.True(tab.IsDirty);

        // One undo takes it back entirely.
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && !tab.CanUndo);
        Assert.Equal(string.Empty, (await ValueAsync(tab, "Kurz")).Text);
    }

    [AvaloniaFact]
    public async Task RejectedCharacterKeepsTheTextAndShowsTheReason()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);
        var name = Field(tab, "Name");

        ClickField(window, view, name);
        window.KeyTextInput("漢");
        Settle(window);

        // Tab applies: the field cannot show the character, so the whole text stays in the field with the reason.
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        var editor = view.FormFieldEditor ?? throw new InvalidOperationException("The input closed.");
        Assert.Equal(name.Key, editor.Field!.Key);
        Assert.Equal("Alt漢", editor.TextField.Text);
        Assert.True(editor.HasProblem);
        Assert.Contains("漢", editor.Problem, StringComparison.Ordinal);
        Assert.Equal(name.Key, tab.FocusedField);
        Assert.Equal("Alt漢", tab.CurrentDraft!.Text);
        Assert.NotNull(tab.CurrentDraft.Reason);
        Assert.True(tab.HasFormDraft);
        Assert.False(tab.CanUndo);

        // A press beside the fields does not leave it either; nothing is shortened or lost.
        Click(window, ScreenPoint(view, window, 0, new Point(395, 5)));
        Assert.Equal(name.Key, tab.FocusedField);
        Assert.Equal("Alt漢", view.FormFieldEditor!.TextField.Text);
        Assert.Equal("Alt", (await ValueAsync(tab, "Name")).Text);
    }

    [AvaloniaFact]
    public async Task ClickTogglesCheckBoxAndSelectsRadio()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);

        var agree = Field(tab, "Agb");
        ClickField(window, view, agree);
        Assert.Null(view.FormFieldEditor);
        Assert.Equal(agree.Key, tab.FocusedField);
        Assert.Equal(agree.Key, view.FormOverlay!.Focused);
        await WaitForValueAsync(window, tab, "Agb", state => state.Checked == true);

        ClickField(window, view, Field(tab, "Agb"));
        await WaitForValueAsync(window, tab, "Agb", state => state.Checked == false);

        // A press that is released outside the box switches nothing.
        var center = Center(view, window, Field(tab, "Agb"));
        window.MouseDown(center, MouseButton.Left);
        Settle(window);
        window.MouseMove(center + new Point(0, 120), RawInputModifiers.LeftMouseButton);
        window.MouseUp(center + new Point(0, 120), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasEditsInFlight);
        Assert.False((await ValueAsync(tab, "Agb")).Checked);

        var bar = tab.FieldsOfPage(0).First(field => field.Key.FieldName == "Zahlung" && field.ExportValue == "Bar");
        ClickField(window, view, bar);
        await WaitForValueAsync(window, tab, "Zahlung", state => state.Text == "Bar");
        var card = tab.FieldsOfPage(0).First(field => field.Key.FieldName == "Zahlung" && field.ExportValue == "Karte");
        ClickField(window, view, card);
        await WaitForValueAsync(window, tab, "Zahlung", state => state.Text == "Karte");

        // The chosen button stays chosen: a click on it makes no step.
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        var steps = tab.EditVersion;
        ClickField(window, view, tab.FieldsOfPage(0).First(field => field.Key.FieldName == "Zahlung" && field.ExportValue == "Karte"));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Equal(steps, tab.EditVersion);
        Assert.Equal("Karte", (await ValueAsync(tab, "Zahlung")).Text);
    }

    [AvaloniaFact]
    public async Task ComboAndListCommitTheirSelection()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);

        ClickField(window, view, Field(tab, "Farbe"));
        var editor = view.FormFieldEditor ?? throw new InvalidOperationException("No input for the combo box.");
        Assert.True(editor.ChoiceField.IsVisible);
        Assert.False(editor.ChoiceField.IsEditable);
        Assert.Equal(["Rot", "Gruen", "Blau"], editor.ChoiceField.Items.Cast<string>());
        editor.ChoiceField.SelectedIndex = 1;
        await WaitForValueAsync(window, tab, "Farbe", state => state.SelectedOptions.SequenceEqual([1]));

        ClickField(window, view, Field(tab, "Liste"));
        editor = view.FormFieldEditor!;
        Assert.True(editor.ListField.IsVisible);
        Assert.Equal(SelectionMode.Single, editor.ListField.SelectionMode);
        editor.ListField.SelectedIndex = 2;
        await WaitForValueAsync(window, tab, "Liste", state => state.SelectedOptions.SequenceEqual([2]));

        ClickField(window, view, Field(tab, "Mehrfach"));
        editor = view.FormFieldEditor!;
        Assert.True(editor.ListField.SelectionMode.HasFlag(SelectionMode.Multiple));
        editor.ListField.Selection.Select(0);
        editor.ListField.Selection.Select(3);
        await WaitForValueAsync(window, tab, "Mehrfach", state => state.SelectedOptions.SequenceEqual([0, 3]));

        // An editable combo box takes typed text, applied with Enter like a text field.
        ClickField(window, view, Field(tab, "Frei"));
        editor = view.FormFieldEditor!;
        Assert.True(editor.ChoiceField.IsEditable);
        editor.ChoiceField.Text = "Vier";
        Settle(window);
        Assert.Equal("Vier", tab.CurrentDraft!.Text);
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        await WaitForValueAsync(window, tab, "Frei", state => state.Text == "Vier");
        Assert.Equal("Liste", tab.FocusedField!.Value.FieldName);
    }

    [AvaloniaFact]
    public async Task FillingAFormWithTheKeyboardOnly()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);

        // The mode by its key; then Tab walks the fields in the order of the page, read-only "Gesperrt" left out.
        Press(window, Key.F, PhysicalKey.F, Ctrl | Shift);
        await WaitUntilAsync(window, () => tab.IsFilling && tab.FieldsOfPage(0).Count > 0);
        Assert.True(view.IsKeyboardFocusWithin);

        Tab(window);
        AssertFocusIn(view, "Name");
        Assert.True(view.FormFieldEditor!.TextField.IsKeyboardFocusWithin);
        window.KeyTextInput("Ada");
        Settle(window);

        foreach (var next in (string[])["Bemerkung", "Kamm", "Kurz", "Pflicht"])
        {
            Tab(window);
            AssertFocusIn(view, next);
        }

        window.KeyTextInput("x");
        Tab(window);
        AssertFocusIn(view, "Passwort");

        // A check box: no input, the view keeps the keys and Space switches it.
        Tab(window);
        Assert.Equal("Agb", tab.FocusedField!.Value.FieldName);
        Assert.Null(view.FormFieldEditor);
        Assert.True(view.FormOverlay!.ElementOf(tab.FocusedField!.Value)!.IsFocused);
        Press(window, Key.Space, PhysicalKey.Space, None);
        Tab(window);
        Assert.Equal("Zustimmung", tab.FocusedField!.Value.FieldName);

        // A radio button: Space chooses it.
        Tab(window);
        Assert.Equal("Zahlung", tab.FocusedField!.Value.FieldName);
        Press(window, Key.Space, PhysicalKey.Space, None);
        Tab(window);
        Tab(window);
        Tab(window);
        Assert.Equal("Stufe", tab.FocusedField!.Value.FieldName);

        // The combo box: the arrow chooses the next option.
        Tab(window);
        AssertFocusIn(view, "Farbe");
        Assert.True(view.FormFieldEditor!.ChoiceField.IsKeyboardFocusWithin);
        Press(window, Key.Down, PhysicalKey.ArrowDown, None);

        // Enter applies and goes on.
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        AssertFocusIn(view, "Frei");

        // A list takes the keys without choosing an option.
        Tab(window);
        AssertFocusIn(view, "Liste");

        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Empty((await ValueAsync(tab, "Liste")).SelectedOptions);
        Assert.Equal("Ada", (await ValueAsync(tab, "Name")).Text);
        Assert.Equal("x", (await ValueAsync(tab, "Pflicht")).Text);
        Assert.True((await ValueAsync(tab, "Agb")).Checked);
        Assert.Equal("Bar", (await ValueAsync(tab, "Zahlung")).Text);
        Assert.Equal([0], (await ValueAsync(tab, "Farbe")).SelectedOptions);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task TabLeavesTheViewAfterTheLastField()
    {
        // forms-signed.pdf: "Name" is the only field Pagewing can change (two signature fields and a button are locked).
        var (window, _, tab, view) = await ShowAsync("forms-signed.pdf");
        await EnterAsync(window, tab, view);
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);

        Tab(window);
        AssertFocusIn(view, "Name");
        window.KeyTextInput("Bo");
        Settle(window);

        // After the last field the key is not taken: the focus leaves the document (WCAG 2.1.2) and the value is applied.
        Tab(window);
        await WaitUntilAsync(window, () => !view.IsKeyboardFocusWithin && tab.FocusedField is null);
        Assert.Null(view.FormFieldEditor);
        await WaitForValueAsync(window, tab, "Name", state => state.Text == "Bo");

        // Before the first field the same, backwards.
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab, Shift);
        AssertFocusIn(view, "Name");
        Press(window, Key.Tab, PhysicalKey.Tab, Shift);
        await WaitUntilAsync(window, () => !view.IsKeyboardFocusWithin && tab.FocusedField is null);
    }

    [AvaloniaFact]
    public async Task EscDiscardsTheDraft()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);
        var name = Field(tab, "Name");

        ClickField(window, view, name);
        window.KeyTextInput("X");
        Settle(window);
        Assert.Equal("AltX", tab.CurrentDraft!.Text);
        Assert.True(tab.IsDirty);

        // Esc drops the open value: the field shows its value again and stays open.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Null(tab.CurrentDraft);
        Assert.False(tab.IsDirty);
        Assert.Equal("Alt", view.FormFieldEditor!.TextField.Text);
        Assert.Equal(name.Key, tab.FocusedField);

        // The next Esc leaves the field (the keys go to the document), the one after ends the mode.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Null(view.FormFieldEditor);
        Assert.Null(tab.FocusedField);
        Assert.True(tab.IsFilling);
        Assert.True(view.IsKeyboardFocusWithin);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.IsFilling);
        Assert.False(tab.CanUndo);
        Assert.Equal("Alt", (await ValueAsync(tab, "Name")).Text);
    }

    [AvaloniaFact]
    public async Task ReadOnlyFieldShowsALockAndOpensNoEditor()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);
        var locked = Field(tab, "Gesperrt");

        // The lock is drawn and named (never colour alone); the pointer over it shows why as tooltip.
        Assert.True(FormOverlay.HasLock(locked));
        Assert.False(FormOverlay.HasLock(Field(tab, "Name")));
        var element = view.FormOverlay!.ElementOf(locked.Key)!;
        Assert.Contains(Strings.FormLockedReadOnly.TrimEnd('.'), AutomationProperties.GetHelpText(element), StringComparison.Ordinal);
        window.MouseMove(Center(view, window, locked), None);
        Settle(window);
        Assert.Equal(Strings.FormLockedReadOnly, ToolTip.GetTip(view.ScrollViewer));

        ClickField(window, view, locked);
        Assert.Null(view.FormFieldEditor);
        Assert.Null(tab.FocusedField);
        Assert.Equal(Strings.FormLockedReadOnly, tab.FormNotice);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);

        // A required field has its star.
        Assert.True(FormOverlay.HasStar(Field(tab, "Pflicht")));
        Assert.False(FormOverlay.HasStar(Field(tab, "Kurz")));
    }

    [AvaloniaFact]
    public async Task ButtonFieldDoesNothingButShowTheHint()
    {
        var (window, _, tab, view) = await ShowAsync("forms-signed.pdf");
        await EnterAsync(window, tab, view);

        ClickField(window, view, Field(tab, "Senden"));
        Settle(window);
        Assert.Equal(Strings.FormLockedButton, tab.FormNotice);
        Assert.Null(view.FormFieldEditor);
        Assert.Null(tab.FocusedField);
        Assert.False(tab.HasEditsInFlight);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
        Assert.True(FormOverlay.HasLock(Field(tab, "Senden")));
    }

    [AvaloniaFact]
    public async Task SignatureFieldShowsTheNeutralHint()
    {
        var (window, _, tab, view) = await ShowAsync("forms-signed.pdf");
        await EnterAsync(window, tab, view);

        foreach (var name in (string[])["Signature1", "Signature2"])
        {
            ClickField(window, view, Field(tab, name));
            Assert.Equal(Strings.FormLockedSignature, tab.FormNotice);
            Assert.Null(view.FormFieldEditor);
            Assert.Null(tab.FocusedField);
        }

        // Neutral: Pagewing does not support digital signatures; nothing is called valid.
        Assert.Contains("does not support digital signatures", tab.FormNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("valid", tab.FormNotice, StringComparison.OrdinalIgnoreCase);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task ScrollingAwayWhileTheEditorIsOpenCommitsOrKeepsTheDraft()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);

        // A value the field takes is applied when the field scrolls out of view.
        ClickField(window, view, Field(tab, "Name"));
        window.KeyTextInput("Q");
        Settle(window);
        view.ScrollViewer.Offset = new Vector(0, 100_000);
        Settle(window);
        Assert.Null(view.FormFieldEditor);
        await WaitForValueAsync(window, tab, "Name", state => state.Text == "AltQ");
        Assert.False(tab.HasFormDraft);

        // One it cannot take stays with the tab, with its reason, and comes back with the field.
        view.ScrollViewer.Offset = new Vector(0, 0);
        Settle(window);
        var remark = Field(tab, "Bemerkung");
        ClickField(window, view, remark);
        window.KeyTextInput("漢");
        Settle(window);
        view.ScrollViewer.Offset = new Vector(0, 100_000);
        Settle(window);
        Assert.Null(view.FormFieldEditor);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Equal("漢", tab.CurrentDraft!.Text);
        Assert.NotNull(tab.CurrentDraft.Reason);
        Assert.True(tab.HasFormDraft);

        view.ScrollViewer.Offset = new Vector(0, 0);
        Settle(window);
        var editor = view.FormFieldEditor ?? throw new InvalidOperationException("The input did not come back.");
        Assert.Equal(remark.Key, editor.Field!.Key);
        Assert.Equal("漢", editor.TextField.Text);
        Assert.True(editor.HasProblem);
    }

    [AvaloniaFact]
    public async Task AutomationNamesComeFromTheAlternateName()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");

        // forms-basic.pdf has no /TU: the read is given one for "Name", as a document with tooltip names would have.
        tab.FormFieldReader = async (document, page, cancellationToken) =>
            [.. (await document.GetFormFieldsAsync(page, cancellationToken)).Select(field => field.Key.FieldName == "Name" ? field with { AlternateName = "Full name" } : field)];
        await EnterAsync(window, tab, view);
        var overlay = view.FormOverlay!;

        var name = Peer(overlay, Field(tab, "Name"));
        Assert.Equal("Full name", name.GetName());
        Assert.Equal(AutomationControlType.Edit, name.GetAutomationControlType());
        Assert.Equal("Alt", name.GetProvider<IValueProvider>()!.Value);
        Assert.Null(name.GetProvider<IToggleProvider>());

        // Without /TU the field name; the state as text and as a pattern.
        var required = Peer(overlay, Field(tab, "Pflicht"));
        Assert.Equal("Pflicht", required.GetName());
        Assert.Contains(Strings.FormRequired, required.GetHelpText(), StringComparison.Ordinal);
        var agree = Peer(overlay, Field(tab, "Agb"));
        Assert.Equal(AutomationControlType.CheckBox, agree.GetAutomationControlType());
        Assert.Equal(ToggleState.Off, agree.GetProvider<IToggleProvider>()!.ToggleState);
        Assert.Equal(AutomationControlType.RadioButton, Peer(overlay, Field(tab, "Zahlung")).GetAutomationControlType());
        Assert.Equal(AutomationControlType.ComboBox, Peer(overlay, Field(tab, "Farbe")).GetAutomationControlType());
        Assert.Equal(AutomationControlType.List, Peer(overlay, Field(tab, "Liste")).GetAutomationControlType());
        Assert.Null(Peer(overlay, Field(tab, "Passwort")).GetProvider<IValueProvider>()!.Value);
        var locked = Peer(overlay, Field(tab, "Gesperrt"));
        Assert.True(locked.GetProvider<IValueProvider>()!.IsReadOnly);
        Assert.Contains(Strings.FormLockedReadOnly.TrimEnd('.'), locked.GetHelpText(), StringComparison.Ordinal);

        // Toggled by a screen reader, the box is checked, and says so.
        agree.GetProvider<IToggleProvider>()!.Toggle();
        await WaitForValueAsync(window, tab, "Agb", state => state.Checked == true);
        await WaitUntilAsync(window, () => Peer(overlay, Field(tab, "Agb")).GetProvider<IToggleProvider>()!.ToggleState == ToggleState.On);
        Assert.Contains(Strings.FormFieldChecked, Peer(overlay, Field(tab, "Agb")).GetHelpText(), StringComparison.Ordinal);

        // The input of the field has the same name.
        ClickField(window, view, Field(tab, "Name"));
        Assert.Equal("Full name", AutomationProperties.GetName(view.FormFieldEditor!.TextField));
    }

    [AvaloniaFact]
    public async Task HighlightTellsTheSessionBeforeTheCanvas()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        var seen = new List<(bool Canvas, bool Session)>();
        view.Canvas.PropertyChanged += (_, e) =>
        {
            if (e.Property == DocumentCanvas.FormHighlightProperty)
            {
                seen.Add((view.Canvas.FormHighlight, tab.Session.FormHighlight));
            }
        };
        Assert.False(view.Canvas.FormHighlight);
        Assert.False(tab.Session.FormHighlight);

        await EnterAsync(window, tab, view);
        Assert.True(view.Canvas.FormHighlight);
        Assert.True(view.FormOverlay!.ShowsOutline);

        tab.ToggleHighlightCommand.Execute(null);
        Settle(window);
        Assert.False(view.Canvas.FormHighlight);
        Assert.False(view.FormOverlay.ShowsOutline);
        tab.ToggleHighlightCommand.Execute(null);
        Settle(window);
        tab.EndFill();
        Settle(window);
        Assert.False(view.Canvas.FormHighlight);
        Assert.False(view.FormOverlay.IsVisible);

        // Every change reached the canvas only after the session renders with it.
        Assert.Equal([(true, true), (false, false), (true, true), (false, false)], seen);
    }

    [AvaloniaFact]
    public async Task RefusedChoiceShowsTheValueTheDocumentHas()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);

        // The tab takes no edit (an update locks editing): the chosen option goes back to the one the document has.
        ClickField(window, view, Field(tab, "Farbe"));
        var editor = view.FormFieldEditor!;
        tab.IsEditingFrozen = true;
        editor.ChoiceField.SelectedIndex = 2;
        Settle(window);
        Assert.Equal(-1, editor.ChoiceField.SelectedIndex);
        Assert.Equal(Strings.FormValueNotNow, tab.FormNotice);
        Assert.Empty((await ValueAsync(tab, "Farbe")).SelectedOptions);

        tab.IsEditingFrozen = false;
        ClickField(window, view, Field(tab, "Liste"));
        editor = view.FormFieldEditor!;
        tab.IsEditingFrozen = true;
        editor.ListField.SelectedIndex = 1;
        Settle(window);
        Assert.Equal(-1, editor.ListField.SelectedIndex);

        // PDFium refuses the value: the same.
        tab.IsEditingFrozen = false;
        tab.FormValueWriter = (_, _, _) => throw new InvalidOperationException("Refused for the test.");
        editor.ListField.SelectedIndex = 3;
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && editor.ListField.SelectedIndex == -1);
        Assert.Equal(Strings.FormValueRefused, tab.FormNotice);
        Assert.Empty((await ValueAsync(tab, "Liste")).SelectedOptions);
        Assert.False(tab.CanUndo);
    }

    [AvaloniaFact]
    public async Task RefusedValueInTheLastFieldDoesNotTrapTab()
    {
        var (window, _, tab, view) = await ShowAsync("forms-signed.pdf");
        await EnterAsync(window, tab, view);
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);

        Tab(window);
        AssertFocusIn(view, "Name");
        window.KeyTextInput("漢");
        Settle(window);

        // The value cannot be applied, but after the last field Tab still leaves the document; the text stays with its reason.
        Tab(window);
        await WaitUntilAsync(window, () => !view.IsKeyboardFocusWithin);
        Assert.Equal("漢", tab.CurrentDraft!.Text);
        Assert.NotNull(tab.CurrentDraft.Reason);
        Assert.True(tab.HasFormDraft);

        // Back in the field, Shift+Tab leaves it backwards too; the reason names the way out (Esc).
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);
        Tab(window);
        AssertFocusIn(view, "Name");
        Assert.Equal("漢", view.FormFieldEditor!.TextField.Text);
        Assert.Contains("Esc", AutomationProperties.GetHelpText(view.FormFieldEditor.TextField), StringComparison.Ordinal);
        Press(window, Key.Tab, PhysicalKey.Tab, Shift);
        await WaitUntilAsync(window, () => !view.IsKeyboardFocusWithin);
        Assert.Equal("漢", tab.CurrentDraft!.Text);
    }

    [AvaloniaFact]
    public async Task PageKeysStayInTheInput()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);

        foreach (var name in (string[])["Bemerkung", "Liste", "Name"])
        {
            ClickField(window, view, Field(tab, name));
            var offset = view.ScrollViewer.Offset;
            var page = tab.CurrentPage;
            Assert.True(view.FormFieldEditor!.IsKeyboardFocusWithin, $"{name}: focus on {window.FocusManager!.GetFocusedElement()}");
            Press(window, Key.PageDown, PhysicalKey.PageDown, None);
            Press(window, Key.PageUp, PhysicalKey.PageUp, None);
            Press(window, Key.PageDown, PhysicalKey.PageDown, None);
            Assert.Equal(name, view.FormFieldEditor?.Field?.Key.FieldName);
            Assert.True(view.FormFieldEditor!.IsKeyboardFocusWithin, $"{name} lost the keys");
            Assert.Equal(page, tab.CurrentPage);
            Assert.Equal(offset, view.ScrollViewer.Offset);
        }

        Assert.False(tab.CanUndo);
    }

    [AvaloniaFact]
    public async Task CheckBoxTakesTheKeyboardFocusAndTellsItsState()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);
        ClickField(window, view, Field(tab, "Passwort"));

        // Tab to the check box: its element has the keyboard focus (screen readers follow it), the ring is on the layer.
        Tab(window);
        var agree = Field(tab, "Agb");
        Assert.Equal(agree.Key, tab.FocusedField);
        var element = view.FormOverlay!.ElementOf(agree.Key)!;
        Assert.True(element.IsFocused);
        Assert.Same(element, window.FocusManager!.GetFocusedElement());
        Assert.False(KeyboardNavigation.GetIsTabStop(element));

        var peer = ControlAutomationPeer.CreatePeerForElement(element);
        var changes = new List<AutomationPropertyChangedEventArgs>();
        peer.PropertyChanged += (_, e) => changes.Add(e);
        Press(window, Key.Space, PhysicalKey.Space, None);
        await WaitForValueAsync(window, tab, "Agb", state => state.Checked == true);
        await WaitUntilAsync(window, () => changes.Any(change => change.Property == TogglePatternIdentifiers.ToggleStateProperty));
        Assert.Equal(ToggleState.On, peer.GetProvider<IToggleProvider>()!.ToggleState);
        var toggled = changes.Last(change => change.Property == TogglePatternIdentifiers.ToggleStateProperty);
        Assert.Equal((ToggleState.Off, ToggleState.On), ((ToggleState)toggled.OldValue!, (ToggleState)toggled.NewValue!));
        Assert.True(element.IsFocused);

        // Esc takes the focus from the field: the keys go back to the document.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Null(tab.FocusedField);
        Assert.True(view.IsFocused);
    }

    [AvaloniaFact]
    public async Task WheelOverAFocusedComboChangesNothing()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);
        var colour = Field(tab, "Farbe");
        ClickField(window, view, colour);
        var editor = view.FormFieldEditor!;
        Assert.True(editor.ChoiceField.IsKeyboardFocusWithin);
        var version = tab.EditVersion;
        var offset = view.ScrollViewer.Offset;

        window.MouseWheel(Center(view, window, colour), new Vector(0, -1), None);
        Settle(window);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);

        // The wheel scrolls the document, never the choice.
        Assert.Equal(version, tab.EditVersion);
        Assert.Equal(-1, editor.ChoiceField.SelectedIndex);
        Assert.Empty((await ValueAsync(tab, "Farbe")).SelectedOptions);
        Assert.True(view.ScrollViewer.Offset.Y > offset.Y, $"{view.ScrollViewer.Offset} vs {offset}");
    }

    [AvaloniaFact]
    public async Task HighlightSurvivesSavingTheDocument()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);
        ClickField(window, view, Field(tab, "Kurz"));
        window.KeyTextInput("ab");
        Click(window, ScreenPoint(view, window, 0, new Point(395, 5)));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.CanUndo);
        var old = tab.Document!;

        var saved = await tab.Editor.SaveAsync(new PdfEngine(), tab.FullPath!, overwriteChangedOriginal: false, progress: null, TestContext.Current.CancellationToken);
        Assert.True(saved.Adopted);
        await WaitUntilAsync(window, () => tab.Document is { } document && !ReferenceEquals(document, old) && ReferenceEquals(view.Document, document));

        // The saved document renders highlighted as before, and the canvas still asks for it.
        Assert.True(tab.ShowsFieldHighlight);
        Assert.True(tab.Session.FormHighlight);
        Assert.True(tab.Session.Scheduler!.FormHighlight);
        Assert.True(view.Canvas.FormHighlight);
        Assert.True(view.FormOverlay!.ShowsOutline);
    }

    [AvaloniaFact]
    public async Task OverlayColoursMeetAA()
    {
        var (window, _, tab, view) = await ShowAsync("forms-basic.pdf");
        await EnterAsync(window, tab, view);
        var overlay = view.FormOverlay!;
        var white = Colors.White;

        // The outline lies on the light field, plain or under PDFium's tint (a quarter of the light accent).
        var tint = Blend(Color.FromRgb(0x3B, 0x48, 0xC4), FormHighlightColors.Alpha, white);
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), overlay.OutlineColor);
        Assert.True(Wcag.Contrast(overlay.OutlineColor, white) >= 3, "outline on a white field");
        Assert.True(Wcag.Contrast(overlay.OutlineColor, tint) >= 3, "outline on the tinted field");

        // On a light page the ring and the badges of the light theme, on a dark page those of the dark theme.
        foreach (var (variant, ground) in (ReadOnlySpan<(ThemeVariant, Color)>)[(ThemeVariant.Light, white), (ThemeVariant.Dark, TokenTests.Token("Page.DarkBackground", ThemeVariant.Light))])
        {
            var ring = TokenTests.Token("Focus.Ring", variant);
            Assert.True(Wcag.Contrast(ring, ground) >= 3, $"focus ring on the {variant} page");
            Assert.True(Wcag.Contrast(TokenTests.Token("Border.Strong", variant), ground) >= 3, $"badge edge on the {variant} page");
            Assert.True(Wcag.Contrast(TokenTests.Token("Text.Primary", variant), TokenTests.Token("Surface.Raised", variant)) >= 4.5, $"badge symbol ({variant})");
        }

        Assert.Equal(TokenTests.Token("Focus.Ring", ThemeVariant.Light), overlay.FocusRingColor);
        Assert.Equal(
            (TokenTests.Token("Surface.Raised", ThemeVariant.Light), TokenTests.Token("Border.Strong", ThemeVariant.Light), TokenTests.Token("Text.Primary", ThemeVariant.Light)),
            overlay.BadgeColors);
        tab.PageThemeOverride = PageTheme.Dark;
        Settle(window);
        Assert.Equal(TokenTests.Token("Focus.Ring", ThemeVariant.Dark), overlay.FocusRingColor);
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), overlay.OutlineColor);

        // Moving the focus (without a change of a value) redraws the rings, not the whole layer.
        var name = Field(tab, "Name");
        var shortField = Field(tab, "Kurz");
        ClickField(window, view, name);
        ClickField(window, view, shortField);
        var rings = overlay.ViewRectOf(name)!.Value.Union(overlay.ViewRectOf(shortField)!.Value).Inflate(FormOverlay.FocusRingThickness + 1);
        var redrawn = overlay.LastInvalidatedRect;
        Assert.True(
            Math.Abs(redrawn.X - rings.X) < 0.01 && Math.Abs(redrawn.Y - rings.Y) < 0.01 && Math.Abs(redrawn.Width - rings.Width) < 0.01 && Math.Abs(redrawn.Height - rings.Height) < 0.01,
            $"{redrawn} vs the rings {rings}");
    }

    private static Color Blend(Color top, byte alpha, Color under)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(((a * alpha) + (b * (255 - alpha))) / 255.0);
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static AutomationPeer Peer(FormOverlay overlay, FormField field) =>
        ControlAutomationPeer.CreatePeerForElement(overlay.ElementOf(field.Key) ?? throw new InvalidOperationException($"No element for {field.Key}."));

    private static FormField Field(TabViewModel tab, string name, int page = 0) =>
        tab.FieldsOfPage(page).First(field => field.Key.FieldName == name);

    private static void AssertFocusIn(DocumentView view, string name)
    {
        var editor = view.FormFieldEditor ?? throw new InvalidOperationException($"No input open, expected {name}.");
        Assert.Equal(name, editor.Field!.Key.FieldName);
        Assert.True(editor.IsKeyboardFocusWithin, $"The input of {name} has no keyboard focus.");
    }

    /// <summary>The input lies exactly on the field as the page shows it (the page placement of the canvas and the field's points).</summary>
    private static void AssertInputOnField(DocumentView view, FormFieldEditor editor, FormField field)
    {
        var layout = view.Layout!;
        var page = layout.GetPageRect(field.Key.Page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var expected = new Rect(
            page.X - view.Canvas.Viewport.X + (field.Bounds.Left * scale),
            page.Y - view.Canvas.Viewport.Y + (field.Bounds.Top * scale),
            field.Bounds.Width * scale,
            field.Bounds.Height * scale);
        var input = editor.Input!;
        var topLeft = input.TranslatePoint(default, view.Canvas)!.Value;
        var actual = new Rect(topLeft, input.Bounds.Size);
        Assert.True(
            Math.Abs(expected.X - actual.X) <= 1 && Math.Abs(expected.Y - actual.Y) <= 1
            && Math.Abs(expected.Width - actual.Width) <= 1 && Math.Abs(expected.Height - actual.Height) <= 1,
            $"input {actual} vs field {expected}");
        Assert.Equal(expected.Width, view.FormOverlay!.ViewRectOf(field)!.Value.Width, 3);
    }

    private static void AssertRectNear(PageRect expected, PageRect actual, double tolerance) =>
        Assert.True(
            Math.Abs(expected.Left - actual.Left) <= tolerance && Math.Abs(expected.Top - actual.Top) <= tolerance
            && Math.Abs(expected.Right - actual.Right) <= tolerance && Math.Abs(expected.Bottom - actual.Bottom) <= tolerance,
            $"{expected} vs {actual}");

    private static async Task EnterAsync(Window window, TabViewModel tab, DocumentView view)
    {
        await tab.EnterFillAsync();
        await WaitUntilAsync(window, () => tab.IsFilling && tab.FieldsOfPage(0).Count > 0 && view.FormOverlay is { IsVisible: true, Fields.Count: > 0 });
    }

    private static async Task<FormFieldState> ValueAsync(TabViewModel tab, string name, int page = 0) =>
        (await tab.Session.Document!.GetFormFieldsAsync(page, TestContext.Current.CancellationToken)).First(field => field.Key.FieldName == name).State;

    private static async Task WaitForValueAsync(Window window, TabViewModel tab, string name, Func<FormFieldState, bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        FormFieldState? state = null;
        while (true)
        {
            Settle(window);
            if (!tab.HasEditsInFlight)
            {
                state = await ValueAsync(tab, name);
                if (condition(state))
                {
                    return;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{name} did not reach the expected value ({state}).");
            }

            await Task.Delay(10);
        }
    }

    private static Point Center(DocumentView view, Window window, FormField field) =>
        ScreenPoint(view, window, field.Key.Page, new Point((field.Bounds.Left + field.Bounds.Right) / 2, (field.Bounds.Top + field.Bounds.Bottom) / 2));

    /// <summary>Clicks the middle of a field, scrolled into view first (only when it is not fully visible).</summary>
    private static void ClickField(Window window, DocumentView view, FormField field)
    {
        if (view.DataContext is TabViewModel tab)
        {
            tab.RevealArea(field.Key.Page, field.Bounds, fromPanel: false);
        }

        Settle(window);
        Click(window, Center(view, window, field));
    }

    private static Point ScreenPoint(DocumentView view, Window window, int page, Point pagePoint)
    {
        var layout = view.Layout!;
        var rect = layout.GetPageRect(page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var canvas = new Point(rect.X - view.Canvas.Viewport.X + (pagePoint.X * scale), rect.Y - view.Canvas.Viewport.Y + (pagePoint.Y * scale));
        return view.Canvas.TranslatePoint(canvas, window)!.Value;
    }

    private static void Click(Window window, Point point)
    {
        Settle(window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Tab(Window window) => Press(window, Key.Tab, PhysicalKey.Tab, None);

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

    /// <summary>A window with a copy of a test file open, its first sharp page shown, the auto-opened panel and the offer of the mode closed.</summary>
    private async Task<(MainWindow Window, MainViewModel ViewModel, TabViewModel Tab, DocumentView View)> ShowAsync(string fixture)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = window.GetVisualDescendants().OfType<DocumentView>().First(v => v.Name == "DocumentView");

        var path = System.IO.Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8] + "-" + fixture);
        File.Copy(TestData.Pdf(fixture), path);
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage && tab.OffersFill);
        tab.CloseFillOfferCommand.Execute(null);
        Settle(window);
        return (window, viewModel, tab, view);
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
