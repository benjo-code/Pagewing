using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Saving;

/// <summary>What <see cref="DocumentSaver.SaveAsync(PdfDocument, PdfEngine, SaveRequest, IProgress{double}?, CancellationToken)"/> writes and checks.</summary>
/// <param name="OriginalPath">The file the document was opened from.</param>
/// <param name="TargetPath">
/// The file to write: <paramref name="OriginalPath"/> for "Save", another path for "Save As". An existing target is replaced
/// (the caller asks the user first), unless it is read-only.
/// </param>
/// <param name="OriginalStamp">The stamp of the original taken when it was opened, or <see langword="null"/> to skip the stamp checks.</param>
/// <param name="Password">
/// The password the document was opened with, to reopen the written file; it is never written anywhere and
/// <see cref="ToString"/> does not show it.
/// </param>
/// <param name="ExpectedPageCount">The number of pages the written file must have.</param>
/// <param name="ExpectedAnnotationCounts">
/// The changed pages (0-based index) and the number of annotations each must list, as
/// <see cref="PdfDocument.GetAnnotationsAsync"/> counts them (without popups and replies).
/// </param>
/// <param name="OverwriteChangedOriginal">
/// Whether the user confirmed overwriting the path of the original although another file now stands there (the stamp at
/// the path differs while the opened file is untouched). It never overrides <see cref="SaveFailureReason.SourceModified"/>.
/// </param>
public sealed record SaveRequest(
    string OriginalPath,
    string TargetPath,
    FileStamp? OriginalStamp,
    string? Password,
    int ExpectedPageCount,
    IReadOnlyDictionary<int, int> ExpectedAnnotationCounts,
    bool OverwriteChangedOriginal)
{
    /// <summary>Prints the members like a record does, with the password replaced (secrets never reach a log).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"OriginalPath = {OriginalPath}, TargetPath = {TargetPath}, OriginalStamp = {OriginalStamp}, Password = {(Password is null ? "null" : "(hidden)")}, ExpectedPageCount = {ExpectedPageCount}, ExpectedAnnotationCounts = {ExpectedAnnotationCounts?.Count ?? 0} pages, OverwriteChangedOriginal = {OverwriteChangedOriginal}");
        return true;
    }
}

/// <summary>A successful save.</summary>
/// <param name="Reopened">The written file, opened and checked; the caller owns it and disposes it.</param>
/// <param name="NewStamp">The stamp of the written file, for the next save.</param>
public sealed record SaveResult(PdfDocument Reopened, FileStamp NewStamp);

/// <summary>Hooks into a save, for tests only.</summary>
internal sealed class SaveFaults
{
    /// <summary>Gets the wrapper put around the stream of the temporary file.</summary>
    public Func<Stream, Stream>? WrapTemporary { get; init; }

    /// <summary>Gets the action run after the temporary file is written and closed, before it is checked.</summary>
    public Action? AfterWrite { get; init; }

    /// <summary>Gets the file name used for the temporary file instead of a random one.</summary>
    public Func<string>? TemporaryName { get; init; }

    /// <summary>Gets the stand-in for the atomic replace on Windows (source, target, returns a Win32 error code or 0).</summary>
    public Func<string, string, int>? AtomicReplace { get; init; }

    /// <summary>Gets the action run in the Windows fallback after the open target was moved aside, before the temporary file takes its name.</summary>
    public Action? AfterTargetMovedAside { get; init; }
}

/// <summary>
/// Saves a document incrementally and atomically: the original bytes followed by the changes go into a temporary file in
/// the target folder, which is checked and only then replaces the target. A failure or a cancellation leaves the target
/// byte for byte as it was and removes the temporary file.
/// </summary>
public static class DocumentSaver
{
    /// <summary>The longer side, in pixels, at which a changed page is rendered for the check.</summary>
    private const double CheckRenderSize = 1024;

    private const string InUse = "The file is in use by another program.";

    /// <summary><c>ERROR_SHARING_VIOLATION</c>: another handle without the needed sharing has the file open.</summary>
    private const int ErrorSharingViolation = 32;

