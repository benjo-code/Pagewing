using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

public sealed class AnnotationEditorTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task ApplyMarkupMakesTheDocumentDirtyAndInvalidatesItsPage()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var cache = session.Cache!;
        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        cache.Add(new TileKey(1, 1000, 0, 0), SmallImage());
        var editor = new AnnotationEditor(session);
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);

        var rects = await WordRectsAsync(session.Document!, 0, "Hello");
        var created = await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, "A note", "Ada");

        Assert.NotNull(created);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.Equal([0], editor.ChangedPages);
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.False(cache.Contains(new TileKey(0, 1000, 0, 0)));
        Assert.True(cache.Contains(new TileKey(1, 1000, 0, 0)));
        var listed = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, listed.Name);
        Assert.Equal("Ada", listed.Author);
        Assert.Equal("A note", listed.Contents);
    }

    [Fact]
    public async Task VersionChangesWithEveryStepAndNeverComesBack()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var seen = new List<int> { editor.Version };

        var rects = await WordRectsAsync(session.Document!, 0, "Hello");
        Assert.NotNull(await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null));
        seen.Add(editor.Version);
        await editor.UndoAsync();
        seen.Add(editor.Version);
        await editor.RedoAsync();
        seen.Add(editor.Version);

        // Every step has a version of its own, also the undo back to the opened state: nothing equals an earlier look.
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task EmptyGeometryCreatesNothing()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var cache = session.Cache!;
        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        var editor = new AnnotationEditor(session);
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);

        Assert.Null(await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [], Yellow, null, null));
        Assert.Null(await editor.ApplyMarkupAsync(0, AnnotationKind.Underline, [new PageRect(10, 10, 10, 30), new PageRect(5, double.NaN, 9, 9)], Yellow, null, null));

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.Empty(editor.ChangedPages);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.True(cache.Contains(new TileKey(0, 1000, 0, 0)));
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [Fact]
    public async Task UndoRemovesTheMarkupAndRedoRestoresIt()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var rects = await WordRectsAsync(session.Document!, 0, "Hello");
        var created = (await editor.ApplyMarkupAsync(0, AnnotationKind.Underline, rects, Blue, "note", "Ada"))!;
        var cache = session.Cache!;

        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        await editor.UndoAsync();

        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
        Assert.False(cache.Contains(new TileKey(0, 1000, 0, 0)));

        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        await editor.RedoAsync();

        // The markup comes back as it was, under its old name, so the steps of the history still find it.
        var restored = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, restored.Name);
        Assert.Equal(AnnotationKind.Underline, restored.Kind);
        Assert.Equal(Blue, restored.Color);
        Assert.Equal("note", restored.Contents);
        Assert.Equal("Ada", restored.Author);
        AssertRectNear(created.QuadRects[0], Assert.Single(restored.QuadRects), 0.1);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.True(editor.IsDirty);
        Assert.False(cache.Contains(new TileKey(0, 1000, 0, 0)));

        // Undo again finds the re-created markup by that name.
        await editor.UndoAsync();
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [Fact]
    public async Task DeleteThenUndoRestoresColorAndComment()
    {
        using var session = await OpenSessionAsync("annotations-foreign.pdf");
        var editor = new AnnotationEditor(session);
        var bare = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Index == 3);
        Assert.Null(bare.Name);

        await editor.DeleteAsync(new AnnotationLocator(0, bare.Index, bare.Name));

        var afterDelete = await session.Document!.GetAnnotationsAsync(0, Ct);
        Assert.DoesNotContain(afterDelete, a => a.Contents == "No name, no appearance");
        Assert.True(editor.IsDirty);
        Assert.Equal([0], editor.ChangedPages);

        await editor.UndoAsync();

        var restored = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Contents == "No name, no appearance");
        Assert.Equal(AnnotationKind.Highlight, restored.Kind);
        Assert.Equal(new AnnotationColor(153, 255, 153), restored.Color);
        Assert.True(restored.Editable);
        AssertRectNear(Assert.Single(bare.QuadRects), Assert.Single(restored.QuadRects), 0.1);

        // A redo deletes it again (the restored copy has a name now), and another undo brings it back once more.
        await editor.RedoAsync();
        Assert.DoesNotContain(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Contents == "No name, no appearance");
        await editor.UndoAsync();
        Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Contents == "No name, no appearance");
    }

    [Fact]
    public async Task MarkupWithoutAnAreaIsNotDeletedAndOneWithBoundsOnlyComesBackFromThem()
    {
        // A foreign highlight whose quadrilateral and rectangle have no area, and one whose quadrilateral is flat but whose
        // rectangle is fine (both damaged, both still listed as editable).
        var path = WritePdf(folder, "flat-markups.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [50 50 50 50] /QuadPoints [50 50 50 50 50 50 50 50] /C [1 1 0] /NM (flat) /Contents (flat) >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [20 100 120 120] /QuadPoints [20 120 20 120 20 120 20 120] /C [0 1 0] /NM (rect-only) /Contents (rect only) >>",
        ]);
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        var listed = await session.Document!.GetAnnotationsAsync(0, Ct);
        var flat = listed.Single(a => a.Name == "flat");
        var rectOnly = listed.Single(a => a.Name == "rect-only");
        Assert.True(flat.Editable);
        Assert.True(rectOnly.Editable);

        // Undo could not create the flat one again: the delete is refused before the document is touched.
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.DeleteAsync(new AnnotationLocator(0, flat.Index, flat.Name)));
        Assert.Contains(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == "flat");
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);

        // The other one comes back from its bounds.
        await editor.DeleteAsync(new AnnotationLocator(0, rectOnly.Index, rectOnly.Name));
        Assert.DoesNotContain(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == "rect-only");
        await editor.UndoAsync();
        var restored = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == "rect-only");
        Assert.Equal("rect only", restored.Contents);
        Assert.Equal(new AnnotationColor(0, 255, 0), restored.Color);
        AssertRectNear(rectOnly.Bounds, Assert.Single(restored.QuadRects), 0.5);
    }

    [Fact]
    public async Task AStepThatFailsIsDroppedAndTheOlderStepsStayUsable()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var hello = await WordRectsAsync(session.Document!, 0, "Hello");
        var pagewing = await WordRectsAsync(session.Document!, 0, "Pagewing");
        var first = (await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, hello, Yellow, null, null))!;
        var second = (await editor.ApplyMarkupAsync(0, AnnotationKind.Underline, pagewing, Blue, null, null))!;

        // The second markup disappears behind the editor's back: undoing its creation fails, every time it would be tried.
        await session.Document!.DeleteAnnotationAsync(new AnnotationLocator(0, second.Index, second.Name), Ct);
        await Assert.ThrowsAsync<PdfAnnotationNotFoundException>(() => editor.UndoAsync());

        // The failed step is gone (not on either stack), and the older one undoes and redoes as before.
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
        await editor.UndoAsync();
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
        await editor.RedoAsync();
        Assert.Equal(first.Name, Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct)).Name);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public async Task UndoOfAnEditRestoresColorAndComment()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var rects = await WordRectsAsync(session.Document!, 0, "Hello");
        var created = (await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, "first", null))!;
        var locator = new AnnotationLocator(0, created.Index, created.Name);

        var changed = await editor.UpdateAsync(locator, new AnnotationEdit("second", Blue));
        Assert.Equal(Blue, changed.Color);
        Assert.Equal("second", changed.Contents);

        await editor.UndoAsync();
        var undone = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(Yellow, undone.Color);
        Assert.Equal("first", undone.Contents);

        await editor.RedoAsync();
        var redone = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(Blue, redone.Color);
        Assert.Equal("second", redone.Contents);
    }

    [Fact]
    public async Task SaveClearsDirtyAndTheHistory()
    {
        var path = Copy("text-basic.pdf");
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        var old = session.Document!;
        var cache = session.Cache!;
        var rects = await WordRectsAsync(old, 0, "Hello");
        var created = (await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, "saved", "Ada"))!;
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);

        var result = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.Empty(editor.ChangedPages);
        Assert.True(Volatile.Read(ref changes) >= 1);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.True(result.Adopted);
        Assert.Same(result.Saved.Reopened, session.Document);
        Assert.Same(cache, session.Cache);
        Assert.Throws<ObjectDisposedException>(() => old.PageCount);
        Assert.Equal(created.Name, Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct)).Name);

        // The file holds the markup.
        await using var check = await OpenAsync(path);
        Assert.Equal(created.Name, Assert.Single(await check.GetAnnotationsAsync(0, Ct)).Name);
    }

    [Fact]
    public async Task SaveRereadsThePathStampSoASecondSaveSucceeds()
    {
        var path = Copy("text-basic.pdf");
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        var before = session.OpenedStamp;
        Assert.Equal(FileStamp.Of(path), before);
        var hello = await WordRectsAsync(session.Document!, 0, "Hello");
        await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, hello, Yellow, null, null);

        await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        Assert.Equal(FileStamp.Of(path), session.OpenedStamp);
        Assert.NotEqual(before, session.OpenedStamp);

        // With a stale stamp this second save to the same path would report OriginalChanged.
        var pagewing = await WordRectsAsync(session.Document!, 0, "Pagewing");
        await editor.ApplyMarkupAsync(0, AnnotationKind.StrikeOut, pagewing, Blue, null, null);
        await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        Assert.False(editor.IsDirty);
        await using var check = await OpenAsync(path);
        Assert.Equal(2, (await check.GetAnnotationsAsync(0, Ct)).Count);
    }

    [Fact]
    public async Task SaveAsMovesTheSessionToTheNewPath()
    {
        var path = Copy("text-basic.pdf");
        var target = Path.Combine(folder.Path, "copy.pdf");
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        var hello = await WordRectsAsync(session.Document!, 0, "Hello");
        await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, hello, Yellow, null, null);

        await editor.SaveAsync(engine, target, overwriteChangedOriginal: false, progress: null, Ct);

        Assert.Equal(target, session.Path);
        Assert.Equal(FileStamp.Of(target), session.OpenedStamp);
        await using var original = await OpenAsync(path);
        Assert.Empty(await original.GetAnnotationsAsync(0, Ct));
    }

    [Fact]
    public async Task SaveFailureKeepsDirtyAndTheHistory()
    {
        var path = Copy("text-basic.pdf");
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        var old = session.Document!;
        var rects = await WordRectsAsync(old, 0, "Hello");
        await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null);
        var before = await File.ReadAllBytesAsync(path, Ct);

        // A folder that does not exist: NotWritable.
        var missing = Path.Combine(folder.Path, "missing", "x.pdf");
        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => editor.SaveAsync(engine, missing, overwriteChangedOriginal: false, progress: null, Ct));
        Assert.Equal(SaveFailureReason.NotWritable, failure.Reason);
        AssertStillDirty();

        // Another file at the path of the original (the opened file itself is untouched): OriginalChanged.
        File.Move(path, path + ".moved");
        File.Copy(TestData.Pdf("text-basic.pdf"), path);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));
        before = await File.ReadAllBytesAsync(path, Ct);
        var changed = await Assert.ThrowsAsync<SaveFailureException>(
            () => editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct));
        Assert.Equal(SaveFailureReason.OriginalChanged, changed.Reason);
        AssertStillDirty();

        // A cancellation by the user.
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => editor.SaveAsync(engine, path, overwriteChangedOriginal: true, progress: null, canceled.Token));
        AssertStillDirty();
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));

        // The history still works on the old document.
        await editor.UndoAsync();
        Assert.Empty(await old.GetAnnotationsAsync(0, Ct));

        void AssertStillDirty()
        {
            Assert.True(editor.IsDirty);
            Assert.True(editor.CanUndo);
            Assert.Equal([0], editor.ChangedPages);
            Assert.Same(old, session.Document);
            Assert.Equal(ViewerState.Ready, session.State);
            Assert.True(old.PageCount > 0);
        }
    }

    [Fact]
    public async Task TwoRapidSelectionsOfTheSameWordCreateTwoAndUndoRemovesExactlyOne()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var rects = await WordRectsAsync(session.Document!, 0, "Hello");

        // Both start before either is done.
        var first = editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null);
        var second = editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null);
        var created = await Task.WhenAll(first, second);

        Assert.NotEqual(created[0]!.Name, created[1]!.Name);
        Assert.Equal(2, (await session.Document!.GetAnnotationsAsync(0, Ct)).Count);

        await editor.UndoAsync();

        var left = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created[0]!.Name, left.Name);
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public async Task OperationsRunOneAfterTheOther()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var rects = await WordRectsAsync(session.Document!, 0, "Hello");

        // The undo is asked for while the markup is still being created: it waits for it and then removes it.
        var apply = editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null);
        var undo = editor.UndoAsync();
        await Task.WhenAll(apply, undo);

        Assert.NotNull(await apply);
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public async Task TheEditorNeitherBlocksTheCallerNorCallsPdfiumOnItsThread()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var rects = await WordRectsAsync(session.Document!, 0, "Hello");
        using var started = new ManualResetEventSlim();
        using var hold = new ManualResetEventSlim();
        var held = PdfiumThread.Shared.InvokeAsync(
            () =>
            {
                started.Set();
                hold.Wait(TimeSpan.FromSeconds(10));
            },
            PdfiumPriority.Interactive,
            Ct);
        Task<PdfAnnotation?> apply;
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10), Ct));

            // The PDFium thread is busy: the call returns at once with a running task (a call to PDFium on this thread
            // would throw instead).
            apply = editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null);
            Assert.False(apply.IsCompleted);
            Assert.False(editor.IsDirty);
        }
        finally
        {
            hold.Set();
        }

        await held;
        Assert.NotNull(await apply);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task IsBusyFromTheCallUntilTheStepIsRecorded()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var rects = await WordRectsAsync(session.Document!, 0, "Hello");
        Assert.False(editor.IsBusy);
        Assert.True(editor.WhenIdleAsync().IsCompleted);
        using var started = new ManualResetEventSlim();
        using var hold = new ManualResetEventSlim();
        var held = PdfiumThread.Shared.InvokeAsync(
            () =>
            {
                started.Set();
                hold.Wait(TimeSpan.FromSeconds(10));
            },
            PdfiumPriority.Interactive,
            Ct);
        Task<PdfAnnotation?> apply;
        Task undo;
        Task idle;
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10), Ct));

            // Accepted, not recorded yet: not dirty, but busy (closing must count it), and the wait covers both calls.
            apply = editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null);
            undo = editor.UndoAsync();
            idle = editor.WhenIdleAsync();
            Assert.False(editor.IsDirty);
            Assert.True(editor.IsBusy);
            Assert.False(idle.IsCompleted);
        }
        finally
        {
            hold.Set();
        }

        await held;
        await idle;
        Assert.True(apply.IsCompleted);
        Assert.True(undo.IsCompleted);
        Assert.False(editor.IsBusy);
        Assert.True(editor.CanRedo);

        // A failing operation ends the busy state as well; empty geometry is refused before the editor takes it.
        await Assert.ThrowsAsync<PdfAnnotationNotFoundException>(() => editor.DeleteAsync(new AnnotationLocator(0, 7, "missing")));
        Assert.False(editor.IsBusy);
        Assert.Null(await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [], Yellow, null, null));
        Assert.False(editor.IsBusy);
    }

    [Fact]
    public async Task WithoutADocumentTheEditorRefuses()
    {
        using var session = new ViewerSession(engine);
        var editor = new AnnotationEditor(session);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(10, 10, 50, 20)], Yellow, null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => editor.SaveAsync(engine, Path.Combine(folder.Path, "x.pdf"), overwriteChangedOriginal: false, progress: null, Ct));
        await editor.UndoAsync();
        Assert.False(editor.IsDirty);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task EmptyDrawingChangesNothing()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var cache = session.Cache!;
        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        var editor = new AnnotationEditor(session);
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);
        var version = editor.Version;

        Assert.Null(await editor.ApplyDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(10, 10, 10, 50), 2), Red, null, null));
        Assert.Null(await editor.ApplyDrawingAsync(0, DrawingGeometry.Ink([[new(10, 10)]], 2), Red, null, null));

        Assert.Equal(version, editor.Version);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.Empty(editor.ChangedPages);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.True(cache.Contains(new TileKey(0, 1000, 0, 0)));
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [Fact]
    public async Task UndoOfCreateDeletesTheDrawing()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var geometry = DrawingGeometry.Ink([[new(20, 30), new(40, 60), new(90, 35)]], 2);
        var created = (await editor.ApplyDrawingAsync(0, geometry, Red, "note", "Ada"))!;
        Assert.True(editor.IsDirty);
        Assert.Equal([0], editor.ChangedPages);
        var cache = session.Cache!;
        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());

        await editor.UndoAsync();

        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
        Assert.False(cache.Contains(new TileKey(0, 1000, 0, 0)));

        // Redo creates it again under its old name, with everything it had.
        await editor.RedoAsync();
        var restored = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, restored.Name);
        Assert.Equal(AnnotationKind.Ink, restored.Kind);
        Assert.Equal(Red, restored.Color);
        Assert.Equal("note", restored.Contents);
        Assert.Equal("Ada", restored.Author);
        AssertSameShape(created.Drawing!, restored.Drawing!, 1e-4);

        // The re-created drawing is found by its name again.
        await editor.UndoAsync();
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [Fact]
    public async Task UndoOfMoveRestoresTheOldGeometry()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(50, 50, 150, 100), 3), Red, "kept", "Ada"))!;
        var versionBefore = editor.Version;

        var moved = await editor.ReplaceGeometryAsync(
            new AnnotationLocator(0, created.Index, created.Name),
            DrawingTransform.Move(created.Drawing!, 40, 25, new PageRect(0, 0, 500, 500)));

        Assert.Equal(versionBefore + 1, editor.Version);
        Assert.Equal(created.Name, moved.Name);
        AssertSameShape(DrawingGeometry.Rectangle(new PageRect(90, 75, 190, 125), 3), moved.Drawing!, 1e-3);

        await editor.UndoAsync();

        // Only the move is undone: the drawing is still there, at its old place, the creation is still on the stack.
        var undone = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, undone.Name);
        Assert.Equal("kept", undone.Contents);
        Assert.Equal("Ada", undone.Author);
        Assert.Equal(Red, undone.Color);
        AssertSameShape(created.Drawing!, undone.Drawing!, 1e-3);
        Assert.True(editor.CanUndo);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public async Task RedoOfMoveAppliesItAgain()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyDrawingAsync(0, DrawingGeometry.Ellipse(new PageRect(60, 60, 160, 120), 2), Blue, null, null))!;
        var moved = await editor.ReplaceGeometryAsync(
            new AnnotationLocator(0, created.Index, created.Name),
            DrawingTransform.Move(created.Drawing!, -10, 30, new PageRect(0, 0, 500, 500)));
        await editor.UndoAsync();

        await editor.RedoAsync();

        var redone = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, redone.Name);
        Assert.Equal(Blue, redone.Color);
        AssertSameShape(moved.Drawing!, redone.Drawing!, 1e-3);
        Assert.False(editor.CanRedo);
        Assert.True(editor.IsDirty);

        // And the move can be undone once more.
        await editor.UndoAsync();
        AssertSameShape(created.Drawing!, Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct)).Drawing!, 1e-3);
    }

    [Fact]
    public async Task UndoOfDeleteRecreatesUnderTheSameName()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyDrawingAsync(
            0, DrawingGeometry.Arrow(new DisplayPoint(30.3, 150.7), new DisplayPoint(250.9, 40.1), 4), Red, "pointing", "Ada"))!;

        await editor.DeleteAsync(new AnnotationLocator(0, created.Index, created.Name));
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));

        await editor.UndoAsync();

        var restored = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, restored.Name);
        Assert.Equal(AnnotationKind.Arrow, restored.Kind);
        Assert.Equal(Red, restored.Color);
        Assert.Equal("pointing", restored.Contents);
        Assert.Equal("Ada", restored.Author);
        Assert.True(restored.GeometryEditable);
        AssertSameShape(created.Drawing!, restored.Drawing!, 1e-4);

        // Redo deletes it again (by name), and another undo brings it back once more.
        await editor.RedoAsync();
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
        await editor.UndoAsync();
        Assert.Equal(created.Name, Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct)).Name);
    }

    [Fact]
    public async Task AForeignDrawingCanBeDeletedAndCommentedAndComesBackFromItsCopy()
    {
        using var session = await OpenSessionAsync(DrawingsForeign);
        var editor = new AnnotationEditor(session);
        var ink = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == "ink-0001");
        Assert.True(ink.Editable);
        Assert.False(ink.GeometryEditable);
        var locator = new AnnotationLocator(0, ink.Index, ink.Name);

        await editor.DeleteAsync(locator);
        Assert.DoesNotContain(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == "ink-0001");
        await editor.UndoAsync();

        var restored = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == "ink-0001");
        Assert.Equal(AnnotationKind.Ink, restored.Kind);
        Assert.Equal(ink.Color, restored.Color);
        Assert.Equal(ink.Contents, restored.Contents);
        AssertSameShape(ink.Drawing!, restored.Drawing!, 1e-3);

        // A comment alone needs no geometry rights.
        var commented = await editor.UpdateAsync(new AnnotationLocator(0, restored.Index, restored.Name), new AnnotationEdit("Seen"));
        Assert.Equal("Seen", commented.Contents);
        await editor.UndoAsync();
        Assert.Equal(ink.Contents, (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == "ink-0001").Contents);
    }

    [Fact]
    public async Task UndoOfDeletingALockedForeignDrawingRecreatesItFromTheReadShape()
    {
        using var session = await OpenSessionAsync(DrawingsForeign);
        var editor = new AnnotationEditor(session);

        // ink-0001 is locked by its own appearance, square-0003 by a dashed /BS border.
        foreach (var name in new[] { "ink-0001", "square-0003" })
        {
            var locked = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == name);
            Assert.True(locked.Editable);
            Assert.False(locked.GeometryEditable);
            var locator = new AnnotationLocator(0, locked.Index, locked.Name);

            await editor.DeleteAsync(locator);
            Assert.DoesNotContain(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == name);
            await editor.UndoAsync();

            // What Pagewing read comes back, under the same name, drawn by Pagewing from now on.
            var restored = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == name);
            Assert.Equal(locked.Kind, restored.Kind);
            Assert.Equal(locked.Color, restored.Color);
            Assert.Equal(locked.Contents, restored.Contents);
            Assert.Equal(locked.Author, restored.Author);
            Assert.Equal(locked.Drawing!.Width, restored.Drawing!.Width, 6);
            AssertSameShape(locked.Drawing!, restored.Drawing!, 1e-3);
            Assert.True(restored.GeometryEditable);
            Assert.True(restored.Editable);
        }
    }

    [Fact]
    public async Task ColorOnlyChangeOfADrawingUndoesAndRedoes()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyDrawingAsync(0, DrawingGeometry.Ellipse(new PageRect(60, 60, 160, 120), 2), Red, "e", null))!;
        var locator = new AnnotationLocator(0, created.Index, created.Name);
        var version = editor.Version;

        var changed = await editor.UpdateAsync(locator, new AnnotationEdit(Color: Blue));

        Assert.Equal(version + 1, editor.Version);
        Assert.Equal(Blue, changed.Color);
        await editor.UndoAsync();
        var undone = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(Red, undone.Color);
        AssertSameShape(created.Drawing!, undone.Drawing!, 1e-3);
        Assert.Equal("e", undone.Contents);
        await editor.RedoAsync();
        Assert.Equal(Blue, Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct)).Color);
    }

    [Fact]
    public async Task ColorAndWidthChangeAreOneStep()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyDrawingAsync(0, DrawingGeometry.Ink([[new(20, 30), new(60, 90), new(120, 40)]], 2), Red, "c", null))!;
        var locator = new AnnotationLocator(0, created.Index, created.Name);
        var versionBefore = editor.Version;

        var changed = await editor.UpdateAsync(locator, new AnnotationEdit(Color: Blue, Geometry: DrawingGeometry.Ink(created.Drawing!.Strokes, 6)));

        Assert.Equal(versionBefore + 1, editor.Version);
        Assert.Equal(Blue, changed.Color);
        Assert.Equal(6, changed.Drawing!.Width, 6);
        Assert.Equal("c", changed.Contents);

        // One undo takes back both.
        await editor.UndoAsync();
        var undone = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(Red, undone.Color);
        Assert.Equal(2, undone.Drawing!.Width, 6);
        Assert.True(editor.CanUndo);

        await editor.RedoAsync();
        var redone = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(Blue, redone.Color);
        Assert.Equal(6, redone.Drawing!.Width, 6);
    }

    [Fact]
    public async Task LockedForeignDrawingRefusesReplace()
    {
        using var session = await OpenSessionAsync(DrawingsForeign);
        var editor = new AnnotationEditor(session);
        var ink = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == "ink-0001");
        Assert.False(ink.GeometryEditable);
        var locator = new AnnotationLocator(0, ink.Index, ink.Name);
        var moved = DrawingTransform.Move(ink.Drawing!, 10, 10, new PageRect(0, 0, 400, 300));
        var version = editor.Version;
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);

        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.ReplaceGeometryAsync(locator, moved));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.UpdateAsync(locator, new AnnotationEdit(Color: Blue)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.UpdateAsync(locator, new AnnotationEdit(Geometry: moved)));

        // The history, the version and the drawing are as before.
        Assert.Equal(version, editor.Version);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.Equal(0, Volatile.Read(ref changes));
        var after = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == "ink-0001");
        Assert.Equal(ink.Index, after.Index);
        AssertSameShape(ink.Drawing!, after.Drawing!, 1e-6);

        // A drawing with a reply cannot even be deleted.
        var answered = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == "ink-0008");
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.DeleteAsync(new AnnotationLocator(0, answered.Index, answered.Name)));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public async Task DocumentLimitSurfacesAsException()
    {
        var notes = string.Concat(Enumerable.Repeat("<< /Subtype /Text /Rect [10 10 20 20] >> ", PdfDocument.MaxAnnotations));
        var path = WritePdf(folder, "full.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{notes}] >>",
        ]);
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        var version = editor.Version;
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);

        await Assert.ThrowsAsync<PdfAnnotationLimitException>(
            () => editor.ApplyDrawingAsync(0, DrawingGeometry.Ink([[new(20, 30), new(40, 60)]], 2), Red, null, null));

        Assert.Equal(version, editor.Version);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.False(editor.IsBusy);
        Assert.Empty(editor.ChangedPages);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.Equal(PdfDocument.MaxAnnotations, (await session.Document!.GetAnnotationsAsync(0, Ct)).Count);
    }

    private static readonly AnnotationColor Red = new(220, 30, 30);

    private static void AssertSameShape(DrawingGeometry expected, DrawingGeometry actual, double tolerance)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Strokes.Count, actual.Strokes.Count);
        for (var s = 0; s < expected.Strokes.Count; s++)
        {
            Assert.Equal(expected.Strokes[s].Count, actual.Strokes[s].Count);
            for (var i = 0; i < expected.Strokes[s].Count; i++)
            {
                Assert.True(
                    Math.Abs(expected.Strokes[s][i].X - actual.Strokes[s][i].X) <= tolerance
                    && Math.Abs(expected.Strokes[s][i].Y - actual.Strokes[s][i].Y) <= tolerance,
                    $"Expected {expected.Strokes[s][i]}, got {actual.Strokes[s][i]}.");
            }
        }

        AssertRectNear(expected.Box, actual.Box, tolerance);
    }

    private static RenderedImage SmallImage() => new(1, 1, 4, new byte[4]);

    private string Copy(string fixture)
    {
        var path = Path.Combine(folder.Path, fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    private async Task<ViewerSession> OpenSessionAsync(string fixtureOrPath)
    {
        var path = Path.IsPathRooted(fixtureOrPath) ? fixtureOrPath : Copy(fixtureOrPath);
        var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        Assert.Equal(ViewerState.Ready, session.State);
        return session;
    }
}
