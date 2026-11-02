using System.Globalization;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.Core.TextEdit;

/// <summary>Tells whether the font of a run can draw a character (see <c>Docs/DECISIONS.md</c>, "Text bearbeiten: Spike (M16)", (b)).</summary>
public interface IFontCoverage
{
    /// <summary>Gets whether the font can draw the Unicode scalar value.</summary>
    /// <param name="codePoint">The Unicode scalar value.</param>
    /// <returns>Whether the character can be drawn.</returns>
    bool Covers(int codePoint);
}

/// <summary>What <see cref="TextEditCheck.Evaluate"/> decided.</summary>
public enum TextEditOutcome
{
    /// <summary>The text is the old text; nothing changes (no history step, no unsaved change).</summary>
    Unchanged,

    /// <summary>Every new character is in the font of the run; the font stays.</summary>
    KeepFont,

    /// <summary>Some new characters are not; only those are set in the replacement font.</summary>
    SubstituteMissing,

    /// <summary>The whole run is set in the replacement font, as the user chose.</summary>
    WholeRunInGeist,

    /// <summary>The new text is empty: the run is removed (one history step; undo brings it back).</summary>
    Remove,

    /// <summary>The new text is refused as a whole (see <see cref="TextEditDecision.Rejection"/>); nothing is changed.</summary>
    Rejected,
}

/// <summary>Why a new text is refused.</summary>
public enum TextEditRejection
{
    /// <summary>The text is not refused.</summary>
    None,

    /// <summary>The text has more than <see cref="TextEditLimits.MaxCharsPerRun"/> characters.</summary>
    TooLong,

    /// <summary>A new character is in neither the font of the run nor the replacement font (<see cref="TextEditDecision.Unsupported"/>).</summary>
    UnsupportedCharacters,
}

/// <summary>A piece of the new text and the font it is set in.</summary>
/// <param name="Text">The text.</param>
/// <param name="InGeist">Whether the piece is set in the replacement font.</param>
public sealed record TextEditSegment(string Text, bool InGeist);

/// <summary>The decision about a new text of a run.</summary>
/// <param name="Outcome">What happens.</param>
/// <param name="Segments">The new text cut into pieces by font; empty for <see cref="TextEditOutcome.Rejected"/> and <see cref="TextEditOutcome.Remove"/>.</param>
/// <param name="Missing">The new characters the font of the run lacks (each once, in the order they come).</param>
/// <param name="Unsupported">The new characters that are in neither font (each once).</param>
/// <param name="WholeRunInGeistPossible">Whether every character of the new text is in the replacement font, so that the user may choose the whole run in it.</param>
/// <param name="Rejection">Why the text is refused.</param>
public sealed record TextEditDecision(
    TextEditOutcome Outcome,
    IReadOnlyList<TextEditSegment> Segments,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unsupported,
    bool WholeRunInGeistPossible,
    TextEditRejection Rejection);

