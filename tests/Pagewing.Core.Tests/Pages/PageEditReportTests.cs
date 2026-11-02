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
        using var primary = await Open(folder, "text-rich.pdf");

        // A document without page labels, forms or signatures: reordering and turning lose nothing.
        var plan = PageEditPlan.Identity(2).Move([1], 0).Rotate([0, 1], 90);
        var report = await Analyze(plan, primary);

        Assert.Empty(report.Losses);
        Assert.False(report.IsRefused);
    }

    [Fact]
    public async Task PureRotationReportsNothing()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "pages-marked.pdf");

        // The pages keep their positions, so the page labels of the file still name the same pages.
        var report = await Analyze(PageEditPlan.Identity(6).Rotate([1, 2], 90).Rotate([4], 180), primary);

        Assert.Empty(report.Losses);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("insert")]
    public async Task ChangedPositionsReportPageLabelsShifted(string change)
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "pages-marked.pdf");
        using var other = await Open(folder, "text-rich.pdf");

        // pages-marked.pdf has page labels (roman for the first two pages, decimal from the third on).
        var identity = PageEditPlan.Identity(6);
        var plan = change switch
        {
            "delete" => identity.Delete([5]),
            "move" => identity.Move([5], 0),
            "duplicate" => identity.Duplicate([5]),
            "blank" => identity.InsertBlank(6, new BlankPage(100, 100)),
            _ => identity.InsertFrom(First, [0], 6),
        };
        var report = await Analyze(plan, primary, (First, other));

        var loss = Assert.Single(report.Losses, l => l.Kind == PageLossKind.PageLabelsShifted);
        Assert.Equal(PageLossDetail.Shifted, loss.Detail);
        Assert.Null(loss.Pages);
        Assert.False(report.IsRefused);
    }

    [Fact]
    public async Task ADocumentWithoutPageLabelsNeverReportsThem()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "text-rich.pdf");
        using var other = await Open(folder, "text-rich.pdf");

        var plan = PageEditPlan.Identity(2).Delete([0]).InsertFrom(First, [0, 1], 1).Duplicate([0]).InsertBlank(0, new BlankPage(10, 10));
        var report = await Analyze(plan, primary, (First, other));

        Assert.DoesNotContain(report.Losses, l => l.Kind == PageLossKind.PageLabelsShifted);
    }

    [Fact]
    public async Task PageLabelsOfAnInsertedFileAreReportedAsNotCarriedOver()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "text-rich.pdf");
        using var labelled = await Open(folder, "pages-marked.pdf");

        var report = await Analyze(PageEditPlan.Identity(2).InsertFrom(First, [0, 1], 2), primary, (First, labelled));

        // The primary file has no labels, so only the inserted file's labels are named.
        var loss = Assert.Single(report.Losses, l => l.Kind == PageLossKind.PageLabelsShifted);
        Assert.Equal("source:1", loss.Detail);
    }

    [Fact]
    public async Task DeletingTheFormPageReportsOrphanFormFields()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "forms-basic.pdf");

        // Telefon and Ort live on page two only; Name and Zahlung have a widget on page one as well.
        var report = await Analyze(PageEditPlan.Identity(2).Delete([1]), primary);

        var loss = Assert.Single(report.Losses, l => l.Kind == PageLossKind.OrphanFormFields);
        Assert.Equal([1], loss.Pages);
        Assert.Null(loss.Detail);

        // Keeping every page, in another order or duplicated, leaves no field without a page.
        Assert.DoesNotContain((await Analyze(PageEditPlan.Identity(2).Move([1], 0), primary)).Losses, l => l.Kind == PageLossKind.OrphanFormFields);
        Assert.DoesNotContain((await Analyze(PageEditPlan.Identity(2).Duplicate([1]), primary)).Losses, l => l.Kind == PageLossKind.OrphanFormFields);
    }

    [Fact]
    public async Task OrphanFieldsStillOccupyTheirNameForTheCollisionCheck()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "forms-basic.pdf");
        using var other = await Open(folder, "forms-basic.pdf");

        // Page two of the primary file is deleted: its fields stay in the form without a page. The inserted page two brings
        // fields of the same names, which the form engine reads as the same fields (Name and Zahlung also stand on the kept
        // page, so this fixture cannot show that Telefon and Ort alone would collide: it checks the report, not the seeding).
        var plan = PageEditPlan.Identity(2).Delete([1]).InsertFrom(First, [1], 1);
        var report = await Analyze(plan, primary, (First, other));

        Assert.Contains(report.Losses, l => l.Kind == PageLossKind.OrphanFormFields);
        var collision = Assert.Single(report.Losses, l => l.Kind == PageLossKind.FormFieldNameCollision);
        Assert.Equal([0, 1], collision.Pages);
    }

    [Fact]
    public async Task EncryptedInsertIntoAPlainPrimaryBlocksAndTheOtherWaysOnlyWarn()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        using var plain = await Open(folder, "pages-marked.pdf");
        using var plainOther = await Open(folder, "text-rich.pdf");
        using var encrypted = await engine.OpenAsync(PageEditExecutorTests.CopyOf(folder, "encrypted_hello_world_r3.pdf").Path, "hôtel", Cancel);
        using var protectedPrimary = await engine.OpenAsync(PageEditExecutorTests.CopyOf(folder, "encrypted_hello_world_r6.pdf").Path, "hôtel", Cancel);

        // A plain primary file and an encrypted insert: the insert would be written unprotected.
        var blocked = await Analyze(PageEditPlan.Identity(6).InsertFrom(First, [0], 6), plain, (First, encrypted));
        var loss = Assert.Single(blocked.Losses, l => l.Kind == PageLossKind.EncryptionNotPreserved);
        Assert.True(loss.Blocks);
        Assert.True(blocked.IsRefused);

        // An encrypted primary file and a plain insert: the result stays encrypted, nothing to refuse and nothing lost.
        var allowed = await Analyze(PageEditPlan.Identity(protectedPrimary.PageCount).InsertFrom(First, [0], protectedPrimary.PageCount), protectedPrimary, (First, plainOther));
        Assert.DoesNotContain(allowed.Losses, l => l.Kind == PageLossKind.EncryptionNotPreserved);
        Assert.False(allowed.IsRefused);

        // Both encrypted: the pages get the protection of the primary file; that is told, but not refused.
        var both = await Analyze(PageEditPlan.Identity(protectedPrimary.PageCount).InsertFrom(First, [0], protectedPrimary.PageCount), protectedPrimary, (First, encrypted));
        var warning = Assert.Single(both.Losses, l => l.Kind == PageLossKind.EncryptionNotPreserved);
        Assert.False(warning.Blocks);
        Assert.False(both.IsRefused);

        // Only a loss that blocks makes a report refuse; the other losses (signature, forms, labels) are warnings.
        Assert.False(new PageEditReport([new PageLoss(PageLossKind.SignatureInvalidated, null, null)]).IsRefused);
        Assert.True(new PageEditReport([new PageLoss(PageLossKind.EncryptionNotPreserved, "source:1", null, Blocks: true)]).IsRefused);
    }

    [Fact]
    public async Task DetailsOfSeveralSourcesAreOrderedNumerically()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "text-rich.pdf");
        using var ten = await Open(folder, "outline.pdf");
        using var two = await Open(folder, "outline.pdf");

        var plan = PageEditPlan.Identity(2).InsertFrom(new PageSourceId(10), [0], 2).InsertFrom(new PageSourceId(2), [0], 3);
        var report = await Analyze(plan, primary, (new PageSourceId(10), ten), (new PageSourceId(2), two));

        Assert.Equal(["source:2", "source:10"], report.Losses.Where(l => l.Kind == PageLossKind.ForeignOutlineDropped).Select(l => l.Detail));
        Assert.True(PageLossDetail.Compare("source:2", "source:10") < 0);
        Assert.True(PageLossDetail.Compare("docmdp:3", "source:1") < 0);
        Assert.True(PageLossDetail.Compare(null, "source:1") < 0);
        Assert.Equal(0, PageLossDetail.Compare("shifted", "shifted"));
    }

    [Fact]
    public async Task DeletingATargetPageReportsBookmarkToDeletedPage()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "pages-marked.pdf");

        // Kapitel 2 points to page 3; page 4 has no bookmark.
        var report = await Analyze(PageEditPlan.Identity(6).Delete([2, 3]), primary);

        var loss = Assert.Single(report.Losses, l => l.Kind == PageLossKind.BookmarkToDeletedPage);
        Assert.Equal([2], loss.Pages);

        // Deleting only a page without a bookmark loses no bookmark.
        Assert.DoesNotContain((await Analyze(PageEditPlan.Identity(6).Delete([3]), primary)).Losses, l => l.Kind == PageLossKind.BookmarkToDeletedPage);
    }

    [Fact]
    public async Task InsertedFileWithOutlineReportsForeignOutlineDropped()
    {
        using var folder = new TempFolder();
        using var primary = await Open(folder, "text-rich.pdf");
        using var other = await Open(folder, "pages-marked.pdf");

        var plan = PageEditPlan.Identity(2).InsertFrom(First, [0, 1], 2);
        var report = await Analyze(plan, primary, (First, other));

        var loss = Assert.Single(report.Losses, l => l.Kind == PageLossKind.ForeignOutlineDropped);
        Assert.Equal(PageLossDetail.Source(1), loss.Detail);

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
        Assert.Equal(PageLossDetail.Inserted, inserted.Detail);
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
        Assert.Equal(PageLossDetail.Duplicated, loss.Detail);
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
        Assert.True(loss.Blocks);
        Assert.True(report.IsRefused);

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
        Assert.Equal(PageLossDetail.DeletedPage, loss.Detail);
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
        Assert.Equal(PageLossDetail.CopiedPage, duplicatedLoss.Detail);
        Assert.Equal([1], duplicatedLoss.Pages);

        var inserted = await Analyze(PageEditPlan.Identity(6).InsertFrom(First, [0, 1], 6), primary, (First, rich));
        var insertedLoss = Assert.Single(inserted.Losses, l => l.Kind == PageLossKind.InternalLinkLost);
        Assert.Equal(PageLossDetail.CopiedPage, insertedLoss.Detail);
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
