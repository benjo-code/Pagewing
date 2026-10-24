using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Saving;

public sealed class DocumentSaverTests : IDisposable
{
    private const string Password = "hôtel";

    private static readonly PageRect MarkRect = new(20, 20, 120, 40);

    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task SavedFileStartsWithTheOriginalBytes()
    {
        var path = Copy("text-basic.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            var after = await File.ReadAllBytesAsync(path, Ct);
            Assert.True(after.Length > before.Length);
            Assert.Equal(before, after.AsSpan(0, before.Length).ToArray());
            Assert.Equal(FileStamp.Of(path), result.NewStamp);
            Assert.Single(await result.Reopened.GetAnnotationsAsync(0, Ct));
            Assert.Equal(document.PageCount, result.Reopened.PageCount);
        }

        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task CanceledSaveLeavesTheOriginalByteIdenticalAndRemovesTheTemporaryFile()
    {
        var path = Copy("text-dense.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var written = 0L;
        var faults = new SaveFaults
        {
            WrapTemporary = inner => new HookStream(inner, count =>
            {
                written = count;
                if (count >= 1024)
                {
                    cancel.Cancel();
                }
            }),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DocumentSaver.SaveAsync(document, engine, request, null, faults, cancel.Token));

        Assert.InRange(written, 1024, before.Length - 1);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task CancelAfterWritingRemovesTheTemporaryFile()
    {
        var path = Copy("text-basic.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var faults = new SaveFaults { AfterWrite = cancel.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DocumentSaver.SaveAsync(document, engine, request, null, faults, cancel.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task WriteFailureLeavesTheOriginalAndRemovesTheTemporaryFile()
    {
        var path = Copy("text-dense.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        var faults = new SaveFaults
        {
            WrapTemporary = inner => new HookStream(inner, count =>
            {
                if (count >= 1024)
                {
                    throw new IOException("The test stream fails.");
                }
            }),
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, null, faults, Ct));

        Assert.Equal(SaveFailureReason.WriteFailed, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task DiskFullIsReported()
    {
        var path = Copy("text-basic.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);

        // The code the system reports for a full disk: ERROR_DISK_FULL as HRESULT on Windows, ENOSPC elsewhere.
        var code = OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28;
        var faults = new SaveFaults
        {
            WrapTemporary = inner => new HookStream(inner, count =>
            {
                if (count >= 256)
                {
                    throw new IOException("The disk is full.", code);
                }
            }),
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, null, faults, Ct));

        Assert.Equal(SaveFailureReason.DiskFull, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        AssertOnlyFiles(path);
    }

    [Fact]
    public void DiskFullCodesAreRecognized()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(DocumentSaver.IsDiskFull(new IOException("full", unchecked((int)0x80070070))));
            Assert.True(DocumentSaver.IsDiskFull(new IOException("full", unchecked((int)0x80070027))));
            Assert.False(DocumentSaver.IsDiskFull(new IOException("sharing", unchecked((int)0x80070020))));
            Assert.False(DocumentSaver.IsDiskFull(new IOException("errno-like", 28)));
        }
        else
        {
            Assert.True(DocumentSaver.IsDiskFull(new IOException("full", 28)));
            Assert.True(DocumentSaver.IsDiskFull(new IOException("quota", 122)));
            Assert.False(DocumentSaver.IsDiskFull(new IOException("io", 5)));
        }

        Assert.True(DocumentSaver.IsDiskFull(new IOException("wrapped", new IOException("full", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28))));
        Assert.False(DocumentSaver.IsDiskFull(new IOException("plain")));
        Assert.False(DocumentSaver.IsDiskFull(null));
    }

    [Fact]
    public void TheSystemReportsAFullDiskAsDiskFull()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/dev/full"), "/dev/full exists on Linux only.");
        var error = Assert.ThrowsAny<IOException>(() =>
        {
            using var stream = new FileStream("/dev/full", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
            stream.Write(new byte[4096]);
            stream.Flush(flushToDisk: true);
        });
        Assert.True(DocumentSaver.IsDiskFull(error), $"HResult {error.HResult}: {error.Message}");
    }

    [Fact]
    public async Task FailedVerificationLeavesTheOriginal()
    {
        var path = Copy("text-basic.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        var faults = new SaveFaults { WrapTemporary = inner => new CorruptingStream(inner) };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, null, faults, Ct));

        Assert.Equal(SaveFailureReason.VerificationFailed, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task AnnotationCountMismatchFailsVerification()
    {
        var path = Copy("text-basic.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        request = request with { ExpectedAnnotationCounts = new Dictionary<int, int> { [0] = 2 } };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.VerificationFailed, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task PageCountMismatchFailsVerification()
    {
        var path = Copy("text-basic.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        request = request with { ExpectedPageCount = document.PageCount + 1 };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.VerificationFailed, failure.Reason);
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task ChangedOriginalIsReportedBeforeAnythingIsWritten()
    {
        var path = Copy("text-basic.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));
        var before = await File.ReadAllBytesAsync(path, Ct);
        var opened = false;
        var faults = new SaveFaults
        {
            WrapTemporary = inner =>
            {
                opened = true;
                return inner;
            },
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, null, faults, Ct));

        Assert.Equal(SaveFailureReason.OriginalChanged, failure.Reason);
        Assert.False(opened);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task OverwriteChangedOriginalSkipsThatCheck()
    {
        var path = Copy("text-basic.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path) with { OverwriteChangedOriginal = true };
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Single(await result.Reopened.GetAnnotationsAsync(0, Ct));
            Assert.Equal(FileStamp.Of(path), result.NewStamp);
        }

        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task SaveAsIsAllowedWhenTheOriginalChanged()
    {
        var path = Copy("text-basic.pdf");
        var target = Path.Combine(folder.Path, "copy.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, target);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Single(await result.Reopened.GetAnnotationsAsync(0, Ct));
        }

        AssertOnlyFiles(path, target);
    }

    [Fact]
    public async Task UnwritableFolderReportsNotWritable()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess, "Root writes into any folder.");
        var path = Copy("text-basic.pdf");
        var locked = Directory.CreateDirectory(Path.Combine(folder.Path, "locked"));
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, Path.Combine(locked.FullName, "out.pdf"));
        using (DenyWrites(locked))
        {
            var failure = await Assert.ThrowsAsync<SaveFailureException>(
                () => DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct));
            Assert.Equal(SaveFailureReason.NotWritable, failure.Reason);
        }

        Assert.Empty(locked.GetFileSystemInfos());
    }

    [Fact]
    public async Task MissingFolderReportsNotWritable()
    {
        var path = Copy("text-basic.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, Path.Combine(folder.Path, "missing", "out.pdf"));

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct));

        Assert.Equal(SaveFailureReason.NotWritable, failure.Reason);
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task SaveAsWritesANewFileAndKeepsTheOriginal()
    {
        var path = Copy("text-basic.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        var target = Path.Combine(folder.Path, "copy.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, target);

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
            var saved = await File.ReadAllBytesAsync(target, Ct);
            Assert.Equal(before, saved.AsSpan(0, before.Length).ToArray());
            Assert.Equal(FileStamp.Of(target), result.NewStamp);
            Assert.Single(await result.Reopened.GetAnnotationsAsync(0, Ct));
        }

        await using (var original = await OpenAsync(path))
        {
            Assert.Empty(await original.GetAnnotationsAsync(0, Ct));
        }

        AssertOnlyFiles(path, target);
    }

    [Fact]
    public async Task SaveAsReplacesAnExistingTarget()
    {
        var path = Copy("text-basic.pdf");
        var target = Path.Combine(folder.Path, "copy.pdf");
        await File.WriteAllTextAsync(target, "old content", Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, target);

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Single(await result.Reopened.GetAnnotationsAsync(0, Ct));
        }

        Assert.Equal(await File.ReadAllBytesAsync(path, Ct), (await File.ReadAllBytesAsync(target, Ct)).AsSpan(0, (int)new FileInfo(path).Length).ToArray());
        AssertOnlyFiles(path, target);
    }

    [Fact]
    public async Task ReplacingAnOpenTargetWorks()
    {
        var path = Copy("text-basic.pdf");
        await using var other = await OpenAsync(path);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Single(await result.Reopened.GetAnnotationsAsync(0, Ct));

            // Both documents that had the original open still read it.
            Assert.Empty(await other.GetAnnotationsAsync(0, Ct));
            Assert.Single(await document.GetAnnotationsAsync(0, Ct));
            await other.RenderPageAsync(0, 0.5, cancellationToken: Ct);
            await document.RenderPageAsync(0, 0.5, cancellationToken: Ct);
        }

        await using (var reread = await OpenAsync(path))
        {
            Assert.Single(await reread.GetAnnotationsAsync(0, Ct));
        }

        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task FailedReplaceOfAnOpenTargetPutsTheOriginalBack()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows replaces an open file in three steps.");
        var path = Copy("text-basic.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        var movedAside = false;
        var faults = new SaveFaults
        {
            AfterTargetMovedAside = () =>
            {
                movedAside = true;
                Assert.False(File.Exists(path));
                throw new IOException("The test refuses the move.");
            },
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, null, faults, Ct));

        Assert.True(movedAside);
        Assert.Equal(SaveFailureReason.WriteFailed, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        Assert.Single(await document.GetAnnotationsAsync(0, Ct));
        await document.RenderPageAsync(0, 0.5, cancellationToken: Ct);
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task SignedFixtureKeepsTheSignedBytes()
    {
        var path = Copy("signed-docmdp-3.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            var after = await File.ReadAllBytesAsync(path, Ct);
            Assert.Equal(before, after.AsSpan(0, before.Length).ToArray());
            var signature = await result.Reopened.GetSignatureInfoAsync(Ct);
            Assert.Equal(1, signature.SignatureCount);
            Assert.Equal(DocMdpLevel.FormsAndAnnotations, signature.Permission);
        }
    }

    [Theory]
    [InlineData("encrypted_hello_world_r6.pdf")]
    [InlineData("encrypted_hello_world_r3.pdf")]
    public async Task EncryptedDocumentStaysEncryptedOrIsRefused(string fixture)
    {
        var path = Copy(fixture);
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path, Password);
        var request = await MarkAsync(document, path, path) with { Password = Password };

        SaveResult result;
        try
        {
            result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        }
        catch (SaveFailureException failure) when (failure.Reason == SaveFailureReason.EncryptedNotSupported)
        {
            Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
            AssertOnlyFiles(path);
            return;
        }

        await using (result.Reopened)
        {
            Assert.Single(await result.Reopened.GetAnnotationsAsync(0, Ct));
            Assert.True((await result.Reopened.GetPrintInfoAsync(Ct)).IsEncrypted);
        }

        var after = await File.ReadAllBytesAsync(path, Ct);
        Assert.Equal(before, after.AsSpan(0, before.Length).ToArray());

        // The appended objects are encrypted too: the comment is not in the file as plain text.
        var appended = Encoding.Latin1.GetString(after, before.Length, after.Length - before.Length);
        Assert.DoesNotContain("Saved", appended, StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.Latin1.GetString(Encoding.BigEndianUnicode.GetBytes("Saved")), appended, StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<PdfOpenException>(() => engine.OpenAsync(path, cancellationToken: Ct));
        Assert.Equal(PdfOpenError.PasswordRequired, refused.Error);
        await using var reopened = await OpenAsync(path, Password);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        Assert.Equal("Saved", annotation.Contents);
        Assert.Equal("Ada", annotation.Author);
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task WrittenFileWithoutEncryptionIsRefused()
    {
        var path = Copy("encrypted_hello_world_r6.pdf");
        await using var document = await OpenAsync(path, Password);
        var request = await MarkAsync(document, path, path) with { Password = Password, OverwriteChangedOriginal = true };

        // A changed original skips the prefix check, so the unencrypted stand-in reaches the later checks.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-5));
        var before = await File.ReadAllBytesAsync(path, Ct);
        var faults = new SaveFaults
        {
            AfterWrite = () => File.Copy(TestData.Pdf("hello_world.pdf"), Assert.Single(Directory.GetFiles(folder.Path, "*.tmp")), overwrite: true),
        };

        var failure = await Assert.ThrowsAsync<SaveFailureException>(
            () => DocumentSaver.SaveAsync(document, engine, request, null, faults, Ct));

        Assert.Equal(SaveFailureReason.EncryptedNotSupported, failure.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task LinearizedFileSaves()
    {
        var path = Copy("linearized.pdf");
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await using (result.Reopened)
        {
            Assert.Single(await result.Reopened.GetAnnotationsAsync(0, Ct));
            Assert.Equal(document.PageCount, result.Reopened.PageCount);
        }

        var after = await File.ReadAllBytesAsync(path, Ct);
        Assert.Equal(before, after.AsSpan(0, before.Length).ToArray());
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task ProgressRisesFromZeroToOne()
    {
        var path = Copy("text-dense.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        var reports = new List<double>();
        var progress = new SyncProgress(value =>
        {
            lock (reports)
            {
                reports.Add(value);
            }
        });

        var result = await DocumentSaver.SaveAsync(document, engine, request, progress, Ct);
        await result.Reopened.DisposeAsync();

        lock (reports)
        {
            Assert.True(reports.Count >= 3, $"Only {reports.Count} reports.");
            Assert.Equal(0, reports[0]);
            Assert.Equal(1, reports[^1]);
            Assert.All(reports, value => Assert.InRange(value, 0, 1));
            for (var i = 1; i < reports.Count; i++)
            {
                Assert.True(reports[i] >= reports[i - 1], "Progress went back.");
            }
        }
    }

    [Fact]
    public async Task UnchangedPagesAreNotChecked()
    {
        var path = Copy("text-dense.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        Assert.Equal([0], request.ExpectedAnnotationCounts.Keys);

        var result = await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct);
        await result.Reopened.DisposeAsync();
        AssertOnlyFiles(path);
    }

    [Fact]
    public async Task ExpectedCountsOutsideTheDocumentAreRejected()
    {
        var path = Copy("text-basic.pdf");
        await using var document = await OpenAsync(path);
        var request = await MarkAsync(document, path, path);
        request = request with { ExpectedAnnotationCounts = new Dictionary<int, int> { [document.PageCount] = 0 } };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct));
        AssertOnlyFiles(path);
    }

    private static Restore DenyWrites(DirectoryInfo directory) =>
        OperatingSystem.IsWindows() ? DenyWritesOnWindows(directory) : DenyWritesOnUnix(directory);

    [SupportedOSPlatform("windows")]
    private static Restore DenyWritesOnWindows(DirectoryInfo directory)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        return new Restore(() => AllowWritesOnWindows(directory, rule));
    }

    [SupportedOSPlatform("windows")]
    private static void AllowWritesOnWindows(DirectoryInfo directory, FileSystemAccessRule rule)
    {
        var security = directory.GetAccessControl();
        security.RemoveAccessRule(rule);
        directory.SetAccessControl(security);
    }

    [UnsupportedOSPlatform("windows")]
    private static Restore DenyWritesOnUnix(DirectoryInfo directory)
    {
        var mode = File.GetUnixFileMode(directory.FullName);
        File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() => AllowWritesOnUnix(directory, mode));
    }

    [UnsupportedOSPlatform("windows")]
    private static void AllowWritesOnUnix(DirectoryInfo directory, UnixFileMode mode) => File.SetUnixFileMode(directory.FullName, mode);

    private string Copy(string fixture)
    {
        var path = Path.Combine(folder.Path, fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    private Task<PdfDocument> OpenAsync(string path, string? password = null) => engine.OpenAsync(path, password, Ct);

    /// <summary>Creates one highlight on page 0 and builds the request the app would make for it.</summary>
    private static async Task<SaveRequest> MarkAsync(PdfDocument document, string original, string target)
    {
        var stamp = FileStamp.Of(original);
        await document.CreateMarkupAsync(0, AnnotationKind.Highlight, [MarkRect], AnnotationTestFiles.Yellow, "Saved", "Ada", Ct);
        var count = (await document.GetAnnotationsAsync(0, Ct)).Count;
        return new SaveRequest(original, target, stamp, null, document.PageCount, new Dictionary<int, int> { [0] = count }, OverwriteChangedOriginal: false);
    }

    private void AssertOnlyFiles(params string[] paths)
    {
        var expected = paths.Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var actual = Directory.GetFiles(folder.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>A write-only stream that passes everything on and calls a hook with the bytes written so far.</summary>
    private sealed class HookStream(Stream inner, Action<long> written) : WriteOnlyStream(inner)
    {
        private long count;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Inner.Write(buffer);
            count += buffer.Length;
            written(count);
        }
    }

    /// <summary>A write-only stream that flips the first byte it writes.</summary>
    private sealed class CorruptingStream(Stream inner) : WriteOnlyStream(inner)
    {
        private bool done;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (done || buffer.IsEmpty)
            {
                Inner.Write(buffer);
                return;
            }

            done = true;
            var copy = buffer.ToArray();
            copy[0] ^= 0x01;
            Inner.Write(copy);
        }
    }

    private abstract class WriteOnlyStream(Stream inner) : Stream
    {
        protected Stream Inner { get; } = inner;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => Inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public abstract override void Write(ReadOnlySpan<byte> buffer);
    }
}
