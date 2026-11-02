using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Saving;

/// <summary>What <see cref="DocumentSaver.SaveAsync(PdfDocument, PdfEngine, SaveRequest, IProgress{double}?, CancellationToken)"/> writes and checks.</summary>
/// <param name="OriginalPath">The file the document was opened from.</param>
/// <param name="TargetPath">
/// The file to write: <paramref name="OriginalPath"/> for "Save", another path for "Save As". An existing target is replaced
/// (the caller asks the user first), unless it is read-only.
/// </param>
/// <param name="OriginalStamp">
/// The stamp at the path of the original (<see cref="FileStamp.Of"/>) taken when it was opened, or <see langword="null"/>
/// to skip the <see cref="SaveFailureReason.OriginalChanged"/> check. The check of the opened file itself does not use it.
/// </param>
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
/// <param name="ReplaceExisting">
/// Whether an existing target may be replaced (the default, as "Save" and "Save As" ask the user first). The command line passes
/// <see langword="false"/> unless <c>--force</c> was given: a file that stands at the target when the temporary file is moved into place
/// (also one that appeared meanwhile) is never replaced, the save ends in <see cref="SaveFailureReason.TargetExists"/> and removes its temporary file.
/// </param>
public sealed record SaveRequest(
    string OriginalPath,
    string TargetPath,
    FileStamp? OriginalStamp,
    string? Password,
    int ExpectedPageCount,
    IReadOnlyDictionary<int, int> ExpectedAnnotationCounts,
    bool OverwriteChangedOriginal,
    bool ReplaceExisting = true)
{
    /// <summary>Prints the members like a record does, with the password replaced (secrets never reach a log).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"OriginalPath = {OriginalPath}, TargetPath = {TargetPath}, OriginalStamp = {OriginalStamp}, Password = {(Password is null ? "null" : "(hidden)")}, ExpectedPageCount = {ExpectedPageCount}, ExpectedAnnotationCounts = {ExpectedAnnotationCounts?.Count ?? 0} pages, OverwriteChangedOriginal = {OverwriteChangedOriginal}, ReplaceExisting = {ReplaceExisting}");
        return true;
    }
}

