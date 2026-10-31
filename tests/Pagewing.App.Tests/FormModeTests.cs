using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The mode "Fill in form" of the tab (milestone 11, task 10): when it is offered, what it reads and when, the open value of a
/// field (the draft) with its reason, applying it before everything that ends or blocks editing, the switch to "Comment",
/// the hints, the tab order and the locked fields. Without a window: the tab is driven as the document view will drive it.
/// Every file is a copy in a temp folder; settings and history are in memory.
/// </summary>
// Strings has a process-wide culture (see LocalizationTests).
[Collection("Localization")]
public sealed partial class FormModeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagewing-forms-" + Guid.NewGuid().ToString("N"))).FullName;

    public FormModeTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task EnteringTheModeReadsTheFieldsOfTheVisiblePagesOnly()
    {
        var reads = new List<int>();
        var tab = await OpenAsync("forms-basic.pdf", configure: tab => tab.FormFieldReader = Recording(reads));
        Assert.True(tab.HasForm);
        Assert.False(tab.IsFilling);
        Assert.True(tab.ToggleFillCommand.CanExecute(null));

        // Nothing is read before the mode.
        Pump();
        Assert.Empty(reads);

        await tab.EnterFillAsync();

        // The page the view shows (page 1 of 2), in the order of /Annots; highlighting is on in the mode.
        Assert.True(tab.IsFilling);
        Assert.True(tab.HighlightFields);
        Assert.True(tab.ShowsFieldHighlight);
        Assert.Equal([0], reads);
        var fields = tab.FieldsOfPage(0);
        Assert.Equal(17, fields.Count);
        Assert.Equal("Name", fields[0].Key.FieldName);
        Assert.Equal("Mehrfach", fields[^1].Key.FieldName);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);

        // Asked again it costs nothing; another page is read when asked for, once.
        Assert.Same(fields, tab.FieldsOfPage(0));
        // Whether the first answer is still empty depends on how fast the read ends, so only the result is asserted.
        _ = tab.FieldsOfPage(1);
        await WaitUntilAsync(() => tab.FieldsOfPage(1).Count == 4);
        Assert.Equal([0, 1], reads);
        Assert.Equal(4, tab.FieldsOfPage(1).Count);
        Assert.Equal([0, 1], reads);

        // The view tells which pages it shows: entering again reads only what it does not have.
        tab.EndFill();
        tab.VisibleFormPages = () => [0, 1];
        await tab.EnterFillAsync();
        Assert.Equal([0, 1], reads);
    }

    [AvaloniaFact]
    public async Task DocumentWithoutAFormOffersNoModeAndCreatesNoEnvironment()
    {
        var infoReads = 0;
        var fieldReads = new List<int>();
        var tab = await OpenAsync("text-basic.pdf", configure: tab =>
        {
            tab.FormInfoReader = (document, token) =>
            {
                infoReads++;
                return document.GetFormInfoAsync(token);
            };
            tab.FormFieldReader = Recording(fieldReads);
        });

        Assert.False(tab.HasForm);
        Assert.False(tab.ToggleFillCommand.CanExecute(null));
        Assert.False(tab.NextFieldCommand.CanExecute(null));
        Assert.False(tab.ToggleHighlightCommand.CanExecute(null));
        await tab.EnterFillAsync();
        Assert.False(tab.IsFilling);
        Assert.Empty(tab.FieldsOfPage(0));
        Assert.False(await tab.MoveToFieldAsync(forward: true));
        Assert.False(await tab.CommitFieldAsync("Name", FormFieldState.OfText("x")));
        Assert.True(tab.FlushFormDraft());
        Pump();

        Assert.Equal(FormHint.None, tab.PendingHints);
        Assert.False(tab.HasFormFields);
        Assert.Equal(0, infoReads);
        Assert.Empty(fieldReads);
        Assert.False(tab.HasEditor);
        Assert.Equal(0, tab.Document!.FormEnvironmentsCreated);

        // A document with a form has its environment from the open on (it draws the fields).
        var form = await OpenAsync("forms-basic.pdf");
        Assert.Equal(1, form.Document!.FormEnvironmentsCreated);
    }

    [AvaloniaFact]
    public async Task NothingIsReadBeforeTheFirstSharpPage()
    {
        var infoReads = 0;
        var fieldReads = new List<int>();
        var tab = await OpenAsync(
            "forms-calc.pdf",
            configure: tab =>
            {
                tab.FormInfoReader = (document, token) =>
                {
                    infoReads++;
                    return document.GetFormInfoAsync(token);
                };
                tab.FormFieldReader = Recording(fieldReads);
            },
            sharp: false);

        // Known after the open, but nothing is offered or read yet.
        Assert.True(tab.HasForm);
        Assert.False(tab.ToggleFillCommand.CanExecute(null));
        await tab.EnterFillAsync();
        Assert.False(tab.IsFilling);
        Assert.Empty(tab.FieldsOfPage(0));
        await tab.EnsureFormInfoAsync();
        Pump();
        Assert.Equal(FormHint.None, tab.PendingHints);
        Assert.Equal(0, infoReads);
        Assert.Empty(fieldReads);
        Assert.False(tab.HasEditor);

        // The first sharp page starts the hints, once; the fields still wait for the mode.
        tab.HasShownSharpPage = true;
        await WaitUntilAsync(() => tab.PendingHints != FormHint.None);
        Assert.Equal(FormHint.Calculated | FormHint.FormatScripts, tab.PendingHints);
        Assert.True(tab.HasFormFields);
        Assert.Equal(1, infoReads);
        Assert.Empty(fieldReads);
        Assert.True(tab.ToggleFillCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CommitUsesTheEditorAndIsOneStep()
    {
        var tab = await OpenAsync("forms-basic.pdf");
        await tab.EnterFillAsync();
        var name = tab.FieldsOfPage(0)[0];
        Assert.Equal("Alt", name.State.Text);

        // Entering and leaving without a change: no change, no step, not even an editor.
        Assert.True(tab.EditDraft(name.Key, "Alt"));
        Assert.False(tab.HasFormDraft);
        Assert.False(tab.IsDirty);
        Assert.True(await tab.CommitFieldAsync("Name", FormFieldState.OfText("Alt")));
        Assert.Null(tab.CurrentDraft);
        Assert.False(tab.HasEditor);

        // Typed: an unsaved change from the first key on.
        Assert.True(tab.EditDraft(name.Key, "Ada Lovelace"));
        Assert.True(tab.HasFormDraft);
        Assert.True(tab.IsDirty);
        Assert.True(tab.AsksBeforeClosing);

        // Left: one step of the editor's history, the draft is gone, the value is in the document (on both widgets).
        Assert.True(await tab.CommitFieldAsync("Name", FormFieldState.OfText("Ada Lovelace")));
        Assert.True(tab.HasEditor);
        Assert.Null(tab.CurrentDraft);
        Assert.False(tab.HasFormDraft);
        await WaitUntilAsync(() => tab.IsDirty && tab.CanUndo && !tab.HasEditsInFlight);
        Assert.Equal("Ada Lovelace", await ValueAsync(tab, 0, "Name"));
        Assert.Equal("Ada Lovelace", await ValueAsync(tab, 1, "Name"));
        Assert.Equal("Ada Lovelace", tab.FieldsOfPage(0)[0].State.Text);

        // One step: a single undo takes it back, and there is nothing more to undo.
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(() => !tab.CanUndo && tab.CanRedo && !tab.HasEditsInFlight);
        Assert.Equal("Alt", await ValueAsync(tab, 0, "Name"));

        // The fields the tab shows follow the history.
        await WaitUntilAsync(() => tab.FieldsOfPage(0)[0].State.Text == "Alt");

        // A check box is one click, one step.
        var agb = tab.FieldsOfPage(0).Single(field => field.Key.FieldName == "Agb");
        Assert.True(await tab.CommitFieldAsync("Agb", FormFieldState.OfChecked(agb.State.Checked != true)));
        await WaitUntilAsync(() => tab.CanUndo && !tab.CanRedo && !tab.HasEditsInFlight);
        Assert.Null(tab.CurrentDraft);
    }

    [AvaloniaFact]
    public async Task RejectedTextStaysAsADraftWithItsReason()
    {
        var tab = await OpenAsync("forms-basic.pdf");
        await tab.EnterFillAsync();
        var fields = tab.FieldsOfPage(0);
        var kurz = fields.Single(field => field.Key.FieldName == "Kurz");

        // Longer than /MaxLen 5: refused as a whole, the text stays with the reason, nothing shortened.
        Assert.True(tab.EditDraft(kurz.Key, "123456"));
        Assert.False(await tab.CommitFieldAsync("Kurz", FormFieldState.OfText("123456")));
        var draft = Assert.IsType<FormDraft>(tab.CurrentDraft);
        Assert.Equal(kurz.Key, draft.Key);
        Assert.Equal("123456", draft.Text);
        Assert.Equal(Format(Strings.FormValueTooLong, 5), draft.Reason);
        Assert.True(tab.HasFormDraft);
        Assert.True(tab.IsDirty);
        Assert.True(tab.AsksBeforeClosing);
        Assert.False(tab.HasEditor);

        // A flush (save, print, close) cannot take it either: it stays.
        Assert.False(tab.FlushFormDraft());
        Assert.Equal("123456", tab.CurrentDraft!.Text);

        // Typing on clears the reason until the next try.
        Assert.True(tab.EditDraft(kurz.Key, "12345"));
        Assert.Null(tab.CurrentDraft!.Reason);
        tab.DiscardDraft();
        Assert.Null(tab.CurrentDraft);
        Assert.False(tab.IsDirty);

        // Characters the field cannot show: the reason names each once; the text is kept exactly.
        var name = fields[0];
        const string Typed = "„Ada“ – \U0001F600";
        Assert.True(tab.EditDraft(name.Key, Typed));
        Assert.False(await tab.CommitFieldAsync("Name", FormFieldState.OfText(Typed)));
        Assert.Equal(Typed, tab.CurrentDraft!.Text);
        Assert.Equal(Format(Strings.FormValueUnsupportedCharacters, "„ “ – \U0001F600"), tab.CurrentDraft.Reason);
        tab.DiscardDraft();

        // A line break in a one-line field, a control character.
        Assert.True(tab.EditDraft(name.Key, "a\nb"));
        Assert.False(tab.FlushFormDraft());
        Assert.Equal(Strings.FormValueOneLine, tab.CurrentDraft!.Reason);
        Assert.True(tab.EditDraft(name.Key, "a\tb"));
        Assert.False(tab.FlushFormDraft());
        Assert.Equal(Format(Strings.FormValueControlCharacter, "U+0009"), tab.CurrentDraft!.Reason);

        // While a draft is refused, another field does not take its place (the typed text would be lost).
        Assert.False(tab.EditDraft(kurz.Key, "1"));
        Assert.Equal(name.Key, tab.CurrentDraft!.Key);
        tab.DiscardDraft();

        // Nothing reached the document.
        Assert.Equal("Alt", await ValueAsync(tab, 0, "Name"));
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task FlushTakesTheDraftBeforeSavePrintCloseAndModeEnd()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached());
        _cleanup.Add(viewModel);
        var path = Copy("forms-basic.pdf");
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null);
        tab.HasShownSharpPage = true;
        await tab.EnterFillAsync();

        // Save (Ctrl+S): the open value is applied and saved with the rest. The card about the empty required field
        // ("Pflicht", FormSaveTests) is not the subject here.
        viewModel.RequiredFieldsReader = static (_, _) => Task.FromResult<IReadOnlyList<string>>([]);
        Assert.True(tab.EditDraft(Key(tab, "Name"), "Gespeichert"));
        Assert.True(await viewModel.SaveTabAsync(tab, saveAs: false).WaitAsync(Timeout));
        Assert.False(tab.IsDirty);
        Assert.Null(tab.CurrentDraft);
        await using (var saved = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal("Gespeichert", (await saved.GetFormFieldsAsync(0, TestContext.Current.CancellationToken))[0].State.Text);
        }

        // The mode stays with the saved document; its fields are read again from it.
        Assert.True(tab.IsFilling);
        await tab.ReadFieldsAsync(0);

        // Printing: the job applies it first (MainViewModel.Print: FlushNudge before the job).
        Assert.True(tab.EditDraft(Key(tab, "Pflicht"), "Gedruckt"));
        tab.FlushNudge();
        await tab.WhenEditsSettledAsync();
        Assert.Equal("Gedruckt", await ValueAsync(tab, 0, "Pflicht"));
        Assert.Null(tab.CurrentDraft);

        // While the tab takes no edit (a card asks about it), the value waits with its reason and is not dropped.
        tab.IsAskedAbout = true;
        Assert.True(tab.EditDraft(Key(tab, "Passwort"), "geheim"));
        Assert.False(tab.FlushFormDraft());
        Assert.True(tab.CurrentDraft!.Waiting);
        Assert.Equal(Strings.FormValueNotNow, tab.CurrentDraft.Reason);
        Assert.Same(tab.CurrentDraft, tab.PendingFormDraft);
        Assert.True(tab.AsksBeforeClosing);
        tab.IsAskedAbout = false;

        // Closing: applied before the question, which then asks about it.
        viewModel.CloseTabCommand.Execute(tab);
        await WaitUntilAsync(() => viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges });
        Assert.Contains(tab, viewModel.Tabs);
        await tab.WhenEditsSettledAsync();
        Assert.Equal("geheim", await ValueAsync(tab, 0, "Passwort"));
        Assert.Null(tab.PendingFormDraft);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        await WaitUntilAsync(() => viewModel.PendingSave is null);

        // The end of the mode ("Done").
        Assert.True(tab.EditDraft(Key(tab, "Kamm"), "ABC"));
        Assert.True(tab.EndFill());
        Assert.False(tab.IsFilling);
        await tab.WhenEditsSettledAsync();
        Assert.Equal("ABC", await ValueAsync(tab, 0, "Kamm"));
        Assert.Null(tab.CurrentDraft);

        // A refused value keeps the mode: "Done" does not drop what was typed.
        await tab.EnterFillAsync();
        Assert.True(tab.EditDraft(Key(tab, "Kurz"), "123456"));
        Assert.False(tab.EndFill());
        Assert.True(tab.IsFilling);
        Assert.Equal(Key(tab, "Kurz"), tab.FocusedField);
        Assert.Equal("123456", tab.CurrentDraft!.Text);
    }

    [AvaloniaFact]
    public async Task SwitchingBetweenFillAndAnnotateTakesTheOpenDraft()
    {
        var tab = await OpenAsync("forms-basic.pdf");
        await tab.EnterFillAsync();

        // Filling → commenting: the open value is applied, filling ends.
        Assert.True(tab.EditDraft(Key(tab, "Name"), "Wechsel"));
        Assert.True(tab.ToggleAnnotateCommand.CanExecute(null));
        tab.ToggleAnnotateCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsAnnotating);
        Assert.False(tab.IsFilling);
        await tab.WhenEditsSettledAsync();
        Assert.Null(tab.CurrentDraft);
        Assert.Equal("Wechsel", await ValueAsync(tab, 0, "Name"));

        // Commenting → filling: the open text box field is applied first, commenting ends.
        var flushed = 0;
        tab.TextBoxFieldFlush = () =>
        {
            flushed++;
            return true;
        };
        Assert.True(tab.ToggleFillCommand.CanExecute(null));
        tab.ToggleFillCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsFilling);
        Assert.True(flushed > 0);
        Assert.False(tab.IsAnnotating);
        tab.TextBoxFieldFlush = null;

        // A refused value is not dropped by the switch: filling stays, with the text.
        await tab.ReadFieldsAsync(0);
        Assert.True(tab.EditDraft(Key(tab, "Kurz"), "123456"));
        tab.ToggleAnnotateCommand.Execute(null);
        Pump();
        Assert.True(tab.IsFilling);
        Assert.False(tab.IsAnnotating);
        Assert.Equal("123456", tab.CurrentDraft!.Text);

        // Undo and save are shared: the value applied while filling is undone while commenting.
        tab.DiscardDraft();
        tab.ToggleAnnotateCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsAnnotating);
        await WaitUntilAsync(() => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(() => !tab.CanUndo && !tab.HasEditsInFlight);
        Assert.Equal("Alt", await ValueAsync(tab, 0, "Name"));
    }

    [AvaloniaFact]
    public async Task HintsAreRaisedOncePerTabAndDocument()
    {
        // The scan is slow on purpose: everything that asks while it runs shares it.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var infoReads = 0;
        var tab = await OpenAsync("forms-calc.pdf", configure: tab => tab.FormInfoReader = async (document, token) =>
        {
            infoReads++;
            await gate.Task;
            return await document.GetFormInfoAsync(token);
        });
        var changes = new List<string?>();
        tab.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        var first = tab.EnsureFormInfoAsync();
        var second = tab.EnsureFormInfoAsync();
        await tab.EnterFillAsync();
        var move = tab.MoveToFieldAsync(forward: true);
        Assert.Equal(FormHint.None, tab.PendingHints);
        gate.SetResult();
        await Task.WhenAll(first, second, move);
        Assert.True(await move);

        Assert.Equal(FormHint.Calculated | FormHint.FormatScripts, tab.PendingHints);
        Assert.Contains(nameof(TabViewModel.PendingHints), changes);

        // A commit needs the same scan (to find the pages of the field): no second one.
        Assert.True(await tab.CommitFieldAsync("Zahl1", FormFieldState.OfText("12")));
        await tab.WhenEditsSettledAsync();
        Assert.Equal(1, infoReads);

        // XFA, and the signature field with a button.
        var xfa = await OpenAsync("forms-xfa.pdf");
        await WaitUntilAsync(() => xfa.PendingHints != FormHint.None);
        Assert.Equal(FormHint.Xfa, xfa.PendingHints);
        var signed = await OpenAsync("forms-signed.pdf");
        await WaitUntilAsync(() => signed.PendingHints != FormHint.None);
        Assert.Equal(FormHint.SignatureField | FormHint.Buttons, signed.PendingHints);

        // A form without anything to point out has no hint, but it does have fields.
        var basic = await OpenAsync("forms-basic.pdf");
        await basic.EnsureFormInfoAsync();
        Assert.Equal(FormHint.None, basic.PendingHints);
        Assert.True(basic.HasFormFields);

        // Each hint has its own text in both languages.
        foreach (var hint in new[] { FormHint.Calculated, FormHint.FormatScripts, FormHint.Xfa, FormHint.SignatureField, FormHint.Buttons })
        {
            Assert.False(string.IsNullOrWhiteSpace(TabViewModel.HintTextOf(hint)));
        }
    }

    [AvaloniaFact]
    public async Task DismissedHintDoesNotComeBack()
    {
        var infoReads = 0;
        var tab = await OpenAsync("forms-signed.pdf", configure: tab => tab.FormInfoReader = (document, token) =>
        {
            infoReads++;
            return document.GetFormInfoAsync(token);
        });
        await WaitUntilAsync(() => tab.PendingHints != FormHint.None);
        Assert.True(tab.DismissHintCommand.CanExecute(FormHint.Buttons));

        tab.DismissHintCommand.Execute(FormHint.Buttons);

        Assert.Equal(FormHint.SignatureField, tab.PendingHints);
        Assert.False(tab.DismissHintCommand.CanExecute(FormHint.Buttons));

        // Not with the mode, another request, nor after the save swapped the document for the written file.
        await tab.EnterFillAsync();
        Assert.True(tab.EditDraft(Key(tab, "Name"), "Ada"));
        Assert.True(tab.FlushFormDraft());
        await tab.WhenEditsSettledAsync();
        var old = tab.Document!;
        var saved = await tab.Editor.SaveAsync(new PdfEngine(), tab.FullPath!, overwriteChangedOriginal: false, progress: null, TestContext.Current.CancellationToken).WaitAsync(Timeout);
        Assert.True(saved.Adopted);
        await WaitUntilAsync(() => tab.Document is { } document && !ReferenceEquals(document, old));
        await tab.EnsureFormInfoAsync();
        Assert.Equal(FormHint.SignatureField, tab.PendingHints);
        Assert.Equal(1, infoReads);

        // Another file in the tab brings its own hints again.
        await tab.OpenAsync(Copy("forms-signed.pdf", "other.pdf"));
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is { } document && !ReferenceEquals(document, old));
        Assert.Equal(FormHint.None, tab.PendingHints);
        tab.HasShownSharpPage = true;
        await WaitUntilAsync(() => tab.PendingHints == (FormHint.SignatureField | FormHint.Buttons));
        Assert.Equal(2, infoReads);
    }

    [AvaloniaFact]
    public async Task AdoptingTheSavedDocumentKeepsTheModeAndTheFocusedField()
    {
        var tab = await OpenAsync("forms-basic.pdf");
        await tab.EnterFillAsync();
        Assert.True(await tab.MoveToFieldAsync(forward: true));
        Assert.True(tab.EditDraft(Key(tab, "Name"), "Vorher"));
        Assert.True(tab.FlushFormDraft());
        await tab.WhenEditsSettledAsync();
        var focused = tab.FocusedField;
        var old = tab.Document!;

        var saved = await tab.Editor.SaveAsync(new PdfEngine(), tab.FullPath!, overwriteChangedOriginal: false, progress: null, TestContext.Current.CancellationToken).WaitAsync(Timeout);

        Assert.True(saved.Adopted);
        await WaitUntilAsync(() => tab.Document is { } document && !ReferenceEquals(document, old));
        Assert.True(tab.IsFilling);
        Assert.Equal(focused, tab.FocusedField);

        // The fields are read from the saved document, and the next value goes into it.
        var fields = await tab.ReadFieldsAsync(0);
        Assert.Equal("Vorher", fields[0].State.Text);
        Assert.True(tab.EditDraft(fields[0].Key, "Nachher"));
        Assert.True(await tab.CommitFieldAsync("Name", FormFieldState.OfText("Nachher")));
        Assert.Equal("Nachher", await ValueAsync(tab, 0, "Name"));
        Assert.True(await tab.MoveToFieldAsync(forward: true));
        Assert.Equal("Bemerkung", tab.FocusedField!.Value.FieldName);
    }

    [AvaloniaFact]
    public async Task NextFieldFollowsThePageAndThenTheNextPage()
    {
        var reads = new List<int>();
        var tab = await OpenAsync("forms-basic.pdf", configure: tab => tab.FormFieldReader = Recording(reads));
        await tab.EnterFillAsync();
        var first = tab.FieldsOfPage(0);
        var editable = first.Where(field => field.Lock == FormLock.None).Select(field => field.Key).ToList();
        Assert.DoesNotContain(first.Single(field => field.Key.FieldName == "Gesperrt").Key, editable);

        // Without a field Tab starts at the first one of the page, then follows /Annots and skips the read-only field.
        var visited = new List<FormWidgetKey>();
        while (await tab.MoveToFieldAsync(forward: true) && tab.FocusedField!.Value.Page == 0)
        {
            visited.Add(tab.FocusedField.Value);
        }

        Assert.Equal(editable, visited);
        Assert.Equal(["Kurz", "Pflicht"], visited.Select(key => key.FieldName).SkipWhile(name => name != "Kurz").Take(2));

        // Then the next page: the third button of the radio group "Zahlung", the second widget of "Name", "Telefon", "Ort".
        Assert.Equal(new FormWidgetKey("Zahlung", 1, 0), tab.FocusedField);
        Assert.Contains(1, reads);
        tab.NextFieldCommand.Execute(null);
        await WaitUntilAsync(() => tab.FocusedField == new FormWidgetKey("Name", 1, 0));
        Assert.True(await tab.MoveToFieldAsync(forward: true));
        Assert.Equal(new FormWidgetKey("Telefon", 1, 0), tab.FocusedField);
        Assert.True(await tab.MoveToFieldAsync(forward: true));
        Assert.Equal(new FormWidgetKey("Ort", 1, 0), tab.FocusedField);

        // Back from the first field of page 2 to the last one of page 1.
        tab.FocusedField = new FormWidgetKey("Zahlung", 1, 0);
        tab.PreviousFieldCommand.Execute(null);
        await WaitUntilAsync(() => tab.FocusedField == editable[^1]);
        Assert.Equal("Mehrfach", tab.FocusedField!.Value.FieldName);

        // A typed value goes in before the focus moves on.
        tab.FocusedField = editable[0];
        Assert.True(tab.EditDraft(editable[0], "Weiter"));
        Assert.True(await tab.MoveToFieldAsync(forward: true));
        Assert.Equal(editable[1], tab.FocusedField);
        await tab.WhenEditsSettledAsync();
        Assert.Equal("Weiter", await ValueAsync(tab, 0, "Name"));

        // A refused one keeps the focus in its field.
        Assert.True(tab.EditDraft(Key(tab, "Kurz"), "123456"));
        Assert.True(await tab.MoveToFieldAsync(forward: true));
        Assert.Equal(Key(tab, "Kurz"), tab.FocusedField);
    }

    [AvaloniaFact]
    public async Task NextFieldAtTheLastFieldReturnsNotHandled()
    {
        var tab = await OpenAsync("forms-basic.pdf");
        await tab.EnterFillAsync();
        await tab.EnsureFormInfoAsync();
        await tab.ReadFieldsAsync(1);

        // After the last field Tab is not handled (the view lets it go on, WCAG 2.1.2); the focus stays where it was.
        tab.FocusedField = new FormWidgetKey("Ort", 1, 0);
        var past = tab.MoveToFieldAsync(forward: true);
        Assert.True(past.IsCompleted);
        Assert.False(await past);
        Assert.Equal(new FormWidgetKey("Ort", 1, 0), tab.FocusedField);

        // Before the first one the same with Shift+Tab.
        tab.FocusedField = new FormWidgetKey("Name", 0, 0);
        var before = tab.MoveToFieldAsync(forward: false);
        Assert.True(before.IsCompleted);
        Assert.False(await before);
        Assert.Equal(new FormWidgetKey("Name", 0, 0), tab.FocusedField);

        // With the pages known the answer comes at once (the view decides about Tab while it handles the key).
        var next = tab.MoveToFieldAsync(forward: true);
        Assert.True(next.IsCompleted);
        Assert.True(await next);

        // Outside the mode nothing moves.
        tab.EndFill();
        Assert.False(tab.NextFieldCommand.CanExecute(null));
        Assert.False(await tab.MoveToFieldAsync(forward: true));
        Assert.Null(tab.FocusedField);
    }

    [AvaloniaFact]
    public async Task ReadOnlyAndUnsupportedFieldsAreNotEditable()
    {
        var tab = await OpenAsync("forms-basic.pdf");
        await tab.EnterFillAsync();
        var locked = tab.FieldsOfPage(0).Single(field => field.Key.FieldName == "Gesperrt");
        Assert.Equal(FormLock.ReadOnly, locked.Lock);
        Assert.False(TabViewModel.IsEditable(locked));
        Assert.Equal(Strings.FormLockedReadOnly, TabViewModel.LockTextOf(locked));
        Assert.True(TabViewModel.IsEditable(tab.FieldsOfPage(0)[0]));
        Assert.Null(TabViewModel.LockTextOf(tab.FieldsOfPage(0)[0]));

        // No draft, no write, no step.
        Assert.False(tab.EditDraft(locked.Key, "Neu"));
        Assert.Null(tab.CurrentDraft);
        Assert.False(await tab.CommitFieldAsync("Gesperrt", FormFieldState.OfText("Neu")));
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
        Assert.Equal("Fest", await ValueAsync(tab, 0, "Gesperrt"));

        // Signature and button: not editable, with a text of their own (never the English diagnostic of Core).
        Strings.Culture = new CultureInfo("de");
        var signed = await OpenAsync("forms-signed.pdf");
        await signed.EnterFillAsync();
        var fields = signed.FieldsOfPage(0);
        var signature = fields.First(field => field.Kind == FormFieldKind.Signature);
        var button = fields.Single(field => field.Kind == FormFieldKind.PushButton);
        Assert.Equal(Strings.FormLockedSignature, TabViewModel.LockTextOf(signature));
        Assert.Equal(Strings.FormLockedButton, TabViewModel.LockTextOf(button));
        Assert.NotEqual(signature.LockReason, TabViewModel.LockTextOf(signature));
        Assert.False(await signed.CommitFieldAsync(button.Key.FieldName, FormFieldState.OfChecked(true)));
        Assert.False(signed.HasEditor);
        var order = await TabOrderAsync(signed);
        Assert.Equal(["Name"], order.Select(key => key.FieldName));
        Assert.All(fields.Where(field => !TabViewModel.IsEditable(field)), field => Assert.DoesNotContain(field.Key, order));

        // File selection, nameless, unknown kind.
        Assert.Equal(Strings.FormLockedFile, TabViewModel.LockTextOf(Field(FormFieldKind.Text, FormFieldTraits.FileSelect, "Datei")));
        Assert.Equal(Strings.FormLockedNameless, TabViewModel.LockTextOf(Field(FormFieldKind.Text, FormFieldTraits.None, string.Empty)));
        Assert.Equal(Strings.FormLockedOther, TabViewModel.LockTextOf(Field(FormFieldKind.Unknown, FormFieldTraits.None, "X")));
    }

    [AvaloniaFact]
    public async Task RefusalAfterTheModeEndedBringsTheModeBackWithTheText()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tab = await OpenAsync("forms-basic.pdf", configure: tab => tab.FormValueWriter = Refusing(gate.Task, "Name"));
        var refused = new List<string>();
        tab.FormValueRefused += refused.Add;
        await tab.EnterFillAsync();
        var name = Key(tab, "Name");

        // "Done" while the value is on its way: the mode ends at once, PDFium refuses afterwards.
        Assert.True(tab.EditDraft(name, "Neu"));
        Assert.True(tab.EndFill());
        Assert.False(tab.IsFilling);
        gate.SetResult();
        await tab.WhenEditsSettledAsync();

        // The mode comes back with the focus in the field, the text and the reason: nothing dropped unseen.
        Assert.True(tab.IsFilling);
        Assert.Equal(name, tab.FocusedField);
        Assert.Equal("Neu", tab.CurrentDraft!.Text);
        Assert.Equal(Strings.FormValueNotTaken, tab.CurrentDraft.Reason);
        Assert.True(tab.HasFormDraft);
        Assert.True(tab.AsksBeforeClosing);
        Assert.Empty(refused);

        // Switched to "Comment" meanwhile: that mode stays, the window is told, the text waits for the next time the mode starts.
        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.FormValueWriter = Refusing(gate.Task, "Name");
        Assert.True(tab.EditDraft(name, "Zwei"));
        tab.ToggleAnnotateCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsAnnotating);
        gate.SetResult();
        await tab.WhenEditsSettledAsync();
        Assert.True(tab.IsAnnotating);
        Assert.False(tab.IsFilling);
        Assert.Equal([Strings.FormValueNotTaken], refused);
        Assert.Equal("Zwei", tab.CurrentDraft!.Text);
        Assert.True(tab.AsksBeforeClosing);

        // Outside the mode it cannot be applied (the flush says so); back in the mode it can, and goes in.
        Assert.False(tab.FlushFormDraft());
        tab.FormValueWriter = static (editor, field, state) => editor.SetFormValueAsync(field, state);
        await tab.EnterFillAsync();
        Assert.False(tab.IsAnnotating);
        Assert.Equal(name, tab.FocusedField);
        Assert.True(tab.FlushFormDraft());
        await tab.WhenEditsSettledAsync();
        Assert.Equal("Zwei", await ValueAsync(tab, 0, "Name"));
        Assert.False(tab.HasFormDraft);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARefusalThatArrivesAfterAnotherDocumentOpenedLeavesNoDraftBehind(bool engineRefusal)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tab = await OpenAsync("forms-basic.pdf", configure: tab => tab.FormValueWriter = async (_, _, _) =>
        {
            await gate.Task;
            throw engineRefusal
                ? new FormWriteException("refused", new HashSet<int> { 0 }, restored: true)
                : new InvalidOperationException("refused");
        });
        var refused = new List<string>();
        tab.FormValueRefused += refused.Add;
        await tab.EnterFillAsync();
        var name = Key(tab, "Name");
        Assert.True(tab.EditDraft(name, "Neu"));
        Assert.True(tab.FlushFormDraft());
        var first = tab.Document;

        // Another document replaces the first one while the value is on its way (the draft belongs to the old document).
        await tab.OpenAsync(Copy("forms-basic.pdf"));
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null && !ReferenceEquals(tab.Document, first));
        Assert.False(tab.IsFilling);
        Assert.False(tab.HasFormDraft);

        // The refusal of the old write arrives late: it neither brings the draft back nor starts the mode again nor tells the window.
        gate.SetResult();
        await tab.WhenEditsSettledAsync();
        Pump();
        Assert.False(tab.HasFormDraft);
        Assert.Null(tab.CurrentDraft);
        Assert.False(tab.IsFilling);
        Assert.False(tab.AsksBeforeClosing);
        Assert.Empty(refused);
        Assert.Null(tab.FormNotice);
    }

    [AvaloniaFact]
    public async Task RefusedValueOfAFieldLeftMeanwhileComesBackWithTheField()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tab = await OpenAsync("forms-basic.pdf", configure: tab => tab.FormValueWriter = Refusing(gate.Task, "Name"));
        var refused = new List<string>();
        tab.FormValueRefused += refused.Add;
        await tab.EnterFillAsync();
        var name = Key(tab, "Name");
        var remark = Key(tab, "Bemerkung");

        // Tab to the next field and type there while the first value is still on its way; then PDFium refuses it.
        tab.FocusedField = name;
        Assert.True(tab.EditDraft(name, "Erster"));
        Assert.True(await tab.MoveToFieldAsync(forward: true));
        Assert.Equal(remark, tab.FocusedField);
        Assert.True(tab.EditDraft(remark, "Zweiter"));
        gate.SetResult();
        await WaitUntilAsync(() => !tab.HasEditsInFlight);

        // The open field keeps its text; the refused one is kept as well, counted and announced.
        Assert.Equal(remark, tab.CurrentDraft!.Key);
        Assert.Equal([Strings.FormValueNotTaken], refused);
        Assert.True(tab.HasFormDraft);
        Assert.True(tab.AsksBeforeClosing);

        // Back in that field: its text and reason are there again; the other field's text went in on the way.
        Assert.True(await tab.MoveToFieldAsync(forward: false));
        await tab.WhenEditsSettledAsync();
        Assert.Equal(name, tab.FocusedField);
        Assert.Equal("Erster", tab.CurrentDraft!.Text);
        Assert.Equal(Strings.FormValueNotTaken, tab.CurrentDraft.Reason);
        Assert.Equal("Zweiter", await ValueAsync(tab, 0, "Bemerkung"));

        // Esc drops it on purpose; then nothing is left.
        tab.DiscardDraft();
        Assert.Null(tab.CurrentDraft);
        Assert.False(tab.HasFormDraft);

        // At most a few refused texts are kept: the oldest goes, said aloud.
        tab.MaxKeptFormDrafts = 1;
        refused.Clear();
        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.FormValueWriter = Refusing(gate.Task, "Kamm", "Kurz", "Pflicht");
        foreach (var field in new[] { "Kamm", "Kurz", "Pflicht" })
        {
            Assert.True(tab.EditDraft(Key(tab, field), field + "!"));
            Assert.True(tab.FlushFormDraft());
        }

        Assert.True(tab.EditDraft(Key(tab, "Passwort"), "offen"));
        gate.SetResult();
        await WaitUntilAsync(() => !tab.HasEditsInFlight);
        // Three refusals, two of them pushed out again (each said); the newest refused text is there for its field.
        Assert.Equal(5, refused.Count);
        Assert.Equal("offen", tab.CurrentDraft!.Text);
        tab.DiscardDraft();
        Assert.True(tab.HasFormDraft);
        tab.FocusedField = Key(tab, "Pflicht");
        Assert.Equal("Pflicht!", tab.CurrentDraft!.Text);
        tab.DiscardDraft();
        tab.FocusedField = Key(tab, "Kurz");
        Assert.Null(tab.CurrentDraft);
        tab.FocusedField = Key(tab, "Kamm");
        Assert.Null(tab.CurrentDraft);
        Assert.False(tab.HasFormDraft);
    }

    [AvaloniaFact]
    public async Task ClosingWithARefusedValueStopsAtTheFieldInsteadOfAskingAgain()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached());
        _cleanup.Add(viewModel);
        await viewModel.OpenAsync(Copy("forms-basic.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null);
        tab.FormValueWriter = Refusing(Task.CompletedTask, "Name");
        tab.HasShownSharpPage = true;
        await tab.EnterFillAsync();
        var cards = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PendingSave) && viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges })
            {
                cards++;
            }
        };

        // A value PDFium refuses: closing asks; "Save" cannot write it, so the flow stops at the field instead of asking again.
        Assert.True(tab.EditDraft(Key(tab, "Name"), "Nein"));
        viewModel.CloseTabCommand.Execute(tab);
        await WaitUntilAsync(() => viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges });
        Assert.True(tab.AsksBeforeClosing);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Save);
        await WaitUntilAsync(() => viewModel.PendingSave is null && !viewModel.IsResolving);
        for (var i = 0; i < 20; i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.Equal(1, cards);
        Assert.Null(viewModel.PendingSave);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.True(tab.IsFilling);
        Assert.Equal(Key(tab, "Name"), tab.FocusedField);
        Assert.Equal("Nein", tab.CurrentDraft!.Text);
        Assert.Equal(Strings.FormValueNotTaken, tab.CurrentDraft.Reason);

        // "Don't save" closes it, with the text.
        viewModel.CloseTabCommand.Execute(tab);
        await WaitUntilAsync(() => viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges });
        viewModel.PendingSave!.Resolve(PendingSaveChoice.DontSave);
        await WaitUntilAsync(() => viewModel.Tabs.Count == 0);
    }

    [AvaloniaFact]
    public async Task NothingCanBeTypedWhileEditingIsFrozen()
    {
        var tab = await OpenAsync("forms-basic.pdf");
        await tab.EnterFillAsync();
        tab.IsEditingFrozen = true;

        Assert.False(tab.EditDraft(Key(tab, "Name"), "Neu"));
        Assert.Null(tab.CurrentDraft);
        Assert.False(tab.AsksBeforeClosing);

        tab.IsEditingFrozen = false;
        Assert.True(tab.EditDraft(Key(tab, "Name"), "Neu"));
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public void NoUiTextCallsAnythingLegallyBinding(string language)
    {
        Strings.Culture = new CultureInfo(language);

        var texts = FormTexts();

        Assert.True(texts.Count >= 25);
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        foreach (var text in texts)
        {
            Assert.DoesNotContain("rechtsgültig", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legally", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(ValidWord(), text);
        }
    }

    /// <summary>
    /// Every UI text of the forms (milestone 11) in the current language: the strings named <c>Form…</c> and what the tab
    /// makes of them (hints, locks, reasons); <see cref="SaveCardsTests.NoUiTextCallsAnythingLegallyBinding"/> checks them too.
    /// </summary>
    internal static IReadOnlyList<string> FormTexts()
    {
        var texts = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string) && property.Name.StartsWith("Form", StringComparison.Ordinal))
            .Select(property => (string)property.GetValue(null)!)
            .ToList();
        texts.AddRange(new[] { FormHint.Calculated, FormHint.FormatScripts, FormHint.Xfa, FormHint.SignatureField, FormHint.Buttons }.Select(TabViewModel.HintTextOf));
        foreach (var kind in Enum.GetValues<FormFieldKind>())
        {
            texts.Add(TabViewModel.LockTextOf(Field(kind, FormFieldTraits.FileSelect, "X")) ?? string.Empty);
            texts.Add(TabViewModel.LockTextOf(Field(kind, FormFieldTraits.None, string.Empty)) ?? string.Empty);
            texts.Add(TabViewModel.LockTextOf(Field(kind, FormFieldTraits.ReadOnly, "X", FormLock.ReadOnly)) ?? string.Empty);
        }

        var field = Field(FormFieldKind.Text, FormFieldTraits.None, "X", FormLock.None, maxLength: 2);
        foreach (var typed in new[] { "„x“", "abc", "a\nb", "a\tb" })
        {
            texts.Add(TabViewModel.ProblemTextOf(FormValueRules.CheckText(field, typed)) ?? string.Empty);
        }

        return texts;
    }

    [GeneratedRegex(@"(?<![\p{L}])(valid|gültig)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValidWord();

    private static FormField Field(FormFieldKind kind, FormFieldTraits traits, string name, FormLock fieldLock = FormLock.Unsupported, int? maxLength = null) =>
        new(new FormWidgetKey(name, 0, 0), 0, kind, traits, new PageRect(0, 0, 10, 10), null, null, maxLength, [], FormFieldState.OfText(null), fieldLock, "diagnostic");

    private static string Format(string format, object argument) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), argument);

    /// <summary>The key of the first widget of a field on page 1 as the tab read it.</summary>
    private static FormWidgetKey Key(TabViewModel tab, string fieldName) =>
        tab.FieldsOfPage(0).First(field => field.Key.FieldName == fieldName).Key;

    /// <summary>The tab order of the whole document, as Tab walks it from the start.</summary>
    private static async Task<List<FormWidgetKey>> TabOrderAsync(TabViewModel tab)
    {
        var order = new List<FormWidgetKey>();
        tab.FocusedField = null;
        while (await tab.MoveToFieldAsync(forward: true))
        {
            order.Add(tab.FocusedField!.Value);
        }

        return order;
    }

    /// <summary>The value of a field as the document of the tab has it now (read directly, not through the tab).</summary>
    private static async Task<string?> ValueAsync(TabViewModel tab, int page, string fieldName) =>
        (await tab.Document!.GetFormFieldsAsync(page, TestContext.Current.CancellationToken)).First(field => field.Key.FieldName == fieldName).State.Text;

    /// <summary>A write that refuses the named fields (after <paramref name="gate"/>, as PDFium would: restored) and passes the others on.</summary>
    private static Func<AnnotationEditor, string, FormFieldState, Task<FormChange?>> Refusing(Task gate, params string[] names) =>
        async (editor, field, state) =>
        {
            if (names.Contains(field))
            {
                await gate;
                throw new FormWriteException("refused", new HashSet<int> { 0 }, restored: true);
            }

            return await editor.SetFormValueAsync(field, state);
        };

    private static Func<PdfDocument, int, CancellationToken, Task<IReadOnlyList<FormField>>> Recording(List<int> reads) =>
        (document, page, token) =>
        {
            reads.Add(page);
            return document.GetFormFieldsAsync(page, token);
        };

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Pump();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The tab did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private string Copy(string name, string? copyName = null)
    {
        var path = Path.Combine(_folder, copyName ?? Guid.NewGuid().ToString("N") + "-" + name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    /// <summary>
    /// Opens a copy of a test file in a new tab without a window and, unless <paramref name="sharp"/> is false, marks its
    /// first sharp page as shown (as the document view does).
    /// </summary>
    private async Task<TabViewModel> OpenAsync(string name, Action<TabViewModel>? configure = null, bool sharp = true)
    {
        var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(tab);
        tab.AnnotationSettings = UserSettings.Detached();
        configure?.Invoke(tab);
        await tab.OpenAsync(Copy(name));
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null);
        tab.HasShownSharpPage = sharp;
        return tab;
    }
}
