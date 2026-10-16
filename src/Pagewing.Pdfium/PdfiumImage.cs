namespace Pagewing.Pdfium;

/// <summary>A rectangle in device pixels, origin top left.</summary>
internal readonly record struct PdfiumPixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// A rendered bitmap: <see cref="Height"/> rows of <see cref="Stride"/> bytes, 4 bytes per pixel in the order
/// blue, green, red, unused (<c>FPDFBitmap_BGRx</c>). The unused byte is always 0xFF, so the pixels can be
/// read as opaque BGRA. <see cref="Pixels"/> is exactly <see cref="Stride"/> × <see cref="Height"/> bytes long.
/// </summary>
internal sealed class PdfiumImage(int width, int height, int stride, ReadOnlyMemory<byte> pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride { get; } = stride;

    public ReadOnlyMemory<byte> Pixels { get; } = pixels;
}
