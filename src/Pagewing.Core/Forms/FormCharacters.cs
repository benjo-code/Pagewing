using System.Globalization;

namespace Pagewing.Core.Forms;

/// <summary>
/// The characters Pagewing writes into a form field: U+0020 to U+007E, U+00A0 to U+00FF without the soft hyphen (U+00AD), and
/// the four of Windows-1252 outside Latin-1 that the engine's default font covers: "€" (U+20AC), "ˆ" (U+02C6), "˜" (U+02DC)
/// and "™" (U+2122). Anything else is refused as a whole by <see cref="FormValueRules"/>, never replaced or dropped. This is
/// not the character set of the text box (<c>TextBoxCharacters</c>), which draws its own glyphs. A line break is no character
/// here; <see cref="FormValueRules"/> handles it.
/// </summary>
public static class FormCharacters
{
    /// <summary>Gets whether a form field can show <paramref name="c"/> (a line break is not a character here).</summary>
    /// <param name="c">The character.</param>
    /// <returns>Whether it is in the supported set.</returns>
    public static bool IsSupported(char c) =>
        c is >= (char)0x20 and <= (char)0x7E
        || c is >= (char)0xA0 and <= (char)0xFF and not (char)0xAD
        || c is '€' or 'ˆ' or '˜' or '™';

    /// <summary>
    /// The characters of <paramref name="text"/> a form field cannot show (line breaks aside), each once in the order they come
    /// first, at most <paramref name="limit"/>. A whole text element (an emoji, a letter with a combining mark) counts as one;
    /// a character that cannot be seen (a soft hyphen, a control character, a lone surrogate) is named as <c>U+XXXX</c>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="limit">The most characters returned.</param>
    /// <returns>The unsupported characters.</returns>
    public static IReadOnlyList<string> UnsupportedIn(string text, int limit = 8)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (result.Count < limit && enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (element is "\n" or "\r" or "\r\n" || (element.Length == 1 && IsSupported(element[0])))
            {
                continue;
            }

            // A supported letter with a combining mark after it is one element: name only what makes it unsupported.
            var offending = element.Length > 1 && IsSupported(element[0]) ? element[1..] : element;
            var named = Describe(offending);
            if (!result.Contains(named, StringComparer.Ordinal))
            {
                result.Add(named);
            }
        }

        return result;
    }

    private static string Describe(string element)
    {
        if (element.Length == 1 && char.IsSurrogate(element[0]))
        {
            return FormattableString.Invariant($"U+{(int)element[0]:X4}");
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
        if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.PrivateUse or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
        {
            return FormattableString.Invariant($"U+{char.ConvertToUtf32(element, 0):X4}");
        }

        return element;
    }
}
