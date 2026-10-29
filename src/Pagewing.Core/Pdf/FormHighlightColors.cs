namespace Pagewing.Core.Pdf;

/// <summary>
/// The highlight PDFium lays over the fillable form fields when <see cref="RenderOptions.FormHighlight"/> is on: a tint in
/// the light accent (<c>accent.default</c> of the light theme, checked by <c>TokenTests</c>) at a quarter opacity, drawn by
/// <c>FPDF_FFLDraw</c> over each field that is neither read-only nor a signature or push button.
/// </summary>
/// <remarks>
/// PDFium draws the tint over the field's appearance, value included, so its opacity decides both how far the field stands out
/// and how readable the value stays. A quarter keeps black text at about 12:1 on the tint; the tint itself is a cue, not a
/// state (required and read-only fields are marked with symbols, never by colour alone). Docs/DECISIONS.md, entry
/// "Formulare: Felder im Rendern, Hervorhebung und dunkle Seite (M11)".
/// </remarks>
public static class FormHighlightColors
{
    /// <summary>The colour as RGB (<c>0x00RRGGBB</c>): <c>#3B48C4</c>.</summary>
    public const uint Color = 0x3B48C4;

    /// <summary>The opacity (0 to 255): <c>0x40</c>, a quarter.</summary>
    public const byte Alpha = 0x40;
}
