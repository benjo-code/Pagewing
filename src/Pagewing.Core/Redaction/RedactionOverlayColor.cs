namespace Pagewing.Core.Redaction;

/// <summary>
/// The colour of the overlay text (milestone 14.1): a colour the user chose stands as it is (a choice below the contrast of
/// <see cref="MinContrast"/> is warned about by the interface, never refused), otherwise black or white, whichever reads better on the fill
/// of the areas. The contrast is the one of WCAG 2.2 (relative luminance of sRGB).
/// </summary>
public static class RedactionOverlayColor
{
    /// <summary>The least contrast, as a ratio to 1, the automatic colour is meant to reach (WCAG 2.2 AA for text).</summary>
    public const double MinContrast = 4.5;

    /// <summary>Chooses the colour of the text on areas of the given fill.</summary>
    /// <param name="fill">The colour of the areas.</param>
    /// <param name="chosen">The colour the user chose, or <see langword="null"/> for the automatic one.</param>
    /// <returns>
    /// The chosen colour, or, for the automatic one, white or black, whichever has the higher contrast with the fill. One of the two always
    /// reaches <see cref="MinContrast"/> (the best of black and white never falls below 4.58:1), so the rule "at least 4.5:1, otherwise the
    /// colour with the higher contrast" is this one.
    /// </returns>
    public static RedactionFill Choose(RedactionFill fill, RedactionFill? chosen)
    {
        if (chosen is { } own)
        {
            return own;
        }

        return Contrast(RedactionFill.White, fill) >= Contrast(RedactionFill.Black, fill) ? RedactionFill.White : RedactionFill.Black;
    }

    /// <summary>The contrast of two colours as a ratio from 1 to 21 (WCAG 2.2).</summary>
    /// <param name="a">The first colour.</param>
    /// <param name="b">The second colour.</param>
    public static double Contrast(RedactionFill a, RedactionFill b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var light = Math.Max(la, lb);
        var dark = Math.Min(la, lb);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double Luminance(RedactionFill color) =>
        (0.2126 * Linear(color.Red)) + (0.7152 * Linear(color.Green)) + (0.0722 * Linear(color.Blue));

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// What is left of the overlay text budget of a redaction (<see cref="RedactionLimits.MaxOverlayPaths"/>, <see cref="RedactionLimits.MaxOverlaySegments"/>)
/// and whether it was reached. Pages are taken in the same order by the analysis and by applying (pages redacted in place by their number,
/// then the pages turned into images by their number), so both decide alike.
/// </summary>
/// <param name="Paths">The path objects that may still be drawn.</param>
/// <param name="Segments">The path segments that may still be drawn.</param>
/// <param name="Reached">Whether the limit was reached: every overlay text after that point is left out.</param>
internal readonly record struct OverlayBudget(int Paths, long Segments, bool Reached)
{
    /// <summary>Gets the whole budget of a document.</summary>
    public static OverlayBudget Full { get; } = new(RedactionLimits.MaxOverlayPaths, RedactionLimits.MaxOverlaySegments, false);

    /// <summary>The budget that remains after a page used <paramref name="paths"/> and <paramref name="segments"/>.</summary>
    /// <param name="paths">The path objects drawn.</param>
    /// <param name="segments">The path segments drawn.</param>
    /// <param name="reached">Whether the page reached the limit.</param>
    public OverlayBudget After(int paths, long segments, bool reached) =>
        new(Math.Max(0, Paths - paths), Math.Max(0, Segments - segments), Reached || reached);
}
