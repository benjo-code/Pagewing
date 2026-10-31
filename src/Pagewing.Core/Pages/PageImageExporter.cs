using System.Globalization;
using System.Text;
using Pagewing.Core.Imaging;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.Core.Pages;

/// <summary>Why <see cref="PageImageExporter"/> refused or stopped an export.</summary>
public enum PageExportError
{
    /// <summary>A page would be larger than <see cref="PageImageExporter.MaxPixels"/> (or its size does not fit the arithmetic).</summary>
    TooLarge,

    /// <summary>The target folder does not exist.</summary>
    DirectoryMissing,

    /// <summary>A file (or a folder) already stands where an image would be written, and replacing it was not chosen.</summary>
    TargetExists,

    /// <summary>An image would replace the file the document was opened from.</summary>
    TargetIsSource,

    /// <summary>A file name would lead out of the target folder (cannot happen with the names made here; a guard).</summary>
    UnsafeName,

    /// <summary>More pages were asked for than <see cref="PageImageExporter.MaxPages"/>.</summary>
    TooManyPages,

    /// <summary>An image could not be written (the inner exception says why); <see cref="PageExportException.Page"/> names the page.</summary>
    WriteFailed,
}

/// <summary>An image export that is refused before the first file is written, or that stopped while the files were written.</summary>
public sealed class PageExportException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PageExportException()
        : this(PageExportError.UnsafeName, "The export was refused.", -1, [], null)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public PageExportException(string message)
        : this(PageExportError.UnsafeName, message, -1, [], null)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PageExportException(string message, Exception innerException)
        : this(PageExportError.UnsafeName, message, -1, [], innerException)
    {
    }

    /// <summary>Creates the exception for a page that is above the size limit (<see cref="PageExportError.TooLarge"/>).</summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="message">The message.</param>
    public PageExportException(int page, string message)
        : this(PageExportError.TooLarge, message, page, [], null)
    {
    }

    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    /// <param name="error">Why the export was refused or stopped.</param>
    /// <param name="message">The message.</param>
    /// <param name="page">The 0-based page concerned, or -1.</param>
    /// <param name="paths">The full paths concerned: the files in the way, or the file that failed.</param>
    /// <param name="innerException">What went wrong, or <see langword="null"/>.</param>
    public PageExportException(PageExportError error, string message, int page, IReadOnlyList<string> paths, Exception? innerException)
        : base(message, innerException)
    {
        Error = error;
        Page = page;
        Paths = paths;
    }

    /// <summary>Gets why the export was refused or stopped.</summary>
    public PageExportError Error { get; }

    /// <summary>Gets the 0-based page concerned, or -1.</summary>
    public int Page { get; }

    /// <summary>Gets the full paths concerned: for <see cref="PageExportError.TargetExists"/> and <see cref="PageExportError.TargetIsSource"/> the files in the way, for <see cref="PageExportError.WriteFailed"/> the file that failed.</summary>
    public IReadOnlyList<string> Paths { get; }
}

/// <summary>
/// Renders pages of an open document as PNG files (milestone 13): the one implementation behind <c>pagewing render</c> and the
/// page export. A file is called <c>&lt;stem&gt;-p&lt;NNN&gt;.png</c> with as many digits as the page count needs (at least
/// three). Everything that can be refused is refused <em>before the first file is written</em>: a page above
/// <see cref="MaxPixels"/>, a missing folder, an existing file or folder at a target without <c>overwrite</c>, and the source
/// file itself as a target (also with <c>overwrite</c>). Each file is written to a temporary file in the target folder and moved
/// into place, so a canceled or failed export never leaves a partial PNG.
/// </summary>
public static class PageImageExporter
{
    /// <summary>The most pixels one exported page may have (64 megapixels); above it the page is refused with <see cref="PageExportError.TooLarge"/>.</summary>
    public const long MaxPixels = 64_000_000;

    /// <summary>
    /// Pass as <c>maxPixels</c> to leave the size to the render itself: its own limit (<see cref="PdfRenderException"/> for more
    /// than 256 megapixels or a side beyond the <see cref="int"/> range, with its own text) then applies alone. This is what
    /// <c>pagewing render</c> always did.
    /// </summary>
    public const long RenderLimitOnly = long.MaxValue;

    /// <summary>The most pages one export may name (as many as a document may have pages).</summary>
    public const int MaxPages = PageEditLimits.MaxSlots;

