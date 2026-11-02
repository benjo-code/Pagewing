using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.TextEdit;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>The run on the page is not the one that was asked for: the page changed since the runs were read (undo, other tools, a quick second edit). Nothing was changed.</summary>
public sealed class TextRunStaleException : InvalidOperationException
{
    public TextRunStaleException()
        : base("The text on the page is not the text that was asked for.")
    {
    }

    public TextRunStaleException(string message)
        : base(message)
    {
    }

    public TextRunStaleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The run cannot be edited (<see cref="Reason"/>). Nothing was changed.</summary>
public sealed class TextRunLockedException : InvalidOperationException
{
    public TextRunLockedException()
        : this(TextEditLockReason.PageNotRewritable)
    {
    }

    public TextRunLockedException(string message)
        : this(TextEditLockReason.PageNotRewritable, message)
    {
    }

    public TextRunLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a run locked for a reason.</summary>
    /// <param name="reason">Why the run cannot be edited.</param>
    /// <param name="message">A short English explanation.</param>
    public TextRunLockedException(TextEditLockReason reason, string? message = null)
        : base(message ?? $"The text cannot be edited: {reason}.")
    {
        Reason = reason;
    }

    /// <summary>Gets why the run cannot be edited.</summary>
    public TextEditLockReason Reason { get; }
}

/// <summary>The new text was refused as a whole (<see cref="Decision"/>). Nothing was changed.</summary>
public sealed class TextEditRejectedException : InvalidOperationException
{
    public TextEditRejectedException()
        : this(new TextEditDecision(TextEditOutcome.Rejected, [], [], [], false, TextEditRejection.UnsupportedCharacters))
    {
    }

    public TextEditRejectedException(string message)
        : base(message)
    {
        Decision = new TextEditDecision(TextEditOutcome.Rejected, [], [], [], false, TextEditRejection.UnsupportedCharacters);
    }

    public TextEditRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Decision = new TextEditDecision(TextEditOutcome.Rejected, [], [], [], false, TextEditRejection.UnsupportedCharacters);
    }

    /// <summary>Creates the exception for a refused decision.</summary>
    /// <param name="decision">The decision of <see cref="TextEditCheck"/>.</param>
    public TextEditRejectedException(TextEditDecision decision)
        : base("The new text was refused: " + decision.Rejection)
    {
        Decision = decision;
    }

    /// <summary>Gets what <see cref="TextEditCheck"/> decided.</summary>
    public TextEditDecision Decision { get; }
}

/// <summary>PDFium failed while the page was rewritten and the page may have changed. The caller redraws the page and marks the document as changed without a history step.</summary>
public sealed class TextEditWriteException : InvalidOperationException
{
    public TextEditWriteException()
        : base("The text could not be written.")
    {
    }

    public TextEditWriteException(string message)
        : base(message)
    {
    }

