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
    private const string UserPassword = "hôtel";

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
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document!;
        var original = await PageEditExecutor.FingerprintsAsync(shown, Ct);
        var plan = PageEditPlan.Identity(shown.PageCount)
            .Delete(new HashSet<int> { 0 })
            .Rotate(new HashSet<int> { 1 }, 90)
            .InsertBlank(2, new BlankPage(300, 400));

        var expected = await PageEditExecutor.ExpectedFingerprintsAsync(plan, shown, new Dictionary<PageSourceId, PdfDocument>(), Ct);

        Assert.Equal(plan.Slots.Count, expected.Count);
        Assert.Equal(original[1], expected[0]);
        Assert.Equal(original[2].Height, expected[1].Width);
        Assert.Equal(original[2].Width, expected[1].Height);
        Assert.Equal((original[2].Rotation + 90) % 360, expected[1].Rotation);
        Assert.False(expected[1].CompareTextOrder);
        Assert.Equal(original[2].TextSetHash, expected[1].TextSetHash);
        Assert.Equal(original[3], expected[3]);

        // What the file really has afterwards is what was expected: the blank page (its size and its empty text) and the
        // turned page (as far as the plan can know it) included.
        await PageEditApplier.ApplyAsync(session, engine, new PageApplyRequest(plan, Path.Combine(folder.Path, "out.pdf"), None.Files), null, Ct);
        var actual = await PageEditExecutor.FingerprintsAsync(session.Document!, Ct);
        Assert.Equal(expected[0], actual[0]);
        Assert.Equal(expected[2], actual[2]);
        Assert.Equal(new PageFingerprint(300, 400, 0, actual[2].TextHash, actual[2].TextSetHash), actual[2]);
        Assert.Equal(expected[3], actual[3]);
        Assert.Equal((expected[1].Width, expected[1].Height, expected[1].Rotation, expected[1].TextSetHash), (actual[1].Width, actual[1].Height, actual[1].Rotation, actual[1].TextSetHash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APathThatNowNamesAnotherFileIsRefusedWhateverTheTarget(bool inOriginal)
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;

        // Another program replaced the file at the path by a different one with the same number of pages.
        File.Move(path, Path.Combine(folder.Path, "moved.pdf"));
        File.Copy(TestData.Pdf("pages-marked.pdf"), path);
        File.AppendAllText(path, "\n");
        var foreign = await File.ReadAllBytesAsync(path, Ct);
        var target = inOriginal ? path : Path.Combine(folder.Path, "out.pdf");
        var plan = PageEditPlan.Identity(6).Delete(new HashSet<int> { 0 });
        var rewrites = 0;

        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => PageEditApplier.ApplyAsync(
            session,
            engine,
            new PageApplyRequest(plan, target, None.Files, OverwriteChangedOriginal: true),
            null,
            Ct,
            (copy, e, r, p, token) =>
            {
                rewrites++;
                return DocumentSaver.RewriteAsync(copy, e, r, p, token);
            }));

        // Nothing is written, not even with the override: the pages built from the foreign file are never put on its fingerprints.
        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
        Assert.Equal(0, rewrites);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(path, Ct));
        Assert.False(File.Exists(Path.Combine(folder.Path, "out.pdf")));
        Assert.Same(shown, session.Document);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }

    [Fact]
    public async Task AFailureAfterTheWriteIsToldApartFromAFailedWrite()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var target = Path.Combine(folder.Path, "written.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = PageEditPlan.Identity(6).Delete(new HashSet<int> { 0 });

        // The checked file is closed before the session can take it (an adopt that cannot work).
        var failure = await Assert.ThrowsAsync<PageApplyAdoptException>(() => PageEditApplier.ApplyAsync(
            session,
            engine,
            new PageApplyRequest(plan, target, None.Files),
            null,
            Ct,
            async (copy, e, r, p, token) =>
            {
                var result = await DocumentSaver.RewriteAsync(copy, e, r, p, token);
                await result.Reopened.DisposeAsync();
                return result;
            }));

        Assert.Equal(target, failure.TargetPath);
        Assert.NotNull(failure.InnerException);
        await using var written = await engine.OpenAsync(target, cancellationToken: Ct);
        Assert.Equal(5, written.PageCount);
        Assert.Equal(6, session.Document!.PageCount);
    }

    [Fact]
    public async Task TheWorkingCopyAndTheSourcesAreClosedOnEveryFailurePath()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var inserted = Copy(folder, "hello_world.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var plan = PageEditPlan.Identity(6).InsertFrom(new PageSourceId(1), [0], 6);
        var files = new Dictionary<PageSourceId, PageSourceFile> { [new PageSourceId(1)] = new PageSourceFile(inserted, null) };
        PdfDocument? working = null;
        var sources = new List<PdfDocument>();
        var hooks = new PageApplyHooks
        {
            Executor = new PageEditExecutor(engine) { WorkingCopyOpened = copy => working = copy },
            SourceOpened = sources.Add,
        };

        // A failed rewrite.
        await Assert.ThrowsAsync<SaveFailureException>(() => PageEditApplier.ApplyAsync(
            session, engine, new PageApplyRequest(plan, Path.Combine(folder.Path, "a.pdf"), files), null,
            static (_, _, _, _, _) => throw new SaveFailureException(SaveFailureReason.DiskFull, "full"), hooks, Ct));
        AssertClosed(working, sources);

        // A file that is not the one that was opened (checked before anything is written).
        File.AppendAllText(path, "\n");
        working = null;
        sources.Clear();
        await Assert.ThrowsAsync<SaveFailureException>(() => PageEditApplier.ApplyAsync(
            session, engine, new PageApplyRequest(plan, Path.Combine(folder.Path, "b.pdf"), files), null, null, hooks, Ct));
        AssertClosed(working, sources);
    }

    private static void AssertClosed(PdfDocument? working, List<PdfDocument> sources)
    {
        Assert.NotNull(working);
        Assert.Throws<ObjectDisposedException>(() => working.PageCount);
        Assert.Single(sources);
        Assert.All(sources, source => Assert.Throws<ObjectDisposedException>(() => source.PageCount));
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

    [Fact]
    public async Task AnInsertedFileIsCheckedAgainstTheStampTakenWhenItWasInserted()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var other = Copy(folder, "text-rich.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var first = new PageSourceId(1);
        var plan = PageEditPlan.Identity(session.Document!.PageCount).InsertFrom(first, [0, 1], 6);

        // The stamp of the file as it was when it was opened for the dialog.
        PageSourceFile inserted;
        await using (var opened = await engine.OpenAsync(other, null, Ct))
        {
            inserted = PageSourceFile.Of(other, null, opened);
        }

        Assert.NotNull(inserted.Stamp);
        var files = new Dictionary<PageSourceId, PageSourceFile> { [first] = inserted };

        // The file is still that file: the plan is written (and the stamp does not depend on how the file is read).
        var target = Path.Combine(folder.Path, "merged.pdf");
        var done = await PageEditApplier.ApplyAsync(session, engine, new PageApplyRequest(plan, target, files), null, Ct);
        Assert.Equal(8, done.Saved.Reopened.PageCount);

        // It was touched since: refused by name before anything is written, with no way to overwrite.
        using var again = new ViewerSession(engine);
        await again.OpenAsync(path);
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(2));
        var second = Path.Combine(folder.Path, "second.pdf");
        var rewrites = 0;
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => PageEditApplier.ApplyAsync(
            again,
            engine,
            new PageApplyRequest(plan, second, files, OverwriteChangedOriginal: true),
            null,
            Ct,
            (_, _, _, _, _) =>
            {
                rewrites++;
                throw new InvalidOperationException("must not be called");
            }));
        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
        Assert.Equal(other, failure.FilePath);
        Assert.Equal(0, rewrites);
        Assert.False(File.Exists(second));

        // Deleted or replaced by something that is no PDF: the same, naming the file (a file without a stamp keeps the old errors).
        File.Delete(other);
        failure = await Assert.ThrowsAsync<SaveFailureException>(() => PageEditApplier.ApplyAsync(again, engine, new PageApplyRequest(plan, second, files), null, Ct));
        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
        Assert.Equal(other, failure.FilePath);
        var unstamped = new Dictionary<PageSourceId, PageSourceFile> { [first] = new PageSourceFile(other, null) };
        var open = await Assert.ThrowsAsync<PdfOpenException>(() => PageEditApplier.ApplyAsync(again, engine, new PageApplyRequest(plan, second, unstamped), null, Ct));
        Assert.Equal(PdfOpenError.FileNotFound, open.Error);
        Assert.Equal(6, again.Document!.PageCount);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task AnEncryptedInsertedFileInAPlainDocumentIsRefusedByTheApplierItself(string fixture)
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "hello_world.pdf");
        var locked = Copy(folder, fixture);
        var target = Path.Combine(folder.Path, "out.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;
        var first = new PageSourceId(1);
        var plan = PageEditPlan.Identity(shown!.PageCount).InsertFrom(first, [0], shown.PageCount);
        var files = new Dictionary<PageSourceId, PageSourceFile> { [first] = new PageSourceFile(locked, UserPassword) };
        var sources = new List<PdfDocument>();
        var rewrites = 0;
        var hooks = new PageApplyHooks { SourceOpened = sources.Add };

        // Called directly, without consulting the report.
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => PageEditApplier.ApplyAsync(
            session,
            engine,
            new PageApplyRequest(plan, target, files),
            null,
            (_, _, _, _, _) =>
            {
                rewrites++;
                throw new InvalidOperationException("must not be called");
            },
            hooks,
            Ct));

        Assert.Equal(SaveFailureReason.EncryptedNotSupported, failure.Reason);
        Assert.Equal(locked, failure.FilePath);
        Assert.Equal(0, rewrites);
        Assert.False(File.Exists(target));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
        Assert.Empty(Directory.GetFiles(folder.Path, ".pw-*"));
        Assert.Same(shown, session.Document);
        var source = Assert.Single(sources);
        Assert.Throws<ObjectDisposedException>(() => source.PageCount);

        // The command-line entry point refuses the same way.
        var viaWrite = await Assert.ThrowsAsync<SaveFailureException>(() => PageEditApplier.WriteAsync(
            engine, shown, path, null, session.OpenedStamp, new PageApplyRequest(plan, target, files), null, Ct));
        Assert.Equal(SaveFailureReason.EncryptedNotSupported, viaWrite.Reason);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task AnEncryptedInsertedFileInAnEncryptedDocumentIsWrittenAndStaysEncrypted(string fixture)
    {
        using var folder = new TempFolder();
        var path = Copy(folder, fixture);
        var locked = Path.Combine(folder.Path, "inserted.pdf");
        File.Copy(TestData.Pdf(fixture == "encrypted_hello_world_r3.pdf" ? "encrypted_hello_world_r6.pdf" : "encrypted_hello_world_r3.pdf"), locked);
        var target = Path.Combine(folder.Path, "out.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path, UserPassword);
        var first = new PageSourceId(1);
        var plan = PageEditPlan.Identity(session.Document!.PageCount).InsertFrom(first, [0], session.Document.PageCount);
        var files = new Dictionary<PageSourceId, PageSourceFile> { [first] = new PageSourceFile(locked, UserPassword) };

        var result = await PageEditApplier.ApplyAsync(session, engine, new PageApplyRequest(plan, target, files), null, Ct);

        Assert.True(result.Adopted);
        Assert.Equal(2, session.Document!.PageCount);
        var closed = await Assert.ThrowsAsync<PdfOpenException>(() => engine.OpenAsync(target, cancellationToken: Ct));
        Assert.Equal(PdfOpenError.PasswordRequired, closed.Error);
    }

    [Fact]
    public async Task APlainInsertedFileInAPlainDocumentIsStillWritten()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "hello_world.pdf");
        var other = Copy(folder, "text-rich.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var first = new PageSourceId(1);
        var plan = PageEditPlan.Identity(session.Document!.PageCount).InsertFrom(first, [0, 1], session.Document.PageCount);
        var files = new Dictionary<PageSourceId, PageSourceFile> { [first] = new PageSourceFile(other, null) };

        var result = await PageEditApplier.ApplyAsync(session, engine, new PageApplyRequest(plan, target, files), null, Ct);

        Assert.True(result.Adopted);
        Assert.Equal(3, session.Document!.PageCount);
    }

    [Fact]
    public void ThePasswordOfASourceFileIsNeverInItsText()
    {
        var file = new PageSourceFile("a.pdf", "tops3cret", new FileStamp(10, DateTime.UnixEpoch));
        var request = new PageApplyRequest(PageEditPlan.Identity(1), "out.pdf", new Dictionary<PageSourceId, PageSourceFile> { [new PageSourceId(1)] = file });

        Assert.DoesNotContain("tops3cret", file.ToString(), StringComparison.Ordinal);
        Assert.Contains("(hidden)", file.ToString(), StringComparison.Ordinal);
        Assert.Contains("a.pdf", file.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tops3cret", request.ToString(), StringComparison.Ordinal);
        Assert.Contains("Password = null", new PageSourceFile("b.pdf", null).ToString(), StringComparison.Ordinal);

        // Equality still sees the password (two files with different passwords are different sources).
        Assert.NotEqual(file, file with { Password = "other" });
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
