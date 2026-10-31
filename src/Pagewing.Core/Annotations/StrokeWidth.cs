namespace Pagewing.Core.Annotations;

/// <summary>The stroke widths offered for the drawing tools; <see cref="StrokeWidths"/> maps them to points.</summary>
public enum StrokeWidth
{
    /// <summary>Thin: 1 pt.</summary>
    Thin,

    /// <summary>Medium: 2 pt, the default.</summary>
    Medium,

    /// <summary>Thick: 4 pt.</summary>
    Thick,
}

/// <summary>The widths of <see cref="StrokeWidth"/> in points.</summary>
public static class StrokeWidths
{
    /// <summary>Gets the width in points.</summary>
    /// <param name="width">The width.</param>
    /// <returns>The width in points: 1, 2 or 4.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not a defined width.</exception>
    public static double PointsOf(StrokeWidth width) => width switch
    {
        StrokeWidth.Thin => 1,
        StrokeWidth.Medium => 2,
        StrokeWidth.Thick => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(width)),
    };
}