/// <summary>A successful save.</summary>
/// <param name="Reopened">The written file, opened and checked; the caller owns it and disposes it.</param>
/// <param name="NewStamp">The stamp of the written file, read through the handle of <paramref name="Reopened"/>.</param>
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

    /// <summary>Gets the stand-in for the probe whether the temporary file may be opened with DELETE, the access its rename needs (Windows).</summary>
    public Func<string, bool>? CanDelete { get; init; }

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
    /// still be what was opened (its stamp, read through PDFium's own handle, equals the one taken the same way when it was
    /// opened, <see cref="PdfDocument.SourceStampAtOpen"/>; otherwise <see cref="SaveFailureReason.SourceModified"/>); when the
    /// target is the original, the stamp at the path must still equal <see cref="SaveRequest.OriginalStamp"/> (path against
    /// path; otherwise <see cref="SaveFailureReason.OriginalChanged"/>, unless <see cref="SaveRequest.OverwriteChangedOriginal"/>);
    /// an existing target must not be read-only (<see cref="SaveFailureReason.NotWritable"/>). Then: (1) write the incremental
    /// save into <c>.pw-&lt;12 hex&gt;.tmp</c> in the target folder (created new, not shared, flushed to disk; on Unix with
    /// the permissions of an existing target, on Windows with its Hidden attribute); (2) check that it starts with the bytes
    /// PDFium copied, read through PDFium's handle; (3) open it with <paramref name="engine"/> and the password, and compare
    /// the page count, the encryption and the annotation count of every changed page; (4) render every changed page;
    /// (5) replace the target. The source check runs again after the write, after the prefix check and immediately before
    /// the replace, so a rewrite in place while PDFium copies is refused too. The document checked in step 3 is returned and
    /// stays open.
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

        var job = new Job(
            request.OriginalPath,
            request.TargetPath,
            request.OriginalStamp,
            request.OverwriteChangedOriginal,
            request.Password,
            ExtraSources: [],
            Write: document.SaveIncrementalAsync,
            CheckPrefix: true,
            Check: (reopened, wasEncrypted, _, token) => CheckAsync(reopened, request, wasEncrypted, token),
            ReplaceExisting: request.ReplaceExisting);
        return await RunAsync(document, engine, job, progress, faults, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the complete document of a page edit (never incrementally: the page operations change the pages the original
    /// bytes describe, so there is no prefix to keep) through the same chain as
    /// <see cref="SaveAsync(PdfDocument, PdfEngine, SaveRequest, IProgress{double}?, CancellationToken)"/>. Before the first byte,
    /// the file of the working copy and of every <see cref="RewriteRequest.UnchangedSources"/> entry must still be what was
    /// opened (handle against handle; <see cref="SaveFailureReason.SourceModified"/> without override, also for "Save As");
    /// when the target is the original, the stamp at its path must still be the opened one
    /// (<see cref="SaveFailureReason.OriginalChanged"/>, overridable); an existing target must not be read-only. Then the working
    /// copy is written with <see cref="PdfDocument.SaveRewriteAsync"/> into <c>.pw-&lt;12 hex&gt;.tmp</c> in the target folder, the
    /// sources are checked again, the file is opened with <paramref name="engine"/> and the password, and its page count, its
    /// encryption (an encrypted document must stay encrypted, otherwise <see cref="SaveFailureReason.EncryptedNotSupported"/>)
    /// and the fingerprint of every page (all up to 50, then a deterministic sample) are compared with
    /// <see cref="RewriteRequest.ExpectedPages"/> (<see cref="SaveFailureReason.VerificationFailed"/>); the checked pages are
    /// rendered; then <see cref="RewriteRequest.ExtraCheck"/> runs, if there is one, on the reopened file (a failure of it is
    /// <see cref="SaveFailureReason.RedactionFailed"/>); the sources are checked once more and the target is replaced. A working copy
    /// built from an empty document has no file worth watching, but it reads lazily from the documents its pages were imported from:
    /// <see cref="RewriteRequest.RequiresListedSources"/> makes the request refuse unless those documents are listed. Every failure and cancellation before the replace
    /// leaves the target byte for byte as it was and removes the temporary file. The checked document is returned and stays
    /// open; the working copy stays with the caller.
    /// </summary>
    /// <param name="workingCopy">The working copy built by the page edit executor (not rendered, not changed afterwards).</param>
    /// <param name="engine">Opens the written file for the check.</param>
    /// <param name="request">What to write and check.</param>
    /// <param name="progress">Receives 0 to 1 while the temporary file is written (from the PDFium thread).</param>
    /// <param name="cancellationToken">Cancels until the target is replaced; afterwards it has no effect.</param>
    /// <exception cref="SaveFailureException">The save failed; the target is unchanged.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the target is unchanged.</exception>
    /// <exception cref="ObjectDisposedException">The working copy or a source is disposed.</exception>
    /// <exception cref="ArgumentException">The working copy is a scratch document and no source is listed.</exception>
    public static Task<SaveResult> RewriteAsync(
        PdfDocument workingCopy,
        PdfEngine engine,
        RewriteRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        RewriteAsync(workingCopy, engine, request, progress, faults: null, cancellationToken);

    /// <summary>Like the public overload, with hooks for tests.</summary>
    internal static async Task<SaveResult> RewriteAsync(
        PdfDocument workingCopy,
        PdfEngine engine,
        RewriteRequest request,
        IProgress<double>? progress,
        SaveFaults? faults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingCopy);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.OriginalPath, nameof(request));
        ArgumentException.ThrowIfNullOrEmpty(request.TargetPath, nameof(request));
        ArgumentNullException.ThrowIfNull(request.ExpectedPages, nameof(request));
        ArgumentNullException.ThrowIfNull(request.UnchangedSources, nameof(request));
        ArgumentOutOfRangeException.ThrowIfZero(request.ExpectedPages.Count, nameof(request));
        if (request.UnchangedSources.Any(source => source is null))
        {
            throw new ArgumentException("A source document is missing.", nameof(request));
        }

        if (request.RequiresListedSources && request.UnchangedSources.Count == 0)
        {
            // A rebuilt working copy reads from the documents its pages were imported from; with none listed nothing guards them.
            throw new ArgumentException("A rebuilt working copy needs every document it reads from in UnchangedSources.", nameof(request));
        }

        var job = new Job(
            request.OriginalPath,
            request.TargetPath,
            request.OriginalStamp,
            request.OverwriteChangedOriginal,
            request.Password,
            request.UnchangedSources,
            Write: workingCopy.SaveRewriteAsync,
            CheckPrefix: false,
            Check: async (reopened, wasEncrypted, temporaryPath, token) =>
            {
                await CheckRewriteAsync(reopened, request, wasEncrypted, token).ConfigureAwait(false);
                if (request.ExtraCheck is { } extra)
                {
                    await RunExtraCheckAsync(extra, reopened, temporaryPath, token).ConfigureAwait(false);
                }
            },
            ReplaceExisting: request.ReplaceExisting,
            RequiresListedSources: request.RequiresListedSources);
        return await RunAsync(workingCopy, engine, job, progress, faults, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The chain both saves share: source checks, check of the original's path, temporary file, write, (prefix check), reopen,
    /// check, replace. What differs is in <paramref name="job"/>.
    /// </summary>
    private static async Task<SaveResult> RunAsync(PdfDocument document, PdfEngine engine, Job job, IProgress<double>? progress, SaveFaults? faults, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = Path.GetFullPath(job.OriginalPath);
        var target = Path.GetFullPath(job.TargetPath);
        var folder = Path.GetDirectoryName(target)
            ?? throw new SaveFailureException(SaveFailureReason.NotWritable, "The target has no folder.");

        var sourceLength = CheckSources(document, job);
        if (job.OriginalStamp is { } stamp && FileStamp.Of(original) != stamp && SamePath(original, target) && !job.OverwriteChangedOriginal)
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
            await WriteAsync(job.Write, file, sourceLength, progress, faults, cancellationToken).ConfigureAwait(false);
            CopyTargetMetadata(temporary, existing);
            var lateAccessList = OperatingSystem.IsWindows() ? GiveAccessList(temporary, existing.AccessList, faults) : null;
            faults?.AfterWrite?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            // PDFium copied the source while it wrote, which can take seconds: a rewrite in place meanwhile ends up in the
            // copy, and the prefix check would compare it with the same new bytes. So the source is checked again.
            CheckSources(document, job);
            if (job.CheckPrefix)
            {
                await CheckPrefixAsync(temporary, document, sourceLength, cancellationToken).ConfigureAwait(false);
                CheckSources(document, job);
            }

            reopened = await ReopenAsync(engine, temporary, job.Password, cancellationToken).ConfigureAwait(false);
            await job.Check(reopened, wasEncrypted, temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            CheckSources(document, job);
            Replace(temporary, target, folder, job.ReplaceExisting, faults);
            committed = true;
            if (lateAccessList is not null && OperatingSystem.IsWindows())
            {
                // The rules could not be on the file while it was renamed (see GiveAccessList); the replaced file gets them now.
                WriteAccessList(target, lateAccessList);
            }

            return new SaveResult(reopened, StampOf(reopened));
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
    /// Checks that the file PDFium reads is still the file that was opened: PDFium copies its bytes lazily through its own
    /// handle, so a rewrite in place since the open would end up in the saved file. Handle against handle (the stamp now
    /// against <see cref="PdfDocument.SourceStampAtOpen"/>), never against the path, whose stamp describes a symbolic link
    /// instead of the file behind it.
    /// </summary>
    /// <returns>The length of the opened file, which PDFium copies as the first bytes of the save.</returns>
    private static long CheckSource(PdfDocument document)
    {
        FileStamp now;
        FileStamp atOpen;
        try
        {
            now = document.GetSourceStamp();
            atOpen = document.SourceStampAtOpen;
        }
        catch (IOException ex)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "The opened file cannot be read.", ex);
        }

        if (now != atOpen)
        {
            throw new SaveFailureException(SaveFailureReason.SourceModified, "The opened file was changed by another program; it has to be opened again.");
        }

        return atOpen.Length;
    }

    /// <summary>
    /// Checks the document and every extra source (<see cref="CheckSource"/>), always all of them; returns the length of the
    /// document's file, or, for a rebuilt working copy (<see cref="Job.RequiresListedSources"/>), whose own file is a few hundred
    /// bytes, the sum of the lengths of the listed sources, the best guess of the size of the result for the progress.
    /// </summary>
    private static long CheckSources(PdfDocument document, Job job)
    {
        var length = CheckSource(document);
        var listed = 0L;
        foreach (var source in job.ExtraSources)
        {
            listed += CheckSource(source);
        }

        return job.RequiresListedSources ? listed : length;
    }

    /// <summary>
    /// Runs the extra check of a rewrite. A cancellation of the caller passes through; a <see cref="SaveFailureException"/> of the check
    /// becomes <see cref="SaveFailureReason.RedactionFailed"/> with its <see cref="SaveFailureException.Details"/>; any other
    /// exception (a file that cannot be read, a bug) fails the save too, because a check that could not be completed has not passed.
    /// The message is fixed: the exception of the check is the inner exception, and its message may quote what was redacted.
    /// </summary>
    private static async Task RunExtraCheckAsync(Func<RewriteCheckContext, CancellationToken, Task> extra, PdfDocument reopened, string temporaryPath, CancellationToken cancellationToken)
    {
        try
        {
            await extra(new RewriteCheckContext(reopened, temporaryPath), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SaveFailureException ex)
        {
            throw new SaveFailureException(SaveFailureReason.RedactionFailed, "The check of the written file found a problem.", ex) { Details = ex.Details, FilePath = ex.FilePath };
        }
        catch (Exception ex)
        {
            throw new SaveFailureException(SaveFailureReason.RedactionFailed, "The check of the written file could not be completed.", ex);
        }
    }

    /// <summary>
    /// The stamp of the saved file for the next save, through the handle of the reopened document like
    /// <see cref="CheckSource"/>; the replace keeps the file and its times, so the stamp at its open still fits.
    /// </summary>
    private static FileStamp StampOf(PdfDocument reopened)
    {
        try
        {
            return reopened.GetSourceStamp();
        }
        catch (IOException)
        {
            return reopened.SourceStampAtOpen;
        }
    }

    /// <summary>What an existing target passes on to its replacement; a read-only target is refused here.</summary>
    private static TargetInfo ReadTarget(string target)
    {
        FileAttributes attributes;
        UnixFileMode? mode = null;
        byte[]? acl = null;
        try
        {
            if (!File.Exists(target))
            {
                return default;
            }

            attributes = File.GetAttributes(target);
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode Permissions = (UnixFileMode)0x1FF;
                mode = File.GetUnixFileMode(target) & Permissions;
            }
            else
            {
                acl = ReadAccessList(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The target went away or cannot be read; the save then goes on as for a new file.
            return default;
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            throw new SaveFailureException(SaveFailureReason.NotWritable, "The file is read-only.");
        }

        return new TargetInfo(Exists: true, Hidden: (attributes & FileAttributes.Hidden) != 0, UnixMode: mode, AccessList: acl);
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
    /// The access rules (DACL, with the flag that switches inheritance off) of the target as a security descriptor, or
    /// <see langword="null"/> when they cannot be read: the replacement then gets the rules of the folder, as before.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static byte[]? ReadAccessList(string target)
    {
        try
        {
            return new FileInfo(target).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gives the temporary file the access rules of the replaced target, after its content is written (it was open with
    /// <see cref="FileShare.None"/> until now, so nobody else could read it; from its creation until here it has the rules of
    /// the folder, as before the rules were copied). Without this, a file that only its owner may read would come back with
    /// the rules of its folder, which are often much wider. The rename that replaces the target needs DELETE on the
    /// temporary file: if the copied rules do not give that (probed with exactly that access), the rules of the folder are
    /// put back and the target's rules are set on the replaced file instead, right after the rename; the save never fails
    /// for this. A file system without access rules (FAT, some network shares) keeps the rules it has.
    /// </summary>
    /// <returns>The rules to set after the rename, or <see langword="null"/> when there is nothing left to do.</returns>
    [SupportedOSPlatform("windows")]
    private static byte[]? GiveAccessList(string temporary, byte[]? accessList, SaveFaults? faults)
    {
        if (accessList is null)
        {
            return null;
        }

        var inherited = ReadAccessList(temporary);
        if (!WriteAccessList(temporary, accessList))
        {
            return null;
        }

        var canDelete = faults?.CanDelete is { } probe ? probe(temporary) : WindowsFileRename.CanOpenForDelete(temporary);
        if (canDelete)
        {
            return null;
        }

        if (inherited is not null)
        {
            WriteAccessList(temporary, inherited);
        }

        return accessList;
    }

    [SupportedOSPlatform("windows")]
    private static bool WriteAccessList(string file, byte[] descriptor)
    {
        try
        {
            var security = new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            new FileInfo(file).SetAccessControl(security);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or SecurityException)
        {
            // The rules the file has stay; the save itself goes on.
            return false;
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

    /// <summary>Writes the save (<paramref name="write"/>) into <paramref name="file"/>, flushes it to disk and closes it.</summary>
    private static async Task WriteAsync(
        Func<Stream, CancellationToken, Task> write,
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
                await write(counting, cancellationToken).ConfigureAwait(false);
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
    /// Compares the reopened file of a rewrite with what was expected: the page count, the encryption and the fingerprint of
    /// every page up to 50 (then a sample, <see cref="PageEditExecutor.SampledPages"/>), and renders the checked pages.
    /// </summary>
    private static async Task CheckRewriteAsync(PdfDocument reopened, RewriteRequest request, bool wasEncrypted, CancellationToken cancellationToken)
    {
        var expected = request.ExpectedPages;
        if (reopened.PageCount != expected.Count)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, $"The saved file has {reopened.PageCount} pages instead of {expected.Count}.");
        }

        if (wasEncrypted && !(await reopened.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false)).IsEncrypted)
        {
            throw new SaveFailureException(SaveFailureReason.EncryptedNotSupported, "The saved file would lose its encryption.");
        }

        try
        {
            var options = new RenderOptions { Priority = PdfPriority.Background };
            var pages = PageEditExecutor.SampledPages(expected.Count);
            var actual = await PageEditExecutor.FingerprintsAsync(reopened, pages, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < pages.Count; i++)
            {
                if (!Matches(expected[pages[i]], actual[i]))
                {
                    throw new SaveFailureException(SaveFailureReason.VerificationFailed, $"Page {pages[i] + 1} of the saved file is not the page that was expected.");
                }

                var size = await reopened.GetPageSizeAsync(pages[i], cancellationToken).ConfigureAwait(false);
                var longer = Math.Max(size.Width, size.Height);
                var scale = longer > 0 ? Math.Min(1, CheckRenderSize / longer) : 1;
                await reopened.RenderPageAsync(pages[i], scale, options, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or PdfRenderException or ArgumentOutOfRangeException)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "A page of the saved file cannot be read or rendered.", ex);
        }
    }

    /// <summary>Whether two fingerprints describe the same page; sizes may differ by the rounding step (0.5 pt), so a value on a rounding boundary cannot fail a correct file.</summary>
    private static bool Matches(PageFingerprint expected, PageFingerprint actual) =>
        expected.Rotation == actual.Rotation
        && (!expected.CompareText || (expected.TextSetHash == actual.TextSetHash && (!expected.CompareTextOrder || expected.TextHash == actual.TextHash)))
        && Math.Abs(expected.Width - actual.Width) <= 0.5
        && Math.Abs(expected.Height - actual.Height) <= 0.5;

    /// <summary>
    /// Moves the checked temporary file over the target. Linux: one atomic <c>rename</c>, which replaces the target even while
    /// it is open. Windows: the atomic POSIX rename (<see cref="WindowsFileRename.ReplaceAtomically"/>); Windows' ordinary
    /// rename refuses to replace a file another handle has open, even one shared for deleting, and the old document of the
    /// tab always has it open. Only when the file system does not offer the POSIX rename (FAT, exFAT, some SMB servers) is
    /// the ordinary rename tried, and if the open target blocks it, the target is moved aside to <c>.pw-&lt;12 hex&gt;.old</c>
    /// (renaming a file opened shared for deleting is allowed), the temporary file takes its name and the old file is
    /// deleted. If that second move fails, the old file is moved back.
    /// </summary>
    private static void Replace(string temporary, string target, string folder, bool replaceExisting, SaveFaults? faults)
    {
        if (OperatingSystem.IsWindows())
        {
            var error = faults?.AtomicReplace is { } replace ? replace(temporary, target) : WindowsFileRename.ReplaceAtomically(temporary, target, replaceExisting);
            if (error == 0)
            {
                return;
            }

            if (!replaceExisting && error is WindowsFileRename.ErrorAlreadyExists or WindowsFileRename.ErrorFileExists)
            {
                throw TargetExists(new Win32Exception(error));
            }

            if (!WindowsFileRename.IsUnsupported(error))
            {
                throw FailureOfWin32(error);
            }
        }

        if (!replaceExisting)
        {
            // Never replaces: .NET moves without overwriting by linking the new name first and then removing the old one, so a
            // file that appears at the target at any moment before that ends here and stays as it is.
            try
            {
                File.Move(temporary, target, overwrite: false);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw File.Exists(target) || Directory.Exists(target)
                    ? TargetExists(ex)
                    : FailureOf(ex, "The file could not be written.");
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

    /// <summary>
    /// Moves a finished temporary file over (or, without <paramref name="replaceExisting"/>, next to) <paramref name="target"/>
    /// with the same rules as the save of a document: atomic where the file system allows it, and without
    /// <paramref name="replaceExisting"/> never replacing a file that appears at the target meanwhile. For files other than
    /// documents (the PNG files of an image export) that are written to a temporary file in the folder of the target first.
    /// </summary>
    /// <param name="temporary">The full path of the finished temporary file, in the folder of <paramref name="target"/>.</param>
    /// <param name="target">The full path of the file to create or replace.</param>
    /// <param name="replaceExisting">Whether an existing target may be replaced.</param>
    /// <exception cref="SaveFailureException">The move failed; with <see cref="SaveFailureReason.TargetExists"/> a file stands at the target and was not replaced.</exception>
    internal static void MoveIntoPlace(string temporary, string target, bool replaceExisting) =>
        Replace(temporary, target, Path.GetDirectoryName(target) ?? string.Empty, replaceExisting, faults: null);

    /// <summary>A short temporary name independent of the target's name (<c>.pw-&lt;12 hex&gt;.tmp</c>), so a long target name never makes it too long.</summary>
    internal static string TemporaryName() => ShortName(".tmp");

    private static SaveFailureException TargetExists(Exception inner) =>
        new(SaveFailureReason.TargetExists, "A file already exists at the target.", inner);

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

    /// <summary>What one run of the chain does: what it watches, how it writes, whether it checks the prefix, what it checks after the reopen.</summary>
    private sealed record Job(
        string OriginalPath,
        string TargetPath,
        FileStamp? OriginalStamp,
        bool OverwriteChangedOriginal,
        string? Password,
        IReadOnlyList<PdfDocument> ExtraSources,
        Func<Stream, CancellationToken, Task> Write,
        bool CheckPrefix,
        Func<PdfDocument, bool, string, CancellationToken, Task> Check,
        bool ReplaceExisting = true,
        bool RequiresListedSources = false);

    /// <summary>What an existing target passes on to the file that replaces it.</summary>
    private readonly record struct TargetInfo(bool Exists, bool Hidden, UnixFileMode? UnixMode, byte[]? AccessList = null);

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
