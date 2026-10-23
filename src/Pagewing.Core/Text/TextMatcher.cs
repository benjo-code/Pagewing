using System.Buffers;
using System.Globalization;
using System.Text;

namespace Pagewing.Core.Text;

/// <summary>A match in the characters of a page.</summary>
/// <param name="FirstChar">The index of the first matched character.</param>
/// <param name="LastChar">The index of the last matched character (inclusive).</param>
public readonly record struct TextMatch(int FirstChar, int LastChar);

/// <summary>
/// Finds a query in the characters of a page. Page and query are compared as normalized search text: Unicode
/// normalization form KC (ligatures become their letters, decomposed umlauts are composed), lowercased with the
/// invariant culture unless <see cref="SearchOptions.MatchCase"/> is set, every run of whitespace and line breaks as
/// one space, and a line-end hyphen behind a letter is dropped when the next line starts with a lowercase letter
/// ("Win-" + "dows" is "windows"). A hyphen that stays (the next line starts with a capital, as in German compounds)
/// is followed by a space, so a query with the hyphen of such a compound broken over two lines finds nothing.
/// Diacritics are not ignored and ß is not folded to ss. Matches do not overlap.
/// </summary>
public static class TextMatcher
{
    /// <summary>The most characters a <see cref="SearchSnippet"/> shows on each side of the match.</summary>
    internal const int SnippetContext = 30;

    /// <summary>The UTF-16 length of the stack buffers that hold a character and its combining marks.</summary>
    private const int StackGroupLength = 64;

