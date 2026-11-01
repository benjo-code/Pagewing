using System.Security.Cryptography;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pages;

/// <summary>
/// <see cref="DocumentSplitter"/>: how a document is cut into parts (pure), and how the parts are written through the checked
/// full rewrite into a folder without ever leaving it or replacing a file.
/// </summary>
public sealed class DocumentSplitterTests
{
    private const string UserPassword = "hôtel";

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PdfOutlineItem Mark(string title, int? page, params PdfOutlineItem[] children) => new(title, page, false, children);

    [Fact]
    public void EveryNSplitsWithARemainder()
    {
        var parts = DocumentSplitter.Plan(7, SplitMode.Every(3));

        Assert.Equal(3, parts.Count);
        Assert.Equal([0, 1, 2], parts[0].Pages);
        Assert.Equal([3, 4, 5], parts[1].Pages);
        Assert.Equal([6], parts[2].Pages);
        Assert.Equal(parts.Count, parts.Select(p => p.Stem).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(6, 6, 1)]
    [InlineData(6, 1, 6)]
    [InlineData(6, 2, 3)]
    [InlineData(1, 5, 1)]
    public void EveryNCoversEveryPageOnce(int pageCount, int every, int expectedParts)
    {
        var parts = DocumentSplitter.Plan(pageCount, SplitMode.Every(every));

        Assert.Equal(expectedParts, parts.Count);
        Assert.Equal(Enumerable.Range(0, pageCount), parts.SelectMany(p => p.Pages));
    }

    [Fact]
    public void EveryNRefusesNothingToDo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SplitMode.Every(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SplitMode.Every(-3));
        Assert.Throws<PageEditException>(() => DocumentSplitter.Plan(0, SplitMode.Every(1)));
    }

    [Fact]
    public void RangesSplitAsGiven()
    {
        var parts = DocumentSplitter.Plan(9, SplitMode.Ranges("1-3,4-9"));
        Assert.Equal(2, parts.Count);
        Assert.Equal([0, 1, 2], parts[0].Pages);
        Assert.Equal([3, 4, 5, 6, 7, 8], parts[1].Pages);

        // Overlaps and repeats are allowed, the order of the text is the order of the files, and a part may be one page.
        var overlapping = DocumentSplitter.Plan(9, SplitMode.Ranges(" 2-5 , 4-7,9, 1 "));
        Assert.Equal(4, overlapping.Count);
        Assert.Equal([1, 2, 3, 4], overlapping[0].Pages);
        Assert.Equal([3, 4, 5, 6], overlapping[1].Pages);
        Assert.Equal([8], overlapping[2].Pages);
        Assert.Equal([0], overlapping[3].Pages);
        Assert.Equal(4, overlapping.Select(p => p.Stem).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.Single(DocumentSplitter.Plan(4, SplitMode.Ranges("all")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("5-3")]
    [InlineData("abc")]
    [InlineData("1,,2")]
    [InlineData("1-")]
    public void RangesRefuseBadText(string text)
    {
        Assert.Throws<FormatException>(() => DocumentSplitter.Plan(9, SplitMode.Ranges(text)));
    }

    [Fact]
    public void RangesRefusePagesBeyondTheDocument()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DocumentSplitter.Plan(9, SplitMode.Ranges("1-3,8-12")));
        Assert.Equal(12, ex.ActualValue);
    }

    [Fact]
    public void RangesRefuseMoreWorkThanTheLimitsAllowBeforeParsingIt()
    {
        var tooManyParts = string.Join(',', Enumerable.Repeat("1", DocumentSplitter.MaxParts + 1));
        Assert.Equal(PageEditError.LimitExceeded, Assert.Throws<PageEditException>(() => DocumentSplitter.Plan(5, SplitMode.Ranges(tooManyParts))).Error);

        // 100 parts of 1000 pages are 100,000 pages in total: exactly the limit of the sum is fine, one more is refused.
        var atLimit = string.Join(',', Enumerable.Repeat("1-1000", DocumentSplitter.MaxTotalPages / 1000));
        Assert.Equal(DocumentSplitter.MaxTotalPages / 1000, DocumentSplitter.Plan(1000, SplitMode.Ranges(atLimit)).Count);
        Assert.Equal(PageEditError.LimitExceeded, Assert.Throws<PageEditException>(() => DocumentSplitter.Plan(1000, SplitMode.Ranges(atLimit + ",1"))).Error);
    }

    [Fact]
    public void BookmarksSplitAtTopLevelTargetsSorted()
    {
        // The order of the bookmarks is not the order of the pages; children never split.
        var outline = new[]
        {
            Mark("Kapitel 3", 4),
            Mark("Kapitel 1", 0, Mark("Abschnitt 1.1", 1)),
            Mark("Kapitel 2", 2),
        };

        var parts = DocumentSplitter.Plan(6, SplitMode.Bookmarks(outline));

        Assert.Equal(3, parts.Count);
        Assert.Equal("Kapitel 1", parts[0].Stem);
        Assert.Equal([0, 1], parts[0].Pages);
        Assert.Equal("Kapitel 2", parts[1].Stem);
        Assert.Equal([2, 3], parts[1].Pages);
        Assert.Equal("Kapitel 3", parts[2].Stem);
        Assert.Equal([4, 5], parts[2].Pages);
    }

    [Fact]
    public async Task BookmarksOfTheTestDocumentSplitIntoThreeParts()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        await using var document = await engine.OpenAsync(path, null, Ct);

        var parts = DocumentSplitter.Plan(document.PageCount, SplitMode.Bookmarks(await document.GetOutlineAsync(Ct)));

        // Pages 1-2, 3-4 and 5-6 of the document (1-based).
        Assert.Equal(["Kapitel 1", "Kapitel 2", "Kapitel 3"], parts.Select(p => p.Stem));
        Assert.Equal([0, 1], parts[0].Pages);
        Assert.Equal([2, 3], parts[1].Pages);
        Assert.Equal([4, 5], parts[2].Pages);
    }

