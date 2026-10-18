using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// Reads the characters and links of a loaded page. Every method must be called on the PDFium thread and throws
/// <see cref="PdfiumThreadViolationException"/> otherwise.
/// </summary>
internal static unsafe class PdfiumPageText
{
    /// <summary>
    /// <c>FPDF_PageToDevice</c> works in whole device pixels; mapping to 16 pixels per point keeps 1/16 pt.
    /// </summary>
    private const int DeviceScale = 16;

    /// <summary>The size of the stack buffer for font names; longer names fall back to a rented array.</summary>
    private const int FontNameBufferLength = 256;

    /// <summary>The weight from which a font counts as bold.</summary>
    private const int BoldWeight = 600;

    /// <summary>
    /// What <c>FPDFText_GetUnicode</c> reports for a line-end hyphen PDFium recognized (it replaces the '-').
    /// <c>FPDFText_GetText</c> writes the same character as <see cref="HyphenTextMarker"/>.
    /// </summary>
    private const uint HyphenMarker = 0x0002;

    /// <summary>The line-end hyphen as <c>FPDFText_GetText</c> writes it; accepted here as well, in case PDFium aligns both.</summary>
    private const uint HyphenTextMarker = 0xFFFE;

    /// <summary>
    /// Reads all characters of the page. Without <paramref name="geometry"/> the boxes are 0, without
    /// <paramref name="style"/> the font size is 0 and Bold and Italic are false. A page without text gives an
    /// empty array.
    /// </summary>
    public static PdfiumChar[] ReadChars(PdfiumPage page, PdfiumTextPage text, bool geometry, bool style)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(text);
        var textPage = text.Access();
        var pageValue = page.Access();
        var count = Math.Max(0, Fpdf.FPDFText_CountChars(textPage));
        return Read(page, pageValue, textPage, 0, count, geometry, style);
    }

    /// <summary>Reads the characters <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/> - 1 with geometry and style.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is not inside the characters of the page.</exception>
    public static PdfiumChar[] ReadChars(PdfiumPage page, PdfiumTextPage text, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(text);
        var textPage = text.Access();
        var pageValue = page.Access();
        var total = Math.Max(0, Fpdf.FPDFText_CountChars(textPage));
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, total);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, total - start);
        return Read(page, pageValue, textPage, start, count, geometry: true, style: true);
    }

    /// <summary>
    /// Reads the links of the page: link annotations first, in page order, then the web addresses PDFium detects
    /// in the text. Areas of a web address that overlap a link annotation are dropped (the annotation wins); a web
    /// address without areas left is dropped.
    /// </summary>
    public static PdfiumLink[] ReadLinks(PdfiumDocument document, PdfiumPage page, PdfiumTextPage text)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(text);
        var documentValue = document.Access();
        var pageValue = page.Access();
        var textPage = text.Access();
        var transform = new PageTransform(pageValue, page.Width, page.Height);

        var anchors = new List<PdfiumLink>();
        ReadAnnotationLinks(documentValue, pageValue, transform, anchors);

        var webLinks = new List<PdfiumLink>();
        var linkPage = Fpdf.FPDFLink_LoadWebLinks(textPage);
        if (linkPage != 0)
        {
            try
            {
                ReadWebLinks(linkPage, transform, webLinks);
            }
            finally
            {
                Fpdf.FPDFLink_CloseWebLinks(linkPage);
            }
        }

        return Merge(anchors, webLinks);
    }

    /// <summary>
    /// Joins link annotations and detected web addresses: the annotations first, then the web addresses without
    /// their areas that overlap an annotation area; a web address without areas left is dropped.
    /// </summary>
    internal static PdfiumLink[] Merge(IReadOnlyList<PdfiumLink> anchors, IReadOnlyList<PdfiumLink> webLinks)
    {
        var links = new List<PdfiumLink>(anchors.Count + webLinks.Count);
        links.AddRange(anchors);
        foreach (var webLink in webLinks)
        {
            var rects = webLink.Rects.Where(area => !OverlapsAnchor(area, anchors)).ToArray();
            if (rects.Length > 0)
            {
                links.Add(webLink with { Rects = rects });
            }
        }

        return [.. links];
    }

    private static PdfiumChar[] Read(PdfiumPage page, nint pageValue, nint textPage, int start, int count, bool geometry, bool style)
    {
        if (count == 0)
        {
            return [];
        }

        var transform = geometry ? new PageTransform(pageValue, page.Width, page.Height) : default;
        var chars = new PdfiumChar[count];
        Span<byte> nameBuffer = stackalloc byte[FontNameBufferLength];
        for (var i = 0; i < count; i++)
        {
            var index = start + i;
            var hyphen = Fpdf.FPDFText_IsHyphen(textPage, index) == 1;
            var codePoint = ToCodePoint(Fpdf.FPDFText_GetUnicode(textPage, index), hyphen);
            var generated = Fpdf.FPDFText_IsGenerated(textPage, index) == 1;

            (double Left, double Top, double Right, double Bottom) box = default;
            if (geometry)
            {
                double left, right, bottom, top;
                if (Fpdf.FPDFText_GetCharBox(textPage, index, &left, &right, &bottom, &top) != 0)
                {
                    box = transform.ToPage(left, top, right, bottom);
                }
            }

            float fontSize = 0;
            bool bold = false, italic = false;
            if (style)
            {
                fontSize = (float)Fpdf.FPDFText_GetFontSize(textPage, index);
                (bold, italic) = ReadFontStyle(textPage, index, nameBuffer);
            }

            chars[i] = new PdfiumChar(codePoint, box.Left, box.Top, box.Right, box.Bottom, generated, hyphen, fontSize, bold, italic);
        }

        return chars;
    }

    private static int ToCodePoint(uint value, bool hyphen)
    {
        if (hyphen && value is HyphenMarker or HyphenTextMarker)
        {
            return '-';
        }

        // 0 means PDFium has no Unicode value; lone surrogates and values beyond U+10FFFF are no scalar values.
        return value is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? (int)value : 0xFFFD;
    }

    /// <summary>Bold: weight of at least 600, flag ForceBold or "Bold" in the font name. Italic: flag Italic or "Italic"/"Oblique" in the name.</summary>
    private static (bool Bold, bool Italic) ReadFontStyle(nint textPage, int index, Span<byte> buffer)
    {
        var bold = Fpdf.FPDFText_GetFontWeight(textPage, index) >= BoldWeight;
        int flags = 0;
        nuint length;
        fixed (byte* pointer = buffer)
        {
            length = Fpdf.FPDFText_GetFontInfo(textPage, index, pointer, new CULong((nuint)buffer.Length), &flags).Value;
        }

        if (length == 0)
        {
            return (bold, false);
        }

        byte[]? rented = null;
        try
        {
            if (length > (nuint)buffer.Length)
            {
                if (length > int.MaxValue)
                {
                    return (bold, false);
                }

                rented = ArrayPool<byte>.Shared.Rent((int)length);
                buffer = rented;
                fixed (byte* pointer = buffer)
                {
                    length = Fpdf.FPDFText_GetFontInfo(textPage, index, pointer, new CULong((nuint)buffer.Length), &flags).Value;
                }

                if (length == 0 || length > (nuint)buffer.Length)
                {
                    return (bold, false);
                }
            }

            // The length includes the terminating NUL.
            var name = buffer[..Math.Max(0, (int)length - 1)];
            bold |= (flags & FpdfConstants.FontFlagForceBold) != 0 || ContainsIgnoreCase(name, "bold"u8);
            var italic = (flags & FpdfConstants.FontFlagItalic) != 0
                || ContainsIgnoreCase(name, "italic"u8)
                || ContainsIgnoreCase(name, "oblique"u8);
            return (bold, italic);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Whether <paramref name="text"/> (UTF-8) contains <paramref name="lowerAscii"/>, ignoring ASCII case.</summary>
    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> lowerAscii)
    {
        for (var start = 0; start + lowerAscii.Length <= text.Length; start++)
        {
            var match = true;
            for (var i = 0; i < lowerAscii.Length; i++)
            {
                var value = text[start + i];
                if ((value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value) != lowerAscii[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static void ReadAnnotationLinks(nint document, nint page, PageTransform transform, List<PdfiumLink> links)
    {
        var pageCount = Fpdf.FPDF_GetPageCount(document);
        var position = 0;
        nint link;
        while (Fpdf.FPDFLink_Enumerate(page, &position, &link) != 0)
        {
            if (link == 0)
            {
                continue;
            }

            FsRectF rect;
            if (Fpdf.FPDFLink_GetAnnotRect(link, &rect) == 0)
            {
                continue;
            }

            var area = transform.ToPage(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (area.Right <= area.Left || area.Bottom <= area.Top)
            {
                continue;
            }

            links.Add(ReadTarget(document, pageCount, link, [area]));
        }
    }

    /// <summary>
    /// The target of a link annotation: its destination, else its action (GoTo with a destination, URI);
    /// everything else, and destinations outside the document, are <see cref="PdfiumLinkKind.Unsupported"/>.
    /// </summary>
    private static PdfiumLink ReadTarget(nint document, int pageCount, nint link, (double, double, double, double)[] rects)
    {
        var unsupported = new PdfiumLink(rects, PdfiumLinkKind.Unsupported, -1, null, null);
        var dest = Fpdf.FPDFLink_GetDest(document, link);
        if (dest == 0)
        {
            var action = Fpdf.FPDFLink_GetAction(link);
            if (action == 0)
            {
                return unsupported;
            }

            switch (Fpdf.FPDFAction_GetType(action).Value)
            {
                case FpdfConstants.ActionGoTo:
                    dest = Fpdf.FPDFAction_GetDest(document, action);
                    break;
                case FpdfConstants.ActionUri:
                    var uri = ReadUri(document, action);
                    return uri is null ? unsupported : new PdfiumLink(rects, PdfiumLinkKind.Uri, -1, null, uri);
                default:
                    return unsupported;
            }

            if (dest == 0)
            {
                return unsupported;
            }
        }

        var pageIndex = Fpdf.FPDFDest_GetDestPageIndex(document, dest);
        if (pageIndex < 0 || pageIndex >= pageCount)
        {
            return unsupported;
        }

        return new PdfiumLink(rects, PdfiumLinkKind.Page, pageIndex, ReadTargetTop(document, pageIndex, dest), null);
    }

    /// <summary>
    /// The y of an <c>/XYZ</c> destination in points from the top of the target page, clamped to the page, or
    /// <see langword="null"/> without y. The target page is not loaded: the value assumes the page is not rotated
    /// and its CropBox starts at the bottom of the MediaBox (<c>FPDF_PageToDevice</c> would need a loaded page).
    /// </summary>
    private static double? ReadTargetTop(nint document, int pageIndex, nint dest)
    {
        int hasX, hasY, hasZoom;
        float x, y, zoom;
        if (Fpdf.FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) == 0 || hasY == 0)
        {
            return null;
        }

        FsSizeF size;
        if (Fpdf.FPDF_GetPageSizeByIndexF(document, pageIndex, &size) == 0 || !float.IsFinite(y))
        {
            return null;
        }

        return Math.Clamp(size.Height - y, 0, size.Height);
    }

    /// <summary>Reads the URI of a URI action (bytes, decoded as UTF-8); <see langword="null"/> when empty.</summary>
    private static string? ReadUri(nint document, nint action)
    {
        var length = Fpdf.FPDFAction_GetURIPath(document, action, null, new CULong(0)).Value;
        if (length <= 1 || length > int.MaxValue)
        {
            return null;
        }

        var buffer = new byte[(int)length];
        nuint written;
        fixed (byte* pointer = buffer)
        {
            written = Fpdf.FPDFAction_GetURIPath(document, action, pointer, new CULong(length)).Value;
        }

        if (written != length)
        {
            return null;
        }

        // The length includes the terminating NUL. The bytes may be badly encoded; invalid UTF-8 becomes U+FFFD.
        var uri = Encoding.UTF8.GetString(buffer, 0, buffer.Length - 1);
        return uri.Length == 0 ? null : uri;
    }

    private static void ReadWebLinks(nint webLinks, PageTransform transform, List<PdfiumLink> links)
    {
        var count = Fpdf.FPDFLink_CountWebLinks(webLinks);
        for (var i = 0; i < count; i++)
        {
            var uri = ReadWebLinkUrl(webLinks, i);
            if (uri is null)
            {
                continue;
            }

            var rects = new List<(double Left, double Top, double Right, double Bottom)>();
            var rectCount = Fpdf.FPDFLink_CountRects(webLinks, i);
            for (var r = 0; r < rectCount; r++)
            {
                double left, top, right, bottom;
                if (Fpdf.FPDFLink_GetRect(webLinks, i, r, &left, &top, &right, &bottom) == 0)
                {
                    continue;
                }

                var area = transform.ToPage(left, top, right, bottom);
                if (area.Right > area.Left && area.Bottom > area.Top)
                {
                    rects.Add(area);
                }
            }

            if (rects.Count > 0)
            {
                links.Add(new PdfiumLink(rects, PdfiumLinkKind.Uri, -1, null, uri));
            }
        }
    }

    private static string? ReadWebLinkUrl(nint webLinks, int index)
    {
        // Lengths are in UTF-16 code units and include the terminator.
        var length = Fpdf.FPDFLink_GetURL(webLinks, index, null, 0);
        if (length <= 1)
        {
            return null;
        }

        var buffer = new char[length];
        int written;
        fixed (char* pointer = buffer)
        {
            written = Fpdf.FPDFLink_GetURL(webLinks, index, (ushort*)pointer, length);
        }

        var uri = new string(buffer, 0, Math.Clamp(written - 1, 0, length - 1));
        return uri.Length == 0 ? null : uri;
    }

    private static bool OverlapsAnchor((double Left, double Top, double Right, double Bottom) area, IReadOnlyList<PdfiumLink> anchors)
    {
        foreach (var link in anchors)
        {
            foreach (var anchor in link.Rects)
            {
                if (area.Left < anchor.Right && anchor.Left < area.Right && area.Top < anchor.Bottom && anchor.Top < area.Bottom)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Converts PDF user space (origin bottom left, before rotation) to points from the top left of the displayed page.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly struct PageTransform
    {
        private readonly nint page;
        private readonly int width;
        private readonly int height;

        public PageTransform(nint page, double pageWidth, double pageHeight)
        {
            this.page = page;
            width = (int)Math.Round(pageWidth * DeviceScale);
            height = (int)Math.Round(pageHeight * DeviceScale);
        }

        /// <summary>Converts two opposite corners and joins them into an axis-aligned box; zeros if the conversion fails.</summary>
        public (double Left, double Top, double Right, double Bottom) ToPage(double x1, double y1, double x2, double y2)
        {
            if (width <= 0 || height <= 0)
            {
                return default;
            }

            int ax, ay, bx, by;
            if (Fpdf.FPDF_PageToDevice(page, 0, 0, width, height, 0, x1, y1, &ax, &ay) == 0
                || Fpdf.FPDF_PageToDevice(page, 0, 0, width, height, 0, x2, y2, &bx, &by) == 0)
            {
                return default;
            }

            return (
                Math.Min(ax, bx) / (double)DeviceScale,
                Math.Min(ay, by) / (double)DeviceScale,
                Math.Max(ax, bx) / (double)DeviceScale,
                Math.Max(ay, by) / (double)DeviceScale);
        }
    }
}
