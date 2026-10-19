using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// Renders a whole page or one tile of it progressively, one <c>FPDF_RenderPageBitmap_Start</c> or
/// <c>FPDF_RenderPage_Continue</c> call per <see cref="Step"/>. PDFium yields once the time slice is used up
/// (it checks the pause callback after shadings, form XObjects, images and every 100 objects), so other
/// work can run on the <see cref="PdfiumThread"/> between steps. Every member runs on the PDFium thread.
/// </summary>
/// <remarks>
/// Each work loads its own page: PDFium keeps the progressive render state in the page object, so two renders
/// of the same page object would overwrite each other. A tile is rendered like Chrome does it: the page is drawn
/// at its full pixel size, shifted by the tile offset, into a bitmap of the tile size, widened by
/// <see cref="TileOverlap"/> pixels to the left that are dropped afterwards (see there why).
/// The work holds a reference on the page and bitmap handles until <see cref="Dispose"/> has ended the render
/// and destroyed the bitmap, and keeps the pinned pixel array alive until then.
/// With <see cref="PdfiumPageColors"/> the page is recoloured through PDFium's colour scheme
/// (<c>FPDF_RenderPageBitmapWithColorScheme_Start</c> with <c>FPDF_CONVERT_FILL_TO_STROKE</c>) on a bitmap filled with
/// the background; without them the page is rendered on white exactly as before (<c>FPDF_RenderPageBitmap_Start</c>).
/// </remarks>
internal sealed unsafe class PdfiumRenderWork : SteppedWork<PdfiumImage>
{
    /// <summary>The largest bitmap in pixels: 256 Mpx, that is 1 GiB at 4 bytes per pixel.</summary>
    public const long MaxPixels = 268_435_456;

    /// <summary>
    /// Extra pixels rendered left of a tile and then dropped. PDFium (<c>CFX_RenderDevice::DrawNormalText</c>,
    /// chromium/8100) computes the subpixel phase of a glyph as <c>static_cast&lt;int&gt;(origin_x * 3) % 3</c>,
    /// which is wrong for a glyph whose origin lies left of the bitmap (negative x): the visible part of a glyph
    /// cut by a tile's left edge would differ from the whole-page render by up to about 135 per channel. PDFium
    /// draws glyph bitmaps only up to an em of 50 device pixels (larger text becomes paths), so 64 pixels move
    /// the origin of practically every glyph that reaches into the tile inside the bitmap.
    /// </summary>
    public const int TileOverlap = 64;

    private const int BytesPerPixel = 4;

    private readonly PdfiumThread thread;
    private readonly int pageIndex;
    private readonly PdfiumPage page;
    private readonly int pageWidth;
    private readonly int pageHeight;
    private readonly int width;
    private readonly int height;
    private readonly int stride;
    private readonly int overlap;
    private readonly int startX;
    private readonly int startY;
    private readonly int flags;
    private readonly bool recoloured;
    private readonly long sliceTicks;
    private BitmapHandle? bitmap;
    private byte[]? pixels;
    private PauseState* pause;
    private bool pageReferenced;
    private bool bitmapReferenced;
    private bool started;
    private bool finished;
    private bool disposed;

