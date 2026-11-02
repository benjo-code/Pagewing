using System.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// The rule that an overlay text must not bring back what is redacted (milestone 14.1): the text of a mark may not contain the term of any
/// mark of the plan, the mark's own included. It is one decision for the analysis and for the apply (<see cref="RedactionExecutor"/>
/// asks it before anything is opened), and it reads the whole plan, so a term that comes after the text, from another page or from the
/// command line after its search, is found all the same. The check of the written file does not change for it: the overlay text is drawn as
/// glyph paths and holds no extractable text.
/// </summary>
/// <remarks>
/// Compared without regard to case, as a piece of the text, and with both sides in Unicode form C, for terms of at least
/// <see cref="RedactionLimits.MinRawCheckLength"/> characters (the length from which the check of the file searches too). A shorter term counts
/// only if the whole overlay text equals it: otherwise a term "ab" would forbid the text "geschwärzt". Terms are trimmed like the check
/// trims them (a selection can include the white space next to the word).
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

        var shortTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mark in plan.Marks)
        {
            var term = mark.Term?.Trim().Normalize(NormalizationForm.FormC);
            if (string.IsNullOrEmpty(term))
            {
                continue;
            }

            (term.Length >= RedactionLimits.MinRawCheckLength ? longTerms : shortTerms).Add(term);
        }

        if (shortTerms.Count == 0 && longTerms.Count == 0)
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
                hit = Contains(text, shortTerms, longTerms);
                verdicts[text] = hit;
            }

            if (hit)
            {
                pages.Add(mark.Page);
            }
        }

        return pages.Count == 0 ? null : new RedactionRefusal(RedactionRefusalKind.OverlayRepeatsTerm, [.. pages], Reason);
    }

    private static bool Contains(string text, HashSet<string> shortTerms, HashSet<string> longTerms)
    {
        if (shortTerms.Contains(text))
        {
            return true;
        }

        foreach (var term in longTerms)
        {
            if (term.Length <= text.Length && text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
