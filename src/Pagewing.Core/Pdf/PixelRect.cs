namespace Pagewing.Core.Pdf;

/// <summary>A rectangle in device pixels of a rendered page, origin top left.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);
