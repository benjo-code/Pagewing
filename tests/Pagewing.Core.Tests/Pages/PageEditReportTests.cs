using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Xunit;

namespace Pagewing.Core.Tests.Pages;

/// <summary>What a page plan loses, couples or invalidates, told before it is applied (milestone 13, spec 5.5; the facts are from the spike).</summary>
public sealed class PageEditReportTests
{
    private static readonly PageSourceId First = new(1);
    private static readonly PageSourceId Second = new(2);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static Task<PdfDocument> Open(TempFolder folder, string name) =>
        new PdfEngine().OpenAsync(PageEditExecutorTests.CopyOf(folder, name).Path, null, Cancel);

    private static Task<PageEditReport> Analyze(PageEditPlan plan, PdfDocument primary, params (PageSourceId Id, PdfDocument Document)[] inserted) =>
        PageEditExecutor.AnalyzeAsync(plan, primary, inserted.ToDictionary(i => i.Id, i => i.Document), Cancel);

    [Fact]
    public async Task PlainReorderReportsNothing()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "pages-marked.pdf");

        var plan = PageEditPlan.Identity(6).Move([4], 0).Rotate([1, 2], 90);
        var report = await Analyze(plan, primary);

        Assert.Empty(report.Losses);
    }

    [Fact]
    public async Task DeletingATargetPageReportsBookmarkToDeletedPage()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "pages-marked.pdf");

        // Kapitel 2 points to page 3; page 4 has no bookmark.
        var report = await Analyze(PageEditPlan.Identity(6).Delete([2, 3]), primary);

        var loss = Assert.Single(report.Losses);
        Assert.Equal(PageLossKind.BookmarkToDeletedPage, loss.Kind);
        Assert.Equal([2], loss.Pages);

        // Deleting only a page without a bookmark loses nothing.
        Assert.Empty((await Analyze(PageEditPlan.Identity(6).Delete([3]), primary)).Losses);
    }

    [Fact]
    public async Task InsertedFileWithOutlineReportsForeignOutlineDropped()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "text-rich.pdf");
        using var other = await Open(folder, "pages-marked.pdf");

        var plan = PageEditPlan.Identity(2).InsertFrom(First, [0, 1], 2);
        var report = await Analyze(plan, primary, (First, other));

        var loss = Assert.Single(report.Losses);
        Assert.Equal(PageLossKind.ForeignOutlineDropped, loss.Kind);
        Assert.Equal("source:1", loss.Detail);

        // A file that is in the dictionary but whose pages are not in the plan loses nothing.
        Assert.Empty((await Analyze(PageEditPlan.Identity(2), primary, (First, other))).Losses);
    }

    [Fact]
    public async Task SameFieldNamesFromTwoFilesReportCollision()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "forms-basic.pdf");
        using var other = await Open(folder, "forms-basic.pdf");

        var plan = PageEditPlan.Identity(2).InsertFrom(First, [0, 1], 2);
        var report = await Analyze(plan, primary, (First, other));

        var collision = Assert.Single(report.Losses, l => l.Kind == PageLossKind.FormFieldNameCollision);
        Assert.Equal([0, 1, 2, 3], collision.Pages);

        // The fields of the inserted pages are not entered into the form of the file.
        var inserted = Assert.Single(report.Losses, l => l.Kind == PageLossKind.DuplicatedFormFields);
        Assert.Equal("inserted", inserted.Detail);
        Assert.Equal([2, 3], inserted.Pages);
    }

    [Fact]
    public async Task OneFileWithFieldsOnSeveralPagesCollidesWithNothing()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "forms-basic.pdf");

        // Both pages of one file: fields that live on several pages are one field, not a collision.
        var report = await Analyze(PageEditPlan.Identity(2).Move([1], 0), primary);

        Assert.Empty(report.Losses);
    }

    [Fact]
    public async Task DuplicatingAFormPageReportsDuplicatedFormFields()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "forms-basic.pdf");

        var report = await Analyze(PageEditPlan.Identity(2).Duplicate([0]), primary);

        var loss = Assert.Single(report.Losses, l => l.Kind == PageLossKind.DuplicatedFormFields);
        Assert.Equal("duplicated", loss.Detail);
        Assert.Equal([0, 1], loss.Pages);
        Assert.DoesNotContain(report.Losses, l => l.Kind == PageLossKind.FormFieldNameCollision);
    }

    [Theory]
    [InlineData("signed-docmdp-none.pdf", null)]
    [InlineData("signed-docmdp-1.pdf", "docmdp:1")]
    [InlineData("signed-docmdp-2.pdf", "docmdp:2")]
    [InlineData("signed-docmdp-3.pdf", "docmdp:3")]
    public async Task SignedDocumentReportsSignatureInvalidated(string name, string? detail)
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, name);

        var report = await Analyze(PageEditPlan.Identity(primary.PageCount).Rotate([0], 90), primary);

        var loss = Assert.Single(report.Losses, l => l.Kind == PageLossKind.SignatureInvalidated);
        Assert.Equal(detail, loss.Detail);
        Assert.Null(loss.Pages);
    }

    [Fact]
    public async Task InsertedEncryptedFileReportsEncryptionNotPreserved()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "pages-marked.pdf");
        using var encrypted = await new PdfEngine().OpenAsync(PageEditExecutorTests.CopyOf(folder, "encrypted_hello_world_r3.pdf").Path, "hôtel", Cancel);

        // The result carries the protection of the primary file only.
        var report = await Analyze(PageEditPlan.Identity(6).InsertFrom(Second, [0], 6), primary, (Second, encrypted));

        var loss = Assert.Single(report.Losses, l => l.Kind == PageLossKind.EncryptionNotPreserved);
        Assert.Equal("source:2", loss.Detail);

        // An encrypted primary file stays encrypted: nothing to report.
        using var protectedPrimary = await new PdfEngine().OpenAsync(PageEditExecutorTests.CopyOf(folder, "encrypted_hello_world_r6.pdf").Path, "hôtel", Cancel);
        Assert.Empty((await Analyze(PageEditPlan.Identity(protectedPrimary.PageCount).Rotate([0], 90), protectedPrimary)).Losses);
    }

    [Fact]
    public async Task LinkToADeletedPageReportsInternalLinkLost()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "text-rich.pdf");

        // Page one links to page two.
        var report = await Analyze(PageEditPlan.Identity(2).Delete([1]), primary);

        var loss = Assert.Single(report.Losses);
        Assert.Equal(PageLossKind.InternalLinkLost, loss.Kind);
        Assert.Equal("deleted-page", loss.Detail);
        Assert.Equal([0], loss.Pages);

        // The link stays whole while its target stays, also after moving.
        Assert.Empty((await Analyze(PageEditPlan.Identity(2).Move([1], 0), primary)).Losses);
    }

    [Fact]
    public async Task InternalLinksOfCopiedPagesReportInternalLinkLost()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "pages-marked.pdf");
        using var rich = await Open(folder, "text-rich.pdf");
        using var richPrimary = await Open(folder, "text-rich.pdf");

        // The copy of a page with an internal link loses the target (the spike: a GoTo action without /D).
        var duplicated = await Analyze(PageEditPlan.Identity(2).Duplicate([0]), richPrimary);
        var duplicatedLoss = Assert.Single(duplicated.Losses, l => l.Kind == PageLossKind.InternalLinkLost);
        Assert.Equal("copied-page", duplicatedLoss.Detail);
        Assert.Equal([1], duplicatedLoss.Pages);

        var inserted = await Analyze(PageEditPlan.Identity(6).InsertFrom(First, [0, 1], 6), primary, (First, rich));
        var insertedLoss = Assert.Single(inserted.Losses, l => l.Kind == PageLossKind.InternalLinkLost);
        Assert.Equal("copied-page", insertedLoss.Detail);
        Assert.Equal([6], insertedLoss.Pages);
    }

    [Fact]
    public async Task BadInputIsRefusedClearly()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "pages-marked.pdf");

        // A source without a document, and a plan for another number of pages.
        var plan = PageEditPlan.Identity(6).InsertFrom(First, [0], 6);
        await Assert.ThrowsAsync<PageEditException>(() => Analyze(plan, primary));
        await Assert.ThrowsAsync<PageEditException>(() => Analyze(PageEditPlan.Identity(5), primary));
    }
}
