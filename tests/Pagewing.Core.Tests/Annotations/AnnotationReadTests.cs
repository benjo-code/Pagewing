using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

public sealed class AnnotationReadTests
{
    [Fact]
    public async Task ReadsHighlightColorQuadsAndContentsOfTheForeignFixture()
    {
        await using var document = await OpenFixtureAsync("annotations.pdf");

        var annotations = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal([0, 1, 2], annotations.Select(a => a.Index));
        Assert.Equal(["Square", "Highlight", "Link"], annotations.Select(a => a.Subtype));
        var highlight = annotations[1];
        Assert.Equal(AnnotationKind.Highlight, highlight.Kind);
        Assert.Equal(0, highlight.Page);
        Assert.Null(highlight.Name);
        Assert.Equal(new AnnotationColor(255, 255, 0), highlight.Color);
        Assert.Equal("Highlight annotation", highlight.Contents);
        AssertQuadsEqual(
            [new Quad(new PagePoint(28, 140), new PagePoint(190, 140), new PagePoint(28, 114), new PagePoint(190, 114))],
            highlight.Quads);

        // The page is 300 x 200 points: y = 140 in PDF space is 60 points from the top.
        AssertRectNear(new PageRect(28, 60, 190, 86), Assert.Single(highlight.QuadRects), 0.1);
        AssertRectNear(new PageRect(28, 60, 190, 86), highlight.Bounds, 0.1);
        Assert.True(highlight.Editable);

        Assert.Equal(AnnotationKind.Other, annotations[0].Kind);
        Assert.Equal(new AnnotationColor(255, 0, 0), annotations[0].Color);
        Assert.Empty(annotations[0].Quads);
        Assert.Equal(AnnotationKind.Other, annotations[2].Kind);
    }

    [Fact]
    public async Task SkipsPopupsAndReplies()
    {
        await using var document = await OpenFixtureAsync("annotations-foreign.pdf");

        var annotations = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);

