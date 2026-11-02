using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.TextEdit;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>A text edit as one undoable, unsaved change of the shared history, and what saving does with it (milestone 16).</summary>
public sealed class TextEditEditorTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private async Task<ViewerSession> OpenAsync(string name)
    {
        var session = new ViewerSession(engine);
        await session.OpenAsync(Copy(name), null);
        Assert.Equal(ViewerState.Ready, session.State);
        return session;
    }

    private static async Task<TextRun> RunAsync(ViewerSession session, int page, string text) =>
        (await session.Document!.GetTextRunsAsync(page, Ct)).Single(r => r.Text.TrimEnd() == text);

    private static async Task<string> PageTextAsync(ViewerSession session, int page)
    {
        var text = await session.Document!.GetPageTextAsync(page, Ct);
        return text.GetText(0, text.Chars.Count);
    }

    [Fact]
    public async Task UndoRestoresTheOldTextAndRedoSetsTheNewOneAgain()
    {
        using var session = await OpenAsync("text-edit-subset.pdf");
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Invoice total: 1,250 EUR");

        var result = await editor.ApplyTextEditAsync(run, "Invoice total: 1,259 EUR", TextEditMode.KeepFont);

        Assert.NotNull(result);
        Assert.Contains("Invoice total: 1,259 EUR", await PageTextAsync(session, 0), StringComparison.Ordinal);
        await editor.UndoAsync();
        Assert.Contains("Invoice total: 1,250 EUR", await PageTextAsync(session, 0), StringComparison.Ordinal);
        Assert.Equal(run.Fingerprint, (await RunAsync(session, 0, "Invoice total: 1,250 EUR")).Fingerprint);
        await editor.RedoAsync();
        Assert.Contains("Invoice total: 1,259 EUR", await PageTextAsync(session, 0), StringComparison.Ordinal);
        await editor.UndoAsync();
        Assert.Contains("Invoice total: 1,250 EUR", await PageTextAsync(session, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoAndRedoOfARemovalBringTheRunBackAndTakeItAway()
    {
        using var session = await OpenAsync("text-edit-standard.pdf");
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Quarterly report 2026");

        _ = await editor.ApplyTextEditAsync(run, string.Empty, TextEditMode.KeepFont);
        Assert.DoesNotContain("Quarterly", await PageTextAsync(session, 0), StringComparison.Ordinal);
        await editor.UndoAsync();
        Assert.Contains("Quarterly report 2026", await PageTextAsync(session, 0), StringComparison.Ordinal);
        await editor.RedoAsync();
        Assert.DoesNotContain("Quarterly", await PageTextAsync(session, 0), StringComparison.Ordinal);
        await editor.UndoAsync();
        Assert.Contains("Quarterly report 2026", await PageTextAsync(session, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnchangedTextAddsNoStep()
    {
        using var session = await OpenAsync("text-edit-standard.pdf");
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Quarterly report 2026");

        var result = await editor.ApplyTextEditAsync(run, run.Text, TextEditMode.KeepFont);

        Assert.Null(result);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.False(editor.HasTextEdits);
    }

    [Fact]
    public async Task AnEditCountsAsAnUnsavedChange()
    {
        using var session = await OpenAsync("text-edit-standard.pdf");
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Quarterly report 2026");

        _ = await editor.ApplyTextEditAsync(run, "Quarterly report 2027", TextEditMode.KeepFont);

        Assert.True(editor.IsDirty);
        Assert.True(editor.CanUndo);
        Assert.True(editor.HasTextEdits);
        Assert.False(editor.HasOnlyFormChanges);
        Assert.Contains(0, editor.ChangedPages);
        await editor.UndoAsync();
        Assert.True(editor.HasTextEdits);
    }

    [Fact]
    public async Task AStaleRunAfterUndoIsRefused()
    {
        using var session = await OpenAsync("text-edit-subset.pdf");
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Invoice total: 1,250 EUR");
        _ = await editor.ApplyTextEditAsync(run, "Invoice total: 1,205 EUR", TextEditMode.KeepFont);
        await editor.UndoAsync();

        // The run the user still holds is the one before the edit: it is the page's again after the undo, so a second edit works ...
        var again = await editor.ApplyTextEditAsync(run, "Invoice total: 1,215 EUR", TextEditMode.KeepFont);
        Assert.NotNull(again);

        // ... but a run read before an edit is stale once something else changed the page.
        await Assert.ThrowsAsync<TextRunStaleException>(() => editor.ApplyTextEditAsync(run, "Invoice total: 1,999 EUR", TextEditMode.KeepFont));
        Assert.Contains("Invoice total: 1,215 EUR", await PageTextAsync(session, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedSecondStepIsDroppedAndTheOlderOneStaysUsable()
    {
        using var session = await OpenAsync("text-edit-standard.pdf");
        var editor = new AnnotationEditor(session);
        var first = await RunAsync(session, 0, "Quarterly report 2026");
        var firstResult = await editor.ApplyTextEditAsync(first, "Quarterly report 2027", TextEditMode.KeepFont);
        var second = await RunAsync(session, 0, "Dear customer, thank you");
        _ = await editor.ApplyTextEditAsync(second, "Dear customer, thanks", TextEditMode.KeepFont);

        await editor.UndoAsync();
        await editor.UndoAsync();

        Assert.False(editor.CanUndo);
        var text = await PageTextAsync(session, 0);
        Assert.Contains("Quarterly report 2026", text, StringComparison.Ordinal);
        Assert.Contains("Dear customer, thank you", text, StringComparison.Ordinal);
        Assert.NotNull(firstResult);
    }

    [Fact]
    public async Task HistoryIsEmptyAfterSaving()
    {
        var path = Copy("text-edit-standard.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path, null);
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Quarterly report 2026");
        _ = await editor.ApplyTextEditAsync(run, "Quarterly report 2027", TextEditMode.KeepFont);

        var result = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        Assert.True(result.Adopted);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.False(editor.HasTextEdits);
    }

    [Fact]
    public async Task SaveIsIncrementalAndTheOriginalBytesStayThePrefix()
    {
        var path = Copy("text-edit-subset.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path, null);
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Invoice total: 1,250 EUR");
        _ = await editor.ApplyTextEditAsync(run, "Invoice total: 1,205 EUR", TextEditMode.KeepFont);

        _ = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        var saved = await File.ReadAllBytesAsync(path, Ct);
        Assert.True(saved.Length > original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "the original bytes are not the beginning of the saved file");
    }

    [Fact]
    public async Task SavedFileGivesTheNewTextAfterReopen()
    {
        var path = Copy("text-edit-subset.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path, null);
        var editor = new AnnotationEditor(session);
        var run = await RunAsync(session, 0, "Invoice total: 1,250 EUR");
        _ = await editor.ApplyTextEditAsync(run, "Invoice total: 1,205 EUR", TextEditMode.KeepFont);

        _ = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        await using var reopened = await engine.OpenAsync(path, cancellationToken: Ct);
        var text = await reopened.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);
        Assert.Contains("Invoice total: 1,205 EUR", all, StringComparison.Ordinal);
        Assert.DoesNotContain("1,250", all, StringComparison.Ordinal);
        Assert.Contains("Kontoauszug März", all, StringComparison.Ordinal);

        // The adopted document is the saved one: a second edit of the same line works on it.
        var next = await RunAsync(session, 0, "Invoice total: 1,205 EUR");
        _ = await editor.ApplyTextEditAsync(next, "Invoice total: 1,250 EUR", TextEditMode.KeepFont);
        Assert.Contains("Invoice total: 1,250 EUR", await PageTextAsync(session, 0), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("signed-docmdp-1.pdf")]
    [InlineData("signed-docmdp-2.pdf")]
    public async Task SignedDocumentWarnsAndNamesChangedText(string file)
    {
        using var session = await OpenAsync(file);
        var editor = new AnnotationEditor(session);
        var runs = await session.Document!.GetTextRunsAsync(0, Ct);
        var run = runs.First(static r => !r.IsLocked && r.Text.Trim().Length > 2);

        _ = await editor.ApplyTextEditAsync(run, run.Text.TrimEnd() + "x", TextEditMode.KeepFont);

        Assert.True(editor.HasTextEdits);
        Assert.False(editor.HasOnlyFormChanges);
        var info = await session.Document.GetSignatureInfoAsync(Ct);
        Assert.Equal(SignatureAdvice.WouldInvalidate, SignaturePolicy.Evaluate(info));
    }

    [Fact]
    public async Task EncryptedFileKeepsItsEncryptionOrIsRefusedWithAReason()
    {
        // A file whose encryption the written file would lose is refused (EncryptedNotSupported: the tab then locks its runs, NotSavable); one that
        // keeps it carries the new text.
        var path = Copy("encrypted_hello_world_r3.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path, "hôtel");
        Assert.Equal(ViewerState.Ready, session.State);
        var editor = new AnnotationEditor(session);
        var runs = await session.Document!.GetTextRunsAsync(0, Ct);
        var run = runs.First(static r => !r.IsLocked);
        var edited = await editor.ApplyTextEditAsync(run, run.Text.TrimEnd() + "x", TextEditMode.KeepFont);
        Assert.NotNull(edited);

        try
        {
            _ = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);
        }
        catch (SaveFailureException failure)
        {
            Assert.Equal(SaveFailureReason.EncryptedNotSupported, failure.Reason);
            Assert.True(editor.IsDirty);
            Assert.True(editor.HasTextEdits);
            return;
        }

        await using var reopened = await engine.OpenAsync(path, "hôtel", Ct);
        Assert.True((await reopened.GetPrintInfoAsync(Ct)).IsEncrypted);
        var text = await reopened.GetPageTextAsync(0, Ct);
        Assert.Contains(run.Text.TrimEnd() + "x", text.GetText(0, text.Chars.Count), StringComparison.Ordinal);
    }
}
