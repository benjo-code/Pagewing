using System.Globalization;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Viewing;

namespace Pagewing.Core.Pages;

/// <summary>One file a split or an extract writes.</summary>
/// <param name="Stem">The title the file name is made from (a bookmark title, a generated name): never used as a file name as it is, <see cref="OutputNames"/> makes it one.</param>
/// <param name="Pages">The 0-based pages of the open document the file gets, strictly ascending (the order of the document); at least one.</param>
public sealed record SplitPart(string Stem, IReadOnlyList<int> Pages);

/// <summary>The open document of a session with the file it was opened from (<see cref="DocumentSplitter.SourceOf"/>).</summary>
/// <param name="Document">The open document.</param>
/// <param name="File">Its file with the password it was opened with and the stamp taken when it was opened; <see cref="object.ToString"/> does not show the password.</param>
public sealed record SplitSource(PdfDocument Document, PageSourceFile File);

/// <summary>How <see cref="DocumentSplitter.Plan"/> cuts a document.</summary>
public abstract record SplitMode
{
    private SplitMode()
    {
    }

    /// <summary>Cuts after every <paramref name="pages"/> pages; the last part keeps the rest.</summary>
    /// <param name="pages">The number of pages of a part, at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pages"/> is less than 1.</exception>
    public static SplitMode Every(int pages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pages, 1);
        return new EveryMode(pages);
    }

    /// <summary>One part per comma separated range of <paramref name="text"/> (<c>1-3,4-9</c>, in the syntax of <see cref="PageRange"/>); ranges may overlap.</summary>
    /// <param name="text">The ranges.</param>
    public static SplitMode Ranges(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new RangesMode(text);
    }

    /// <summary>One part per target page of a top-level bookmark.</summary>
    /// <param name="outline">The outline of the document (<see cref="PdfDocument.GetOutlineAsync"/>); only its top level is read.</param>
    public static SplitMode Bookmarks(IReadOnlyList<PdfOutlineItem> outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        return new BookmarksMode(outline);
    }

    internal sealed record EveryMode(int Pages) : SplitMode;

    internal sealed record RangesMode(string Text) : SplitMode;

    internal sealed record BookmarksMode(IReadOnlyList<PdfOutlineItem> Outline) : SplitMode;
}

/// <summary>Why a split was refused.</summary>
public enum DocumentSplitError
{
    /// <summary>The document has no top-level bookmark with a target page.</summary>
    NoBookmarks,

    /// <summary>The target folder does not exist.</summary>
    DirectoryMissing,

    /// <summary>A file (or a folder) already stands where a part would be written, and replacing it was not chosen.</summary>
    TargetExists,

    /// <summary>A part would replace the file the document was opened from.</summary>
    TargetIsSource,

    /// <summary>A file name would lead out of the target folder (cannot happen with <see cref="OutputNames"/>; a guard).</summary>
    UnsafeName,

    /// <summary>
    /// A part failed while the parts were written (the inner exception says why: <see cref="SaveFailureException"/>,
    /// <see cref="PdfOpenException"/> …); <see cref="DocumentSplitException.PartIndex"/> and <see cref="DocumentSplitException.Paths"/>
    /// name it. A file that appeared at its place meanwhile is <see cref="TargetExists"/> instead, with the same details.
    /// </summary>
    PartFailed,
}