    public TextEditWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What <see cref="PdfDocument.ReplaceTextRunAsync"/> and <see cref="PdfDocument.RestoreTextRunAsync"/> did.</summary>
/// <param name="Before">The run as it was.</param>
/// <param name="After">The run as it stands now; <see langword="null"/> when the run was removed.</param>
/// <param name="BoundsBefore">The box of the run before, in page points from the top left.</param>
/// <param name="BoundsAfter">The box of the run now (empty when removed).</param>
/// <param name="Decision">What <see cref="TextEditCheck"/> decided for the new text; <see langword="null"/> for a restore.</param>
/// <param name="Stash">The token under which the objects taken out wait for an undo or redo (0 when nothing changed).</param>
public sealed record TextReplaceResult(TextRun Before, TextRun? After, PageRect BoundsBefore, PageRect BoundsAfter, TextEditDecision? Decision, long Stash);

/// <summary>
/// Editing a line of text (milestone 16, <c>Docs/DECISIONS.md</c>, "Text bearbeiten: Spike (M16)"): the runs of a page, which characters the font of a
/// run can draw, replacing a run and putting the earlier one back. Each call is one short job on the PDFium thread; the change is made in the
/// document in memory, like an annotation, and saved with the document.
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>Reads the runs of a page in the order of the page, locked ones included (with their reason). Changes nothing.</summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<IReadOnlyList<TextRun>> GetTextRunsAsync(int page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        return document.Thread.InvokeAsync(
            () =>
            {
                var read = PdfiumTextEdit.ReadRuns(document, page);
                return (IReadOnlyList<TextRun>)[.. read.Runs.Select(r => ToRun(page, r))];
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>Tells which of the code points the font of a run can draw: those it draws on this page already and that read back as themselves (the rule of the spike).</summary>
    /// <param name="run">The run.</param>
    /// <param name="codePoints">The Unicode scalar values to ask about.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="TextRunStaleException">The page no longer has this run.</exception>
    /// <exception cref="TextRunLockedException">The run is locked.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<IFontCoverage> GetFontCoverageAsync(TextRun run, IEnumerable<int> codePoints, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(codePoints);
        ThrowIfNotAPage(run.Page);
        var codes = codePoints.Distinct().ToArray();
        return document.Thread.InvokeAsync(
            () =>
            {
                var covered = Translate(() => PdfiumTextEdit.FontCoverage(document, run.Page, run.ObjectIndex, run.Fingerprint, codes));
                return (IFontCoverage)new SetCoverage([.. codes.Where((_, i) => covered[i])]);
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>
    /// Replaces the text of a run. The new text is checked against the font of the run (<see cref="TextEditCheck"/>); characters it lacks are set
    /// in Geist Regular, or the whole run is, as <paramref name="mode"/> says; an empty text removes the run. The page is rewritten on a copy first and
    /// the document is touched only when that copy shows nothing else changing.
    /// </summary>
    /// <param name="run">The run as <see cref="GetTextRunsAsync"/> gave it.</param>
    /// <param name="newText">The new text.</param>
    /// <param name="mode">Whether the font of the run stays or the whole run goes to Geist.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <returns>What happened; for an unchanged text <see cref="TextReplaceResult.After"/> is the run itself and nothing was written.</returns>
    /// <exception cref="TextRunStaleException">The page no longer has this run; nothing was changed.</exception>
    /// <exception cref="TextRunLockedException">The run is locked, or rewriting the page would change something else; nothing was changed.</exception>
    /// <exception cref="TextEditRejectedException">A new character is in neither font, or the text is too long; nothing was changed.</exception>
    /// <exception cref="TextEditWriteException">PDFium failed while writing; the page may have changed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<TextReplaceResult> ReplaceTextRunAsync(TextRun run, string newText, TextEditMode mode, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(newText);
        ThrowIfNotAPage(run.Page);
        if (run.Lock is { } reason)
        {
            throw new TextRunLockedException(reason);
        }

        return document.Thread.InvokeAsync(
            () =>
            {
                var oldCodes = run.Text.EnumerateRunes().Select(static r => r.Value).ToHashSet();
                var wanted = newText.EnumerateRunes().Select(static r => r.Value).Where(c => !oldCodes.Contains(c)).Distinct().ToArray();
                var covered = wanted.Length == 0 ? [] : Translate(() => PdfiumTextEdit.FontCoverage(document, run.Page, run.ObjectIndex, run.Fingerprint, wanted));
                var decision = TextEditCheck.Evaluate(run.Text, newText, new SetCoverage([.. wanted.Where((_, i) => covered[i])]), mode);
                switch (decision.Outcome)
                {
                    case TextEditOutcome.Unchanged:
                        return new TextReplaceResult(run, run, run.Bounds, run.Bounds, decision, 0);
                    case TextEditOutcome.Rejected:
                        throw new TextEditRejectedException(decision);
                }

                var segments = decision.Outcome == TextEditOutcome.Remove ? [] : decision.Segments.Select(static s => new PdfiumSegment(s.Text, s.InGeist)).ToArray();
                var edited = Translate(() => PdfiumTextEdit.Replace(document, run.Page, run.ObjectIndex, run.Fingerprint, segments, TextBoxFont.ReadData));
                return new TextReplaceResult(
                    run,
                    edited.After is null ? null : ToRun(run.Page, edited.After),
                    ToRect(edited.BoxBefore, run.Bounds),
                    edited.After is null ? run.Bounds with { Right = run.Bounds.Left, Bottom = run.Bounds.Top } : ToRect(edited.BoxAfter, run.Bounds),
                    decision,
                    edited.Stash);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Puts back the objects an earlier <see cref="ReplaceTextRunAsync"/> (or this call) took out: they replace the run that stands now
    /// (<paramref name="current"/>, <see langword="null"/> when the earlier edit removed the run). Undo is this call with the stash of the edit; redo
    /// is this call with the stash it returns.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="at">The index of the first page object the objects go to.</param>
    /// <param name="current">The run that stands there now, or <see langword="null"/> when nothing stands there.</param>
    /// <param name="stash">The token of the waiting objects.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="TextRunStaleException">The page no longer has the current run; nothing was changed.</exception>
    /// <exception cref="TextEditWriteException">PDFium failed while writing, or the waiting objects are gone; the page may have changed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<TextReplaceResult> RestoreTextRunAsync(int page, int at, TextRun? current, long stash, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        return document.Thread.InvokeAsync(
            () =>
            {
                var edited = Translate(() => PdfiumTextEdit.Swap(document, page, at, current?.ObjectCount ?? 0, current?.Fingerprint ?? string.Empty, stash));
                var restored = edited.After is null ? null : ToRun(page, edited.After);
                var before = current ?? new TextRun(page, at, 0, string.Empty, default, string.Empty, TextRunFontKind.NotEmbedded, 0, default, string.Empty, null);
                return new TextReplaceResult(before, restored, ToRect(edited.BoxBefore, default), ToRect(edited.BoxAfter, default), null, edited.Stash);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    private static T Translate<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (PdfiumTextEditException exception) when (exception.Failure == PdfiumTextEditFailure.Stale)
        {
            throw new TextRunStaleException(exception.Message, exception);
        }
        catch (PdfiumTextEditException exception) when (exception.Failure == PdfiumTextEditFailure.Locked)
        {
            throw new TextRunLockedException(MapLock(exception.Lock), exception.Message);
        }
        catch (PdfiumTextEditException exception) when (exception.Failure == PdfiumTextEditFailure.NotRewritable)
        {
            throw new TextRunLockedException(TextEditLockReason.PageNotRewritable, exception.Message);
        }
        catch (PdfiumTextEditException exception)
        {
            throw new TextEditWriteException(exception.Message, exception);
        }
    }

    private static TextEditLockReason MapLock(PdfiumTextLock reason) => reason switch
    {
        PdfiumTextLock.Invisible => TextEditLockReason.InvisibleText,
        PdfiumTextLock.Type3 => TextEditLockReason.Type3Font,
        PdfiumTextLock.Vertical => TextEditLockReason.VerticalWriting,
        PdfiumTextLock.RenderMode => TextEditLockReason.UnsupportedRenderMode,
        PdfiumTextLock.Form => TextEditLockReason.FormXObject,
        PdfiumTextLock.MarkedContent => TextEditLockReason.MarkedContent,
        PdfiumTextLock.TooManyRuns => TextEditLockReason.TooManyRuns,
        PdfiumTextLock.RunTooLong => TextEditLockReason.RunTooLong,
        PdfiumTextLock.Ligature => TextEditLockReason.Ligature,
        _ => TextEditLockReason.PageNotRewritable,
    };

    private static PageRect ToRect(PdfiumTextArea area, PageRect fallback) => area.IsEmpty ? fallback : new PageRect(area.Left, area.Top, area.Right, area.Bottom);

    private static TextRun ToRun(int page, PdfiumTextRun run) => new(
        page,
        run.Index,
        run.Count,
        run.Text,
        ToRect(run.Box, default),
        run.FontName,
        run.FontName.Length == 0 ? TextRunFontKind.Type3 : run.Embedded ? TextRunFontKind.Embedded : run.Standard14 ? TextRunFontKind.Standard14 : TextRunFontKind.NotEmbedded,
        run.Size,
        new AnnotationColor(run.Red, run.Green, run.Blue),
        run.Fingerprint,
        run.Lock == PdfiumTextLock.None ? null : MapLock(run.Lock));

    private sealed class SetCoverage(IReadOnlySet<int> codePoints) : IFontCoverage
    {
        public SetCoverage(IEnumerable<int> codePoints)
            : this(new HashSet<int>(codePoints))
        {
        }

        public bool Covers(int codePoint) => codePoints.Contains(codePoint);
    }
}
