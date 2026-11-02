using System.Globalization;

namespace Pagewing.Core.Redaction;

public static partial class RedactionPatterns
{
    /// <summary>
    /// E-mail addresses: <c>local@domain.tld</c>. The local part has letters (any script), digits and <c>. _ % + - '</c>, at most
    /// <see cref="MaxEmailLocalLength"/> units; the domain has labels of letters, digits and inner hyphens, at least two labels, the
    /// last one with at least two units and a letter. No comments, no quoted forms (RFC edge cases are backlog). The whole address is
    /// at most <see cref="MaxEmailLength"/> units.
    /// </summary>
    private static List<PatternMatch> ScanEmails(string t)
    {
        var result = new List<PatternMatch>();
        var lowerBound = 0;
        var from = 0;
        while (from < t.Length)
        {
            var at = t.IndexOf('@', from);
            if (at < 0)
            {
                break;
            }

            from = at + 1;
            if (TryEmail(t, at, lowerBound, out var start, out var end))
            {
                result.Add(new PatternMatch(RedactionPatternKind.Email, start, end, PatternCertainty.ShapeOnly));
                lowerBound = end;
                if (end > from)
                {
                    from = end;
                }
            }
        }

        return result;
    }

    private static bool TryEmail(string t, int at, int lowerBound, out int start, out int end)
    {
        start = at;
        end = at;

        // The local part, read backwards. It must end in a letter, digit or mark (not in a dot or a plus).
        var ls = at;
        while (ls > lowerBound)
        {
            var width = LocalWidthBefore(t, ls);
            if (width == 0)
            {
                break;
            }

            if (at - ls + width > MaxEmailLocalLength)
            {
                return false;
            }

            ls -= width;
        }

        if (ls == at)
        {
            return false;
        }

        var last = t[at - 1];
        if (!(char.IsLowSurrogate(last) || last == '_' || IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(last))))
        {
            return false;
        }

        // Trim leading punctuation: ".john@x.de" is the address john@x.de.
        while (ls < at && !StartsWithWord(t, ls))
        {
            ls++;
        }

        if (ls == at)
        {
            return false;
        }

        // The domain, read forwards.
        var ds = at + 1;
        var de = ds;
        while (de < t.Length)
        {
            var width = DomainWidthAt(t, de);
            if (width == 0)
            {
                break;
            }

            de += width;
            if (de - ls > MaxEmailLength)
            {
                return false;
            }
        }

        while (de > ds && t[de - 1] is '.' or '-')
        {
            de--;
        }

        var validEnd = ValidDomainEnd(t, ds, de, out var lastLabelStart, out var labels);
        if (labels < 2 || !IsTopLevelLabel(t, lastLabelStart, validEnd))
        {
            return false;
        }

        start = ls;
        end = validEnd;
        return end - start <= MaxEmailLength;
    }

    /// <summary>The end of the longest run of valid labels at the start of the domain <c>[ds, de)</c>.</summary>
    private static int ValidDomainEnd(string t, int ds, int de, out int lastLabelStart, out int labels)
    {
        labels = 0;
        lastLabelStart = ds;
        var validEnd = ds;
        var p = ds;
        while (p <= de)
        {
            var q = p;
            while (q < de && t[q] != '.')
            {
                q++;
            }

            if (q == p || t[p] == '-' || t[q - 1] == '-')
            {
                break;
            }

            labels++;
            lastLabelStart = p;
            validEnd = q;
            if (q >= de)
            {
                break;
            }

            p = q + 1;
        }

        return validEnd;
    }

    private static bool IsTopLevelLabel(string t, int start, int end)
    {
        if (end - start < 2)
        {
            return false;
        }

        for (var i = start; i < end; i++)
        {
            if (char.IsLetter(t[i]) || char.IsSurrogate(t[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The width (1 or 2 units) of the local-part character that ends right before <paramref name="end"/>, or 0 when there is none.</summary>
    private static int LocalWidthBefore(string t, int end)
    {
        var c = t[end - 1];
        if (char.IsLowSurrogate(c))
        {
            return end >= 2 && char.IsHighSurrogate(t[end - 2]) && IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(t, end - 2)) ? 2 : 0;
        }

        if (char.IsHighSurrogate(c))
        {
            return 0;
        }

        return IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(c)) || c is '.' or '_' or '%' or '+' or '-' or '\'' ? 1 : 0;
    }

    private static bool StartsWithWord(string t, int i)
    {
        var c = t[i];
        if (char.IsHighSurrogate(c))
        {
            return i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]) && IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(t, i));
        }

        return IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(c)) || c == '_';
    }

    /// <summary>The width of the domain character at <paramref name="i"/> (letter, digit, mark, hyphen or dot), or 0.</summary>
    private static int DomainWidthAt(string t, int i)
    {
        var c = t[i];
        if (char.IsHighSurrogate(c))
        {
            return i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]) && IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(t, i)) ? 2 : 0;
        }

        if (char.IsLowSurrogate(c))
        {
            return 0;
        }

        return IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(c)) || c is '-' or '.' ? 1 : 0;
    }
}
