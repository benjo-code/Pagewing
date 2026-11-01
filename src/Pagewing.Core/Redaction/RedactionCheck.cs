namespace Pagewing.Core.Redaction;

/// <summary>
/// A text that redaction removed from a page (or left to remove everywhere), for the check of the result. The strings come from the
/// text the engine reported (<see cref="PageRedactionImpact.RemovedText"/> reports U+FFFD for characters it could not read exactly)
/// or from the terms of the marks: the check compares them as given, never repairs them, and says in its report which ones it
/// could not use. The caller supplies real Unicode text (from the plan's terms or the text of the page), not an impact string with U+FFFD.
/// </summary>
/// <param name="Page">The 0-based page the text was removed from (for the report; the counts are for the whole document).</param>
/// <param name="Text">The text as the page held it.</param>
/// <param name="Everywhere">Whether the text must be gone from the whole document.</param>
/// <param name="OccurrencesBefore">How often the text occurred in the text of the whole document before redaction (with <see cref="Everywhere"/> it is not used).</param>
public sealed record RemovedText(int Page, string Text, bool Everywhere, int OccurrencesBefore);

/// <summary>What the check expects of a result: the plan that was applied, the text that went, and the colour of the areas.</summary>
/// <param name="Plan">The marks that were applied.</param>
/// <param name="Removed">The texts that were removed, one entry per removed place.</param>
/// <param name="Fill">The colour the areas were filled with.</param>
public sealed record RedactionExpectation(RedactionPlan Plan, IReadOnlyList<RemovedText> Removed, RedactionFill Fill);

/// <summary>The kinds of finding of the check.</summary>
public enum RedactionFindingKind
{
    /// <summary>A removed text is still in the page text of the result, more often than the removal explains.</summary>
    TextStillPresent,

    /// <summary>A character of the result lies inside a marked area.</summary>
    CharInsideArea,

    /// <summary>A removed text is still in the bytes of the file (an old revision, metadata, an attachment, a pattern ...) more often than the page text shows it.</summary>
    RawBytesStillPresent,

    /// <summary>A pixel of an image under a marked area is not the colour of the area.</summary>
    ImagePixelNotOverwritten,

    /// <summary>Something could not be read, so the check cannot say whether text hides in it.</summary>
    UnreadableStream,

    /// <summary>A page still has a key that carries content the cleanup cannot remove (set by the apply step, not by <see cref="RedactionVerifier"/>).</summary>
    PageKeysStillPresent,

    /// <summary>A page looks different outside the marked areas (set by the apply step, not by <see cref="RedactionVerifier"/>).</summary>
    PageChangedOutsideAreas,
}

/// <summary>One thing the check found. The detail names the kind of the problem and the place, never the text itself.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Page">The 0-based page, or <see langword="null"/> when the finding concerns the file.</param>
/// <param name="Detail">A short English explanation.</param>
public sealed record RedactionFinding(RedactionFindingKind Kind, int? Page, string Detail);

/// <summary>The result of the check: what it found and, always, what it did not check.</summary>
/// <param name="Findings">The findings; none means the check passed.</param>
/// <param name="NotChecked">What could not be checked, one line each; never empty by omission: the report never promises more than the check does.</param>
public sealed record RedactionCheck(IReadOnlyList<RedactionFinding> Findings, IReadOnlyList<string> NotChecked)
{
    /// <summary>Gets whether the check found nothing.</summary>
    public bool IsClean => Findings.Count == 0;
}
