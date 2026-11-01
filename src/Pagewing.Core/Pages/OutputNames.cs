using System.Globalization;
using System.Text;

namespace Pagewing.Core.Pages;

/// <summary>
/// File names for the files a split or an extract writes. The text comes from the document (bookmark titles), so it is hostile:
/// it may hold path separators, <c>..</c>, device names of Windows, control and bidirectional characters, endless text. The
/// result of <see cref="Sanitize"/> is one safe file name component; <see cref="Unique"/> makes names differ from each other and
/// from the files that already exist, so that no existing file is ever replaced by accident.
/// </summary>
public static class OutputNames
{
    /// <summary>The default length limit of <see cref="Sanitize"/> in text elements.</summary>
    public const int DefaultMaxLength = 80;

    // Whatever the text elements are, a name stays far below the 255 bytes (UTF-8, ext4) or 255 UTF-16 units (NTFS) of a file
    // name component, also with a number and the extension added.
    internal const int MaxStemBytes = 160;

    private const int MaxStemChars = 200;

    // Only the beginning of a huge text is read; what lies beyond the limit is never needed.
    private const int MaxScanned = 4096;

    private const int MaxNumber = 100_000;

    private const int MaxExtensionLength = 16;

    private const string Fallback = "part";

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$"];

    /// <summary>
    /// Turns a title into one safe file name component (without extension): no path separator, no drive or stream separator,
    /// no <c>..</c>, no control, format (bidirectional, zero width, byte order mark) or separator character, none of the
    /// characters Windows forbids, no device name of Windows (<c>CON</c>, <c>NUL</c>, <c>COM1</c> …, also before an extension),
    /// no leading or trailing dot or space; the empty result is <c>part</c>. The length is limited in text elements, so a
    /// surrogate pair or a letter with combining marks is never cut, and in bytes, so one element with thousands of combining
    /// marks cannot make a huge name.
    /// </summary>
    /// <param name="title">The text, for example a bookmark title.</param>
    /// <param name="maxLength">The most text elements the name keeps (at least 1); the bytes are limited as well.</param>
    /// <returns>A name that is safe on Windows, Linux and macOS.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="title"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is less than 1.</exception>
    public static string Sanitize(string title, int maxLength = DefaultMaxLength) => Sanitize(title, maxLength, MaxStemBytes);

    /// <summary>
    /// Like <see cref="Sanitize(string, int)"/> with a smaller byte limit, for a name that gets a suffix of known size after
    /// it (a generated stem such as <c>Name-p001-003</c>): the limits leave room, so the later <see cref="Unique"/> does not cut
    /// the suffix off again.
    /// </summary>
    /// <param name="title">The text.</param>
    /// <param name="maxLength">The most text elements (at least 1).</param>
    /// <param name="maxBytes">The most UTF-8 bytes (at least 1, at most <see cref="MaxStemBytes"/>).</param>
    internal static string Sanitize(string title, int maxLength, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        var limit = Math.Min(maxLength, MaxStemChars);
        var bytes = Math.Min(maxBytes, MaxStemBytes);
        var cleaned = Clean(title);
        var name = Truncate(cleaned, limit, bytes);
        if (name.Length == 0)
        {
            return Fallback;
        }

        if (IsDeviceName(name))
        {
            // A prefix cannot make it longer than allowed: the name is cut again.
            name = Truncate("_" + name, limit, bytes);
        }

        return name;
    }

