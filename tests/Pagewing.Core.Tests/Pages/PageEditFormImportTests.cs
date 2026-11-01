using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pages;

/// <summary>
/// Pages with form fields imported from another file (milestone 13, task 14b): the imported widget pages point into their
/// source until the document they were imported into is closed, so a full rewrite of that document crashed PDFium (an access
/// violation in <c>FPDF_SaveAsCopy</c>) once the source was closed. The destination now keeps its sources open until it closes.
/// </summary>
public sealed class PageEditFormImportTests
{
    private static readonly PageSourceId First = new(1);

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APageImportedFromAFormKeepsItsSourceOpenUntilTheCopyCloses()
    {
        using var folder = new TempFolder();
        await using var copy = await engine.OpenAsync(Copy(folder, "forms-basic.pdf"), cancellationToken: Ct);
        var source = await engine.OpenAsync(Copy(folder, "forms-basic.pdf"), cancellationToken: Ct);

        await copy.ImportPagesAsync(source, [0], copy.PageCount, Ct);
        await source.DisposeAsync();

        // Closed for the caller, but PDFium still has it: the imported page reads from it when the copy is written.
        Assert.Equal(1, copy.KeptOpenCount);
        Assert.True(source.IsNativeOpen);
        Assert.Throws<ObjectDisposedException>(() => source.PageCount);

        await copy.DisposeAsync();
        Assert.False(source.IsNativeOpen);
    }

    [Fact]
    public async Task EverySourceIsKeptOnceAndClosedExactlyOnceInAnyOrder()
    {
        using var folder = new TempFolder();
        var copy = await engine.OpenAsync(Copy(folder, "hello_world.pdf"), cancellationToken: Ct);
        var forms = await engine.OpenAsync(Copy(folder, "forms-basic.pdf"), cancellationToken: Ct);
        var rich = await engine.OpenAsync(Copy(folder, "text-rich.pdf"), cancellationToken: Ct);
        var neverUsed = await engine.OpenAsync(Copy(folder, "text-basic.pdf"), cancellationToken: Ct);

        await copy.ImportPagesAsync(forms, [0], copy.PageCount, Ct);
        await copy.ImportPagesAsync(forms, [1], copy.PageCount, Ct);
        await copy.ImportPagesAsync(rich, [0], copy.PageCount, Ct);
        await copy.ImportPagesAsync(copy, [0], copy.PageCount, Ct);

        // The same source twice and the copy itself add nothing.
        Assert.Equal(2, copy.KeptOpenCount);

        // Closed in a different order than they were imported, a source twice, and the one that was never used at once.
        await rich.DisposeAsync();
        await forms.DisposeAsync();
        await forms.DisposeAsync();
        await neverUsed.DisposeAsync();
        Assert.True(rich.IsNativeOpen);
        Assert.True(forms.IsNativeOpen);
        Assert.False(neverUsed.IsNativeOpen);

        await copy.DisposeAsync();
        await copy.DisposeAsync();
        Assert.False(rich.IsNativeOpen);
        Assert.False(forms.IsNativeOpen);
        Assert.False(copy.IsNativeOpen);
    }

    [Fact]
    public async Task AnImportRefusedBeforePdfiumIsCalledKeepsNothing()
    {
        using var folder = new TempFolder();
        await using var copy = await engine.OpenAsync(Copy(folder, "hello_world.pdf"), cancellationToken: Ct);
        var source = await engine.OpenAsync(Copy(folder, "forms-basic.pdf"), cancellationToken: Ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => copy.ImportPagesAsync(source, [5], copy.PageCount, Ct));

        Assert.Equal(0, copy.KeptOpenCount);
        await source.DisposeAsync();
        Assert.False(source.IsNativeOpen);
    }

    [Theory]
    [InlineData("forms-basic.pdf", "forms-basic.pdf", "end")]
    [InlineData("hello_world.pdf", "forms-basic.pdf", "end")]
    [InlineData("forms-basic.pdf", "forms-basic.pdf", "front")]
    [InlineData("forms-basic.pdf", "forms-basic.pdf", "several")]
    [InlineData("hello_world.pdf", "forms-basic.pdf", "several")]
    [InlineData("forms-basic.pdf", "hello_world.pdf", "end")]
    public async Task WritingPagesImportedFromAFormNeverCrashesPdfium(string primaryName, string sourceName, string where)
    {
        // Before the fix PDFium died in FPDF_SaveAsCopy on the first write of the cases with a form in the inserted file
        // (access violation, the whole test host); 40 writes each is far more than that needs. The long run (1,200 each) is
        // a throwaway harness, not a test.
        using var folder = new TempFolder();
        var primary = Copy(folder, primaryName);
        var inserted = Copy(folder, sourceName);
        await using var shown = await engine.OpenAsync(primary, cancellationToken: Ct);
        await using var opened = await engine.OpenAsync(inserted, cancellationToken: Ct);
        var files = new Dictionary<PageSourceId, PageSourceFile> { [First] = PageSourceFile.Of(inserted, null, opened) };
        var count = shown.PageCount;
        var plan = where switch
        {
            "end" => PageEditPlan.Identity(count).InsertFrom(First, [0], count),
            "front" => PageEditPlan.Identity(count).InsertFrom(First, [0], 0),
            _ => PageEditPlan.Identity(count).InsertFrom(First, [0, Math.Min(1, opened.PageCount - 1), 0], 1).Duplicate(new HashSet<int> { 0 }),
        };
        var target = Path.Combine(folder.Path, "out.pdf");
        for (var i = 0; i < 40; i++)
        {
            var written = await PageEditApplier.WriteAsync(engine, shown, primary, null, null, new PageApplyRequest(plan, target, files), null, Ct);
            Assert.Equal(plan.Slots.Count, written.Reopened.PageCount);
            await written.Reopened.DisposeAsync();
        }
    }

