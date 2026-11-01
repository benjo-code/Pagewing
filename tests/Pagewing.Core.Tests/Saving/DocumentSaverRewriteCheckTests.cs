using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Saving;

/// <summary>The hook for an extra check, the text-free fingerprint and the working document without a source file (milestone 14).</summary>
public sealed class DocumentSaverRewriteCheckTests : IDisposable
{
    private const string TemporaryName = ".pw-hooktest.tmp";

    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData("saveFailure")]
    [InlineData("plain")]
    [InlineData("io")]
    public async Task ExtraCheckFailureKeepsTheTargetAndRemovesTheTemporaryFile(string failure)
    {
        var original = Copy("pages-marked.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var copy = await BuildAsync(shown, original);
        var marker = new InvalidOperationException("the hook found a leak");
        var details = new object();
        var request = await RequestAsync(shown, original, original) with
        {
            ExtraCheck = (_, _) => Fail(failure, details, marker),
        };

        var error = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request, null, new SaveFaults { TemporaryName = () => TemporaryName }, Ct));

        Assert.Equal(SaveFailureReason.RedactionFailed, error.Reason);
        switch (failure)
        {
            case "saveFailure":
                Assert.Same(details, error.Details);
                Assert.Contains("leak on page 2", error.Message, StringComparison.Ordinal);
                Assert.Equal(SaveFailureReason.VerificationFailed, Assert.IsType<SaveFailureException>(error.InnerException).Reason);
                break;
            case "io":
                Assert.IsType<IOException>(error.InnerException);
                Assert.Null(error.Details);
                break;
            default:
                Assert.Same(marker, error.InnerException);
                Assert.Null(error.Details);
                break;
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        AssertOnlyFiles(original);
    }

    [Fact]
    public async Task ExtraCheckCanceledKeepsTheTargetAndRemovesTheTemporaryFile()
    {
        var original = Copy("pages-marked.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var copy = await BuildAsync(shown, original);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var request = await RequestAsync(shown, original, original) with
        {
            ExtraCheck = async (_, token) =>
            {
                await cancel.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request, null, new SaveFaults { TemporaryName = () => TemporaryName }, cancel.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        AssertOnlyFiles(original);
    }

    [Fact]
    public async Task ExtraCheckSeesTheClosedTemporaryFileBeforeTheTargetIsReplaced()
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var copy = await BuildAsync(shown, original);
        var seen = new List<string>();
        var request = await RequestAsync(shown, original, target) with
        {
            ExtraCheck = async (context, token) =>
            {
                seen.Add($"pages={context.Reopened.PageCount}");
                seen.Add($"targetExists={File.Exists(target)}");
                seen.Add($"name={Path.GetFileName(context.TemporaryPath)}");

                // Closed for writing (nothing holds it open for writing) but present and readable.
                var bytes = await File.ReadAllBytesAsync(context.TemporaryPath, token);
                seen.Add($"header={System.Text.Encoding.ASCII.GetString(bytes, 0, 5)}");
                seen.Add($"text={(await context.Reopened.GetTextAsync(0, token)).Trim()}");
            },
        };

        await using var result = (await DocumentSaver.RewriteAsync(copy, engine, request, null, new SaveFaults { TemporaryName = () => TemporaryName }, Ct)).Reopened;

        Assert.Equal(["pages=6", "targetExists=False", "name=" + TemporaryName, "header=%PDF-", "text=PAGE 1"], seen);
        Assert.True(File.Exists(target));
        AssertOnlyFiles(original, target);
    }

    [Fact]
    public async Task ExtraCheckDoesNotRunWhenTheStandardCheckFails()
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var copy = await BuildAsync(shown, original);
        var called = false;
        var request = await RequestAsync(shown, original, target) with
        {
            ExpectedPages = [.. (await RequestAsync(shown, original, target)).ExpectedPages.Skip(1)],
            ExtraCheck = (_, _) =>
            {
                called = true;
                return Task.CompletedTask;
            },
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => DocumentSaver.RewriteAsync(copy, engine, request, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.VerificationFailed, failure.Reason);
        Assert.False(called);
        AssertOnlyFiles(original);
    }

    [Fact]
    public async Task APassingExtraCheckLeavesTheResultAsWithoutIt()
    {
        var original = Copy("pages-marked.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        var plain = Path.Combine(folder.Path, "plain.pdf");
        var hooked = Path.Combine(folder.Path, "hooked.pdf");
        await using (var copy = await BuildAsync(shown, original))
        {
            await using var written = (await DocumentSaver.RewriteAsync(copy, engine, await RequestAsync(shown, original, plain), cancellationToken: Ct)).Reopened;
        }

        var calls = 0;
        await using (var copy = await BuildAsync(shown, original))
        {
            var request = await RequestAsync(shown, original, hooked) with
            {
                ExtraCheck = (_, _) =>
                {
                    calls++;
                    return Task.CompletedTask;
                },
            };
            await using var written = (await DocumentSaver.RewriteAsync(copy, engine, request, cancellationToken: Ct)).Reopened;
        }

        Assert.Equal(1, calls);
        await using var a = await engine.OpenAsync(plain, null, Ct);
        await using var b = await engine.OpenAsync(hooked, null, Ct);
        Assert.Equal(a.PageCount, b.PageCount);
        Assert.Equal(await PageEditExecutor.FingerprintsAsync(a, Ct), await PageEditExecutor.FingerprintsAsync(b, Ct));
        AssertOnlyFiles(original, plain, hooked);
    }

    [Fact]
    public async Task FingerprintWithoutTextCompareAcceptsChangedText()
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var copy = await BuildAsync(shown, original);
        var request = await RequestAsync(shown, original, target);
        var expected = request.ExpectedPages.ToArray();

        // The page lost its text (a redaction): neither hash is the one of the source, and the size and rotation are.
        expected[2] = expected[2] with { TextHash = expected[2].TextHash ^ 0x1234, TextSetHash = expected[2].TextSetHash ^ 0x4321, CompareText = false };

        await using var result = (await DocumentSaver.RewriteAsync(copy, engine, request with { ExpectedPages = expected }, cancellationToken: Ct)).Reopened;

        Assert.Equal(6, result.PageCount);
    }

    [Fact]
    public async Task FingerprintWithoutTextCompareStillComparesSizeAndRotation()
    {
        var original = Copy("pages-marked.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var copy = await BuildAsync(shown, original);
        var request = await RequestAsync(shown, original, Path.Combine(folder.Path, "out.pdf"));

        foreach (var wrong in new Func<PageFingerprint, PageFingerprint>[]
        {
            page => page with { CompareText = false, Rotation = (page.Rotation + 90) % 360 },
            page => page with { CompareText = false, Width = page.Width + 1 },
            page => page with { CompareText = false, Height = page.Height + 1 },
        })
        {
            var expected = request.ExpectedPages.ToArray();
            expected[1] = wrong(expected[1]);
            var failure = await Assert.ThrowsAsync<SaveFailureException>(
                () => DocumentSaver.RewriteAsync(copy, engine, request with { ExpectedPages = expected }, cancellationToken: Ct));
            Assert.Equal(SaveFailureReason.VerificationFailed, failure.Reason);
        }

        AssertOnlyFiles(original);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FingerprintWithTextCompareStillRejectsChangedText(bool changeOrderHash, bool changeSetHash)
    {
        var original = Copy("pages-marked.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var copy = await BuildAsync(shown, original);
        var request = await RequestAsync(shown, original, Path.Combine(folder.Path, "out.pdf"));
        var expected = request.ExpectedPages.ToArray();
        Assert.True(expected[2].CompareText);
        expected[2] = expected[2] with
        {
            TextHash = changeOrderHash ? expected[2].TextHash ^ 0x1234 : expected[2].TextHash,
            TextSetHash = changeSetHash ? expected[2].TextSetHash ^ 0x4321 : expected[2].TextSetHash,
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request with { ExpectedPages = expected }, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.VerificationFailed, failure.Reason);
        AssertOnlyFiles(original);
    }

    [Fact]
    public async Task SaverAcceptsAWorkingDocumentWithoutSourceFileWhenTheRealSourceIsListed()
    {
        var original = Copy("pages-marked.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        var target = Path.Combine(folder.Path, "out.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var rebuilt = await RebuildAsync(shown);
        var request = await RequestAsync(shown, original, target) with { WorkingCopyIsScratch = true };

        await using var result = (await DocumentSaver.RewriteAsync(rebuilt, engine, request, cancellationToken: Ct)).Reopened;

        Assert.Equal(6, result.PageCount);
        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        AssertOnlyFiles(original, target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARealSourceThatChangedIsRefusedForAWorkingDocumentWithoutSourceFile(bool duringTheWrite)
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var rebuilt = await RebuildAsync(shown);
        var request = await RequestAsync(shown, original, target) with { WorkingCopyIsScratch = true };
        if (!duringTheWrite)
        {
            Append(original);
        }

        var faults = duringTheWrite ? new SaveFaults { AfterWrite = () => Append(original) } : null;
        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(rebuilt, engine, request, null, faults, Ct));

        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
        Assert.False(File.Exists(target));
        AssertOnlyFiles(original);
    }

    [Fact]
    public async Task AWorkingDocumentWithoutSourceFileIsRefusedWhenNoRealSourceIsListed()
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var rebuilt = await RebuildAsync(shown);
        var request = await RequestAsync(shown, original, target) with { WorkingCopyIsScratch = true, UnchangedSources = [] };

        await Assert.ThrowsAsync<ArgumentException>(() => DocumentSaver.RewriteAsync(rebuilt, engine, request, cancellationToken: Ct));

        AssertOnlyFiles(original);
    }

    [Fact]
    public async Task AWorkingDocumentWithSourceFileStillChecksItsOwnFileByDefault()
    {
        var original = Copy("pages-marked.pdf");
        await using var shown = await engine.OpenAsync(original, null, Ct);
        await using var copy = await BuildAsync(shown, original);
        var request = await RequestAsync(shown, original, Path.Combine(folder.Path, "out.pdf"));
        Assert.False(request.WorkingCopyIsScratch);

        // The file the working copy was opened from changes: without the scratch flag that is still caught.
        Append(original);
        var failure = await Assert.ThrowsAsync<SaveFailureException>(() => DocumentSaver.RewriteAsync(copy, engine, request with { UnchangedSources = [] }, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
    }

    private static Task Fail(string failure, object details, Exception marker) => failure switch
    {
        "saveFailure" => throw new SaveFailureException(SaveFailureReason.VerificationFailed, "leak on page 2") { Details = details },
        "io" => throw new IOException("cannot read the file"),
        _ => throw marker,
    };

    private static void Append(string path)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.WriteByte((byte)'\n');
    }

    private string Copy(string fixture)
    {
        var path = Path.Combine(folder.Path, fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    /// <summary>A working copy opened from the file of <paramref name="shown"/> with no change: the result is the same six pages.</summary>
    private async Task<PdfDocument> BuildAsync(PdfDocument shown, string original)
    {
        var executor = new PageEditExecutor(engine);
        return await executor.BuildAsync(PageEditPlan.Identity(shown.PageCount), new PageSourceFile(original, null), new Dictionary<PageSourceId, PageSourceFile>(), null, Ct);
    }

    /// <summary>A document built from an empty one into which the pages of <paramref name="shown"/> were imported (the way a redaction rebuilds).</summary>
    private async Task<PdfDocument> RebuildAsync(PdfDocument shown)
    {
        var rebuilt = await engine.CreateEmptyAsync(Ct);
        await rebuilt.ImportPagesAsync(shown, [.. Enumerable.Range(0, shown.PageCount)], 0, Ct);
        return rebuilt;
    }

    /// <summary>The request the app would make: the fingerprints of the pages of <paramref name="shown"/>, which is also the one listed source.</summary>
    private static async Task<RewriteRequest> RequestAsync(PdfDocument shown, string original, string target) =>
        new(original, target, FileStamp.Of(original), null, await PageEditExecutor.FingerprintsAsync(shown, Ct), [shown], OverwriteChangedOriginal: false);

    private void AssertOnlyFiles(params string[] paths)
    {
        var expected = paths.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var actual = Directory.GetFiles(folder.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }
}
