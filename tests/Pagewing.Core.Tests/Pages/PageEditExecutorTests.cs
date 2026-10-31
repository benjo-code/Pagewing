using System.Security.Cryptography;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pages;

/// <summary>The working copy of a page plan (milestone 13): the plan is executed on a copy of the primary file, never on the shown document.</summary>
public sealed class PageEditExecutorTests
{
    private static readonly PageSourceId First = new(1);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Copies a test PDF into the temporary folder so that nothing ever touches the checked-in file.</summary>
    internal static PageSourceFile CopyOf(TempFolder folder, string name, string? password = null)
    {
        var target = Path.Combine(folder.Path, Guid.NewGuid().ToString("N") + "-" + name);
        File.Copy(TestData.Pdf(name), target);
        return new PageSourceFile(target, password);
    }

    internal static IReadOnlyDictionary<PageSourceId, PageSourceFile> None { get; } = new Dictionary<PageSourceId, PageSourceFile>();

    private static async Task<string[]> TextsOf(PdfDocument document)
    {
        var texts = new string[document.PageCount];
        for (var i = 0; i < texts.Length; i++)
        {
            texts[i] = (await document.GetTextAsync(i, Cancel)).Trim();
        }

        return texts;
    }

    private static async Task<int[]> RotationsOf(PdfDocument document)
    {
        var rotations = new int[document.PageCount];
        for (var i = 0; i < rotations.Length; i++)
        {
            rotations[i] = await document.GetPageRotationAsync(i, Cancel);
        }

        return rotations;
    }

    /// <summary>Writes the working copy completely and opens the result again (what the saver of the next step does).</summary>
    internal static async Task<(PdfDocument Document, string Path)> RewriteAndReopenAsync(PdfEngine engine, PdfDocument copy, TempFolder folder, string? password = null)
    {
        var path = Path.Combine(folder.Path, "result-" + Guid.NewGuid().ToString("N") + ".pdf");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            await copy.SaveRewriteAsync(stream, Cancel);
        }

