using System.Text;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Applying the redaction marks of a tab (milestone 14, spec 5.5 and 5.6): the marks are a plan until here, and redacting removes
/// the marked content for good by a full rewrite of the file that cannot be undone. The flow asks first whatever would otherwise be
/// lost silently: comments and form values that are not saved go through the incremental save first (they never flow into the
/// rewrite). Then the analysis runs <em>before</em> anything is built (<see cref="RedactionExecutor.AnalyzeAsync(RedactionPlan, PdfDocument, string, string?, CancellationToken)"/>)
/// and a refusal ends in a card without "Apply" (encrypted file, page keys Pagewing cannot remove, a page that cannot be redacted,
/// each with its pages), the plan stays. Otherwise the card shows the numbers, what is removed as a whole, what is cleaned, what
/// changes and what is NOT cleaned or checked; "Save as …" with the name "&lt;name&gt;-redacted.pdf" is its default, "Save in the
/// original file" is dangerous (locked at first, never the default) and the app never overwrites a changed original. The work runs
/// in the background through the checked rewrite of <see cref="RedactionApplier"/>; a check that finds anything ends in a card with the
/// findings, nothing is written and the plan stays. After success the tab shows the redacted file where the marks were, the mode
/// ends, and the report (removed, cleaned, lost, checked, not checked) is shown.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Gets or sets what writes the redacted file (<see cref="RedactionApplier.ApplyAsync(ViewerSession, PdfEngine, RedactionApplyRequest, IProgress{double}?, CancellationToken)"/>
    /// when <see langword="null"/>; tests make it fail in ways the applier does not, as with <see cref="PageRewriter"/>).
    /// </summary>
    internal Func<ViewerSession, PdfEngine, RedactionApplyRequest, IProgress<double>?, CancellationToken, Task<RedactionApplyResult>>? RedactionRewriter { get; set; }

    /// <summary>
    /// Gets or sets how the analysis of a plan is made, for the card and the preview (<see cref="RedactionExecutor"/> when
    /// <see langword="null"/>; tests give an analysis with every kind of line, or a refusal). The document is the one shown, the
    /// path the file it was opened from.
    /// </summary>
    internal Func<RedactionPlan, PdfDocument, string, CancellationToken, Task<RedactionAnalysis>>? RedactionAnalyzer { get; set; }

    /// <summary>
    /// Applies the marks of <paramref name="tab"/> (the button "Apply …", Ctrl+S and "Save as" while marks wait). Nothing happens
    /// without marks, while another save or question of the window waits, while the tab prints or is being asked about, or while
    /// an update has locked editing.
    /// </summary>
    /// <returns><see langword="true"/> when the redacted file was written; <see langword="false"/> when nothing was written (back, cancelled, refused, failed, not possible). The marks stay in every case but success.</returns>
    internal Task<bool> ApplyRedactionsAsync(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        // A nudge of the selection that waits for the keys to rest belongs to the comments saved first.
        tab.FlushNudge();
        if (_disposed || _savingTab is not null || _pendingSave is not null || Tabs.IndexOf(tab) < 0 || !tab.CanApplyRedactions || tab.FullPath is null)
        {
            return Task.FromResult(false);
        }

        var run = RunRedactionAsync(tab);
        _saveRun = run;
        return run;
    }

    private async Task<bool> RunRedactionAsync(TabViewModel tab)
    {
        var outcome = await ApplyRedactionsCoreAsync(tab);
        if (outcome.Report is { } report && outcome.Path is { } written && !_disposed && Tabs.IndexOf(tab) >= 0)
        {
            // The report waits for the user on its own, after the flow ended: closing the tab or the window never waits for it.
            _ = ShowRedactionReportAsync(tab, written, report);
        }

        return outcome.Done;
    }

    private async Task ShowRedactionReportAsync(TabViewModel tab, string path, RedactionApplyInfo report)
    {
        try
        {
            await AskAsync(PendingSave.RedactionReport(tab, path, report), CancellationToken.None);
        }
        catch (Exception)
        {
            // The report is information; a failure to show it (the window went) loses nothing.
        }
    }

    private async Task<RedactionOutcome> ApplyRedactionsCoreAsync(TabViewModel tab)
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
            if (!await SaveDocumentChangesFirstAsync(tab, path, token, redaction: true))
            {
                return RedactionOutcome.None;
            }

            // The tab may have moved on while it asked (it closed, another file opened in it).
            if (!tab.HasPendingRedactions || tab.RedactionPlan is not { } plan || tab.Document is not { } document || tab.FullPath is not { } current)
            {
                return RedactionOutcome.None;
            }

            // The analysis comes before anything is built, and a refusal is honoured here: the build does not read it again.
            RedactionAnalysis analysis;
            try
            {
                analysis = await AnalyzeRedactionsAsync(plan, document, current, token);
            }
            catch (SaveFailureException ex) when (ex.Reason is SaveFailureReason.SourceModified or SaveFailureReason.OriginalChanged)
            {
                await AskIfShownAsync(tab, PendingSave.PageApplyError(tab, current, static () => Strings.RedactErrorFileChanged), token);
                return RedactionOutcome.None;
            }
            catch (Exception ex) when (ex is RedactionException or PdfOpenException or InvalidDataException or IOException or InvalidOperationException or ObjectDisposedException or SaveFailureException)
            {
                // A mark on a page the document does not have, a page that cannot be read: nothing is written.
                await AskIfShownAsync(tab, PendingSave.PageApplyError(tab, current, static () => Strings.RedactErrorNotPrepared), CancellationToken.None);
                return RedactionOutcome.None;
            }

            if (analysis.IsRefused)
            {
                await AskAsync(PendingSave.RedactionRefused(tab, current, RedactionApplyInfo.ForRefusals(analysis.Refusals)), token);
                return RedactionOutcome.None;
            }

            var choice = await AskAsync(PendingSave.ApplyRedactions(tab, current, RedactionApplyInfo.Create(analysis)), token);
            var target = choice switch
            {
                PendingSaveChoice.SaveAs => await PickTargetAsync(tab, SuggestedRedactedPath(current)),
                PendingSaveChoice.SaveInOriginal => current,
                _ => null,
            };
            while (target is not null && !token.IsCancellationRequested)
            {
                var attempt = await RedactOnceAsync(tab, plan, analysis, target, token);
                switch (attempt.Choice)
                {
                    case null:
                        return attempt.Outcome;
                    case PendingSaveChoice.SaveAs:
                        target = await PickTargetAsync(tab, target);
                        break;
                    default:
                        return RedactionOutcome.None;
                }
            }

            return RedactionOutcome.None;
        }
        catch (OperationCanceledException)
        {
            // The tab or the window went, or the user cancelled: nothing is shown.
            return RedactionOutcome.None;
        }
        catch (Exception)
        {
            // Anything else (the file dialog, a bug): never a silent failure. The card says nothing was written; the tab keeps its
            // document and its marks. The exception text (it may name a path) is not shown.
            await AskIfShownAsync(tab, PendingSave.SaveError(tab, path, SaveFailureReason.WriteFailed), CancellationToken.None);
            return RedactionOutcome.None;
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
    /// Writes, checks and adopts the redacted file to <paramref name="target"/> in the background (<see cref="RedactionApplier"/>). A
    /// refusal that the analysis did not see and a check that finds something end in a card without anything written; a file that
    /// is not the one that was opened ends in a card with "Cancel" alone, and nothing overwrites a changed original; a failure after
    /// the write says the file is written. The marks and the document stay in every failure before the write.
    /// </summary>
    private async Task<(RedactionOutcome Outcome, PendingSaveChoice? Choice)> RedactOnceAsync(
        TabViewModel tab, RedactionPlan plan, RedactionAnalysis analysis, string target, CancellationToken token)
    {
        var progress = new SaveProgressReport(tab);
        var page = Math.Max(tab.CurrentPage - 1, 0);
        var written = false;
        RedactionApplyResult result;
        try
        {
            // On the thread pool: it reads file stamps, and the adoption closes the old document, never on the UI thread. The callback
            // tells the tab which document is about to arrive, so it is shown as the same file with the same view.
            var request = new RedactionApplyRequest(plan, target, OverwriteChangedOriginal: false, Rewritten: rewritten => tab.ExpectRewrittenDocument(rewritten, page));
            var rewriter = RedactionRewriter ?? RedactionApplier.ApplyAsync;
            result = await Task.Run(() => rewriter(tab.Session, SaveEngine, request, progress, token), token);
            written = true;
            if (!result.Adopted)
            {
                // The tab closed or opened another file meanwhile: the written file is the user's, nothing here is left to show it.
                tab.ForgetRewrittenDocument();
                await result.Saved.Reopened.DisposeAsync();
            }
        }
        catch (RedactionRefusedException ex) when (ex.Refusals.Count > 0)
        {
            // The analysis lists the same, but the file changed meanwhile or was not consulted: nothing was written.
            return (RedactionOutcome.None, await AskIfShownAsync(tab, PendingSave.RedactionRefused(tab, target, RedactionApplyInfo.ForRefusals(ex.Refusals)), token));
        }
        catch (SaveFailureException ex) when (ex.Reason == SaveFailureReason.RedactionFailed)
        {
            // The check of the written file found something (or could not be completed): nothing was written, the plan stays.
            return (RedactionOutcome.None, await AskIfShownAsync(tab, PendingSave.RedactionFailed(tab, target, RedactionApplyInfo.ForFailure(ex.Details as RedactionCheck)), token));
        }
        catch (SaveFailureException ex) when (ex.Reason is SaveFailureReason.OriginalChanged or SaveFailureReason.SourceModified)
        {
            // Another program changed the file since it was opened: the marks cannot be applied to it, and nothing overwrites it.
            return (RedactionOutcome.None, await AskIfShownAsync(tab, PendingSave.PageApplyError(tab, target, static () => Strings.RedactErrorFileChanged), token));
        }
        catch (SaveFailureException ex) when (ex.Reason != SaveFailureReason.Canceled)
        {
            return (RedactionOutcome.None, await AskIfShownAsync(tab, PendingSave.SaveError(tab, target, ex.Reason), token));
        }
        catch (SaveFailureException)
        {
            return (RedactionOutcome.None, PendingSaveChoice.Cancel);
        }
        catch (RedactionAdoptException ex)
        {
            // The file IS redacted and checked, the tab shows the old document: it must not keep marks for it or offer to apply
            // again. The card says so; then the written file is opened in the tab through the normal open path.
            tab.ForgetRewrittenDocument();
            if (!_disposed && Tabs.IndexOf(tab) >= 0)
            {
                await AskAsync(
                    PendingSave.PageApplyError(
                        tab,
                        ex.TargetPath,
                        () => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ApplyErrorViewFailed), ex.TargetPath),
                        () => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.RedactViewFailedTitle), Path.GetFileName(ex.TargetPath))),
                    CancellationToken.None);
                tab.AbandonRedactions();
                _ = tab.OpenAsync(ex.TargetPath);
            }

            return (new RedactionOutcome(true, null, null), null);
        }
        catch (Exception ex) when (ex is RedactionException or PdfOpenException or InvalidDataException or InvalidOperationException or RedactionRefusedException)
        {
            // The marks no longer fit, a file cannot be read: nothing was written.
            return (RedactionOutcome.None, _disposed || Tabs.IndexOf(tab) < 0 ? PendingSaveChoice.Cancel : await AskAsync(PendingSave.PageApplyError(tab, target, static () => Strings.RedactErrorNotPrepared), token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_disposed || Tabs.IndexOf(tab) < 0)
            {
                return (RedactionOutcome.None, PendingSaveChoice.Cancel);
            }

            return (RedactionOutcome.None, await AskAsync(PendingSave.SaveError(tab, target, SaveFailureReason.WriteFailed), token));
        }
        finally
        {
            // After a success the claim stays until the tab has taken the document (a refresh that is posted may come after this
            // point; the claim is for exactly that document, so a left-over one does no harm); a failure takes it back.
            if (!written)
            {
                tab.ForgetRewrittenDocument();
            }
        }

        // Back on the UI thread: the tab shows the redacted file now, where the marks were, and the plan and the mode are gone.
        tab.SyncDocumentFromSession();
        tab.UnsaveableReason = null;
        tab.SyncFromEditor();
        return (new RedactionOutcome(true, RedactionApplyInfo.ForReport(analysis, result.Check), target), null);
    }

    /// <summary>Works out what redacting <paramref name="plan"/> does, in the background (the seam <see cref="RedactionAnalyzer"/> or the executor).</summary>
    private Task<RedactionAnalysis> AnalyzeRedactionsAsync(RedactionPlan plan, PdfDocument document, string path, CancellationToken token)
    {
        var seam = RedactionAnalyzer;
        var engine = SaveEngine;
        return Task.Run(
            () => seam is not null
                ? seam(plan, document, path, token)

                // An encrypted file is refused by the analysis, so no password is ever needed here.
                : new RedactionExecutor(engine).AnalyzeAsync(plan, document, path, null, token),
            token);
    }

    // The preview's numbers: the same analysis for the plan as it is now, on the file the tab shows.
    private Task<RedactionAnalysis> AnalyzeRedactionsOfAsync(TabViewModel tab, RedactionPlan plan, CancellationToken token) =>
        tab.Document is { } document && tab.FullPath is { } path
            ? AnalyzeRedactionsAsync(plan, document, path, token)
            : Task.FromException<RedactionAnalysis>(new InvalidOperationException("The tab has no open document."));

    /// <summary>Shows <paramref name="question"/> unless the tab or the window went meanwhile (a question for a tab that is gone would never be answered).</summary>
    private async Task<PendingSaveChoice> AskIfShownAsync(TabViewModel tab, PendingSave question, CancellationToken token) =>
        _disposed || Tabs.IndexOf(tab) < 0 ? PendingSaveChoice.Cancel : await AskAsync(question, token);

    // "<folder>/<name>-redacted.pdf": the file dialog of "Save as …" starts with this name; the original is never the suggestion.
    private static string SuggestedRedactedPath(string current) =>
        Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, Path.GetFileNameWithoutExtension(current) + "-redacted.pdf");

    /// <summary>How an apply of the marks ended: whether the file was written, the report to show and the path it was written to.</summary>
    private readonly record struct RedactionOutcome(bool Done, RedactionApplyInfo? Report, string? Path)
    {
        public static RedactionOutcome None => new(false, null, null);
    }
}
