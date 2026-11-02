using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>A recognition run as one undoable, unsaved change, and what saving does with it.</summary>
public sealed class OcrEditorTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    private static OcrPageWord[] Known(string file, int page) =>
        [.. OcrFixtures.WordsOf(file, page).Select(static w => new OcrPageWord(w.Text, w.Left, w.Top, w.Right, w.Bottom, 90))];

    private static OcrRunResult Run(params (int Page, OcrPageWord[] Words)[] pages) =>
        new([.. pages.Select(static p => new OcrPageResult(p.Page, p.Words, 200, false, 90))], OcrReport.Build([], [], []));

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private async Task<ViewerSession> OpenAsync(string name, string? password = null)
    {
        var session = new ViewerSession(engine);
        await session.OpenAsync(Copy(name), password);
        Assert.Equal(ViewerState.Ready, session.State);
        return session;
    }

    private static async Task<string[]> LayerOf(PdfDocument document, int page) =>
        [.. (await document.ReadOcrLayerAsync(page, Ct)).Select(static w => w.Text)];

    [Fact]
    public async Task UndoRemovesTheLayerAndRedoWritesItAgain()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session);
        var words = Known("ocr-scan.pdf", 0);

        var applied = await editor.ApplyOcrAsync(Run((0, words)), replace: false);

        Assert.NotNull(applied);
        Assert.Equal(words.Length, applied.WordsWritten);
        Assert.Equal([0], applied.Pages);
        Assert.Equal(words.Select(static w => w.Text), await LayerOf(session.Document!, 0));

        await editor.UndoAsync();
        Assert.Empty(await LayerOf(session.Document!, 0));
        Assert.False((await session.Document!.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer);

        await editor.RedoAsync();
        Assert.Equal(words.Select(static w => w.Text), await LayerOf(session.Document!, 0));
    }

    [Fact]
    public async Task ReplaceUndoRestoresTheOldLayer()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session);
        var first = Known("ocr-scan.pdf", 0);
        OcrPageWord[] second = [new("Replaced", 40, 40, 160, 64, 80), new("Layer", 180, 40, 260, 64, 80)];
        await editor.ApplyOcrAsync(Run((0, first)), replace: false);

        var applied = await editor.ApplyOcrAsync(Run((0, second)), replace: true);

        Assert.NotNull(applied);
        Assert.Equal(["Replaced", "Layer"], await LayerOf(session.Document!, 0));
        await editor.UndoAsync();
        Assert.Equal(first.Select(static w => w.Text), await LayerOf(session.Document!, 0));
        await editor.RedoAsync();
        Assert.Equal(["Replaced", "Layer"], await LayerOf(session.Document!, 0));
        await editor.UndoAsync();
        await editor.UndoAsync();
        Assert.Empty(await LayerOf(session.Document!, 0));
    }

    [Fact]
    public async Task WithoutReplaceAPageThatAlreadyHasALayerKeepsItAndNeverGetsASecond()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session);
        var first = Known("ocr-scan.pdf", 0);
        await editor.ApplyOcrAsync(Run((0, first)), replace: false);

        var applied = await editor.ApplyOcrAsync(Run((0, [new OcrPageWord("Other", 40, 40, 100, 64, 80)]), (1, Known("ocr-scan.pdf", 1))), replace: false);

        Assert.NotNull(applied);
        Assert.Equal([1], applied.Pages);
        Assert.Equal([0], applied.AlreadyRecognized);
        Assert.Equal(first.Select(static w => w.Text), await LayerOf(session.Document!, 0));
        Assert.Equal(Known("ocr-scan.pdf", 1).Select(static w => w.Text), await LayerOf(session.Document!, 1));
    }

    [Fact]
    public async Task OversizedRunIsAppliedWithoutAnUndoStepAndStaysAnUnsavedChange()
    {
        // A run that does not fit the history (a scanned book) must not be thrown away after an hour: it is written, counts as unsaved, cannot be undone.
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session) { ImageBudget = 200 };

        var applied = await editor.ApplyOcrAsync(Run((0, Known("ocr-scan.pdf", 0))), replace: false);

        Assert.NotNull(applied);
        Assert.False(applied.Undoable);
        Assert.Equal([0], applied.Pages);
        Assert.True(editor.IsDirty);
        Assert.True(editor.HasOcrChanges);
        Assert.False(editor.CanUndo);
        Assert.Contains(0, editor.ChangedPages);
        Assert.True((await session.Document!.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer);
    }

    [Fact]
    public async Task PageWhoseWordsAreAllOutsideTheFontMakesNoStep()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session);

        var applied = await editor.ApplyOcrAsync(Run((0, [new OcrPageWord("Привет", 40, 40, 120, 64, 90), new OcrPageWord("мир", 140, 40, 200, 64, 90)])), replace: false);

        Assert.Null(applied);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public async Task PlanCountsTheWordsLeftOutAndKeepsOnlyWhatTheFontCarries()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var run = Run((0, [new OcrPageWord("Привет", 40, 40, 120, 64, 90), new OcrPageWord("Straße", 140, 40, 220, 64, 90)]), (1, [new OcrPageWord("мир", 40, 40, 100, 64, 90)]));

        var plan = await OcrLayerApplier.PlanAsync(session.Document!, run, replace: false, Ct);

        Assert.Equal([0], plan.After.Keys);
        Assert.Equal(["Straße"], plan.After[0].Select(static w => w.Text));
        Assert.Equal(2, plan.LeftOut);
    }

    [Fact]
    public async Task RunCountsAsAnUnsavedChange()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session);
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasOcrChanges);

        await editor.ApplyOcrAsync(Run((0, Known("ocr-scan.pdf", 0)), (2, Known("ocr-scan.pdf", 2))), replace: false);

        Assert.True(editor.IsDirty);
        Assert.True(editor.CanUndo);
        Assert.True(editor.HasOcrChanges);
        Assert.False(editor.HasOnlyFormChanges);
        Assert.Equal([0, 2], editor.ChangedPages.Order());
        Assert.Equal(1, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task ARunWithoutWordsChangesNothing()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session);

        var applied = await editor.ApplyOcrAsync(Run((0, []), (1, [new OcrPageWord(" ", 1, 1, 1, 1, 50)])), replace: false);

        Assert.Null(applied);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public async Task AFailureHalfWayLeavesNothingBehind()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session);

        // Page 0 is written, page 9 does not exist: the whole step is taken back.
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => editor.ApplyOcrAsync(Run((0, Known("ocr-scan.pdf", 0)), (9, Known("ocr-scan.pdf", 1))), replace: false));

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.False((await session.Document!.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer);
    }

    [Fact]
    public async Task WordsWithCharactersTheFontLacksAreCountedNotReplaced()
    {
        using var session = await OpenAsync("ocr-scan.pdf");
        var editor = new AnnotationEditor(session);
        OcrPageWord[] words = [new("Привет", 40, 40, 120, 64, 90), new("Straße", 140, 40, 220, 64, 90)];

        var applied = await editor.ApplyOcrAsync(Run((0, words)), replace: false);

        Assert.NotNull(applied);
        Assert.Equal(1, applied.WordsWritten);
        Assert.Equal(1, applied.WordsLeftOut);
        Assert.Equal(["Straße"], await LayerOf(session.Document!, 0));
    }

    [Fact]
    public async Task SaveIsIncrementalAndTheOriginalBytesStayThePrefix()
    {
        var path = Copy("ocr-scan.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var editor = new AnnotationEditor(session);
        await editor.ApplyOcrAsync(Run((0, Known("ocr-scan.pdf", 0)), (1, Known("ocr-scan.pdf", 1)), (2, Known("ocr-scan.pdf", 2))), replace: false);

        var result = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        var saved = await File.ReadAllBytesAsync(path, Ct);
        Assert.True(result.Adopted);
        Assert.True(saved.Length > original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes are not the beginning of the saved file");
        // The font is in the file once for all three pages, not three times.
        Assert.True(saved.Length - original.Length < 130_000, $"the layer of three pages took {saved.Length - original.Length} bytes");
    }

    [Fact]
    public async Task SavedFileIsSearchableAfterReopenAndHistoryIsEmpty()
    {
        var path = Copy("ocr-scan.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var editor = new AnnotationEditor(session);
        await editor.ApplyOcrAsync(Run((1, Known("ocr-scan.pdf", 1))), replace: false);

        await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.False(editor.HasOcrChanges);
        await using var reopened = await engine.OpenAsync(path, cancellationToken: Ct);
        var info = await reopened.GetOcrPageInfoAsync(1, Ct);
        Assert.True(info.HasOwnLayer);
        Assert.Equal(0, info.TextCharacters);
        var text = await reopened.GetPageTextAsync(1, Ct);
        var all = text.GetText(0, text.Chars.Count);
        Assert.Contains("Müller", all, StringComparison.Ordinal);
        Assert.Contains("Straße", all, StringComparison.Ordinal);
        Assert.Contains("Größe", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedDocumentAfterARunCountsAsChangedAndWouldInvalidate()
    {
        using var session = await OpenAsync("signed-docmdp-2.pdf");
        var editor = new AnnotationEditor(session);

        await editor.ApplyOcrAsync(Run((0, [new OcrPageWord("Added", 40, 40, 120, 64, 90)])), replace: false);

        Assert.True(editor.HasOcrChanges);
        Assert.False(editor.HasOnlyFormChanges);
        var info = await session.Document!.GetSignatureInfoAsync(Ct);
        Assert.Equal(SignatureAdvice.WouldInvalidate, SignaturePolicy.Evaluate(info));
    }

    [Fact]
    public async Task EncryptedFileStaysEncryptedOrIsRefused()
    {
        const string Password = "hôtel";
        var path = Copy("encrypted_hello_world_r6.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path, Password);
        var editor = new AnnotationEditor(session);
        await editor.ApplyOcrAsync(Run((0, [new OcrPageWord("Added", 40, 40, 120, 64, 90)])), replace: false);

        try
        {
            await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);
        }
        catch (SaveFailureException failure)
        {
            // A refusal is fine; a file that lost its encryption is not.
            Assert.Equal(SaveFailureReason.EncryptedNotSupported, failure.Reason);
            return;
        }

        await using var reopened = await engine.OpenAsync(path, Password, Ct);
        Assert.True((await reopened.GetPrintInfoAsync(Ct)).IsEncrypted);
        Assert.True((await reopened.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer);
    }
}
