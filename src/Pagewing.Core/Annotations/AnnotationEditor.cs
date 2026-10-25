using System.Runtime.ExceptionServices;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.Pdfium;

namespace Pagewing.Core.Annotations;

/// <summary>
/// Changes the text markup and the drawings of the document of a <see cref="ViewerSession"/> and keeps the undo history
/// and the "unsaved changes" state of it; saves the document and lets the session adopt the saved file.
/// </summary>
/// <remarks>
/// <para>
/// Every change is a short job on the PDFium thread through <see cref="ViewerSession.Document"/>; the methods may be
/// called from any thread (typically the UI thread), never block the caller and never call PDFium on its thread. They
/// run one after the other: a call that comes while another one is running waits for it (an asynchronous gate), so two
/// quick changes, or a change and an undo, never interleave. After each change the editor drops the images, thumbnails
/// and text of the changed page (<see cref="ViewerSession.InvalidatePages"/>), records the page in
/// <see cref="ChangedPages"/>, puts the step on the history and raises <see cref="Changed"/>.
/// </para>
/// <para>
/// Undo restores by creating anew, not byte for byte: a deleted markup is created again from its copy (kind, lines,
/// colour, comment, author) under its old <c>/NM</c>, so the later steps of the history still find it; keys of another
/// application that Pagewing does not read are lost then. A deleted drawing is created again the same way from its
/// <see cref="PdfAnnotation.Drawing"/> (a colour it did not have comes back black, as PDFium draws such a drawing). A
/// change is undone by applying the old colour and comment again; a drawing also gets its old shape back (it is replaced
/// by the old one, under the same name). A colour that the annotation did not have cannot be restored as missing: the
/// annotation keeps the one it has. A move, a resize or a change of colour and width together is one step.
/// Saving empties the history: what is saved cannot be undone.
/// </para>
/// <para>
/// The history belongs to the document the editor worked on. When the session opens another document meanwhile, the
/// next call forgets the history and starts clean (the caller asks about unsaved changes before it opens another file).
/// </para>
/// <para>
/// A step of undo or redo that fails is put back only when it was canceled or the document was being closed
/// (<see cref="OperationCanceledException"/>, <see cref="ObjectDisposedException"/>), so it can be tried again. Any other
/// failure (the annotation is gone, PDFium refuses, the copy cannot be created again) would fail the same way every time:
/// that step is dropped from the history, its exception reaches the caller once, and the older steps stay usable.
/// </para>
/// <para>
/// Re-entrancy: an operation raises <see cref="ViewerSession.Changed"/> of the session on its own thread while it runs
/// (<see cref="ViewerSession.InvalidatePages"/>, and the adoption after a save). A handler of
/// <see cref="ViewerSession.Changed"/> must therefore never wait synchronously for an operation of the editor: that
/// operation waits for the one that raised the event, which waits for the handler, a deadlock. Starting one without
/// waiting for it is fine.
/// </para>
/// </remarks>
public sealed class AnnotationEditor
{
    private readonly ViewerSession session;

    // Guards everything below. Never held while an event is raised, PDFium is called or the session is called.
    private readonly object gate = new();
    private readonly AnnotationHistory history = new();
    private readonly HashSet<int> changedPages = [];
    private int versionAtLastSave;
    private PdfDocument? bound;

    // The end of the last operation that was started: the next one waits for it, so the operations run one after the
    // other in the order they were called (an asynchronous gate that needs no disposing).
    private Task tail = Task.CompletedTask;

    // The operations accepted (called) and not ended yet; see IsBusy.
    private int pending;