    /// <summary>
    /// Loads the page and prepares the bitmap. Must be called on the document's thread.
    /// </summary>
    /// <param name="document">The open document.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="scale">Pixels per point (1 = 72 dpi); finite and positive.</param>
    /// <param name="tile">The part of the page to render in page pixels, or <see langword="null"/> for the whole page.</param>
    /// <param name="annotations">Whether annotations are drawn (<c>FPDF_ANNOT</c>).</param>
    /// <param name="slice">How long one step may take before PDFium yields; <see cref="TimeSpan.Zero"/> yields at every chance, <see cref="TimeSpan.MaxValue"/> never.</param>
    /// <param name="colors">The colours of a recoloured (dark) page, or <see langword="null"/> for the page as it is, on white.</param>
    /// <exception cref="ArgumentOutOfRangeException">Invalid page index, scale, slice, or a tile that is empty or not within the page.</exception>
    /// <exception cref="PdfiumRenderException">The bitmap would exceed <see cref="MaxPixels"/>, or PDFium could not create it.</exception>
    /// <exception cref="InvalidDataException">The page could not be loaded.</exception>
    public PdfiumRenderWork(PdfiumDocument document, int pageIndex, double scale, PdfiumPixelRect? tile, bool annotations, TimeSpan slice, PdfiumPageColors? colors = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Thread.VerifyAccess();
        ValidateScale(scale);
        ArgumentOutOfRangeException.ThrowIfLessThan(slice, TimeSpan.Zero);
        thread = document.Thread;
        this.pageIndex = pageIndex;
        sliceTicks = ToStopwatchTicks(slice);
        flags = annotations ? FpdfConstants.RenderAnnot : 0;
        recoloured = colors is not null;
        if (recoloured)
        {
            // With a single fill colour for all paths, adjacent fills would merge; their outlines keep them apart.
            flags |= FpdfConstants.RenderConvertFillToStroke;
        }

        page = document.LoadPage(pageIndex);
        try
        {
            page.Handle.DangerousAddRef(ref pageReferenced);
            (pageWidth, pageHeight) = PagePixelSize(page.Width, page.Height, scale);
            var area = tile ?? new PdfiumPixelRect(0, 0, pageWidth, pageHeight);
            if (tile is not null)
            {
                ValidateTile(area, pageWidth, pageHeight);
            }

            // Checked before anything is allocated.
            if ((long)area.Width * area.Height > MaxPixels)
            {
                throw new PdfiumRenderException(
                    $"The bitmap of {area.Width} x {area.Height} pixels is larger than {MaxPixels} pixels.");
            }

            width = area.Width;
            height = area.Height;
            stride = width * BytesPerPixel;

            // The overlap never leaves the page and never pushes the bitmap over the size limit.
            overlap = (int)Math.Min(Math.Min(TileOverlap, area.X), (MaxPixels / height) - width);
            var bitmapWidth = width + overlap;
            var bitmapStride = bitmapWidth * BytesPerPixel;
            startX = -(area.X - overlap);
            startY = -area.Y;

            // White (or the background of a recoloured page), including the unused fourth byte, so the result reads
            // as opaque BGRA.
            pixels = GC.AllocateUninitializedArray<byte>(bitmapStride * height, pinned: true);
            if (colors is { } ground)
            {
                // Blue, green, red, opaque: the byte order of the bitmap.
                var argb = ground.Background;
                ReadOnlySpan<byte> pixel = [(byte)argb, (byte)(argb >> 8), (byte)(argb >> 16), 0xFF];
                MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()).Fill(MemoryMarshal.Read<uint>(pixel));
            }
            else
            {
                pixels.AsSpan().Fill(0xFF);
            }

            bitmap = new BitmapHandle(thread);
            var buffer = Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(pixels));
            var value = Fpdf.FPDFBitmap_CreateEx(bitmapWidth, height, FpdfConstants.BitmapBgrx, buffer, bitmapStride);
            if (value == 0)
            {
                throw new PdfiumRenderException($"PDFium could not create a bitmap of {bitmapWidth} x {height} pixels.");
            }

            bitmap.Attach(value);
            bitmap.DangerousAddRef(ref bitmapReferenced);

