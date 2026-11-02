using System.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// The rule that an overlay text must not bring back what is redacted (milestone 14.1): the text of a mark may not contain the term of any
/// mark of the plan, the mark's own included. It is one decision for the analysis and for the apply (<see cref="RedactionExecutor"/>
/// asks it before anything is opened), and it reads the whole plan, so a term that comes after the text, from another page or from the
/// command line after its search, is found all the same. It is the only guard against this: the overlay text is drawn as glyph paths,
/// so the check of the written file cannot read it.
/// </summary>
/// <remarks>
/// <para>
/// Every term and every overlay text is compared in two forms. The <b>plain</b> form is the text trimmed and in Unicode form C; the
/// <b>stripped</b> form is that text without any separator (<see cref="RemovedText.WithoutSeparators"/>: white space of every kind, also no-break,
/// narrow no-break and thin spaces, tabs and line breaks; hyphens including U+2010 and U+2011; slashes, dots, brackets, soft hyphen). So
/// the term "DE89 3704 0044 ..." (as the page holds it, with whatever spaces) is found in "Konto DE89370400440532013000", and
/// "+49-711-1234567" in "+49 711 1234567". Both are compared without regard to case.
/// </para>
/// <para>
/// A term counts as a piece of an overlay text from <see cref="RedactionLimits.MinRawCheckLength"/> characters on, <b>measured on the form
/// that is compared</b>: a term of 6 characters with 3 left after stripping is, in the stripped form, a short term. A short term counts only if
/// the whole overlay text equals it in that same form (plain with plain, stripped with stripped): otherwise a term "ab" would forbid the text
/// "geschwärzt". A form without any character (a term of separators only) is no term.
/// </para>
/// <para>
/// Known limits, not covered: "ß" against "SS" and other case foldings beyond the ordinal ones, ligature code points (a term written with
/// "ﬁ" against an overlay with "fi"; the characters of the overlay come from the text box set, which has none), and a term that is spread
/// over the texts of two marks.
/// </para>
/// </remarks>
public static class RedactionOverlayRule
{
    /// <summary>The reason of the refusal. English and stable, and it never names the term or the text.</summary>
    public const string Reason = "The overlay text of a mark contains a text that is redacted, which would put it back on the page.";

    /// <summary>Checks the overlay texts of a plan against the terms of its marks.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The refusal with the 0-based pages of the marks whose overlay text contains a term, or <see langword="null"/> when no text does.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static RedactionRefusal? Refusal(RedactionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Marks.Any(static m => !string.IsNullOrEmpty(m.OverlayText)))
        {
            return null;
        }

        // The terms, once: deduplicated per form, split into the ones that count as a piece and the short ones that count when equal.
        var terms = new TermSets();
        foreach (var mark in plan.Marks)
        {
            if (mark.Term?.Trim().Normalize(NormalizationForm.FormC) is { Length: > 0 } term)
            {
                terms.Add(term);
            }
        }

        if (terms.IsEmpty)
        {
            return null;
        }

        var verdicts = new Dictionary<string, bool>(StringComparer.Ordinal);
        var pages = new SortedSet<int>();
        foreach (var mark in plan.Marks)
        {
            if (string.IsNullOrEmpty(mark.OverlayText))
            {
                continue;
            }

            var text = mark.OverlayText.Trim().Normalize(NormalizationForm.FormC);
            if (!verdicts.TryGetValue(text, out var hit))
            {
                hit = terms.Hit(text);
                verdicts[text] = hit;
            }

            if (hit)
            {
                pages.Add(mark.Page);
            }
        }

        return pages.Count == 0 ? null : new RedactionRefusal(RedactionRefusalKind.OverlayRepeatsTerm, [.. pages], Reason);
    }

    /// <summary>The terms in their two forms, each split by the length that decides between "piece of" and "equal to".</summary>
    private sealed class TermSets
    {
        private readonly HashSet<string> shortPlain = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> longPlain = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> shortStripped = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> longStripped = new(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty => shortPlain.Count + longPlain.Count + shortStripped.Count + longStripped.Count == 0;

        public void Add(string term)
        {
            (term.Length >= RedactionLimits.MinRawCheckLength ? longPlain : shortPlain).Add(term);
            var stripped = RemovedText.WithoutSeparators(term);
            if (stripped.Length > 0)
            {
                (stripped.Length >= RedactionLimits.MinRawCheckLength ? longStripped : shortStripped).Add(stripped);
            }
        }

        public bool Hit(string text)
        {
            if (shortPlain.Contains(text) || ContainsAny(text, longPlain))
            {
                return true;
            }

            var stripped = RemovedText.WithoutSeparators(text);
            return stripped.Length > 0 && (shortStripped.Contains(stripped) || ContainsAny(stripped, longStripped));
        }

        private static bool ContainsAny(string text, HashSet<string> terms)
        {
            foreach (var term in terms)
            {
                if (term.Length <= text.Length && text.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
