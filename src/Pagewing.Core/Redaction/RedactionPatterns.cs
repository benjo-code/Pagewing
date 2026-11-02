using System.Globalization;

namespace Pagewing.Core.Redaction;

/// <summary>How sure the scanner is that a match is what it looks like.</summary>
public enum PatternCertainty
{
    /// <summary>The checksum (and the length) is right.</summary>
    ChecksumOk,

    /// <summary>The kind has no checksum (e-mail address, telephone number); the shape fits.</summary>
    ShapeOnly,

    /// <summary>The shape fits, but the checksum or the length is wrong. Kept, because a typo in an IBAN is still a sensitive number.</summary>
    ChecksumFailed,
}

/// <summary>A pattern found in a text.</summary>
/// <param name="Kind">The kind of pattern.</param>
/// <param name="Start">The index of the first UTF-16 unit of the match in the scanned text.</param>
/// <param name="End">The index after the last UTF-16 unit of the match.</param>
/// <param name="Certainty">How sure the scanner is.</param>
public readonly record struct PatternMatch(RedactionPatternKind Kind, int Start, int End, PatternCertainty Certainty);

/// <summary>
/// Hand-written, bounded scanners for e-mail addresses, IBANs, telephone numbers and payment card numbers (milestone 14.1). No
/// <c>Regex</c>: every scanner runs in linear time with a fixed upper bound for one candidate, so a hostile text cannot make it slow.
/// A match is delimited by characters that do not belong to the pattern (nothing is found in the middle of a longer run of digits
/// or letters). The scanners only read the text; they know nothing about pages or PDFium.
/// </summary>
public static partial class RedactionPatterns
{
    /// <summary>The longest e-mail address that is recognised (RFC 5321).</summary>
    public const int MaxEmailLength = 254;

    /// <summary>The longest local part (before the at sign) that is recognised (RFC 5321).</summary>
    public const int MaxEmailLocalLength = 64;

    /// <summary>The most characters of an IBAN, without group separators.</summary>
    public const int MaxIbanLength = 34;

    /// <summary>The fewest characters of an IBAN, without group separators (the shortest registry entry is Norway with 15).</summary>
    public const int MinIbanLength = 15;

    /// <summary>The most digits of a payment card number.</summary>
    public const int MaxCardDigits = 19;

    /// <summary>The fewest digits of a payment card number.</summary>
    public const int MinCardDigits = 13;

    /// <summary>The most digits of a telephone number (E.164).</summary>
    public const int MaxPhoneDigits = 15;

    /// <summary>The fewest digits of a telephone number.</summary>
    public const int MinPhoneDigits = 7;

    /// <summary>
    /// Finds the patterns of the given kinds in a text. The matches are sorted by position and never overlap. When two kinds
    /// claim the same characters, the more specific wins: an e-mail address, then an IBAN, then a card number with a right checksum,
    /// then a telephone number, then a card number with a wrong checksum.
    /// </summary>
    /// <param name="text">The text to scan. Positions in the result are UTF-16 indices into it.</param>
    /// <param name="kinds">The kinds to look for.</param>
    /// <returns>The matches, sorted by <see cref="PatternMatch.Start"/>.</returns>
    public static IReadOnlyList<PatternMatch> Scan(string text, IReadOnlySet<RedactionPatternKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(kinds);

        List<PatternMatch> accepted = [];
        if (text.Length == 0 || kinds.Count == 0)
        {
            return accepted;
        }

        if (kinds.Contains(RedactionPatternKind.Email))
        {
            accepted = Combine(accepted, ScanEmails(text));
        }

        if (kinds.Contains(RedactionPatternKind.Iban))
        {
            accepted = Combine(accepted, ScanIbans(text));
        }

        if (kinds.Contains(RedactionPatternKind.Card))
        {
            var cardsOk = new List<PatternMatch>();
            var cardsOther = new List<PatternMatch>();
            ScanCards(text, cardsOk, cardsOther);
            accepted = Combine(accepted, cardsOk);
            if (kinds.Contains(RedactionPatternKind.Phone))
            {
                accepted = Combine(accepted, ScanPhones(text));
            }

            accepted = Combine(accepted, cardsOther);
        }
        else if (kinds.Contains(RedactionPatternKind.Phone))
        {
            accepted = Combine(accepted, ScanPhones(text));
        }

        return accepted;
    }

    /// <summary>
    /// Adds the matches of <paramref name="next"/> that overlap nothing in <paramref name="accepted"/> (both sorted and free of
    /// overlaps inside) and returns the merged, sorted list.
    /// </summary>
    private static List<PatternMatch> Combine(List<PatternMatch> accepted, List<PatternMatch> next)
    {
        if (next.Count == 0)
        {
            return accepted;
        }

        if (accepted.Count == 0)
        {
            return next;
        }

        var merged = new List<PatternMatch>(accepted.Count + next.Count);
        var a = 0;
        foreach (var candidate in next)
        {
            while (a < accepted.Count && accepted[a].End <= candidate.Start)
            {
                merged.Add(accepted[a]);
                a++;
            }

            if (a < accepted.Count && accepted[a].Start < candidate.End)
            {
                continue;
            }

            merged.Add(candidate);
        }

        for (; a < accepted.Count; a++)
        {
            merged.Add(accepted[a]);
        }

        return merged;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiLetterOrDigit(char c) => IsDigit(c) || IsAsciiLetter(c);

    private static char UpperAscii(char c) => c is >= 'a' and <= 'z' ? (char)(c - 32) : c;

    /// <summary>Space, no-break space, narrow no-break space and thin space: what separates the groups of an IBAN or a card number.</summary>
    private static bool IsSpaceSeparator(char c) => c is ' ' or '\u00A0' or '\u202F' or '\u2009';

    private static bool IsHyphen(char c) => c is '-' or '\u2010' or '\u2011';

    /// <summary>Whether the letter or digit at <paramref name="i"/> (a surrogate pair counts as one character) would continue a run.</summary>
    private static bool LetterOrDigitAt(string t, int i) => i >= 0 && i < t.Length && char.IsLetterOrDigit(t, i);

    /// <summary>Whether the letter or digit that ends right before <paramref name="end"/> (a surrogate pair counts as one character) would continue a run.</summary>
    private static bool LetterOrDigitBefore(string t, int end)
    {
        if (end <= 0)
        {
            return false;
        }

        var c = t[end - 1];
        return char.IsLowSurrogate(c) ? end >= 2 && char.IsHighSurrogate(t[end - 2]) && char.IsLetterOrDigit(t, end - 2) : char.IsLetterOrDigit(c);
    }

    private static bool IsWordCategory(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber;
}
