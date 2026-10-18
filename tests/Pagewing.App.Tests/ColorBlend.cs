using Avalonia.Media;

namespace Pagewing.App.Tests;

/// <summary>Colour mixing as the frame stores it, for the contrast checks of translucent marks (with <see cref="Wcag"/>).</summary>
internal static class ColorBlend
{
    /// <summary>Source-over of a translucent colour on an opaque one, rounded to 8 bits as the frame stores it.</summary>
    public static Color Over(Color overlay, Color under)
    {
        var alpha = overlay.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round((overlay.R * alpha) + (under.R * (1 - alpha))),
            (byte)Math.Round((overlay.G * alpha) + (under.G * (1 - alpha))),
            (byte)Math.Round((overlay.B * alpha) + (under.B * (1 - alpha))));
    }
}
