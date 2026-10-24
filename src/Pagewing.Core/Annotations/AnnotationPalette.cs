namespace Pagewing.Core.Annotations;

/// <summary>The five colours of text markup and their RGB values.</summary>
public static class AnnotationPalette
{
    // RGB: Yellow (255,235,59), Green (139,224,139), Blue (124,196,250), Pink (255,143,199), Orange (255,180,84).
    private static readonly AnnotationColor[] Values =
    [
        new(255, 235, 59),
        new(139, 224, 139),
        new(124, 196, 250),
        new(255, 143, 199),
        new(255, 180, 84),
    ];

    /// <summary>Gets all colours in the order they are offered.</summary>
    public static IReadOnlyList<MarkupColor> All { get; } =
        [MarkupColor.Yellow, MarkupColor.Green, MarkupColor.Blue, MarkupColor.Pink, MarkupColor.Orange];

    /// <summary>Gets the RGB value of a colour.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The RGB value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="color"/> is not a defined colour.</exception>
    public static AnnotationColor ColorOf(MarkupColor color)
    {
        var index = (int)color;
        ArgumentOutOfRangeException.ThrowIfNegative(index, nameof(color));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Values.Length, nameof(color));
        return Values[index];
    }

    /// <summary>Gets the colour whose RGB value is exactly <paramref name="color"/>.</summary>
    /// <param name="color">The RGB value, for example read from an annotation.</param>
    /// <returns>The colour, or <see langword="null"/> for a colour of another application.</returns>
    public static MarkupColor? Match(AnnotationColor color)
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

    /// <summary>Gets the key of the colour's name in the UI resources.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The resource key.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="color"/> is not a defined colour.</exception>
    public static string NameKey(MarkupColor color)
    {
        _ = ColorOf(color);
        return "Markup.Color." + color;
    }
}
