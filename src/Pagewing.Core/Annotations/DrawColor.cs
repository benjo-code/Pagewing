namespace Pagewing.Core.Annotations;

/// <summary>The colours offered for the drawing tools; <see cref="DrawPalette"/> maps them to RGB.</summary>
public enum DrawColor
{
    /// <summary>Red, the default.</summary>
    Red,

    /// <summary>Black.</summary>
    Black,

    /// <summary>Blue.</summary>
    Blue,

    /// <summary>Green.</summary>
    Green,

    /// <summary>Orange.</summary>
    Orange,
}

/// <summary>The five colours of the drawing tools and their RGB values.</summary>
public static class DrawPalette
{
    // RGB: Red (220,30,30), Black (0,0,0), Blue (30,80,220), Green (20,140,60), Orange (240,130,0).
    private static readonly AnnotationColor[] Values =
    [
        new(220, 30, 30),
        new(0, 0, 0),
        new(30, 80, 220),
        new(20, 140, 60),
        new(240, 130, 0),
    ];

    /// <summary>Gets all colours in the order they are offered.</summary>
    public static IReadOnlyList<DrawColor> All { get; } =
        [DrawColor.Red, DrawColor.Black, DrawColor.Blue, DrawColor.Green, DrawColor.Orange];

    /// <summary>Gets the RGB value of a colour.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The RGB value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="color"/> is not a defined colour.</exception>
    public static AnnotationColor ColorOf(DrawColor color)
    {
        var index = (int)color;
        ArgumentOutOfRangeException.ThrowIfNegative(index, nameof(color));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Values.Length, nameof(color));
        return Values[index];
    }

    /// <summary>Gets the colour whose RGB value is exactly <paramref name="color"/>.</summary>
    /// <param name="color">The RGB value, for example read from an annotation.</param>
    /// <returns>The colour, or <see langword="null"/> for a colour of another application.</returns>
    public static DrawColor? Match(AnnotationColor color)
    {
        for (var i = 0; i < Values.Length; i++)
        {
            if (Values[i] == color)
            {
                return All[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the key of the colour's name in the UI resources: <c>DrawColor</c> and the name of the colour
    /// (<c>DrawColorRed</c>), so the key is also the name of its accessor.
    /// </summary>
    /// <param name="color">The colour.</param>
    /// <returns>The resource key.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="color"/> is not a defined colour.</exception>
    public static string NameKey(DrawColor color)
    {
        _ = ColorOf(color);
        return "DrawColor" + color;
    }
}
