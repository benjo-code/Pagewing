using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// Prints pages on a device context (Windows <c>HDC</c>). <c>FPDF_RenderPage</c> on a device context draws no widget, so a
/// page with widgets in a document with a form environment is printed in strips instead: each strip is rendered onto a bitmap
/// (<c>FPDF_RenderPageBitmap</c>, then <c>FPDF_FFLDraw</c> without the highlight, both with <c>FPDF_ANNOT | FPDF_PRINTING</c>
/// and the strip's offset) and copied 1:1 onto the device context with <c>StretchDIBits</c>. Every other page is printed as
/// vectors with <c>FPDF_RenderPage</c>, as before (Docs/DECISIONS.md, "Formulare: Druck unter Windows in Streifen (M11)").
/// Like <c>FPDF_RenderPage</c>, the strips are drawn on the device context from the PDFium thread. Every member runs there.
/// </summary>
internal static class PdfiumPrint
{
    /// <summary>The rows of one strip (device pixels), unless <see cref="MaxStripBytes"/> asks for fewer.</summary>
    public const int StripRows = 512;

    /// <summary>The most memory one strip may take; a very wide target gets fewer rows per strip (at least one).</summary>
    public const long MaxStripBytes = 16L * 1024 * 1024;

    /// <summary>
    /// How long one request prints strips before it returns, so the rendering of the tabs goes on in between (a Letter page at
    /// 600 dpi took 40 to 48 ms in strips on a memory device context); a request prints at least one strip.
    /// </summary>
    public static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(25);

    /// <summary>The flags of the print: annotations, as for a printer.</summary>
    internal const int Flags = FpdfConstants.RenderAnnot | FpdfConstants.RenderPrinting;

    /// <summary>
    /// Prints page <paramref name="pageIndex"/>, loaded as <paramref name="page"/>: a page without widgets completely, as
    /// vectors; a page with widgets in strips, at least one and as many as fit into <paramref name="slice"/>.
    /// </summary>
    /// <returns><see langword="null"/> when the page is printed; else the strips still to print (<see cref="PdfiumPrintStrips.Print"/>, each with a freshly loaded page).</returns>
    /// <exception cref="PdfiumRenderException">PDFium could not draw the page, or the device context did not take a strip.</exception>
    [SupportedOSPlatform("windows")]
    public static PdfiumPrintStrips? Begin(
        PdfiumDocument document,
        int pageIndex,
        PdfiumPage page,
        nint deviceContext,
        int x,
        int y,
        int width,
        int height,
        int quarterTurns,
        int stripRows,
        TimeSpan slice)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripRows);
        if (document.FormEnvironment is null || !document.PageHasWidgets(pageIndex, page))
        {
            page.RenderToDevice(deviceContext, x, y, width, height, quarterTurns);
            document.CountVectorPrint();
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfZero(deviceContext);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(quarterTurns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quarterTurns, 3);

        // Only the part of the page the device can draw is rendered (a page printed at its actual size may be far larger
        // than the paper).
        var (left, top, right, bottom) = ((long)x, (long)y, (long)x + width, (long)y + height);
        if (TryGetClipBox(deviceContext, out var clip))
        {
            (left, top, right, bottom) = (Math.Max(left, clip.Left), Math.Max(top, clip.Top), Math.Min(right, clip.Right), Math.Min(bottom, clip.Bottom));
        }

        if (left >= right || top >= bottom)
        {
            return null;
        }

        var rowBytes = (right - left) * 4;
        var rows = (int)Math.Max(1, Math.Min(stripRows, MaxStripBytes / rowBytes));
        var strips = new PdfiumPrintStrips(pageIndex, deviceContext, x, y, width, height, quarterTurns, (int)left, (int)top, (int)right, (int)bottom, rows);
        return strips.Print(document, page, slice) ? null : strips;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe bool TryGetClipBox(nint deviceContext, out GdiRect clip)
    {
        GdiRect rect;
        var kind = Gdi.GetClipBox(deviceContext, &rect);
        clip = rect;
        if (kind == Gdi.NullRegion)
        {
            clip = default;
            return true;
        }

        return kind != Gdi.RegionError;
    }
}

