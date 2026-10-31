using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Saving;

/// <summary>The complete rewrite of a page edit in the chain of checks of the safe save (milestone 13).</summary>
public sealed class DocumentSaverRewriteTests : IDisposable
{
    private const string Password = "hôtel";

    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();
    private readonly PageEditExecutor executor;

    public DocumentSaverRewriteTests() => executor = new PageEditExecutor(engine);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task RewriteWritesAFullFileWithoutThePrefixCheck()
    {
        var original = Copy("pages-marked.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        var (copy, request) = await BuildAsync(original, Path.Combine(folder.Path, "out.pdf"), p => p.Delete([1]).Rotate([0], 90));
        await using var scope = copy;

        var result = await DocumentSaver.RewriteAsync(copy, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            var written = await File.ReadAllBytesAsync(request.TargetPath, Ct);
            Assert.False(written.Length >= before.Length && written.AsSpan(0, before.Length).SequenceEqual(before), "The rewrite must not be the original plus an appendix.");
            Assert.Equal(5, result.Reopened.PageCount);
            var actual = await PageEditExecutor.FingerprintsAsync(result.Reopened, Ct);

            // Page 1 is turned (its reading order is not expected), the others are exactly what was expected.
            Assert.Equal(request.ExpectedPages.Skip(1), actual.Skip(1));
            Assert.Equal(request.ExpectedPages[0] with { TextHash = actual[0].TextHash, CompareTextOrder = true }, actual[0]);
            Assert.Equal(FileStamp.Of(request.TargetPath), result.NewStamp);
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("cancelWhileWriting")]
    [InlineData("cancelAfterWrite")]
    [InlineData("corrupt")]
    [InlineData("fingerprint")]
    public async Task RewriteKeepsTheTargetUntouchedOnEveryFailure(string fault)
    {
        var original = Copy("pages-marked.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        var (copy, request) = await BuildAsync(original, original, p => p.Delete([1]));
        await using var scope = copy;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var faults = fault switch
        {
            "write" => new SaveFaults { WrapTemporary = inner => new HookStream(inner, (_, count) => ThrowAfter(count)) },
            "cancelWhileWriting" => new SaveFaults { WrapTemporary = inner => new HookStream(inner, (_, count) => CancelAfter(count, cancel)) },
            "cancelAfterWrite" => new SaveFaults { AfterWrite = cancel.Cancel },
            "corrupt" => new SaveFaults { WrapTemporary = inner => new HookStream(inner, Corrupt) },
            _ => null,
        };
        if (fault == "fingerprint")
        {
            request = request with { ExpectedPages = [.. request.ExpectedPages.Skip(1)] };
        }

        Exception error = await Assert.ThrowsAnyAsync<Exception>(
            () => DocumentSaver.RewriteAsync(copy, engine, request, null, faults, cancel.Token));

        switch (fault)
        {
            case "write":
                Assert.Equal(SaveFailureReason.WriteFailed, Assert.IsType<SaveFailureException>(error).Reason);
                break;
            case "corrupt":
            case "fingerprint":
                Assert.Equal(SaveFailureReason.VerificationFailed, Assert.IsType<SaveFailureException>(error).Reason);
                break;
            default:
                Assert.IsAssignableFrom<OperationCanceledException>(error);
                break;
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        AssertOnlyFiles(original);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RewriteRefusesWhenAnUnchangedSourceChangedMeanwhile(bool saveAs, bool duringTheWrite)
    {
        var original = Copy("pages-marked.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        var insertedPath = Copy("text-basic.pdf");
        var target = saveAs ? Path.Combine(folder.Path, "out.pdf") : original;
        var (copy, request) = await BuildAsync(original, target, p => p.Delete([1]));
        await using var scope = copy;
        await using var inserted = await engine.OpenAsync(insertedPath, null, Ct);
        request = request with { UnchangedSources = [inserted] };
        var faults = duringTheWrite ? new SaveFaults { AfterWrite = () => Overwrite(insertedPath) } : null;
        if (!duringTheWrite)
        {
            Overwrite(insertedPath);
        }

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request, null, faults, Ct));

        Assert.Equal(SaveFailureReason.SourceModified, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        Assert.False(File.Exists(Path.Combine(folder.Path, "out.pdf")));
        AssertOnlyFiles(original, insertedPath);
    }

    [Fact]
    public async Task RewriteRefusesWhenTheOriginalWasReplaced()
    {
        var original = Copy("pages-marked.pdf");
        var (copy, request) = await BuildAsync(original, original, p => p.Delete([1]));
        await using var scope = copy;
        var moved = original + ".moved";
        File.Move(original, moved);
        File.Copy(TestData.Pdf("text-rich.pdf"), original);
        var other = await File.ReadAllBytesAsync(original, Ct);

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.OriginalChanged, failure.Reason);
        Assert.Equal(other, await File.ReadAllBytesAsync(original, Ct));
        AssertOnlyFiles(original, moved);

        // The user confirmed overwriting the other file: the rewrite then replaces it.
        var result = await DocumentSaver.RewriteAsync(copy, engine, request with { OverwriteChangedOriginal = true }, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Equal(5, result.Reopened.PageCount);
        }

        Assert.Equal(5, await PageCountOfAsync(original));
        AssertOnlyFiles(original, moved);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r6.pdf")]
    [InlineData("encrypted_hello_world_r3.pdf")]
    public async Task RewriteRefusesWhenEncryptionIsLost(string fixture)
    {
        var original = Copy(fixture);
        var before = await File.ReadAllBytesAsync(original, Ct);
        var (copy, request) = await BuildAsync(original, original, p => p, Password);
        await using var scope = copy;
        Assert.Equal(1, copy.PageCount);

        // The written file is replaced by one page without encryption: the page count fits, the encryption is gone.
        var faults = new SaveFaults
        {
            AfterWrite = () => File.Copy(TestData.Pdf("hello_world.pdf"), Assert.Single(Directory.GetFiles(folder.Path, "*.tmp")), overwrite: true),
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request, null, faults, Ct));

        Assert.Equal(SaveFailureReason.EncryptedNotSupported, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        AssertOnlyFiles(original);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r6.pdf")]
    [InlineData("encrypted_hello_world_r3.pdf")]
    public async Task RewriteOfAnEncryptedDocumentStaysEncrypted(string fixture)
    {
        var original = Copy(fixture);
        var (copy, request) = await BuildAsync(original, Path.Combine(folder.Path, "out.pdf"), p => p, Password);
        await using var scope = copy;

        var result = await DocumentSaver.RewriteAsync(copy, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.True((await result.Reopened.GetPrintInfoAsync(Ct)).IsEncrypted);
        }

        var refused = await Assert.ThrowsAsync<PdfOpenException>(() => engine.OpenAsync(request.TargetPath, cancellationToken: Ct));
        Assert.Equal(PdfOpenError.PasswordRequired, refused.Error);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("rotation")]
    [InlineData("text")]
    [InlineData("textset")]
    [InlineData("size")]
    public async Task RewriteDetectsAWrongOrderOrRotation(string wrong)
    {
        var original = Copy("pages-marked.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        var (copy, request) = await BuildAsync(original, original, p => p.Delete([1]).Rotate([0], 90));
        await using var scope = copy;
        var expected = request.ExpectedPages.ToArray();
        switch (wrong)
        {
            case "order":
                (expected[1], expected[2]) = (expected[2], expected[1]);
                break;
            case "rotation":
                expected[0] = expected[0] with { Rotation = (expected[0].Rotation + 90) % 360 };
                break;
            case "text":
                expected[3] = expected[3] with { TextHash = expected[3].TextHash + 1 };
                break;
            case "textset":
                expected[3] = expected[3] with { TextSetHash = expected[3].TextSetHash + 1 };
                break;
            default:
                expected[4] = expected[4] with { Width = expected[4].Width + 1 };
                break;
        }

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request with { ExpectedPages = expected }, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.VerificationFailed, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        AssertOnlyFiles(original);
    }

    [Fact]
    public async Task TheReadingOrderIsComparedOnlyWhereThePlanDoesNotTurnThePage()
    {
        var original = Copy("pages-marked.pdf");
        var (copy, request) = await BuildAsync(original, Path.Combine(folder.Path, "turned.pdf"), p => p.Rotate([0], 90));
        await using var scope = copy;
        var expected = request.ExpectedPages.ToArray();

        // The turned slot: PDFium reads such a page in another order, so only which characters it has is expected.
        Assert.False(expected[0].CompareTextOrder);
        Assert.True(expected[1].CompareTextOrder);
        expected[0] = expected[0] with { TextHash = expected[0].TextHash + 1 };
        await using var written = (await DocumentSaver.RewriteAsync(copy, engine, request with { ExpectedPages = expected }, cancellationToken: Ct)).Reopened;

        // An unturned page with the same characters in another order is another page: its order hash is compared.
        var (second, other) = await BuildAsync(original, Path.Combine(folder.Path, "swapped.pdf"), p => p);
        await using var secondScope = second;
        var wrong = other.ExpectedPages.ToArray();
        wrong[2] = wrong[2] with { TextHash = wrong[2].TextHash ^ 0x5555 };
        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(second, engine, other with { ExpectedPages = wrong }, cancellationToken: Ct));
        Assert.Equal(SaveFailureReason.VerificationFailed, failure.Reason);
    }

    [Fact]
    public async Task RewriteToANewNameLeavesTheOriginalAlone()
    {
        var original = Copy("pages-marked.pdf");
        var before = await File.ReadAllBytesAsync(original, Ct);
        var target = Path.Combine(folder.Path, "renamed.pdf");
        var (copy, request) = await BuildAsync(original, target, p => p.Move([2], 0));
        await using var scope = copy;

        var result = await DocumentSaver.RewriteAsync(copy, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Equal("PAGE 3", (await result.Reopened.GetTextAsync(0, Ct)).Trim());
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(original, Ct));
        AssertOnlyFiles(original, target);
    }

    [Fact]
    public async Task TheRequestDoesNotShowThePasswordInText()
    {
        var original = Copy("pages-marked.pdf");
        var (copy, request) = await BuildAsync(original, original, p => p);
        await using var scope = copy;

        var text = (request with { Password = "geheim-1234" }).ToString();

        Assert.DoesNotContain("geheim-1234", text, StringComparison.Ordinal);
        Assert.Contains("(hidden)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FingerprintsOfManyPagesAreSampledDeterministically()
    {
        var first = PageEditExecutor.SampledPages(500);
        Assert.Equal(first, PageEditExecutor.SampledPages(500));
        Assert.InRange(first.Count, 30, 50);
        Assert.Equal(0, first[0]);
        Assert.Equal(499, first[^1]);
        Assert.Equal(first.Order().Distinct(), first);
        Assert.Equal(Enumerable.Range(0, 50), PageEditExecutor.SampledPages(50));
    }

    private static void ThrowAfter(long count)
    {
        if (count >= 256)
        {
            throw new IOException("The test stream fails.");
        }
    }

    private static void CancelAfter(long count, CancellationTokenSource cancel)
    {
        if (count >= 256)
        {
            cancel.Cancel();
        }
    }

    private static byte[] Corrupt(byte[] block, long count) => count == block.Length ? [.. block.Select((b, i) => i == 0 ? (byte)(b ^ 1) : b)] : block;

    private static void Overwrite(string path)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.WriteByte((byte)'\n');
    }

    [Fact]
    public async Task RewriteWithoutReplacingNeverReplacesAFileThatAppearsWhileItIsWritten()
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        var (copy, request) = await BuildAsync(original, target, p => p.Delete([1]));
        await using var scope = copy;
        var foreign = "foreign file of another program"u8.ToArray();

        // Another program creates the target after the check of the target, while the file is written and checked.
        var faults = new SaveFaults { AfterWrite = () => File.WriteAllBytes(target, foreign) };
        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request with { ReplaceExisting = false }, null, faults, Ct));

        Assert.Equal(SaveFailureReason.TargetExists, failure.Reason);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(target, Ct));
        AssertOnlyFiles(original, target);
    }

    [Fact]
    public async Task RewriteWithoutReplacingRefusesAFileThatIsThereFromTheStart()
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        await File.WriteAllTextAsync(target, "precious", Ct);
        var (copy, request) = await BuildAsync(original, target, p => p.Delete([1]));
        await using var scope = copy;

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.RewriteAsync(copy, engine, request with { ReplaceExisting = false }, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.TargetExists, failure.Reason);
        Assert.Equal("precious", await File.ReadAllTextAsync(target, Ct));
        AssertOnlyFiles(original, target);
    }

    [Fact]
    public async Task RewriteWithoutReplacingWritesANewFile()
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        var (copy, request) = await BuildAsync(original, target, p => p.Delete([1]));
        await using var scope = copy;

        var result = await DocumentSaver.RewriteAsync(copy, engine, request with { ReplaceExisting = false }, cancellationToken: Ct);

        await using (result.Reopened)
        {
            Assert.Equal(5, result.Reopened.PageCount);
        }

        AssertOnlyFiles(original, target);
    }

    [Fact]
    public async Task RewriteReplacesAFileThatAppearsWhileItIsWrittenWhenReplacingIsAllowed()
    {
        var original = Copy("pages-marked.pdf");
        var target = Path.Combine(folder.Path, "out.pdf");
        var (copy, request) = await BuildAsync(original, target, p => p.Delete([1]));
        await using var scope = copy;
        Assert.True(request.ReplaceExisting);

        var faults = new SaveFaults { AfterWrite = () => File.WriteAllBytes(target, "foreign"u8.ToArray()) };
        var result = await DocumentSaver.RewriteAsync(copy, engine, request, null, faults, Ct);

        await using (result.Reopened)
        {
            Assert.Equal(5, result.Reopened.PageCount);
        }

        AssertOnlyFiles(original, target);
    }

    private async Task<int> PageCountOfAsync(string path)
    {
        await using var document = await engine.OpenAsync(path, null, Ct);
        return document.PageCount;
    }

    private string Copy(string fixture)
    {
        var path = Path.Combine(folder.Path, fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    /// <summary>Builds the working copy of <paramref name="edit"/> on <paramref name="original"/> and the request the app would make for it.</summary>
    private async Task<(PdfDocument Copy, RewriteRequest Request)> BuildAsync(string original, string target, Func<PageEditPlan, PageEditPlan> edit, string? password = null)
    {
        var stamp = FileStamp.Of(original);
        await using var shown = await engine.OpenAsync(original, password, Ct);
        var plan = edit(PageEditPlan.Identity(shown.PageCount));
        var primary = await PageEditExecutor.FingerprintsAsync(shown, Ct);
        var expected = plan.Slots.Select(slot => Turned(primary[slot.SourceIndex], slot.ExtraRotation)).ToArray();
        var copy = await executor.BuildAsync(plan, new PageSourceFile(original, password), new Dictionary<PageSourceId, PageSourceFile>(), null, Ct);
        return (copy, new RewriteRequest(original, target, stamp, password, expected, [], OverwriteChangedOriginal: false));
    }

    private static PageFingerprint Turned(PageFingerprint page, int extra)
    {
        var swap = extra % 180 != 0;
        return page with
        {
            Width = swap ? page.Height : page.Width,
            Height = swap ? page.Width : page.Height,
            Rotation = (page.Rotation + extra) % 360,
            CompareTextOrder = extra == 0,
        };
    }

    private void AssertOnlyFiles(params string[] paths)
    {
        var expected = paths.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var actual = Directory.GetFiles(folder.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>A write-only stream that passes the bytes on and calls a hook with the block and the bytes written so far.</summary>
    private sealed class HookStream(Stream inner, Func<byte[], long, byte[]> change) : Stream
    {
        private long count;

        public HookStream(Stream inner, Action<byte[], long> written)
            : this(inner, (block, total) =>
            {
                written(block, total);
                return block;
            })
        {
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            count += buffer.Length;
            inner.Write(change(buffer.ToArray(), count));
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
