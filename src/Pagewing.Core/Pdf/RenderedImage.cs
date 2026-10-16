namespace Pagewing.Core.Pdf;

/// <summary>
/// A rendered page or tile: <see cref="Height"/> rows of <see cref="Stride"/> bytes, 4 bytes per pixel in the
/// order blue, green, red, alpha (BGRA, top row first). The image is opaque: alpha is always 0xFF.
/// </summary>
public sealed class RenderedImage
{
    /// <summary>Creates an image over existing BGRA pixels (no copy).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The sizes are not positive or the pixels are too short.</exception>
    public RenderedImage(int width, int height, int stride, ReadOnlyMemory<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan((long)stride, (long)width * 4, nameof(stride));
        ArgumentOutOfRangeException.ThrowIfLessThan((long)pixels.Length, (long)stride * height, nameof(pixels));
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the number of bytes per row (at least <see cref="Width"/> × 4).</summary>
    public int Stride { get; }

    /// <summary>Gets the pixels as BGRA, opaque (alpha 0xFF).</summary>
    public ReadOnlyMemory<byte> Pixels { get; }
}