    /// <summary><c>ERROR_LOCK_VIOLATION</c>.</summary>
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Saves <paramref name="document"/> as <paramref name="request"/> says. Before anything is written: the opened file must
    /// still be what was opened (its stamp, read through PDFium's own handle, equals <see cref="SaveRequest.OriginalStamp"/>
    /// and the length at open, otherwise <see cref="SaveFailureReason.SourceModified"/>); when the target is the original,
    /// the stamp at the path must match too (otherwise <see cref="SaveFailureReason.OriginalChanged"/>, unless
    /// <see cref="SaveRequest.OverwriteChangedOriginal"/>); an existing target must not be read-only
    /// (<see cref="SaveFailureReason.NotWritable"/>). Then: (1) write the incremental save into <c>.pw-&lt;12 hex&gt;.tmp</c>
    /// in the target folder (created new, not shared, flushed to disk; on Unix with the permissions of an existing target,
    /// on Windows with its Hidden attribute); (2) check that it starts with the bytes PDFium copied, read through PDFium's
    /// handle; (3) open it with <paramref name="engine"/> and the password, and compare the page count, the encryption and
    /// the annotation count of every changed page; (4) render every changed page; (5) replace the target. The document
    /// checked in step 3 is returned and stays open.
    /// </summary>
    /// <remarks>
    /// Replacing works while documents still have the old file open (Pagewing opens files shared for reading, writing and
    /// deleting); they keep reading the old content. Linux renames atomically; Windows uses the atomic POSIX rename and only
    /// on file systems without it moves the old file aside first (see <see cref="Replace"/>).
    /// </remarks>
    /// <param name="document">The document with the changes.</param>
    /// <param name="engine">Opens the written file for the check.</param>
    /// <param name="request">What to write and check.</param>
    /// <param name="progress">Receives 0 to 1 while the temporary file is written (from the PDFium thread).</param>
    /// <param name="cancellationToken">Cancels the save until the target is replaced; afterwards it has no effect.</param>
    /// <exception cref="SaveFailureException">The save failed; the target is unchanged.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the target is unchanged.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page in <see cref="SaveRequest.ExpectedAnnotationCounts"/> is not a page of the expected document.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="document"/> is disposed.</exception>
    public static Task<SaveResult> SaveAsync(
        PdfDocument document,
        PdfEngine engine,
        SaveRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        SaveAsync(document, engine, request, progress, faults: null, cancellationToken);

    /// <summary>Like the public overload, with hooks for tests.</summary>
    internal static async Task<SaveResult> SaveAsync(
        PdfDocument document,
        PdfEngine engine,
        SaveRequest request,
        IProgress<double>? progress,
        SaveFaults? faults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.OriginalPath, nameof(request));
        ArgumentException.ThrowIfNullOrEmpty(request.TargetPath, nameof(request));
        ArgumentNullException.ThrowIfNull(request.ExpectedAnnotationCounts, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ExpectedPageCount, nameof(request));
        foreach (var page in request.ExpectedAnnotationCounts.Keys)
        {
            if (page < 0 || page >= request.ExpectedPageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(request), page, "Every changed page must be a page of the expected document.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var original = Path.GetFullPath(request.OriginalPath);
        var target = Path.GetFullPath(request.TargetPath);
        var folder = Path.GetDirectoryName(target)
            ?? throw new SaveFailureException(SaveFailureReason.NotWritable, "The target has no folder.");

        var sourceLength = CheckSource(document, request.OriginalStamp);
        if (request.OriginalStamp is { } stamp && FileStamp.Of(original) != stamp && SamePath(original, target) && !request.OverwriteChangedOriginal)
        {
            throw new SaveFailureException(SaveFailureReason.OriginalChanged, "Another file stands at the path of the original since it was opened.");
        }

        var existing = ReadTarget(target);
        var wasEncrypted = (await document.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false)).IsEncrypted;
        var temporary = Path.Combine(folder, faults?.TemporaryName?.Invoke() ?? ShortName(".tmp"));
        var file = CreateTemporary(temporary, existing.UnixMode);
        PdfDocument? reopened = null;
        var committed = false;
        try
        {
            await WriteAsync(document, file, sourceLength, progress, faults, cancellationToken).ConfigureAwait(false);
            CopyTargetMetadata(temporary, existing);
            faults?.AfterWrite?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            await CheckPrefixAsync(temporary, document, sourceLength, cancellationToken).ConfigureAwait(false);
            reopened = await ReopenAsync(engine, temporary, request.Password, cancellationToken).ConfigureAwait(false);
            await CheckAsync(reopened, request, wasEncrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Replace(temporary, target, folder, faults);
            committed = true;

            // The replace succeeded, so the file is there; a stamp that cannot be read only weakens the next check.
            return new SaveResult(reopened, FileStamp.Of(target) ?? new FileStamp(-1, DateTime.MinValue));
        }
        finally
        {
            if (!committed)
            {
                if (reopened is not null)
                {
                    await reopened.DisposeAsync().ConfigureAwait(false);
                }

                TryDelete(temporary);
            }
        }
    }

    /// <summary>Whether <paramref name="error"/> or one of its inner exceptions reports a full disk or quota.</summary>
    internal static bool IsDiskFull(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            if (error is IOException && IsDiskFullCode(error.HResult))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Windows reports <c>ERROR_DISK_FULL</c> (112) or <c>ERROR_HANDLE_DISK_FULL</c> (39) as an HRESULT of FACILITY_WIN32;
    /// .NET on Linux puts the raw errno into the HResult, <c>ENOSPC</c> (28) or <c>EDQUOT</c> (122).
    /// </summary>
    private static bool IsDiskFullCode(int hresult)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32CodeOf(hresult) is 112 or 39;
        }

        return hresult is 28 or 122;
    }

    /// <summary>Whether a Windows error says another handle without the needed sharing has the file open.</summary>
    private static bool IsSharingViolation(Exception error) =>
        OperatingSystem.IsWindows() && error is IOException && Win32CodeOf(error.HResult) is ErrorSharingViolation or ErrorLockViolation;

    /// <summary>The Win32 error code of an HRESULT of FACILITY_WIN32, otherwise -1.</summary>
    private static int Win32CodeOf(int hresult)
    {
        const int FacilityWin32 = 7;
        return ((hresult >> 16) & 0x1FFF) == FacilityWin32 ? hresult & 0xFFFF : -1;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>A short name independent of the target's name, so a long target name never makes it too long.</summary>
    private static string ShortName(string extension) => ".pw-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)) + extension;

    /// <summary>
    /// Checks that the file PDFium reads is still the file that was opened, before anything is written: PDFium copies its
    /// bytes lazily through its own handle, so a rewrite in place since the open would end up in the saved file.
    /// </summary>
    /// <returns>The length of the opened file, which PDFium copies as the first bytes of the save.</returns>
    private static long CheckSource(PdfDocument document, FileStamp? originalStamp)
    {
        FileStamp source;
        long length;
        try
        {
            source = document.GetSourceStamp();
            length = document.SourceLengthAtOpen;
        }
        catch (IOException ex)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "The opened file cannot be read.", ex);
        }

        if (source.Length != length || (originalStamp is { } stamp && source != stamp))
        {
            throw new SaveFailureException(SaveFailureReason.SourceModified, "The opened file was changed by another program; it has to be opened again.");
        }

        return length;
    }

