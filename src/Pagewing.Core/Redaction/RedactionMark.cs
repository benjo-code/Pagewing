using System.Globalization;
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

/// <summary>
/// The colour of the areas that replace the removed content: an opaque RGB colour. Black is the default, white the other
/// preset; any other colour comes from the colour field of the tool bar (as <c>#RRGGBB</c>).
/// </summary>
/// <param name="Red">The red part, 0 to 255.</param>
/// <param name="Green">The green part, 0 to 255.</param>
/// <param name="Blue">The blue part, 0 to 255.</param>
public readonly record struct RedactionFill(byte Red, byte Green, byte Blue)
{
    /// <summary>Gets opaque black (the default).</summary>
    public static RedactionFill Black { get; } = new(0, 0, 0);

    /// <summary>Gets opaque white.</summary>
    public static RedactionFill White { get; } = new(255, 255, 255);

    /// <summary>
    /// Gets the five colours the colour field offers besides black and white (graphite, grey, light grey, navy, dark red): neutral
    /// colours that read as "taken out" on paper, in the order of the field.
    /// </summary>
    public static IReadOnlyList<RedactionFill> Presets { get; } =
    [
        new(0x33, 0x33, 0x33),
        new(0x80, 0x80, 0x80),
        new(0xD9, 0xD9, 0xD9),
        new(0x1F, 0x3A, 0x5F),
        new(0x7A, 0x1F, 0x1F),
    ];

    /// <summary>Reads a colour written as <c>#RRGGBB</c> (hexadecimal digits in either case); nothing else is accepted.</summary>
    /// <param name="text">The text.</param>
    /// <param name="fill">The colour; black if the text is not one.</param>
    /// <returns>Whether the text is a colour.</returns>
    public static bool TryParse(string? text, out RedactionFill fill)
    {
        fill = default;
        if (text is null || text.Length != 7 || text[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!char.IsAsciiHexDigit(text[i]))
            {
                return false;
            }
        }

        var bytes = Convert.FromHexString(text.AsSpan(1));
        fill = new RedactionFill(bytes[0], bytes[1], bytes[2]);
        return true;
    }

    /// <summary>Writes the colour as <c>#RRGGBB</c> in upper case.</summary>
    /// <returns>The text.</returns>
    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{Red:X2}{Green:X2}{Blue:X2}");
}

/// <summary>What happens to the part of an image under a mark.</summary>
public enum RedactionImageTreatment
{
    /// <summary>The pixels are overwritten with the fill colour (the default).</summary>
    Solid,

    /// <summary>The pixels are replaced by the mean of coarse blocks.</summary>
    Pixelate,

    /// <summary>The pixels are replaced by a blur of coarse blocks.</summary>
    Blur,
}

/// <summary>How coarse <see cref="RedactionImageTreatment.Pixelate"/> and <see cref="RedactionImageTreatment.Blur"/> work.</summary>
public enum RedactionImageStrength
{
    /// <summary>At most <see cref="RedactionLimits.CoarseBlocksPerAxis"/> blocks along each side of an area.</summary>
    Coarse,

    /// <summary>At most <see cref="RedactionLimits.VeryCoarseBlocksPerAxis"/> blocks along each side of an area.</summary>
    VeryCoarse,
}

/// <summary>The kind of a pattern that "search and redact" can look for.</summary>
public enum RedactionPatternKind
{
    /// <summary>An e-mail address.</summary>
    Email,

    /// <summary>An IBAN.</summary>
    Iban,

    /// <summary>A telephone number.</summary>
    Phone,

    /// <summary>A payment card number.</summary>
    Card,
}

/// <summary>How the text on the areas of a plan is drawn.</summary>
/// <param name="Repeat">Whether the text is repeated over the area instead of drawn once.</param>
/// <param name="AutoSize">Whether the size is chosen to fit the area; otherwise a fixed size.</param>
/// <param name="TextColor">The colour of the text; <see langword="null"/> chooses black or white by contrast against the fill.</param>
public sealed record RedactionOverlayStyle(bool Repeat = false, bool AutoSize = true, RedactionFill? TextColor = null);

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
/// <param name="OverlayText">The text drawn on the area; <see langword="null"/> for none. Only characters of <see cref="Annotations.TextBoxCharacters"/>, at most <see cref="RedactionLimits.MaxOverlayTextLength"/>; the plan turns white space alone into <see langword="null"/>.</param>
/// <param name="Pattern">The pattern kind the mark was found by, or <see langword="null"/>. The term is still the literal text, never the pattern.</param>
public sealed record RedactionMark(Guid Id, RedactionKind Kind, int Page, IReadOnlyList<PageRect> Areas, string? Term, bool Everywhere, string? OverlayText = null, RedactionPatternKind? Pattern = null);
