using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

public sealed partial class MarkupRoundTripTests
{
    private const double Scale = 4;

    [Fact]
    public async Task HighlightSurvivesSaveAndReopen()
    {
        using var folder = new TempFolder();
        PdfAnnotation created;
        string saved;
        await using (var document = await OpenFixtureAsync("text-basic.pdf"))
        {
            var rects = await WordRectsAsync(document, 0, "Hello");
            created = await document.CreateMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, "A note", "  Ada  ", TestContext.Current.CancellationToken);
            Assert.Equal(AnnotationKind.Highlight, created.Kind);
            Assert.NotNull(created.Name);
            Assert.Matches("^[0-9a-f]{32}$", created.Name);
            Assert.Equal("Ada", created.Author);
            Assert.Equal(Yellow, created.Color);
            Assert.True(created.Editable);
            AssertRectNear(rects[0], Assert.Single(created.QuadRects), 0.5);

            var listed = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(created.Index, Assert.Single(listed).Index);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        Assert.Equal(created.Name, annotation.Name);
        Assert.Equal(AnnotationKind.Highlight, annotation.Kind);
        Assert.Equal("Highlight", annotation.Subtype);
        Assert.Equal(Yellow, annotation.Color);
        Assert.Equal("A note", annotation.Contents);
        Assert.Equal("Ada", annotation.Author);
        Assert.NotNull(annotation.Modified);
        Assert.True(annotation.Editable);
        AssertQuadsEqual(created.Quads, annotation.Quads);
        AssertRectNear(created.QuadRects[0], annotation.QuadRects[0], 0.1);
    }

