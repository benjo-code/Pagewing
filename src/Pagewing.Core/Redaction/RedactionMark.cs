using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>How a mark came about.</summary>
public enum RedactionKind
{
    /// <summary>A rectangle dragged on the page.</summary>
    Area,

    /// <summary>A text selection: the rectangles are the character boxes per line, the text is the term for the check.</summary>
    Text,

    /// <summary>A hit of "search and redact"; like <see cref="Text"/>.</summary>
    Hit,
}

/// <summary>The colour of the area that replaces the removed content.</summary>
public enum RedactionFill
{
    /// <summary>Opaque black (the default).</summary>
    Black,

    /// <summary>Opaque white.</summary>
    White,
}

/// <summary>
/// One thing to redact. It is checked when it enters a <see cref="RedactionPlan"/>; the plan needs no page size, so rectangles
/// outside the page are kept and clipped by the engine later. The term is kept exactly as given: comparing it (case, Unicode
/// forms) is the job of the search and the check, not of the mark.
/// </summary>
/// <param name="Id">The identity of the mark; unique within a plan.</param>
/// <param name="Kind">How the mark came about.</param>
/// <param name="Page">The 0-based page.</param>
/// <param name="Areas">The rectangles in points on that page, origin top left, as the user sees the page.</param>
/// <param name="Term">The text for the check (required for <see cref="RedactionKind.Text"/> and <see cref="RedactionKind.Hit"/>); for an area optionally the text read under it. Never interpreted as a pattern.</param>
/// <param name="Everywhere">Whether the term must be gone from the whole document, not only from the marked places.</param>
public sealed record RedactionMark(Guid Id, RedactionKind Kind, int Page, IReadOnlyList<PageRect> Areas, string? Term, bool Everywhere);