/// <summary>
/// The strips of one printed page with widgets that are still to print (<see cref="PdfiumPrint.Begin"/>). PDFium thread only;
/// each request loads the page again, as every render does.
/// </summary>
internal sealed class PdfiumPrintStrips
{
    private readonly nint deviceContext;
    private readonly int x;
    private readonly int y;
    private readonly int width;
    private readonly int height;
    private readonly int quarterTurns;
    private readonly int left;
    private readonly int right;
    private readonly int bottom;
    private readonly int rows;
    private int next;

    internal PdfiumPrintStrips(
        int pageIndex,
        nint deviceContext,
        int x,
        int y,
        int width,
        int height,
        int quarterTurns,
        int left,
        int top,
        int right,
        int bottom,
        int rows)
    {
        PageIndex = pageIndex;
        this.deviceContext = deviceContext;
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
        this.quarterTurns = quarterTurns;
        this.left = left;
        this.right = right;
        this.bottom = bottom;
        this.rows = rows;
        next = top;
    }

    /// <summary>Gets the page index.</summary>
    public int PageIndex { get; }

    /// <summary>Prints the next strips onto the device context, at least one, until <paramref name="slice"/> is used up.</summary>
    /// <returns><see langword="true"/> when the page is completely printed.</returns>
    /// <exception cref="PdfiumRenderException">PDFium could not create a bitmap, or the device context did not take a strip.</exception>
    [SupportedOSPlatform("windows")]
    public unsafe bool Print(PdfiumDocument document, PdfiumPage page, TimeSpan slice)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        var environment = document.FormEnvironment;
        var native = page.Access();
        var stripWidth = right - left;
        var stride = stripWidth * 4;
        var deadline = Stopwatch.GetTimestamp() + (long)(slice.TotalSeconds * Stopwatch.Frequency);
        var buffer = NativeMemory.Alloc((nuint)((long)stride * rows));
        try
        {
            do
            {
                var stripHeight = Math.Min(rows, bottom - next);
                var pixels = new Span<byte>(buffer, stride * stripHeight);
                pixels.Fill(0xFF);
                var bitmap = Fpdf.FPDFBitmap_CreateEx(stripWidth, stripHeight, FpdfConstants.BitmapBgrx, buffer, stride);
                if (bitmap == 0)
                {
                    throw new PdfiumRenderException($"PDFium could not create a bitmap of {stripWidth} x {stripHeight} pixels.");
                }

                try
                {
                    // The page's area relative to the strip, the same mapping for the page and its fields.
                    var startX = x - left;
                    var startY = y - next;
                    Fpdf.FPDF_RenderPageBitmap(bitmap, native, startX, startY, width, height, quarterTurns, PdfiumPrint.Flags);
                    if (environment is not null)
                    {
                        // FPDF_FFLDraw draws the highlight also with FPDF_PRINTING: removed here, set again by every render.
                        PdfiumFormDrawing.Draw(environment, bitmap, page, startX, startY, width, height, quarterTurns, PdfiumPrint.Flags, highlight: null);
                    }
                }
                finally
                {
                    Fpdf.FPDFBitmap_Destroy(bitmap);
                }

                var header = new BitmapInfoHeader
                {
                    Size = sizeof(BitmapInfoHeader),
                    Width = stripWidth,
                    Height = -stripHeight,
                    Planes = 1,
                    BitCount = 32,
                    Compression = Gdi.BiRgb,
                };
                var copied = Gdi.StretchDIBits(
                    deviceContext,
                    left,
                    next,
                    stripWidth,
                    stripHeight,
                    0,
                    0,
                    stripWidth,
                    stripHeight,
                    buffer,
                    &header,
                    Gdi.DibRgbColors,
                    Gdi.SourceCopy);
                if (copied <= 0)
                {
                    throw new PdfiumRenderException($"The device context did not take the strip at row {next} of page {PageIndex + 1}.");
                }

                document.CountPrintStrip();
                next += stripHeight;
            }
            while (next < bottom && Stopwatch.GetTimestamp() < deadline);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }

        return next >= bottom;
    }
}
