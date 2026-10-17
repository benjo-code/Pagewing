using Avalonia.Media;

namespace Pagewing.App.Tests;

/// <summary>WCAG 2.2 relative luminance and contrast ratio (https://www.w3.org/TR/WCAG22/#dfn-contrast-ratio).</summary>
internal static class Wcag
{
    /// <summary>Relative luminance of an opaque sRGB colour.</summary>
    public static double RelativeLuminance(Color color)
    {
        if (color.A != 255)
        {
            throw new ArgumentException("Contrast is only defined for opaque colours.", nameof(color));
        }

        return (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));
    }

    /// <summary>Contrast ratio (1 to 21) between two opaque colours; the order does not matter.</summary>
    public static double Contrast(Color first, Color second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        var (lighter, darker) = a >= b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
