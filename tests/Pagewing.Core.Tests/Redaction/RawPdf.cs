using System.IO.Compression;
using System.Text;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// Looks into the raw bytes of a PDF file the way the redaction tests need it: wherever the file stores a term, the check
/// finds it, whatever PDFium shows. It works on bytes only and decodes no PDF syntax: a string written as a hex string
/// (<c>&lt;FEFF005A...&gt;</c>) or with octal escapes (<c>\337</c>) is not found; the test files write literal strings.
/// </summary>
internal static class RawPdf
{
    /// <summary>The largest decoded size of one stream; a bigger one is a decompression bomb and throws.</summary>
    public const long MaxInflatedStream = 128L * 1024 * 1024;

    /// <summary>The largest decoded size of all streams of one file together.</summary>
    public const long MaxInflatedTotal = 1024L * 1024 * 1024;

    /// <summary>
    /// The file with the payload of every Flate stream replaced in place by its decoded bytes; everything else (dictionaries,
    /// other streams, the rest of the file) is unchanged, so <c>/Length</c> is stale and the result is no valid PDF, only text to
    /// search. A stream that does not decode as zlib stays as it is. Predictors are not undone (the files of the tests use none).
    /// </summary>
    /// <exception cref="InvalidDataException">A stream or the sum of all streams grows past the limits.</exception>
    public static byte[] InflateAllStreams(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var text = Encoding.Latin1.GetString(file); // one char per byte: indices are the same in text and file
        var output = new MemoryStream(file.Length);
        var copied = 0;
        var total = 0L;
        var search = 0;
        while (true)
        {
            var keyword = text.IndexOf("stream", search, StringComparison.Ordinal);
            if (keyword < 0)
            {
                break;
            }

            search = keyword + "stream".Length;
            if (keyword >= 3 && string.CompareOrdinal(text, keyword - 3, "end", 0, 3) == 0)
            {
                continue;
            }

            var start = search;
            if (start < text.Length && text[start] == '\r')
            {
                start++;
            }

            if (start >= text.Length || text[start] != '\n')
            {
                continue;
            }

            start++;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            var objectStart = text.LastIndexOf(" obj", keyword, StringComparison.Ordinal);
            if (end < 0 || objectStart < 0)
            {
                break;
            }

            var dictionary = text.AsSpan(objectStart, keyword - objectStart);
            search = end + "endstream".Length;
            if (!dictionary.Contains("/FlateDecode", StringComparison.Ordinal) && !dictionary.Contains("/Fl ", StringComparison.Ordinal))
            {
                continue;
            }

            var payloadEnd = end;
            if (payloadEnd > start && text[payloadEnd - 1] == '\n')
            {
                payloadEnd--;
            }

            if (payloadEnd > start && text[payloadEnd - 1] == '\r')
            {
                payloadEnd--;
            }

            var inflated = TryInflate(file.AsMemory(start, payloadEnd - start), ref total);
            if (inflated is null)
            {
                continue;
            }

            output.Write(file, copied, start - copied);
            output.Write(inflated);
            copied = payloadEnd;
        }

        output.Write(file, copied, file.Length - copied);
        return output.ToArray();
    }

    /// <summary>
    /// How often <paramref name="term"/> occurs in <paramref name="haystack"/>: non-overlapping and case-sensitive, summed over every
    /// distinct byte form of the term: single byte (ASCII and PDFDocEncoding/Latin-1, when every character fits), UTF-8, UTF-16BE and
    /// UTF-16LE (without a byte order mark, which sits before the first character and does not change the match).
    /// </summary>
    public static int Count(byte[] haystack, string term)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentException.ThrowIfNullOrEmpty(term);
        var patterns = new List<byte[]>();
        if (term.All(static c => c <= 0xFF))
        {
            patterns.Add(Encoding.Latin1.GetBytes(term));
        }

        patterns.Add(Encoding.UTF8.GetBytes(term));
        patterns.Add(Encoding.BigEndianUnicode.GetBytes(term));
        patterns.Add(Encoding.Unicode.GetBytes(term));

        var count = 0;
        for (var i = 0; i < patterns.Count; i++)
        {
            if (patterns.FindIndex(0, i, other => other.AsSpan().SequenceEqual(patterns[i])) >= 0)
            {
                continue; // the same bytes as an earlier form (ASCII is also valid UTF-8)
            }

            var rest = haystack.AsSpan();
            int index;
            while ((index = rest.IndexOf(patterns[i])) >= 0)
            {
                count++;
                rest = rest[(index + patterns[i].Length)..];
            }
        }

        return count;
    }

    /// <summary>Whether the term is in the file as it is or in any of its Flate streams after decoding.</summary>
    public static bool ContainsAnywhere(byte[] file, string term) =>
        Count(file, term) > 0 || Count(InflateAllStreams(file), term) > 0;

    private static byte[]? TryInflate(ReadOnlyMemory<byte> payload, ref long total)
    {
        using var decoded = new MemoryStream();
        var exceeded = false;
        try
        {
            using var source = new MemoryStream(payload.ToArray(), writable: false);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decoded.Length + read > MaxInflatedStream || total + decoded.Length + read > MaxInflatedTotal)
                {
                    exceeded = true;
                    break;
                }

                decoded.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            return null; // not zlib data: stays as it is
        }

        // Outside the try: a bomb must never be taken for "not zlib data" and passed as clean.
        if (exceeded)
        {
            throw new InvalidDataException("A Flate stream of the file grows past the limit of the test helper.");
        }

        total += decoded.Length;
        return decoded.ToArray();
    }
}