    [Fact]
    public async Task TwoFormsAreMergedIntoOneFileThatOpensWithTheFieldsOnEveryPage()
    {
        using var folder = new TempFolder();
        var primary = Copy(folder, "forms-basic.pdf");
        var inserted = Copy(folder, "forms-basic.pdf");
        var target = Path.Combine(folder.Path, "merged.pdf");
        await using var shown = await engine.OpenAsync(primary, cancellationToken: Ct);
        await using var opened = await engine.OpenAsync(inserted, cancellationToken: Ct);
        var files = new Dictionary<PageSourceId, PageSourceFile> { [First] = PageSourceFile.Of(inserted, null, opened) };
        var plan = PageEditPlan.Identity(2).InsertFrom(First, [0, 1], 2);
        var originalTexts = new[] { await TextOf(shown, 0), await TextOf(shown, 1) };
        var originalFields = new[] { await FieldNames(shown, 0), await FieldNames(shown, 1) };

        var written = await PageEditApplier.WriteAsync(engine, shown, primary, null, null, new PageApplyRequest(plan, target, files), null, Ct);
        await written.Reopened.DisposeAsync();

        // The file on disk opens on its own and has the four pages, the text of each as in its file.
        await using var merged = await engine.OpenAsync(target, cancellationToken: Ct);
        Assert.Equal(4, merged.PageCount);
        Assert.Equal(originalTexts, new[] { await TextOf(merged, 0), await TextOf(merged, 1) });
        Assert.Equal(originalTexts, new[] { await TextOf(merged, 2), await TextOf(merged, 3) });

        // Spike (Docs/DECISIONS.md, "Seiten organisieren: Spike und QPDF-Entscheidung (M13)"): the file has the form of its
        // first file, and the widgets of the imported pages arrive as annotations on their pages, which PDFium reads as the
        // fields of the same names.
        Assert.True(merged.HasAcroForm);
        Assert.NotEmpty(originalFields[0]);
        Assert.Equal(originalFields[0], await FieldNames(merged, 0));
        Assert.Equal(originalFields[1], await FieldNames(merged, 1));
        Assert.Equal(originalFields[0], await FieldNames(merged, 2));
        Assert.Equal(originalFields[1], await FieldNames(merged, 3));
    }

    [Fact]
    public async Task APageOfAFormInsertedIntoAFileWithoutOneArrivesWithoutAForm()
    {
        using var folder = new TempFolder();
        var primary = Copy(folder, "hello_world.pdf");
        var inserted = Copy(folder, "forms-basic.pdf");
        var target = Path.Combine(folder.Path, "merged.pdf");
        await using var shown = await engine.OpenAsync(primary, cancellationToken: Ct);
        await using var opened = await engine.OpenAsync(inserted, cancellationToken: Ct);
        var files = new Dictionary<PageSourceId, PageSourceFile> { [First] = PageSourceFile.Of(inserted, null, opened) };
        var plan = PageEditPlan.Identity(1).InsertFrom(First, [0], 1);

        var written = await PageEditApplier.WriteAsync(engine, shown, primary, null, null, new PageApplyRequest(plan, target, files), null, Ct);
        await written.Reopened.DisposeAsync();

        // The spike's finding: without a form in the first file there is none afterwards (the widgets stay annotations).
        await using var merged = await engine.OpenAsync(target, cancellationToken: Ct);
        Assert.Equal(2, merged.PageCount);
        Assert.False(merged.HasAcroForm);
        Assert.Equal(await TextOf(shown, 0), await TextOf(merged, 0));
        Assert.Equal(await TextOf(opened, 0), await TextOf(merged, 1));
        Assert.Empty(await merged.GetFormFieldsAsync(1, Ct));
    }

    [Fact]
    public async Task ASessionTakesOverAFileMergedFromTwoFormsAndTheInputsAreNotHeldOpen()
    {
        using var folder = new TempFolder();
        var primary = Copy(folder, "forms-basic.pdf");
        var inserted = Copy(folder, "forms-basic.pdf");
        var target = Path.Combine(folder.Path, "merged.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(primary);
        PageSourceFile file;
        await using (var opened = await engine.OpenAsync(inserted, cancellationToken: Ct))
        {
            file = PageSourceFile.Of(inserted, null, opened);
        }

        var plan = PageEditPlan.Identity(2).InsertFrom(First, [1], 0).InsertFrom(First, [0], 3);

        var result = await PageEditApplier.ApplyAsync(session, engine, new PageApplyRequest(plan, target, new Dictionary<PageSourceId, PageSourceFile> { [First] = file }), null, Ct);

        Assert.True(result.Adopted);
        Assert.Equal(4, session.Document!.PageCount);
        Assert.Equal(Path.GetFullPath(target), session.Path);

        // The inserted file is not held open once the apply is over (Windows refuses to delete a file with an open handle).
        File.Delete(inserted);
        Assert.False(File.Exists(inserted));
    }

    private static async Task<string> TextOf(PdfDocument document, int page) => (await document.GetTextAsync(page, Ct)).Trim();

    private static async Task<string[]> FieldNames(PdfDocument document, int page) =>
        [.. (await document.GetFormFieldsAsync(page, Ct)).Select(field => field.Key.FieldName)];

    private static string Copy(TempFolder folder, string fixture)
    {
        var path = Path.Combine(folder.Path, Guid.NewGuid().ToString("N") + "-" + fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }
}