    [Fact]
    public async Task CreatedMarkupKeepsAGivenName()
    {
        using var folder = new TempFolder();
        string saved;
        await using (var document = await OpenFixtureAsync("text-basic.pdf"))
        {
            var rects = await WordRectsAsync(document, 0, "Hello");
            var created = await document.CreateMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null, "restored-0001", TestContext.Current.CancellationToken);
            Assert.Equal("restored-0001", created.Name);
            await Assert.ThrowsAsync<ArgumentException>(
                () => document.CreateMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null, "  ", TestContext.Current.CancellationToken));
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        Assert.Equal("restored-0001", Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Name);
    }

    [Fact]
    public async Task UnderlineAndStrikeOutSurvive()
    {
        using var folder = new TempFolder();
        string saved;
        await using (var document = await OpenFixtureAsync("text-basic.pdf"))
        {
            var hello = await WordRectsAsync(document, 0, "Hello");
            var pagewing = await WordRectsAsync(document, 0, "Pagewing");
            await document.CreateMarkupAsync(0, AnnotationKind.Underline, hello, Blue, null, null, TestContext.Current.CancellationToken);
            await document.CreateMarkupAsync(0, AnnotationKind.StrikeOut, pagewing, new AnnotationColor(230, 40, 120), "gone", null, TestContext.Current.CancellationToken);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotations = await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal([AnnotationKind.Underline, AnnotationKind.StrikeOut], annotations.Select(a => a.Kind));
        Assert.Equal(["Underline", "StrikeOut"], annotations.Select(a => a.Subtype));
        Assert.Equal(Blue, annotations[0].Color);
        Assert.Equal(new AnnotationColor(230, 40, 120), annotations[1].Color);
        Assert.Null(annotations[0].Contents);
        Assert.Null(annotations[0].Author);
        Assert.Equal("gone", annotations[1].Contents);
        Assert.All(annotations, a => Assert.True(a.Editable));
    }

    [Fact]
    public Task RotatedPageKeepsTheQuadsOnTheText() => KeepsTheQuadsOnTheText("rotated-page.pdf", "Rotated");

    [Fact]
    public Task CropBoxOffsetKeepsTheQuadsOnTheText() => KeepsTheQuadsOnTheText("cropbox.pdf", "CropBox");

    /// <summary>The reopened markup covers the same character rectangle as the selection it was made from (0.5 pt).</summary>
    private static async Task KeepsTheQuadsOnTheText(string file, string word)
    {
        using var folder = new TempFolder();
        PageRect selection;
        string saved;
        await using (var document = await OpenFixtureAsync(file))
        {
            var rects = await WordRectsAsync(document, 0, word);
            selection = Assert.Single(rects);
            await document.CreateMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, null, TestContext.Current.CancellationToken);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        AssertRectNear(selection, Assert.Single(annotation.QuadRects), 0.5);
        AssertRectNear(selection, annotation.Bounds, 0.5);
    }

    [Fact]
    public async Task UnicodeAndLongCommentsSurvive()
    {
        var unicode = "Grüße, 東京 😀\nzweite Zeile\r\nund \"Zitat\" (Klammer) \\ Ende";
        var builder = new StringBuilder();
        while (builder.Length < 32 * 1024)
        {
            builder.Append("Lorem ipsum äöü 😀 ");
        }

        var longComment = builder.ToString(0, 32 * 1024);
        using var folder = new TempFolder();
        string saved;
        await using (var document = await OpenFixtureAsync("text-basic.pdf"))
        {
            var hello = await WordRectsAsync(document, 0, "Hello");
            var pagewing = await WordRectsAsync(document, 0, "Pagewing");
            await document.CreateMarkupAsync(0, AnnotationKind.Highlight, hello, Yellow, unicode, "Zoë 😀", TestContext.Current.CancellationToken);
            await document.CreateMarkupAsync(0, AnnotationKind.Highlight, pagewing, Yellow, longComment, null, TestContext.Current.CancellationToken);
            saved = await SaveAsync(document, folder);
        }

        await using var reopened = await OpenAsync(saved);
        var annotations = await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal(unicode, annotations[0].Contents);
        Assert.Equal("Zoë 😀", annotations[0].Author);
        Assert.Equal(longComment, annotations[1].Contents);
    }

    [Fact]
    public async Task EditingTheCommentRemovesRichText()
    {
        using var folder = new TempFolder();

        // hl-0001 of annotations-foreign.pdf has rich text but also a reply (read-only), so the rule is checked on
        // the same kind of highlight without a reply.
        var withRichText = WritePdf(folder, "rich.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [10 100 50 120] /QuadPoints [10 120 50 120 10 100 50 100] /C [1 1 0] /F 4 /NM (rich-1) "
                + "/Contents (Old text) /RC (<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\"><p>Old <b>rich</b> text</p></body>) >>",
        ]);
        var richOriginal = await File.ReadAllBytesAsync(withRichText, TestContext.Current.CancellationToken);
        string richSaved;
        await using (var document = await OpenAsync(withRichText))
        {
            var edited = await document.UpdateAnnotationAsync(new AnnotationLocator(0, 0, "rich-1"), new AnnotationEdit(Contents: "New text"), TestContext.Current.CancellationToken);
            Assert.Equal("New text", edited.Contents);
            richSaved = await SaveAsync(document, folder, "rich-saved.pdf");
        }

        var appended = Appended(richOriginal, await File.ReadAllBytesAsync(richSaved, TestContext.Current.CancellationToken));
        var dictionary = AnnotationDictionary(appended, "rich-1");
        Assert.DoesNotContain("<b>rich</b>", dictionary, StringComparison.Ordinal);
        Assert.DoesNotContain("Old", dictionary, StringComparison.Ordinal);
        Assert.Contains("New text", dictionary, StringComparison.Ordinal);
        await using (var reopened = await OpenAsync(richSaved))
        {
            Assert.Equal("New text", Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Contents);
        }
    }

    [Fact]
    public async Task DeletingAMarkupRemovesItsPopup()
    {
        using var folder = new TempFolder();

        // annotations-foreign.pdf has its only popup on hl-0001, which has a reply and therefore stays read-only;
        // this file has a highlight with a popup and no reply, next to a plain note that must stay.
        var path = WritePdf(folder, "popup.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [10 100 50 120] /QuadPoints [10 120 50 120 10 100 50 100] /C [1 1 0] /F 4 /NM (with-popup) /Contents (x) /Popup 5 0 R >>",
            "<< /Type /Annot /Subtype /Popup /Rect [60 100 160 160] /Parent 4 0 R /Open false >>",
            "<< /Type /Annot /Subtype /Text /Rect [200 200 218 218] /NM (note) /Contents (stays) >>",
        ]);
        string saved;
        await using (var document = await OpenAsync(path))
        {
            await document.DeleteAnnotationAsync(new AnnotationLocator(0, 0, "with-popup"), TestContext.Current.CancellationToken);
            Assert.Equal(["note"], (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Select(a => a.Name));
            saved = await SaveAsync(document, folder);
        }

        var subtypes = await RawSubtypesAsync(saved, 0);
        Assert.Equal([FpdfConstants.AnnotText], subtypes);
    }

    [Fact]
    public async Task DeleteFollowsPopupOnlyToARealPopup()
    {
        using var folder = new TempFolder();

        // A damaged /Popup that names a note instead of a popup: the note must stay.
        var path = WritePdf(folder, "false-popup.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [10 100 50 120] /QuadPoints [10 120 50 120 10 100 50 100] /C [1 1 0] /F 4 /NM (hl) /Popup 5 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [200 200 218 218] /NM (note) /Contents (stays) >>",
        ]);
        await using var document = await OpenAsync(path);

        await document.DeleteAnnotationAsync(new AnnotationLocator(0, 0, "hl"), TestContext.Current.CancellationToken);

        Assert.Equal(["note"], (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Select(a => a.Name));
    }

    [Fact]
    public async Task UpdatingOnlyTheCommentGivesAnAppearance()
    {
        using var folder = new TempFolder();
        var original = await File.ReadAllBytesAsync(TestData.Pdf("annotations-foreign.pdf"), TestContext.Current.CancellationToken);
        string name;
        string saved;
        await using (var document = await OpenFixtureAsync("annotations-foreign.pdf"))
        {
            // Index 3 is the bare highlight: no /NM and no /AP.
            var updated = await document.UpdateAnnotationAsync(new AnnotationLocator(0, 3, null), new AnnotationEdit(Contents: "Only the comment"), TestContext.Current.CancellationToken);
            name = updated.Name!;
            saved = await SaveAsync(document, folder);
        }

        var appended = Appended(original, await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken));
        var dictionary = AnnotationDictionary(appended, name);
        Assert.Matches(FlagPrintPattern(), dictionary);
        Assert.Matches(FormPattern(), AppearanceObject(appended, dictionary));
    }

    [Fact]
    public async Task UpdateSetsThePrintFlag()
    {
        using var folder = new TempFolder();
        var path = WritePdf(folder, "no-flags.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Underline /Rect [10 100 50 120] /QuadPoints [10 120 50 120 10 100 50 100] /C [0 0 1] /NM (no-flags) >>",
        ]);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        string saved;
        await using (var document = await OpenAsync(path))
        {
            await document.UpdateAnnotationAsync(new AnnotationLocator(0, 0, "no-flags"), new AnnotationEdit(Contents: "x"), TestContext.Current.CancellationToken);
            saved = await SaveAsync(document, folder);
        }

        var dictionary = AnnotationDictionary(Appended(original, await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken)), "no-flags");
        Assert.Matches(FlagPrintPattern(), dictionary);
    }

    [Fact]
    public async Task ColorChangeSurvivesSaveAndReopenWithItsAppearance()
    {
        using var folder = new TempFolder();
        var original = await File.ReadAllBytesAsync(TestData.Pdf("annotations.pdf"), TestContext.Current.CancellationToken);
        string name;
        string saved;
        await using (var document = await OpenFixtureAsync("annotations.pdf"))
        {
            var updated = await document.UpdateAnnotationAsync(new AnnotationLocator(0, 1, null), new AnnotationEdit(Color: Blue), TestContext.Current.CancellationToken);
            name = updated.Name!;
            saved = await SaveAsync(document, folder);
        }

        var appended = Appended(original, await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken));
        var appearance = AppearanceObject(appended, AnnotationDictionary(appended, name));
        Assert.Matches(FormPattern(), appearance);
        Assert.Matches(MultiplyPattern(), appearance);
        await using var reopened = await OpenAsync(saved);
        var highlight = (await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Single(a => a.Name == name);
        Assert.Equal(Blue, highlight.Color);
    }

    [Fact]
    public async Task DeletingByIndexRemovesTheAnnotationWithoutAName()
    {
        await using var document = await OpenFixtureAsync("annotations-foreign.pdf");

        await document.DeleteAnnotationAsync(new AnnotationLocator(0, 3, null), TestContext.Current.CancellationToken);

        var annotations = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(annotations, a => a.Contents == "No name, no appearance");
        Assert.Equal(5, annotations.Count);
    }

    [Fact]
    public async Task AnnotationsWithRepliesOrOfOtherKindsCannotBeChanged()
    {
        await using var document = await OpenFixtureAsync("annotations-foreign.pdf");

        await Assert.ThrowsAsync<InvalidOperationException>(() => document.DeleteAnnotationAsync(new AnnotationLocator(0, 0, "hl-0001"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.UpdateAnnotationAsync(new AnnotationLocator(0, 0, "hl-0001"), new AnnotationEdit("x"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.DeleteAnnotationAsync(new AnnotationLocator(0, 4, "ink-0001"), TestContext.Current.CancellationToken));
        Assert.Equal(6, (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task MissingAnnotationThrows()
    {
        await using var document = await OpenFixtureAsync("annotations-foreign.pdf");

        await Assert.ThrowsAsync<PdfAnnotationNotFoundException>(() => document.DeleteAnnotationAsync(new AnnotationLocator(0, 0, "no-such-name"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PdfAnnotationNotFoundException>(() => document.UpdateAnnotationAsync(new AnnotationLocator(1, 99, null), new AnnotationEdit("x"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAssignsANameWhenItIsMissing()
    {
        await using var document = await OpenFixtureAsync("annotations-foreign.pdf");

        var updated = await document.UpdateAnnotationAsync(new AnnotationLocator(0, 3, null), new AnnotationEdit(Contents: "Named now"), TestContext.Current.CancellationToken);

        Assert.Matches("^[0-9a-f]{32}$", updated.Name);
        Assert.Equal(3, updated.Index);
        Assert.Equal("Named now", updated.Contents);
        var again = (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Single(a => a.Index == 3);
        Assert.Equal(updated.Name, again.Name);
    }

    [Fact]
    public async Task ChangingTheColorRegeneratesTheAppearance()
    {
        // The highlight of annotations.pdf has a foreign appearance (yellow, multiply) and no name.
        await using var document = await OpenFixtureAsync("annotations.pdf");
        var highlight = (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken))[1];
        // Right of the words "Highlighted text", still inside the highlight (28 60 190 86 on the displayed page).
        var area = new PageRect(183, 62, 189, 84);
        var before = MeanColor(await document.RenderPageAsync(0, Scale, cancellationToken: TestContext.Current.CancellationToken), Scale, area);

        var updated = await document.UpdateAnnotationAsync(new AnnotationLocator(0, highlight.Index, null), new AnnotationEdit(Color: Blue), TestContext.Current.CancellationToken);

        var after = MeanColor(await document.RenderPageAsync(0, Scale, cancellationToken: TestContext.Current.CancellationToken), Scale, area);
        Assert.Equal(Blue, updated.Color);
        Assert.Equal(Blue, (await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken))[1].Color);
        Assert.True(before.B < 60 && before.R > 200, $"Before: {before}");
        Assert.True(after.B > 200 && after.R < 80, $"After: {after}");

        // An own highlight changes colour the same way.
        var created = await document.CreateMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(200, 20, 280, 40)], Yellow, null, null, TestContext.Current.CancellationToken);
        var own = new PageRect(202, 22, 278, 38);
        var ownBefore = MeanColor(await document.RenderPageAsync(0, Scale, cancellationToken: TestContext.Current.CancellationToken), Scale, own);
        await document.UpdateAnnotationAsync(new AnnotationLocator(0, created.Index, created.Name), new AnnotationEdit(Color: Blue), TestContext.Current.CancellationToken);
        var ownAfter = MeanColor(await document.RenderPageAsync(0, Scale, cancellationToken: TestContext.Current.CancellationToken), Scale, own);
        Assert.True(ownBefore.B < 60, $"Own before: {ownBefore}");
        Assert.True(ownAfter.B > 200 && ownAfter.R < 80, $"Own after: {ownAfter}");
    }

    [Fact]
    public async Task CreatedMarkupIsDrawnByPdfiumWithAnnotationFlag()
    {
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        var rects = await WordRectsAsync(document, 0, "Hello");
        await document.CreateMarkupAsync(0, AnnotationKind.Highlight, rects, Blue, null, null, TestContext.Current.CancellationToken);

        var with = await document.RenderPageAsync(0, Scale, cancellationToken: TestContext.Current.CancellationToken);
        var without = await document.RenderPageAsync(0, Scale, RenderOptions.Default with { Annotations = false }, TestContext.Current.CancellationToken);

        // Multiplied over white paper the highlight shows its colour; the dark glyphs stay dark.
        var shown = MeanColor(with, Scale, rects[0]);
        var plain = MeanColor(without, Scale, rects[0]);
        Assert.True(plain.R > shown.R + 60, $"Highlight not drawn: {shown} vs {plain}");
        Assert.True(shown.B > shown.R + 60, $"Not the highlight colour: {shown}");
        Assert.True(Darkest(with, rects[0]) < 80, "The text under the highlight is no longer readable.");
    }

    [Fact]
    public async Task SavedFileHasPrintFlagAndAppearanceObject()
    {
        using var folder = new TempFolder();
        var original = await File.ReadAllBytesAsync(TestData.Pdf("text-basic.pdf"), TestContext.Current.CancellationToken);
        var names = new List<string>();
        string saved;
        await using (var document = await OpenFixtureAsync("text-basic.pdf"))
        {
            var rects = await WordRectsAsync(document, 0, "Hello");
            foreach (var kind in new[] { AnnotationKind.Highlight, AnnotationKind.Underline, AnnotationKind.StrikeOut })
            {
                names.Add((await document.CreateMarkupAsync(0, kind, rects, Yellow, "c", null, TestContext.Current.CancellationToken)).Name!);
            }

            saved = await SaveAsync(document, folder);
        }

        var bytes = await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken);
        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original));
        var appended = Appended(original, bytes);
        foreach (var name in names)
        {
            var dictionary = AnnotationDictionary(appended, name);
            Assert.Matches(FlagPrintPattern(), dictionary);
            var appearance = AppearanceObject(appended, dictionary);
            Assert.Matches(FormPattern(), appearance);
        }
    }

    [Fact]
    public async Task HighlightAppearanceIsIndirectWithMultiply()
    {
        // Guard against PDFium updates: variant D relies on PDFium's own appearance generator (Docs/DECISIONS.md,
        // "Erscheinungsbild der Annotationen: PDFium-Generator (Variante D) …").
        using var folder = new TempFolder();
        var original = await File.ReadAllBytesAsync(TestData.Pdf("annotations.pdf"), TestContext.Current.CancellationToken);
        string name;
        string saved;
        await using (var document = await OpenFixtureAsync("annotations.pdf"))
        {
            var rects = await WordRectsAsync(document, 0, "Highlighted");
            name = (await document.CreateMarkupAsync(0, AnnotationKind.Highlight, rects, Blue, null, null, TestContext.Current.CancellationToken)).Name!;
            saved = await SaveAsync(document, folder);
        }

        var appended = Appended(original, await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken));
        var dictionary = AnnotationDictionary(appended, name);
        var appearance = AppearanceObject(appended, dictionary);
        Assert.Matches(FormPattern(), appearance);
        Assert.Matches(MultiplyPattern(), appearance);
    }

    [Fact]
    public async Task CreateRejectsOtherKindsAndEmptyGeometry()
    {
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        PageRect[] rects = [new PageRect(20, 20, 60, 40)];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.CreateMarkupAsync(0, AnnotationKind.Other, rects, Yellow, null, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateMarkupAsync(0, AnnotationKind.Highlight, [], Yellow, null, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => document.CreateMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(10, 10, 10, 30)], Yellow, null, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.CreateMarkupAsync(5, AnnotationKind.Highlight, rects, Yellow, null, null, TestContext.Current.CancellationToken));
        Assert.Empty(await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthorIsTrimmedAndBlankAuthorIsLeftOut()
    {
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        PageRect[] rects = [new PageRect(20, 20, 60, 40)];

        var blank = await document.CreateMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, null, "   ", TestContext.Current.CancellationToken);
        var trimmed = await document.CreateMarkupAsync(0, AnnotationKind.Highlight, rects, Yellow, "", "\tBen ", TestContext.Current.CancellationToken);

        Assert.Null(blank.Author);
        Assert.Equal("Ben", trimmed.Author);
        Assert.Null(trimmed.Contents);
    }

    [Fact]
    public async Task DatesComeFromTheClock()
    {
        using var folder = new TempFolder();
        var now = new DateTimeOffset(2026, 10, 24, 9, 30, 5, TimeSpan.FromHours(2));
        string saved;
        string name;
        var original = await File.ReadAllBytesAsync(TestData.Pdf("text-basic.pdf"), TestContext.Current.CancellationToken);
        await using (var document = await OpenFixtureAsync("text-basic.pdf"))
        {
            document.Clock = new FixedClock(now);
            var created = await document.CreateMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(20, 20, 60, 40)], Yellow, null, null, TestContext.Current.CancellationToken);
            Assert.Equal(now, created.Modified);
            name = created.Name!;
            saved = await SaveAsync(document, folder);
        }

        var dictionary = AnnotationDictionary(Appended(original, await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken)), name);
        Assert.Contains("/M(D:20261024073005Z)", dictionary, StringComparison.Ordinal);
        Assert.Contains("/CreationDate(D:20261024073005Z)", dictionary, StringComparison.Ordinal);
    }

    private static string Appended(byte[] original, byte[] saved)
    {
        Assert.True(saved.Length > original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "The original bytes were changed.");
        return Encoding.Latin1.GetString(saved, original.Length, saved.Length - original.Length);
    }

    /// <summary>The dictionary of the indirect annotation object whose /NM is <paramref name="name"/>.</summary>
    private static string AnnotationDictionary(string appended, string name)
    {
        var match = ObjectPattern().Matches(appended).LastOrDefault(m => m.Groups["body"].Value.Contains("/NM(" + name + ")", StringComparison.Ordinal));
        Assert.True(match is not null, $"No indirect annotation object with /NM ({name}) in the appended revision.");
        return match.Groups["body"].Value;
    }

    private static string AppearanceObject(string appended, string dictionary)
    {
        var reference = AppearancePattern().Match(dictionary);
        Assert.True(reference.Success, "The annotation has no /AP << /N n 0 R >>: " + dictionary);
        var number = reference.Groups["n"].Value;
        var match = ObjectPattern().Matches(appended).LastOrDefault(m => m.Groups["number"].Value == number);
        Assert.True(match is not null, $"The appearance object {number} is not in the appended revision.");
        var body = match.Groups["body"].Value;
        var stream = body.IndexOf("stream", StringComparison.Ordinal);
        return stream < 0 ? body : body[..stream];
    }

    private static int Darkest(RenderedImage image, PageRect rect)
    {
        var darkest = 255;
        var pixels = image.Pixels.Span;
        for (var y = (int)(rect.Top * Scale); y < (int)(rect.Bottom * Scale); y++)
        {
            for (var x = (int)(rect.Left * Scale); x < (int)(rect.Right * Scale); x++)
            {
                var o = (y * image.Stride) + (x * 4);
                darkest = Math.Min(darkest, Math.Max(pixels[o], Math.Max(pixels[o + 1], pixels[o + 2])));
            }
        }

        return darkest;
    }

    /// <summary>The subtypes of all entries of a page's /Annots, as PDFium reports them (popups included).</summary>
    private static Task<int[]> RawSubtypesAsync(string path, int pageIndex)
    {
        var thread = PdfiumThread.Shared;
        return thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = document.LoadPage(pageIndex);
                var native = page.Access();
                var subtypes = new int[Fpdf.FPDFPage_GetAnnotCount(native)];
                for (var i = 0; i < subtypes.Length; i++)
                {
                    var annotation = Fpdf.FPDFPage_GetAnnot(native, i);
                    subtypes[i] = Fpdf.FPDFAnnot_GetSubtype(annotation);
                    Fpdf.FPDFPage_CloseAnnot(annotation);
                }

                return subtypes;
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [GeneratedRegex(@"(?<!\d)(?<number>\d+)\s+0\s+obj\b(?<body>.*?)\bendobj", RegexOptions.Singleline)]
    private static partial Regex ObjectPattern();

    [GeneratedRegex(@"/AP\s*<<\s*/N\s+(?<n>\d+)\s+0\s+R\s*>>")]
    private static partial Regex AppearancePattern();

    [GeneratedRegex(@"/F\s*4(?![\d.])")]
    private static partial Regex FlagPrintPattern();

    [GeneratedRegex(@"/Subtype\s*/Form")]
    private static partial Regex FormPattern();

    [GeneratedRegex(@"/BM\s*/Multiply")]
    private static partial Regex MultiplyPattern();
}