    /// <summary>What an existing target passes on to its replacement; a read-only target is refused here.</summary>
    private static TargetInfo ReadTarget(string target)
    {
        FileAttributes attributes;
        try
        {
            if (!File.Exists(target))
            {
                return default;
            }

            attributes = File.GetAttributes(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            throw new SaveFailureException(SaveFailureReason.NotWritable, "The file is read-only.");
        }

        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode Permissions = (UnixFileMode)0x1FF;
            mode = File.GetUnixFileMode(target) & Permissions;
        }

        return new TargetInfo(Exists: true, Hidden: (attributes & FileAttributes.Hidden) != 0, UnixMode: mode);
    }

    private static FileStream CreateTemporary(string temporary, UnixFileMode? mode)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81920,
        };
        if (mode is { } unixMode && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = unixMode;
        }

        try
        {
            return new FileStream(temporary, options);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            throw new SaveFailureException(SaveFailureReason.NotWritable, "The folder cannot be written to.", ex);
        }
        catch (IOException ex)
        {
            throw FailureOf(ex, "The temporary file could not be created.");
        }
    }

    /// <summary>
    /// Gives the temporary file the permissions of the target on Unix (exactly, without the umask that applied when it was
    /// created) and its Hidden attribute on Windows.
    /// </summary>
    private static void CopyTargetMetadata(string temporary, TargetInfo existing)
    {
        try
        {
            if (existing.UnixMode is { } mode && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, mode);
            }

            if (existing.Hidden && OperatingSystem.IsWindows())
            {
                File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FailureOf(ex, "The file could not be written.");
        }
    }

    /// <summary>Writes the incremental save into <paramref name="file"/>, flushes it to disk and closes it.</summary>
    private static async Task WriteAsync(
        PdfDocument document,
        FileStream file,
        long expectedLength,
        IProgress<double>? progress,
        SaveFaults? faults,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (file.ConfigureAwait(false))
            {
                var inner = faults?.WrapTemporary?.Invoke(file) ?? file;
                var counting = new ProgressStream(inner, expectedLength, progress, cancellationToken);
                progress?.Report(0);
                await document.SaveIncrementalAsync(counting, cancellationToken).ConfigureAwait(false);
                inner.Flush();
                file.Flush(flushToDisk: true);
                progress?.Report(1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && cancellationToken.IsCancellationRequested)
        {
            // The progress stream stops the writer on cancellation; PDFium then reports a failed save.
            throw new OperationCanceledException("The save was canceled.", ex, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw FailureOf(ex, "The file could not be written.");
        }
    }

    /// <summary>
    /// Checks that the temporary file starts with exactly the bytes PDFium copied from the opened file (the proof that signed
    /// bytes are untouched), read through PDFium's own handle, not the path.
    /// </summary>
    private static async Task CheckPrefixAsync(string temporary, PdfDocument document, long length, CancellationToken cancellationToken)
    {
        bool same;
        try
        {
            var written = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, useAsync: true);
            await using (written.ConfigureAwait(false))
            {
                var source = document.OpenSourceReader();
                await using (source.ConfigureAwait(false))
                {
                    same = written.Length >= length && await StartsWithAsync(written, source, length, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "The opened file became shorter during the save.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "The saved file could not be compared with the original.", ex);
        }

        if (!same)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "The saved file does not start with the bytes of the original.");
        }
    }

    private static async Task<bool> StartsWithAsync(Stream written, Stream source, long length, CancellationToken cancellationToken)
    {
        const int BlockSize = 1 << 20;
        var a = new byte[(int)Math.Min(BlockSize, Math.Max(1, length))];
        var b = new byte[a.Length];
        for (var left = length; left > 0;)
        {
            var size = (int)Math.Min(a.Length, left);
            await written.ReadExactlyAsync(a.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
            await source.ReadExactlyAsync(b.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
            if (!a.AsSpan(0, size).SequenceEqual(b.AsSpan(0, size)))
            {
                return false;
            }

            left -= size;
        }

        return true;
    }

    private static async Task<PdfDocument> ReopenAsync(PdfEngine engine, string temporary, string? password, CancellationToken cancellationToken)
    {
        try
        {
            return await engine.OpenAsync(temporary, password, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfOpenException ex)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "The saved file cannot be opened.", ex);
        }
    }

    /// <summary>Compares the reopened file with what was expected and renders every changed page.</summary>
    private static async Task CheckAsync(PdfDocument reopened, SaveRequest request, bool wasEncrypted, CancellationToken cancellationToken)
    {
        if (reopened.PageCount != request.ExpectedPageCount)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, $"The saved file has {reopened.PageCount} pages instead of {request.ExpectedPageCount}.");
        }

        if (wasEncrypted && !(await reopened.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false)).IsEncrypted)
        {
            throw new SaveFailureException(SaveFailureReason.EncryptedNotSupported, "The saved file would lose its encryption.");
        }

        try
        {
            var options = new RenderOptions { Priority = PdfPriority.Background };
            foreach (var (page, expected) in request.ExpectedAnnotationCounts)
            {
                var count = (await reopened.GetAnnotationsAsync(page, cancellationToken).ConfigureAwait(false)).Count;
                if (count != expected)
                {
                    throw new SaveFailureException(SaveFailureReason.VerificationFailed, $"Page {page + 1} of the saved file has {count} annotations instead of {expected}.");
                }

                var size = await reopened.GetPageSizeAsync(page, cancellationToken).ConfigureAwait(false);
                var longer = Math.Max(size.Width, size.Height);
                var scale = longer > 0 ? Math.Min(1, CheckRenderSize / longer) : 1;
                await reopened.RenderPageAsync(page, scale, options, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or PdfRenderException or ArgumentOutOfRangeException)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "A changed page of the saved file cannot be read or rendered.", ex);
        }
    }

    /// <summary>
    /// Moves the checked temporary file over the target. Linux: one atomic <c>rename</c>, which replaces the target even while
    /// it is open. Windows: the atomic POSIX rename (<see cref="WindowsFileRename.ReplaceAtomically"/>); Windows' ordinary
    /// rename refuses to replace a file another handle has open, even one shared for deleting, and the old document of the
    /// tab always has it open. Only when the file system does not offer the POSIX rename (FAT, exFAT, some SMB servers) is
    /// the ordinary rename tried, and if the open target blocks it, the target is moved aside to <c>.pw-&lt;12 hex&gt;.old</c>
    /// (renaming a file opened shared for deleting is allowed), the temporary file takes its name and the old file is
    /// deleted. If that second move fails, the old file is moved back.
    /// </summary>
    private static void Replace(string temporary, string target, string folder, SaveFaults? faults)
    {
        if (OperatingSystem.IsWindows())
        {
            var error = faults?.AtomicReplace is { } replace ? replace(temporary, target) : WindowsFileRename.ReplaceAtomically(temporary, target);
            if (error == 0)
            {
                return;
            }

            if (!WindowsFileRename.IsUnsupported(error))
            {
                throw FailureOfWin32(error);
            }
        }

        try
        {
            File.Move(temporary, target, overwrite: true);
            return;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && File.Exists(target))
        {
            // Without POSIX rename Windows refuses to rename over an open target (ACCESS_DENIED); a read-only target was
            // refused before anything was written. Replace it in three steps below.
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw FailureOf(ex, "The file could not be replaced.");
        }

        var aside = Path.Combine(folder, ShortName(".old"));
        try
        {
            File.Move(target, aside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw FailureOf(ex, "The file could not be replaced.");
        }

        try
        {
            faults?.AfterTargetMovedAside?.Invoke();
            File.Move(temporary, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            try
            {
                File.Move(aside, target);
            }
            catch (Exception restore) when (restore is UnauthorizedAccessException or IOException)
            {
                // Practically unreachable: the name was free a moment ago. The old content is kept under the other name.
                throw new SaveFailureException(
                    SaveFailureReason.WriteFailed,
                    $"The file could not be replaced, and the original could not be moved back; it is kept as '{aside}'.",
                    new AggregateException(ex, restore));
            }

            throw FailureOf(ex, "The file could not be replaced.");
        }

        TryDelete(aside);
    }

    /// <summary>Maps a write or replace error: full disk, file in use, no access, anything else.</summary>
    private static SaveFailureException FailureOf(Exception error, string message)
    {
        if (IsDiskFull(error))
        {
            return new SaveFailureException(SaveFailureReason.DiskFull, "The disk is full.", error);
        }

        if (IsSharingViolation(error))
        {
            return new SaveFailureException(SaveFailureReason.WriteFailed, InUse, error);
        }

        return error is UnauthorizedAccessException
            ? new SaveFailureException(SaveFailureReason.NotWritable, "The file cannot be replaced.", error)
            : new SaveFailureException(SaveFailureReason.WriteFailed, message, error);
    }

    [SupportedOSPlatform("windows")]
    private static SaveFailureException FailureOfWin32(int error)
    {
        var inner = new Win32Exception(error);
        return error switch
        {
            ErrorSharingViolation or ErrorLockViolation => new SaveFailureException(SaveFailureReason.WriteFailed, InUse, inner),
            WindowsFileRename.ErrorAccessDenied => new SaveFailureException(SaveFailureReason.NotWritable, "The file cannot be replaced.", inner),
            112 or 39 => new SaveFailureException(SaveFailureReason.DiskFull, "The disk is full.", inner),
            _ => new SaveFailureException(SaveFailureReason.WriteFailed, "The file could not be replaced.", inner),
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the file (.tmp or .old) is not the target.
        }
    }

    /// <summary>What an existing target passes on to the file that replaces it.</summary>
    private readonly record struct TargetInfo(bool Exists, bool Hidden, UnixFileMode? UnixMode);

    /// <summary>
    /// Passes the writes of PDFium on, reports the progress and stops the writer on cancellation (PDFium only learns
    /// that a block failed, so the cancellation is checked by the caller afterwards).
    /// </summary>
    private sealed class ProgressStream(Stream inner, long expectedLength, IProgress<double>? progress, CancellationToken cancellationToken) : Stream
    {
        private long written;
        private int reported;

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
            cancellationToken.ThrowIfCancellationRequested();
            inner.Write(buffer);
            written += buffer.Length;
            if (progress is null || expectedLength <= 0)
            {
                return;
            }

            // In whole percent, so a large file does not report once per block; the appended part runs at 100 %.
            var percent = (int)Math.Min(100, written * 100 / expectedLength);
            if (percent > reported)
            {
                reported = percent;
                progress.Report(percent / 100.0);
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
