using Pagewing.Core.Pdf;

namespace Pagewing.Core.Saving;

/// <summary>What <see cref="DocumentSaver.SaveAsync(PdfDocument, PdfEngine, SaveRequest, IProgress{double}?, CancellationToken)"/> writes and checks.</summary>
/// <param name="OriginalPath">The file the document was opened from.</param>
/// <param name="TargetPath">
/// The file to write: <paramref name="OriginalPath"/> for "Save", another path for "Save As". An existing target is replaced
/// (the caller asks the user first).
/// </param>
/// <param name="OriginalStamp">The stamp of the original taken when it was opened, or <see langword="null"/> to skip the check.</param>
/// <param name="Password">The password the document was opened with, to reopen the written file; it is never written anywhere.</param>
/// <param name="ExpectedPageCount">The number of pages the written file must have.</param>
/// <param name="ExpectedAnnotationCounts">
/// The changed pages (0-based index) and the number of annotations each must list, as
/// <see cref="PdfDocument.GetAnnotationsAsync"/> counts them (without popups and replies).
/// </param>
/// <param name="OverwriteChangedOriginal">Whether the user confirmed overwriting an original that changed since it was opened.</param>
public sealed record SaveRequest(
    string OriginalPath,
    string TargetPath,
    FileStamp? OriginalStamp,
    string? Password,
    int ExpectedPageCount,
    IReadOnlyDictionary<int, int> ExpectedAnnotationCounts,
    bool OverwriteChangedOriginal);

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

    /// <summary>Gets the action run on Windows after an open target was moved aside, before the temporary file takes its name.</summary>
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

    /// <summary>
    /// Saves <paramref name="document"/> as <paramref name="request"/> says. The steps: (1) if the target is the original and
    /// its stamp no longer matches <see cref="SaveRequest.OriginalStamp"/>, fail with
    /// <see cref="SaveFailureReason.OriginalChanged"/> before anything is written, unless
    /// <see cref="SaveRequest.OverwriteChangedOriginal"/>; (2) write the incremental save into
    /// <c>&lt;target&gt;.&lt;guid&gt;.tmp</c> (created new, not shared, flushed to disk); (3) check that it starts with the
    /// bytes of the original file (skipped when the original changed since it was opened, because the file on disk is then
    /// no longer what the document was read from); (4) open it with <paramref name="engine"/> and the password, and compare
    /// the page count, the encryption and the annotation count of every changed page; (5) render every changed page;
    /// (6) move it over the target. The document checked in step 4 is returned and stays open.
    /// </summary>
    /// <remarks>
    /// Replacing works while documents still have the old file open (Pagewing opens files shared for reading, writing and
    /// deleting); they keep reading the old content. On Windows that needs three renames instead of one (see
    /// <see cref="Replace"/>). "Save As" over an existing file replaces it the same way; the caller asks the user first.
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
        var current = FileStamp.Of(original);
        var originalChanged = request.OriginalStamp is { } stamp && current != stamp;
        if (originalChanged && SamePath(original, target) && !request.OverwriteChangedOriginal)
        {
            throw new SaveFailureException(SaveFailureReason.OriginalChanged, "The file was changed by another program since it was opened.");
        }

        var wasEncrypted = (await document.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false)).IsEncrypted;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var file = CreateTemporary(temporary);
        PdfDocument? reopened = null;
        var committed = false;
        try
        {
            await WriteAsync(document, file, current?.Length ?? 0, progress, faults, cancellationToken).ConfigureAwait(false);
            faults?.AfterWrite?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            if (!originalChanged)
            {
                await CheckPrefixAsync(temporary, original, current, cancellationToken).ConfigureAwait(false);
            }

            reopened = await ReopenAsync(engine, temporary, request.Password, cancellationToken).ConfigureAwait(false);
            await CheckAsync(reopened, request, wasEncrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Replace(temporary, target, faults);
            committed = true;

            // The move succeeded, so the file is there; a stamp that cannot be read only weakens the next check.
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
            const int FacilityWin32 = 7;
            return ((hresult >> 16) & 0x1FFF) == FacilityWin32 && (hresult & 0xFFFF) is 112 or 39;
        }

        return hresult is 28 or 122;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static FileStream CreateTemporary(string temporary)
    {
        try
        {
            return new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new SaveFailureException(SaveFailureReason.DiskFull, "The disk is full.", ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // A missing folder (DirectoryNotFoundException), no access, a read-only medium or a path that is too long.
            throw new SaveFailureException(SaveFailureReason.NotWritable, "The folder cannot be written to.", ex);
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
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new SaveFailureException(SaveFailureReason.DiskFull, "The disk is full.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new SaveFailureException(SaveFailureReason.WriteFailed, "The file could not be written.", ex);
        }
    }

    /// <summary>Checks that the temporary file starts with the bytes of the original (the proof that signed bytes are untouched).</summary>
    private static async Task CheckPrefixAsync(string temporary, string original, FileStamp? stamp, CancellationToken cancellationToken)
    {
        if (stamp is null)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "The original file cannot be read for the check.");
        }

        bool same;
        try
        {
            var written = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, useAsync: true);
            await using (written.ConfigureAwait(false))
            {
                var source = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, useAsync: true);
                await using (source.ConfigureAwait(false))
                {
                    same = source.Length == stamp.Value.Length && written.Length >= source.Length
                        && await StartsWithAsync(written, source, source.Length, cancellationToken).ConfigureAwait(false);
                }
            }
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
    /// Moves the checked temporary file over the target. On Linux (and on Windows when nothing has the target open) this is
    /// one atomic rename. Windows refuses to rename over a file another handle has open, even one shared for deleting (the
    /// old document of the tab always has it open), and <see cref="File.Replace(string, string, string?)"/> refuses a
    /// replacement that is open (the reopened document has it open). So Windows then moves the target aside to
    /// <c>&lt;target&gt;.&lt;guid&gt;.old</c> (renaming a file opened shared for deleting is allowed), moves the temporary
    /// file to the target and deletes the old file (its name goes at once on NTFS; the documents keep reading it). If the
    /// second move fails, the old file is moved back.
    /// </summary>
    private static void Replace(string temporary, string target, SaveFaults? faults)
    {
        try
        {
            File.Move(temporary, target, overwrite: true);
            return;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException && File.Exists(target))
        {
            // The target is open; replace it in three steps below.
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SaveFailureException(SaveFailureReason.NotWritable, "The file cannot be replaced.", ex);
        }
        catch (IOException ex)
        {
            throw new SaveFailureException(SaveFailureReason.WriteFailed, "The file could not be replaced.", ex);
        }

        var aside = target + "." + Guid.NewGuid().ToString("N") + ".old";
        try
        {
            File.Move(target, aside);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SaveFailureException(SaveFailureReason.NotWritable, "The file cannot be replaced.", ex);
        }
        catch (IOException ex)
        {
            throw new SaveFailureException(SaveFailureReason.WriteFailed, "The file could not be replaced.", ex);
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

            throw new SaveFailureException(SaveFailureReason.WriteFailed, "The file could not be replaced.", ex);
        }

        TryDelete(aside);
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
