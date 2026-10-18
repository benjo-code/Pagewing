namespace Pagewing.Core.Text;

/// <summary>
/// A rectangle in PDF points. The origin is the top left of the displayed page (after rotation and CropBox);
/// y grows downwards, so <see cref="Top"/> is less than <see cref="Bottom"/>.
/// </summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">The right edge.</param>
/// <param name="Bottom">The bottom edge.</param>
public readonly record struct PageRect(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Gets the width (negative for an inverted rectangle).</summary>
    public double Width => Right - Left;

    /// <summary>Gets the height (negative for an inverted rectangle).</summary>
    public double Height => Bottom - Top;

    /// <summary>Gets whether the point lies inside the rectangle; all four edges count as inside.</summary>
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>Gets the smallest rectangle that contains this one and <paramref name="other"/>.</summary>
    public PageRect Union(PageRect other) => new(
        Math.Min(Left, other.Left),
        Math.Min(Top, other.Top),
        Math.Max(Right, other.Right),
        Math.Max(Bottom, other.Bottom));

    /// <summary>Gets the rectangle grown by <paramref name="amount"/> on every side (shrunk when negative).</summary>
    public PageRect Inflate(double amount) => new(Left - amount, Top - amount, Right + amount, Bottom + amount);
}