        return (await engine.OpenAsync(path, password, Cancel), path);
    }

    [Fact]
    public async Task DeleteMoveRotateGivesTheOrderAndRotationOfThePlan()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");

        // PAGE 1..6; page 5 is turned by /Rotate 90 in the file.
        var plan = PageEditPlan.Identity(6)
            .Delete([1, 3])
            .Move([1], 0)
            .Rotate([0], 90)
            .Rotate([1], 180)
            .Rotate([2], 270)
            .Rotate([3], 90);
        Assert.Equal([2, 0, 4, 5], plan.Slots.Select(s => s.SourceIndex));

        using var copy = await executor.BuildAsync(plan, primary, None, progress: null, Cancel);

        Assert.Equal(4, copy.PageCount);
        Assert.Equal(["PAGE 3", "PAGE 1", "PAGE 5", "PAGE 6"], await TextsOf(copy));

        // The rotation is absolute: the page's own plus the extra of the plan (page 5: 90 + 270 = 0).
        var rotations = await RotationsOf(copy);
        Assert.Equal([90, 180, 0, 90], rotations);
    }

    [Fact]
    public async Task OutlineLabelsAndInfoOfThePrimaryFileSurvive()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");

        // Page 2 (target of "Abschnitt 1.1") and page 6 are deleted, page 4 comes to the front.
        var plan = PageEditPlan.Identity(6).Delete([1, 5]).Move([2], 0);
        using var copy = await executor.BuildAsync(plan, primary, None, null, Cancel);
        var (result, resultPath) = await RewriteAndReopenAsync(engine, copy, folder);
        using var resultScope = result;

        Assert.Equal(["PAGE 4", "PAGE 1", "PAGE 3", "PAGE 5"], await TextsOf(result));

        var outline = await result.GetOutlineAsync(Cancel);
        Assert.Equal(["Kapitel 1", "Kapitel 2", "Kapitel 3"], outline.Select(i => i.Title));
        Assert.Equal([1, 2, 3], outline.Select(i => i.PageIndex));

        // The bookmark of the deleted page stays as an entry without a target.
        var child = Assert.Single(outline[0].Children);
        Assert.Equal("Abschnitt 1.1", child.Title);
        Assert.Null(child.PageIndex);

        // The page labels are a list by position and stay as they were; the title stays.
        var raw = string.Concat((await File.ReadAllTextAsync(resultPath, System.Text.Encoding.Latin1, Cancel)).Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains("/PageLabels<</Nums[0<</S/r>>2<</S/D/St3>>]>>", raw, StringComparison.Ordinal);
        using var original = await engine.OpenAsync(TestData.Pdf("pages-marked.pdf"), null, Cancel);
        var title = (await original.GetMetadataAsync(Cancel)).Title;
        Assert.False(string.IsNullOrEmpty(title));
        Assert.Equal(title, (await result.GetMetadataAsync(Cancel)).Title);
    }

    [Fact]
    public async Task DuplicateGivesTwoPagesWithTheSameText()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");

        // Page 3 (landscape) and page 5 (turned in the file) are duplicated; the copies are turned further.
        var plan = PageEditPlan.Identity(6).Duplicate([2, 4]).Rotate([3], 90);
        Assert.Equal([0, 1, 2, 2, 3, 4, 4, 5], plan.Slots.Select(s => s.SourceIndex));

        using var copy = await executor.BuildAsync(plan, primary, None, null, Cancel);

        Assert.Equal(["PAGE 1", "PAGE 2", "PAGE 3", "PAGE 3", "PAGE 4", "PAGE 5", "PAGE 5", "PAGE 6"], await TextsOf(copy));
        var rotations = await RotationsOf(copy);
        Assert.Equal([0, 0, 0, 90, 0, 90, 90, 0], rotations);
    }

    [Fact]
    public async Task InsertFromAnotherFileAppendsItsPages()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");
        var rich = CopyOf(folder, "text-rich.pdf");

        string[] richTexts;
        await using (var richDocument = await engine.OpenAsync(rich.Path, null, Cancel))
        {
            richTexts = await TextsOf(richDocument);
        }

        // Page two of the other file, then page one, between PAGE 2 and PAGE 3; page one of the other file once more at the end.
        var plan = PageEditPlan.Identity(6)
            .InsertFrom(First, [1, 0], 2)
            .InsertFrom(First, [0], 8);
        using var copy = await executor.BuildAsync(plan, primary, new Dictionary<PageSourceId, PageSourceFile> { [First] = rich }, null, Cancel);

        var texts = await TextsOf(copy);
        Assert.Equal(9, texts.Length);
        Assert.Equal(["PAGE 1", "PAGE 2"], texts[..2]);
        Assert.Equal(richTexts[1], texts[2]);
        Assert.Equal(richTexts[0], texts[3]);
        Assert.Equal(["PAGE 3", "PAGE 4", "PAGE 5", "PAGE 6"], texts[4..8]);
        Assert.Equal(richTexts[0], texts[8]);

        // The pages are copies: they survive the other file being closed, written out and read back.
        var (result, _) = await RewriteAndReopenAsync(engine, copy, folder);
        using var resultScope = result;
        Assert.Equal(texts, await TextsOf(result));
    }

    [Fact]
    public async Task APlanOfBlankPagesOnlyBuildsThem()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");

        // Every page of the file is deleted; two blank pages of different sizes are all that is left.
        var plan = PageEditPlan.Identity(6).InsertBlank(0, new BlankPage(120, 80)).InsertBlank(1, new BlankPage(90, 70)).Delete([2, 3, 4, 5, 6, 7]);
        Assert.Equal(2, plan.Slots.Count);

        using var copy = await executor.BuildAsync(plan, primary, None, null, Cancel);

        Assert.Equal(2, copy.PageCount);
        var first = await copy.GetPageSizeAsync(0, Cancel);
        var second = await copy.GetPageSizeAsync(1, Cancel);
        Assert.Equal((120, 80, 90, 70), (Math.Round(first.Width), Math.Round(first.Height), Math.Round(second.Width), Math.Round(second.Height)));
    }

    [Fact]
    public async Task InsertBlankHasTheGivenSizeAndNoText()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");

        var plan = PageEditPlan.Identity(6).InsertBlank(0, new BlankPage(200, 100)).InsertBlank(7, new BlankPage(300, 400)).Rotate([7], 90);
        using var copy = await executor.BuildAsync(plan, primary, None, null, Cancel);

        Assert.Equal(8, copy.PageCount);
        Assert.Equal(string.Empty, (await TextsOf(copy))[0]);
        Assert.Equal("PAGE 1", (await TextsOf(copy))[1]);
        Assert.Equal(string.Empty, (await TextsOf(copy))[7]);
        var first = await copy.GetPageSizeAsync(0, Cancel);
        Assert.Equal((200, 100), (Math.Round(first.Width), Math.Round(first.Height)));

        // A turned blank page is displayed with its sides swapped.
        Assert.Equal(90, await copy.GetPageRotationAsync(7, Cancel));
        var last = await copy.GetPageSizeAsync(7, Cancel);
        Assert.Equal((400, 300), (Math.Round(last.Width), Math.Round(last.Height)));
    }

    [Fact]
    public async Task BuildDoesNotTouchTheOriginalFile()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");
        var rich = CopyOf(folder, "text-rich.pdf");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(primary.Path, Cancel));
        var richBefore = SHA256.HashData(await File.ReadAllBytesAsync(rich.Path, Cancel));

        var plan = PageEditPlan.Identity(6).Delete([0]).Duplicate([1]).InsertFrom(First, [0, 1], 3).InsertBlank(1, new BlankPage(100, 100)).Rotate([2], 90);
        using (var copy = await executor.BuildAsync(plan, primary, new Dictionary<PageSourceId, PageSourceFile> { [First] = rich }, null, Cancel))
        {
            await using var sink = new MemoryStream();
            await copy.SaveRewriteAsync(sink, Cancel);
            Assert.True(sink.Length > 0);
        }

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(primary.Path, Cancel)));
        Assert.Equal(richBefore, SHA256.HashData(await File.ReadAllBytesAsync(rich.Path, Cancel)));
    }

    [Fact]
    public async Task BuildIsCancelableBetweenBatches()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        PdfDocument? opened = null;
        var executor = new PageEditExecutor(engine) { WorkingCopyOpened = copy => opened = copy };
        var primary = CopyOf(folder, "pages-marked.pdf");
        var dense = CopyOf(folder, "text-dense.pdf");

        // 120 pages are three batches of 50, 50 and 20.
        var plan = PageEditPlan.Identity(6).InsertFrom(First, [.. Enumerable.Range(0, 120)], 6);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var progress = new SynchronousProgress(value =>
        {
            if (value > 0)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.BuildAsync(plan, primary, new Dictionary<PageSourceId, PageSourceFile> { [First] = dense }, progress, cancellation.Token));

        // Nothing was reported after the cancel, and the working copy was released.
        Assert.Equal(1, progress.Count);
        Assert.NotNull(opened);
        Assert.Throws<ObjectDisposedException>(() => opened.PageCount);
    }

    [Fact]
    public async Task ReorderOfMoreThanOneBatchKeepsThePlanOrder()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "thousand-pages.pdf");

        string[] original;
        await using (var document = await engine.OpenAsync(primary.Path, null, Cancel))
        {
            original = await TextsOf(document);
        }

        // 1000 pages ("Page N"), each with its own text; the plan moves a block to the front, scatters pages to the end, drops some and copies some.
        Assert.Equal(1000, original.Distinct().Count());
        var plan = PageEditPlan.Identity(1000)
            .Move([.. Enumerable.Range(700, 300)], 0)
            .Move([.. Enumerable.Range(0, 40).Select(i => i * 5)], 1000)
            .Delete([.. Enumerable.Range(0, 20).Select(i => i * 7)])
            .Duplicate([3, 77, 120]);
        using var copy = await executor.BuildAsync(plan, primary, None, null, Cancel);

        var texts = await TextsOf(copy);
        Assert.Equal(plan.Slots.Select(slot => original[slot.SourceIndex]), texts);
    }

    [Fact]
    public async Task ImportsRunInBatchesAndProgressNeverGoesBack()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");
        var dense = CopyOf(folder, "text-dense.pdf");

        var plan = PageEditPlan.Identity(6).InsertFrom(First, [.. Enumerable.Range(0, 120)], 6);
        var values = new List<double>();
        var progress = new SynchronousProgress(values.Add);
        using var copy = await executor.BuildAsync(plan, primary, new Dictionary<PageSourceId, PageSourceFile> { [First] = dense }, progress, Cancel);

        Assert.Equal(126, copy.PageCount);
        Assert.True(values.Count >= 3, "Three batches of pages are three reports at least.");
        Assert.Equal(values.Order(), values);
        Assert.Equal(1.0, values[^1]);
    }

    [Fact]
    public async Task BuildOfFiftyCopiesStaysUnderTheLimit()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");

        var plan = PageEditPlan.Identity(6);
        for (var i = 0; i < 50; i++)
        {
            plan = plan.Duplicate([0]);
        }

        using (var copy = await executor.BuildAsync(plan, primary, None, null, Cancel))
        {
            Assert.Equal(56, copy.PageCount);
            var texts = await TextsOf(copy);
            Assert.Equal(51, texts.Count(t => t == "PAGE 1"));
        }

        // More files than a plan may take pages from is refused before any file is opened.
        var opened = 0;
        var counting = new PageEditExecutor(engine) { WorkingCopyOpened = _ => opened++ };
        var tooMany = Enumerable.Range(1, PageEditLimits.MaxInsertedFiles + 1)
            .ToDictionary(n => new PageSourceId(n), n => new PageSourceFile(Path.Combine(folder.Path, $"missing-{n}.pdf"), null));
        var error = await Assert.ThrowsAsync<PageEditException>(() => counting.BuildAsync(plan, primary, tooMany, null, Cancel));
        Assert.Equal(PageEditError.LimitExceeded, error.Error);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task WrongPasswordOfAnInsertedFileFails()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        PdfDocument? opened = null;
        var executor = new PageEditExecutor(engine) { WorkingCopyOpened = copy => opened = copy };
        var primary = CopyOf(folder, "pages-marked.pdf");
        var plan = PageEditPlan.Identity(6).InsertFrom(First, [0], 6);

        var wrong = CopyOf(folder, "encrypted_hello_world_r3.pdf", "wrong");
        var failure = await Assert.ThrowsAsync<PdfOpenException>(
            () => executor.BuildAsync(plan, primary, new Dictionary<PageSourceId, PageSourceFile> { [First] = wrong }, null, Cancel));
        Assert.Equal(PdfOpenError.PasswordIncorrect, failure.Error);

        // The working copy opened before the failure is released.
        Assert.NotNull(opened);
        Assert.Throws<ObjectDisposedException>(() => opened.PageCount);

        // With the right password the same plan works.
        var right = CopyOf(folder, "encrypted_hello_world_r3.pdf", "hôtel");
        using var copy = await new PageEditExecutor(engine).BuildAsync(plan, primary, new Dictionary<PageSourceId, PageSourceFile> { [First] = right }, null, Cancel);
        Assert.Equal(7, copy.PageCount);
    }

    [Fact]
    public async Task SourceIndexBeyondTheFileFailsClearly()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        PdfDocument? opened = null;
        var executor = new PageEditExecutor(engine) { WorkingCopyOpened = copy => opened = copy };
        var primary = CopyOf(folder, "pages-marked.pdf");
        var rich = CopyOf(folder, "text-rich.pdf");
        var sources = new Dictionary<PageSourceId, PageSourceFile> { [First] = rich };

        // The plan cannot know how many pages the other file has: two, but the plan asks for the hundredth.
        var plan = PageEditPlan.Identity(6).InsertFrom(First, [0, 99], 6);
        var error = await Assert.ThrowsAsync<PageEditException>(() => executor.BuildAsync(plan, primary, sources, null, Cancel));
        Assert.Equal(PageEditError.OutOfRange, error.Error);
        Assert.Throws<ObjectDisposedException>(() => opened!.PageCount);

        // A plan for another page count than the file has (the file changed since the plan was made).
        error = await Assert.ThrowsAsync<PageEditException>(() => executor.BuildAsync(PageEditPlan.Identity(7), primary, None, null, Cancel));
        Assert.Equal(PageEditError.OutOfRange, error.Error);

        // A source without a file.
        error = await Assert.ThrowsAsync<PageEditException>(() => executor.BuildAsync(plan, primary, None, null, Cancel));
        Assert.Equal(PageEditError.OutOfRange, error.Error);
    }

    [Fact]
    public async Task WorkingCopyOfAFormDocumentIsWrittenAndReadBack()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "forms-basic.pdf");

        var plan = PageEditPlan.Identity(2).Duplicate([0]).Move([2], 0);
        using var copy = await executor.BuildAsync(plan, primary, None, null, Cancel);
        var (result, _) = await RewriteAndReopenAsync(engine, copy, folder);
        using var resultScope = result;

        Assert.Equal(3, result.PageCount);
        Assert.True(result.HasAcroForm);
    }

    [Fact]
    public async Task LinkToADeletedPageLosesItsTarget()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "text-rich.pdf");

        // Page one links to page two.
        await using (var original = await engine.OpenAsync(primary.Path, null, Cancel))
        {
            Assert.Contains((await original.GetPageTextAsync(0, Cancel)).Links, link => link.Target is PageTarget { PageIndex: 1 });
        }

        using var copy = await executor.BuildAsync(PageEditPlan.Identity(2).Delete([1]), primary, None, null, Cancel);
        var (result, _) = await RewriteAndReopenAsync(engine, copy, folder);
        using var resultScope = result;

        // The link is not turned to another page; it points nowhere, and Pagewing does not follow it.
        Assert.DoesNotContain((await result.GetPageTextAsync(0, Cancel)).Links, link => link.Target is PageTarget);
    }

    [Fact]
    public async Task AMixedPlanGivesTheTextRotationAndBlankSizeOfEverySlot()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        var executor = new PageEditExecutor(engine);
        var primary = CopyOf(folder, "pages-marked.pdf");
        var thousand = CopyOf(folder, "thousand-pages.pdf");
        var rich = CopyOf(folder, "text-rich.pdf");
        var marked = CopyOf(folder, "pages-marked.pdf");
        var one = new PageSourceId(1);
        var two = new PageSourceId(2);
        var three = new PageSourceId(3);

        // Three foreign files interleaved (one of them with 60 pages: more than one import batch), copies of primary and of
        // foreign pages, two blank pages, deletions, a block moved, and turns on pages of every kind including the blanks.
        var plan = PageEditPlan.Identity(6)
            .InsertFrom(one, [.. Enumerable.Range(100, 60)], 2)
            .InsertFrom(two, [1, 0, 1], 10)
            .InsertFrom(three, [5, 3, 4], 0)
            .Duplicate([0, 20, 70])
            .InsertBlank(4, new BlankPage(200, 100))
            .InsertBlank(30, new BlankPage(150, 250))
            .Delete([7, 8, 9, 40])
            .Move([1, 2, 3, 50, 51], 20);
        var blankPositions = Enumerable.Range(0, plan.Slots.Count).Where(i => plan.Slots[i].Blank is not null).ToArray();
        Assert.Equal(2, blankPositions.Length);
        plan = plan.Rotate([blankPositions[0]], 90).Rotate([blankPositions[1], 3, 21, 60], 180).Rotate([5, 22, 61], 270).Rotate([21, 6], 90);

        var files = new Dictionary<PageSourceId, PageSourceFile> { [one] = thousand, [two] = rich, [three] = marked };
        using var copy = await executor.BuildAsync(plan, primary, files, null, Cancel);
        Assert.Equal(plan.Slots.Count, copy.PageCount);

        var originals = new Dictionary<int, PdfDocument>
        {
            [0] = await engine.OpenAsync(primary.Path, null, Cancel),
            [1] = await engine.OpenAsync(thousand.Path, null, Cancel),
            [2] = await engine.OpenAsync(rich.Path, null, Cancel),
            [3] = await engine.OpenAsync(marked.Path, null, Cancel),
        };
        try
        {
            for (var i = 0; i < plan.Slots.Count; i++)
            {
                var slot = plan.Slots[i];
                var text = (await copy.GetTextAsync(i, Cancel)).Trim();
                var rotation = await copy.GetPageRotationAsync(i, Cancel);
                var size = await copy.GetPageSizeAsync(i, Cancel);
                if (slot.Blank is { } blank)
                {
                    Assert.Equal(string.Empty, text);
                    Assert.Equal(slot.ExtraRotation, rotation);
                    var turned = slot.ExtraRotation is 90 or 270;
                    Assert.Equal(turned ? (blank.Height, blank.Width) : (blank.Width, blank.Height), (Math.Round(size.Width), Math.Round(size.Height)));
                    continue;
                }

                var source = originals[slot.Source.Value];
                Assert.Equal((await source.GetTextAsync(slot.SourceIndex, Cancel)).Trim(), text);
                Assert.Equal(((await source.GetPageRotationAsync(slot.SourceIndex, Cancel)) + slot.ExtraRotation) % 360, rotation);
            }
        }
        finally
        {
            foreach (var original in originals.Values)
            {
                original.Dispose();
            }
        }
    }

    [Fact]
    public async Task HugeListsAreRefusedBeforeAnythingIsRead()
    {
        using var folder = new TempFolder();
        var engine = new PdfEngine();
        using var document = await engine.OpenAsync(CopyOf(folder, "pages-marked.pdf").Path, null, Cancel);

        var huge = new CountOnlyList(PageEditLimits.MaxSlots + 1);
        var error = await Assert.ThrowsAsync<PageEditException>(() => document.ImportPagesAsync(document, huge, 0, Cancel));
        Assert.Equal(PageEditError.LimitExceeded, error.Error);
        await Assert.ThrowsAsync<PageEditException>(() => document.DeletePagesAsync(huge, Cancel));

        // A list of any size is cut into batches; pages beyond the source and a destination beyond the end are refused up front.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.ImportPagesAsync(document, [0, 6], 0, Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.ImportPagesAsync(document, [0], 7, Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => document.DeletePagesAsync([0, 1, 2, 3, 4, 5], Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => document.MovePagesAsync([.. Enumerable.Range(0, PageEditLimits.ImportBatch + 1)], 0, Cancel));
        Assert.Equal(6, document.PageCount);
    }

    /// <summary>A list that claims a size and fails if anyone reads it.</summary>
    private sealed class CountOnlyList(int count) : IReadOnlyList<int>
    {
        public int Count => count;

        public int this[int index] => throw new InvalidOperationException("The list must not be read.");

        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("The list must not be enumerated.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public int Count { get; private set; }

        public void Report(double value)
        {
            Count++;
            report(value);
        }
    }
}