/// <summary>A split that is refused before the first byte is written.</summary>
public sealed class DocumentSplitException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DocumentSplitException()
        : this(DocumentSplitError.UnsafeName)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public DocumentSplitException(string message)
        : this(DocumentSplitError.UnsafeName, message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public DocumentSplitException(string message, Exception innerException)
        : this(DocumentSplitError.UnsafeName, message, [], -1, innerException)
    {
    }

    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    /// <param name="error">Why the split was refused.</param>
    public DocumentSplitException(DocumentSplitError error)
        : this(error, $"The split was refused: {error}.")
    {
    }

    /// <summary>Creates the exception for <paramref name="error"/> with a message.</summary>
    /// <param name="error">Why the split was refused.</param>
    /// <param name="message">The message.</param>
    public DocumentSplitException(DocumentSplitError error, string message)
        : this(error, message, [])
    {
    }

    /// <summary>Creates the exception for <paramref name="error"/> with a message and the paths concerned.</summary>
    /// <param name="error">Why the split was refused.</param>
    /// <param name="message">The message.</param>
    /// <param name="paths">The full paths concerned (the files in the way).</param>
    public DocumentSplitException(DocumentSplitError error, string message, IReadOnlyList<string> paths)
        : this(error, message, paths, -1, null)
    {
    }

    /// <summary>Creates the exception for a part that failed while the parts were written.</summary>
    /// <param name="error">Why the split stopped.</param>
    /// <param name="message">The message.</param>
    /// <param name="paths">The full path of the failing part's file.</param>
    /// <param name="partIndex">The 0-based index of the failing part.</param>
    /// <param name="innerException">What went wrong.</param>
    public DocumentSplitException(DocumentSplitError error, string message, IReadOnlyList<string> paths, int partIndex, Exception? innerException)
        : base(message, innerException)
    {
        Error = error;
        Paths = paths;
        PartIndex = partIndex;
    }

    /// <summary>Gets why the split was refused.</summary>
    public DocumentSplitError Error { get; }

    /// <summary>Gets the 0-based index of the part that failed while the parts were written (<see cref="DocumentSplitError.PartFailed"/>, and <see cref="DocumentSplitError.TargetExists"/> at the moment of writing), or -1.</summary>
    public int PartIndex { get; }

    /// <summary>Gets the full paths concerned: for <see cref="DocumentSplitError.TargetExists"/> and <see cref="DocumentSplitError.TargetIsSource"/> the files in the way.</summary>
    public IReadOnlyList<string> Paths { get; }
}

/// <summary>
/// Cuts a document into parts (every <i>n</i> pages, by ranges, by bookmarks), tells what each part loses, and writes the
/// parts as new files (milestone 13). A part is a page plan that keeps some pages of the open document, written through the
/// same checked, atomic full rewrite as the page panel's "Apply" (<see cref="PageEditApplier.WriteAsync"/>); an extract is one
/// part. The original is never touched, and an existing file is replaced only on the explicit choice <c>overwrite</c>.
/// </summary>
public static class DocumentSplitter
{
    /// <summary>The most parts one split or extract may have (as many as a document may have pages).</summary>
    public const int MaxParts = PageEditLimits.MaxSlots;

    /// <summary>The most pages all parts together may hold (overlapping ranges count each time), checked before they are listed.</summary>
    public const int MaxTotalPages = 100_000;

    // The longest generated suffix: "-p" + two page numbers of up to five digits + "-" is 13 characters.
    private const int SuffixRoom = 16;

    private static readonly IReadOnlyDictionary<PageSourceId, PdfDocument> NoSources = new Dictionary<PageSourceId, PdfDocument>();

    private static readonly IReadOnlyDictionary<PageSourceId, PageSourceFile> NoFiles = new Dictionary<PageSourceId, PageSourceFile>();

    /// <summary>
    /// Reads the open document of <paramref name="session"/> together with its file, password and stamp (one consistent view,
    /// as <see cref="PageEditApplier.ApplyAsync(ViewerSession, PdfEngine, PageApplyRequest, IProgress{double}?, CancellationToken, Func{PdfDocument, PdfEngine, RewriteRequest, IProgress{double}?, CancellationToken, Task{SaveResult}}?)"/>
    /// reads it), so that a caller outside this assembly can <see cref="AnalyzeAsync"/> and <see cref="WriteAsync"/> what the user sees.
    /// </summary>
    /// <param name="session">The session of the tab.</param>
    /// <returns>The source, or <see langword="null"/> when the session has no open document (or it was closed just now).</returns>
    public static SplitSource? SourceOf(ViewerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SnapshotForSave() is not { } snapshot)
        {
            return null;
        }