    [Fact]
    public void BookmarksWithoutTopLevelFail()
    {
        var empty = Assert.Throws<DocumentSplitException>(() => DocumentSplitter.Plan(6, SplitMode.Bookmarks([])));
        Assert.Equal(DocumentSplitError.NoBookmarks, empty.Error);

        // Only bookmarks without a usable target are as good as none; a child is no top-level bookmark.
        var unusable = new[] { Mark("Lost", null, Mark("Child", 3)), Mark("Beyond", 6), Mark("Negative", -1) };
        Assert.Equal(DocumentSplitError.NoBookmarks, Assert.Throws<DocumentSplitException>(() => DocumentSplitter.Plan(6, SplitMode.Bookmarks(unusable))).Error);
    }

    [Fact]
    public void BookmarksOnTheSamePageFormOnePart()
    {
        var outline = new[] { Mark("First", 2), Mark("Second", 2), Mark("Third", 4), Mark("Before", 0) };

        var parts = DocumentSplitter.Plan(6, SplitMode.Bookmarks(outline));

        Assert.Equal(["Before", "First", "Third"], parts.Select(p => p.Stem));
        Assert.Equal([2, 3], parts[1].Pages);
    }

    [Fact]
    public void PagesBeforeTheFirstBookmarkFormTheFirstPart()
    {
        var outline = new[] { Mark("Chapter", 3), Mark("Lost", null), Mark("Later", 5) };

        var parts = DocumentSplitter.Plan(7, SplitMode.Bookmarks(outline), "Book");

        Assert.Equal(3, parts.Count);
        Assert.Equal("Book-front", parts[0].Stem);
        Assert.Equal([0, 1, 2], parts[0].Pages);
        Assert.Equal("Chapter", parts[1].Stem);
        Assert.Equal([3, 4], parts[1].Pages);
        Assert.Equal("Later", parts[2].Stem);
        Assert.Equal([5, 6], parts[2].Pages);
    }

