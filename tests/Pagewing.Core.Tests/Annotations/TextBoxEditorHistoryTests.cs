using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>Text boxes through <see cref="AnnotationEditor"/>: one step of the history per create, change and delete (milestone 10).</summary>
public sealed class TextBoxEditorHistoryTests : IDisposable
{
    private static readonly AnnotationColor TextBlue = new(30, 80, 220);

    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    public TextBoxEditorHistoryTests() => UseTextBoxFont();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task BlankTextCreatesNothing()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);

        Assert.Null(await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 180, 60), " \n\t ", 12, TextBlue), null));

        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
    }

    [Fact]
    public async Task CreateIsOneStepAndUndoAndRedoFollowIt()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 180, 60), "Hallo Welt", 16, TextBlue), "Ada"))!;
        Assert.True(editor.IsDirty);
        Assert.Equal([0], editor.ChangedPages);

        await editor.UndoAsync();
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.False(editor.CanUndo);

        await editor.RedoAsync();
        var restored = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, restored.Name);
        Assert.Equal(created.TextBox, restored.TextBox);
        Assert.Equal("Ada", restored.Author);
        Assert.True(restored.GeometryEditable);
    }

    [Fact]
    public async Task MoveAndRetypeAreOneStepEach()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 180, 60), "First", 12, TextBlue), null))!;

        var moved = await editor.ReplaceTextBoxAsync(LocatorOf(created), created.TextBox! with { Box = new PageRect(60, 100, 220, 140) });
        var retyped = await editor.ReplaceTextBoxAsync(LocatorOf(moved), moved.TextBox! with { Text = "Second, longer text", FontSize = 24, Color = new AnnotationColor(220, 30, 30) });
        Assert.Equal(created.Name, retyped.Name);

        await editor.UndoAsync();
        var afterOne = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(moved.TextBox, afterOne.TextBox);

        await editor.UndoAsync();
        var afterTwo = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.TextBox, afterTwo.TextBox);
        Assert.True(editor.CanUndo);

        await editor.RedoAsync();
        await editor.RedoAsync();
        Assert.Equal(retyped.TextBox, Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct)).TextBox);
    }

    [Fact]
    public async Task DeleteThenUndoRecreatesTheTextBox()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 180, 60), "Grüße\naus Köln", 12, TextBlue), "Ada"))!;

        await editor.DeleteAsync(LocatorOf(created));
        Assert.Empty(await session.Document!.GetAnnotationsAsync(0, Ct));

        await editor.UndoAsync();
        var restored = Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct));
        Assert.Equal(created.Name, restored.Name);
        Assert.Equal(created.TextBox, restored.TextBox);
        Assert.Equal("Ada", restored.Author);
    }

    [Fact]
    public async Task AForeignTextBoxCanBeDeletedAndComesBackAsPagewingsOwn()
    {
        var path = WritePdf(folder, "foreign.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /FreeText /Rect [20 200 220 260] /Contents (Foreign) /DA (/Helv 14 Tf 1 0 0 rg) /NM (foreign) /F 4 >>",
            "<< /Type /Annot /Subtype /FreeText /Rect [20 100 220 160] /Contents <FEFF03A9> /DA (/Helv 14 Tf 0 g) /NM (greek) /F 4 >>",
        ]);
        using var session = await OpenSessionAsync(path);
        var editor = new AnnotationEditor(session);
        var annotations = await session.Document!.GetAnnotationsAsync(0, Ct);
        var foreign = annotations[0];
        Assert.False(foreign.GeometryEditable);

        // Changing it is refused, deleting it is one step; undo makes it again from what was read, now in Pagewing's look.
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.ReplaceTextBoxAsync(LocatorOf(foreign), foreign.TextBox! with { Text = "Other" }));
        Assert.False(editor.CanUndo);
        await editor.DeleteAsync(LocatorOf(foreign));
        await editor.UndoAsync();
        var restored = (await session.Document!.GetAnnotationsAsync(0, Ct)).Single(a => a.Name == "foreign");
        Assert.Equal("Foreign", restored.Contents);
        Assert.Equal(14, restored.TextBox!.FontSize);
        Assert.Equal(new AnnotationColor(255, 0, 0), restored.TextBox.Color);
        Assert.True(restored.GeometryEditable);

        // A text Pagewing cannot show could not be restored by undo, so it is not deleted.
        var greek = annotations[1];
        Assert.Equal("Ω", greek.Contents);
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.DeleteAsync(LocatorOf(greek)));
        Assert.Contains(await session.Document!.GetAnnotationsAsync(0, Ct), a => a.Name == "greek");
    }

    [Fact]
    public async Task RefusedTextLeavesTheHistoryAlone()
    {
        using var session = await OpenSessionAsync("text-basic.pdf");
        var editor = new AnnotationEditor(session);
        var created = (await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 180, 60), "Fine", 12, TextBlue), null))!;
        var version = editor.Version;

        await Assert.ThrowsAsync<ArgumentException>(() => editor.ReplaceTextBoxAsync(LocatorOf(created), created.TextBox! with { Text = "Ω" }));
        await Assert.ThrowsAsync<ArgumentException>(() => editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 80, 180, 120), "😀", 12, TextBlue), null));

        Assert.Equal(version, editor.Version);
        Assert.Equal("Fine", Assert.Single(await session.Document!.GetAnnotationsAsync(0, Ct)).Contents);
    }

    private static AnnotationLocator LocatorOf(PdfAnnotation annotation) => new(annotation.Page, annotation.Index, annotation.Name);

    private async Task<ViewerSession> OpenSessionAsync(string fixtureOrPath)
    {
        var path = fixtureOrPath;
        if (!Path.IsPathRooted(fixtureOrPath))
        {
            path = Path.Combine(folder.Path, "copy-" + fixtureOrPath);
            File.Copy(TestData.Pdf(fixtureOrPath), path);
        }

        var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        Assert.Equal(ViewerState.Ready, session.State);
        return session;
    }
}