/// <summary>
/// The rule of Spec 3.3: only the characters that are new are checked; the unchanged ones stay in their font even if the replacement font lacks
/// them. A new character that the font of the run lacks goes to Geist Regular alone; one that neither has refuses the whole text.
/// </summary>
public static class TextEditCheck
{
    /// <summary>Decides what to do with <paramref name="newText"/> for a run that shows <paramref name="oldText"/>.</summary>
    /// <param name="oldText">The text of the run.</param>
    /// <param name="newText">The new text.</param>
    /// <param name="existing">Which characters the font of the run can draw.</param>
    /// <param name="mode">Whether the user asked for the whole run in the replacement font.</param>
    /// <returns>The decision.</returns>
    public static TextEditDecision Evaluate(string oldText, string newText, IFontCoverage existing, TextEditMode mode)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentNullException.ThrowIfNull(existing);

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return new TextEditDecision(TextEditOutcome.Unchanged, [new TextEditSegment(newText, false)], [], [], GeistHasAll(Elements(newText)), TextEditRejection.None);
        }

        if (newText.Length == 0)
        {
            return new TextEditDecision(TextEditOutcome.Remove, [], [], [], false, TextEditRejection.None);
        }

        var next = Elements(newText);
        var previous = Elements(oldText);
        var wholePossible = GeistHasAll(next);
        if (next.Count > TextEditLimits.MaxCharsPerRun)
        {
            return new TextEditDecision(TextEditOutcome.Rejected, [], [], [], wholePossible, TextEditRejection.TooLong);
        }

        var controls = next.Where(static e => e.Any(char.IsControl)).Distinct(StringComparer.Ordinal).ToArray();
        if (controls.Length > 0)
        {
            return new TextEditDecision(TextEditOutcome.Rejected, [], [], controls, wholePossible, TextEditRejection.UnsupportedCharacters);
        }

        var prefix = 0;
        while (prefix < Math.Min(previous.Count, next.Count) && previous[prefix] == next[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < Math.Min(previous.Count, next.Count) - prefix && previous[^(suffix + 1)] == next[^(suffix + 1)])
        {
            suffix++;
        }

        var known = new HashSet<string>(previous, StringComparer.Ordinal);
        var missing = new List<string>();
        for (var i = prefix; i < next.Count - suffix; i++)
        {
            var element = next[i];
            if (!known.Contains(element) && !Covered(existing, element) && !missing.Contains(element, StringComparer.Ordinal))
            {
                missing.Add(element);
            }
        }

        if (mode == TextEditMode.WholeRunInGeist)
        {
            if (wholePossible)
            {
                return new TextEditDecision(TextEditOutcome.WholeRunInGeist, [new TextEditSegment(newText, true)], missing, [], true, TextEditRejection.None);
            }

            return new TextEditDecision(TextEditOutcome.Rejected, [], missing, [.. next.Where(static e => !GeistHas(e)).Distinct(StringComparer.Ordinal)], false, TextEditRejection.UnsupportedCharacters);
        }

        if (missing.Count == 0)
        {
            return new TextEditDecision(TextEditOutcome.KeepFont, [new TextEditSegment(newText, false)], [], [], wholePossible, TextEditRejection.None);
        }

        var unsupported = missing.Where(static e => !GeistHas(e)).ToArray();
        if (unsupported.Length > 0)
        {
            return new TextEditDecision(TextEditOutcome.Rejected, [], missing, unsupported, wholePossible, TextEditRejection.UnsupportedCharacters);
        }

        var segments = new List<TextEditSegment>();
        for (var i = 0; i < next.Count; i++)
        {
            var inGeist = i >= prefix && i < next.Count - suffix && missing.Contains(next[i], StringComparer.Ordinal);
            if (segments.Count > 0 && segments[^1].InGeist == inGeist)
            {
                segments[^1] = segments[^1] with { Text = segments[^1].Text + next[i] };
            }
            else
            {
                segments.Add(new TextEditSegment(next[i], inGeist));
            }
        }

        return new TextEditDecision(TextEditOutcome.SubstituteMissing, segments, missing, [], wholePossible, TextEditRejection.None);
    }

    /// <summary>Splits a text into Unicode text elements (a surrogate pair or a letter with combining marks is one).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The elements.</returns>
    public static IReadOnlyList<string> Elements(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            result.Add(enumerator.GetTextElement());
        }

        return result;
    }

    private static bool Covered(IFontCoverage coverage, string element)
    {
        for (var i = 0; i < element.Length; i += char.IsSurrogatePair(element, i) ? 2 : 1)
        {
            if (!coverage.Covers(char.ConvertToUtf32(element, i)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool GeistHas(string element) => element.Length == 1 && TextBoxCharacters.IsSupported(element[0]);

    private static bool GeistHasAll(IReadOnlyList<string> elements) => elements.All(GeistHas);
}

/// <summary>What the new box of a run runs into.</summary>
[Flags]
public enum TextEditOverflow
{
    /// <summary>Nothing: the new text fits where it stands.</summary>
    None = 0,

    /// <summary>The new box overlaps the box of another character on the page.</summary>
    OverlapsText = 1,

    /// <summary>The new box reaches beyond the edge of the page.</summary>
    OutsidePage = 2,
}

/// <summary>The hint of Spec 3.4: a longer text may overlap other text or leave the page; the user is told, the text is not refused.</summary>
public static class TextEditOverlap
{
    /// <summary>The slack, in points: boxes that meet at an edge, or an edge off the page by less than this, do not count.</summary>
    public const double Slack = 0.01;

    /// <summary>Finds what the new box runs into.</summary>
    /// <param name="newBounds">The box of the run after the change, in page points from the top left.</param>
    /// <param name="otherCharBoxes">The boxes of the characters of the page that are not part of the run.</param>
    /// <param name="pageBounds">The page, in page points from the top left.</param>
    /// <returns>The flags.</returns>
    public static TextEditOverflow Find(PageRect newBounds, IReadOnlyList<PageRect> otherCharBoxes, PageRect pageBounds)
    {
        ArgumentNullException.ThrowIfNull(otherCharBoxes);
        var result = TextEditOverflow.None;
        if (newBounds.Left < pageBounds.Left - Slack || newBounds.Top < pageBounds.Top - Slack || newBounds.Right > pageBounds.Right + Slack || newBounds.Bottom > pageBounds.Bottom + Slack)
        {
            result |= TextEditOverflow.OutsidePage;
        }

        foreach (var box in otherCharBoxes)
        {
            if (box.Left < newBounds.Right - Slack && newBounds.Left < box.Right - Slack && box.Top < newBounds.Bottom - Slack && newBounds.Top < box.Bottom - Slack)
            {
                result |= TextEditOverflow.OverlapsText;
                break;
            }
        }

        return result;
    }
}
