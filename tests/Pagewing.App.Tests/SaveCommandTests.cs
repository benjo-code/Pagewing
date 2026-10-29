using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Measurement;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.Core.Saving;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Saving in the app (milestone 9, task 13): the commands, the unsaved marks of the tab and the status bar, the questions of
/// the save flow (<see cref="PendingSave"/>, answered here in code; the cards come later) and what the view keeps when the
/// session swaps the document for the saved file. Every file is a copy in a temp folder; settings and history are in memory.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests); the density is app-wide as well.
[Collection("Localization")]
public sealed class SaveCommandTests : IDisposable
{
    private const RawInputModifiers None = RawInputModifiers.None;
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly AnnotationColor Yellow = new(255, 230, 0);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-save-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakePrintService _print = new();
    private UserSettings? _densitySettings;
    private Appearance? _appearance;

    public SaveCommandTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        _densitySettings?.Update(_ => AppSettings.Default);
        _appearance?.Dispose();
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
    public async Task UndoAndRedoWaitWhileTheTabPrints()
    {
        var (window, viewModel, view) = Show();
        var (tab, _) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab, page: 0);
        Assert.True(tab.UndoCommand.CanExecute(null));

        // A print job prints the page in several short requests (the windows of a form page in strips), so a change between two
        // of them would print one page half old, half new.
        var raised = 0;
        tab.UndoCommand.CanExecuteChanged += (_, _) => raised++;
        tab.RedoCommand.CanExecuteChanged += (_, _) => raised++;
        tab.IsPrinting = true;
        Assert.False(tab.UndoCommand.CanExecute(null));
        Assert.False(tab.RedoCommand.CanExecute(null));
        Assert.Equal(2, raised);

