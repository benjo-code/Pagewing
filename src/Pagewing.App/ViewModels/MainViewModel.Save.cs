using Avalonia.Threading;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Saving the tabs of a window (Ctrl+S, Ctrl+Shift+S): the signatures (read at the first save of a document, never at
/// open), the questions on the way (<see cref="PendingSave"/>, shown as cards in the document area), the save itself in
/// the background with its progress, and what a failure means. One save at a time per window; the UI thread never waits
/// for it.
/// </summary>
public sealed partial class MainViewModel
{
    private PendingSave? _pendingSave;
    private PdfEngine? _saveEngine;
    private TabViewModel? _savingTab;
    private CancellationTokenSource? _saveCancellation;

    // The save that runs or ran last (the close flow waits for one that still runs).
    private Task<bool>? _saveRun;

    /// <summary>
    /// Gets the question the save flow waits for (the signature, another file at the path, a failure), or
    /// <see langword="null"/>. At most one at a time; it is answered on its card (<see cref="PendingSave.Resolve"/>), and
    /// answered with "Cancel" when its tab closes.
    /// </summary>
    public PendingSave? PendingSave
    {
        get => _pendingSave;
        private set
        {
            if (!ReferenceEquals(_pendingSave, value))
            {
                if (_pendingSave is { } old)
                {
                    old.Tab.IsAskedAbout = false;
                }

                _pendingSave = value;
                if (value is not null)
                {
                    // Undo, redo, save and print of that tab wait for the answer.
                    value.Tab.IsAskedAbout = true;
                }

                _printCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Gets or sets how the signatures of a document are read (<see cref="PdfDocument.GetSignatureInfoAsync"/>; tests count the calls).</summary>
    internal Func<PdfDocument, CancellationToken, Task<PdfSignatureInfo>> SignatureReader { get; set; } =
        static (document, cancellationToken) => document.GetSignatureInfoAsync(cancellationToken);

    /// <summary>
    /// Gets or sets what writes the document of a tab (<see cref="Core.Annotations.AnnotationEditor.SaveAsync"/>; tests
    /// make it fail in ways the editor does not).
    /// </summary>
    internal Func<Core.Annotations.AnnotationEditor, PdfEngine, string, bool, IProgress<double>, CancellationToken, Task> DocumentWriter { get; set; } =
        static (editor, engine, target, overwrite, progress, cancellationToken) => editor.SaveAsync(engine, target, overwrite, progress, cancellationToken);

    /// <summary>Gets or sets what opens the written file for the check of a save (made on the first save).</summary>
    internal PdfEngine SaveEngine
    {
        get => _saveEngine ??= new PdfEngine();
        set => _saveEngine = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Saves the changes of the active tab to its file (Ctrl+S): asks first where the signature of the document needs it,
    /// then writes in the background. Nothing happens without unsaved changes, while a save runs or while the tab prints.
    /// </summary>
    /// <returns><see langword="true"/> when the file was saved; <see langword="false"/> when nothing was saved (cancelled, failed, not possible).</returns>
    public Task<bool> SaveActiveTabAsync() => ActiveTab is { } tab ? SaveTabAsync(tab, saveAs: false) : Task.FromResult(false);

    /// <summary>Saves the document of the active tab under a name chosen in the file dialog (Ctrl+Shift+S); the tab then belongs to that file.</summary>
    /// <returns><see langword="true"/> when the file was saved.</returns>
    public Task<bool> SaveActiveTabAsAsync() => ActiveTab is { } tab ? SaveTabAsync(tab, saveAs: true) : Task.FromResult(false);

    /// <summary>
    /// Saves <paramref name="tab"/>, a tab of this window: "Save" writes to its file, "Save as" (<paramref name="saveAs"/>)
    /// asks for another one first. The flow: the signatures (a DocMDP level 1 or 2 asks before every save, a signature that
    /// allows comments is mentioned once per document); then the save in the background; another file at the path of the
    /// original asks whether to overwrite it; a failure says why and offers "Save as". The tab keeps its document and all
    /// its changes until a save succeeded; then it is clean and shows the written file, where it was.
    /// </summary>
    /// <returns><see langword="true"/> when a file was written.</returns>
    /// <remarks>Nothing happens while another question of the window waits (the one before closing, for example).</remarks>
    internal Task<bool> SaveTabAsync(TabViewModel tab, bool saveAs)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_disposed || _savingTab is not null || _pendingSave is not null || Tabs.IndexOf(tab) < 0
            || !(saveAs ? tab.CanSaveAs() : tab.CanSave()) || tab.Document is not { } document || tab.FullPath is not { } path)
        {
            return Task.FromResult(false);
        }

        var run = SaveTabCoreAsync(tab, document, path, saveAs);
        _saveRun = run;
        return run;
    }

    private async Task<bool> SaveTabCoreAsync(TabViewModel tab, PdfDocument document, string path, bool saveAs)
    {
        using var cancellation = new CancellationTokenSource();
        _savingTab = tab;
        _saveCancellation = cancellation;
        tab.IsSaving = true;
        tab.SaveProgress = 0;
        try
        {
            switch (await AskAboutSignaturesAsync(tab, document, path, cancellation.Token))
            {
                case SignatureAnswer.Stop:
                    return false;
                case SignatureAnswer.SaveAs:
                    saveAs = true;
                    break;
            }

            var target = saveAs ? await PickTargetAsync(tab, path) : path;
            var overwrite = false;
            while (target is not null && !cancellation.IsCancellationRequested)
            {
                var outcome = await WriteAsync(tab, target, overwrite, cancellation.Token);
                switch (outcome.Choice)
                {
                    case null:
                        return outcome.Saved;
                    case PendingSaveChoice.Overwrite:
                        overwrite = true;
                        break;
                    case PendingSaveChoice.SaveAs:
                        overwrite = false;
                        target = await PickTargetAsync(tab, target);
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            // The tab or the window went, or the user cancelled: nothing is shown.
            return false;
        }
        catch (Exception)
        {
            // Anything else (the signatures, the file dialog, a bug): never a silent failed save. The error card says the
            // file was not saved; the tab keeps its document and its changes. The exception text is not shown.
            if (!_disposed && Tabs.IndexOf(tab) >= 0)
            {
                await AskAsync(PendingSave.SaveError(tab, path, SaveFailureReason.WriteFailed), CancellationToken.None);
            }

            return false;
        }
        finally
        {
            _saveCancellation = null;
            _savingTab = null;
            tab.IsSaving = false;
            tab.SyncFromEditor();
        }
    }

    /// <summary>How the save goes on after the signatures were looked at.</summary>
    private enum SignatureAnswer
    {
        /// <summary>As asked ("Save" or "Save as").</summary>
        Go,

        /// <summary>"Save as" on the warning card: the file dialog first.</summary>
        SaveAs,

        /// <summary>Nothing is saved.</summary>
        Stop,
    }

    /// <summary>
    /// The signatures of the document: a DocMDP level that forbids comments asks before every save ("Cancel" is the default);
    /// a signature that allows them is mentioned once per document. Read at the first save attempt, never at open.
    /// </summary>
    private async Task<SignatureAnswer> AskAboutSignaturesAsync(TabViewModel tab, PdfDocument document, string path, CancellationToken cancellationToken)
    {
        PdfSignatureInfo info;
        try
        {
            info = await tab.GetSignatureInfoAsync(document, SignatureReader, cancellationToken);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            // The document went meanwhile (its tab closed or another file opened in it): nothing to save.
            return SignatureAnswer.Stop;
        }

        switch (SignaturePolicy.Evaluate(info))
        {
            case SignatureAdvice.WouldInvalidate:
                return await AskAsync(PendingSave.SignatureWarning(tab, path, info.Permission), cancellationToken) switch
                {
                    PendingSaveChoice.SaveAnyway => SignatureAnswer.Go,
                    PendingSaveChoice.SaveAs => SignatureAnswer.SaveAs,
                    _ => SignatureAnswer.Stop,
                };
            case SignatureAdvice.AppendedOnly when !tab.SignatureNoteShown:
                tab.SignatureNoteShown = true;
                return await AskAsync(PendingSave.SignatureNote(tab, path, info.Permission), cancellationToken) == PendingSaveChoice.Save
                    ? SignatureAnswer.Go
                    : SignatureAnswer.Stop;
            default:
                return SignatureAnswer.Go;
        }
    }

    /// <summary>
    /// Writes the document of <paramref name="tab"/> to <paramref name="target"/> in the background
    /// (<see cref="Core.Annotations.AnnotationEditor.SaveAsync"/>, which checks the written file and lets the session
    /// adopt it). A failure asks the user: another file at the path of the original (overwrite, save as, cancel), any other
    /// reason (save as, cancel); <see cref="SaveFailureReason.SourceModified"/> keeps the tab with all its changes and says
    /// to save under another name (opening the file again would lose them).
    /// </summary>
    /// <returns>Whether the file was saved, or the answer of the user to a failure.</returns>
    private async Task<(bool Saved, PendingSaveChoice? Choice)> WriteAsync(TabViewModel tab, string target, bool overwrite, CancellationToken cancellationToken)
    {
        var editor = tab.Editor;
        var engine = SaveEngine;
        var progress = new SaveProgressReport(tab);
        try
        {
            // On the thread pool: the save reads file stamps and its adoption closes the old document, never on the UI thread.
            await Task.Run(() => DocumentWriter(editor, engine, target, overwrite, progress, cancellationToken), cancellationToken);
        }
        catch (SaveFailureException ex) when (ex.Reason == SaveFailureReason.OriginalChanged)
        {
            return (false, await AskAsync(PendingSave.FileChanged(tab, target), cancellationToken));
        }
        catch (SaveFailureException ex) when (ex.Reason != SaveFailureReason.Canceled)
        {
            if (ex.Reason is SaveFailureReason.SourceModified or SaveFailureReason.EncryptedNotSupported)
            {
                // Every further save fails the same way: closing the tab warns that the changes are lost.
                tab.UnsaveableReason = ex.Reason;
            }

            return (false, await AskAsync(PendingSave.SaveError(tab, target, ex.Reason), cancellationToken));
        }
        catch (SaveFailureException)
        {
            // Canceled (reserved, not reported today): like a cancel, nothing is shown.
            return (false, PendingSaveChoice.Cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any other failure, also one nobody expected (NotSupportedException, a Win32 error, a bug): the error card,
            // never a silent failed save. The tab stays dirty; the exception text (it may name the path) is not shown.
            if (_disposed || Tabs.IndexOf(tab) < 0)
            {
                return (false, PendingSaveChoice.Cancel);
            }

            return (false, await AskAsync(PendingSave.SaveError(tab, target, SaveFailureReason.WriteFailed), cancellationToken));
        }

        // Back on the UI thread: the tab shows the written file now (path and title after "Save as") and is clean.
        tab.SyncDocumentFromSession();
        tab.UnsaveableReason = null;
        tab.SyncFromEditor();
        return (true, null);
    }

    /// <summary>The file dialog of "Save as", in the folder of <paramref name="current"/> with its name; <see langword="null"/> when cancelled.</summary>
    private async Task<string?> PickTargetAsync(TabViewModel tab, string current)
    {
        string? target;
        try
        {
            target = await _picker.PickSavePdfAsync(Path.GetFileName(current), Path.GetDirectoryName(current));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dialog itself failed (no file chooser portal on Linux, for example): like a cancelled one, the tab keeps
            // its changes; the error card says why the save did not happen.
            await AskAsync(PendingSave.SaveError(tab, current, SaveFailureReason.WriteFailed), CancellationToken.None);
            return null;
        }

        if (string.IsNullOrEmpty(target) || _disposed || Tabs.IndexOf(tab) < 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Shows <paramref name="question"/> and waits for its answer; the tab or window going (or a cancel) answers "Cancel".</summary>
    private async Task<PendingSaveChoice> AskAsync(PendingSave question, CancellationToken cancellationToken)
    {
        question.Clock = Clock;
        PendingSave = question;
        try
        {
            using (cancellationToken.Register(question.Abandon))
            {
                return await question.Choice;
            }
        }
        finally
        {
            if (ReferenceEquals(_pendingSave, question))
            {
                PendingSave = null;
            }
        }
    }

    /// <summary>A tab leaves the window (closed or moved): its save stops before the file is replaced, its question goes.</summary>
    private void CancelSavingOf(TabViewModel tab)
    {
        if (ReferenceEquals(_savingTab, tab))
        {
            _saveCancellation?.Cancel();
        }

        if (_pendingSave is { } question && ReferenceEquals(question.Tab, tab))
        {
            question.Abandon();
        }
    }

    private void DisposeSaving()
    {
        _saveCancellation?.Cancel();
        _pendingSave?.Abandon();
    }

    /// <summary>Takes the progress of a save from the PDFium thread to the tab, on the UI thread.</summary>
    private sealed class SaveProgressReport(TabViewModel tab) : IProgress<double>
    {
        public void Report(double value)
        {
            try
            {
                Dispatcher.UIThread.Post(() => tab.SaveProgress = Math.Clamp(value, 0, 1));
            }
            catch (Exception)
            {
                // The dispatcher is shutting down: nobody sees the progress.
            }
        }
    }
}