    /// <summary>Creates the editor of the document that <paramref name="session"/> has open now or opens later.</summary>
    /// <param name="session">The session; the editor does not own it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public AnnotationEditor(ViewerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.session = session;
    }

    /// <summary>
    /// Occurs on any thread after every change of <see cref="IsDirty"/>, <see cref="CanUndo"/>, <see cref="CanRedo"/> or
    /// <see cref="ChangedPages"/>, after the operation has ended and never while the editor holds a lock; marshal to the
    /// UI thread if needed. A handler may start another operation, but should not block until it completes.
    /// </summary>
    public event Action? Changed;

    /// <summary>Gets whether the document has changes that are not saved: the history moved since the last save.</summary>
    public bool IsDirty
    {
        get
        {
            lock (gate)
            {
                return history.Version != versionAtLastSave;
            }
        }
    }

    /// <summary>
    /// Gets the version of the history: it changes with every step (a change, undo, redo, a save, a forgotten history) and
    /// never comes back to an earlier value, so a caller can tell whether anything happened since it last looked (the
    /// question before closing: a "Don't save" counts only for the version it was given for).
    /// </summary>
    public int Version
    {
        get
        {
            lock (gate)
            {
                return history.Version;
            }
        }
    }

    /// <summary>
    /// Gets whether an operation was called and has not ended yet: from the moment a call is accepted (before its first
    /// await) until its step is recorded (or it failed). A change in flight is not on the history yet, so
    /// <see cref="IsDirty"/> does not see it; whoever must not lose changes (closing a tab) counts a busy editor as dirty
    /// and waits for <see cref="WhenIdleAsync"/>. A save keeps the editor busy as well. Changing it raises no
    /// <see cref="Changed"/>.
    /// </summary>
    public bool IsBusy
    {
        get
        {
            lock (gate)
            {
                return pending > 0;
            }
        }
    }

    /// <summary>Gets whether <see cref="UndoAsync"/> has something to undo.</summary>
    public bool CanUndo
    {
        get
        {
            lock (gate)
            {
                return history.CanUndo;
            }
        }
    }

    /// <summary>Gets whether <see cref="RedoAsync"/> has something to redo.</summary>
    public bool CanRedo
    {
        get
        {
            lock (gate)
            {
                return history.CanRedo;
            }
        }
    }

    /// <summary>
    /// Gets the 0-based pages changed since the last save (also by undo and redo), as a snapshot. The save checks the
    /// number of annotations of each of them in the written file.
    /// </summary>
    public IReadOnlySet<int> ChangedPages
    {
        get
        {
            lock (gate)
            {
                return new HashSet<int>(changedPages);
            }
        }
    }

    /// <summary>
    /// Creates a highlight, underline or strike-out over a selection. The rectangles are joined into lines first
    /// (<see cref="MarkupGeometry.LinesFor"/>); if no usable line is left, nothing is created and nothing changes.
    /// </summary>
    /// <param name="page">The 0-based page index.</param>
    /// <param name="kind">Highlight, underline or strike-out.</param>
    /// <param name="lineRects">The rectangles of the selection, in page points from the top left.</param>
    /// <param name="color">The colour.</param>
    /// <param name="contents">The comment, or <see langword="null"/>.</param>
    /// <param name="author">The author (the setting "Author name"), or <see langword="null"/>.</param>
    /// <returns>The new annotation, or <see langword="null"/> when the selection has no usable line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lineRects"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The session has no ready document, or PDFium could not create the markup.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is not a page, or <paramref name="kind"/> is not a markup kind.</exception>
    public async Task<PdfAnnotation?> ApplyMarkupAsync(
        int page,
        AnnotationKind kind,
        IReadOnlyList<PageRect> lineRects,
        AnnotationColor color,
        string? contents,
        string? author)
    {
        ArgumentNullException.ThrowIfNull(lineRects);
        var lines = MarkupGeometry.LinesFor(lineRects);
        if (lines.Count == 0)
        {
            return null;
        }

        return await RunAsync<PdfAnnotation?>(async document =>
        {
            var created = await document.CreateMarkupAsync(page, kind, lines, color, contents, author).ConfigureAwait(false);
            Record(page, new AnnotationCommand.Create(created));
            return created;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a drawing (freehand, line, arrow, rectangle or ellipse, never filled). A drawing with nothing to draw
    /// (<see cref="DrawingGeometry.IsEmpty"/>) creates nothing and changes nothing.
    /// </summary>
    /// <param name="page">The 0-based page index.</param>
    /// <param name="geometry">The shape in page points from the top left.</param>
    /// <param name="color">The colour.</param>
    /// <param name="contents">The comment, or <see langword="null"/>.</param>
    /// <param name="author">The author (the setting "Author name"), or <see langword="null"/>.</param>
    /// <returns>The new annotation, or <see langword="null"/> when the drawing is empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="geometry"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The session has no ready document, or PDFium could not create the drawing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page is not a page, or the width or the number of strokes or points is beyond what a drawing may have.</exception>
    /// <exception cref="PdfAnnotationLimitException">The document lists the most annotations Pagewing reads; nothing changes.</exception>
    public async Task<PdfAnnotation?> ApplyDrawingAsync(
        int page,
        DrawingGeometry geometry,
        AnnotationColor color,
        string? contents,
        string? author)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty)
        {
            return null;
        }

        return await RunAsync<PdfAnnotation?>(async document =>
        {
            var created = await document.CreateDrawingAsync(page, geometry, color, contents, author).ConfigureAwait(false);
            Record(page, new AnnotationCommand.Create(created));
            return created;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves or resizes a drawing, or changes its stroke width: it is replaced by <paramref name="geometry"/> (of the same
    /// kind) under the same name; colour, comment and author stay. It is one step of the history however it came about; a
    /// move with the mouse calls this once, on release.
    /// </summary>
    /// <param name="locator">Finds the drawing: by name if it has one, else by index.</param>
    /// <param name="geometry">The new shape.</param>
    /// <returns>The replaced drawing; its index may have changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="geometry"/> is <see langword="null"/>.</exception>
    /// <exception cref="PdfAnnotationNotFoundException">No annotation matches the locator.</exception>
    /// <exception cref="InvalidOperationException">
    /// The session has no ready document, or the annotation is no drawing or is not <see cref="PdfAnnotation.GeometryEditable"/>
    /// (the history and the document are unchanged).
    /// </exception>
    public Task<PdfAnnotation> ReplaceGeometryAsync(AnnotationLocator locator, DrawingGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return UpdateAsync(locator, new AnnotationEdit(Geometry: geometry));
    }

    /// <summary>
    /// Deletes a highlight, underline or strike-out (also one of another application) or a drawing, together with its
    /// popup. The copy that undo creates again from is taken first: for a markup its lines are the quadrilaterals as
    /// rectangles (<see cref="MarkupGeometry.LinesFor"/>), or the bounds of the annotation when no quadrilateral has an
    /// area; for a drawing it is the shape as read. A markup with neither and a drawing whose shape cannot be read could
    /// not be restored, so they are not deleted.
    /// </summary>
    /// <param name="locator">Finds the annotation: by name if it has one, else by index.</param>
    /// <exception cref="PdfAnnotationNotFoundException">No annotation matches the locator.</exception>
    /// <exception cref="InvalidOperationException">
    /// The session has no ready document, the annotation is not editable, or it has no area or shape that undo could create
    /// again (the document is unchanged).
    /// </exception>
    public Task DeleteAsync(AnnotationLocator locator) =>
        RunAsync(async document =>
        {
            var removed = await FindAsync(document, locator).ConfigureAwait(false);
            if (!removed.Editable)
            {
                throw new InvalidOperationException("Only highlights, underlines, strike-outs and drawings without replies can be deleted.");
            }

            if (removed.Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut)
            {
                var lines = RestorableLines(removed);
                if (lines.Count == 0)
                {
                    throw new InvalidOperationException("The markup has no area that undo could restore, so it is not deleted.");
                }

                removed = removed with { QuadRects = lines };
            }
            else if (!CanRecreateDrawing(removed))
            {
                throw new InvalidOperationException("The drawing has no shape that undo could restore, so it is not deleted.");
            }

            // The copy gets a name if it has none: undo creates it again under that name, so redo finds it again.
            removed = removed with { Name = removed.Name ?? Guid.NewGuid().ToString("N") };
            await document.DeleteAnnotationAsync(locator).ConfigureAwait(false);
            Record(locator.Page, new AnnotationCommand.Delete(removed));
            return true;
        });

    /// <summary>
    /// Changes the colour or the comment of a highlight, underline or strike-out, or the colour, the comment or the shape
    /// of a drawing. A new colour or shape of a drawing replaces it (one step, also colour and width together) and needs
    /// <see cref="PdfAnnotation.GeometryEditable"/>; a comment alone needs only <see cref="PdfAnnotation.Editable"/>.
    /// </summary>
    /// <param name="locator">Finds the annotation: by name if it has one, else by index.</param>
    /// <param name="edit">The change.</param>
    /// <returns>The changed annotation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="edit"/> is <see langword="null"/>.</exception>
    /// <exception cref="PdfAnnotationNotFoundException">No annotation matches the locator.</exception>
    /// <exception cref="InvalidOperationException">
    /// The session has no ready document, the annotation is not editable, or it is a drawing that is not
    /// <see cref="PdfAnnotation.GeometryEditable"/> and gets a new colour or shape (the history is unchanged).
    /// </exception>
    public Task<PdfAnnotation> UpdateAsync(AnnotationLocator locator, AnnotationEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return RunAsync(async document =>
        {
            var before = await FindAsync(document, locator).ConfigureAwait(false);
            var after = await document.UpdateAnnotationAsync(locator, edit).ConfigureAwait(false);
            Record(locator.Page, new AnnotationCommand.Edit(before, after));
            return after;
        });
    }

    /// <summary>Undoes the newest change; does nothing when there is none.</summary>
    /// <exception cref="InvalidOperationException">The session has no ready document, or PDFium refused; the history is as before.</exception>
    public Task UndoAsync() => StepAsync(undo: true);

    /// <summary>Applies the newest undone change again; does nothing when there is none.</summary>
    /// <exception cref="InvalidOperationException">The session has no ready document, or PDFium refused; the history is as before.</exception>
    public Task RedoAsync() => StepAsync(undo: false);

    /// <summary>
    /// Saves the document incrementally and atomically (<see cref="DocumentSaver"/>) to <paramref name="targetPath"/> and
    /// lets the session adopt the written file (<see cref="ViewerSession.AdoptSavedDocumentAsync"/>); then the editor is
    /// clean, its history and <see cref="ChangedPages"/> are empty, and <see cref="ViewerSession.OpenedStamp"/> is the stamp
    /// of the written file. On any failure or cancellation the document stays as it was, still unsaved, with its history.
    /// </summary>
    /// <remarks>
    /// The file is saved once the call returns, also when the session did not adopt it because it was closed or opened
    /// another file meanwhile (<see cref="AnnotationSaveResult.Adopted"/> is <see langword="false"/>): then the session has
    /// already closed <see cref="SaveResult.Reopened"/>, and the editor is clean all the same (the changes are on disk).
    /// </remarks>
    /// <param name="engine">Opens the written file for the check.</param>
    /// <param name="targetPath">The path of the open document ("Save") or another one ("Save As"); an existing file is replaced.</param>
    /// <param name="overwriteChangedOriginal">Whether the user confirmed overwriting another file that now stands at the path of the original.</param>
    /// <param name="progress">Receives 0 to 1 while the file is written (from the PDFium thread).</param>
    /// <param name="cancellationToken">Cancels the save until the target is replaced.</param>
    /// <returns>
    /// The result of the save and whether the session adopted it. An adopted document belongs to the session (do not
    /// dispose it); one that was not adopted is already closed.
    /// </returns>
    /// <exception cref="SaveFailureException">The save failed; the target is unchanged.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the target is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The session is not <see cref="ViewerState.Ready"/>; nothing is written.</exception>
    public Task<AnnotationSaveResult> SaveAsync(
        PdfEngine engine,
        string targetPath,
        bool overwriteChangedOriginal,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        return RunAsync(
            async snapshot =>
            {
                // RunAsync refuses without a ready session, so the snapshot is there; document, path, stamp and password
                // are read together and belong to the same document.
                var (document, originalPath, openedStamp, password) = snapshot!;
                int[] pages;
                lock (gate)
                {
                    pages = [.. changedPages];
                }

                var counts = new Dictionary<int, int>(pages.Length);
                foreach (var page in pages)
                {
                    counts[page] = (await document.GetAnnotationsAsync(page, cancellationToken).ConfigureAwait(false)).Count;
                }

                var request = new SaveRequest(
                    originalPath,
                    targetPath,
                    openedStamp,
                    password,
                    document.PageCount,
                    counts,
                    overwriteChangedOriginal);

                // A failure leaves everything as it is; the saver closes the document it may have reopened.
                var result = await DocumentSaver.SaveAsync(document, engine, request, progress, cancellationToken).ConfigureAwait(false);

                // The file is saved from here on, whatever happens to the session: the changes are on disk. A session that
                // does not adopt the document closes it.
                var adopted = await session.AdoptSavedDocumentAsync(result.Reopened, targetPath, replacing: document).ConfigureAwait(false);
                lock (gate)
                {
                    history.Clear();
                    versionAtLastSave = history.Version;
                    changedPages.Clear();
                    bound = adopted ? result.Reopened : null;
                }

                return new AnnotationSaveResult(result, adopted);
            },
            requireDocument: true);
    }

    /// <summary>
    /// Waits until every operation called so far has ended (also those that failed); operations called later are not
    /// waited for. Never faults.
    /// </summary>
    /// <returns>A task that completes when the operations called before this call have ended.</returns>
    public Task WhenIdleAsync()
    {
        lock (gate)
        {
            return tail;
        }
    }

    private static AnnotationLocator LocatorOf(PdfAnnotation annotation) => new(annotation.Page, annotation.Index, annotation.Name);

    /// <summary>
    /// The change that turns the colour, the comment and (of a drawing) the shape of <paramref name="from"/> into those of
    /// <paramref name="to"/>.
    /// </summary>
    private static AnnotationEdit EditTowards(PdfAnnotation from, PdfAnnotation to) => new(
        Contents: from.Contents == to.Contents ? null : to.Contents ?? string.Empty,

        // A missing colour cannot be restored as missing; the annotation keeps the one it has.
        Color: from.Color == to.Color ? null : to.Color,
        Geometry: from.Drawing is { } was && to.Drawing is { } now && !SameShape(was, now) ? now : null);

    private static bool SameShape(DrawingGeometry a, DrawingGeometry b)
    {
        if (a.Kind != b.Kind || a.Width != b.Width || a.Box != b.Box || a.Strokes.Count != b.Strokes.Count)
        {
            return false;
        }

        for (var s = 0; s < a.Strokes.Count; s++)
        {
            if (a.Strokes[s].Count != b.Strokes[s].Count)
            {
                return false;
            }

            for (var i = 0; i < a.Strokes[s].Count; i++)
            {
                if (a.Strokes[s][i] != b.Strokes[s][i])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a drawing can be created again from its read shape (undo of a delete).</summary>
    private static bool CanRecreateDrawing(PdfAnnotation annotation) =>
        annotation.Drawing is { IsEmpty: false, Width: > 0 and <= PdfiumDrawings.MaxWidth };

    private static async Task<PdfAnnotation> FindAsync(PdfDocument document, AnnotationLocator locator)
    {
        var annotations = await document.GetAnnotationsAsync(locator.Page).ConfigureAwait(false);
        foreach (var annotation in annotations)
        {
            if (locator.Name is not null ? annotation.Name == locator.Name : annotation.Index == locator.Index)
            {
                return annotation;
            }
        }

        throw new PdfAnnotationNotFoundException("The annotation was not found on its page.");
    }

    /// <summary>
    /// The lines a markup can be created again with: its quadrilaterals as rectangles, or its bounds when none of them has
    /// an area; empty when neither has one.
    /// </summary>
    private static IReadOnlyList<PageRect> RestorableLines(PdfAnnotation annotation)
    {
        var lines = MarkupGeometry.LinesFor(annotation.QuadRects);
        return lines.Count > 0 ? lines : MarkupGeometry.LinesFor([annotation.Bounds]);
    }

    /// <summary>
    /// Creates a markup or a drawing again from its copy, under its old name. A drawing without a colour comes back black,
    /// which is how PDFium draws it.
    /// </summary>
    private static Task<PdfAnnotation> RecreateAsync(PdfDocument document, PdfAnnotation copy) =>
        copy.Drawing is { } shape
            ? document.CreateDrawingAsync(
                copy.Page, shape, copy.Color ?? new AnnotationColor(0, 0, 0), copy.Contents, copy.Author, copy.Name, CancellationToken.None)
            : document.CreateMarkupAsync(
                copy.Page,
                copy.Kind,
                RestorableLines(copy),
                copy.Color ?? AnnotationPalette.ColorOf(MarkupColor.Yellow),
                copy.Contents,
                copy.Author,
                copy.Name,
                CancellationToken.None);

    /// <summary>Reverses (<paramref name="undo"/>) or repeats a step of the history.</summary>
    private static Task ApplyAsync(PdfDocument document, AnnotationCommand command, bool undo) => command switch
    {
        AnnotationCommand.Create create when undo => document.DeleteAnnotationAsync(LocatorOf(create.Created)),
        AnnotationCommand.Create create => RecreateAsync(document, create.Created),
        AnnotationCommand.Delete delete when undo => RecreateAsync(document, delete.Removed),
        AnnotationCommand.Delete delete => document.DeleteAnnotationAsync(LocatorOf(delete.Removed)),
        AnnotationCommand.Edit edit when undo => document.UpdateAnnotationAsync(LocatorOf(edit.After), EditTowards(edit.After, edit.Before)),
        AnnotationCommand.Edit edit => document.UpdateAnnotationAsync(LocatorOf(edit.After), EditTowards(edit.Before, edit.After)),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown command."),
    };

    private static int PageOf(AnnotationCommand command) => command switch
    {
        AnnotationCommand.Create create => create.Created.Page,
        AnnotationCommand.Delete delete => delete.Removed.Page,
        AnnotationCommand.Edit edit => edit.After.Page,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown command."),
    };

    private Task<bool> StepAsync(bool undo) =>
        RunAsync(
            async snapshot =>
            {
                // Decided only now, after the operations before it: an undo right after a change undoes that change.
                AnnotationCommand? command;
                lock (gate)
                {
                    if (undo ? !history.CanUndo : !history.CanRedo)
                    {
                        // Nothing to do needs no document.
                        return false;
                    }

                    if (snapshot is null)
                    {
                        throw new InvalidOperationException("The session has no open document.");
                    }

                    command = (undo ? history.TakeUndo() : history.TakeRedo())!;
                }

                try
                {
                    await ApplyAsync(snapshot.Document, command, undo).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        if (ex is OperationCanceledException or ObjectDisposedException)
                        {
                            // Nothing was wrong with the step: put it back where it was (the job changed all or nothing).
                            _ = undo ? history.TakeRedo() : history.TakeUndo();
                        }
                        else
                        {
                            // It would fail the same way every time and block every older step: forget it.
                            history.Discard(command);
                        }
                    }

                    throw;
                }

                var page = PageOf(command);
                lock (gate)
                {
                    changedPages.Add(page);
                }

                session.InvalidatePages(new HashSet<int> { page });
                return true;
            },
            requireDocument: false);

    /// <summary>Records a change that was made: the page, the step of the history and what the session shows.</summary>
    private void Record(int page, AnnotationCommand command)
    {
        lock (gate)
        {
            history.Push(command);
            changedPages.Add(page);
        }

        session.InvalidatePages(new HashSet<int> { page });
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the ready document of the session after every earlier operation, and raises
    /// <see cref="Changed"/> once afterwards (after a failure too, since the history may have moved).
    /// </summary>
    private Task<T> RunAsync<T>(Func<PdfDocument, Task<T>> work) => RunAsync<T>(snapshot => work(snapshot!.Document), requireDocument: true);

    private Task<T> RunAsync<T>(Func<ViewerSession.ReadySnapshot?, Task<T>> work, bool requireDocument)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (gate)
        {
            previous = tail;
            tail = done.Task;
            pending++;
        }

        return RunAfterAsync(previous, done, work, requireDocument);
    }

    private async Task<T> RunAfterAsync<T>(Task previous, TaskCompletionSource done, Func<ViewerSession.ReadySnapshot?, Task<T>> work, bool requireDocument)
    {
        // Never faults: every operation completes its own task in the finally below.
        await previous.ConfigureAwait(false);
        int versionBefore;
        lock (gate)
        {
            versionBefore = history.Version;
        }

        try
        {
            // One consistent read of the session: the ready document with its path, stamp and password, or nothing.
            var snapshot = session.SnapshotForSave();
            var document = snapshot?.Document;
            lock (gate)
            {
                // The history belongs to the document it was made on; another document starts clean.
                if (document is not null && bound is not null && !ReferenceEquals(document, bound))
                {
                    history.Clear();
                    versionAtLastSave = history.Version;
                    changedPages.Clear();
                }

                bound = document ?? bound;
            }

            if (document is null && requireDocument)
            {
                throw new InvalidOperationException("The session has no open document.");
            }

            return await work(snapshot).ConfigureAwait(false);
        }
        finally
        {
            // Every change of the state moves the version: a step, an undo or redo (also one that was put back), a save.
            bool changed;
            lock (gate)
            {
                changed = history.Version != versionBefore;
                pending--;
            }

            done.SetResult();
            if (changed)
            {
                RaiseChanged();
            }
        }
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception ex)
        {
            // A failing handler must not break the editor; it surfaces like any unhandled exception on a pool thread.
            var captured = ExceptionDispatchInfo.Capture(ex);
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), captured);
        }
    }
}

/// <summary>A save of <see cref="AnnotationEditor.SaveAsync"/>.</summary>
/// <param name="Saved">
/// The result of <see cref="DocumentSaver"/>. Its document belongs to the session when <paramref name="Adopted"/> is
/// <see langword="true"/>, and is already closed otherwise.
/// </param>
/// <param name="Adopted">
/// Whether the session adopted the saved document; <see langword="false"/> when it was closed or opened another file
/// while the save ran (the file is saved all the same).
/// </param>
public sealed record AnnotationSaveResult(SaveResult Saved, bool Adopted);