        tab.IsPrinting = false;
        Assert.True(tab.UndoCommand.CanExecute(null));
        Assert.Equal(4, raised);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.CanRedo && !tab.CanUndo);
        Assert.True(tab.RedoCommand.CanExecute(null));
        tab.IsPrinting = true;
        Assert.False(tab.RedoCommand.CanExecute(null));
        tab.IsPrinting = false;
        Assert.True(tab.RedoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SaveWritesTheFileAndClearsTheDirtyMarker()
    {
        var recent = RecentFilesService.Detached();
        var (window, viewModel, view) = Show(recent: recent);
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab, page: 0);
        Assert.True(tab.IsDirty);
        Assert.True(Find<Control>(window, "UnsavedIndicator").IsEffectivelyVisible);
        Assert.Single(recent.Files.Items);

        // Ctrl+S from the document: saved in the background, the tab is clean again.
        view.Focus();
        Press(window, Key.S, PhysicalKey.S, Ctrl);
        await WaitUntilAsync(window, () => !tab.IsDirty && !tab.IsSaving);

        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.True(saved.Length > original.Length);
        Assert.Equal(original, saved[..original.Length]);
        await using (var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        }

        Assert.False(Find<Control>(window, "UnsavedIndicator").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "TabUnsavedDot").IsEffectivelyVisible);
        Assert.Null(viewModel.PendingSave);
        Assert.False(tab.CanUndo);
        Assert.Equal(path, tab.FullPath);

        // The history is not told again (no second open of the same file); the tab shows the adopted document.
        Assert.Single(recent.Files.Items);
        Assert.Same(tab.Document, tab.ReportedDocument);
        Assert.Same(tab.Session.Document, tab.Document);
        Assert.Equal(0, CountTemporaryFiles());

        // Nothing to save any more: Ctrl+S does nothing.
        Assert.False(tab.SaveCommand.CanExecute(null));
        Assert.False(await viewModel.SaveActiveTabAsync());
    }

    [AvaloniaFact]
    public async Task AnUnexpectedFailureOfTheSaveShowsTheErrorCard()
    {
        var (window, viewModel, view) = Show(new SavePicker());
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab, page: 0);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // A failure none of the expected types covers, first before the writer (reading the signatures): the error card,
        // then the tab as it was. Never a silent failed save.
        var signatures = viewModel.SignatureReader;
        viewModel.SignatureReader = static (_, _) => throw new NotSupportedException("secret detail of the reader");
        var save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        Assert.Equal(PendingSaveKind.SaveError, viewModel.PendingSave!.Kind);
        Assert.Equal(SaveFailureReason.WriteFailed, viewModel.PendingSave.Reason);
        Assert.True(viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await save);
        Assert.False(tab.IsSaving);
        Assert.True(tab.IsDirty);

        // Then from the writer itself (Ctrl+S, which does not wait for the save).
        viewModel.SignatureReader = signatures;
        viewModel.DocumentWriter = static (_, _, _, _, _, _) => throw new NotSupportedException("secret detail of the writer");
        view.Focus();
        Press(window, Key.S, PhysicalKey.S, Ctrl);
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);

        var question = viewModel.PendingSave!;
        Assert.Equal(PendingSaveKind.SaveError, question.Kind);
        Assert.Equal(SaveFailureReason.WriteFailed, question.Reason);
        Assert.Same(tab, question.Tab);
        Assert.DoesNotContain("secret", question.Message, StringComparison.Ordinal);
        Assert.True(question.Resolve(PendingSaveChoice.Cancel));
        await WaitUntilAsync(window, () => !tab.IsSaving);

        // The tab keeps its document and its change and can be saved again; the file is untouched.
        Assert.Null(viewModel.PendingSave);
        Assert.True(tab.IsDirty);
        Assert.True(tab.SaveCommand.CanExecute(null));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(0, CountTemporaryFiles());
    }

    [AvaloniaFact]
    public async Task SaveAsChangesTheTabToTheNewFile()
    {
        var recent = RecentFilesService.Detached();
        var picker = new SavePicker();
        var (window, viewModel, view) = Show(picker, recent);
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab, page: 0);
        var target = System.IO.Path.Combine(_folder, "copy of it.pdf");
        picker.Result = target;

        Assert.True(await viewModel.SaveActiveTabAsAsync());
        await WaitUntilAsync(window, () => tab.FullPath == target);

        // The dialog started in the folder of the file with its name; the original stays as it was.
        Assert.Equal(System.IO.Path.GetFileName(path), picker.SuggestedName);
        Assert.Equal(_folder, picker.Folder);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        var written = await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal(original, written[..original.Length]);

        // The tab belongs to the new file: name, title, path of the session, and the history as for an open.
        Assert.False(tab.IsDirty);
        Assert.Equal("copy of it.pdf", tab.FileName);
        Assert.Equal("copy of it.pdf – Pagewing", viewModel.WindowTitle);
        Assert.Equal(target, tab.Session.Path);
        Assert.Equal(target, recent.Files.Items[0].Path);
        Assert.Equal("copy of it.pdf", Find<MiddleEllipsisTextBlock>(window, "TabName").Text);

        // Saving again goes to the new file.
        await MarkAsync(window, tab, page: 1);
        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        await using var reopened = await new PdfEngine().OpenAsync(target, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await reopened.GetAnnotationsAsync(1, TestContext.Current.CancellationToken));
    }

    [AvaloniaTheory]
    [InlineData("en", "Unsaved", "{0}, unsaved changes")]
    [InlineData("de", "Nicht gespeichert", "{0}, nicht gespeichert")]
    public async Task TabShowsDotAndStatusTextWhileDirty(string language, string text, string tabName)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, view) = Show();
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var name = System.IO.Path.GetFileName(path);
        var dot = Find<Panel>(window, "TabUnsavedDot");
        var indicator = Find<Control>(window, "UnsavedIndicator");
        var tabView = window.GetVisualDescendants().OfType<TabView>().Single(view => view.IsActive);

        Assert.False(dot.IsEffectivelyVisible);
        Assert.False(indicator.IsEffectivelyVisible);
        Assert.Equal(name, ControlAutomationPeer.CreatePeerForElement(tabView).GetName());

        await MarkAsync(window, tab, page: 0);

        // A shape and a text, never colour alone; screen readers hear the state in the name of the tab.
        Assert.True(dot.IsEffectivelyVisible);
        Assert.IsType<Ellipse>(dot.Children.Single());
        Assert.True(indicator.IsEffectivelyVisible);
        Assert.Equal(text, Find<TextBlock>(window, "UnsavedText").Text);
        Assert.Equal(text, Strings.Unsaved);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, tabName, name), ControlAutomationPeer.CreatePeerForElement(tabView).GetName());

        // The dot sits between the name and the close button, inside the tab.
        var tabBorder = Find<Border>(window, "Tab");
        var close = Find<Button>(window, "CloseTabButton");
        var dotLeft = dot.TranslatePoint(default, tabBorder)!.Value.X;
        Assert.True(dotLeft + dot.Bounds.Width <= close.TranslatePoint(default, tabBorder)!.Value.X);
        Assert.True(Find<MiddleEllipsisTextBlock>(window, "TabName").TranslatePoint(default, tabBorder)!.Value.X < dotLeft);

        // An inactive tab has its dot too, without the element names of the active one.
        await OpenCopyAsync(window, viewModel, view, "hello_world.pdf");
        var inactive = window.GetVisualDescendants().OfType<TabView>().Single(view => !view.IsActive);
        Assert.Contains(inactive.GetVisualDescendants().OfType<Ellipse>(), ellipse => ellipse.IsEffectivelyVisible);
        Assert.Single(window.GetVisualDescendants().OfType<Panel>(), panel => panel.Name == "TabUnsavedDot");
    }

    [AvaloniaFact]
    public async Task UnsavedMarksFitTheCompactDensity()
    {
        var app = Application.Current ?? throw new InvalidOperationException("No app.");
        _densitySettings = UserSettings.Detached();
        _appearance = new Appearance(app, _densitySettings);
        _densitySettings.Update(current => current with { Density = DensitySetting.Compact });
        var (window, viewModel, view) = Show(settings: _densitySettings);
        var (tab, _) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab, page: 0);

        var bar = Find<Control>(window, "StatusBar");
        Assert.Equal(26, bar.Bounds.Height);
        var indicator = Find<Control>(window, "UnsavedIndicator");
        var top = indicator.TranslatePoint(default, bar)!.Value.Y;
        Assert.True(top >= 0 && top + indicator.Bounds.Height <= bar.Bounds.Height, $"The indicator ({top}, {indicator.Bounds.Height}) leaves the bar.");
        var tabBorder = Find<Border>(window, "Tab");
        var dot = Find<Panel>(window, "TabUnsavedDot");
        var dotTop = dot.TranslatePoint(default, tabBorder)!.Value.Y;
        Assert.True(dotTop >= 0 && dotTop + dot.Bounds.Height <= tabBorder.Bounds.Height);
    }

    [AvaloniaFact]
    public async Task SaveIsBlockedWhilePrinting()
    {
        var (window, viewModel, view) = Show();
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab, page: 0);
        var release = new TaskCompletionSource();
        _print.SystemDialog = true;
        _print.DialogTicket = new PrintTicket(new PrintSettings { Printer = "Plotter" });
        _print.OnPrint = async (_, _, cancellationToken) => await release.Task.WaitAsync(cancellationToken);

        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: true });

        // While the job prints the document, neither key nor command saves; the reason is there for a card or tooltip.
        var save = viewModel.Commands.ById("file.save")!;
        Assert.False(save.CanExecute());
        Assert.False(viewModel.Commands.ById("file.saveAs")!.CanExecute());
        Assert.False(tab.SaveCommand.CanExecute(null));
        Assert.Equal(Strings.SaveBlockedPrinting, tab.SaveBlockedReason);
        Assert.False(await viewModel.SaveActiveTabAsync());
        Assert.True(tab.IsDirty);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        release.SetResult();
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: false });
        Assert.Null(tab.SaveBlockedReason);
        Assert.True(save.CanExecute());
        Assert.True(await viewModel.SaveActiveTabAsync());
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task SourceModifiedKeepsTheTabAndItsEdits()
    {
        var (window, viewModel, view) = Show(new SavePicker());
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab, page: 0);
        var document = tab.Document;

        // Another program rewrote the opened file in place (a touched time is enough to count).
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);

        var question = viewModel.PendingSave!;
        Assert.Equal(PendingSaveKind.SaveError, question.Kind);
        Assert.Equal(SaveFailureReason.SourceModified, question.Reason);
        Assert.Same(tab, question.Tab);
        Assert.Equal(Strings.SaveErrorSourceModified, question.Message);
        Assert.Equal("“text-basic.pdf” could not be saved", question.Title);

        // Every save reads the opened file, whatever its target: another name cannot help, and opening the file again would
        // lose the changes, so neither is offered.
        Assert.Equal([PendingSaveChoice.Cancel], question.Choices);
        Assert.False(question.Resolve(PendingSaveChoice.SaveAs));
        Assert.True(question.Resolve(PendingSaveChoice.Cancel));
        Assert.False(await save);

        // The tab keeps its document and every change, and still offers to save.
        Assert.Null(viewModel.PendingSave);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.Same(document, tab.Document);
        Assert.True(tab.IsDirty);
        Assert.True(tab.CanUndo);
        Assert.Single(await tab.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        Assert.True(tab.SaveCommand.CanExecute(null));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(0, CountTemporaryFiles());
    }

    [AvaloniaFact]
    public async Task OriginalChangedOffersOverwriteSaveAsCancel()
    {
        var (window, viewModel, view) = Show(new SavePicker());
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab, page: 0);
        ReplaceAtPath(path);

        var save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        var question = viewModel.PendingSave!;
        Assert.Equal(PendingSaveKind.FileChanged, question.Kind);
        Assert.Equal([PendingSaveChoice.Overwrite, PendingSaveChoice.SaveAs, PendingSaveChoice.Cancel], question.Choices);
        Assert.Equal(PendingSaveChoice.SaveAs, question.DefaultChoice);
        Assert.Equal(path, question.Path);
        Assert.Equal(Strings.FileChangedText, question.Message);

        // "Overwrite" writes the document of the tab over the other file.
        question.ResolveCommand.Execute(PendingSaveChoice.Overwrite);
        Assert.True(await save);
        Assert.False(tab.IsDirty);
        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task SignatureWarningComesBeforeTheSaveAtLevelOne()
    {
        var (window, viewModel, view) = Show();
        var reads = CountSignatureReads(viewModel);
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "signed-docmdp-1.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(0, reads.Count);
        await MarkAsync(window, tab, page: 0);

        var save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        var question = viewModel.PendingSave!;
        Assert.Equal(PendingSaveKind.SignatureWarning, question.Kind);
        Assert.Equal(DocMdpLevel.NoChanges, question.Level);
        Assert.Equal([PendingSaveChoice.Cancel, PendingSaveChoice.SaveAs, PendingSaveChoice.SaveAnyway], question.Choices);
        Assert.Equal(PendingSaveChoice.Cancel, question.DefaultChoice);
        Assert.Equal(Strings.SignatureWarningText, question.Message);
        Assert.DoesNotContain("valid", question.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, reads.Count);

        // Nothing is written while the card waits.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Settle(window);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(tab.IsDirty);

        question.Resolve(PendingSaveChoice.SaveAnyway);
        Assert.True(await save);
        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(original, saved[..original.Length]);
        Assert.False(tab.IsDirty);

        // The next save asks again (the signature is read once per document, also across the saved file).
        await MarkAsync(window, tab, page: 0);
        save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        Assert.Equal(PendingSaveKind.SignatureWarning, viewModel.PendingSave!.Kind);
        viewModel.PendingSave.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);
        Assert.True(tab.IsDirty);
        Assert.Equal(1, reads.Count);
    }

    [AvaloniaFact]
    public async Task SignatureNoteIsShownOncePerTab()
    {
        var (window, viewModel, view) = Show();
        var reads = CountSignatureReads(viewModel);
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "signed-docmdp-3.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await MarkAsync(window, tab, page: 0);

        var save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        var question = viewModel.PendingSave!;
        Assert.Equal(PendingSaveKind.SignatureNote, question.Kind);
        Assert.Equal(DocMdpLevel.FormsAndAnnotations, question.Level);
        Assert.Equal([PendingSaveChoice.Save, PendingSaveChoice.Cancel], question.Choices);
        Assert.Equal(PendingSaveChoice.Save, question.DefaultChoice);
        Assert.Equal(Strings.SignatureNoteText, question.Message);
        question.Resolve(PendingSaveChoice.Save);
        Assert.True(await save);
        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(original, saved[..original.Length]);

        // The second save of the same tab goes through without the note.
        await MarkAsync(window, tab, page: 0);
        save = viewModel.SaveActiveTabAsync();
        Settle(window);
        Assert.Null(viewModel.PendingSave);
        Assert.True(await save);
        Assert.Null(viewModel.PendingSave);
        Assert.False(tab.IsDirty);
        Assert.Equal(1, reads.Count);
    }

    [AvaloniaFact]
    public async Task SaveCancelledByTheUserWritesNothing()
    {
        var picker = new SavePicker();
        var (window, viewModel, view) = Show(picker);
        var (tab, path) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab, page: 0);

        // The file dialog of "Save as" cancelled: nothing written, nothing asked, the changes stay.
        picker.Result = null;
        Assert.False(await viewModel.SaveActiveTabAsAsync());
        Assert.Equal(1, picker.Calls);
        Assert.Null(viewModel.PendingSave);
        Assert.True(tab.IsDirty);
        Assert.Equal([System.IO.Path.GetFileName(path)], Directory.GetFiles(_folder).Select(System.IO.Path.GetFileName));

        // "Cancel" on the card about another file at the path: that file stays as it is.
        ReplaceAtPath(path);
        var other = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is { Kind: PendingSaveKind.FileChanged });
        viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);
        Assert.Equal(other, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(tab.IsDirty);

        // "Save as" there, then cancelling the dialog: still nothing.
        save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is { Kind: PendingSaveKind.FileChanged });
        viewModel.PendingSave!.Resolve(PendingSaveChoice.SaveAs);
        Assert.False(await save);
        Assert.Equal(2, picker.Calls);
        Assert.Equal(other, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.True(tab.IsDirty);

        // Closing the tab while its card asks is ignored (task 14: the card is answered first); "Cancel" then writes nothing.
        save = viewModel.SaveActiveTabAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        var question = viewModel.PendingSave!;
        viewModel.CloseActiveTabCommand.Execute(null);
        Settle(window);
        Assert.Same(question, viewModel.PendingSave);
        Assert.Contains(tab, viewModel.Tabs);
        question.Resolve(PendingSaveChoice.Cancel);
        Assert.False(await save);
        Assert.Null(viewModel.PendingSave);
        Assert.Equal(other, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(0, CountTemporaryFiles());
    }

    [AvaloniaFact]
    public async Task AFailedSaveAsOffersAnotherName()
    {
        var picker = new SavePicker();
        var (window, viewModel, view) = Show(picker);
        var (tab, _) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await MarkAsync(window, tab, page: 0);
        picker.Result = System.IO.Path.Combine(_folder, "missing", "out.pdf");

        var save = viewModel.SaveActiveTabAsAsync();
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        var question = viewModel.PendingSave!;
        Assert.Equal(PendingSaveKind.SaveError, question.Kind);
        Assert.Equal(SaveFailureReason.NotWritable, question.Reason);
        Assert.Equal([PendingSaveChoice.SaveAs, PendingSaveChoice.Cancel], question.Choices);
        Assert.Equal(PendingSaveChoice.SaveAs, question.DefaultChoice);
        Assert.Equal(Strings.SaveErrorNotWritable, question.Message);

        // "Save as" again, into a folder that exists: saved there.
        var target = System.IO.Path.Combine(_folder, "out.pdf");
        picker.Result = target;
        question.Resolve(PendingSaveChoice.SaveAs);
        Assert.True(await save);
        Assert.Equal(target, tab.FullPath);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task PagesInvalidatedDropsTheBitmapsOfThosePages()
    {
        var (window, viewModel, view) = Show();
        var (tab, _) = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        tab.Zoom = 0.5;
        await WaitUntilAsync(window, () => SharpBitmapsOf(view, 0).Count > 0 && SharpBitmapsOf(view, 1).Count > 0);
        var first = SharpBitmapsOf(view, 0);
        var second = SharpBitmapsOf(view, 1);

        tab.Session.InvalidatePages(new HashSet<int> { 0 });

        // Page 0 gets new bitmaps (its old ones were stale, never kept once the new image was there); page 1 keeps its own.
        await WaitUntilAsync(window, () =>
            view.Canvas.StaleKeys.Count == 0
            && SharpBitmapsOf(view, 0) is { Count: > 0 } now
            && now.All(entry => !first.TryGetValue(entry.Key, out var old) || !ReferenceEquals(old, entry.Value)));
        Assert.All(SharpBitmapsOf(view, 1), entry => Assert.Same(second[entry.Key], entry.Value));

        // The thumbnails of the pages panel the same way.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        var list = await WaitForAsync(window, () => TryFind<ThumbnailList>(window, "ThumbnailList"));
        await WaitUntilAsync(window, () => list.Thumbnails.Count == 2 && list.Thumbnails.All(item => item.HasBitmap));
        var thumbnail0 = list.Thumbnails[0].Bitmap;
        var thumbnail1 = list.Thumbnails[1].Bitmap;

        tab.Session.InvalidatePages(new HashSet<int> { 0 });

        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is { } now && !ReferenceEquals(now, thumbnail0) && list.StaleEntries.Count == 0);
        Assert.Same(thumbnail1, list.Thumbnails[1].Bitmap);
    }

    [AvaloniaFact]
    public async Task AfterTheSaveTheViewKeepsItsScrollAndZoom()
    {
        var (window, viewModel, view) = Show();
        var (tab, _) = await OpenCopyAsync(window, viewModel, view, "text-dense.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        var list = await WaitForAsync(window, () => TryFind<ThumbnailList>(window, "ThumbnailList"));
        tab.Zoom = 1.5;
        tab.GoToPage(50);
        Settle(window);
        await WaitUntilAsync(window, () => SharpBitmapsOf(view, 49).Count > 0);
        var selection = new TextSelection(new TextPosition(49, 0), new TextPosition(49, 10));
        tab.TextSelection = selection;
        await MarkAsync(window, tab, page: 49);
        var layout = view.Layout;
        var offset = view.ScrollViewer.Offset;
        var items = list.Thumbnails;
        var old = tab.Document;
        var oldScheduler = view.Canvas.Scheduler;

        Assert.True(await viewModel.SaveActiveTabAsync());
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && !ReferenceEquals(view.Document, old));
        Settle(window);

        // The same layout and position, the same zoom, page, selection and panel; the file is not treated as new.
        Assert.Same(layout, view.Layout);
        Assert.Equal(offset, view.ScrollViewer.Offset);
        Assert.Equal(1.5, tab.Zoom);
        Assert.Equal(50, tab.CurrentPage);
        Assert.Equal(selection, tab.TextSelection);
        Assert.True(tab.HasShownSharpPage);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        Assert.Same(items, list.Thumbnails);

        // The canvas draws the new document with the new scheduler, from the same cache, and still has its images.
        Assert.Same(tab.Session.Scheduler, view.Canvas.Scheduler);
        Assert.NotSame(oldScheduler, view.Canvas.Scheduler);
        Assert.Same(tab.Session.Cache, view.Canvas.Cache);
        Assert.NotEmpty(SharpBitmapsOf(view, 49));

        // It goes on rendering: another zoom brings sharp tiles of the new document.
        tab.Zoom = 1.25;
        await WaitUntilAsync(window, () => SharpBitmapsOf(view, 49).Count > 0);
    }

    [AvaloniaFact]
    public async Task RunningSearchRestartsAfterTheSave()
    {
        var (window, viewModel, view) = Show();
        var (tab, _) = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searched = new List<PdfDocument>();
        tab.Search.Searcher = (document, query, options, start, cancellationToken) => Search(document, query, options, start, cancellationToken);
        async IAsyncEnumerable<SearchPageResult> Search(PdfDocument document, string query, SearchOptions options, int start, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            int count;
            lock (searched)
            {
                searched.Add(document);
                count = searched.Count;
            }

            // The first search waits until the session has swapped its document for the saved file.
            if (count == 1)
            {
                await gate.Task;
            }

            await foreach (var result in DocumentSearch.SearchAsync(document, query, options, start, cancellationToken))
            {
                yield return result;
            }
        }

        tab.Search.Query = "Pagewing";
        await WaitUntilAsync(window, () => Count(searched) == 1);
        var old = tab.Document!;
        await MarkAsync(window, tab, page: 0);

        Assert.True(await viewModel.SaveActiveTabAsync());
        await WaitUntilAsync(window, () => !ReferenceEquals(tab.Document, old));
        gate.SetResult();

        // The search starts again on the new document and finds every match (4 on the two pages).
        await WaitUntilAsync(window, () => Count(searched) == 2 && tab.Search.State == SearchState.Done);
        lock (searched)
        {
            Assert.Same(old, searched[0]);
            Assert.Same(tab.Document, searched[1]);
        }

        Assert.Equal(4, tab.Search.Hits.Count);
        Assert.Equal("Pagewing", tab.Search.Query);

        static int Count(List<PdfDocument> list)
        {
            lock (list)
            {
                return list.Count;
            }
        }
    }

    [AvaloniaFact]
    public async Task MeasurementRunsCreateNoEditor()
    {
        var (window, viewModel, view) = Show();
        var reads = CountSignatureReads(viewModel);
        var exits = new List<int>();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var measurement = MeasurementRun.Create(
            StartupOptions.Parse(["--measure-startup", TestData.Pdf("text-basic.pdf")]),
            viewModel,
            view,
            new MeasurementOutput(output, error, exits.Add))!;

        measurement.Start();
        await WaitUntilAsync(window, () => exits.Count > 0);
        Settle(window);

        Assert.Equal([0], exits);
        Assert.All(viewModel.Tabs, tab => Assert.False(tab.HasEditor));
        Assert.Equal(0, reads.Count);
    }

    [AvaloniaFact]
    public async Task StartPathDoesNotCreateAnEditorOrReadSignatures()
    {
        var (window, viewModel, view) = Show(recent: RecentFilesService.Detached());
        var reads = CountSignatureReads(viewModel);
        var (tab, _) = await OpenCopyAsync(window, viewModel, view, "signed-docmdp-1.pdf");

        // Open, first sharp page, the panel, the history: neither an editor nor a read of the signatures.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Settle(window);
        Assert.False(tab.HasEditor);
        Assert.Equal(0, reads.Count);

        // Asking the commands (the palette lists what can run) makes no editor either.
        viewModel.ShowPaletteCommand.Execute(null);
        Settle(window);
        Assert.False(viewModel.Commands.ById("file.save")!.CanExecute());
        Assert.False(viewModel.Commands.ById("edit.undo")!.CanExecute());
        Assert.False(tab.HasEditor);
        Assert.Equal(0, reads.Count);
    }

    private static Dictionary<TileKey, Bitmap> SharpBitmapsOf(DocumentView view, int page)
    {
        var bitmaps = new Dictionary<TileKey, Bitmap>();
        foreach (var key in view.Canvas.Bitmaps.Keys)
        {
            if (key.Page == page && !key.IsPreview && view.Canvas.Bitmaps.TryGet(key, out var bitmap))
            {
                bitmaps[key] = bitmap;
            }
        }

        return bitmaps;
    }

    private static Counter CountSignatureReads(MainViewModel viewModel)
    {
        var counter = new Counter();
        var read = viewModel.SignatureReader;
        viewModel.SignatureReader = (document, cancellationToken) =>
        {
            counter.Count++;
            return read(document, cancellationToken);
        };
        return counter;
    }

    /// <summary>Puts another file at <paramref name="path"/> (the opened one moves away, untouched).</summary>
    private static void ReplaceAtPath(string path)
    {
        File.Move(path, path + ".moved");
        File.Copy(TestData.Pdf("hello_world.pdf"), path);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));
    }

    /// <summary>A highlight on <paramref name="page"/>; waits until the tab shows it as unsaved.</summary>
    private static async Task MarkAsync(Window window, TabViewModel tab, int page)
    {
        var created = await tab.Editor.ApplyMarkupAsync(page, AnnotationKind.Highlight, [new PageRect(72, 72, 220, 90)], Yellow, null, null);
        Assert.NotNull(created);
        await WaitUntilAsync(window, () => tab.IsDirty && tab.CanUndo);
    }

    private int CountTemporaryFiles() =>
        Directory.GetFiles(_folder, "*", SearchOption.AllDirectories).Count(file => System.IO.Path.GetFileName(file).StartsWith(".pw-", StringComparison.Ordinal));

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(IFilePicker? picker = null, RecentFilesService? recent = null, UserSettings? settings = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), picker ?? new SavePicker(), settings ?? UserSettings.Detached(), recent)
        {
            IsWindowsTitleBar = true,
            PrintServiceFactory = () => _print,
        };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a copy of a test file (in the temp folder) in a new tab and waits for its first sharp page.</summary>
    private async Task<(TabViewModel Tab, string Path)> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        var path = System.IO.Path.Combine(_folder, name);
        File.Copy(TestData.Pdf(name), path);
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return (tab, path);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Settle(window);
    }

    private static async Task<T> WaitForAsync<T>(Window window, Func<T?> find)
        where T : class
    {
        T? found = null;
        await WaitUntilAsync(window, () => (found = find()) is not null);
        return found!;
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
        TryFind<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private static T? TryFind<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private sealed class Counter
    {
        public int Count { get; set; }
    }

    /// <summary>A file picker whose open dialog is cancelled and whose save dialog returns <see cref="Result"/>.</summary>
    private sealed class SavePicker : IFilePicker
    {
        public string? Result { get; set; }

        public int Calls { get; private set; }

        public string? SuggestedName { get; private set; }

        public string? Folder { get; private set; }

        public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSavePdfAsync(string suggestedName, string? folder)
        {
            Calls++;
            SuggestedName = suggestedName;
            Folder = folder;
            return Task.FromResult(Result);
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
