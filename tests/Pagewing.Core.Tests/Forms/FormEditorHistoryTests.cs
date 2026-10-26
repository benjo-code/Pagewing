using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// Form values in the history of <see cref="AnnotationEditor"/> (milestone 11, task 8): one step per value, undo and redo write
/// the state before and after through the same path, an unchanged value is no step, a failed write follows the contract of
/// <see cref="FormWriteException"/>. The history of forms and annotations is one.
/// </summary>
public sealed class FormEditorHistoryTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task SettingAValueIsOneStepAndMakesTheEditorDirty()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var cache = session.Cache!;
        cache.Add(new TileKey(1, 1000, 0, 0), SmallImage());
        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        var editor = new AnnotationEditor(session);
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);
        var version = editor.Version;
        Assert.False(editor.IsDirty);

        var change = await editor.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123"));

        Assert.NotNull(change);
        Assert.Equal(FormFieldState.OfText(string.Empty), change.Before);
        Assert.Equal(FormFieldState.OfText("0221 123"), change.After);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.NotEqual(version, editor.Version);
        Assert.Equal([1], editor.ChangedPages);
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.False(cache.Contains(new TileKey(1, 1000, 0, 0)));
        Assert.True(cache.Contains(new TileKey(0, 1000, 0, 0)));
    }

    [Fact]
    public async Task SettingTheSameValueIsNoStep()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var cache = session.Cache!;
        var editor = new AnnotationEditor(session);

        // Name holds "Alt" in the file; writing "Alt" again changes nothing at all.
        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);
        var version = editor.Version;
        Assert.Null(await editor.SetFormValueAsync("Name", FormFieldState.OfText("Alt")));

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.Equal(version, editor.Version);
        Assert.Empty(editor.ChangedPages);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.True(cache.Contains(new TileKey(0, 1000, 0, 0)));

        // The same holds for a value set by the editor itself, and for a check box and a radio group.
        await editor.SetFormValueAsync("Telefon", FormFieldState.OfText("1"));
        var afterOne = editor.Version;
        Assert.Null(await editor.SetFormValueAsync("Telefon", FormFieldState.OfText("1")));
        Assert.Null(await editor.SetFormValueAsync("Agb", FormFieldState.OfChecked(false)));
        Assert.Equal(afterOne, editor.Version);

        // An unchanged value leaves the earlier undo alone.
        await editor.UndoAsync();
        Assert.False(editor.CanUndo);
        Assert.Equal(FormFieldState.OfText(string.Empty), (await FieldAsync(session.Document!, "Telefon")).State);
    }

    [Fact]
    public async Task UndoRestoresTheOldTextAndRedoTheNew()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);

        await editor.SetFormValueAsync("Name", FormFieldState.OfText("Neu"));
        await editor.SetFormValueAsync("Name", FormFieldState.OfText("Neuer"));

        await editor.UndoAsync();
        Assert.Equal(FormFieldState.OfText("Neu"), (await FieldAsync(session.Document!, "Name")).State);
        Assert.True(editor.CanUndo);
        Assert.True(editor.CanRedo);

        await editor.UndoAsync();
        Assert.Equal(FormFieldState.OfText("Alt"), (await FieldAsync(session.Document!, "Name")).State);
        Assert.False(editor.CanUndo);
        Assert.True(editor.IsDirty);

        await editor.RedoAsync();
        await editor.RedoAsync();
        Assert.Equal(FormFieldState.OfText("Neuer"), (await FieldAsync(session.Document!, "Name")).State);
        Assert.False(editor.CanRedo);
        Assert.Equal([0, 1], editor.ChangedPages.Order());
    }

    [Fact]
    public async Task UndoOfARadioChoiceRestoresThePreviousChoice()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);

        await editor.SetFormValueAsync("Zahlung", FormFieldState.OfText("Bar"));
        await editor.SetFormValueAsync("Zahlung", FormFieldState.OfText("Karte"));
        Assert.Equal(FormFieldState.OfText("Karte"), (await FieldAsync(session.Document!, "Zahlung")).State);

        await editor.UndoAsync();
        Assert.Equal(FormFieldState.OfText("Bar"), (await FieldAsync(session.Document!, "Zahlung")).State);

        await editor.RedoAsync();
        Assert.Equal(FormFieldState.OfText("Karte"), (await FieldAsync(session.Document!, "Zahlung")).State);
    }

    [Fact]
    public async Task UndoOfAChoiceOnAnotherPageInvalidatesBothPages()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);
        await editor.SetFormValueAsync("Zahlung", FormFieldState.OfText("Bar"));
        await editor.SetFormValueAsync("Zahlung", FormFieldState.OfText("Rechnung"));
        Assert.Equal([0, 1], editor.ChangedPages.Order());

        var invalidated = new List<int[]>();
        session.PagesInvalidated += pages => invalidated.Add([.. pages.Order()]);
        var cache = session.Cache!;
        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        cache.Add(new TileKey(1, 1000, 0, 0), SmallImage());

        await editor.UndoAsync();

        Assert.Equal(FormFieldState.OfText("Bar"), (await FieldAsync(session.Document!, "Zahlung")).State);
        AssertInvalidated(invalidated, 0, 1);
        Assert.False(cache.Contains(new TileKey(0, 1000, 0, 0)));
        Assert.False(cache.Contains(new TileKey(1, 1000, 0, 0)));
        Assert.Equal([0, 1], editor.ChangedPages.Order());
    }

    [Fact]
    public async Task UndoToNoChoiceOfANoToggleToOffGroupDiscardsTheStep()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);
        await editor.SetFormValueAsync("Telefon", FormFieldState.OfText("0221"));
        await editor.SetFormValueAsync("Stufe", FormFieldState.OfText("1"));
        await editor.SetFormValueAsync("Ort", FormFieldState.OfText("Köln"));

        // Undo the city, then the first choice of the group: the engine cannot undo a choice, so the step is forgotten.
        await editor.UndoAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => editor.UndoAsync());
        Assert.IsNotType<FormWriteException>(error);
        Assert.Equal(FormFieldState.OfText("1"), (await FieldAsync(session.Document!, "Stufe")).State);

        // The step is gone from both stacks and the older step stays usable; the document and the editor stay marked as changed.
        Assert.True(editor.CanUndo);
        Assert.True(editor.CanRedo);
        Assert.True(editor.IsDirty);
        await editor.UndoAsync();
        Assert.Equal(FormFieldState.OfText(string.Empty), (await FieldAsync(session.Document!, "Telefon")).State);
        Assert.False(editor.CanUndo);

        // Redo still has the city; the discarded choice is not redone.
        await editor.RedoAsync();
        Assert.Equal(FormFieldState.OfText("0221"), (await FieldAsync(session.Document!, "Telefon")).State);
    }

    [Fact]
    public async Task FormStepsAndAnnotationStepsShareOneHistory()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);
        var rects = await WordRectsAsync(session.Document!, 0, "fixture");
        var markup = await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null);
        Assert.NotNull(markup);
        await editor.SetFormValueAsync("Ort", FormFieldState.OfText("Köln"));
        var version = editor.Version;

        await editor.UndoAsync();
        Assert.Equal(FormFieldState.OfText(string.Empty), (await FieldAsync(session.Document!, "Ort")).State);
        Assert.Contains(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == markup.Name);

        await editor.UndoAsync();
        Assert.DoesNotContain(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == markup.Name);
        Assert.False(editor.CanUndo);
        Assert.NotEqual(version, editor.Version);

        await editor.RedoAsync();
        await editor.RedoAsync();
        Assert.Equal(FormFieldState.OfText("Köln"), (await FieldAsync(session.Document!, "Ort")).State);
        Assert.Contains(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == markup.Name);
    }

    [Fact]
    public async Task SaveEmptiesTheHistoryAndKeepsTheFieldsFillable()
    {
        var path = Copy(FormsBasic);
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        await editor.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123"));
        await editor.SetFormValueAsync("Zahlung", FormFieldState.OfText("Karte"));
        await editor.SetFormValueAsync("Agb", FormFieldState.OfChecked(true));

        var result = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);

        Assert.True(result.Adopted);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.Empty(editor.ChangedPages);
        Assert.Equal(FormFieldState.OfText("0221 123"), (await FieldAsync(session.Document!, "Telefon")).State);
        Assert.Equal(FormFieldState.OfText("Karte"), (await FieldAsync(session.Document!, "Zahlung")).State);
        Assert.Equal(FormFieldState.OfChecked(true), (await FieldAsync(session.Document!, "Agb")).State);

        // Fillable and undoable after the save, and it reads back from the file.
        var change = await editor.SetFormValueAsync("Telefon", FormFieldState.OfText("0"));
        Assert.Equal(FormFieldState.OfText("0221 123"), change!.Before);
        await editor.UndoAsync();
        Assert.Equal(FormFieldState.OfText("0221 123"), (await FieldAsync(session.Document!, "Telefon")).State);
        await using var reopened = await OpenAsync(path);
        Assert.Equal(FormFieldState.OfText("0221 123"), (await FieldAsync(reopened, "Telefon")).State);
        Assert.Equal(FormFieldState.OfText("Karte"), (await FieldAsync(reopened, "Zahlung")).State);
    }

    [Fact]
    public async Task AnnotationDeleteBeforeAFormStepDoesNotBreakItsField()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);
        var rects = await WordRectsAsync(session.Document!, 0, "fixture");
        var markup = (await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null))!;
        await editor.SetFormValueAsync("Bemerkung", FormFieldState.OfText("Eins"));

        // The annotation indexes of the page shift when an earlier markup goes: the form step finds its field by name.
        await editor.UndoAsync();
        await editor.UndoAsync();
        await editor.RedoAsync();
        await editor.DeleteAsync(new AnnotationLocator(0, markup.Index, markup.Name));
        Assert.DoesNotContain(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == markup.Name);
        await editor.SetFormValueAsync("Bemerkung", FormFieldState.OfText("Zwei"));

        await editor.UndoAsync();
        Assert.Equal(FormFieldState.OfText(string.Empty), (await FieldAsync(session.Document!, "Bemerkung")).State);
        await editor.UndoAsync();
        Assert.Contains(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == markup.Name);
        await editor.RedoAsync();
        await editor.RedoAsync();
        Assert.Equal(FormFieldState.OfText("Zwei"), (await FieldAsync(session.Document!, "Bemerkung")).State);
    }

    [Fact]
    public async Task FormChangeDoesNotChangeTheAnnotationCountOfThePage()
    {
        var path = Copy(FormsBasic);
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        var before = (await session.Document!.GetAnnotationsAsync(0, Ct)).Count;
        var beforeSecond = (await session.Document!.GetAnnotationsAsync(1, Ct)).Count;

        await editor.SetFormValueAsync("Name", FormFieldState.OfText("Geändert"));
        await editor.SetFormValueAsync("Zahlung", FormFieldState.OfText("Rechnung"));
        await editor.SetFormValueAsync("Agb", FormFieldState.OfChecked(true));

        Assert.Equal(before, (await session.Document!.GetAnnotationsAsync(0, Ct)).Count);
        Assert.Equal(beforeSecond, (await session.Document!.GetAnnotationsAsync(1, Ct)).Count);

        // The check of the save compares these counts of the changed pages in the written file; it must pass.
        var result = await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, Ct);
        Assert.True(result.Adopted);
        Assert.Equal(before, (await session.Document!.GetAnnotationsAsync(0, Ct)).Count);
        Assert.Equal(beforeSecond, (await session.Document!.GetAnnotationsAsync(1, Ct)).Count);
        Assert.Equal(FormFieldState.OfText("Geändert"), (await FieldAsync(session.Document!, "Name")).State);
    }

    [Fact]
    public async Task ARefusedValueLeavesTheHistoryAndTheDocumentAlone()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);

        await Assert.ThrowsAsync<FormFieldNotFoundException>(() => editor.SetFormValueAsync("Gibt es nicht", FormFieldState.OfText("x")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => editor.SetFormValueAsync("Telefon", FormFieldState.OfText("😀")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.SetFormValueAsync("Gesperrt", FormFieldState.OfText("x")));

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.Empty(editor.ChangedPages);
        Assert.False(editor.IsBusy);
    }

    [Fact]
    public async Task ARestoredFailureInvalidatesThePagesButMakesNoStep()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);
        var invalidated = new List<int[]>();
        session.PagesInvalidated += pages => invalidated.Add([.. pages.Order()]);
        session.Document!.FormFaults = new FormWriteFaults { ReadBackThrows = true };

        var error = await Assert.ThrowsAsync<FormWriteException>(() => editor.SetFormValueAsync("Telefon", FormFieldState.OfText("Halb")));

        Assert.True(error.Restored);
        AssertInvalidated(invalidated, 1);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.Empty(editor.ChangedPages);
        session.Document.FormFaults = null;
        Assert.Equal(FormFieldState.OfText(string.Empty), (await FieldAsync(session.Document, "Telefon")).State);
    }

    [Fact]
    public async Task AnUnrestoredFailureMakesTheEditorDirtyWithoutAStep()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);
        var changes = 0;
        editor.Changed += () => Interlocked.Increment(ref changes);
        var invalidated = new List<int[]>();
        session.PagesInvalidated += pages => invalidated.Add([.. pages.Order()]);
        var version = editor.Version;
        session.Document!.FormFaults = new FormWriteFaults { ReadBackThrows = true, RestoreThrows = true };

        var error = await Assert.ThrowsAsync<FormWriteException>(() => editor.SetFormValueAsync("Telefon", FormFieldState.OfText("Halb")));

        Assert.False(error.Restored);
        AssertInvalidated(invalidated, 1);
        Assert.True(editor.IsDirty);
        Assert.NotEqual(version, editor.Version);
        Assert.False(editor.CanUndo);
        Assert.Equal([1], editor.ChangedPages);
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.False(editor.IsBusy);
    }

    [Fact]
    public async Task AFailedUndoWithoutRestoreDiscardsTheStepAndStaysDirty()
    {
        using var session = await OpenSessionAsync(FormsBasic);
        var editor = new AnnotationEditor(session);
        await editor.SetFormValueAsync("Name", FormFieldState.OfText("Eins"));
        await editor.SetFormValueAsync("Ort", FormFieldState.OfText("Köln"));
        session.Document!.FormFaults = new FormWriteFaults { ReadBackThrows = true, RestoreThrows = true };

        var error = await Assert.ThrowsAsync<FormWriteException>(() => editor.UndoAsync());

        Assert.False(error.Restored);
        Assert.True(editor.IsDirty);
        Assert.False(editor.CanRedo);
        Assert.True(editor.CanUndo);
        Assert.Equal([0, 1], editor.ChangedPages.Order());

        // The older step stays usable once the engine works again.
        session.Document.FormFaults = null;
        await editor.UndoAsync();
        Assert.Equal(FormFieldState.OfText("Alt"), (await FieldAsync(session.Document, "Name")).State);
    }

    private static void AssertInvalidated(List<int[]> invalidated, params int[] expected) =>
        Assert.Contains(invalidated, pages => pages.AsSpan().SequenceEqual(expected));

    private static RenderedImage SmallImage() => new(1, 1, 4, new byte[4]);

    private static async Task<FormField> FieldAsync(PdfDocument document, string name)
    {
        var pages = await document.GetFormInfoAsync(Ct);
        foreach (var page in pages.PagesWithFields)
        {
            var found = (await document.GetFormFieldsAsync(page, Ct)).FirstOrDefault(f => f.Key.FieldName == name);
            if (found is not null)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"No field '{name}'.");
    }

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
