using System.Globalization;

namespace Pagewing.Core.Annotations;

/// <summary>
/// The characters a text box can show: those of Basic Latin, Latin-1, Latin Extended-A, "ẞ", the dashes, quotes and the
/// ellipsis of General Punctuation, and "€", as far as the shipped Geist Regular (the subset of
/// <c>Pagewing.App/Assets/Fonts</c>) has a glyph for them. The appearance is drawn from that font's outlines, and a
/// character without its own glyph would come from a system font that differs between machines, so a text with any other
/// character is refused as a whole (never changed). A line break is no character here; it is handled by the layout.
/// </summary>
public static class TextBoxCharacters
{
    /// <summary>
    /// The ranges, first and last character, that the shipped Geist Regular covers (its cmap; checked by
    /// <c>TextBoxSpecTests.TheSupportedSetIsExactlyWhatTheShippedGeistHasOfLatinOneEuroAndCentralEurope</c>). Missing from
    /// Latin-1 is only the soft hyphen (U+00AD), from Latin Extended-A a few rare letters (Ĕĕ Ĭĭ ĸ Ŀŀ ŉ Ŏŏ ſ).
    /// </summary>
    private static readonly (char First, char Last)[] Ranges =
    [
        ((char)0x20, (char)0x7E),
        ((char)0xA0, (char)0xAC),
        ((char)0xAE, (char)0x113),
        ((char)0x116, (char)0x12B),
        ((char)0x12E, (char)0x137),
        ((char)0x139, (char)0x13E),
        ((char)0x141, (char)0x148),
        ((char)0x14A, (char)0x14D),
        ((char)0x150, (char)0x17E),
        ((char)0x1E9E, (char)0x1E9E),
        ((char)0x2013, (char)0x2014),
        ((char)0x2018, (char)0x201A),
        ((char)0x201C, (char)0x201E),
        ((char)0x2026, (char)0x2026),
        ((char)0x20AC, (char)0x20AC),
    ];

    /// <summary>Gets every supported character in ascending order.</summary>
    public static IReadOnlyList<char> All { get; } = BuildAll();

    /// <summary>Gets whether a text box can show <paramref name="character"/> (a line break is not a character here).</summary>
    /// <param name="character">The character.</param>
    /// <returns>Whether it is in the supported set.</returns>
    public static bool IsSupported(char character)
    {
        foreach (var (first, last) in Ranges)
        {
            if (character < first)
            {
                return false;
            }

            if (character <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The characters of <paramref name="text"/> a text box cannot show (line feeds aside), each once in the order they come
    /// first, at most <paramref name="limit"/>: a whole text element (an emoji, a letter with a combining mark) counts as one,
    /// a lone surrogate as itself.
    /// </summary>
    /// <param name="text">The text, with line feeds as line breaks.</param>
    /// <param name="limit">The most characters returned.</param>
    /// <returns>The unsupported characters.</returns>
    public static IReadOnlyList<string> UnsupportedIn(string text, int limit = 8)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext() && result.Count < limit)
        {
            var element = enumerator.GetTextElement();
            if (element is "\n" || (element.Length == 1 && IsSupported(element[0])) || result.Contains(element, StringComparer.Ordinal))
            {
                continue;
            }

            // A supported letter with a combining mark after it is one element: name only what makes it unsupported.
            var named = element.Length > 1 && IsSupported(element[0]) && !char.IsSurrogate(element[0]) ? element[1..] : element;
            if (!result.Contains(named, StringComparer.Ordinal))
            {
                result.Add(named);
            }
        }

        return result;
    }

    private static char[] BuildAll()
    {
        var all = new List<char>();
        foreach (var (first, last) in Ranges)
        {
            for (var c = first; c <= last; c++)
            {
                all.Add(c);
            }
        }

        return [.. all];
    }
}
