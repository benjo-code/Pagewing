using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.Core.Saving;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Saving filled-in forms (milestone 11, task 13): the values go through the save pipeline of milestone 9 together with the
/// annotations, in one save; the card about empty required fields (default "Go to the first empty field", never blocking);
/// the signature warning of a form-only change; and that closing, printing and the update take the open value of a field
/// first and wait for it. Every file is a copy in a temp folder; network and installer are fakes; settings are in memory.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed partial class FormSaveTests : UpdateTestHost
{
    private static readonly UpdateVersion Offered = new(0, 2, 0);

    private readonly SaveCardHost _files = new();

    public FormSaveTests() => Own(_files);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task SaveWithEmptyRequiredFieldsAsksAndDefaultsToGoingToTheField()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);

        // A value outside the mode's current focus, then the mode ends: "Pflicht" stays empty.
        await tab.EnterFillAsync();
        Assert.True(await tab.CommitFieldAsync("Kurz", FormFieldState.OfText("abc")));
        Assert.True(tab.EndFill());
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.IsDirty);

        var save = viewModel.SaveActiveTabAsync();
        var card = await SaveCardHost.WaitForCardAsync<RequiredFieldsCard>(window, viewModel, PendingSaveKind.RequiredFields);
        var question = viewModel.PendingSave!;

        // The number and the first field by name; "Go to the first empty field" is the primary button with the focus.
        Assert.Equal(["Pflicht"], question.EmptyFieldNames);
        Assert.Equal(Strings.RequiredFieldsTitleOne, Find<TextBlock>(card, "RequiredFieldsTitle").Text);
        Assert.Equal("“Pflicht” has no value yet. Fill it in first, or save anyway.", Find<TextBlock>(card, "RequiredFieldsText").Text);
        Assert.Equal([Strings.SaveChoiceGoToField, Strings.SaveChoiceSaveAnyway], SaveCardHost.ShownButtons(card));
        Assert.Equal(PendingSaveChoice.GoToField, question.DefaultChoice);
        Assert.Contains("primary", card.ButtonOf(PendingSaveChoice.GoToField)!.Classes);
        Assert.True(card.ButtonOf(PendingSaveChoice.GoToField)!.IsFocused);
        Assert.False(question.IsWarning);

        // Enter: nothing is saved; the mode starts with the focus and the input in the empty field.
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.False(await save);
        Assert.True(tab.IsDirty);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct));
        await WaitUntilAsync(window, () =>
            tab.IsFilling && tab.FocusedField?.FieldName == "Pflicht"
            && view.FormFieldEditor is { Field: { } field } editor && field.Key.FieldName == "Pflicht" && editor.IsKeyboardFocusWithin);
        Assert.Null(viewModel.PendingSave);

        // Two empty fields: the title counts them, the text names the first.
        Strings.Culture = new CultureInfo("de");
        var many = PendingSave.RequiredFields(tab, path, ["Pflicht", "Zustimmung"], "Bitte ausfüllen");
        Assert.Equal("2 Pflichtfelder sind leer", many.Title);
        Assert.Equal("Das erste ist „Bitte ausfüllen“. Füllen Sie die Felder zuerst aus, oder speichern Sie trotzdem.", many.Message);
        Assert.Equal([PendingSaveChoice.GoToField, PendingSaveChoice.SaveAnyway], many.Choices);
        Assert.Equal("Zum ersten leeren Feld", PendingSave.LabelOf(PendingSaveChoice.GoToField));
    }

    [AvaloniaFact]
    public async Task SaveAnywaySavesTheFilledValues()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        await tab.EnterFillAsync();
        Assert.True(await tab.CommitFieldAsync("Kurz", FormFieldState.OfText("abc")));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.IsDirty);

        // The first write fails: "Save anyway" was given for this state of the changes, so the retry does not ask again.
        var write = viewModel.DocumentWriter;
        var failures = 1;
        viewModel.DocumentWriter = (editor, engine, target, overwrite, progress, cancellationToken) =>
            failures-- > 0 ? throw new SaveFailureException(SaveFailureReason.WriteFailed, "fails once") : write(editor, engine, target, overwrite, progress, cancellationToken);
        var asked = RecordKinds(viewModel);

        var save = viewModel.SaveActiveTabAsync();
        var card = await SaveCardHost.WaitForCardAsync<RequiredFieldsCard>(window, viewModel, PendingSaveKind.RequiredFields);
        var anyway = card.ButtonOf(PendingSaveChoice.SaveAnyway)!;
        Assert.False(anyway.IsEffectivelyEnabled);
        _files.PassTheLock(window);
        Assert.True(anyway.IsEffectivelyEnabled);
        Click(window, anyway);
        await SaveCardHost.WaitForCardAsync<SaveErrorCard>(window, viewModel, PendingSaveKind.SaveError);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);

        Assert.True(await viewModel.SaveActiveTabAsync().WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Equal([PendingSaveKind.RequiredFields, PendingSaveKind.SaveError], asked);
        Assert.False(tab.IsDirty);
        await using (var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: Ct))
        {
            Assert.Equal("abc", (await FieldAsync(reopened, "Kurz")).State.Text);
            Assert.Equal(string.Empty, (await FieldAsync(reopened, "Pflicht")).State.Text ?? string.Empty);
        }

        // A new change asks again, and "Save as" asks the same way.
        Assert.True(await tab.CommitFieldAsync("Kurz", FormFieldState.OfText("xyz")));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.IsDirty);
        var target = Path.Combine(_files.Folder, "copy.pdf");
        _files.Picker.Result = target;
        var saveAs = viewModel.SaveActiveTabAsAsync();
        await SaveCardHost.WaitForCardAsync<RequiredFieldsCard>(window, viewModel, PendingSaveKind.RequiredFields);
        _files.PassTheLock(window);
        Assert.True(viewModel.PendingSave!.Choose(PendingSaveChoice.SaveAnyway));
        Assert.True(await saveAs);
        Assert.Equal(target, tab.FullPath);
        await using var copy = await new PdfEngine().OpenAsync(target, cancellationToken: Ct);
        Assert.Equal("xyz", (await FieldAsync(copy, "Kurz")).State.Text);
    }

    [AvaloniaFact]
    public async Task NoRequiredFieldsMeansNoCard()
    {
        var (window, viewModel, view) = _files.Show();
        var reads = 0;
        var read = viewModel.RequiredFieldsReader;
        viewModel.RequiredFieldsReader = (document, cancellationToken) =>
        {
            reads++;
            return read(document, cancellationToken);
        };
        var asked = RecordKinds(viewModel);

        // A document without a form: nothing of the form is read.
        var (plain, _) = await _files.OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await SaveCardHost.MarkAsync(window, plain);
        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.Equal(0, reads);

        // A form whose required field is filled: read, no card.
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        await tab.EnterFillAsync();
        Assert.True(await tab.CommitFieldAsync("Pflicht", FormFieldState.OfText("Bo")));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.IsDirty);
        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.Equal(1, reads);
        Assert.Empty(asked);
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: Ct);
        Assert.Equal("Bo", (await FieldAsync(reopened, "Pflicht")).State.Text);
    }

    [AvaloniaFact]
    public async Task OpenDraftIsCommittedBeforeTheSave()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        var asked = RecordKinds(viewModel);

        // Typed into the required field and not left: the save applies it first, so the field is no longer empty and no card asks.
        await TypeDraftAsync(window, tab, "Pflicht", "Bo");
        Assert.True(tab.HasFormDraft);
        Assert.True(tab.IsDirty);

        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.Empty(asked);
        Assert.False(tab.HasFormDraft);
        Assert.False(tab.IsDirty);
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: Ct);
        Assert.Equal("Bo", (await FieldAsync(reopened, "Pflicht")).State.Text);
    }

    [AvaloniaFact]
    public async Task SaveKeepsAnnotationsAndValuesInOneSave()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        var annotations = (await tab.Document!.GetAnnotationsAsync(0, Ct)).Count;
        var write = viewModel.DocumentWriter;
        var writes = 0;
        viewModel.DocumentWriter = (editor, engine, target, overwrite, progress, cancellationToken) =>
        {
            writes++;
            return write(editor, engine, target, overwrite, progress, cancellationToken);
        };

        // A markup and two values (one of them the required field), one history.
        await SaveCardHost.MarkAsync(window, tab);
        await tab.EnterFillAsync();
        Assert.True(await tab.CommitFieldAsync("Pflicht", FormFieldState.OfText("Bo")));
        Assert.True(await tab.CommitFieldAsync("Agb", FormFieldState.OfChecked(true)));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.False(tab.Editor.HasOnlyFormChanges);

        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.Equal(1, writes);
        Assert.False(tab.IsDirty);
        Assert.False(tab.CanUndo);

        var saved = await File.ReadAllBytesAsync(path, Ct);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "The original bytes were changed.");
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: Ct);
        Assert.Equal(annotations + 1, (await reopened.GetAnnotationsAsync(0, Ct)).Count);
        Assert.Equal("Bo", (await FieldAsync(reopened, "Pflicht")).State.Text);
        Assert.Equal(FormFieldState.OfChecked(true), (await FieldAsync(reopened, "Agb")).State);
    }

    [AvaloniaFact]
    public async Task SignedFormAtLevelTwoStillWarnsAndNeverSaysValid()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-signed.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        await tab.EnterFillAsync();
        Assert.True(await tab.CommitFieldAsync("Name", FormFieldState.OfText("Bo")));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.IsDirty);
        Assert.True(tab.Editor.HasOnlyFormChanges);

        // Only form values: still the warning with "Cancel" as default, with the more precise text, never "valid".
        var save = viewModel.SaveActiveTabAsync();
        var card = await SaveCardHost.WaitForCardAsync<SignatureWarningCard>(window, viewModel, PendingSaveKind.SignatureWarning);
        Assert.Equal(DocMdpLevel.FormsOnly, viewModel.PendingSave!.Level);
        Assert.Equal(Strings.SignatureWarningTitle, Find<TextBlock>(card, "SignatureTitle").Text);
        Assert.Equal(Strings.SignatureWarningFormsText, Find<TextBlock>(card, "SignatureText").Text);
        Assert.Equal([Strings.SaveChoiceCancel, Strings.SaveChoiceSaveAs, Strings.SaveChoiceSaveAnyway], SaveCardHost.ShownButtons(card));
        Assert.True(card.ButtonOf(PendingSaveChoice.Cancel)!.IsFocused);
        AssertNeverValid(card);
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.False(await save);
        Assert.True(tab.IsDirty);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct));

        // With a markup as well, the general text.
        await SaveCardHost.MarkAsync(window, tab);
        save = viewModel.SaveActiveTabAsync();
        card = await SaveCardHost.WaitForCardAsync<SignatureWarningCard>(window, viewModel, PendingSaveKind.SignatureWarning);
        Assert.Equal(Strings.SignatureWarningText, Find<TextBlock>(card, "SignatureText").Text);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);

        // Level 1 keeps its text even for form values; the German texts say nothing is valid either.
        Assert.Equal(Strings.SignatureWarningText, PendingSave.SignatureWarning(tab, path, DocMdpLevel.NoChanges, formsOnly: true).Message);
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            foreach (var question in new[]
            {
                PendingSave.SignatureWarning(tab, path, DocMdpLevel.FormsOnly, formsOnly: true),
                PendingSave.RequiredFields(tab, path, ["Pflicht"], "Pflicht"),
                PendingSave.RequiredFields(tab, path, ["Pflicht", "Name"], "Pflicht"),
            })
            {
                foreach (var text in new[] { question.Title, question.Message }.Concat(question.Choices.Select(PendingSave.LabelOf)))
                {
                    Assert.DoesNotMatch(ValidWord(), text);
                    Assert.DoesNotContain("legally", text, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("rechtsgültig", text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingAsksAboutAnUnsavedFormValue()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");

        // A value typed and not left yet: closing applies it and asks.
        await TypeDraftAsync(window, tab, "Pflicht", "Bo");
        viewModel.CloseActiveTabCommand.Execute(null);
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.False(tab.HasFormDraft);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Equal("Bo", (await FieldAsync(tab.Document!, "Pflicht")).State.Text);

        // "Save": written, then the tab closes.
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Save);
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 0);
        await using (var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: Ct))
        {
            Assert.Equal("Bo", (await FieldAsync(reopened, "Pflicht")).State.Text);
        }

        // An applied value asks as well; "Don't save" closes without writing.
        var (other, otherPath) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf", "other.pdf");
        var before = await File.ReadAllBytesAsync(otherPath, Ct);
        await other.EnterFillAsync();
        Assert.True(await other.CommitFieldAsync("Agb", FormFieldState.OfChecked(true)));
        viewModel.CloseActiveTabCommand.Execute(null);
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        _files.PassTheLock(window);
        Assert.True(viewModel.PendingSave!.Choose(PendingSaveChoice.DontSave));
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 0);
        Assert.Equal(before, await File.ReadAllBytesAsync(otherPath, Ct));
    }

    [AvaloniaFact]
    public async Task PrintingTakesTheDraftAndPrintsTheValue()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        _files.Print.SystemDialog = true;
        _files.Print.DialogTicket = new PrintTicket(new PrintSettings { Printer = "Plotter" });
        string? printed = null;
        _files.Print.OnPrint = async (job, _, cancellationToken) =>
            printed = (await FieldAsync(job.Document, "Kurz")).State.Text;

        // The write of the value takes a moment: the job waits for it instead of printing the value the field had.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = tab.FormValueWriter;
        tab.FormValueWriter = async (editor, name, state) =>
        {
            await gate.Task;
            return await writer(editor, name, state);
        };
        await TypeDraftAsync(window, tab, "Kurz", "abc");

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsPrinting || _files.Print.Jobs.Count > 0);
        Assert.Null(printed);
        gate.SetResult();
        await WaitUntilAsync(window, () => !tab.IsPrinting && _files.Print.Jobs.Count == 1 && printed is not null);

        // The value is in the document that was printed, unsaved: the job got no path (the file lacks the value).
        Assert.Equal("abc", printed);
        Assert.Null(_files.Print.Jobs[0].Path);
        Assert.False(tab.HasFormDraft);
        Assert.True(tab.IsDirty);
        await using var onDisk = await new PdfEngine().OpenAsync(path, cancellationToken: Ct);
        Assert.Equal(string.Empty, (await FieldAsync(onDisk, "Kurz")).State.Text ?? string.Empty);
    }

    [AvaloniaFact]
    public async Task UpdateAsksAboutAnUnsavedFormValue()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (viewModel, window, tab) = await WindowWithFormAsync(workspace);
        await TypeDraftAsync(window, tab, "Pflicht", "Bo");

        // "Install": the open value is applied first, and the window asks about it.
        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.Same(tab, viewModel.PendingSave!.Tab);
        Assert.False(tab.HasFormDraft);
        Assert.Equal(0, Source.DownloadCalls);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Save);
        await WaitUntilAsync(window, () => install.IsCompleted);

        Assert.Equal(1, Applier.Calls);
        Assert.False(tab.IsDirty);
        await using var reopened = await new PdfEngine().OpenAsync(tab.FullPath!, cancellationToken: Ct);
        Assert.Equal("Bo", (await FieldAsync(reopened, "Pflicht")).State.Text);
    }

    [AvaloniaFact]
    public async Task EditingIsFrozenDuringTheUpdateForForms()
    {
        var (workspace, coordinator) = await WorkspaceAsync();
        var (viewModel, window, tab) = await WindowWithFormAsync(workspace);
        await tab.EnterFillAsync();
        Assert.True(await tab.CommitFieldAsync("Pflicht", FormFieldState.OfText("Bo")));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.IsDirty);
        var path = tab.FullPath!;
        var original = await File.ReadAllBytesAsync(path, Ct);

        Applier.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Applier.Result = ApplyResult.Failed("The folder is read-only.");
        var install = coordinator.BeginInstallAsync();
        await SaveCardHost.WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        viewModel.PendingSave!.Resolve(PendingSaveChoice.DontSave);
        await WaitUntilAsync(window, () => Applier.Calls == 1 && tab.IsEditingFrozen);

        // While the installer has it: no value is typed, applied or toggled, nothing is saved.
        var version = tab.EditVersion;
        var kurz = tab.FieldsOfPage(0).First(field => field.Key.FieldName == "Kurz").Key;
        tab.FocusedField = kurz;
        Assert.False(tab.EditDraft(kurz, "x"));
        Assert.False(await tab.CommitFieldAsync("Kurz", FormFieldState.OfText("x")));
        Assert.False(await tab.CommitFieldAsync("Agb", FormFieldState.OfChecked(true)));
        Assert.False(tab.UndoCommand.CanExecute(null));
        Assert.False(await viewModel.SaveActiveTabAsync());
        Settle(window);
        Assert.Equal(version, tab.EditVersion);
        Assert.False(tab.HasEditsInFlight);
        Assert.Equal(string.Empty, (await FieldAsync(tab.Document!, "Kurz")).State.Text ?? string.Empty);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct));

        // The installer fails: the fields take values again.
        Applier.Gate.SetResult();
        await WaitUntilAsync(window, () => install.IsCompleted && !tab.IsEditingFrozen);
        Assert.True(await tab.CommitFieldAsync("Kurz", FormFieldState.OfText("x")));
        Assert.Equal("x", (await FieldAsync(tab.Document!, "Kurz")).State.Text);
    }

    [AvaloniaFact]
    public async Task FieldsStayFillableAfterTheDocumentIsAdopted()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        await TypeDraftAsync(window, tab, "Pflicht", "Bo");
        var old = tab.Document!;

        // The real save: the session adopts the written file.
        Assert.True(await viewModel.SaveActiveTabAsync());
        await WaitUntilAsync(window, () => tab.Document is { } document && !ReferenceEquals(document, old) && ReferenceEquals(view.Document, document));
        Assert.True(tab.IsFilling);
        Assert.False(tab.CanUndo);

        // Typed on the page into another field and left: written into the new document, one step to undo.
        await WaitUntilAsync(window, () => tab.FieldsOfPage(0).Count > 0 && view.FormOverlay is { IsVisible: true, Fields.Count: > 0 });
        var kurz = tab.FieldsOfPage(0).First(field => field.Key.FieldName == "Kurz");
        ClickField(window, view, kurz);
        var editor = view.FormFieldEditor ?? throw new InvalidOperationException("No input opened after the save.");
        Assert.Equal(kurz.Key, editor.Field!.Key);
        Assert.Same(tab.FieldOf(kurz.Key), editor.Field);
        window.KeyTextInput("neu");
        Settle(window);
        Click(window, ScreenPoint(view, window, 0, new Point(395, 5)));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.CanUndo);
        Assert.Equal("neu", (await FieldAsync(tab.Document!, "Kurz")).State.Text);
        Assert.True(tab.IsDirty);

        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && !tab.CanUndo);
        Assert.Equal(string.Empty, (await FieldAsync(tab.Document!, "Kurz")).State.Text ?? string.Empty);
        Assert.Equal("Bo", (await FieldAsync(tab.Document!, "Pflicht")).State.Text);
        Assert.NotNull(path);
    }

    [AvaloniaFact]
    public async Task RequiredFieldsCardIsNamedAndKeyboardOperable()
    {
        var (window, viewModel, view) = _files.Show();
        Assert.DoesNotContain(window.SaveCards, card => card is RequiredFieldsCard);
        var (tab, _) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        await tab.EnterFillAsync();
        Assert.True(await tab.CommitFieldAsync("Kurz", FormFieldState.OfText("abc")));
        Assert.True(tab.EndFill());
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.IsDirty);
        Assert.DoesNotContain(window.SaveCards, card => card is RequiredFieldsCard);

        var save = viewModel.SaveActiveTabAsync();
        var card = await SaveCardHost.WaitForCardAsync<RequiredFieldsCard>(window, viewModel, PendingSaveKind.RequiredFields);
        var question = viewModel.PendingSave!;

        // Named by its title, described by its text, a polite live region; the symbol beside the text; buttons named by their text.
        var peer = ControlAutomationPeer.CreatePeerForElement(card);
        Assert.Equal(question.Title, peer.GetName());
        Assert.Equal(question.Message, AutomationProperties.GetHelpText(card));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Find<TextBlock>(card, "RequiredFieldsTitle")));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Find<TextBlock>(card, "RequiredFieldsText")));
        Assert.True(Find<StrokeIcon>(card, "RequiredFieldsIcon").IsEffectivelyVisible);
        foreach (var button in card.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible))
        {
            Assert.Equal(button.Content, ControlAutomationPeer.CreatePeerForElement(button).GetName());
        }

        // Tab stays on the card; "Save anyway" is skipped while it is locked.
        var goTo = card.ButtonOf(PendingSaveChoice.GoToField)!;
        var anyway = card.ButtonOf(PendingSaveChoice.SaveAnyway)!;
        Assert.True(goTo.IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.True(goTo.IsFocused);
        _files.PassTheLock(window);
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.True(anyway.IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.True(goTo.IsFocused);

        // The page keys of the document do nothing behind the card.
        var page = tab.CurrentPage;
        Press(window, Key.PageDown, PhysicalKey.PageDown, None);
        Assert.Equal(page, tab.CurrentPage);

        // Esc cancels the save like on every card: nothing written, no mode, the keys back in the document.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(PendingSaveChoice.Cancel, await question.Choice.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.False(await save);
        await WaitUntilAsync(window, () => viewModel.PendingSave is null && !card.IsVisible);
        Assert.False(tab.IsFilling);
        Assert.True(tab.IsDirty);
        Assert.True(view.IsKeyboardFocusWithin);
        Assert.IsType<RequiredFieldsCard>(Assert.Single(window.SaveCards));
    }

    [AvaloniaFact]
    public async Task SavingAMarkupOfAFormDocumentAppendsOnlyWhatPdfiumWrites()
    {
        var (window, viewModel, view) = _files.Show();
        var (tab, path) = await _files.OpenCopyAsync(window, viewModel, view, "forms-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);

        // Viewed (the first page drawn with its widgets, the form read for its hints after the first sharp page, which loads
        // every page with fields) and a markup; no value changed.
        await tab.EnsureFormInfoAsync();
        await SaveCardHost.MarkAsync(window, tab);
        var saved = await tab.Editor.SaveAsync(new PdfEngine(), path, overwriteChangedOriginal: false, progress: null, Ct);
        Assert.True(saved.Adopted);

        var bytes = await File.ReadAllBytesAsync(path, Ct);
        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original), "The original bytes were changed.");
        var increment = Encoding.Latin1.GetString(bytes, original.Length, bytes.Length - original.Length);

        // Pinned, not wanted: loading a page with the form environment gives every widget without an appearance one (in memory),
        // and PDFium's incremental save writes every loaded object again. So all 21 widgets of the file (both pages) are
        // appended with their generated appearance streams, next to the one highlight: 16 form XObjects in all (the widgets'
        // new appearances and the highlight's). A change of these numbers after a PDFium update is a finding, not a test to
        // loosen.
        Assert.Equal(21, WidgetObject().Count(increment));
        Assert.Equal(1, HighlightObject().Count(increment));
        Assert.Equal(16, FormXObject().Count(increment));

        // The values stay as they were.
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: Ct);
        Assert.Equal("Alt", (await FieldAsync(reopened, "Name")).State.Text);
        Assert.Equal(string.Empty, (await FieldAsync(reopened, "Kurz")).State.Text ?? string.Empty);
    }

    private static void AssertNeverValid(Control card)
    {
        foreach (var text in card.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).Append(AutomationProperties.GetName(card)))
        {
            Assert.DoesNotMatch(ValidWord(), text ?? string.Empty);
        }
    }

    private static async Task<FormField> FieldAsync(PdfDocument document, string name)
    {
        for (var page = 0; page < document.PageCount; page++)
        {
            if ((await document.GetFormFieldsAsync(page, Ct)).FirstOrDefault(field => field.Key.FieldName == name) is { } found)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"No field {name}.");
    }

    /// <summary>Starts the mode, gives <paramref name="name"/> the focus and types <paramref name="text"/> as its open value (not left).</summary>
    private static async Task TypeDraftAsync(Window window, TabViewModel tab, string name, string text)
    {
        await tab.EnterFillAsync();
        await WaitUntilAsync(window, () => tab.IsFilling && tab.FieldsOfPage(0).Count > 0);
        var key = tab.FieldsOfPage(0).First(field => field.Key.FieldName == name).Key;
        tab.FocusedField = key;
        Settle(window);
        Assert.True(tab.EditDraft(key, text));
        Settle(window);
        Assert.True(tab.HasFormDraft);
    }

    /// <summary>Records the kind of every question of the save flow of <paramref name="viewModel"/>.</summary>
    private static List<PendingSaveKind> RecordKinds(MainViewModel viewModel)
    {
        var asked = new List<PendingSaveKind>();
        PendingSave? last = null;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PendingSave) && viewModel.PendingSave is { } question && !ReferenceEquals(question, last))
            {
                last = question;
                asked.Add(question.Kind);
            }
        };
        return asked;
    }

    /// <summary>Clicks the middle of a field, scrolled into view first.</summary>
    private static void ClickField(Window window, DocumentView view, FormField field)
    {
        if (view.DataContext is TabViewModel tab)
        {
            tab.RevealArea(field.Key.Page, field.Bounds, fromPanel: false);
        }

        Settle(window);
        var point = ScreenPoint(view, window, field.Key.Page, new Point((field.Bounds.Left + field.Bounds.Right) / 2, (field.Bounds.Top + field.Bounds.Bottom) / 2));
        Click(window, point);
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

    // "valid" or "gültig" as a word of its own ("invalid", "ungültig" do not match).
    [GeneratedRegex(@"(?<![\p{L}])(valid|gültig)(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValidWord();

    // A widget annotation dictionary in the text of a PDF.
    [GeneratedRegex(@"/Subtype\s*/Widget\b")]
    private static partial Regex WidgetObject();

    // A highlight annotation dictionary in the text of a PDF.
    [GeneratedRegex(@"/Subtype\s*/Highlight\b")]
    private static partial Regex HighlightObject();

    // A form XObject (an appearance stream) in the text of a PDF.
    [GeneratedRegex(@"/Subtype\s*/Form\b")]
    private static partial Regex FormXObject();

    /// <summary>A workspace whose windows share one coordinator over the fake source (offering 0.2.0, already found).</summary>
    private async Task<(Workspace Workspace, UpdateCoordinator Coordinator)> WorkspaceAsync()
    {
        Source.Offer("0.2.0");
        UpdateCoordinator? made = null;
        var workspace = new Workspace(
            () => new ViewerSession(new PdfEngine()),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => _files.Picker,
            updateFactory: () => made ??= NewCoordinator());
        Own(workspace);
        var coordinator = workspace.UpdateCoordinator!;
        await coordinator.CheckNowAsync();
        await PumpUntilAsync(() => coordinator.State == UpdateState.Offer(Offered));
        return (workspace, coordinator);
    }

    /// <summary>A window of <paramref name="workspace"/> with a copy of <c>forms-basic.pdf</c>.</summary>
    private async Task<(MainViewModel ViewModel, MainWindow Window, TabViewModel Tab)> WindowWithFormAsync(Workspace workspace)
    {
        var viewModel = workspace.OpenWindow();
        viewModel.Clock = _files.Clock;
        var window = (MainWindow)workspace.WindowOf(viewModel);
        var view = Find<DocumentView>(window, "DocumentView");
        var tab = await SaveCardHost.OpenAsync(window, viewModel, view, _files.Copy("forms-basic.pdf", Guid.NewGuid().ToString("N")[..8] + ".pdf"));
        await WaitUntilAsync(window, () => viewModel.Update is not null);
        return (viewModel, window, tab);
    }
}