    private const int MinimumDigits = 3;

    // "-p" + the page number (up to six digits) + ".png" is 12 characters; the sanitizer leaves room for them.
    private const int SuffixRoom = 16;

    // The longest file name component: 255 UTF-16 units on Windows, 255 bytes on Linux.
    private const int MaxFileNameLength = 255;

    /// <summary>
    /// Renders <paramref name="pages"/> and writes one PNG per page into <paramref name="directory"/>, one after the other, in
    /// the order of <paramref name="pages"/>. Every render is one short job on the PDFium thread
    /// (<see cref="PdfPriority.Background"/>); encoding and writing happen on the caller's side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stem is used exactly as it is whenever it makes a valid file name with the suffix (so every file name
    /// <c>pagewing render</c> could write keeps its name); anything else (a path separator, a character the system forbids,
    /// a device name of Windows, a name beyond 255 units) is made safe by <see cref="OutputNames.Sanitize(string, int)"/>,
    /// so the files are always directly inside <paramref name="directory"/>.
    /// </para>
    /// <para>
    /// Pages are written one by one. If the token is canceled or a page fails, the pages before it are complete, valid PNG
    /// files and <em>stay</em> (they are in <paramref name="written"/>); the page in progress leaves nothing, not even a
    /// temporary file. Without <paramref name="overwrite"/> the finished file is moved into place without replacing, so a file
    /// that another program creates meanwhile is never replaced.
    /// </para>
    /// </remarks>
    /// <param name="document">The open document.</param>
    /// <param name="stem">The beginning of the file names (usually the file name of the document without extension).</param>
    /// <param name="pages">The 0-based pages, each once; the files are written in this order.</param>
    /// <param name="scale">Pixels per point (1 = 72 dpi); finite and greater than 0.</param>
    /// <param name="annotations">Whether annotations (and form fields) are drawn.</param>
    /// <param name="directory">The folder to write into; it must exist.</param>
    /// <param name="overwrite">Whether an existing file at a target may be replaced. The default of every caller with a user is <see langword="false"/>.</param>
    /// <param name="progress">Receives the share of finished pages after each page (0 to 1, ends at 1), from the thread that runs the export.</param>
    /// <param name="cancellationToken">Cancels before and during a page.</param>
    /// <param name="written">Receives the full path of each file as soon as it is complete (also when a later page fails or the export is canceled), or <see langword="null"/>.</param>
    /// <param name="sourcePath">The file <paramref name="document"/> was opened from, which no image may replace, or <see langword="null"/> when it has none.</param>
    /// <param name="maxPixels">The most pixels a page may have, checked for every page before anything is written (exactly this many are allowed); default <see cref="MaxPixels"/>. <see cref="RenderLimitOnly"/> leaves the check to the render.</param>
    /// <returns>The full paths of the files, in the order of <paramref name="pages"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not finite and positive, or a page is not in the document.</exception>
    /// <exception cref="ArgumentException">A page is listed twice.</exception>
    /// <exception cref="PageExportException">Refused before the first file (a page above the limit, a missing folder, a file in the way, too many pages), or stopped by a failed write (<see cref="PageExportError.WriteFailed"/>; the files before it stay).</exception>
    /// <exception cref="PdfRenderException">A page could not be drawn.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the files before it stay.</exception>
    public static async Task<IReadOnlyList<string>> ExportAsync(
        PdfDocument document,
        string stem,
        IReadOnlyList<int> pages,
        double scale,
        bool annotations,
        string directory,
        bool overwrite,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        ICollection<string>? written = null,
        string? sourcePath = null,
        long maxPixels = MaxPixels)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPixels, 1L);
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be finite and greater than 0.");
        }

        if (pages.Count == 0)
        {
            return [];
        }

        if (pages.Count > MaxPages)
        {
            throw new PageExportException(PageExportError.TooManyPages, "More pages were asked for than a document can have.", -1, [], null);
        }

        CheckPages(document.PageCount, pages);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(root))
        {
            throw new PageExportException(PageExportError.DirectoryMissing, "The target folder does not exist.", -1, [], null);
        }

        var targets = TargetPaths(document.PageCount, pages, stem, root);

        // Everything that can be refused is refused before the first file is written: the size of every page first
        // (before anything is rendered or allocated), then the files in the way.
        for (var i = 0; i < pages.Count && maxPixels != RenderLimitOnly; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CheckSizeAsync(document, pages[i], scale, maxPixels, cancellationToken).ConfigureAwait(false);
        }

        var full = sourcePath is null ? null : Path.GetFullPath(sourcePath);
        var inTheWay = targets.Where(target => CheckTarget(target, full, overwrite) is not null).ToArray();
        if (inTheWay.Length > 0)
        {
            // Everything that is wrong is named at once, and the worst reason (the source itself) decides.
            var error = inTheWay.Any(target => CheckTarget(target, full, overwrite) == PageExportError.TargetIsSource)
                ? PageExportError.TargetIsSource
                : PageExportError.TargetExists;
            throw new PageExportException(
                error,
                error == PageExportError.TargetIsSource
                    ? "An image would replace the file that is open."
                    : "A file already exists at the place of an image. Nothing was written.",
                -1,
                inTheWay,
                null);
        }

        var options = new RenderOptions { Annotations = annotations, Priority = PdfPriority.Background };
        var paths = new List<string>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Checked again right before the write: a file that appeared meanwhile must not be replaced unasked.
            if (CheckTarget(targets[i], full, overwrite) is { } error)
            {
                throw new PageExportException(
                    error,
                    error == PageExportError.TargetIsSource ? "An image would replace the file that is open." : "A file already exists at the place of an image.",
                    pages[i],
                    [targets[i]],
                    null);
            }

            var image = await document.RenderPageAsync(pages[i], scale, options, cancellationToken).ConfigureAwait(false);
            var bytes = PngEncoder.Encode(image);
            await WriteAsync(bytes, root, targets[i], overwrite, pages[i], cancellationToken).ConfigureAwait(false);
            paths.Add(targets[i]);
            written?.Add(targets[i]);
            progress?.Report((double)(i + 1) / pages.Count);
        }

        return paths;
    }

    /// <summary>
    /// The number of pixels of a page of <paramref name="size"/> rendered at <paramref name="scale"/> (each side rounded up, at
    /// least 1, like <see cref="PdfPageSize.ToPixels"/>), or <see cref="long.MaxValue"/> when a side does not fit in an
    /// <see cref="int"/>. Cannot overflow.
    /// </summary>
    internal static long PixelsOf(PdfPageSize size, double scale)
    {
        var width = Math.Max(1, Math.Ceiling(size.Width * scale));
        var height = Math.Max(1, Math.Ceiling(size.Height * scale));
        if (!double.IsFinite(width) || !double.IsFinite(height) || width > int.MaxValue || height > int.MaxValue)
        {
            return long.MaxValue;
        }

        // Both sides are at most int.MaxValue, so the product is below 2^62.
        return (long)width * (long)height;
    }

    /// <summary>Whether a page of <paramref name="size"/> rendered at <paramref name="scale"/> has more than <paramref name="maxPixels"/> pixels (exactly <paramref name="maxPixels"/> is allowed).</summary>
    internal static bool Exceeds(PdfPageSize size, double scale, long maxPixels) => PixelsOf(size, scale) > maxPixels;

    private static async Task CheckSizeAsync(PdfDocument document, int page, double scale, long maxPixels, CancellationToken cancellationToken)
    {
        var sizes = await document.GetPageSizesAsync(page, 1, PdfPriority.Background, cancellationToken).ConfigureAwait(false);
        if (!Exceeds(sizes[0], scale, maxPixels))
        {
            return;
        }

        var pixels = PixelsOf(sizes[0], scale);
        var limit = maxPixels / 1_000_000.0;
        var text = pixels == long.MaxValue
            ? string.Create(CultureInfo.InvariantCulture, $"Page {page + 1} would be far larger than the limit of {limit:0.###} megapixels at this resolution. Use a lower resolution.")
            : string.Create(CultureInfo.InvariantCulture, $"Page {page + 1} would have {pixels / 1_000_000.0:0.#} megapixels at this resolution; the limit is {limit:0.###} megapixels. Use a lower resolution.");
        throw new PageExportException(page, text);
    }

    private static void CheckPages(int pageCount, IReadOnlyList<int> pages)
    {
        var seen = new HashSet<int>(pages.Count);
        foreach (var page in pages)
        {
            if (page < 0 || page >= pageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(pages), page, "A page is not in the document.");
            }

            if (!seen.Add(page))
            {
                throw new ArgumentException("A page is listed twice.", nameof(pages));
            }
        }
    }

    private static string[] TargetPaths(int pageCount, IReadOnlyList<int> pages, string stem, string root)
    {
        var digits = Math.Max(MinimumDigits, pageCount.ToString(CultureInfo.InvariantCulture).Length);
        var format = "D" + digits.ToString(CultureInfo.InvariantCulture);
        var name = IsPlain(stem, digits)
            ? stem
            : OutputNames.Sanitize(stem, OutputNames.DefaultMaxLength - SuffixRoom, OutputNames.MaxStemBytes - SuffixRoom);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var targets = new string[pages.Count];
        for (var i = 0; i < pages.Count; i++)
        {
            var file = string.Create(CultureInfo.InvariantCulture, $"{name}-p{(pages[i] + 1).ToString(format, CultureInfo.InvariantCulture)}.png");

            // Defence in depth: whatever the stem was, the result must be a plain file directly in the folder.
            var path = Path.GetFullPath(Path.Combine(root, file));
            var parent = Path.GetDirectoryName(path);
            if (parent is null
                || !string.Equals(Path.TrimEndingDirectorySeparator(parent), root, comparison)
                || !string.Equals(Path.GetFileName(path), file, comparison))
            {
                throw new PageExportException(PageExportError.UnsafeName, "A file name would lead out of the target folder.", pages[i], [], null);
            }

            targets[i] = path;
        }

        return targets;
    }

    // A stem that makes a valid file name with the suffix stays exactly as it is: everything "pagewing render" ever wrote keeps
    // its name (also an empty stem, a leading or trailing space or dot, a long one). Only what cannot be a file name here is
    // made safe: a path separator, a character the system forbids in file names (platform dependent, as before), a lone
    // surrogate, a name beyond the 255 units a file name may have, or a device name of Windows (the part before the first dot
    // of the whole name: "con" is fine, "nul.txt" is not).
    private static bool IsPlain(string stem, int digits)
    {
        if (stem.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) >= 0
            || stem.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !IsWellFormed(stem))
        {
            return false;
        }

        var suffix = 2 + digits + ".png".Length;
        var length = OperatingSystem.IsWindows() ? stem.Length + suffix : Encoding.UTF8.GetByteCount(stem) + suffix;
        return length <= MaxFileNameLength && !OutputNames.IsDeviceName(stem + "-p000.png");
    }

    private static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    // Returns why a target cannot be written, or null when it can.
    private static PageExportError? CheckTarget(string target, string? sourcePath, bool overwrite)
    {
        if (Directory.Exists(target))
        {
            return PageExportError.TargetExists;
        }

        if (!File.Exists(target) && !IsLink(target))
        {
            return null;
        }

        // The file that is open is never a target: the same file under another name (link, 8.3 name, hard link) counts.
        if (sourcePath is not null
            && (string.Equals(target, sourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || FileIdentity.AreSameFile(target, sourcePath) == true))
        {
            return PageExportError.TargetIsSource;
        }

        return overwrite ? null : PageExportError.TargetExists;
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

    // Writes the bytes to a temporary file in the target folder, then moves it into place with the rules of the document save.
    private static async Task WriteAsync(byte[] bytes, string root, string target, bool overwrite, int page, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(root, DocumentSaver.TemporaryName());
        try
        {
            var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            });
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Once the file is moved into place the page is complete, whatever the token says now.
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSaver.MoveIntoPlace(temporary, target, overwrite);
        }
        catch (SaveFailureException ex) when (ex.Reason == SaveFailureReason.TargetExists)
        {
            throw new PageExportException(PageExportError.TargetExists, "A file appeared at the place of an image.", page, [target], ex);
        }
        catch (Exception ex) when (ex is SaveFailureException or IOException or UnauthorizedAccessException)
        {
            throw new PageExportException(PageExportError.WriteFailed, $"An image could not be written: {target}: {Innermost(ex).Message}", page, [target], ex);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    // The reason the system gave ("Access to the path ... is denied"), not the wrapper of the save around it.
    private static Exception Innermost(Exception error) => error is SaveFailureException { InnerException: { } inner } ? inner : error;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do with it; the name is unique and starts with ".pw-".
        }
    }
}
