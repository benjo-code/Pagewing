using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.Core.Redaction;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.App.Tests.SaveCardHost;

namespace Pagewing.App.Tests;

/// <summary>
/// The mode "Redact" of the tab (milestone 14, task 11): the plan of marks the tab holds, the locks it puts on the other modes
/// (and they on it), closing with marks, moving the tab, printing, and what the plan does not touch (the document). Without a
/// window where the tab suffices; with one for closing, moving and printing. Every file is a copy in a temp folder; settings
/// and history are in memory.
/// </summary>
// Strings has a process-wide culture (see LocalizationTests).
[Collection("Localization")]
public sealed class RedactModelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly SaveCardHost _host = new();
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagewing-redact-" + Guid.NewGuid().ToString("N"))).FullName;

    public RedactModelTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        _host.Dispose();
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
    public async Task RedactNeedsASharpPageFirst()
    {
        var tab = await OpenAsync("pages-marked.pdf", sharp: false);

        Assert.False(tab.RedactCommand.CanExecute(null));
        Assert.False(tab.CanRedact);
        Assert.Equal(Strings.RedactNotReady, tab.RedactionBlockedReason);
        tab.RedactCommand.Execute(null);
        await Task.Delay(50);
        Pump();
        Assert.False(tab.IsRedacting);
        Assert.Null(tab.RedactionHistory);

        tab.HasShownSharpPage = true;
        Assert.True(tab.RedactCommand.CanExecute(null));
        Assert.True(tab.CanRedact);
        Assert.Null(tab.RedactionBlockedReason);
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsRedacting);
        Assert.NotNull(tab.RedactionHistory);
        Assert.True(tab.RedactionHistory.Current.IsEmpty);
        Assert.False(tab.HasPendingRedactions);
        Assert.Equal(Strings.RedactActive, tab.RedactionLockReason);
    }

    [AvaloniaFact]
    public async Task MarksAreStagedAndTheDocumentStaysUnchanged()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        var document = tab.Document!;
        var annotations = (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count;
        await EnterAsync(tab);

        Assert.True(tab.AddAreaMark(0, new PageRect(72, 100, 200, 130)));
        var first = Assert.Single(tab.RedactionPlan!.Marks);
        Assert.Equal(RedactionKind.Area, first.Kind);
        Assert.Equal(0, first.Page);
        Assert.Equal(new PageRect(72, 100, 200, 130), Assert.Single(first.Areas));
        Assert.Null(first.Term);
        Assert.False(first.Everywhere);
        Assert.Equal([first.Id], tab.SelectedMarks);

        // A drag from the lower right to the upper left is the same rectangle.
        Assert.True(tab.AddAreaMark(2, new PageRect(300, 400, 100, 250)));
        Assert.Equal(new PageRect(100, 250, 300, 400), Assert.Single(tab.RedactionPlan.Marks[1].Areas));

        // A click without a drag is ignored without a notice, as is a page the document does not have.
        var version = tab.RedactionHistory!.Version;
        Assert.False(tab.AddAreaMark(1, new PageRect(50, 50, 50, 90)));
        Assert.False(tab.AddAreaMark(1, new PageRect(50, 50, 90, 50)));
        Assert.False(tab.AddAreaMark(6, new PageRect(10, 10, 20, 20)));
        Assert.False(tab.AddAreaMark(-1, new PageRect(10, 10, 20, 20)));
        Assert.Null(tab.RedactionNotice);

        // A rectangle that is not one is refused with a notice and changes nothing; the notice goes with the next mark that works.
        Assert.False(tab.AddAreaMark(1, new PageRect(double.NaN, 10, 20, 20)));
        Assert.Equal(Strings.RedactMarkRefused, tab.RedactionNotice);
        Assert.False(tab.AddAreaMark(1, new PageRect(10, 10, double.PositiveInfinity, 20)));
        Assert.Equal(version, tab.RedactionHistory.Version);
        Assert.Equal(2, tab.RedactionPlan.Marks.Count);
        Assert.True(tab.AddAreaMark(1, new PageRect(10, 10, 20, 20)));
        Assert.Null(tab.RedactionNotice);

        // The plan has the marks; the tab shows and holds the document as it was, and nothing is an unsaved change of the file.
        Assert.True(tab.HasPendingRedactions);
        Assert.Equal(3, tab.RedactionPlan.Marks.Count);
        Assert.Equal([0, 1, 2], tab.RedactionPlan.Pages.Order());
        Assert.Equal(6, tab.PageCount);
        Assert.Same(document, tab.Document);
        Assert.Equal(6, document.PageCount);
        Assert.Equal(annotations, (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);

        // Deleting the selected marks is one step; the fill is a step too and survives undo of the marks.
        tab.SelectMarks([first.Id, tab.RedactionPlan.Marks[1].Id]);
        Assert.Equal(2, tab.SelectedMarks.Count);
        tab.DeleteMarksCommand.Execute(null);
        Assert.Single(tab.RedactionPlan.Marks);
        Assert.Empty(tab.SelectedMarks);
        Assert.False(tab.DeleteMarksCommand.CanExecute(null));
        Assert.Equal(RedactionFill.Black, tab.RedactionFill);
        tab.RedactionFill = RedactionFill.White;
        Assert.Equal(RedactionFill.White, tab.RedactionPlan.Fill);
        Assert.Equal(RedactionFill.White, tab.RedactionFill);
    }

    [AvaloniaFact]
    public async Task RedactModeLocksAnnotateFillAndOrganizeAndViceVersa()
    {
        var tab = await OpenAsync("forms-basic.pdf");

        // The offer depends on the form read, which runs in the background: wait for it, so the checks below never race it.
        await tab.EnsureFormInfoAsync();
        Assert.True(tab.HasFormFields);
        Assert.True(tab.BeginAnnotateCommand.CanExecute(null));
        Assert.True(tab.ToggleFillCommand.CanExecute(null));
        Assert.True(tab.OrganizeCommand.CanExecute(null));
        Assert.True(tab.OffersFill);

        // The mode ends an annotation mode that is on.
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsAnnotating);
        await EnterAsync(tab);
        Assert.False(tab.IsAnnotating);

        // None of the other modes starts, each with the reason where the panels show one (the mode is on, no mark yet).
        Assert.False(tab.BeginAnnotateCommand.CanExecute(null));
        Assert.False(tab.ToggleAnnotateCommand.CanExecute(null));
        Assert.False(tab.HighlightCommand.CanExecute(null));
        Assert.False(tab.InkCommand.CanExecute(null));
        Assert.False(tab.ToggleFillCommand.CanExecute(null));
        Assert.False(tab.OrganizeCommand.CanExecute(null));
        Assert.False(tab.CanAnnotate);
        Assert.False(tab.CanOrganize);
        Assert.False(tab.OffersFill);
        Assert.Equal(Strings.RedactActive, tab.RedactionLockReason);
        Assert.Equal(Strings.RedactActive, tab.AnnotateUnavailableReason);
        Assert.Equal(Strings.RedactActive, tab.OrganizeUnavailableReason);
        await tab.EnterFillAsync();
        Assert.False(tab.IsFilling);
        Assert.False(tab.CanRunEdit);
        tab.OrganizeCommand.Execute(null);
        Assert.False(tab.IsOrganizing);

        // With marks the reason says they wait; leaving by the mode command does nothing then (apply or discard first).
        Assert.True(tab.AddAreaMark(0, new PageRect(10, 10, 80, 40)));
        Assert.Equal(Strings.RedactMarksPending, tab.RedactionLockReason);
        Assert.Equal(Strings.RedactMarksPending, tab.AnnotateUnavailableReason);
        Assert.Equal(Strings.RedactMarksPending, tab.OrganizeUnavailableReason);
        Assert.False(tab.RedactCommand.CanExecute(null));
        tab.RedactCommand.Execute(null);
        Assert.True(tab.IsRedacting);

        // Discarding frees the modes again.
        tab.DiscardRedactionsCommand.Execute(null);
        Assert.False(tab.IsRedacting);
        Assert.False(tab.HasPendingRedactions);
        Assert.Null(tab.RedactionHistory);
        Assert.Null(tab.RedactionLockReason);
        Assert.True(tab.BeginAnnotateCommand.CanExecute(null));
        Assert.True(tab.ToggleFillCommand.CanExecute(null));
        Assert.True(tab.OrganizeCommand.CanExecute(null));
        Assert.True(tab.OffersFill);

        // The other way round: while pages are organized the mode is not offered, with the reason (page changes pending or not).
        tab.OrganizeCommand.Execute(null);
        Assert.True(tab.IsOrganizing);
        Assert.False(tab.RedactCommand.CanExecute(null));
        Assert.False(tab.CanRedact);
        Assert.Equal(Strings.PageOrganizeActive, tab.RedactionBlockedReason);
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        Assert.Equal(Strings.PageEditsPending, tab.RedactionBlockedReason);
        tab.RedactCommand.Execute(null);
        await Task.Delay(50);
        Pump();
        Assert.False(tab.IsRedacting);
        tab.DiscardPageEditsCommand.Execute(null);
        Assert.True(tab.CanRedact);

        // A mode that is on when the mode starts ends first: the fill, with its mode, as the comment mode did.
        await tab.EnterFillAsync();
        Assert.True(tab.IsFilling);
        await EnterAsync(tab);
        Assert.False(tab.IsFilling);
        Assert.True(tab.IsRedacting);
    }

    [AvaloniaFact]
    public async Task PendingMarksCountAsUnsavedWhenClosing()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "pages-marked.pdf");
        Assert.False(tab.AsksBeforeClosing);
        await EnterAsync(tab, window);
        Assert.False(tab.AsksBeforeClosing);
        Assert.True(tab.AddAreaMark(1, new PageRect(72, 100, 200, 130)));
        Assert.True(tab.AsksBeforeClosing);
        Assert.True(tab.AsksOnlyAboutRedactions);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Contains("redaction marks pending", tab.TabAutomationName, StringComparison.Ordinal);

        // Ctrl+S does not save the marks as comments: it opens the card of "Apply" (RedactApplyTests), which redacts or does nothing.
        Assert.True(tab.SaveCommand.CanExecute(null));

        // The tab asks first: "Discard the marks?" with "Cancel" as the default, no "Save".
        viewModel.CloseActiveTabCommand.Execute(null);
        var card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        var question = viewModel.PendingSave!;
        Assert.Same(tab, question.Tab);
        Assert.Equal(path, question.Path);
        Assert.Equal("Discard the redaction marks on “pages-marked.pdf”?", question.Title);
        Assert.Equal(Strings.DiscardRedactionsText, question.Message);
        Assert.Equal(PendingSaveChoice.Cancel, question.DefaultChoice);
        Assert.DoesNotContain(PendingSaveChoice.Save, question.Choices);
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Markierungen in „pages-marked.pdf“ verwerfen?", question.Title);
        Strings.Culture = new CultureInfo("en");
        Click(window, card.ButtonOf(PendingSaveChoice.Cancel)!);
        await SaveCardHost.WaitUntilAsync(window, () => viewModel.PendingSave is null);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.True(tab.HasPendingRedactions);
        Assert.Single(tab.RedactionPlan!.Marks);

        // "Close and discard changes" closes the tab without writing anything.
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        viewModel.CloseActiveTabCommand.Execute(null);
        card = await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.DiscardChanges);
        _host.PassTheLock(window);
        Click(window, card.ButtonOf(PendingSaveChoice.Discard)!);
        await SaveCardHost.WaitUntilAsync(window, () => !viewModel.Tabs.Contains(tab));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task PendingMarksWithUnsavedCommentsAskToSaveAndSaySoAboutTheMarks()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "pages-marked.pdf");
        await MarkAsync(window, tab);
        await EnterAsync(tab, window);
        Assert.True(tab.AddAreaMark(0, new PageRect(72, 100, 200, 130)));
        Assert.False(tab.AsksOnlyAboutRedactions);

        // The usual question with its own text: saving does not apply the marks, and they are lost as well.
        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitForCardAsync<SaveChangesCard>(window, viewModel, PendingSaveKind.SaveChanges);
        Assert.Equal(Strings.SaveChangesWithMarksText, viewModel.PendingSave!.Message);
    }

    [AvaloniaFact]
    public async Task ADiscardedPlanDoesNotHideALaterMarkFromTheAnswersOfTheUpdateFlow()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        var start = tab.ChangeVersion;
        await EnterAsync(tab);
        Assert.True(tab.AddAreaMark(0, new PageRect(10, 10, 50, 50)));
        var asked = tab.ChangeVersion;
        Assert.True(asked > start);
        Assert.True(tab.AddAreaMark(1, new PageRect(10, 10, 50, 50)));
        Assert.True(tab.ChangeVersion > asked);

        // The version never goes back, also when the plan is dropped: an answer "Don't save" for an older version asks again.
        var before = tab.ChangeVersion;
        tab.DiscardRedactionsCommand.Execute(null);
        Assert.True(tab.ChangeVersion > before);
        Assert.Equal(0, tab.EditVersion);
    }

    [AvaloniaFact]
    public async Task PendingMarksSurviveMovingTheTabToANewWindow()
    {
        var (window, viewModel, view) = _host.Show();
        var (other, otherViewModel, _) = _host.Show();
        var (tab, _) = await _host.OpenCopyAsync(window, viewModel, view, "pages-marked.pdf");
        await EnterAsync(tab, window);
        Assert.True(tab.AddAreaMark(4, new PageRect(20, 20, 120, 60)));
        Assert.True(tab.AddAreaMark(2, new PageRect(30, 30, 90, 60)));
        var history = tab.RedactionHistory;
        var plan = history!.Current;
        var selected = tab.SelectedMarks.Order().ToArray();

        // The tab keeps its session and its state, also the plan, the selection and the mode, while the flag of the sharp page starts over.
        Assert.True(viewModel.DetachTab(tab));
        otherViewModel.AttachTab(tab, 0);
        Settle(other);

        Assert.True(tab.IsRedacting);
        Assert.True(tab.HasPendingRedactions);
        Assert.Same(history, tab.RedactionHistory);
        Assert.Same(plan, tab.RedactionHistory!.Current);
        Assert.Equal(selected, tab.SelectedMarks.Order());
        Assert.True(tab.AsksBeforeClosing);
        Assert.True(otherViewModel.HasUnsavedChanges);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.True(tab.UndoRedactionCommand.CanExecute(null));
        Assert.True(tab.AddAreaMark(0, new PageRect(5, 5, 25, 25)));
    }

    [AvaloniaFact]
    public async Task PrintingNeverContainsMarks()
    {
        var (window, viewModel, view) = _host.Show();
        var (tab, path) = await _host.OpenCopyAsync(window, viewModel, view, "pages-marked.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var annotations = (await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count;
        var annotationsOnMarkedPage = (await tab.Document.GetAnnotationsAsync(3, TestContext.Current.CancellationToken)).Count;
        await EnterAsync(tab, window);
        Assert.True(tab.AddAreaMark(0, new PageRect(72, 100, 200, 130)));
        Assert.True(tab.AddAreaMark(3, new PageRect(10, 10, 300, 400)));
        var plan = tab.RedactionPlan;

        _host.Print.SystemDialog = true;
        _host.Print.DialogTicket = new PrintTicket(new PrintSettings { Printer = "Plotter" });
        viewModel.PrintCommand.Execute(null);
        await SaveCardHost.WaitUntilAsync(window, () => _host.Print.Jobs.Count == 1 && viewModel.PrintJob is { IsRunning: false });

        // A plan is not an annotation: the job gets the document as it is on disk and in memory, with no mark in it, and the plan
        // is as it was after the job (printing neither applies nor drops the marks).
        var job = Assert.Single(_host.Print.Jobs);
        Assert.Same(tab.Document, job.Document);
        Assert.Equal(6, job.Document.PageCount);
        Assert.Equal(annotations, (await job.Document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count);
        Assert.Equal(annotationsOnMarkedPage, (await job.Document.GetAnnotationsAsync(3, TestContext.Current.CancellationToken)).Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Same(plan, tab.RedactionPlan);
        Assert.True(tab.IsRedacting);
        Assert.True(tab.HasPendingRedactions);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task EncryptedDocumentExplainsWhyRedactingIsNotOffered()
    {
        var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(tab);
        tab.AnnotationSettings = UserSettings.Detached();
        await tab.OpenAsync(Copy("encrypted_hello_world_r3.pdf"));
        await WaitUntilAsync(() => tab.State == ViewerState.PasswordRequired);
        tab.Password = "hôtel";
        tab.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null);
        tab.HasShownSharpPage = true;

        // Not known before the first request: the mode is offered; asking reads the encryption (one short job) and refuses with a reason.
        Assert.True(tab.RedactCommand.CanExecute(null));
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(() => !tab.CanRedact);
        Assert.False(tab.IsRedacting);
        Assert.Null(tab.RedactionHistory);
        Assert.Equal(Strings.RedactEncrypted, tab.RedactionBlockedReason);
        Assert.False(tab.RedactCommand.CanExecute(null));
        Assert.False(tab.AsksBeforeClosing);

        // The text names the file and the reason in both languages (never greyed out without one).
        Assert.Contains("encrypted", tab.RedactionBlockedReason, StringComparison.Ordinal);
        Strings.Culture = new CultureInfo("de");
        Assert.Contains("verschlüsselt", tab.RedactionBlockedReason, StringComparison.Ordinal);
        Strings.Culture = new CultureInfo("en");

        // A key or the palette does nothing either, and the marks cannot be made.
        tab.RedactCommand.Execute(null);
        await Task.Delay(50);
        Pump();
        Assert.False(tab.IsRedacting);
        Assert.False(tab.AddAreaMark(0, new PageRect(10, 10, 20, 20)));
    }

    [AvaloniaFact]
    public async Task TheEncryptionOfADocumentIsReadOncePerDocument()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        var reads = 0;
        tab.EncryptionReader = (_, _) =>
        {
            reads++;
            return Task.FromResult(false);
        };

        await EnterAsync(tab);
        tab.DiscardRedactionsCommand.Execute(null);
        await EnterAsync(tab);
        Assert.Equal(1, reads);

        // A read that fails is never taken for "not encrypted": the mode stays off and a later request reads again.
        tab.DiscardRedactionsCommand.Execute(null);
        await tab.OpenAsync(Copy("hello_world.pdf"));
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.PageCount == 1);
        tab.HasShownSharpPage = true;
        tab.EncryptionReader = (_, _) =>
        {
            reads++;
            throw new InvalidOperationException("The document went away.");
        };
        tab.RedactCommand.Execute(null);
        await Task.Delay(50);
        Pump();
        Assert.False(tab.IsRedacting);
        Assert.Equal(2, reads);
        Assert.True(tab.CanRedact);
    }

    [AvaloniaFact]
    public async Task UndoRedoFollowTheHistory()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        await EnterAsync(tab);
        Assert.False(tab.UndoRedactionCommand.CanExecute(null));
        Assert.False(tab.RedoRedactionCommand.CanExecute(null));
        Assert.False(tab.UndoCommand.CanExecute(null));

        Assert.True(tab.AddAreaMark(0, new PageRect(10, 10, 60, 40)));
        var second = tab.RedactionPlan!.Marks[0].Id;
        Assert.True(tab.AddAreaMark(1, new PageRect(20, 20, 80, 60)));
        var third = tab.RedactionPlan.Marks[1].Id;
        Assert.Equal([third], tab.SelectedMarks);
        Assert.True(tab.UndoRedactionCommand.CanExecute(null));

        // Ctrl+Z (the edit command of the catalog) undoes a mark while the mode is on, never an annotation; the selection follows the plan.
        Assert.True(tab.UndoCommand.CanExecute(null));
        tab.UndoCommand.Execute(null);
        Assert.Equal([second], tab.RedactionPlan.Marks.Select(mark => mark.Id));
        Assert.Empty(tab.SelectedMarks);
        Assert.True(tab.RedoRedactionCommand.CanExecute(null));
        Assert.True(tab.RedoCommand.CanExecute(null));
        Assert.False(tab.HasEditor);

        tab.UndoRedactionCommand.Execute(null);
        Assert.True(tab.RedactionPlan.IsEmpty);
        Assert.False(tab.HasPendingRedactions);
        Assert.False(tab.AsksBeforeClosing);
        Assert.False(tab.UndoRedactionCommand.CanExecute(null));
        tab.RedoRedactionCommand.Execute(null);
        tab.RedoCommand.Execute(null);
        Assert.Equal([second, third], tab.RedactionPlan.Marks.Select(mark => mark.Id));
        Assert.True(tab.HasPendingRedactions);
        Assert.False(tab.RedoRedactionCommand.CanExecute(null));

        // A new mark after an undo forgets what could be redone.
        tab.UndoRedactionCommand.Execute(null);
        Assert.True(tab.RedoRedactionCommand.CanExecute(null));
        Assert.True(tab.AddAreaMark(2, new PageRect(1, 1, 9, 9)));
        Assert.False(tab.RedoRedactionCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ExtractSplitAndExportImagesAreLockedWhileMarksArePending()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Without marks they run, in the mode as well (a mode without a mark changes nothing in what they read).
        Assert.True(tab.SplitCommand.CanExecute(null));
        Assert.True(tab.ExportImagesCommand.CanExecute(null));
        await EnterAsync(tab);
        Assert.Null(tab.PageOutputBlockedReason);
        Assert.True(tab.SplitCommand.CanExecute(null));
        Assert.True(tab.ExportImagesCommand.CanExecute(null));

        // The first mark locks all three: they read the document without the marks, so the files would show what the view hides.
        raised.Clear();
        Assert.True(tab.AddAreaMark(0, new PageRect(72, 100, 200, 130)));
        Assert.Equal(Strings.PageOutputBlockedMarks, tab.PageOutputBlockedReason);
        Assert.Equal(Strings.PageOutputBlockedMarks, tab.PageOutputReasonText);
        Assert.Equal(Strings.PageOutputBlockedMarks, tab.ExtractUnavailableReason);
        Assert.False(tab.CanRunPageOutput);
        Assert.False(tab.SplitCommand.CanExecute(null));
        Assert.False(tab.ExportImagesCommand.CanExecute(null));
        Assert.False(tab.ExtractSelectionCommand.CanExecute(null));
        Assert.Contains(nameof(TabViewModel.CanRunPageOutput), raised);
        Assert.Contains(nameof(TabViewModel.PageOutputBlockedReason), raised);
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Wenden Sie die Markierungen zum Schwärzen zuerst an oder verwerfen Sie sie.", tab.PageOutputBlockedReason);
        Strings.Culture = new CultureInfo("en");

        // Undone: possible again; with a mark and then discarded: possible again.
        tab.UndoRedactionCommand.Execute(null);
        Assert.Null(tab.PageOutputBlockedReason);
        Assert.True(tab.SplitCommand.CanExecute(null));
        Assert.True(tab.AddAreaMark(0, new PageRect(72, 100, 200, 130)));
        Assert.False(tab.SplitCommand.CanExecute(null));
        tab.DiscardRedactionsCommand.Execute(null);
        Assert.Null(tab.PageOutputBlockedReason);
        Assert.True(tab.SplitCommand.CanExecute(null));
        Assert.True(tab.ExportImagesCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task MarksAreRefusedWhileACardAsksOrAnUpdateLocks()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        await EnterAsync(tab);
        Assert.True(tab.AddAreaMark(0, new PageRect(10, 10, 50, 50)));

        tab.IsAskedAbout = true;
        Assert.False(tab.AddAreaMark(1, new PageRect(10, 10, 50, 50)));
        Assert.False(tab.UndoCommand.CanExecute(null));
        Assert.False(tab.DiscardRedactionsCommand.CanExecute(null));
        tab.IsAskedAbout = false;
        Assert.True(tab.AddAreaMark(1, new PageRect(10, 10, 50, 50)));

        tab.IsEditingFrozen = true;
        Assert.False(tab.AddAreaMark(2, new PageRect(10, 10, 50, 50)));
        Assert.False(tab.UndoRedactionCommand.CanExecute(null));
        Assert.False(tab.DiscardRedactionsCommand.CanExecute(null));
        tab.IsEditingFrozen = false;
        Assert.True(tab.AddAreaMark(2, new PageRect(10, 10, 50, 50)));
    }

    [AvaloniaFact]
    public async Task AnotherDocumentEndsTheModeAndTheMarksWithTheOldOne()
    {
        var tab = await OpenAsync("pages-marked.pdf");
        await EnterAsync(tab);
        Assert.True(tab.AddAreaMark(0, new PageRect(10, 10, 50, 50)));
        Assert.True(tab.HasPendingRedactions);

        await tab.OpenAsync(Copy("hello_world.pdf"));
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.PageCount == 1);

        Assert.False(tab.IsRedacting);
        Assert.False(tab.HasPendingRedactions);
        Assert.Null(tab.RedactionHistory);
        Assert.Empty(tab.SelectedMarks);
        Assert.False(tab.AsksBeforeClosing);
        Assert.Null(tab.RedactionLockReason);
    }

    [AvaloniaFact]
    public async Task NothingIsCreatedAtStartup()
    {
        var tab = await OpenAsync("pages-marked.pdf");

        // An open document with a sharp page: no mode, no plan, no history, no selection, no command of the mode. Asking about
        // the reasons makes nothing either.
        Assert.False(tab.IsRedacting);
        Assert.False(tab.HasPendingRedactions);
        Assert.Null(tab.RedactionHistory);
        Assert.Null(tab.RedactionPlan);
        Assert.Empty(tab.SelectedMarks);
        Assert.Null(tab.RedactionLockReason);
        Assert.Null(tab.RedactionBlockedReason);
        Assert.Null(tab.RedactionNotice);
        Assert.Equal(RedactionFill.Black, tab.RedactionFill);
        Assert.False(tab.HasRedactionCommands);
        Assert.False(tab.HasReadEncryption);
        Assert.False(tab.AddAreaMark(0, new PageRect(10, 10, 50, 50)));
        Assert.Null(tab.RedactionHistory);

        // Entering the mode makes the history; the commands of the mode are made by the first access, as for every command of a tab.
        await EnterAsync(tab);
        Assert.NotNull(tab.RedactionHistory);
        Assert.False(tab.HasRedactionCommands);
        _ = tab.DeleteMarksCommand;
        Assert.True(tab.HasRedactionCommands);
    }

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

    /// <summary>Enters the mode with its command (it reads the encryption of the document first, so it ends a moment later).</summary>
    private static async Task EnterAsync(TabViewModel tab)
    {
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(() => tab.IsRedacting);
    }

    private static async Task EnterAsync(TabViewModel tab, Avalonia.Controls.Window window)
    {
        tab.RedactCommand.Execute(null);
        await SaveCardHost.WaitUntilAsync(window, () => tab.IsRedacting);
    }

    private string Copy(string name)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + "-" + name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    /// <summary>
    /// Opens a copy of a test file in a new tab without a window and, unless <paramref name="sharp"/> is false, marks its
    /// first sharp page as shown (as the document view does).
    /// </summary>
    private async Task<TabViewModel> OpenAsync(string name, bool sharp = true)
    {
        var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(tab);
        tab.AnnotationSettings = UserSettings.Detached();
        await tab.OpenAsync(Copy(name));
        await WaitUntilAsync(() => tab.State == ViewerState.Ready && tab.Document is not null);
        tab.HasShownSharpPage = sharp;
        return tab;
    }
}
