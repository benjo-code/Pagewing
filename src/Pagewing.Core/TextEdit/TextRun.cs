using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.Core.TextEdit;

/// <summary>What kind of font a run is set in (milestone 16; whether an embedded font is a subset cannot be told reliably, see <c>Docs/DECISIONS.md</c>, "Text bearbeiten: Spike (M16)").</summary>
public enum TextRunFontKind
{
    /// <summary>One of the 14 standard fonts, not embedded.</summary>
    Standard14,

    /// <summary>Any other font that is not embedded.</summary>
    NotEmbedded,

    /// <summary>An embedded font program (a subset or the whole font).</summary>
    Embedded,

    /// <summary>A Type 3 font (glyphs are small page descriptions); such a run cannot be edited.</summary>
    Type3,
}

/// <summary>Why a run of text cannot be edited; the user sees the reason (<c>TextEditLockText</c>), a run is never locked silently.</summary>
public enum TextEditLockReason
{
    /// <summary>The text is invisible (render mode 3), for example the text layer of a scan: what is seen is a picture.</summary>
    InvisibleText,

    /// <summary>The run or another run of its page is set in a Type 3 font; PDFium cannot write such text again, so the page cannot be rewritten.</summary>
    Type3Font,

    /// <summary>The writing direction is vertical.</summary>
    VerticalWriting,

    /// <summary>The render mode clips (4 to 7) or is unknown; rewriting would change the clipping of the page.</summary>
    UnsupportedRenderMode,

    /// <summary>The text is in a Form XObject; PDFium does not write the content of forms, and a shared form would change other pages.</summary>
    FormXObject,

    /// <summary>The marked content has <c>/ActualText</c>, <c>/Alt</c> or <c>/E</c> (search and copy would go on delivering the old text) or belongs to an optional content layer (<c>/OC</c>, which a new object would leave).</summary>
    MarkedContent,

    /// <summary>The document cannot be saved by Pagewing (encrypted, or the file changed outside).</summary>
    NotSavable,

    /// <summary>The check on a copy of the page found that rewriting the page would change something else; found when a run is replaced.</summary>
    PageNotRewritable,

    /// <summary>Two characters of the line stand at one place (a ligature glyph that stands for several characters): writing it again would draw them on top of each other.</summary>
    Ligature,

    /// <summary>The page has more runs than <see cref="TextEditLimits.MaxRunsPerPage"/>.</summary>
    TooManyRuns,

    /// <summary>The run has more characters than <see cref="TextEditLimits.MaxCharsPerRun"/>.</summary>
    RunTooLong,
}

/// <summary>Whether the new text keeps the font of the run or sets the whole run in the replacement font.</summary>
public enum TextEditMode
{
    /// <summary>The font of the run stays; characters it lacks are set in the replacement font (Geist Regular), only those.</summary>
    KeepFont,

    /// <summary>The whole run is set in the replacement font (Geist Regular); offered only when it has every character.</summary>
    WholeRunInGeist,
}

/// <summary>
/// A piece of text in one font, size and colour on one line that Pagewing can replace as a whole: one text object of PDFium, or after an edit the
/// consecutive objects it was replaced by.
/// </summary>
/// <param name="Page">The 0-based page.</param>
/// <param name="ObjectIndex">The index of the first page object of the run in the page's object list.</param>
/// <param name="ObjectCount">The number of consecutive page objects the run is made of (1 as read from a file).</param>
/// <param name="Text">The text.</param>
/// <param name="Bounds">The box in page points from the top left of the displayed page.</param>
/// <param name="FontName">The base font name.</param>
/// <param name="FontKind">The kind of font.</param>
/// <param name="FontSize">The font size in text space units (not scaled by the matrix).</param>
/// <param name="Color">The fill colour.</param>
/// <param name="Fingerprint">A hash of the text, matrix, font and size of the objects; a replacement refuses a run whose fingerprint no longer matches.</param>
/// <param name="Lock">Why the run cannot be edited, or <see langword="null"/>.</param>
public sealed record TextRun(
    int Page,
    int ObjectIndex,
    int ObjectCount,
    string Text,
    PageRect Bounds,
    string FontName,
    TextRunFontKind FontKind,
    double FontSize,
    AnnotationColor Color,
    string Fingerprint,
    TextEditLockReason? Lock)
{
    /// <summary>Gets whether the run cannot be edited.</summary>
    public bool IsLocked => Lock is not null;
}

/// <summary>The limits of the text edit (milestone 16), against hostile files and for the history.</summary>
public static class TextEditLimits
{
    /// <summary>The most characters (Unicode text elements) of one run; a longer run is locked, a longer new text is refused.</summary>
    public const int MaxCharsPerRun = 2000;

    /// <summary>The most runs one page is offered with; a page with more has all its runs locked (<see cref="TextEditLockReason.TooManyRuns"/>).</summary>
    public const int MaxRunsPerPage = 5000;

    /// <summary>The most page objects looked at when the runs of a page are read.</summary>
    public const int MaxObjectsScanned = 200_000;
}