        try
        {
            return new SplitSource(snapshot.Document, PageSourceFile.Of(snapshot.Path, snapshot.Password, snapshot.Document));
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Works out the parts of a document. Pure: reads nothing but its arguments.</summary>
    /// <param name="pageCount">The number of pages of the open document.</param>
    /// <param name="mode">How to cut.</param>
    /// <param name="baseName">The name the generated stems start with (the file name of the document, without extension).</param>
    /// <returns>The parts in the order of the files; the stems are not yet file names.</returns>
    /// <exception cref="PageEditException">The document has no pages or more than a plan holds, or the split would have too many parts or pages (<see cref="PageEditError.LimitExceeded"/>).</exception>
    /// <exception cref="FormatException">A range is not valid (<see cref="PageRange"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A range ends after the document.</exception>
    /// <exception cref="DocumentSplitException">With <see cref="SplitMode.Bookmarks"/>: no top-level bookmark has a target (<see cref="DocumentSplitError.NoBookmarks"/>).</exception>
    public static IReadOnlyList<SplitPart> Plan(int pageCount, SplitMode mode, string baseName = "part")
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(baseName);
        if (pageCount < 1)
        {
            throw new PageEditException(PageEditError.OutOfRange, "A document has at least one page.");
        }

        if (pageCount > PageEditLimits.MaxSlots)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "The document has more pages than a plan can hold.");
        }


        // The generated suffix ("-p00001-00003", "-front") is added to this name and must survive the later sanitizing of the
        // whole stem (80 elements, 160 bytes), so the name leaves room for the longest suffix.
        var name = OutputNames.Sanitize(baseName, OutputNames.DefaultMaxLength - SuffixRoom, OutputNames.MaxStemBytes - SuffixRoom);
        return mode switch
        {
            SplitMode.EveryMode every => PlanEvery(pageCount, every.Pages, name),
            SplitMode.RangesMode ranges => PlanRanges(pageCount, ranges.Text, name),
            SplitMode.BookmarksMode bookmarks => PlanBookmarks(pageCount, bookmarks.Outline, name),
            _ => throw new ArgumentException("Unknown split mode.", nameof(mode)),
        };
    }

    /// <summary>Makes the one part of an extract: the pages of <paramref name="range"/> in the order of the document.</summary>
    /// <param name="pageCount">The number of pages of the open document.</param>
    /// <param name="range">The pages (<see cref="PageRange"/> syntax).</param>
    /// <param name="stem">The title of the file.</param>
    /// <exception cref="FormatException">The range is not valid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range ends after the document.</exception>
    public static SplitPart Extract(int pageCount, string range, string stem)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(stem);
        return new SplitPart(stem, PageRange.Parse(range, pageCount));
    }

    /// <summary>The page plan of a part: the pages of the part, in order, nothing else.</summary>
    /// <param name="pageCount">The number of pages of the open document.</param>
    /// <param name="part">The part.</param>
    /// <exception cref="PageEditException">The part has no pages, a page twice or out of order, or one that the document does not have.</exception>
    public static PageEditPlan PlanOf(int pageCount, SplitPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        CheckPart(pageCount, part);
        var keep = new bool[pageCount];
        foreach (var page in part.Pages)
        {
            keep[page] = true;
        }

        var drop = new List<int>(pageCount - part.Pages.Count);
        for (var i = 0; i < keep.Length; i++)
        {
            if (!keep[i])
            {
                drop.Add(i);
            }
        }

        return PageEditPlan.Identity(pageCount).Delete(drop);
    }

    /// <summary>
    /// Tells what writing each part loses, couples or invalidates (<see cref="PageEditExecutor.AnalyzeAsync"/> per part), to be
    /// shown <em>before</em> <see cref="WriteAsync"/>. A part is a new file, so a signature of the document is invalidated in
    /// every part. Bookmarks that point to pages of other parts, form fields of other parts and page labels are listed as for
    /// a deletion of those pages.
    /// </summary>
    /// <remarks>
    /// The caller must refuse a split when some report <see cref="PageEditReport.IsRefused"/>. A split takes no pages from other
    /// files, so no loss blocks today; the check is still the contract.
    /// </remarks>
    /// <param name="source">The open document.</param>
    /// <param name="parts">The parts.</param>
    /// <param name="cancellationToken">Cancels before the next job.</param>
    /// <returns>One report per part, in the order of <paramref name="parts"/>.</returns>
    /// <exception cref="PageEditException">A part does not fit the document.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<IReadOnlyList<PageEditReport>> AnalyzeAsync(
        PdfDocument source,
        IReadOnlyList<SplitPart> parts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parts);
        CheckParts(source.PageCount, parts);
        var reports = new List<PageEditReport>(parts.Count);
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = PlanOf(source.PageCount, part);
            reports.Add(await PageEditExecutor.AnalyzeAsync(plan, source, NoSources, cancellationToken).ConfigureAwait(false));
        }

        return reports;
    }

    /// <summary>
    /// Makes the full paths of the files the parts are written to: the stems made safe and different from each other without
    /// regard to case, with the extension <c>.pdf</c>, directly inside <paramref name="directory"/>. Does not look at the file
    /// system (a file that already exists is <see cref="WriteAsync"/>'s concern), so the same call gives the same paths.
    /// </summary>
    /// <param name="parts">The parts.</param>
    /// <param name="directory">The target folder.</param>
    /// <exception cref="DocumentSplitException">A name would lead out of the folder (<see cref="DocumentSplitError.UnsafeName"/>; a guard that <see cref="OutputNames"/> already makes unreachable).</exception>
    public static IReadOnlyList<string> TargetPaths(IReadOnlyList<SplitPart> parts, string directory)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var names = OutputNames.Unique(parts.Select(static part => part.Stem), ".pdf", static _ => false);
        var paths = new List<string>(names.Count);
        foreach (var name in names)
        {
            // Defence in depth: whatever the sanitizer does, the result must be a plain file directly in the folder.
            var full = Path.GetFullPath(Path.Combine(root, name));
            var parent = Path.GetDirectoryName(full);
            if (name.AsSpan().IndexOfAny('/', '\\') >= 0
                || parent is null
                || !string.Equals(Path.TrimEndingDirectorySeparator(parent), root, comparison)
                || !string.Equals(Path.GetFileName(full), name, comparison))
            {
                throw new DocumentSplitException(DocumentSplitError.UnsafeName, "A file name would lead out of the target folder.");
            }

            paths.Add(full);
        }

        return paths;
    }

    /// <summary>
    /// Writes every part as a new file in <paramref name="directory"/> (<see cref="TargetPaths"/>), one after the other, each
    /// through the checked, atomic full rewrite of <see cref="PageEditApplier.WriteAsync"/> from a working copy of the file of
    /// the open document. Everything that can be refused is refused <em>before the first byte</em>: a missing folder, a part that
    /// does not fit the document, an existing file or folder at a target without <paramref name="overwrite"/>, and the source
    /// file itself as a target (always, also with <paramref name="overwrite"/>). The original is never touched. An encrypted
    /// document gives encrypted parts or fails (<see cref="SaveFailureReason.EncryptedNotSupported"/>), never a decrypted file.
    /// </summary>
    /// <remarks>
    /// Parts are written one by one. If a part fails or the token is canceled, the parts before it are complete, checked files
    /// and <em>stay</em> (they are listed in <paramref name="written"/>); the failing part leaves nothing, not even a
    /// temporary file. The caller owns the decision whether to delete the written parts. Because a file could appear between the
    /// check and the write, the target is checked again right before each part, and without <paramref name="overwrite"/> the
    /// checked file is moved into place without replacing (<see cref="RewriteRequest.ReplaceExisting"/>), so a file created
    /// meanwhile by another program is never replaced. The caller ran <see cref="AnalyzeAsync"/> and showed the result.
    /// </remarks>
    /// <param name="engine">Opens the working copies and the written files.</param>
    /// <param name="source">The open document, opened from <paramref name="file"/>.</param>
    /// <param name="file">The file <paramref name="source"/> was opened from, with its password and the stamp taken when it was opened (<see cref="PageSourceFile.Of"/>). It must be the file of the document that is shown.</param>
    /// <param name="parts">The parts.</param>
    /// <param name="directory">The folder to write into; it must exist.</param>
    /// <param name="overwrite">Whether an existing file at a target may be replaced. The default of every caller is <see langword="false"/>.</param>
    /// <param name="progress">Receives 0 to 1 over all parts (from the PDFium thread); never goes back.</param>
    /// <param name="cancellationToken">Cancels before and during a part; a part that is moved into place stays.</param>
    /// <param name="written">Receives the full path of each part as soon as it is complete (also when a later part fails), or <see langword="null"/>.</param>
    /// <returns>The full paths of the files, in the order of <paramref name="parts"/>.</returns>
    /// <exception cref="DocumentSplitException">Refused before the first byte (see above).</exception>
    /// <exception cref="PageEditException">A part does not fit the document (checked before the first byte).</exception>
    /// <exception cref="DocumentSplitException">
    /// Also when a part fails while the parts are written: <see cref="DocumentSplitError.PartFailed"/> with
    /// <see cref="DocumentSplitException.PartIndex"/>, the part's path in <see cref="DocumentSplitException.Paths"/> and the cause
    /// (<see cref="SaveFailureException"/> for a failed rewrite or check or a changed source file, <see cref="PdfOpenException"/> …)
    /// as <see cref="Exception.InnerException"/>; a file that another program created at the place of a part meanwhile is
    /// <see cref="DocumentSplitError.TargetExists"/> (never replaced without <paramref name="overwrite"/>). The parts before it stay.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the parts before it stay.</exception>
    public static async Task<IReadOnlyList<string>> WriteAsync(
        PdfEngine engine,
        PdfDocument source,
        PageSourceFile file,
        IReadOnlyList<SplitPart> parts,
        string directory,
        bool overwrite,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        ICollection<string>? written = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (parts.Count == 0)
        {
            throw new ArgumentException("There is nothing to write.", nameof(parts));
        }

        CheckParts(source.PageCount, parts);
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new DocumentSplitException(DocumentSplitError.DirectoryMissing, "The target folder does not exist.");
        }

        var sourcePath = Path.GetFullPath(file.Path);
        var targets = TargetPaths(parts, root);
        var inTheWay = targets.Where(target => CheckTarget(target, sourcePath, overwrite) is not null).ToArray();
        if (inTheWay.Length > 0)
        {
            // Everything that is wrong is named at once, and the worst reason (the source itself) decides.
            var error = inTheWay.Any(target => CheckTarget(target, sourcePath, overwrite) == DocumentSplitError.TargetIsSource)
                ? DocumentSplitError.TargetIsSource
                : DocumentSplitError.TargetExists;
            throw new DocumentSplitException(
                error,
                error == DocumentSplitError.TargetIsSource
                    ? "A part would replace the file that is open."
                    : "A file already exists at the place of a part. Nothing was written.",
                inTheWay);
        }

        for (var i = 0; i < parts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Checked again right before the write: a file that appeared meanwhile must not be replaced unasked.
            if (CheckTarget(targets[i], sourcePath, overwrite) is { } error)
            {
                throw new DocumentSplitException(
                    error,
                    error == DocumentSplitError.TargetIsSource ? "A part would replace the file that is open." : "A file already exists at the place of a part.",
                    [targets[i]]);
            }

            try
            {
                // Without overwrite nothing is ever replaced, not even a file that another program creates while the part is
                // written: the move into place then fails with TargetExists and leaves that file alone.
                var request = new PageApplyRequest(PlanOf(source.PageCount, parts[i]), targets[i], NoFiles, ReplaceExisting: overwrite);
                var saved = await PageEditApplier.WriteAsync(
                    engine,
                    source,
                    sourcePath,
                    file.Password,
                    file.Stamp,
                    request,
                    new Slice(progress, i, parts.Count),
                    cancellationToken).ConfigureAwait(false);
                await CloseQuietlyAsync(saved.Reopened).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var appeared = ex is SaveFailureException { Reason: SaveFailureReason.TargetExists };
                throw new DocumentSplitException(
                    appeared ? DocumentSplitError.TargetExists : DocumentSplitError.PartFailed,
                    appeared ? "A file appeared at the place of a part." : "A part could not be written.",
                    [targets[i]],
                    i,
                    ex);
            }

            written?.Add(targets[i]);
        }

        progress?.Report(1);
        return targets;
    }

    // Returns why a target cannot be written, or null when it can.
    private static DocumentSplitError? CheckTarget(string target, string sourcePath, bool overwrite)
    {
        if (Directory.Exists(target))
        {
            return DocumentSplitError.TargetExists;
        }

        if (!File.Exists(target) && !IsLink(target))
        {
            return null;
        }

        // The file that is open is never a target: the same file under another name (link, 8.3 name, hard link) counts.
        if (string.Equals(target, sourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || FileIdentity.AreSameFile(target, sourcePath) == true)
        {
            return DocumentSplitError.TargetIsSource;
        }

        return overwrite ? null : DocumentSplitError.TargetExists;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CheckParts(int pageCount, IReadOnlyList<SplitPart> parts)
    {
        if (parts.Count > MaxParts)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "The split would have more parts than allowed.");
        }

        long total = 0;
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            CheckPart(pageCount, part);
            total += part.Pages.Count;
            if (total > MaxTotalPages)
            {
                throw new PageEditException(PageEditError.LimitExceeded, "The split would write more pages than allowed.");
            }
        }
    }

    private static void CheckPart(int pageCount, SplitPart part)
    {
        ArgumentNullException.ThrowIfNull(part.Stem);
        ArgumentNullException.ThrowIfNull(part.Pages);
        if (part.Pages.Count == 0)
        {
            throw new PageEditException(PageEditError.OutOfRange, "A part needs at least one page.");
        }

        if (part.Pages.Count > pageCount)
        {
            throw new PageEditException(PageEditError.OutOfRange, "A part has more pages than the document.");
        }

        var previous = -1;
        foreach (var page in part.Pages)
        {
            if (page < 0 || page >= pageCount)
            {
                throw new PageEditException(PageEditError.OutOfRange, "A page of a part is not in the document.");
            }

            if (page <= previous)
            {
                throw new PageEditException(PageEditError.OutOfRange, "The pages of a part are listed once each, in the order of the document.");
            }

            previous = page;
        }
    }

    private static List<SplitPart> PlanEvery(int pageCount, int every, string name)
    {
        var count = ((pageCount - 1) / every) + 1;
        var width = Width(pageCount);
        var parts = new List<SplitPart>(count);
        for (var first = 0; first < pageCount; first += every)
        {
            var last = Math.Min(first + every, pageCount) - 1;
            parts.Add(new SplitPart(StemOf(name, first, last, width), Range(first, last)));
        }

        return parts;
    }

    private static List<SplitPart> PlanRanges(int pageCount, string text, string name)
    {
        // The limits are checked on the text before it is split or parsed.
        var commas = text.AsSpan().Count(',');
        if (commas >= MaxParts)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "The split would have more parts than allowed.");
        }

        var width = Width(pageCount);
        var parts = new List<SplitPart>(commas + 1);
        long total = 0;
        foreach (var segment in text.Split(','))
        {
            // A segment is one contiguous range ("all" too), parsed (and bounds checked) by the shared syntax of the CLI and the panel.
            var pages = PageRange.Parse(segment, pageCount);
            total += pages.Count;
            if (total > MaxTotalPages)
            {
                throw new PageEditException(PageEditError.LimitExceeded, "The split would write more pages than allowed.");
            }

            parts.Add(new SplitPart(StemOf(name, pages[0], pages[^1], width), pages));
        }

        return parts;
    }

    private static List<SplitPart> PlanBookmarks(int pageCount, IReadOnlyList<PdfOutlineItem> outline, string name)
    {
        // Top level only; a bookmark without a target in this document is skipped; the sort is stable, so of several
        // bookmarks on one page the first of the outline gives the name.
        var starts = new List<(int Page, string Title)>();
        foreach (var item in outline)
        {
            if (item.PageIndex is { } page && page >= 0 && page < pageCount)
            {
                starts.Add((page, item.Title));
            }
        }

        if (starts.Count == 0)
        {
            throw new DocumentSplitException(DocumentSplitError.NoBookmarks, "The document has no top-level bookmark with a target page.");
        }

        var ordered = starts.Select(static (start, index) => (start.Page, start.Title, Index: index))
            .OrderBy(static start => start.Page)
            .ThenBy(static start => start.Index)
            .ToList();
        var parts = new List<SplitPart>();
        if (ordered[0].Page > 0)
        {
            parts.Add(new SplitPart(name + "-front", Range(0, ordered[0].Page - 1)));
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && ordered[i].Page == ordered[i - 1].Page)
            {
                continue;
            }

            var next = i + 1;
            while (next < ordered.Count && ordered[next].Page == ordered[i].Page)
            {
                next++;
            }

            var end = next < ordered.Count ? ordered[next].Page - 1 : pageCount - 1;
            parts.Add(new SplitPart(ordered[i].Title, Range(ordered[i].Page, end)));
        }

        return parts;
    }

    private static int[] Range(int first, int last)
    {
        var pages = new int[last - first + 1];
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i] = first + i;
        }

        return pages;
    }

    private static int Width(int pageCount) => Math.Max(3, pageCount.ToString(CultureInfo.InvariantCulture).Length);

    // "<name>-p001" for one page, "<name>-p001-003" for a range (1-based).
    private static string StemOf(string name, int first, int last, int width)
    {
        var from = (first + 1).ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (first == last)
        {
            return $"{name}-p{from}";
        }

        var to = (last + 1).ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return $"{name}-p{from}-{to}";
    }

    // A document that fails to close must not hide the failure (or the result) of the write.
    private static async Task CloseQuietlyAsync(PdfDocument document)
    {
        try
        {
            await document.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing more to do with it.
        }
    }

    /// <summary>Part <c>index</c> of <c>count</c> of the progress bar: 0 to 1 of the part becomes its share of the whole.</summary>
    private sealed class Slice(IProgress<double>? target, int index, int count) : IProgress<double>
    {
        public void Report(double value) => target?.Report(Math.Min(1, (index + Math.Clamp(value, 0, 1)) / count));
    }
}
