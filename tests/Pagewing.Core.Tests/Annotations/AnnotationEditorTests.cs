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
        Assert.Same(result.Reopened, session.Document);
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
    public async Task WithoutADocumentTheEditorRefuses()
    {
        using var session = new ViewerSession(engine);
        var editor = new AnnotationEditor(session);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(10, 10, 50, 20)], Yellow, null, null));
        await editor.UndoAsync();
        Assert.False(editor.IsDirty);
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
