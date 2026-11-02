using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// Pages that become images, from the analysis to the checked result (milestone 14.1, task 3): the pass skips them, the build renders
/// them from the unchanged original and paints the marks into the bitmap, and the expectation counts their text as gone. The files are
/// copies in a temp folder.
/// </summary>
public sealed class RedactRasterApplyTests : IDisposable
{
    private const string Term = "Zephyrstraße 17";
    private const double Scale = 200 / 72.0;

    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    // ------------------------------------------------------------------ helpers

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(folder.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Target(string name = "result.pdf") => Path.Combine(folder.Path, name);

    private static string[] LeftOver(string directory, params string[] expected) =>
        [.. Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Where(n => !expected.Contains(n)).Select(static n => n!)];

    private static PageRect Union(IEnumerable<PageRect> boxes) =>
        boxes.Aggregate(static (a, b) => new PageRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));

    /// <summary>Adds a mark over every occurrence of the term on the page (case sensitive), over the boxes of its characters.</summary>
    private static async Task<RedactionPlan> WithTermOnAsync(RedactionPlan plan, PdfDocument document, int page, string term, bool everywhere)
    {
        var text = await document.GetPageTextAsync(page, Ct);
        foreach (var match in TextMatcher.Find(text.Chars, term, new SearchOptions(MatchCase: true)))
        {
            var boxes = text.Chars.Skip(match.FirstChar).Take(match.LastChar - match.FirstChar + 1).Where(static c => !c.IsGenerated).Select(static c => c.Box);
            plan = plan.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Hit, page, [Union(boxes)], term, everywhere));
        }

        return plan;
    }

    private static RedactionPlan WithArea(RedactionPlan plan, int page, PageRect area) =>
        plan.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, page, [area], null, false));

    private static async Task<string> TextOf(PdfDocument document, int page) =>
        string.Concat((await document.GetPageTextAsync(page, Ct)).Chars.Select(static c => c.Text));

    private async Task<(ViewerSession Session, RedactionPlan Plan)> OpenAsync(string path, Func<PdfDocument, Task<RedactionPlan>> plan)
    {
        var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        return (session, await plan(session.Document!));
    }

    /// <summary>Two pages that both hold the secret, the first with a public line.</summary>
    private string TwoPageFile(string name = "two.pdf") =>
        Write(name, new VerifierPdf("BT /F1 12 Tf 20 150 Td (secret value) Tj ET BT /F1 12 Tf 20 100 Td (public line) Tj ET", "BT /F1 12 Tf 20 150 Td (secret value) Tj ET BT /F1 12 Tf 20 100 Td (second page) Tj ET").Build());

    // ------------------------------------------------------------------ the choice rescues a page

    [Fact]
    public async Task RejectedPageIsStillRefusedWithoutTheChoice()
    {
        // Page one of the fixture paints with a tiling pattern: PDFium cannot redact around it.
        var path = Copy("redact-everywhere.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = await WithTermOnAsync(RedactionPlan.Empty, shown, 0, Term, everywhere: false);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);

        Assert.True(analysis.IsRefused);
        var refusal = Assert.Single(analysis.Refusals);
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, refusal.Kind);
        Assert.Equal([0], refusal.Pages);
        Assert.Null(analysis.Raster);

        // A caller that did not look is refused all the same, and nothing is written.
        var target = Target();
        await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, target), null, Ct));
        Assert.False(File.Exists(target));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public async Task RejectedPageIsRescuedByTheChoice()
    {
        var path = Copy("redact-everywhere.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = (await WithTermOnAsync(RedactionPlan.Empty, shown, 0, Term, everywhere: false)).WithRasterPage(0, on: true);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);

        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        var info = Assert.Single(analysis.Raster!);
        Assert.Equal(0, info.Page);
        Assert.Equal(200, info.Dpi);
        Assert.False(info.Lowered);
        Assert.True(info.HasText);
        Assert.True(info.Annotations > 0);
        Assert.Equal(1, info.FormFields);
        Assert.Equal(834, info.WidthPx);
        Assert.Equal(1112, info.HeightPx);
        Assert.True(info.EstimatedBytes > 0);

        var target = Target();
        var result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, target), null, Ct);

        await using var reopened = result.Saved.Reopened;
        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        Assert.Equal(2, reopened.PageCount);
        Assert.Equal(string.Empty, (await TextOf(reopened, 0)).Trim());
        Assert.Equal(new PageObjectSummary(Images: 1, Paths: 0, Texts: 0, Others: 0, Annotations: 0), await reopened.GetPageObjectSummaryAsync(0, Ct));

        // The other page keeps its text and its vectors.
        Assert.Contains("Seite zwei", await TextOf(reopened, 1), StringComparison.Ordinal);
        Assert.True((await reopened.GetPageObjectSummaryAsync(1, Ct)).Texts > 0);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.Empty(LeftOver(folder.Path, "redact-everywhere.pdf", "result.pdf"));
    }

    [Fact]
    public async Task RasterPageWithoutMarkIsValid()
    {
        var path = TwoPageFile();
        var plan = RedactionPlan.Empty.WithRasterPage(1, on: true);
        Assert.False(plan.IsEmpty);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, session.Document!, path, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        Assert.Equal(1, Assert.Single(analysis.Raster!).Page);
        Assert.Empty(analysis.Pages);

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct);

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        Assert.Equal(string.Empty, (await TextOf(session.Document!, 1)).Trim());
        Assert.Equal(1, (await session.Document!.GetPageObjectSummaryAsync(1, Ct)).Images);
        Assert.Contains("public line", await TextOf(session.Document!, 0), StringComparison.Ordinal);

        // Nothing was removed as text, and the report says so.
        Assert.Contains(result.Check.Items, static item => item.Kind == RedactionNotCheckedKind.RawNothingRemoved);
    }

    [Fact]
    public async Task APageThatDoesNotExistCannotBeChosen()
    {
        var path = TwoPageFile();
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = RedactionPlan.Empty.WithRasterPage(5, on: true);

        var error = await Assert.ThrowsAsync<RedactionException>(() => new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct));
        Assert.Equal(RedactionError.InvalidArea, error.Error);
        await Assert.ThrowsAsync<RedactionException>(() => new RedactionExecutor(engine).BuildAsync(plan, shown, path, null, null, Ct));
    }

    // ------------------------------------------------------------------ the bitmap

    [Fact]
    public async Task AreasAreFilledInTheBitmap()
    {
        var path = TwoPageFile();
        var area = new PageRect(15, 35, 115.3, 60.2);
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = WithArea(RedactionPlan.Empty, 0, area).WithRasterPage(0, on: true);
        var expected = await shown.RenderPageAsync(0, Scale, new RenderOptions { Priority = PdfPriority.Background }, Ct);

        var result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, Target()), null, Ct);
        await using var reopened = result.Saved.Reopened;

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var whole = Assert.Single(await reopened.GetImageAreaPixelsAsync(0, new PageRect(0, 0, 300, 200), Ct));
        Assert.Equal(expected.Width, whole.Width);
        Assert.Equal(expected.Height, whole.Height);

        // The area is rounded outward to whole pixels, with no smoothing: every pixel it touches is black, every other one is the original.
        var sx = expected.Width / 300.0;
        var sy = expected.Height / 200.0;
        int left = (int)Math.Floor(area.Left * sx), right = (int)Math.Ceiling(area.Right * sx);
        int top = (int)Math.Floor(area.Top * sy), bottom = (int)Math.Ceiling(area.Bottom * sy);
        var originalDarkInside = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var at = ((y * whole.Width) + x) * 4;
                var inside = x >= left && x < right && y >= top && y < bottom;
                var source = (y * expected.Stride) + (x * 4);
                if (inside)
                {
                    Assert.Equal(0, whole.Bgra[at] | whole.Bgra[at + 1] | whole.Bgra[at + 2]);
                    Assert.Equal(255, whole.Bgra[at + 3]);
                    originalDarkInside += expected.Pixels.Span[source] < 128 ? 1 : 0;
                }
                else
                {
                    Assert.True(
                        Math.Abs(whole.Bgra[at] - expected.Pixels.Span[source]) <= 1
                        && Math.Abs(whole.Bgra[at + 1] - expected.Pixels.Span[source + 1]) <= 1
                        && Math.Abs(whole.Bgra[at + 2] - expected.Pixels.Span[source + 2]) <= 1,
                        $"Pixel {x},{y} differs from the original outside the area.");
                }
            }
        }

        Assert.True(originalDarkInside > 0, "The area covered no text, so the test proves nothing.");
    }

    [Fact]
    public async Task RasterAreasTakeTheChosenColor()
    {
        // The whole way: the plan's colour is painted into the bitmap, and the check of the written file expects exactly that colour.
        var path = TwoPageFile();
        var area = new PageRect(15, 35, 115.3, 60.2);
        var chosen = new RedactionFill(0x33, 0x66, 0x99);
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = WithArea(RedactionPlan.Empty, 0, area).WithRasterPage(0, on: true).WithFill(chosen);

        var result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, Target()), null, Ct);
        await using var reopened = result.Saved.Reopened;

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var whole = Assert.Single(await reopened.GetImageAreaPixelsAsync(0, new PageRect(0, 0, 300, 200), Ct));
        var inside = 0;
        for (var y = (int)(whole.Height * area.Top / 200) + 1; y < (int)(whole.Height * area.Bottom / 200) - 1; y++)
        {
            for (var x = (int)(whole.Width * area.Left / 300) + 1; x < (int)(whole.Width * area.Right / 300) - 1; x++)
            {
                var at = ((y * whole.Width) + x) * 4;
                Assert.True(whole.Bgra[at] == 0x99 && whole.Bgra[at + 1] == 0x66 && whole.Bgra[at + 2] == 0x33 && whole.Bgra[at + 3] == 255, $"Pixel {x},{y}.");
                inside++;
            }
        }

        Assert.True(inside > 500);
    }

    [Fact]
    public async Task RotatedAndCroppedPageStaysUpright()
    {
        // MediaBox 400 x 300, CropBox [20 10 380 290], /Rotate 90: shown as 280 x 360 with a red marker at the top left.
        var path = Copy("redact-rotated.pdf");
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var size = await shown.GetPageSizeAsync(0, Ct);
        Assert.Equal(280, size.Width, 0.01);
        Assert.Equal(360, size.Height, 0.01);
        var plan = (await WithTermOnAsync(RedactionPlan.Empty, shown, 0, Term, everywhere: false)).WithRasterPage(0, on: true);
        Assert.Single(plan.Marks);
        var area = plan.Marks[0].Areas[0];
        var expected = await shown.RenderPageAsync(0, Scale, new RenderOptions { Priority = PdfPriority.Background }, Ct);
        RewriteRequest? seen = null;
        var hooks = new RedactionApplyHooks { RewriteStarting = (request, _, _) => seen = request };
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, hooks, Ct);

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var after = session.Document!;

        // The fingerprint expects what the user saw before: the displayed size, now upright.
        Assert.NotNull(seen);
        var fingerprint = seen.ExpectedPages[0];
        Assert.Equal(280, fingerprint.Width);
        Assert.Equal(360, fingerprint.Height);
        Assert.Equal(0, fingerprint.Rotation);
        Assert.False(fingerprint.CompareText);
        var written = await after.GetPageSizeAsync(0, Ct);
        Assert.Equal(size.Width, written.Width, 0.01);
        Assert.Equal(size.Height, written.Height, 0.01);
        Assert.Equal(0, await after.GetPageRotationAsync(0, Ct));

        // The pixels of the page are the page as shown: the marker is where it was, and only the area is black.
        var whole = Assert.Single(await after.GetImageAreaPixelsAsync(0, new PageRect(0, 0, size.Width, size.Height), Ct));
        Assert.Equal(expected.Width, whole.Width);
        Assert.Equal(expected.Height, whole.Height);
        var sx = expected.Width / size.Width;
        var sy = expected.Height / size.Height;
        int left = (int)Math.Floor(area.Left * sx), right = (int)Math.Ceiling(area.Right * sx);
        int top = (int)Math.Floor(area.Top * sy), bottom = (int)Math.Ceiling(area.Bottom * sy);
        var different = 0;
        var darkInside = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var at = ((y * whole.Width) + x) * 4;
                var source = (y * expected.Stride) + (x * 4);
                if (x >= left && x < right && y >= top && y < bottom)
                {
                    Assert.Equal(0, whole.Bgra[at] | whole.Bgra[at + 1] | whole.Bgra[at + 2]);
                    darkInside += expected.Pixels.Span[source + 1] < 128 ? 1 : 0;
                }
                else if (whole.Bgra[at] != expected.Pixels.Span[source] || whole.Bgra[at + 1] != expected.Pixels.Span[source + 1] || whole.Bgra[at + 2] != expected.Pixels.Span[source + 2])
                {
                    different++;
                }
            }
        }

        Assert.Equal(0, different);
        Assert.True(darkInside > 0, "The area covered no text.");

        // The marker of the CropBox corner is red at the top left of the shown page, in the original and in the result.
        var marker = ((20 * whole.Width) + 20) * 4;
        Assert.True(whole.Bgra[marker + 2] > 200 && whole.Bgra[marker + 1] < 60 && whole.Bgra[marker] < 60, "The top left corner is not the red marker.");
    }

    [Fact]
    public void PainterRoundsOutwardClipsAndDoesNotBlend()
    {
        var pixels = new byte[10 * 8 * 4];
        Array.Fill(pixels, (byte)200);

        // 1 point = 2 pixels in x and 1 in y; the area 1.2 .. 2.4 pt starts inside pixel 2 and ends inside pixel 4; the rows are 2 .. 3.
        RasterPainter.Fill(pixels, 10, 8, 2, 1, [new PageRect(1.2, 2, 2.4, 3.5), new PageRect(-50, 6.5, 500, 900), new PageRect(30, 30, 40, 40)], new RedactionFill(10, 20, 30));

        bool IsFilled(int x, int y) => pixels[((y * 10) + x) * 4] == 30 && pixels[(((y * 10) + x) * 4) + 1] == 20 && pixels[(((y * 10) + x) * 4) + 2] == 10 && pixels[(((y * 10) + x) * 4) + 3] == 255;
        bool IsUntouched(int x, int y) => pixels[((y * 10) + x) * 4] == 200 && pixels[(((y * 10) + x) * 4) + 3] == 200;

        for (var x = 0; x < 10; x++)
        {
            foreach (var y in new[] { 0, 1, 4, 5 })
            {
                Assert.True(IsUntouched(x, y), $"pixel {x},{y}");
            }

            foreach (var y in new[] { 2, 3 })
            {
                Assert.True(x is >= 2 and < 5 ? IsFilled(x, y) : IsUntouched(x, y), $"pixel {x},{y}");
            }

            // The second area is clipped to the bitmap.
            Assert.True(IsFilled(x, 6) && IsFilled(x, 7), $"pixel {x},6/7");
        }
    }

    // ------------------------------------------------------------------ the count of occurrences

    [Fact]
    public async Task OccurrencesOnRasterPagesCountAsRemoved()
    {
        var path = TwoPageFile();
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var marked = await WithTermOnAsync(RedactionPlan.Empty, shown, 1, "secret value", everywhere: false);

        // Page one holds the term too, but it becomes an image: its text is gone, so only page two is left to count.
        var withRaster = marked.WithRasterPage(0, on: true);
        var expectation = await RedactionExecutor.ExpectationAsync(withRaster, shown, Ct);
        var removed = Assert.Single(expectation.Removed);
        Assert.Equal(1, removed.OccurrencesBefore);
        Assert.Equal([0], expectation.RasterPages);

        // Without the choice both pages count.
        Assert.Equal(2, Assert.Single((await RedactionExecutor.ExpectationAsync(marked, shown, Ct)).Removed).OccurrencesBefore);
        Assert.Null((await RedactionExecutor.ExpectationAsync(marked, shown, Ct)).RasterPages);

        var result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(withRaster, Target()), null, Ct);
        await using var reopened = result.Saved.Reopened;
        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        Assert.DoesNotContain("secret", await TextOf(reopened, 1), StringComparison.Ordinal);
        Assert.Equal(string.Empty, (await TextOf(reopened, 0)).Trim());
    }

    [Fact]
    public async Task ALeakOnAnotherPageIsStillCaughtWhenAPageBecameAnImage()
    {
        // The engine "forgets" the mark on page two: its text stays. If the text of the raster page were counted as one that stays, the
        // check would allow one copy and let this leak pass.
        var path = TwoPageFile();
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = (await WithTermOnAsync(RedactionPlan.Empty, shown, 1, "secret value", everywhere: false)).WithRasterPage(0, on: true);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var hooks = new RedactionApplyHooks { AreasToRedact = static (_, _) => [] };
        var target = Target();

        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, hooks, Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, failure.Reason);
        var check = Assert.IsType<RedactionCheck>(failure.Details);
        Assert.Contains(check.Findings, static f => f.Kind == RedactionFindingKind.TextStillPresent);
        Assert.False(File.Exists(target));
    }

    // ------------------------------------------------------------------ refusals and failures

    private string ThumbFile()
    {
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (one) Tj ET", "BT /F1 12 Tf 20 150 Td (two) Tj ET");
        var stream = file.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", [0x66]);
        file.PageEntriesByPage = ["", $"/Thumb {stream} 0 R"];
        return Write("thumb.pdf", file.Build());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageKeysOnARasterPageKeepTheFileRefused(bool withAMarkOnAnotherPage)
    {
        // The page that carries the key becomes an image, but the file stays refused: whether a fresh page really sheds the key is not
        // proved, and the rule of milestone 14 does not change here.
        var path = ThumbFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = RedactionPlan.Empty.WithRasterPage(1, on: true);
        if (withAMarkOnAnotherPage)
        {
            plan = WithArea(plan, 0, new PageRect(15, 35, 60, 60));
        }

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);

        Assert.True(analysis.IsRefused);
        var refusal = Assert.Single(analysis.Refusals);
        Assert.Equal(RedactionRefusalKind.UnremovablePageKeys, refusal.Kind);
        Assert.Equal([1], refusal.Pages);
        var target = Target();
        await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, target), null, Ct));
        Assert.False(File.Exists(target));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.Empty(LeftOver(folder.Path, "thumb.pdf"));
    }

    [Fact]
    public async Task OriginalNeverChangesWhenRasterFails()
    {
        var path = TwoPageFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        var plan = RedactionPlan.Empty.WithRasterPage(0, on: true);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;

        // Writing over the original itself, and the saver is about to start when the build fails.
        var hooks = new RedactionApplyHooks { RewriteStarting = static (_, _, _) => throw new IOException("the disk went away") };
        await Assert.ThrowsAsync<IOException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, path), null, hooks, Ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));

        // A cancellation while the pages are built, and a failure while the file is written, leave the same picture.
        using var cancel = new CancellationTokenSource();
        var progress = new Progress<double>(value => { if (value > 0.1) { cancel.Cancel(); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, path), progress, null, cancel.Token));
        var faults = new SaveFaults { AfterWrite = static () => throw new IOException("the disk went away") };
        await Assert.ThrowsAnyAsync<Exception>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, path), null, new RedactionApplyHooks { Faults = faults }, Ct));

        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.Same(shown, session.Document);
        Assert.Empty(LeftOver(folder.Path, "two.pdf"));
    }

    [Fact]
    public async Task SourceChangedBetweenChoiceAndApplyEndsInSourceModified()
    {
        var path = TwoPageFile();
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;
        var plan = RedactionPlan.Empty.WithRasterPage(0, on: true);

        File.AppendAllText(path, "\n%changed\n");
        var changed = await File.ReadAllBytesAsync(path, Ct);
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target(), OverwriteChangedOriginal: true), null, Ct));

        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
        Assert.False(File.Exists(Target()));
        Assert.Equal(changed, await File.ReadAllBytesAsync(path, Ct));
        Assert.Same(shown, session.Document);
        var again = await Assert.ThrowsAsync<SaveFailureException>(() => new RedactionExecutor(engine).AnalyzeAsync(plan, shown!, path, null, Ct));
        Assert.Equal(SaveFailureReason.SourceModified, again.Reason);
        Assert.Empty(LeftOver(folder.Path, "two.pdf"));
    }

    // ------------------------------------------------------------------ what the page loses

    [Fact]
    public async Task RasterPageDropsAnnotationsFormFieldsAndLinks()
    {
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (A page with a note, a link and a field) Tj ET");
        var note = file.Add("<< /Type /Annot /Subtype /Text /Rect [20 20 40 40] /Contents (NOTEWORD-4471) /Name /Comment /F 4 >>");
        var link = file.Add("<< /Type /Annot /Subtype /Link /Rect [50 20 100 40] /Border [0 0 0] /A << /S /URI /URI (http://example.com/linkword-5512) >> >>");
        var field = file.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (FIELDWORD-9938) /Rect [110 20 200 40] /F 4 /DA (/Helv 0 Tf 0 g) >>");
        file.CatalogEntries = $"/AcroForm << /Fields [{field} 0 R] /DA (/Helv 0 Tf 0 g) >>";
        file.PageEntries = $"/Annots [{note} 0 R {link} 0 R {field} 0 R]";
        var path = Write("loses.pdf", file.Build());
        var plan = RedactionPlan.Empty.WithRasterPage(0, on: true);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);

        // The analysis names what goes, before anything is applied.
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, session.Document!, path, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        var info = Assert.Single(analysis.Raster!);
        Assert.Equal(3, info.Annotations);
        Assert.Equal(1, info.FormFields);
        Assert.True(info.Links >= 1);
        Assert.True(info.HasText);

        var target = Target();
        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, target), null, Ct);

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var summary = await session.Document!.GetPageObjectSummaryAsync(0, Ct);
        Assert.Equal(0, summary.Annotations);
        Assert.Empty(await session.Document!.GetFormFieldsAsync(0, Ct));
        Assert.Empty((await session.Document!.GetPageTextAsync(0, Ct)).Links);
        var bytes = await File.ReadAllBytesAsync(target, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, "NOTEWORD-4471"));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "FIELDWORD-9938"));
        Assert.False(RawPdf.ContainsAnywhere(bytes, "linkword-5512"));
    }

    [Fact]
    public async Task TheReportOfARasterOnlyPlanSaysWhatHappensToThePage()
    {
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (A page with a field) Tj ET");
        var field = file.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (x) /Rect [110 20 200 40] /F 4 /DA (/Helv 0 Tf 0 g) >>");
        file.CatalogEntries = $"/AcroForm << /Fields [{field} 0 R] /DA (/Helv 0 Tf 0 g) >>";
        file.PageEntries = $"/Annots [{field} 0 R]";
        var path = Write("report.pdf", file.Build());
        var plan = RedactionPlan.Empty.WithRasterPage(0, on: true);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, session.Document!, path, null, Ct);
        var before = RedactionReportText.Of(analysis);
        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct);
        var after = RedactionReportText.Of(analysis, result.Check);

        foreach (var report in new[] { before, after })
        {
            // A page that becomes an image is not "nothing removed": the report names the page and what it does not keep.
            Assert.DoesNotContain("Removed:\n- none", report, StringComparison.Ordinal);
            Assert.Contains("Page 1: turned into an image", report, StringComparison.Ordinal);
            Assert.Contains("text, links, comments and form fields are not kept", report, StringComparison.Ordinal);

            // The widget is gone from the page, not left on it as an annotation.
            Assert.Contains("gone from the pages turned into images", report, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ the size of the page

    private string PageOfSize(double widthPt, double heightPt) =>
        Write("big.pdf", new VerifierPdf("BT /F1 12 Tf 20 150 Td (big) Tj ET") { PageEntries = $"/MediaBox [0 0 {widthPt} {heightPt}]" }.Build());

    [Fact]
    public async Task ALoweredResolutionIsNamedAndUsed()
    {
        // 3000 x 3000 pt would be 8,334 x 8,334 pixels at 200 dpi (69 million): the resolution goes down so that the page fits in 32 million.
        var path = PageOfSize(3000, 3000);
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = RedactionPlan.Empty.WithRasterPage(0, on: true);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);

        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        var info = Assert.Single(analysis.Raster!);
        Assert.True(info.Lowered);
        Assert.InRange(info.Dpi, 72, 199);
        Assert.True((long)info.WidthPx * info.HeightPx <= RedactionLimits.MaxRasterPixels);
    }

    [Fact]
    public async Task APageThatCannotBeRasterizedEvenAtTheLowestResolutionIsRefused()
    {
        var path = PageOfSize(14400, 14400);
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var plan = RedactionPlan.Empty.WithRasterPage(0, on: true);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);

        Assert.True(analysis.IsRefused);
        var refusal = Assert.Single(analysis.Refusals);
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, refusal.Kind);
        Assert.Equal([0], refusal.Pages);
        Assert.Empty(analysis.Raster!);
        await Assert.ThrowsAsync<RedactionRefusedException>(() => RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, Target()), null, Ct));
        Assert.False(File.Exists(Target()));
    }

    // ------------------------------------------------------------------ hostile and unreadable pages

    [Fact]
    public async Task APageAboveTheObjectLimitIsRescuedByTheChoice()
    {
        // 100,001 paths: more objects than redaction walks (PdfiumRedaction.MaxObjects), so the page is refused as it is; the choice
        // rescues it, and the analysis must not walk the objects to count its annotations.
        var content = new System.Text.StringBuilder("BT /F1 12 Tf 20 150 Td (hostile page) Tj ET\n");
        for (var i = 0; i < 100_001; i++)
        {
            content.Append("0 0 1 1 re f\n");
        }

        var file = new VerifierPdf(content.ToString(), "BT /F1 12 Tf 20 150 Td (calm page) Tj ET");
        var path = Write("hostile.pdf", file.Build());
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shown.GetPageObjectSummaryAsync(0, Ct));
        var marked = WithArea(RedactionPlan.Empty, 0, new PageRect(15, 35, 100, 60));

        // Without the choice: a defined refusal naming the page.
        var refused = await new RedactionExecutor(engine).AnalyzeAsync(marked, shown, path, null, Ct);
        Assert.True(refused.IsRefused);
        var refusal = Assert.Single(refused.Refusals);
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, refusal.Kind);
        Assert.Equal([0], refusal.Pages);

        // With the choice: rescued, no raw exception.
        var plan = marked.WithRasterPage(0, on: true);
        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(plan, shown, path, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        var info = Assert.Single(analysis.Raster!);
        Assert.Equal(0, info.Page);
        Assert.Equal(0, info.Annotations);
        Assert.True(info.HasText);

        var result = await RedactionApplier.WriteAsync(engine, shown, path, null, FileStamp.Of(path), new RedactionApplyRequest(plan, Target()), null, Ct);
        await using var reopened = result.Saved.Reopened;
        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        Assert.Equal(new PageObjectSummary(Images: 1, Paths: 0, Texts: 0, Others: 0, Annotations: 0), await reopened.GetPageObjectSummaryAsync(0, Ct));
        Assert.Contains("calm page", await TextOf(reopened, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageThatCannotBeRenderedEndsInAFailureAndLeavesTheOriginalAlone()
    {
        var path = FivePageFile();
        var before = await File.ReadAllBytesAsync(path, Ct);
        var plan = RedactionPlan.Empty.WithRasterPage(0, on: true).WithRasterPage(3, on: true);
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document;
        var rendered = new List<int>();
        var hooks = new RedactionApplyHooks
        {
            RasterPageRendering = page =>
            {
                rendered.Add(page);
                if (page == 3)
                {
                    throw new InvalidDataException("the page is damaged");
                }
            },
        };

        // Writing over the original itself: the first image was made, the second page fails, nothing is written.
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, path), null, hooks, Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, failure.Reason);
        Assert.Equal([0, 3], rendered);
        var check = Assert.IsType<RedactionCheck>(failure.Details);
        var finding = Assert.Single(check.Findings);
        Assert.Equal(RedactionFindingKind.UnreadableStream, finding.Kind);
        Assert.Equal(3, finding.Page);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.Same(shown, session.Document);
        Assert.Empty(LeftOver(folder.Path, "five.pdf"));
    }

    // ------------------------------------------------------------------ the order of the pages

    private static string Marker(int page) => $"marker-{(char)('a' + page)}-{page * 7919}";

    /// <summary>Five pages of different widths (200, 220, ...), each with its own marker text.</summary>
    private string FivePageFile()
    {
        var file = new VerifierPdf([.. Enumerable.Range(0, 5).Select(static i => $"BT /F1 14 Tf 10 100 Td ({Marker(i)}) Tj ET 0.{i + 2} 0.3 0.6 rg 10 10 {20 + (i * 15)} 30 re f")]);
        file.PageEntriesByPage = [.. Enumerable.Range(0, 5).Select(static i => $"/MediaBox [0 0 {200 + (20 * i)} 150]")];
        return Write("five.pdf", file.Build());
    }

    [Theory]
    [InlineData("2")]
    [InlineData("1,2")]
    [InlineData("0,1,2,3,4")]
    [InlineData("0,3")]
    [InlineData("4")]
    public async Task PagesKeepTheirPlaceWhateverBecomesAnImage(string rasterList)
    {
        var path = FivePageFile();
        var raster = rasterList.Split(',').Select(int.Parse).ToHashSet();
        var plan = raster.Aggregate(RedactionPlan.Empty, static (p, page) => p.WithRasterPage(page, on: true));
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var shown = session.Document!;
        var originals = new RenderedImage[5];
        for (var page = 0; page < 5; page++)
        {
            originals[page] = await shown.RenderPageAsync(page, Scale, new RenderOptions { Priority = PdfPriority.Background }, Ct);
        }

        var result = await RedactionApplier.ApplyAsync(session, engine, new RedactionApplyRequest(plan, Target()), null, Ct);

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var after = session.Document!;
        Assert.Equal(5, after.PageCount);
        for (var page = 0; page < 5; page++)
        {
            // The width tells the page; the marker (text on a normal page, pixels on an image) tells the content.
            var size = await after.GetPageSizeAsync(page, Ct);
            Assert.Equal(200 + (20 * page), size.Width, 0.01);
            Assert.Equal(150, size.Height, 0.01);
            var summary = await after.GetPageObjectSummaryAsync(page, Ct);
            if (raster.Contains(page))
            {
                Assert.Equal(1, summary.Images);
                Assert.Equal(0, summary.Texts);
                Assert.Equal(string.Empty, (await TextOf(after, page)).Trim());
                var pixels = Assert.Single(await after.GetImageAreaPixelsAsync(page, new PageRect(0, 0, size.Width, size.Height), Ct));
                var expected = originals[page];
                Assert.Equal(expected.Width, pixels.Width);
                Assert.Equal(expected.Height, pixels.Height);
                var different = 0;
                for (var y = 0; y < expected.Height; y++)
                {
                    for (var x = 0; x < expected.Width; x++)
                    {
                        var at = ((y * pixels.Width) + x) * 4;
                        var source = (y * expected.Stride) + (x * 4);
                        if (Math.Abs(pixels.Bgra[at] - expected.Pixels.Span[source]) > 1 || Math.Abs(pixels.Bgra[at + 1] - expected.Pixels.Span[source + 1]) > 1 || Math.Abs(pixels.Bgra[at + 2] - expected.Pixels.Span[source + 2]) > 1)
                        {
                            different++;
                        }
                    }
                }

                Assert.Equal(0, different);
            }
            else
            {
                Assert.Equal(0, summary.Images);
                Assert.Contains(Marker(page), await TextOf(after, page), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task LossesCountOnlyWhatTheRebuildLosesOnThePagesKeptAsPages()
    {
        // A field and an internal link on each of two pages; page 0 becomes an image. Its field and link are counted for the raster page
        // ("gone"), so the rebuild's loss ("stays as an annotation only") counts the other page's alone and the two lines never say two things about one field.
        var file = new VerifierPdf("BT /F1 12 Tf 20 150 Td (first) Tj ET", "BT /F1 12 Tf 20 150 Td (second) Tj ET");
        var first = file.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (one) /V (a) /Rect [110 20 200 40] /F 4 /DA (/Helv 0 Tf 0 g) >>");
        var second = file.Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (two) /V (b) /Rect [110 20 200 40] /F 4 /DA (/Helv 0 Tf 0 g) >>");
        var linkOne = file.Add("<< /Type /Annot /Subtype /Link /Rect [50 20 100 40] /Border [0 0 0] /A << /S /GoTo /D [6 0 R /Fit] >> >>");
        var linkTwo = file.Add("<< /Type /Annot /Subtype /Link /Rect [50 60 100 80] /Border [0 0 0] /A << /S /GoTo /D [4 0 R /Fit] >> >>");
        file.CatalogEntries = $"/AcroForm << /Fields [{first} 0 R {second} 0 R] /DA (/Helv 0 Tf 0 g) >>";
        file.PageEntriesByPage = [$"/Annots [{first} 0 R {linkOne} 0 R]", $"/Annots [{second} 0 R {linkTwo} 0 R]"];
        var path = Write("two-forms.pdf", file.Build());
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);
        var executor = new RedactionExecutor(engine);

        var asPages = await executor.AnalyzeAsync(RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 1, [new PageRect(10, 10, 30, 30)], null, false)), shown, path, null, Ct);
        Assert.Equal(2, asPages.Losses.Single(static l => l.Kind == RedactionLossKind.FormFieldsNotFillable).Count);
        Assert.Equal(2, asPages.Losses.Single(static l => l.Kind == RedactionLossKind.InternalLinksLoseTheirTarget).Count);

        var analysis = await executor.AnalyzeAsync(RedactionPlan.Empty.WithRasterPage(0, on: true), shown, path, null, Ct);
        Assert.False(analysis.IsRefused, RedactionRefusalText.Of(analysis.Refusals));
        Assert.Equal(1, Assert.Single(analysis.Raster!).FormFields);
        Assert.Equal(1, analysis.Losses.Single(static l => l.Kind == RedactionLossKind.FormFieldsNotFillable).Count);
        Assert.Equal(1, analysis.Losses.Single(static l => l.Kind == RedactionLossKind.InternalLinksLoseTheirTarget).Count);

        // Every page an image: nothing is left to lose by the rebuild.
        var all = await executor.AnalyzeAsync(RedactionPlan.Empty.WithRasterPage(0, on: true).WithRasterPage(1, on: true), shown, path, null, Ct);
        Assert.DoesNotContain(all.Losses, static l => l.Kind is RedactionLossKind.FormFieldsNotFillable or RedactionLossKind.InternalLinksLoseTheirTarget);
    }
}
