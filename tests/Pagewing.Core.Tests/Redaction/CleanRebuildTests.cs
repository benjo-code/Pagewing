using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The cleaning rebuild of a redaction (milestone 14, task 7): the pages go into a new document without pages, which leaves the
/// document-level data behind; what is in the file and what the rebuild loses is told beforehand; a file whose pages carry keys
/// PDFium cannot remove, or an encrypted file, is refused with its reason.
/// </summary>
public sealed class CleanRebuildTests
{
    private const string Term = "Zephyrstraße 17";

    private readonly PdfEngine engine = new();

    private RedactionExecutor Executor => new(engine);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<byte[]> RebuildAsync(string path, string? password = null)
    {
        await using var copy = await engine.OpenAsync(path, password, Ct);
        using var output = new MemoryStream();
        await Executor.RebuildAsync(copy, output, Ct);
        return output.ToArray();
    }

    private async Task<PdfDocument> OpenBytesAsync(TempFolder folder, byte[] bytes, string name = "rebuilt.pdf")
    {
        var path = Path.Combine(folder.Path, name);
        await File.WriteAllBytesAsync(path, bytes, Ct);
        return await engine.OpenAsync(path, cancellationToken: Ct);
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static RedactionRefusal? Refusal(byte[] bytes, int pages)
    {
        using var stream = new MemoryStream(bytes);
        return RedactionExecutor.PageKeyRefusal(stream, pages);
    }

    private static async Task<string> TextOf(PdfDocument document, int page) =>
        string.Concat((await document.GetPageTextAsync(page, Ct)).Chars.Select(static c => c.Text));

    [Fact]
    public async Task RebuiltFileHasNoInfoXmpOutlinesAttachmentsOrScripts()
    {
        using var folder = new TempFolder();
        var path = TestData.Pdf("redact-everywhere.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        Assert.True(RawPdf.ContainsAnywhere(original, "Quellmann"));

        var bytes = await RebuildAsync(path);

        // The second term lives only in the information dictionary, the XMP and the attachment: gone from the raw bytes, also unpacked.
        Assert.False(RawPdf.ContainsAnywhere(bytes, "Quellmann"));
        var text = Latin1(bytes);
        foreach (var key in new[] { "/Outlines", "/EmbeddedFile", "/Names", "/Author", "/Producer", "/Metadata", "/AcroForm", "/OpenAction", "/JavaScript", "/PageLabels", "/Info" })
        {
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        }

        // What the rebuild keeps of the term is the content of the pages and their annotations: tasks 4 and 5 remove it there.
        Assert.True(RawPdf.ContainsAnywhere(bytes, Term));

        await using var reopened = await OpenBytesAsync(folder, bytes);
        Assert.Equal(2, reopened.PageCount);
        Assert.False(reopened.HasAcroForm);
        Assert.Empty(await reopened.GetOutlineAsync(Ct));
        var metadata = await reopened.GetMetadataAsync(Ct);
        Assert.Equal(new PdfMetadata(null, null, null, null, null, null, null, null), metadata);
        var inventory = RawContentScanner.Inventory(new MemoryStream(bytes), Ct);
        Assert.Equal(0, inventory.EmbeddedFiles + inventory.OutlineItems + inventory.JavaScriptActions + inventory.MetadataKeys + inventory.PageLabelKeys);
    }

    [Fact]
    public async Task InventoryNamesWhatIsPresent()
    {
        var path = TestData.Pdf("redact-everywhere.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        var (cleanup, _) = await RedactionExecutor.InventoryAsync(document, path, Ct);

        string DetailOf(CleanupKind kind) => cleanup.Single(c => c.Kind == kind).Detail;
        Assert.Equal("Quellmann", DetailOf(CleanupKind.Author));
        Assert.Contains(Term, DetailOf(CleanupKind.Subject), StringComparison.Ordinal);
        Assert.Equal("1", DetailOf(CleanupKind.Bookmarks));
        Assert.Equal("1", DetailOf(CleanupKind.Attachments));
        Assert.Equal("1", DetailOf(CleanupKind.Xmp));
        Assert.DoesNotContain(cleanup, c => c.Kind == CleanupKind.PageLabels);

        // The cleanup list follows the order of the kinds.
        Assert.Equal(cleanup.Select(static c => c.Kind).Order(), cleanup.Select(static c => c.Kind));
    }

    [Fact]
    public async Task InventoryOfTheStructureFileNamesScriptsLabelsAndMetadata()
    {
        var path = TestData.Pdf("redact-structure.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        var inventory = await RedactionExecutor.ReadInventoryAsync(document, path, Ct);

        Assert.True(inventory.Complete);
        var kinds = inventory.Cleanup.Select(static c => c.Kind).ToHashSet();
        Assert.Superset(new HashSet<CleanupKind> { CleanupKind.Title, CleanupKind.Subject, CleanupKind.Keywords, CleanupKind.Producer, CleanupKind.Xmp, CleanupKind.JavaScript, CleanupKind.PageLabels }, kinds);
        Assert.DoesNotContain(CleanupKind.Attachments, kinds);
        Assert.DoesNotContain(CleanupKind.Bookmarks, kinds);
    }

    [Fact]
    public async Task InventoryOfAPlainFileNamesOnlyWhatIsThere()
    {
        var path = TestData.Pdf("text-basic.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        var inventory = await RedactionExecutor.ReadInventoryAsync(document, path, Ct);

        Assert.Equal([CleanupKind.Title, CleanupKind.Producer], inventory.Cleanup.Select(static c => c.Kind));
        Assert.Empty(inventory.Losses);
        Assert.True(inventory.Complete);
    }

    [Fact]
    public async Task InternalLinksAndFormFieldsAreReportedAsLoss()
    {
        var links = TestData.Pdf("text-rich.pdf");
        await using (var document = await engine.OpenAsync(links, cancellationToken: Ct))
        {
            var (_, losses) = await RedactionExecutor.InventoryAsync(document, links, Ct);
            var loss = Assert.Single(losses);
            Assert.Equal(RedactionLossKind.InternalLinksLoseTheirTarget, loss.Kind);
            Assert.True(loss.Count >= 1);
        }

        var form = TestData.Pdf("forms-basic.pdf");
        await using (var document = await engine.OpenAsync(form, cancellationToken: Ct))
        {
            var (_, losses) = await RedactionExecutor.InventoryAsync(document, form, Ct);
            var loss = Assert.Single(losses, static l => l.Kind == RedactionLossKind.FormFieldsNotFillable);
            Assert.Equal((await document.GetFormInfoAsync(Ct)).FieldCount, loss.Count);
            Assert.True(loss.Count > 0);
        }
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task EncryptedIsRefusedWithReason(string name)
    {
        await using var document = await engine.OpenAsync(TestData.Pdf(name), "hôtel", Ct);

        var refusal = await RedactionExecutor.FileRefusalAsync(document, Ct);

        Assert.NotNull(refusal);
        Assert.Equal(RedactionRefusalKind.Encrypted, refusal.Kind);
        Assert.Contains("encrypted", refusal.Reason, StringComparison.Ordinal);
        Assert.Empty(refusal.Pages);

        // Never decrypted silently: the rebuild refuses to write it at all.
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor.RebuildAsync(document, output, Ct));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task AFileThatIsNotEncryptedHasNoFileRefusal()
    {
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: Ct);
        Assert.Null(await RedactionExecutor.FileRefusalAsync(document, Ct));
    }

    [Theory]
    [InlineData("signed-docmdp-none.pdf")]
    [InlineData("signed-docmdp-1.pdf")]
    [InlineData("signed-docmdp-2.pdf")]
    [InlineData("signed-docmdp-3.pdf")]
    public async Task SignedDocumentReportsSignatureInvalidated(string name)
    {
        var path = TestData.Pdf(name);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        var (_, losses) = await RedactionExecutor.InventoryAsync(document, path, Ct);

        var loss = Assert.Single(losses, static l => l.Kind == RedactionLossKind.SignatureInvalidated);
        Assert.Equal(1, loss.Count);

        // The rebuild has no form and no permissions entry, so nothing in it is a signature field any more. What stays is the widget
        // annotation of the signature field with its value dictionary (the DocMDP reference in it too), as an annotation of the page like any other (it comes with the page;
        // PDFium cannot leave it behind). It is inert without the form, but the bytes of the old signature are still in the file.
        var bytes = await RebuildAsync(path);
        var text = Latin1(bytes);
        Assert.DoesNotContain("/AcroForm", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Perms", text, StringComparison.Ordinal);
        Assert.Contains("/ByteRange", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LossesFollowTheOrderOfTheirKinds()
    {
        var path = TestData.Pdf("forms-signed.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        var (_, losses) = await RedactionExecutor.InventoryAsync(document, path, Ct);

        Assert.Equal(losses.Select(static l => l.Kind).Order(), losses.Select(static l => l.Kind));
        Assert.Contains(losses, static l => l.Kind == RedactionLossKind.SignatureInvalidated);
        Assert.Contains(losses, static l => l.Kind == RedactionLossKind.FormFieldsNotFillable);
    }

    [Theory]
    [InlineData("rotated-page.pdf")]
    [InlineData("cropbox.pdf")]
    [InlineData("print-mixed.pdf")]
    [InlineData("redact-rotated.pdf")]
    [InlineData("text-basic.pdf")]
    public async Task PagesKeepSizeAndRotation(string name)
    {
        using var folder = new TempFolder();
        var path = TestData.Pdf(name);
        var bytes = await RebuildAsync(path);

        await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
        await using var rebuilt = await OpenBytesAsync(folder, bytes);

        Assert.Equal(original.PageCount, rebuilt.PageCount);
        for (var page = 0; page < original.PageCount; page++)
        {
            Assert.Equal(await original.GetPageSizeAsync(page, Ct), await rebuilt.GetPageSizeAsync(page, Ct));
            Assert.Equal(await original.GetPageRotationAsync(page, Ct), await rebuilt.GetPageRotationAsync(page, Ct));
            Assert.Equal(await TextOf(original, page), await TextOf(rebuilt, page));
        }
    }

    [Fact]
    public async Task WidgetPagesDoNotCrashTheWrite()
    {
        // The pages of a form point into their source until the new document is closed (M13, PageEditFormImportTests). The sequence
        // that crashed PDFium: close the source for the caller, then write. The new document keeps the source open.
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "forms-basic.pdf");
        File.Copy(TestData.Pdf("forms-basic.pdf"), path);

        var copy = await engine.OpenAsync(path, cancellationToken: Ct);
        var pages = copy.PageCount;
        await using var rebuilt = await engine.CreateEmptyAsync(Ct);
        await rebuilt.ImportPagesAsync(copy, [.. Enumerable.Range(0, pages)], 0, Ct);
        await copy.DisposeAsync();
        Assert.True(copy.IsNativeOpen);

        using var output = new MemoryStream();
        await rebuilt.SaveRewriteAsync(output, Ct);
        Assert.True(output.Length > 0);

        // And the executor's own way, for every form fixture.
        foreach (var name in new[] { "forms-basic.pdf", "forms-rotated.pdf", "forms-signed.pdf", "forms-calc.pdf", "forms-xfa.pdf" })
        {
            var bytes = await RebuildAsync(TestData.Pdf(name));
            await using var reopened = await OpenBytesAsync(folder, bytes, "again-" + name);
            Assert.True(reopened.PageCount > 0, name);
            Assert.False(reopened.HasAcroForm, name);
        }
    }

    [Fact]
    public async Task AnnotationsOutsideTheAreaStayInTheRebuiltFile()
    {
        using var folder = new TempFolder();
        foreach (var name in new[] { "annotations.pdf", "annotations-foreign.pdf", "redact-everywhere.pdf" })
        {
            var path = TestData.Pdf(name);
            var bytes = await RebuildAsync(path);
            await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
            await using var rebuilt = await OpenBytesAsync(folder, bytes, "annotations-" + name);

            Assert.Equal(original.PageCount, rebuilt.PageCount);
            for (var page = 0; page < original.PageCount; page++)
            {
                var before = await original.GetAnnotationsAsync(page, Ct);
                var after = await rebuilt.GetAnnotationsAsync(page, Ct);
                Assert.Equal(before.Select(static a => a.Kind), after.Select(static a => a.Kind));
                Assert.Equal(before.Select(static a => a.Contents), after.Select(static a => a.Contents));
            }
        }
    }

    [Theory]
    [InlineData("/Thumb @ 0 R", "/Thumb")]
    [InlineData("/PieceInfo << /Pagewing << /Private (secret) >> >>", "/PieceInfo")]
    [InlineData("/Metadata @ 0 R", "/Metadata")]
    [InlineData("/AA << /O << /S /JavaScript /JS (app.alert(1);) >> >>", "/AA")]
    public async Task FileWithThumbPieceInfoPageMetadataOrAaIsRefusedWithPageNumbers(string entry, string key)
    {
        // One small file per key, on the second of two pages: the rebuild keeps the key (PDFium cannot remove it), the scan finds it.
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (one) Tj ET", "BT /F1 12 Tf 20 150 Td (two) Tj ET");
        var stream = file.AddStream(key == "/Metadata" ? "/Type /Metadata /Subtype /XML" : "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0x66]);
        file.PageEntriesByPage = ["", entry.Replace("@", stream.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)];
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "key.pdf");
        await File.WriteAllBytesAsync(path, file.Build(), Ct);

        var bytes = await RebuildAsync(path);

        var refusal = Refusal(bytes, 2);
        Assert.NotNull(refusal);
        Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, refusal.Kind);
        Assert.Equal([1], refusal.Pages);
        Assert.Contains(key + " on page 2", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStructureFileIsRefusedWithAllFourKeys()
    {
        var bytes = await RebuildAsync(TestData.Pdf("redact-structure.pdf"));

        var refusal = Refusal(bytes, 1);

        Assert.NotNull(refusal);
        Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, refusal.Kind);
        Assert.Equal([0], refusal.Pages);
        foreach (var key in new[] { "/Thumb", "/PieceInfo", "/Metadata", "/AA" })
        {
            Assert.Contains(key, refusal.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RefusalNamesEveryAffectedPage()
    {
        var file = new VerifierPdf("BT ET", "BT ET", "BT ET", "BT ET", "BT ET");
        var metadata = file.AddStream("/Type /Metadata /Subtype /XML", [0x20]);
        file.PageEntriesByPage = ["/AA << >>", "", "/PieceInfo << /X << /Private (x) >> >>", "", $"/AA << >> /Metadata {metadata} 0 R"];
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "many.pdf");
        await File.WriteAllBytesAsync(path, file.Build(), Ct);

        var bytes = await RebuildAsync(path);

        var refusal = Refusal(bytes, 5);
        Assert.NotNull(refusal);
        Assert.Equal([0, 2, 4], refusal.Pages);
        Assert.Contains("/AA on pages 1, 5", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("/PieceInfo on page 3", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("/Metadata on page 5", refusal.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("redact-everywhere.pdf")]
    [InlineData("redact-image.pdf")]
    [InlineData("redact-fonts.pdf")]
    [InlineData("text-rich.pdf")]
    [InlineData("outline.pdf")]
    [InlineData("annotations-foreign.pdf")]
    [InlineData("forms-basic.pdf")]
    [InlineData("pages-marked.pdf")]
    public async Task FileWithoutThePageKeysIsNotRefused(string name)
    {
        await using var original = await engine.OpenAsync(TestData.Pdf(name), cancellationToken: Ct);
        var pages = original.PageCount;
        var bytes = await RebuildAsync(TestData.Pdf(name));

        Assert.Null(Refusal(bytes, pages));
    }

    [Fact]
    public async Task AFileThatPdfiumRewroteFromObjectStreamsStillCarriesItsKeys()
    {
        // Through the rebuild too: the page dictionaries of redact-objstm.pdf are in an object stream, the keys of the second page
        // survive the import, and the written file has them in the open where the scan finds them.
        var bytes = await RebuildAsync(TestData.Pdf("redact-objstm.pdf"));

        var refusal = Refusal(bytes, 2);

        Assert.NotNull(refusal);
        Assert.Equal([1], refusal.Pages);
        Assert.DoesNotContain("/ObjStm", Latin1(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEmptyDocumentHasNoPagesAndNoInformation()
    {
        await using var empty = await engine.CreateEmptyAsync(Ct);

        Assert.Equal(0, empty.PageCount);
        Assert.Equal(new PdfMetadata(null, null, null, null, null, null, null, null), await empty.GetMetadataAsync(Ct));
        Assert.False(empty.HasAcroForm);
    }
}
