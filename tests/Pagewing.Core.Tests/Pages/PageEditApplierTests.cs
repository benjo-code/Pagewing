using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pages;

/// <summary>
/// <see cref="PageEditApplier"/> and <see cref="PageEditExecutor.ExpectedFingerprintsAsync"/>: the plan is written through the
/// check of the full rewrite, and the check compares with what the <em>sources</em> and the plan say, so a correctly turned
/// page of several lines is never refused for its text.
/// </summary>
public sealed class PageEditApplierTests
{
    private static readonly NoInserted None = new();

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARotatedMultiLineTextPageIsNotRefusedByTheCheck()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "text-rich.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = PageEditPlan.Identity(session.Document!.PageCount);
        plan = plan.Rotate(new HashSet<int> { 0, 1 }, 90);

        var result = await PageEditApplier.ApplyAsync(session, engine, new PageApplyRequest(plan, target, None.Files), null, Ct);

        Assert.True(result.Adopted);
        Assert.Same(result.Saved.Reopened, session.Document);
        Assert.Equal(2, session.Document!.PageCount);
        Assert.Equal(90, await session.Document.GetPageRotationAsync(0, Ct));
        Assert.Equal(90, await session.Document.GetPageRotationAsync(1, Ct));
        Assert.Equal(Path.GetFullPath(target), session.Path);
    }

    [Fact]
    public async Task TheExpectedFingerprintsComeFromTheSourcesAndThePlan()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var original = await PageEditExecutor.FingerprintsAsync(shown, Ct);
        var plan = PageEditPlan.Identity(shown.PageCount)
            .Delete(new HashSet<int> { 0 })
            .Rotate(new HashSet<int> { 1 }, 90)
            .InsertBlank(2, new BlankPage(300, 400));

        var expected = await PageEditExecutor.ExpectedFingerprintsAsync(plan, shown, new Dictionary<PageSourceId, PdfDocument>(), Ct);

        Assert.Equal(plan.Slots.Count, expected.Count);
        Assert.Equal(original[1].Width, expected[0].Width);
        Assert.Equal(original[2].Height, expected[1].Width);
        Assert.Equal(original[2].Width, expected[1].Height);
        Assert.Equal((original[2].Rotation + 90) % 360, expected[1].Rotation);
        Assert.Equal(new PageFingerprint(300, 400, 0, expected[2].TextHash), expected[2]);
        Assert.Equal(original[3].TextHash, expected[3].TextHash);
    }

    [Fact]
    public async Task AFailedRewriteLeavesTheTargetAndTheSessionAsTheyWere()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;
        var plan = PageEditPlan.Identity(shown!.PageCount).Delete(new HashSet<int> { 2 });

        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => PageEditApplier.ApplyAsync(
            session,
            engine,
            new PageApplyRequest(plan, path, None.Files),
            null,
            Ct,
            static (_, _, _, _, _) => throw new SaveFailureException(SaveFailureReason.DiskFull, "disk full")));

        Assert.Equal(SaveFailureReason.DiskFull, failure.Reason);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct));
        Assert.Same(shown, session.Document);
        Assert.Equal(6, session.Document!.PageCount);
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task APlanForAnotherPageCountIsRefusedBeforeAnythingIsOpened()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);

        await Assert.ThrowsAsync<PageEditException>(() => PageEditApplier.ApplyAsync(
            session, engine, new PageApplyRequest(PageEditPlan.Identity(3), path, None.Files), null, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PageEditApplier.ApplyAsync(
            new ViewerSession(engine), engine, new PageApplyRequest(PageEditPlan.Identity(6), path, None.Files), null, Ct));
    }

    [Fact]
    public async Task OnlyTheCheckedPositionsOfALongPlanAreReadAndTheRestIsMarkedUnchecked()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "thousand-pages.pdf");
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = PageEditPlan.Identity(shown.PageCount);

        var expected = await PageEditExecutor.ExpectedFingerprintsAsync(plan, shown, new Dictionary<PageSourceId, PdfDocument>(), Ct);

        Assert.Equal(1000, expected.Count);
        var checkedPages = PageEditExecutor.SampledPages(1000);
        Assert.Equal(checkedPages.Count, expected.Count(page => !ReferenceEquals(page, PageEditExecutor.Unchecked)));
        Assert.All(checkedPages, position => Assert.NotSame(PageEditExecutor.Unchecked, expected[position]));
    }

    private static string Copy(TempFolder folder, string fixture)
    {
        var path = Path.Combine(folder.Path, fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    private sealed class NoInserted
    {
        public IReadOnlyDictionary<PageSourceId, PageSourceFile> Files { get; } = new Dictionary<PageSourceId, PageSourceFile>();
    }
}