            // PDFium rejects a null pause (FPDF_RENDER_FAILED), so there is always one.
            pause = (PauseState*)NativeMemory.Alloc((nuint)sizeof(PauseState));
            pause->Pause.Version = 1;
            pause->Pause.NeedToPauseNow = &NeedToPauseNow;
            pause->Pause.User = null;
            pause->Deadline = long.MaxValue;
            pause->Scheme = default;
            if (colors is { } scheme)
            {
                // FPDF_COLORSCHEME: fills take the background, strokes and text the foreground.
                pause->Scheme.PathFillColor = new CULong(scheme.Background);
                pause->Scheme.PathStrokeColor = new CULong(scheme.Foreground);
                pause->Scheme.TextFillColor = new CULong(scheme.Foreground);
                pause->Scheme.TextStrokeColor = new CULong(scheme.Foreground);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Gets the number of <see cref="Step"/> calls so far.</summary>
    public int StepCount { get; private set; }

    /// <summary>
    /// Computes the size of a page in pixels: each side rounded up, at least 1. The same rule defines the tile
    /// grid, so callers can compute tiles without rendering.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not finite and positive.</exception>
    /// <exception cref="PdfiumRenderException">A side does not fit in an <see cref="int"/>.</exception>
    public static (int Width, int Height) PagePixelSize(double widthPt, double heightPt, double scale)
    {
        ValidateScale(scale);
        return (ToPixels(widthPt * scale), ToPixels(heightPt * scale));
    }

    /// <summary>Runs one <c>FPDF_RenderPageBitmap_Start</c> or <c>FPDF_RenderPage_Continue</c>.</summary>
    /// <returns><see langword="true"/> when the page is completely rendered.</returns>
    /// <exception cref="PdfiumRenderException">PDFium reported <c>FPDF_RENDER_FAILED</c>.</exception>
    public override bool Step()
    {
        thread.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (finished)
        {
            return true;
        }

        StepCount++;
        pause->Deadline = sliceTicks == long.MaxValue ? long.MaxValue : Stopwatch.GetTimestamp() + sliceTicks;
        var pageValue = page.Handle.DangerousGetHandle();
        int status;
        if (!started)
        {
            started = true;
            status = recoloured
                ? Fpdf.FPDF_RenderPageBitmapWithColorScheme_Start(
                    bitmap!.DangerousGetHandle(),
                    pageValue,
                    startX,
                    startY,
                    pageWidth,
                    pageHeight,
                    rotate: 0,
                    flags,
                    &pause->Scheme,
                    &pause->Pause)
                : Fpdf.FPDF_RenderPageBitmap_Start(
                    bitmap!.DangerousGetHandle(),
                    pageValue,
                    startX,
                    startY,
                    pageWidth,
                    pageHeight,
                    rotate: 0,
                    flags,
                    &pause->Pause);
        }
        else
        {
            status = Fpdf.FPDF_RenderPage_Continue(pageValue, &pause->Pause);
        }

        switch (status)
        {
            case FpdfConstants.RenderReady:
            case FpdfConstants.RenderToBeContinued:
                return false;
            case FpdfConstants.RenderDone:
                finished = true;
                DropOverlap();
                return true;
            default:
                throw new PdfiumRenderException($"PDFium could not render page {pageIndex + 1} (status {status}).");
        }
    }

    /// <summary>Returns the image. The pixel array is handed over without a copy.</summary>
    public override PdfiumImage Complete()
    {
        if (!finished || pixels is null)
        {
            throw new InvalidOperationException("The render has not finished.");
        }

        return new PdfiumImage(width, height, stride, pixels.AsMemory(0, stride * height));
    }

    /// <summary>
    /// Ends the render (<c>FPDF_RenderPage_Close</c>), destroys the bitmap and closes the page. On another thread
    /// this is queued to the PDFium thread.
    /// </summary>
    public override void Dispose()
    {
        if (!disposed)
        {
            if (thread.IsCurrent)
            {
                Release();
            }
            else
            {
                thread.Post(Dispose);
            }
        }

        base.Dispose();
    }

    private void Release()
    {
        disposed = true;
        try
        {
            // Frees the render context PDFium keeps in the page; it references the bitmap. Harmless when the
            // render never started or already finished.
            Fpdf.FPDF_RenderPage_Close(page.Handle.DangerousGetHandle());
        }
        finally
        {
            if (bitmap is not null)
            {
                if (bitmapReferenced)
                {
                    bitmap.DangerousRelease();
                }

                // On the PDFium thread this runs FPDFBitmap_Destroy right away.
                bitmap.Dispose();
                bitmap = null;
            }

            if (pause != null)
            {
                NativeMemory.Free(pause);
                pause = null;
            }

            if (pageReferenced)
            {
                page.Handle.DangerousRelease();
            }

            page.Dispose();

            // PDFium wrote into the pinned array until the bitmap was destroyed.
            GC.KeepAlive(pixels);
            pixels = null;
        }
    }

    /// <summary>Moves the rows together in place so the overlap columns are gone and the stride is the tile's.</summary>
    private void DropOverlap()
    {
        if (overlap == 0)
        {
            return;
        }

        var bitmapStride = (width + overlap) * BytesPerPixel;
        var span = pixels.AsSpan();
        for (var row = 0; row < height; row++)
        {
            // Overlapping ranges are fine: the destination never lies after the source.
            span.Slice((row * bitmapStride) + (overlap * BytesPerPixel), stride).CopyTo(span.Slice(row * stride));
        }
    }

    private static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be finite and greater than 0.");
        }
    }

    private static void ValidateTile(PdfiumPixelRect tile, int pageWidth, int pageHeight)
    {
        if (tile.Width <= 0 || tile.Height <= 0 || tile.X < 0 || tile.Y < 0
            || (long)tile.X + tile.Width > pageWidth || (long)tile.Y + tile.Height > pageHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tile),
                tile,
                $"The tile must not be empty and must lie within the page ({pageWidth} x {pageHeight} pixels).");
        }
    }

    private static int ToPixels(double size)
    {
        var rounded = Math.Ceiling(size);
        if (!(rounded <= int.MaxValue))
        {
            throw new PdfiumRenderException(string.Create(CultureInfo.InvariantCulture, $"The page is too large at this scale ({size} pixels)."));
        }

        return Math.Max(1, (int)rounded);
    }

    /// <summary>Converts the slice to <see cref="Stopwatch"/> ticks; <see cref="long.MaxValue"/> means never pause.</summary>
    private static long ToStopwatchTicks(TimeSpan slice)
    {
        var ticks = slice.Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        return ticks >= long.MaxValue / 2 ? long.MaxValue : (long)ticks;
    }

    /// <summary><c>IFSDK_PAUSE.NeedToPauseNow</c>: pause once the deadline of the current step has passed.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NeedToPauseNow(IfsdkPause* pause) =>
        Stopwatch.GetTimestamp() >= ((PauseState*)pause)->Deadline ? 1 : 0;

    /// <summary>
    /// The pause interface followed by the deadline the callback reads (cast from its pointer), and the colour scheme
    /// of a recoloured page, so it lives in native memory as long as the render.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PauseState
    {
        public IfsdkPause Pause;
        public long Deadline;
        public FpdfColorScheme Scheme;
    }
}