        // Raw order: highlight, popup, reply, bare highlight, ink, free text, note, link.
        Assert.Equal([0, 3, 4, 5, 6, 7], annotations.Select(a => a.Index));
        Assert.DoesNotContain(annotations, a => a.Subtype == "Popup");
        Assert.DoesNotContain(annotations, a => a.Name == "reply-0001");
        Assert.Equal("note-0001", annotations.Single(a => a.Subtype == "Text").Name);
    }

    [Fact]
    public async Task MarksOnlyPlainMarkupAsEditable()
    {
        await using var document = await OpenFixtureAsync("annotations-foreign.pdf");

        var first = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);
        var second = await document.GetAnnotationsAsync(1, TestContext.Current.CancellationToken);

        var bare = first.Single(a => a.Index == 3);
        Assert.Equal(AnnotationKind.Highlight, bare.Kind);
        Assert.Null(bare.Name);
        Assert.Equal(new AnnotationColor(153, 255, 153), bare.Color);
        Assert.Equal("No name, no appearance", bare.Contents);
        Assert.True(bare.Editable);
        Assert.All(first.Where(a => a.Subtype is "Ink" or "FreeText" or "Text" or "Link"), a =>
        {
            Assert.Equal(AnnotationKind.Other, a.Kind);
            Assert.False(a.Editable);
        });

        Assert.Equal([AnnotationKind.Underline, AnnotationKind.StrikeOut], second.Select(a => a.Kind));
        Assert.All(second, a => Assert.True(a.Editable));
        Assert.Equal(new AnnotationColor(0, 153, 0), second[0].Color);
        Assert.Equal(new AnnotationColor(255, 0, 0), second[1].Color);
        Assert.Equal(["ul-0001", "so-0001"], second.Select(a => a.Name));
        Assert.All(second, a => Assert.Equal(1, a.Page));
    }

    [Fact]
    public async Task ForeignMarkupWithRepliesIsReadOnly()
    {
        await using var document = await OpenFixtureAsync("annotations-foreign.pdf");

        var annotations = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);

        var highlight = annotations.Single(a => a.Name == "hl-0001");
        Assert.Equal(AnnotationKind.Highlight, highlight.Kind);
        Assert.False(highlight.Editable);
        Assert.Equal("Alice", highlight.Author);
        Assert.Equal("Check this sentence", highlight.Contents);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), highlight.Modified);

        // The colour of an annotation with an appearance stream comes from that stream (1 0.9 0 rg).
        Assert.Equal(new AnnotationColor(255, 230, 0), highlight.Color);
    }

    [Fact]
    public async Task HostileAnnotationsAreSkippedAndCapped()
    {
        using var folder = new TempFolder();

        // Coordinates PDFium cannot read as a finite value of a sensible size (PDF syntax has no NaN).
        var huge = "1" + new string('0', 45) + ".5";
        var hostile = WritePdf(folder, "hostile.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 10 0 R] >>",
            "<< /Type /Annot /Subtype /Highlight /QuadPoints [10 20 50 20 10 10 50 10] /C [1 1 0] /NM (no-rect) >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [10 10 50 20] /QuadPoints [10 20 50 20 10 10] /C [1 1 0] /NM (short-quads) >>",
            $"<< /Type /Annot /Subtype /Highlight /Rect [10 10 50 20] /QuadPoints [10 20 {huge} 20 10 10 50 10] /C [1 1 0] /NM (huge-quad) >>",
            $"<< /Type /Annot /Subtype /Square /Rect [10 10 {huge} 20] /C [1 0 0] /NM (huge-rect) >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [10 10 50 20] /C [1 1 0] /NM (no-quads) >>",
            "<< /Type /Annot /Subtype /Highlight /Rect [10 100 50 120] /QuadPoints [10 120 50 120 10 100 50 100] /C [1 1 0] /NM (good) >>",
            "<< /Type /Annot /Subtype /Square /Rect [60 60 90 90] /C [0 0 1] /NM (good-square) >>",
        ]);

        await using (var document = await OpenAsync(hostile))
        {
            var annotations = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);
            Assert.Equal(["good", "good-square"], annotations.Select(a => a.Name));
        }

        // Two pages with 6,000 highlights each: together never more than 10,000, and asking again does not count twice.
        const int perPage = 6_000;
        var one = new StringBuilder();
        for (var i = 0; i < perPage; i++)
        {
            one.Append("<< /Subtype /Highlight /Rect [10 10 50 20] /QuadPoints [10 20 50 20 10 10 50 10] >> ");
        }

        var many = WritePdf(folder, "many.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{one}] >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{one}] >>",
        ]);

        await using (var document = await OpenAsync(many))
        {
            var first = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);
            var second = await document.GetAnnotationsAsync(1, TestContext.Current.CancellationToken);
            var again = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);

            Assert.Equal(perPage, first.Count);
            Assert.Equal(PdfDocument.MaxAnnotations - perPage, second.Count);
            Assert.Equal(perPage, again.Count);
            Assert.Equal(10_000, PdfDocument.MaxAnnotations);
        }
    }

    [Fact]
    public async Task ReadingAPageWithoutAnnotationsGivesAnEmptyList()
    {
        await using var document = await OpenFixtureAsync("text-basic.pdf");

        Assert.Empty(await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetAnnotationsAsync(2, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/GS gs 1 0.9 0 rg 38 246 212 18 re f", true, 255, 230, 0)]
    [InlineData("0 0.6 0 RG 1 w 38 247 m 250 247 l S", false, 0, 153, 0)]
    [InlineData("1 0 0 RG 0.9 0.9 1 rg 3 w 31.5 31.5 97 57 re B", false, 255, 0, 0)]
    [InlineData("1 0 0 RG 0.9 0.9 1 rg 3 w 31.5 31.5 97 57 re B", true, 230, 230, 255)]
    [InlineData("q (a string with 1 0 0 rg inside \\) and \\( parens) Tj 0.5 g Q", true, 128, 128, 128)]
    [InlineData("% 1 0 0 rg comment\n0 0 0 1 k", true, 0, 0, 0)]
    [InlineData("0 1 0 0 K", false, 255, 0, 255)]
    [InlineData("<< /BM /Multiply >> BDC [1 2 3] 0 d .25 .5 .75 rg", true, 64, 128, 191)]
    public void ColorOfAnAppearanceIsItsFirstFillOrStrokeColor(string content, bool preferFill, byte r, byte g, byte b) =>
        Assert.Equal((r, g, b), Pagewing.Pdfium.PdfiumAnnotations.ColorFromAppearance(content, preferFill));

    [Theory]
    [InlineData("")]
    [InlineData("1 0 0 re f")]
    [InlineData("BI /W 1 /H 1 ID ÿ EI 1 0 0 rg")]
    [InlineData("2 0 0 rg")]
    public void AppearanceWithoutAUsableColorGivesNone(string content) =>
        Assert.Null(Pagewing.Pdfium.PdfiumAnnotations.ColorFromAppearance(content, preferFill: true));

    [Fact]
    public void FormatsPdfDatesInUtc() =>
        Assert.Equal("D:20261024073005Z", PdfDocument.FormatPdfDate(new DateTimeOffset(2026, 10, 24, 9, 30, 5, TimeSpan.FromHours(2))));
}