    [Fact]
    public void ExtractIsOnePartWithTheSelection()
    {
        var part = DocumentSplitter.Extract(9, "7,2-3", "Auszug");

        Assert.Equal("Auszug", part.Stem);
        Assert.Equal([1, 2, 6], part.Pages);
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentSplitter.Extract(9, "10", "x"));
        Assert.Throws<FormatException>(() => DocumentSplitter.Extract(9, "x", "x"));
    }

    [Fact]
    public void TargetPathsStayInsideTheFolderWhateverTheTitlesSay()
    {
        using var folder = new TempFolder();
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        var parts = HostileParts(16);

        var targets = DocumentSplitter.TargetPaths(parts, directory);

        Assert.Equal(16, targets.Count);
        Assert.Equal(parts.Count, targets.Count);
        AssertInside(directory, targets);
        Assert.Equal(targets.Count, targets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(targets, target => Assert.EndsWith(".pdf", target, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteCreatesFilesInsideTheFolderOnly()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out", "deep")).FullName;

        // Decoys where a path-like title would land if it were taken as a path: the parent folders and the folder itself.
        var decoys = new[]
        {
            Path.Combine(folder.Path, "x.pdf"),
            Path.Combine(folder.Path, "out", "x.pdf"),
            Path.Combine(folder.Path, "out", "a", "b.pdf"),
        };
        Directory.CreateDirectory(Path.Combine(folder.Path, "out", "a"));
        foreach (var decoy in decoys)
        {
            File.WriteAllText(decoy, "decoy " + Path.GetFileName(decoy));
        }

        var before = decoys.Select(d => (d, Hash(d))).ToArray();
        var untouched = Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var document = await engine.OpenAsync(path, null, Ct);
        var parts = HostileParts(16);

        var written = await DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite: false, progress: null, Ct);

        Assert.Equal(16, written.Count);
        Assert.Equal(parts.Count, written.Count);
        AssertInside(directory, written);
        Assert.Equal(written.Count, written.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(written, file => Assert.True(File.Exists(file)));

        // Nothing else was created or touched, anywhere under the work folder, and no temporary file is left.
        var now = Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = now.Except(untouched, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(written.Order(StringComparer.OrdinalIgnoreCase), created.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(created, f => Path.GetFileName(f).StartsWith(".pw-", StringComparison.Ordinal));
        foreach (var (decoy, hash) in before)
        {
            Assert.Equal(hash, Hash(decoy));
        }

        // Every file is a real PDF of one page of the source.
        foreach (var file in written)
        {
            await using var part = await engine.OpenAsync(file, null, Ct);
            Assert.Equal(1, part.PageCount);
        }
    }

    [Fact]
    public async Task WriteRefusesExistingFilesWithoutOverwrite()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        var parts = DocumentSplitter.Plan(6, SplitMode.Every(2), "doc");
        var targets = DocumentSplitter.TargetPaths(parts, directory);

        // The LAST target exists: nothing may be written before the refusal, not even the first parts.
        File.WriteAllText(targets[2], "precious");
        await using var document = await engine.OpenAsync(path, null, Ct);

        var ex = await Assert.ThrowsAsync<DocumentSplitException>(() =>
            DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite: false, progress: null, Ct));

        Assert.Equal(DocumentSplitError.TargetExists, ex.Error);
        Assert.Equal([targets[2]], ex.Paths);
        Assert.Equal("precious", File.ReadAllText(targets[2]));
        Assert.False(File.Exists(targets[0]));
        Assert.False(File.Exists(targets[1]));
        Assert.Single(Directory.GetFiles(directory));

        // A directory in the way is refused as well, with and without overwrite.
        File.Delete(targets[2]);
        Directory.CreateDirectory(targets[1]);
        foreach (var overwrite in new[] { false, true })
        {
            var inTheWay = await Assert.ThrowsAsync<DocumentSplitException>(() =>
                DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite, progress: null, Ct));
            Assert.Equal(DocumentSplitError.TargetExists, inTheWay.Error);
        }

        // With the explicit choice the file is replaced (atomically, through the checked rewrite).
        Directory.Delete(targets[1]);
        File.WriteAllText(targets[2], "replace me");
        var written = await DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite: true, progress: null, Ct);
        Assert.Equal(targets, written);
        await using var replaced = await engine.OpenAsync(targets[2], null, Ct);
        Assert.Equal(2, replaced.PageCount);
    }

    [Fact]
    public async Task WriteNeverReplacesTheSourceItselfEvenWithOverwrite()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var before = Hash(path);
        await using var document = await engine.OpenAsync(path, null, Ct);

        // A part named like the source, in the folder of the source.
        var parts = new[] { new SplitPart("pages-marked", [0, 1]) };
        foreach (var overwrite in new[] { false, true })
        {
            var ex = await Assert.ThrowsAsync<DocumentSplitException>(() =>
                DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, folder.Path, overwrite, progress: null, Ct));
            Assert.Equal(DocumentSplitError.TargetIsSource, ex.Error);
        }

        Assert.Equal(before, Hash(path));
        Assert.Empty(Directory.GetFiles(folder.Path, ".pw-*"));
    }

    [Fact]
    public async Task WriteRefusesAFolderThatDoesNotExist()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        await using var document = await engine.OpenAsync(path, null, Ct);
        var missing = Path.Combine(folder.Path, "missing");

        var ex = await Assert.ThrowsAsync<DocumentSplitException>(() =>
            DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), [new SplitPart("a", [0])], missing, overwrite: false, progress: null, Ct));

        Assert.Equal(DocumentSplitError.DirectoryMissing, ex.Error);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task WriteRefusesBadPartsBeforeTheFirstByte()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, null, Ct);
        var file = new PageSourceFile(path, null);
        SplitPart[][] bad =
        [
            [new SplitPart("ok", [0]), new SplitPart("beyond", [6])],
            [new SplitPart("ok", [0]), new SplitPart("empty", [])],
            [new SplitPart("ok", [0]), new SplitPart("unordered", [3, 1])],
            [new SplitPart("ok", [0]), new SplitPart("repeated", [1, 1])],
            [new SplitPart("ok", [0]), new SplitPart("negative", [-1])],
        ];

        foreach (var parts in bad)
        {
            await Assert.ThrowsAsync<PageEditException>(() =>
                DocumentSplitter.WriteAsync(engine, document, file, parts, directory, overwrite: false, progress: null, Ct));
        }

        await Assert.ThrowsAsync<ArgumentException>(() =>
            DocumentSplitter.WriteAsync(engine, document, file, [], directory, overwrite: false, progress: null, Ct));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task WrittenPartsHaveThePagesOfTheirRange()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, null, Ct);
        var parts = DocumentSplitter.Plan(document.PageCount, SplitMode.Ranges("1-2,4,3-6,2"), "doc");
        var reports = new List<double>();

        var written = await DocumentSplitter.WriteAsync(
            engine, document, new PageSourceFile(path, null), parts, directory, overwrite: false, new SyncProgress(reports), Ct);

        Assert.Equal(parts.Count, written.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            await using var part = await engine.OpenAsync(written[i], null, Ct);
            Assert.Equal(parts[i].Pages.Count, part.PageCount);
            for (var page = 0; page < part.PageCount; page++)
            {
                var text = await part.GetTextAsync(page, Ct);
                Assert.Contains($"PAGE {parts[i].Pages[page] + 1}", text, StringComparison.Ordinal);
            }
        }

        // The page that is turned and the pages of other sizes keep their size and rotation.
        await using var landscape = await engine.OpenAsync(written[2], null, Ct);
        Assert.Equal(await document.GetPageRotationAsync(4, Ct), await landscape.GetPageRotationAsync(2, Ct));

        // The source was not touched, and the progress went from 0 to 1 without going back.
        Assert.Equal(6, document.PageCount);
        Assert.Equal(1, reports[^1]);
        Assert.All(reports, value => Assert.InRange(value, 0, 1));
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public async Task SplitKeepsOutlineLabelsOfThePrimaryWhereTheSpikeSaysSo()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, null, Ct);
        var parts = DocumentSplitter.Plan(document.PageCount, SplitMode.Bookmarks(await document.GetOutlineAsync(Ct)));

        var reports = await DocumentSplitter.AnalyzeAsync(document, parts, Ct);
        var written = await DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite: false, progress: null, Ct);

        // The report is made for every part and names what the split loses, before anything is written.
        Assert.Equal(parts.Count, reports.Count);
        Assert.All(reports, report => Assert.False(report.IsRefused));
        var middle = reports[1].Losses;
        var toDeleted = Assert.Single(middle, loss => loss.Kind == PageLossKind.BookmarkToDeletedPage);
        Assert.Contains(0, toDeleted.Pages!);
        Assert.Contains(middle, loss => loss.Kind == PageLossKind.PageLabelsShifted);

        // As the spike says: the outline of the primary file stays; the entry whose page is gone stays without a target, and
        // the entry of the part follows its page. The page labels stay in the file (by position).
        await using var second = await engine.OpenAsync(written[1], null, Ct);
        var outline = await second.GetOutlineAsync(Ct);
        Assert.Equal(["Kapitel 1", "Kapitel 2", "Kapitel 3"], outline.Select(o => o.Title));
        Assert.Null(outline[0].PageIndex);
        Assert.Equal(0, outline[1].PageIndex);
        Assert.Null(outline[2].PageIndex);
        Assert.True(await second.HasPageLabelsAsync(Ct));
    }

    [Fact]
    public async Task AnalyzeReportsWhatEveryPartLosesAndTheSignatureOfASignedFile()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "signed-docmdp-2.pdf");
        await using var document = await engine.OpenAsync(path, null, Ct);

        var reports = await DocumentSplitter.AnalyzeAsync(document, [new SplitPart("all", [.. Enumerable.Range(0, document.PageCount)])], Ct);

        // The parts are new files: the signature is shown as invalid, and the report says so.
        Assert.Contains(reports[0].Losses, loss => loss.Kind == PageLossKind.SignatureInvalidated);
        Assert.False(reports[0].IsRefused);
    }

    [Fact]
    public async Task ACanceledWriteLeavesNoTemporaryFileAndReportsWhatWasWritten()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, null, Ct);
        var parts = DocumentSplitter.Plan(6, SplitMode.Every(1), "p");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var written = new List<string>();

        // Cancel as soon as the second part starts: the first is complete and stays, nothing else is left.
        var progress = new SyncProgress([], value =>
        {
            if (value > (1.0 / parts.Count) + 1e-9)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite: false, progress, cancellation.Token, written));

        var files = Directory.GetFiles(directory);
        Assert.NotEmpty(written);
        Assert.True(written.Count < parts.Count);
        Assert.Equal(written.Order(StringComparer.OrdinalIgnoreCase), files.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => Path.GetFileName(f).StartsWith(".pw-", StringComparison.Ordinal));
        foreach (var file in written)
        {
            await using var complete = await engine.OpenAsync(file, null, Ct);
            Assert.Equal(1, complete.PageCount);
        }
    }

    [Fact]
    public async Task ACanceledTokenBeforeTheFirstPartWritesNothing()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, null, Ct);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), [new SplitPart("a", [0])], directory, overwrite: false, progress: null, cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task AFailureInTheMiddleReportsTheFilesThatAreComplete()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, null, Ct);
        var parts = DocumentSplitter.Plan(6, SplitMode.Every(2), "p");
        var targets = DocumentSplitter.TargetPaths(parts, directory);
        var written = new List<string>();

        // The source is replaced by another program after the first part: the next part fails, with a reason, and writes nothing.
        var progress = new SyncProgress([], value =>
        {
            if (value > (1.0 / parts.Count) + 1e-9 && !File.Exists(path + ".changed"))
            {
                File.WriteAllText(path + ".changed", "x");
                File.AppendAllText(path, "\n% changed by another program\n");
            }
        });

        var ex = await Assert.ThrowsAsync<DocumentSplitException>(() =>
            DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite: false, progress, Ct, written));

        Assert.Equal(DocumentSplitError.PartFailed, ex.Error);
        Assert.Equal(1, ex.PartIndex);
        Assert.Equal([targets[1]], ex.Paths);
        Assert.Equal(SaveFailureReason.SourceModified, Assert.IsType<SaveFailureException>(ex.InnerException).Reason);
        Assert.Equal([targets[0]], written);
        Assert.Equal([targets[0]], Directory.GetFiles(directory));
    }

    [Fact]
    public async Task WriteNeverReplacesAFileThatAnotherProgramCreatesWhileAPartIsWritten()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, null, Ct);
        var parts = DocumentSplitter.Plan(6, SplitMode.Every(2), "p");
        var targets = DocumentSplitter.TargetPaths(parts, directory);
        var foreign = "foreign file of another program"u8.ToArray();
        var written = new List<string>();

        // The check before the part has passed; while the part is written and checked, another program creates its file.
        var progress = new SyncProgress([], value =>
        {
            if (value >= ((1 + 0.3) / parts.Count) - 1e-9 && !File.Exists(targets[1]))
            {
                File.WriteAllBytes(targets[1], foreign);
            }
        });

        var ex = await Assert.ThrowsAsync<DocumentSplitException>(() =>
            DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite: false, progress, Ct, written));

        Assert.Equal(DocumentSplitError.TargetExists, ex.Error);
        Assert.Equal(1, ex.PartIndex);
        Assert.Equal([targets[1]], ex.Paths);
        Assert.Equal(SaveFailureReason.TargetExists, Assert.IsType<SaveFailureException>(ex.InnerException).Reason);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(targets[1], Ct));
        Assert.Equal([targets[0]], written);
        Assert.Equal(targets.Take(2).Order(StringComparer.OrdinalIgnoreCase), Directory.GetFiles(directory).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WriteWithOverwriteStillReplacesAFileThatAppearsMeanwhile()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "pages-marked.pdf");
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, null, Ct);
        var parts = DocumentSplitter.Plan(6, SplitMode.Every(2), "p");
        var targets = DocumentSplitter.TargetPaths(parts, directory);
        var progress = new SyncProgress([], value =>
        {
            if (value >= ((1 + 0.3) / parts.Count) - 1e-9 && !File.Exists(targets[1]))
            {
                File.WriteAllText(targets[1], "foreign");
            }
        });

        var written = await DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, null), parts, directory, overwrite: true, progress, Ct);

        Assert.Equal(targets, written);
        await using var replaced = await engine.OpenAsync(targets[1], null, Ct);
        Assert.Equal(2, replaced.PageCount);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("\u6F22")]
    [InlineData("\U0001F600")]
    public void LongBaseNamesKeepTheirSuffixesDistinctAndUnique(string unit)
    {
        var baseName = string.Concat(Enumerable.Repeat(unit, 100));
        using var folder = new TempFolder();
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        var outline = new[] { new PdfOutlineItem("A", 2, false, []), new PdfOutlineItem("A", 4, false, []) };

        var every = DocumentSplitter.Plan(6, SplitMode.Every(2), baseName);
        var ranges = DocumentSplitter.Plan(6, SplitMode.Ranges("1-3,4,5-6"), baseName);
        var bookmarks = DocumentSplitter.Plan(6, SplitMode.Bookmarks(outline), baseName);
        var many = DocumentSplitter.Plan(DocumentSplitter.MaxParts, SplitMode.Every(1), baseName);

        AssertSuffixes(directory, every, ["-p001-002.pdf", "-p003-004.pdf", "-p005-006.pdf"]);
        AssertSuffixes(directory, ranges, ["-p001-003.pdf", "-p004.pdf", "-p005-006.pdf"]);
        AssertSuffixes(directory, bookmarks, ["-front.pdf", string.Empty, string.Empty]);

        // The widest page numbers (five digits) and 20,000 parts: still all different, all with their page in the name.
        var targets = DocumentSplitter.TargetPaths(many, directory);
        Assert.Equal(many.Count, targets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.EndsWith("-p00001.pdf", targets[0], StringComparison.Ordinal);
        Assert.EndsWith("-p20000.pdf", targets[^1], StringComparison.Ordinal);
        AssertInside(directory, targets.Take(50).Concat(targets.TakeLast(50)));
    }

    private static void AssertSuffixes(string directory, IReadOnlyList<SplitPart> parts, string[] suffixes)
    {
        var targets = DocumentSplitter.TargetPaths(parts, directory);
        Assert.Equal(suffixes.Length, targets.Count);
        Assert.Equal(targets.Count, targets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        for (var i = 0; i < suffixes.Length; i++)
        {
            Assert.EndsWith(suffixes[i], targets[i], StringComparison.Ordinal);
        }

        AssertInside(directory, targets);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task PartsOfAnEncryptedFileStayEncrypted(string fixture)
    {
        using var folder = new TempFolder();
        var path = Copy(folder, fixture);
        var directory = Directory.CreateDirectory(Path.Combine(folder.Path, "out")).FullName;
        await using var document = await engine.OpenAsync(path, UserPassword, Ct);
        var parts = DocumentSplitter.Plan(document.PageCount, SplitMode.Every(1), "enc");

        var written = await DocumentSplitter.WriteAsync(engine, document, new PageSourceFile(path, UserPassword), parts, directory, overwrite: false, progress: null, Ct);

        var file = Assert.Single(written);
        var without = await Assert.ThrowsAsync<PdfOpenException>(() => engine.OpenAsync(file, null, Ct));
        Assert.Equal(PdfOpenError.PasswordRequired, without.Error);
        await using var with = await engine.OpenAsync(file, UserPassword, Ct);
        Assert.Equal(1, with.PageCount);
    }

    private static List<SplitPart> HostileParts(int count)
    {
        string[] titles =
        [
            "..\\..\\x",
            "../../x",
            "a/b",
            "CON",
            "NUL.pdf",
            "aux",
            "trailing dots...",
            "trailing spaces   ",
            string.Empty,
            new string('x', 5000),
            "same",
            "SAME",
            "same",
            "e\u0301\u0301\u0301 caf\u00E9 \U0001F600 \u202Eevil\u202C\u0000\u001F\u007F",
            "C:\\Windows\\x",
            "/etc/passwd",
        ];
        var parts = new List<SplitPart>();
        for (var i = 0; i < count; i++)
        {
            parts.Add(new SplitPart(titles[i % titles.Length], [i % 6]));
        }

        return parts;
    }

    private static void AssertInside(string directory, IEnumerable<string> files)
    {
        var root = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var file in files)
        {
            Assert.Equal(file, Path.GetFullPath(file));
            Assert.True(string.Equals(root, Path.GetDirectoryName(file), comparison), $"'{file}' is not directly inside '{root}'.");
            var name = Path.GetFileName(file);
            Assert.True(name.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) < 0, $"'{name}' has an invalid character.");
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= 255, $"'{name}' is longer than 255 bytes.");
        }
    }

    [Fact]
    public async Task TheSourceOfASessionIsItsOpenDocumentWithItsFileAndPassword()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "encrypted_hello_world_r3.pdf");
        using var session = new ViewerSession(engine);

        // Nothing open: no source.
        Assert.Null(DocumentSplitter.SourceOf(session));

        await session.OpenAsync(path, UserPassword);
        var source = DocumentSplitter.SourceOf(session);

        Assert.NotNull(source);
        Assert.Same(session.Document, source.Document);
        Assert.Equal(Path.GetFullPath(path), source.File.Path);
        Assert.Equal(UserPassword, source.File.Password);
        Assert.Equal(session.Document!.SourceStampAtOpen, source.File.Stamp);
        Assert.DoesNotContain(UserPassword, source.ToString(), StringComparison.Ordinal);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Copy(TempFolder folder, string fixture)
    {
        var path = Path.Combine(folder.Path, fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    /// <summary>Reports synchronously (the framework's <see cref="Progress{T}"/> posts to another thread).</summary>
    private sealed class SyncProgress(List<double> values, Action<double>? also = null) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (values)
            {
                values.Add(value);
            }

            also?.Invoke(value);
        }
    }
}