    /// <summary>
    /// Makes the file names of a list of titles: each title is sanitized and given the extension; a name that an earlier one of
    /// the list already has (compared without regard to case, because Windows and macOS file systems do not tell
    /// <c>A.pdf</c> from <c>a.pdf</c>) or that <paramref name="exists"/> reports gets a number: <c>Name (2).pdf</c>.
    /// </summary>
    /// <param name="stems">The titles, in order.</param>
    /// <param name="extension">The extension with its dot (<c>.pdf</c>), or empty.</param>
    /// <param name="exists">Tells whether a file name (with the extension) is taken in the target folder; this is what keeps an existing file from being replaced.</param>
    /// <returns>One file name per title, in order; they differ from each other without regard to case.</returns>
    /// <exception cref="ArgumentException"><paramref name="extension"/> is more than a dot and a short run of safe characters.</exception>
    /// <exception cref="InvalidOperationException">So many numbers are taken that none is free.</exception>
    public static IReadOnlyList<string> Unique(IEnumerable<string> stems, string extension, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(stems);
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(exists);
        CheckExtension(extension);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var title in stems)
        {
            var stem = Sanitize(title);
            var candidate = stem + extension;
            var number = 1;
            while (used.Contains(candidate) || exists(candidate))
            {
                number++;
                if (number > MaxNumber)
                {
                    throw new InvalidOperationException("No free file name was found.");
                }

                candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({number}){extension}");
            }

            used.Add(candidate);
            names.Add(candidate);
        }

        return names;
    }

    private static void CheckExtension(string extension)
    {
        if (extension.Length == 0)
        {
            return;
        }

        if (extension.Length > MaxExtensionLength || extension[0] != '.' || extension.Length == 1 || extension.EndsWith('.') || extension.EndsWith(' '))
        {
            throw new ArgumentException("An extension is a dot and a few safe characters.", nameof(extension));
        }

        for (var i = 1; i < extension.Length; i++)
        {
            var c = extension[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            {
                throw new ArgumentException("An extension is a dot and a few safe characters.", nameof(extension));
            }
        }

        if (extension.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("An extension is a dot and a few safe characters.", nameof(extension));
        }
    }

    // Replaces or removes what must not be in a name, collapses white space and trims dots and spaces at both ends.
    private static string Clean(string title)
    {
        var builder = new StringBuilder(Math.Min(title.Length, MaxScanned));
        var lastWasSpace = true; // drops leading white space
        var scanned = Math.Min(title.Length, MaxScanned);
        for (var i = 0; i < scanned; i++)
        {
            var c = title[i];
            if (char.IsHighSurrogate(c) && i + 1 < title.Length && char.IsLowSurrogate(title[i + 1]))
            {
                // A pair is one character; its category decides (symbols and letters beyond U+FFFF stay, private use too).
                var category = CharUnicodeInfo.GetUnicodeCategory(title, i);
                if (category is UnicodeCategory.Format or UnicodeCategory.Control)
                {
                    i++;
                    continue;
                }

                builder.Append(c).Append(title[i + 1]);
                i++;
                lastWasSpace = false;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                builder.Append('_');
                lastWasSpace = false;
                continue;
            }

            var kind = CharUnicodeInfo.GetUnicodeCategory(c);
            if (kind == UnicodeCategory.Format)
            {
                continue;
            }

            if (kind is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c);
            lastWasSpace = false;
        }

        return TrimDotsAndSpaces(builder.ToString());
    }

    // Keeps whole text elements up to the limits in elements and bytes, then trims dots and spaces the cut may have left at the end.
    private static string Truncate(string text, int maxElements, int maxBytes)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var elements = 0;
        var bytes = 0;
        var end = 0;
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            var size = Encoding.UTF8.GetByteCount(element);
            if (elements >= maxElements || bytes + size > maxBytes)
            {
                break;
            }

            elements++;
            bytes += size;
            end = enumerator.ElementIndex + element.Length;
        }

        return TrimDotsAndSpaces(text[..end]);
    }

    private static string TrimDotsAndSpaces(string text) => text.Trim(' ', '.');

    // Windows treats the part before the first dot (without trailing spaces) as a device, whatever the extension.
    internal static bool IsDeviceName(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        foreach (var device in DeviceNames)
        {
            if (stem.Equals(device, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // COM0 to COM9 and LPT0 to LPT9, also with the superscript digits.
        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³');
    }
}
