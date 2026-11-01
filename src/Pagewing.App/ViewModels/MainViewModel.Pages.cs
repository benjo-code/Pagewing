using System.Text;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Applying the page changes of a tab (milestone 13, spec 5.2 and 5.3): the staged plan is written by a full rewrite, which
/// cannot be undone. The flow asks first whatever would otherwise be lost silently: comments and form values that are not saved
/// go through the incremental save first (they never flow into the rewrite); the report of what the rewrite loses is read
/// before the card (a refused plan is never written); the card shows the summary and every loss, and "Save as …" is its
/// default. The work runs in the background, one save at a time per window like every other save; the tab keeps its document
/// and its plan until the rewrite succeeded, and on every failure and cancellation the target is byte for byte as it was.
/// After success the tab shows the rewritten file where the plan led its current page, with the same zoom and panel, and the
/// mode ends (the plan has no pages left to change).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Gets or sets what writes the working copy and checks the file (<see cref="DocumentSaver"/> when <see langword="null"/>;
    /// tests make it fail in ways the saver does not, as with <see cref="DocumentWriter"/>).
    /// </summary>
    internal Func<PdfDocument, PdfEngine, RewriteRequest, IProgress<double>?, CancellationToken, Task<SaveResult>>? PageRewriter { get; set; }

    /// <summary>
    /// Gets or sets how the report of what a plan loses is read before the card
    /// (<see cref="PageEditExecutor.AnalyzeAsync"/> with the open documents of the files inserted into the plan; tests give a
    /// report with every kind of loss).
    /// </summary>
    internal Func<PageEditPlan, PdfDocument, IReadOnlyDictionary<PageSourceId, PdfDocument>, CancellationToken, Task<PageEditReport>> PageAnalyzer { get; set; } =
        static (plan, document, inserted, cancellationToken) => PageEditExecutor.AnalyzeAsync(plan, document, inserted, cancellationToken);

    /// <summary>
    /// Applies the staged page changes of <paramref name="tab"/> (the button "Apply", Ctrl+S and "Save as" while a plan waits).
    /// Nothing happens without a plan, while another save or question of the window waits, while the tab prints or is
    /// being asked about, or while an update has locked editing.
    /// </summary>
    /// <returns><see langword="true"/> when the rewritten file was written; <see langword="false"/> when nothing was written (back, cancelled, failed, not possible). The plan stays in every case but success.</returns>
    internal Task<bool> ApplyPageEditsAsync(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        // A nudge of the selection that waits for the keys to rest belongs to the comments saved first.
        tab.FlushNudge();
        if (_disposed || _savingTab is not null || _pendingSave is not null || Tabs.IndexOf(tab) < 0 || !tab.CanApplyPages || tab.FullPath is null)
        {
            return Task.FromResult(false);
        }

        var run = ApplyPagesCoreAsync(tab);
        _saveRun = run;
        return run;
    }

    private async Task<bool> ApplyPagesCoreAsync(TabViewModel tab)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _savingTab = tab;
        _saveCancellation = cancellation;
        tab.IsSaving = true;
        tab.SaveProgress = 0;
        var path = tab.FullPath!;
        try
        {
            if (!await SaveDocumentChangesFirstAsync(tab, path, token))
            {
                return false;
            }

            // The tab may have moved on while it asked (it closed, another file opened in it).
            if (!tab.HasPendingPageEdits || tab.PagePlan is not { } plan || tab.Document is not { } document || tab.FullPath is not { } current)
            {
                return false;
            }

            PageEditReport report;
            var insertedDocuments = tab.InsertedDocuments();
            try
            {
                report = await Task.Run(() => PageAnalyzer(plan, document, insertedDocuments, token), token);
            }
            catch (Exception ex) when (ex is PageEditException or PdfOpenException or InvalidDataException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The plan does not fit what is open (the document went meanwhile), or a page cannot be read: nothing is written.
                if (!_disposed && Tabs.IndexOf(tab) >= 0)
                {
                    await AskAsync(PendingSave.PageApplyError(tab, current, static () => Strings.ApplyErrorNotPrepared), CancellationToken.None);
                }

                return false;
            }

            if (report.IsRefused)
            {
                // Such a plan is never written (an encrypted file into a document without protection); the plan stays.
                await AskAsync(PendingSave.PageApplyError(tab, current, static () => Strings.ApplyErrorRefusedEncrypted), token);
                return false;
            }

            var choice = await AskAsync(PendingSave.ApplyPages(tab, current, ApplyPagesInfo.Create(plan, report, tab.InsertedNames())), token);
            var target = choice switch
            {
                PendingSaveChoice.SaveAs => await PickTargetAsync(tab, current),
                PendingSaveChoice.SaveInOriginal => current,
                _ => null,
            };
            while (target is not null && !token.IsCancellationRequested)
            {
                var outcome = await ApplyOnceAsync(tab, plan, target, token);
                switch (outcome.Choice)
                {
                    case null:
                        return outcome.Done;
                    case PendingSaveChoice.SaveAs:
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
            // Anything else (the file dialog, a bug): never a silent failure. The card says nothing was written; the tab keeps
            // its document and its plan. The exception text (it may name a path) is not shown.
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

    /// <summary>
    /// Comments and form values that are not saved stay out of the rewrite, so they are saved first: the card asks ("Save" or
    /// "Cancel", the plan stays), then the incremental save runs with its usual questions. Typed text that a form field
    /// refused cannot be saved and would be lost: the apply stops with the reason (undoing or discarding the plan lets the user
    /// correct the text). Nothing in the tab that is not saved is ever dropped by the apply.
    /// </summary>
    /// <returns>Whether the apply goes on.</returns>
    private async Task<bool> SaveDocumentChangesFirstAsync(TabViewModel tab, string path, CancellationToken token)
    {
        // The text of an open text box field and the open value of a form field become changes of the editor (or stay as
        // refused text, which counts below).
        tab.FlushTextBoxDraft();
        tab.FlushFormDraft();
        await tab.WhenEditsSettledAsync();
        if (tab.HasEditorChanges)
        {
            if (tab.UnsaveableReason is { } reason)
            {
                // Every save of this tab fails the same way: say it, and keep the plan.
                await AskAsync(PendingSave.SaveError(tab, path, reason), token);
                return false;
            }

            if (await AskAsync(PendingSave.SaveAnnotationsFirst(tab, path), token) != PendingSaveChoice.Save)
            {
                return false;
            }

            if (tab.Document is not { } document || !await SaveTabCoreAsync(tab, document, path, saveAs: false, outer: token))
            {
                // Not saved: the save showed why on its card, or the user cancelled there.
                return false;
            }

            await tab.WhenEditsSettledAsync();
        }

        if (tab.HasFormDraft || tab.HasTextBoxText)
        {
            await AskAsync(PendingSave.PageApplyError(tab, path, static () => Strings.ApplyErrorDraft), token);
            return false;
        }

        if (tab.AsksAboutDocumentChanges)
        {
            await AskAsync(PendingSave.PageApplyError(tab, path, static () => Strings.ApplyErrorNotPrepared), token);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds, writes, checks and adopts the rewrite to <paramref name="target"/> in the background (<see cref="PageEditApplier"/>).
    /// A failure asks the user as the save does (save as, cancel), with two differences: a file that is not the one that was opened
    /// (<see cref="SaveFailureReason.OriginalChanged"/> and <see cref="SaveFailureReason.SourceModified"/>) ends in a card with
    /// "Cancel" alone, and there is no overwriting of the changed original, because the pages are built from the file at the
    /// path; and a failure after the write (<see cref="PageApplyAdoptException"/>) says the file is written. The plan and the
    /// document stay in every failure before the write.
    /// </summary>
    /// <returns>Whether the file was written, or the answer of the user to a failure.</returns>
    private async Task<(bool Done, PendingSaveChoice? Choice)> ApplyOnceAsync(TabViewModel tab, PageEditPlan plan, string target, CancellationToken token)
    {
        var progress = new SaveProgressReport(tab);
        var follow = plan.FollowPage(Math.Clamp(tab.CurrentPage - 1, 0, plan.PrimaryPageCount - 1));
        var written = false;
        try
        {
            // On the thread pool: it reads file stamps, and the adoption closes the old document, never on the UI thread. The
            // callback tells the tab which document is about to arrive, so it is shown as the same file in other pages.
            var request = new PageApplyRequest(plan, target, tab.InsertedFiles(), OverwriteChangedOriginal: false, rewritten => tab.ExpectRewrittenDocument(rewritten, follow));
            var result = await Task.Run(() => PageEditApplier.ApplyAsync(tab.Session, SaveEngine, request, progress, token, PageRewriter), token);
            written = true;
            if (!result.Adopted)
            {
                // The tab closed or opened another file meanwhile: nothing here is left to take the claim.
                tab.ForgetRewrittenDocument();
            }
        }
        catch (SaveFailureException ex) when (ex.Reason == SaveFailureReason.EncryptedNotSupported && ex.FilePath is { } locked)
        {
            // The analysis refuses this first; this is the applier's own refusal of an encrypted inserted file in a plain document.
            var lockedName = Path.GetFileName(locked);
            return (false, await AskAsync(PendingSave.PageApplyError(tab, target, () => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ApplyErrorEncryptedInserted), lockedName)), token));
        }
        catch (SaveFailureException ex) when (ex.FilePath is { } inserted)
        {
            // An inserted file is not the file that was inserted (changed, replaced, deleted or unreadable since): the card
            // names it. Nothing was written, the plan and the file stay in the tab; the user removes its pages or inserts it again.
            var name = Path.GetFileName(inserted);
            return (false, await AskAsync(PendingSave.PageApplyError(tab, target, () => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ApplyErrorInsertedChanged), name)), token));
        }
        catch (SaveFailureException ex) when (ex.Reason is SaveFailureReason.OriginalChanged or SaveFailureReason.SourceModified)
        {
            // Another program changed the file since it was opened: the page changes cannot be written onto it, and nothing
            // overwrites it. The tab keeps its plan; the user reopens the file.
            return (false, await AskAsync(PendingSave.PageApplyError(tab, target, static () => Strings.ApplyErrorFileChanged), token));
        }
        catch (SaveFailureException ex) when (ex.Reason != SaveFailureReason.Canceled)
        {
            return (false, await AskAsync(PendingSave.SaveError(tab, target, ex.Reason), token));
        }
        catch (SaveFailureException)
        {
            return (false, PendingSaveChoice.Cancel);
        }
        catch (PageApplyAdoptException ex)
        {
            // The file IS rewritten, the tab shows the old document: it must not keep a plan for it or offer to apply again.
            // The card says so; then the written file is opened in the tab through the normal open path.
            tab.ForgetRewrittenDocument();
            if (!_disposed && Tabs.IndexOf(tab) >= 0)
            {
                await AskAsync(PendingSave.PageApplyViewFailed(tab, ex.TargetPath), CancellationToken.None);
                tab.AbandonPageEdits();
                _ = tab.OpenAsync(ex.TargetPath);
            }

            return (true, null);
        }
        catch (Exception ex) when (ex is PageEditException or PdfOpenException or InvalidDataException or InvalidOperationException)
        {
            // The plan no longer fits, a file cannot be read: nothing was written.
            return (false, _disposed || Tabs.IndexOf(tab) < 0 ? PendingSaveChoice.Cancel : await AskAsync(PendingSave.PageApplyError(tab, target, static () => Strings.ApplyErrorNotPrepared), token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_disposed || Tabs.IndexOf(tab) < 0)
            {
                return (false, PendingSaveChoice.Cancel);
            }

            return (false, await AskAsync(PendingSave.SaveError(tab, target, SaveFailureReason.WriteFailed), token));
        }
        finally
        {
            // After a success the claim stays until the tab has taken the document (a refresh that is posted may come after
            // this point; the claim is for exactly that document, so a left-over one does no harm); a failure takes it back.
            if (!written)
            {
                tab.ForgetRewrittenDocument();
            }
        }

        // Back on the UI thread: the tab shows the rewritten file now, where the plan led its page, and the plan is gone.
        tab.SyncDocumentFromSession();
        tab.UnsaveableReason = null;
        tab.SyncFromEditor();
        return (true, null);
    }
}
