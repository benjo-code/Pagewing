using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

/// <summary>
/// The spike of milestone 13 (Docs/DECISIONS.md, "Seiten organisieren: Spike und QPDF-Entscheidung (M13)"): what PDFium's page
/// operations keep of the outline, the page labels, the form, the metadata, the annotations, the links and the encryption.
/// The assertions are the behaviour found with PDFium 157.0.8100 and guard against a PDFium update that changes it: such a
/// change is a finding to look at (the loss list of the page editor is written from these facts), never an expectation to loosen.
/// Set <c>PAGEWING_SPIKE_OUT</c> to a folder to keep every rewritten file there (for <c>qpdf --check</c>).
/// </summary>
public sealed class PageEditSpikeTests
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    /// <summary>The page labels of pages-marked.pdf as PDFium writes them (whitespace removed): roman for page 1 and 2, decimal from the third page on, starting at 3.</summary>
    private const string MarkedLabels = "/PageLabels<</Nums[0<</S/r>>2<</S/D/St3>>]>>";

    private static readonly string[] MarkedTexts = ["PAGE 1", "PAGE 2", "PAGE 3", "PAGE 4", "PAGE 5", "PAGE 6"];

    [Fact]
    public async Task DeletingPagesKeepsOutlineLabelsAndFormOfTheOthers()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var cancellation = TestContext.Current.CancellationToken;
        var marked = TestData.Pdf("pages-marked.pdf");

        var original = await thread.InvokeAsync(() => Spike.Read(thread, marked, null), cancellationToken: cancellation);
        Assert.Equal(MarkedTexts, original.Texts);
        Assert.Equal(["Kapitel 1->0", ">Abschnitt 1.1->1", "Kapitel 2->2", "Kapitel 3->4"], original.Outline);
        Assert.Contains(MarkedLabels, original.Raw, StringComparison.Ordinal);

        // Delete page 6 and page 2 (Abschnitt 1.1 points to it).
        var deleted = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, marked, null);
                PdfiumPageEdit.Delete(document, 5);
                PdfiumPageEdit.Delete(document, 1);
                return Spike.Rewrite(thread, document, folder, "deleted");
            },
            cancellationToken: cancellation);

        Assert.Equal(4, deleted.PageCount);
        Assert.Equal(["PAGE 1", "PAGE 3", "PAGE 4", "PAGE 5"], deleted.Texts);
        Assert.Equal([0, 0, 0, 90], deleted.Rotations);
        Assert.Equal(["300x400", "400x300", "300x400", "400x300"], deleted.Sizes);

        // The outline of the primary file stays, the bookmarks of the others follow their pages, and a bookmark of a deleted page
        // stays as an entry without a target (-1): it is neither removed nor does it point to another page.
        Assert.Equal(["Kapitel 1->0", ">Abschnitt 1.1->-1", "Kapitel 2->1", "Kapitel 3->3"], deleted.Outline);

        // The page labels are a range list by position: they stay as written, they do not follow the pages.
        Assert.Contains(MarkedLabels, deleted.Raw, StringComparison.Ordinal);
        Assert.Equal(original.Title, deleted.Title);
        Assert.Equal(original.Producer, deleted.Producer);

        // Move page 4 to the front, then set absolute rotations on two pages.
        var moved = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, marked, null);
                PdfiumPageEdit.Move(document, [3], 0);
                PdfiumPageEdit.SetRotation(document, 0, 90);
                PdfiumPageEdit.SetRotation(document, 5, 270);
                return Spike.Rewrite(thread, document, folder, "moved");
            },
            cancellationToken: cancellation);

        Assert.Equal(["PAGE 4", "PAGE 1", "PAGE 2", "PAGE 3", "PAGE 5", "PAGE 6"], moved.Texts);
        Assert.Equal([90, 0, 0, 0, 90, 270], moved.Rotations);
        Assert.Equal(["Kapitel 1->1", ">Abschnitt 1.1->2", "Kapitel 2->3", "Kapitel 3->4"], moved.Outline);
        Assert.Contains(MarkedLabels, moved.Raw, StringComparison.Ordinal);
        Assert.Equal(original.Title, moved.Title);
        Assert.Equal(original.Producer, moved.Producer);

        // The links of the primary file follow their target pages (text-rich.pdf: page one links to page two; swap them).
        var links = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("text-rich.pdf"), null);
                PdfiumPageEdit.Move(document, [1], 0);
                return Spike.Rewrite(thread, document, folder, "links-moved");
            },
            cancellationToken: cancellation);
        Assert.Contains("p1:Page->0", links.Links);

        // The form of the primary file: delete page 2 (Telefon, Ort, one widget of Name and one of Zahlung live there).
        var forms = TestData.Pdf("forms-basic.pdf");
        var formOriginal = await thread.InvokeAsync(() => Spike.Read(thread, forms, null), cancellationToken: cancellation);
        var formDeleted = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, forms, null);
                PdfiumPageEdit.Delete(document, 1);
                return Spike.Rewrite(thread, document, folder, "forms-deleted");
            },
            cancellationToken: cancellation);

        Assert.Equal(1, formDeleted.PageCount);
        Assert.Contains("/AcroForm", formDeleted.Markers);
        Assert.Equal(formOriginal.Fields.Where(f => f.StartsWith("p0:", StringComparison.Ordinal)), formDeleted.Fields);
        Assert.Equal(formOriginal.Title, formDeleted.Title);

        // Fields that lived only on the deleted page stay in /Fields without a widget on any page, with their values:
        // their names are still in the file, although no page shows them.
        Assert.Contains("/T(Telefon)", formDeleted.Raw, StringComparison.Ordinal);
        Assert.Contains("/T(Ort)", formDeleted.Raw, StringComparison.Ordinal);
        Assert.Equal(FieldsEntries(formOriginal), FieldsEntries(formDeleted));

        // Swapping the pages of the form keeps the fields with their pages.
        var formMoved = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, forms, null);
                PdfiumPageEdit.Move(document, [1], 0);
                return Spike.Rewrite(thread, document, folder, "forms-moved");
            },
            cancellationToken: cancellation);
        Assert.Contains("/AcroForm", formMoved.Markers);
        Assert.Equal(
            formOriginal.Fields.Select(f => f.StartsWith("p0:", StringComparison.Ordinal) ? "p1:" + f[3..] : "p0:" + f[3..]).Order(StringComparer.Ordinal),
            formMoved.Fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ImportKeepsAnnotationsLinksAndWidgets()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var cancellation = TestContext.Current.CancellationToken;

        var richSource = await thread.InvokeAsync(() => Spike.Read(thread, TestData.Pdf("text-rich.pdf"), null), cancellationToken: cancellation);
        var foreignSource = await thread.InvokeAsync(() => Spike.Read(thread, TestData.Pdf("annotations-foreign.pdf"), null), cancellationToken: cancellation);
        var formsSource = await thread.InvokeAsync(() => Spike.Read(thread, TestData.Pdf("forms-basic.pdf"), null), cancellationToken: cancellation);

        // pages-marked.pdf has no form. Append text-rich (both pages), then its page one again, annotations-foreign (both pages), forms-basic (both).
        var imported = await thread.InvokeAsync(
            () =>
            {
                using var target = PdfiumDocument.Open(thread, TestData.Pdf("pages-marked.pdf"), null);
                using var rich = PdfiumDocument.Open(thread, TestData.Pdf("text-rich.pdf"), null);
                using var foreign = PdfiumDocument.Open(thread, TestData.Pdf("annotations-foreign.pdf"), null);
                using var forms = PdfiumDocument.Open(thread, TestData.Pdf("forms-basic.pdf"), null);
                PdfiumPageEdit.Import(target, rich, [0, 1], 6);
                PdfiumPageEdit.Import(target, rich, [0], 8);
                PdfiumPageEdit.Import(target, foreign, [0, 1], 9);
                PdfiumPageEdit.Import(target, forms, [0, 1], 11);
                return Spike.Rewrite(thread, target, folder, "import-all");
            },
            cancellationToken: cancellation);

        Assert.Equal(13, imported.PageCount);
        Assert.Equal(MarkedTexts, imported.Texts.Take(6));
        Assert.Equal(richSource.Texts[0], imported.Texts[6]);
        Assert.Equal(richSource.Texts[1], imported.Texts[7]);
        Assert.Equal(richSource.Texts[0], imported.Texts[8]);

        // The outline and the labels of the primary file are untouched; the outline of the inserted files is not taken over.
        Assert.Equal(["Kapitel 1->0", ">Abschnitt 1.1->1", "Kapitel 2->2", "Kapitel 3->4"], imported.Outline);
        Assert.Contains(MarkedLabels, imported.Raw, StringComparison.Ordinal);
        Assert.Equal("PDFium", imported.Producer);

        // Annotations come along completely: the same subtypes in the same order (highlight with popup and reply, ink, free text, note, link).
        Assert.Equal(foreignSource.Annotations.Select(Unpage), imported.Annotations.Where(a => IsOnPage(a, 9, 10)).Select(Unpage));
        Assert.Contains("/IRT", imported.Raw, StringComparison.Ordinal);
        Assert.Contains("/Popup", imported.Raw, StringComparison.Ordinal);

        // URI links come along; the destination of a GoTo link is lost (a GoTo action without /D), also when its target page is imported too.
        Assert.Contains(richSource.Links, link => link.StartsWith("p0:Page->1", StringComparison.Ordinal));
        Assert.Equal(
            richSource.Links.Where(l => l.Contains(":Uri->", StringComparison.Ordinal)).Select(l => "p6" + l[2..]),
            imported.Links.Where(l => l.StartsWith("p6:Uri", StringComparison.Ordinal)));
        Assert.DoesNotContain(imported.Links, l => l.Contains(":Page->", StringComparison.Ordinal));
        Assert.Contains("p6:Unsupported->-1", imported.Links);
        Assert.Contains("p8:Unsupported->-1", imported.Links);

        // The widgets come along as annotations, but the primary file has no form: there is no /AcroForm, so PDFium finds no fields.
        Assert.Equal(formsSource.Annotations.Select(Unpage), imported.Annotations.Where(a => IsOnPage(a, 11, 12)).Select(Unpage));
        Assert.DoesNotContain("/AcroForm", imported.Markers);
        Assert.Empty(imported.Fields);
    }

    [Fact]
    public async Task ImportTakesNoOutlineOrLabelsOfTheSource()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var cancellation = TestContext.Current.CancellationToken;

        // A source with an outline and page labels into a target without both.
        var intoPlain = await thread.InvokeAsync(
            () =>
            {
                using var target = PdfiumDocument.Open(thread, TestData.Pdf("text-rich.pdf"), null);
                using var source = PdfiumDocument.Open(thread, TestData.Pdf("pages-marked.pdf"), null);
                PdfiumPageEdit.Import(target, source, [0, 1, 2, 3, 4, 5], 2);
                return Spike.Rewrite(thread, target, folder, "outline-into-plain");
            },
            cancellationToken: cancellation);

        Assert.Equal(8, intoPlain.PageCount);
        Assert.Empty(intoPlain.Outline);
        Assert.DoesNotContain("/Outlines", intoPlain.Markers);
        Assert.DoesNotContain("/PageLabels", intoPlain.Markers);

        // A source with another outline into a target with one: the outline of the target stays as it is.
        var intoOutlined = await thread.InvokeAsync(
            () =>
            {
                using var target = PdfiumDocument.Open(thread, TestData.Pdf("pages-marked.pdf"), null);
                using var source = PdfiumDocument.Open(thread, TestData.Pdf("outline.pdf"), null);
                PdfiumPageEdit.Import(target, source, [0, 1, 2], 0);
                return Spike.Rewrite(thread, target, folder, "outline-into-outlined");
            },
            cancellationToken: cancellation);

        Assert.Equal(9, intoOutlined.PageCount);
        Assert.Equal(["Kapitel 1->3", ">Abschnitt 1.1->4", "Kapitel 2->5", "Kapitel 3->7"], intoOutlined.Outline);
        Assert.Contains(MarkedLabels, intoOutlined.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromTheSameDocumentDuplicatesAPage()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();

        var facts = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("pages-marked.pdf"), null);

                // Page 3 (landscape) after itself, then page 5 (turned by /Rotate 90) and page 1 at the end of the seven pages.
                PdfiumPageEdit.Import(document, document, [2], 3);
                PdfiumPageEdit.Import(document, document, [5, 0], 7);
                return Spike.Rewrite(thread, document, folder, "duplicate");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(9, facts.PageCount);
        Assert.Equal(["PAGE 1", "PAGE 2", "PAGE 3", "PAGE 3", "PAGE 4", "PAGE 5", "PAGE 6", "PAGE 5", "PAGE 1"], facts.Texts);
        Assert.Equal(["300x400", "300x400", "400x300", "400x300", "300x400", "400x300", "300x400", "400x300", "300x400"], facts.Sizes);
        Assert.Equal([0, 0, 0, 0, 0, 90, 0, 90, 0], facts.Rotations);

        // The bookmarks stay with the original pages, not with the copies; the labels stay positional.
        Assert.Equal(["Kapitel 1->0", ">Abschnitt 1.1->1", "Kapitel 2->2", "Kapitel 3->5"], facts.Outline);
        Assert.Contains(MarkedLabels, facts.Raw, StringComparison.Ordinal);

        // An import, also from the document itself, sets the producer of the information dictionary to PDFium; the title stays.
        Assert.Equal("PDFium", facts.Producer);
        Assert.Equal("Pagewing Seiten markiert", facts.Title);
    }

    [Fact]
    public async Task ImportFromTheSameDocumentSharesResources()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var path = TestData.Pdf("image-rgb.pdf");
        var size = new FileInfo(path).Length;

        var facts = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                PdfiumPageEdit.Import(document, document, [0], 1);
                PdfiumPageEdit.Import(document, document, [0], 2);
                return Spike.Rewrite(thread, document, folder, "image-dup");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        // Two more pages with the image of the first: the image bytes (about 30 KB) exist once, not three times.
        Assert.Equal(3, facts.PageCount);
        Assert.True(facts.Length < size + (size / 2), $"{facts.Length} bytes after two duplicates of a {size} byte file.");
    }

    [Fact]
    public async Task ImportOfTwoFormsWithTheSameFieldNamesYieldsOneOrTwoFields()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var cancellation = TestContext.Current.CancellationToken;
        var path = TestData.Pdf("forms-basic.pdf");
        var original = await thread.InvokeAsync(() => Spike.Read(thread, path, null), cancellationToken: cancellation);
        Assert.Equal(17, FieldsEntries(original));

        // Into itself (a duplicate of both pages) and from a second copy of the file (the pages of another file).
        var self = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                PdfiumPageEdit.Import(document, document, [0, 1], 2);
                return Spike.Rewrite(thread, document, folder, "forms-self");
            },
            cancellationToken: cancellation);

        var other = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var second = PdfiumDocument.Open(thread, path, null);
                PdfiumPageEdit.Import(document, second, [0, 1], 2);
                return Spike.Rewrite(thread, document, folder, "forms-other");
            },
            cancellationToken: cancellation);

        foreach (var facts in new[] { self, other })
        {
            Assert.Equal(4, facts.PageCount);
            Assert.Contains("/AcroForm", facts.Markers);

            // The widgets of the copies are widget annotations on their pages with the same field names ...
            Assert.Equal(original.Fields.Length * 2, facts.Fields.Length);
            Assert.Equal(original.Fields, facts.Fields.Where(f => f.StartsWith("p0:", StringComparison.Ordinal) || f.StartsWith("p1:", StringComparison.Ordinal)));
            Assert.Equal(
                original.Fields.Select(f => "p" + (int.Parse(f[1..2], CultureInfo.InvariantCulture) + 2).ToString(CultureInfo.InvariantCulture) + f[2..]),
                facts.Fields.Where(f => f.StartsWith("p2:", StringComparison.Ordinal) || f.StartsWith("p3:", StringComparison.Ordinal)));
            Assert.Equal(original.Annotations.Length * 2, facts.Annotations.Length);

            // ... but the copies are not entered in /Fields of the AcroForm: the form still lists the 17 fields of the original (15 on page one, Telefon and Ort on page two).
            Assert.Equal(17, FieldsEntries(facts));
        }

        // PDFium couples fields of the same name when it reads the file: a value written to the copy on page three shows on all four pages.
        var values = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, self.Path, null);
                using (var page = document.LoadPage(2))
                {
                    var name = PdfiumForms.ReadPage(page).First(f => f.Name == "Name");
                    PdfiumForms.ReplaceText(page, name.Index, "Neu");
                }

                var result = new List<string>();
                for (var i = 0; i < 4; i++)
                {
                    using var page = document.LoadPage(i);
                    result.AddRange(PdfiumForms.ReadPage(page).Where(f => f.Name == "Name").Select(f => $"p{i}:{f.Value}"));
                }

                return result;
            },
            cancellationToken: cancellation);
        Assert.Equal(["p0:Neu", "p1:Neu", "p2:Neu", "p3:Neu"], values);
    }

    [Fact]
    public async Task RewriteOfEncryptedKeepsEncryption()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var cancellation = TestContext.Current.CancellationToken;

        foreach (var name in new[] { "encrypted_hello_world_r3.pdf", "encrypted_hello_world_r6.pdf" })
        {
            var path = TestData.Pdf(name);
            var original = await thread.InvokeAsync(() => Spike.Read(thread, path, UserPassword), cancellationToken: cancellation);
            Assert.True(original.Encrypted);

            var result = await thread.InvokeAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(thread, path, UserPassword);
                    PdfiumPageEdit.Import(document, document, [0], 1);
                    PdfiumPageEdit.SetRotation(document, 0, 90);
                    return Spike.Rewrite(thread, document, folder, "enc-" + name, UserPassword);
                },
                cancellationToken: cancellation);

            // The result is encrypted with the same handler revision and permissions, and the same password opens it.
            Assert.True(result.Encrypted);
            Assert.Contains("/Encrypt", result.Markers);
            Assert.Equal(original.Security, result.Security);
            Assert.Equal(2, result.PageCount);
            Assert.Equal(original.Texts[0], result.Texts[0]);
            Assert.Equal(original.Texts[0], result.Texts[1]);

            // Without the password the result does not open (no step decrypted it).
            var exception = await thread.InvokeAsync(
                () => Assert.Throws<PdfiumOpenException>(() => PdfiumDocument.Open(thread, result.Path, null)),
                cancellationToken: cancellation);
            Assert.Equal(PdfiumError.Password, exception.Error);
        }
    }

    [Fact]
    public async Task RewriteOfSignedDocumentSaysWhatItDoes()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();
        var cancellation = TestContext.Current.CancellationToken;

        foreach (var (name, level) in new[] { ("signed-docmdp-none.pdf", 0), ("signed-docmdp-2.pdf", 2) })
        {
            var path = TestData.Pdf(name);
            var original = await File.ReadAllBytesAsync(path, cancellation);
            var facts = await thread.InvokeAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(thread, path, null);
                    Assert.Equal((1, level), document.GetSignatures());
                    PdfiumPageEdit.SetRotation(document, 0, 90);
                    return Spike.Rewrite(thread, document, folder, "signed-" + name);
                },
                cancellationToken: cancellation);

            // The signature structure (field, dictionary, DocMDP) is rewritten and stays, but the file no longer starts with the
            // signed bytes: whatever a real signature covered, it does not cover this file. That is what the warning card says.
            Assert.Equal((1, level), facts.Signatures);
            Assert.Contains("/SigFlags", facts.Markers);
            var written = await File.ReadAllBytesAsync(facts.Path, cancellation);
            Assert.False(written.AsSpan().StartsWith(original), "A rewrite never keeps the original bytes as its beginning.");
        }
    }

    [Fact]
    public async Task InsertBlankAndGetRotationWork()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();

        var (rotations, facts) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("pages-marked.pdf"), null);
                var before = Enumerable.Range(0, 6).Select(i => PdfiumPageEdit.GetRotation(document, i)).ToArray();
                PdfiumPageEdit.InsertBlank(document, 0, 595, 842);
                PdfiumPageEdit.InsertBlank(document, 7, 300, 200);
                return (before, Spike.Rewrite(thread, document, folder, "blank"));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([0, 0, 0, 0, 90, 0], rotations);
        Assert.Equal(8, facts.PageCount);
        Assert.Equal(["", "PAGE 1", "PAGE 2", "PAGE 3", "PAGE 4", "PAGE 5", "PAGE 6", ""], facts.Texts);
        Assert.Equal("595x842", facts.Sizes[0]);
        Assert.Equal("300x200", facts.Sizes[7]);
        Assert.Equal([0, 0, 0, 0, 0, 90, 0, 0], facts.Rotations);
        Assert.Equal(["Kapitel 1->1", ">Abschnitt 1.1->2", "Kapitel 2->3", "Kapitel 3->5"], facts.Outline);
        Assert.Equal("Pagewing test generator", facts.Producer);
    }

    [Fact]
    public async Task MovePagesPlacesThemInTheGivenOrderAtTheDestination()
    {
        var thread = PdfiumThread.Shared;
        using var folder = new TempFolder();

        var facts = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("pages-marked.pdf"), null);

                // The pages 5 and 3 (in this order) end up at the indices 1 and 2 of the result.
                PdfiumPageEdit.Move(document, [4, 2], 1);
                return Spike.Rewrite(thread, document, folder, "move-many");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["PAGE 1", "PAGE 5", "PAGE 3", "PAGE 2", "PAGE 4", "PAGE 6"], facts.Texts);
        Assert.Equal([0, 90, 0, 0, 0, 0], facts.Rotations);
        Assert.Equal(["Kapitel 1->0", ">Abschnitt 1.1->3", "Kapitel 2->2", "Kapitel 3->1"], facts.Outline);
    }

    [Fact]
    public async Task PageEditRejectsWrongArgumentsAndOtherThreads()
    {
        var thread = PdfiumThread.Shared;
        var cancellation = TestContext.Current.CancellationToken;
        var document = await thread.InvokeAsync(() => PdfiumDocument.Open(thread, TestData.Pdf("pages-marked.pdf"), null), cancellationToken: cancellation);
        try
        {
            // Not on the PDFium thread: every operation throws before it touches PDFium.
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumPageEdit.Delete(document, 0));
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumPageEdit.GetRotation(document, 0));
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumPageEdit.SaveRewrite(document, new MemoryStream()));

            await thread.InvokeAsync(
                () =>
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.Delete(document, 6));
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.Delete(document, -1));
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.SetRotation(document, 0, 45));
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.SetRotation(document, 6, 90));
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.Import(document, document, [6], 0));
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.Import(document, document, [0], 7));
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.InsertBlank(document, 7, 100, 100));
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.InsertBlank(document, 0, 0, 100));
                    Assert.Throws<ArgumentOutOfRangeException>(() => PdfiumPageEdit.InsertBlank(document, 0, 100, double.NaN));
                    Assert.Throws<ArgumentException>(() => PdfiumPageEdit.Import(document, document, [], 0));
                    Assert.Throws<ArgumentException>(() => PdfiumPageEdit.Move(document, [], 0));

                    // PDFium refuses duplicates in the move list (FALSE), which becomes an exception.
                    Assert.Throws<InvalidOperationException>(() => PdfiumPageEdit.Move(document, [2, 2], 0));
                    Assert.Equal(6, document.PageCount);
                },
                cancellationToken: cancellation);
        }
        finally
        {
            document.Dispose();
        }
    }

    /// <summary>The number of entries of the first <c>/Fields</c> array of a file (the one of the AcroForm).</summary>
    private static int FieldsEntries(Spike.Facts facts)
    {
        var match = Regex.Match(facts.Text, @"/Fields\s*\[(?<list>[^\]]*)\]");
        Assert.True(match.Success, "No /Fields array in the file.");
        return Regex.Count(match.Groups["list"].Value, @"\d+\s+\d+\s+R");
    }

    private static bool IsOnPage(string annotation, int first, int last)
    {
        var page = int.Parse(annotation[1..annotation.IndexOf(':', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
        return page >= first && page <= last;
    }

    /// <summary>The entry without its page number (<c>p0:20</c> becomes <c>20</c>) for comparing the annotations of copied pages.</summary>
    private static string Unpage(string annotation) => annotation[(annotation.IndexOf(':', StringComparison.Ordinal) + 1)..];
}

/// <summary>Reading what is left of a document after the edits.</summary>
internal static class Spike
{
    private static readonly string[] MarkerKeys =
    [
        "/PageLabels", "/AcroForm", "/Outlines", "/Dests", "/Names", "/Encrypt", "/Perms", "/SigFlags", "/StructTreeRoot",
    ];

    public static Facts Read(PdfiumThread thread, string path, string? password)
    {
        using var document = PdfiumDocument.Open(thread, path, password);
        return Describe(document, path, File.ReadAllBytes(path));
    }

    /// <summary>
    /// Saves the document completely, writes the bytes to a file in <paramref name="folder"/> (and to <c>PAGEWING_SPIKE_OUT</c>
    /// if set, for <c>qpdf --check</c>), opens that file again and describes it.
    /// </summary>
    public static Facts Rewrite(PdfiumThread thread, PdfiumDocument document, TempFolder folder, string name, string? password = null)
    {
        using var stream = new MemoryStream();
        PdfiumPageEdit.SaveRewrite(document, stream);
        var bytes = stream.ToArray();
        var fileName = name.Replace('.', '-') + ".pdf";
        var path = System.IO.Path.Combine(folder.Path, fileName);
        File.WriteAllBytes(path, bytes);
        var output = Environment.GetEnvironmentVariable("PAGEWING_SPIKE_OUT");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            File.WriteAllBytes(System.IO.Path.Combine(output, fileName), bytes);
        }

        using var reopened = PdfiumDocument.Open(thread, path, password);
        return Describe(reopened, path, bytes);
    }

    private static Facts Describe(PdfiumDocument document, string path, byte[] bytes)
    {
        var count = document.PageCount;
        var texts = new string[count];
        var rotations = new int[count];
        var sizes = new string[count];
        var fields = new List<string>();
        var links = new List<string>();
        var annotations = new List<string>();
        for (var i = 0; i < count; i++)
        {
            rotations[i] = PdfiumPageEdit.GetRotation(document, i);
            using var page = document.LoadPage(i);
            sizes[i] = string.Create(CultureInfo.InvariantCulture, $"{page.Width:0.#}x{page.Height:0.#}");
            using var text = page.LoadTextPage();
            texts[i] = text.GetText().Replace("\r\n", " ", StringComparison.Ordinal).Trim();
            if (texts[i].Length > 24)
            {
                texts[i] = texts[i][..24];
            }

            foreach (var link in PdfiumPageText.ReadLinks(document, page, text))
            {
                links.Add(string.Create(CultureInfo.InvariantCulture, $"p{i}:{link.Kind}->{link.PageIndex}{(link.Uri is null ? string.Empty : " " + link.Uri)}"));
            }

            var entries = PdfiumAnnotations.CountEntries(page);
            for (var a = 0; a < entries; a++)
            {
                annotations.Add(string.Create(CultureInfo.InvariantCulture, $"p{i}:{PdfiumAnnotations.SubtypeOf(page, a)}"));
            }

            if (document.FormEnvironment is not null)
            {
                foreach (var field in PdfiumForms.ReadPage(page))
                {
                    fields.Add(string.Create(CultureInfo.InvariantCulture, $"p{i}:{field.Name}={field.Value}{(field.Checked ? "[x]" : string.Empty)}"));
                }
            }
        }

        var outline = new List<string>();
        Flatten(document.ReadOutline(1000, 10), 0, outline);
        var native = document.Access();
        var security = string.Create(CultureInfo.InvariantCulture, $"rev={Fpdf.FPDF_GetSecurityHandlerRevision(native)} permissions=0x{Fpdf.FPDF_GetDocPermissions(native).Value:X}");

        // The text of the file (PDFium writes its objects uncompressed; streams are not searched), as is and without whitespace.
        var text2 = Encoding.Latin1.GetString(bytes);
        var raw = string.Concat(text2.Where(c => !char.IsWhiteSpace(c)));
        var markers = MarkerKeys.Where(key => raw.Contains(key, StringComparison.Ordinal)).ToArray();
        return new Facts(
            count,
            texts,
            rotations,
            sizes,
            [.. outline],
            document.GetMetaText("Title"),
            document.GetMetaText("Producer"),
            document.IsEncrypted,
            bytes.LongLength,
            markers,
            raw,
            text2,
            [.. fields],
            [.. links],
            [.. annotations],
            security,
            document.GetSignatures(),
            path);
    }

    private static void Flatten(IReadOnlyList<PdfiumOutlineNode> nodes, int depth, List<string> into)
    {
        foreach (var node in nodes)
        {
            into.Add(string.Create(CultureInfo.InvariantCulture, $"{new string('>', depth)}{node.Title}->{node.PageIndex}"));
            Flatten(node.Children, depth + 1, into);
        }
    }

    /// <summary>What a document looks like after reading it back through PDFium and scanning its bytes.</summary>
    /// <param name="PageCount">The number of pages.</param>
    /// <param name="Texts">The first 24 characters of the text of each page.</param>
    /// <param name="Rotations">The absolute rotation of each page in degrees.</param>
    /// <param name="Sizes">The displayed size of each page in points.</param>
    /// <param name="Outline">The bookmarks depth first, <c>&gt;</c> marks the level, <c>-&gt;</c> the target page or -1.</param>
    /// <param name="Title">The Title of the information dictionary.</param>
    /// <param name="Producer">The Producer of the information dictionary.</param>
    /// <param name="Encrypted">Whether PDFium reports a security handler.</param>
    /// <param name="Length">The file length in bytes.</param>
    /// <param name="Markers">Which of the catalog keys occur in the bytes.</param>
    /// <param name="Raw">The bytes as text without whitespace.</param>
    /// <param name="Text">The bytes as text.</param>
    /// <param name="Fields">Page and name and value of every widget PDFium reads as a field.</param>
    /// <param name="Links">Page, kind and target of every link PDFium reads.</param>
    /// <param name="Annotations">Page and subtype of every entry of <c>/Annots</c>.</param>
    /// <param name="Security">Handler revision and permission word.</param>
    /// <param name="Signatures">Number of signatures and the lowest DocMDP level.</param>
    /// <param name="Path">The file that was read.</param>
    internal sealed record Facts(
        int PageCount,
        string[] Texts,
        int[] Rotations,
        string[] Sizes,
        string[] Outline,
        string? Title,
        string? Producer,
        bool Encrypted,
        long Length,
        string[] Markers,
        string Raw,
        string Text,
        string[] Fields,
        string[] Links,
        string[] Annotations,
        string Security,
        (int Count, int DocMdpLevel) Signatures,
        string Path);
}