    /// <summary>Finds the query in the chars of one page. An empty or whitespace-only query finds nothing.</summary>
    /// <param name="chars">The characters of the page, in the order the engine reports them.</param>
    /// <param name="query">The text to find.</param>
    /// <param name="options">Case and whole-word options.</param>
    /// <returns>The matches in text order.</returns>
    public static IReadOnlyList<TextMatch> Find(IReadOnlyList<TextChar> chars, string query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(chars);
        ArgumentNullException.ThrowIfNull(query);
        if (chars.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var needle = NormalizeQuery(query, options.MatchCase);
        if (needle.Length == 0)
        {
            return [];
        }

        using var text = new SearchText(chars.Count);
        Build(chars, options.MatchCase, text);
        var haystack = text.Text;
        var matches = new List<TextMatch>();
        var position = 0;
        while (position <= haystack.Length - needle.Length)
        {
            var found = haystack[position..].IndexOf(needle, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            var start = position + found;
            var end = start + needle.Length;
            if (options.WholeWord && (EndsWithWordChar(haystack[..start]) || StartsWithWordChar(haystack[end..])))
            {
                position = start + 1;
                continue;
            }

            matches.Add(new TextMatch(text.FirstOf(start), text.LastOf(end - 1)));
            position = end;
        }

        return matches;
    }

    /// <summary>
    /// Builds the snippet of a match: up to <see cref="SnippetContext"/> characters before it from the line of
    /// <paramref name="first"/>, the match, and up to <see cref="SnippetContext"/> characters after it from the line
    /// of <paramref name="last"/>. Inside the match, line ends become one space and dropped line-end hyphens are left
    /// out, like in the search text; whitespace becomes a plain space.
    /// </summary>
    internal static SearchSnippet SnippetOf(IReadOnlyList<TextChar> chars, int first, int last)
    {
        var beforeStart = Math.Max(TextGeometry.LineStart(chars, first), first - SnippetContext);
        var afterEnd = Math.Min(TextGeometry.SegmentEnd(chars, last), last + 1 + SnippetContext);

        return new SearchSnippet(Plain(chars, beforeStart, first), Joined(chars, first, last + 1), Plain(chars, last + 1, afterEnd));
    }

    /// <summary>Normalizes the query like the text of a page; leading and trailing whitespace drop out.</summary>
    private static string NormalizeQuery(string query, bool matchCase)
    {
        var chars = new List<TextChar>(query.Length);
        foreach (var rune in query.EnumerateRunes())
        {
            chars.Add(new TextChar(rune.Value, default, TextCharTraits.None, 0));
        }

        using var text = new SearchText(chars.Count);
        Build(chars, matchCase, text);
        return text.Text.ToString();
    }

    /// <summary>Appends the search text of <paramref name="chars"/> and records which characters each position comes from.</summary>
    private static void Build(IReadOnlyList<TextChar> chars, bool matchCase, SearchText text)
    {
        Span<char> group = stackalloc char[StackGroupLength];
        Span<char> lowered = stackalloc char[StackGroupLength];
        var pendingSpace = false;
        var spaceIndex = 0;
        var i = 0;
        while (i < chars.Count)
        {
            var c = chars[i];
            if (IsSpace(c))
            {
                if (!pendingSpace)
                {
                    pendingSpace = true;
                    spaceIndex = i;
                }

                i++;
                continue;
            }

            if (JoinsHyphenated(chars, i, out var continuation))
            {
                // The hyphen and the line end after it drop out; the word goes on with no space.
                i = continuation;
                continue;
            }

            if (pendingSpace && text.Length > 0)
            {
                text.Append(' ', spaceIndex, spaceIndex);
            }

            pendingSpace = false;

            // A character and the combining marks after it are normalized together.
            var end = i + 1;
            while (end < chars.Count && IsMark(chars[end]))
            {
                end++;
            }

            AppendGroup(chars, i, end, matchCase, text, group, lowered);

            // A line-end hyphen that stays ends its line, and the engine reports no line break after it.
            if ((c.Flags & TextCharTraits.Hyphen) != 0 && end == i + 1)
            {
                pendingSpace = true;
                spaceIndex = i;
            }

            i = end;
        }
    }

    /// <summary>Appends the normalized (and unless <paramref name="matchCase"/> lowercased) text of the characters [start, end).</summary>
    private static void AppendGroup(IReadOnlyList<TextChar> chars, int start, int end, bool matchCase, SearchText text, Span<char> buffer, Span<char> lowered)
    {
        var last = end - 1;
        var single = chars[start].CodePoint;
        if (end == start + 1 && single < 0x80)
        {
            // ASCII is normalized already.
            var ascii = (char)single;
            text.Append(matchCase ? ascii : char.ToLowerInvariant(ascii), start, last);
            return;
        }

        // A group longer than the buffer (only possible with dozens of combining marks) is copied to a string.
        ReadOnlySpan<char> source = end - start <= buffer.Length / 2 ? buffer[..Encode(chars, start, end, buffer)] : TextOf(chars, start, end);
        if (!source.IsNormalized(NormalizationForm.FormKC))
        {
            source = source.ToString().Normalize(NormalizationForm.FormKC);
        }

        if (matchCase)
        {
            text.Append(source, start, last);
            return;
        }

        // Simple case mapping keeps the length, also for surrogate pairs.
        var written = source.ToLowerInvariant(lowered);
        text.Append(written >= 0 ? lowered[..written] : source.ToString().ToLowerInvariant(), start, last);
    }

    /// <summary>Writes the characters [start, end) as UTF-16 to <paramref name="buffer"/>, which holds two chars per character; returns the length.</summary>
    private static int Encode(IReadOnlyList<TextChar> chars, int start, int end, Span<char> buffer)
    {
        var length = 0;
        for (var k = start; k < end; k++)
        {
            var codePoint = chars[k].CodePoint;
            var rune = Rune.IsValid(codePoint) ? new Rune(codePoint) : Rune.ReplacementChar;
            length += rune.EncodeToUtf16(buffer[length..]);
        }

        return length;
    }

    /// <summary>
    /// Gets whether the character at <paramref name="index"/> is a hyphen at the end of a line that drops out of the
    /// search text: a hyphen (trait <see cref="TextCharTraits.Hyphen"/>, '-' or U+00AD) directly behind a letter,
    /// then the end of the line (a hyphen with the trait always ends its line; otherwise a line break must follow,
    /// spaces around it allowed), then a lowercase letter.
    /// </summary>
    /// <param name="continuation">The index of the lowercase letter that continues the word.</param>
    private static bool JoinsHyphenated(IReadOnlyList<TextChar> chars, int index, out int continuation)
    {
        continuation = index;
        var c = chars[index];
        var trait = (c.Flags & TextCharTraits.Hyphen) != 0;
        if (c.IsGenerated || !(trait || c.CodePoint is '-' or 0xAD) || index == 0)
        {
            return false;
        }

        var before = chars[index - 1];
        if (before.IsGenerated || !(IsRune(before.CodePoint, Rune.IsLetter) || IsMark(before)))
        {
            return false;
        }

        var lineEnd = trait;
        var next = index + 1;
        while (next < chars.Count && IsSpace(chars[next]))
        {
            lineEnd |= IsLineBreak(chars[next]);
            next++;
        }

        if (!lineEnd || next >= chars.Count || chars[next].IsGenerated || !IsRune(chars[next].CodePoint, Rune.IsLower))
        {
            return false;
        }

        continuation = next;
        return true;
    }

    /// <summary>The characters [start, end) as they are, with whitespace as plain spaces.</summary>
    private static string Plain(IReadOnlyList<TextChar> chars, int start, int end)
    {
        var builder = new StringBuilder(Math.Max(0, end - start));
        for (var i = start; i < end; i++)
        {
            if (IsSpace(chars[i]))
            {
                builder.Append(' ');
            }
            else
            {
                AppendText(builder, chars[i].CodePoint);
            }
        }

        return builder.ToString();
    }

    /// <summary>The characters [start, end) joined like the search text, but not normalized.</summary>
    private static string Joined(IReadOnlyList<TextChar> chars, int start, int end)
    {
        var builder = new StringBuilder(end - start);
        var pendingSpace = false;
        var i = start;
        while (i < end)
        {
            var c = chars[i];
            if (IsSpace(c))
            {
                pendingSpace = true;
                i++;
                continue;
            }

            if (JoinsHyphenated(chars, i, out var continuation))
            {
                i = continuation;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = (c.Flags & TextCharTraits.Hyphen) != 0;
            AppendText(builder, c.CodePoint);
            i++;
        }

        return builder.ToString();
    }

    private static string TextOf(IReadOnlyList<TextChar> chars, int start, int end)
    {
        var builder = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
        {
            AppendText(builder, chars[i].CodePoint);
        }

        return builder.ToString();
    }

    private static void AppendText(StringBuilder builder, int codePoint)
    {
        var rune = Rune.IsValid(codePoint) ? new Rune(codePoint) : Rune.ReplacementChar;
        Span<char> utf16 = stackalloc char[2];
        builder.Append(utf16[..rune.EncodeToUtf16(utf16)]);
    }

    /// <summary>Whitespace: everything the engine generated (spaces and line breaks) and Unicode whitespace.</summary>
    private static bool IsSpace(TextChar c) => c.IsGenerated || IsRune(c.CodePoint, Rune.IsWhiteSpace);

    private static bool IsLineBreak(TextChar c) => c.IsLineBreak || c.CodePoint is '\r' or '\n' or 0x2028 or 0x2029;

    private static bool IsMark(TextChar c) =>
        !c.IsGenerated
        && Rune.IsValid(c.CodePoint)
        && Rune.GetUnicodeCategory(new Rune(c.CodePoint)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool IsRune(int codePoint, Func<Rune, bool> test) => Rune.IsValid(codePoint) && test(new Rune(codePoint));

    private static bool EndsWithWordChar(ReadOnlySpan<char> text) =>
        !text.IsEmpty && Rune.DecodeLastFromUtf16(text, out var rune, out _) == OperationStatus.Done && Rune.IsLetterOrDigit(rune);

    private static bool StartsWithWordChar(ReadOnlySpan<char> text) =>
        !text.IsEmpty && Rune.DecodeFromUtf16(text, out var rune, out _) == OperationStatus.Done && Rune.IsLetterOrDigit(rune);

    /// <summary>
    /// The search text of a page with, for each of its positions, the first and last character it comes from. The
    /// buffers are rented from the shared pools and returned by <see cref="Dispose"/>.
    /// </summary>
    private sealed class SearchText : IDisposable
    {
        private char[] text;
        private int[] firstOf;
        private int[] lastOf;

        public SearchText(int capacity)
        {
            capacity = Math.Max(capacity, 16);
            text = ArrayPool<char>.Shared.Rent(capacity);
            firstOf = ArrayPool<int>.Shared.Rent(capacity);
            lastOf = ArrayPool<int>.Shared.Rent(capacity);
        }

        public int Length { get; private set; }

        public ReadOnlySpan<char> Text => text.AsSpan(0, Length);

        public int FirstOf(int position) => firstOf[position];

        public int LastOf(int position) => lastOf[position];

        public void Append(char value, int first, int last)
        {
            EnsureCapacity(1);
            text[Length] = value;
            firstOf[Length] = first;
            lastOf[Length] = last;
            Length++;
        }

        public void Append(ReadOnlySpan<char> value, int first, int last)
        {
            EnsureCapacity(value.Length);
            value.CopyTo(text.AsSpan(Length));
            firstOf.AsSpan(Length, value.Length).Fill(first);
            lastOf.AsSpan(Length, value.Length).Fill(last);
            Length += value.Length;
        }

        public void Dispose()
        {
            ArrayPool<char>.Shared.Return(text);
            ArrayPool<int>.Shared.Return(firstOf);
            ArrayPool<int>.Shared.Return(lastOf);
            text = [];
            firstOf = [];
            lastOf = [];
        }

        private void EnsureCapacity(int extra)
        {
            if (Length + extra <= text.Length)
            {
                return;
            }

            var size = Math.Max(text.Length * 2, Length + extra);
            text = Grow(text, size, Length);
            firstOf = Grow(firstOf, size, Length);
            lastOf = Grow(lastOf, size, Length);
        }

        private static T[] Grow<T>(T[] old, int size, int used)
        {
            var grown = ArrayPool<T>.Shared.Rent(size);
            old.AsSpan(0, used).CopyTo(grown);
            ArrayPool<T>.Shared.Return(old);
            return grown;
        }
    }
}
