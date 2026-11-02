namespace Pagewing.Core.Redaction;

/// <summary>
/// Why a page cannot be redacted (the reason of a <see cref="RedactionRefusalKind.PageNotRedactable"/> refusal), in groups the interface
/// can say in its own language. The engine gives the reason as one English sentence; <see cref="RedactionPageCauses.Classify"/> reads the
/// group from it, and a test keeps every sentence the engine can produce in a group (a new sentence without one fails it).
/// </summary>
public enum RedactionPageCause
{
    /// <summary>The reason is not known to the interface: it shows the English sentence and says so.</summary>
    Unknown,

    /// <summary>The page looks different outside the marked areas after it is written again (the render check): PDFium cannot write it without changing it.</summary>
    ChangesOutsideAreas,

    /// <summary>The page changed its size when it was written again.</summary>
    SizeChanged,

    /// <summary>The page could not be rendered for the check, so it cannot be shown that nothing outside the marked areas changed.</summary>
    RenderFailed,

    /// <summary>The rewritten document could not be opened again for the check.</summary>
    ReopenFailed,

    /// <summary>The page holds more than the engine handles (objects, characters, image pixels): a limit against hostile pages.</summary>
    TooLarge,

    /// <summary>A part of the page (bounds, geometry, text, the box of a character) could not be read reliably, so it cannot be told what lies under a mark.</summary>
    Unreadable,

    /// <summary>PDFium could not write the content of the page again reliably, or the page did not pass the read-back after it was rewritten.</summary>
    RewriteUnreliable,

    /// <summary>A shape that covers more than half of the page lies under a mark and can be neither cut nor removed as a whole.</summary>
    BigShape,

    /// <summary>The annotations of the page (replies, popups) are too many or nested too deeply to follow completely.</summary>
    AnnotationsTooComplex,
}

/// <summary>Reads the <see cref="RedactionPageCause"/> from the English reason of a refused page.</summary>
public static class RedactionPageCauses
{
    // The start of the sentence and, where a number sits in the middle, a word that follows it (no pattern engine: Native AOT).
    private static readonly (string Start, string? Then, RedactionPageCause Cause)[] Table =
    [
        ("The page has ", " annotations; at most", RedactionPageCause.AnnotationsTooComplex),
        ("The replies and popups of the annotations of the page could not be followed", null, RedactionPageCause.AnnotationsTooComplex),
        ("Replies to annotations nest deeper than", null, RedactionPageCause.AnnotationsTooComplex),
        ("The page looks different outside the marked areas after it is written again", null, RedactionPageCause.ChangesOutsideAreas),
        ("The page changed its size when it was written again", null, RedactionPageCause.SizeChanged),
        ("The page could not be rendered for the check", null, RedactionPageCause.RenderFailed),
        ("The rewritten document could not be opened again for the check", null, RedactionPageCause.ReopenFailed),
        ("The page has ", " objects; at most", RedactionPageCause.TooLarge),
        ("The page has ", " characters; at most", RedactionPageCause.TooLarge),
        ("The rewritten page has ", " characters; at most", RedactionPageCause.TooLarge),
        ("The form objects of the page hold more than", null, RedactionPageCause.TooLarge),
        ("An image under a rectangle has ", " pixels; at most", RedactionPageCause.TooLarge),
        ("The images of the page lie under the rectangles in more than", null, RedactionPageCause.TooLarge),
        ("A shape that covers more than half of the page", null, RedactionPageCause.BigShape),
        ("The bounds of a text object could not be read", null, RedactionPageCause.Unreadable),
        ("The bounds of an image could not be read", null, RedactionPageCause.Unreadable),
        ("The bounds of a path could not be read", null, RedactionPageCause.Unreadable),
        ("The geometry of the page could not be read", null, RedactionPageCause.Unreadable),
        ("The text of the page could not be read", null, RedactionPageCause.Unreadable),
        ("The box of a character could not be read", null, RedactionPageCause.Unreadable),
        ("A character under a rectangle belongs to no text object", null, RedactionPageCause.Unreadable),
        ("A text object under a rectangle could not be located", null, RedactionPageCause.Unreadable),
        ("The text could not be rewritten reliably", null, RedactionPageCause.RewriteUnreliable),
        ("The characters that stay could not be read back", null, RedactionPageCause.RewriteUnreliable),
        ("An image was lost when the page was written again", null, RedactionPageCause.RewriteUnreliable),
        ("An image that PDFium would drop", null, RedactionPageCause.RewriteUnreliable),
        ("The page does not match its scratch copy", null, RedactionPageCause.RewriteUnreliable),
        ("PDFium could not write the content of the page", null, RedactionPageCause.RewriteUnreliable),
        ("The rewritten page could not be loaded again", null, RedactionPageCause.RewriteUnreliable),
        ("The text of the rewritten page could not be read", null, RedactionPageCause.RewriteUnreliable),
        ("Text under a rectangle is still readable after the page was rewritten", null, RedactionPageCause.RewriteUnreliable),
    ];

    /// <summary>Gets the group of a reason; <see cref="RedactionPageCause.Unknown"/> for a sentence that is in no group.</summary>
    /// <param name="reason">The reason of a <see cref="RedactionRefusalKind.PageNotRedactable"/> refusal.</param>
    public static RedactionPageCause Classify(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return RedactionPageCause.Unknown;
        }

        foreach (var (start, then, cause) in Table)
        {
            if (reason.StartsWith(start, StringComparison.Ordinal) && (then is null || reason.Contains(then, StringComparison.Ordinal)))
            {
                return cause;
            }
        }

        return RedactionPageCause.Unknown;
    }
}
