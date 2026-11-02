using Pagewing.Ocr;

namespace Pagewing.Core.Ocr;

/// <summary>A recognized word on the page: its text, its box in points from the top left of the displayed page and the engine's confidence (0 to 100).</summary>
public sealed record OcrPageWord(string Text, double Left, double Top, double Right, double Bottom, float Confidence)
{
    /// <summary>Gets whether the word has a text and a box with an area (an empty box or a blank text is dropped, not written).</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Text) && Right > Left && Bottom > Top;
}

/// <summary>Turns the pixels of a rendered page into points of the displayed page.</summary>
public static class OcrWordMapper
{
    /// <summary>
    /// Maps a word. The page was rendered as it is displayed (PDFium applied rotation and crop box), so the pixels are already upright and
    /// only the scale is left: the displayed size in points over the size in pixels. The box is cut to the page.
    /// </summary>
    /// <param name="word">The word in pixels from the top left of the rendered page.</param>
    /// <param name="raster">The size the page was rendered at.</param>
    /// <param name="widthPt">The width of the displayed page in points.</param>
    /// <param name="heightPt">The height of the displayed page in points.</param>
    public static OcrPageWord ToPagePoints(OcrWord word, OcrRaster raster, double widthPt, double heightPt)
    {
        ArgumentNullException.ThrowIfNull(raster);
        var scaleX = widthPt / raster.WidthPx;
        var scaleY = heightPt / raster.HeightPx;
        return new OcrPageWord(
            word.Text,
            Math.Clamp(word.Left * scaleX, 0, widthPt),
            Math.Clamp(word.Top * scaleY, 0, heightPt),
            Math.Clamp(word.Right * scaleX, 0, widthPt),
            Math.Clamp(word.Bottom * scaleY, 0, heightPt),
            word.Confidence);
    }
}
