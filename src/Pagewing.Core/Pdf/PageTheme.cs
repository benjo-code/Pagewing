namespace Pagewing.Core.Pdf;

/// <summary>How the content of a page is coloured when it is rendered.</summary>
public enum PageTheme
{
    /// <summary>As the document says: the page on white, as before (the default).</summary>
    Light,

    /// <summary>
    /// A dark page: the ground is <see cref="PageThemeColors.DarkBackground"/>, text and path strokes are drawn in
    /// <see cref="PageThemeColors.DarkForeground"/>, filled paths become outlines in that colour, and images keep
    /// their colours. Details: <c>Docs/DECISIONS.md</c>, entry "Dunkle Seite: PDFium-Farbschema mit Fill-to-Stroke (M6)".
    /// </summary>
    Dark,
}

/// <summary>The colours of <see cref="PageTheme.Dark"/> as ARGB (<c>0xAARRGGBB</c>), opaque.</summary>
public static class PageThemeColors
{
    /// <summary>The ground of a dark page: <c>#1E2127</c>.</summary>
    public const uint DarkBackground = 0xFF1E2127;

    /// <summary>Text, strokes and outlines on a dark page: <c>#E3E5E8</c> (12.78:1 on the ground).</summary>
    public const uint DarkForeground = 0xFFE3E5E8;
}
