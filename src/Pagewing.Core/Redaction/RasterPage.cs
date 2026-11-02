namespace Pagewing.Core.Redaction;

/// <summary>
/// A page that is nothing but one image (milestone 14.1): what a page turned into pixels is written as.
/// </summary>
/// <param name="WidthPt">The width of the page (its media box) in points.</param>
/// <param name="HeightPt">The height of the page (its media box) in points.</param>
/// <param name="Rotation">The <c>/Rotate</c> written on the page: 0, 90, 180 or 270. The image fills the media box and is turned with it, so a caller that rendered the displayed page passes the displayed size and 0.</param>
/// <param name="WidthPx">The width of the image in pixels.</param>
/// <param name="HeightPx">The height of the image in pixels.</param>
/// <param name="Bgra">The pixels, top row first, blue, green, red, alpha: <c>WidthPx * HeightPx * 4</c> bytes. The page is opaque, the alpha byte is ignored and no soft mask is written. Not copied: the caller leaves it alone until the call has finished.</param>
public sealed record RasterPage(double WidthPt, double HeightPt, int Rotation, int WidthPx, int HeightPx, byte[] Bgra);
