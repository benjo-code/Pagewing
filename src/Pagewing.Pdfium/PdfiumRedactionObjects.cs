using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>An affine map of the PDF plane: x' = a x + c y + e, y' = b x + d y + f (the matrix [a b c d e f] of PDF, row vectors).</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity.</summary>
    public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>The map of a PDFium matrix.</summary>
    public static Affine From(FsMatrix matrix) => new(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);

    /// <summary>Whether all six numbers are finite and the map can be undone.</summary>
    public bool IsUsable => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F)
        && Math.Abs((A * D) - (B * C)) > 1e-12;

    /// <summary>Whether the map only scales (also mirrors) and shifts: no rotation and no shear, to within rounding of the stored numbers.</summary>
    public bool IsAxisParallel => IsUsable && Math.Abs(B) <= 1e-6 * (Math.Abs(A) + Math.Abs(D)) && Math.Abs(C) <= 1e-6 * (Math.Abs(A) + Math.Abs(D));

    /// <summary>The map as a PDFium matrix.</summary>
    public FsMatrix ToFs() => new() { A = (float)A, B = (float)B, C = (float)C, D = (float)D, E = (float)E, F = (float)F };

    /// <summary>This map first, then <paramref name="next"/> (the PDF product <c>this × next</c>).</summary>
    public Affine Then(Affine next) => new(
        (A * next.A) + (B * next.C),
        (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C),
        (C * next.B) + (D * next.D),
        (E * next.A) + (F * next.C) + next.E,
        (E * next.B) + (F * next.D) + next.F);

    /// <summary>The image of a point.</summary>
    public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>The smallest axis-parallel rectangle that holds the image of the four corners of <paramref name="rect"/>.</summary>
    public UserRect Bounds(UserRect rect)
    {
        var (x1, y1) = Apply(rect.Left, rect.Bottom);
        var (x2, y2) = Apply(rect.Right, rect.Bottom);
        var (x3, y3) = Apply(rect.Left, rect.Top);
        var (x4, y4) = Apply(rect.Right, rect.Top);
        return new UserRect(
            Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)),
            Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)),
            Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)),
            Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)));
    }
}

/// <summary>A filled rectangle path as the redaction recognises it: a rectangle in page space and how it is filled.</summary>
internal sealed class PathRectangle
{
    /// <summary>The rectangle in page space (the object matrix is applied; the matrix has no rotation or shear).</summary>
    public UserRect Rect { get; init; }

    /// <summary>The fill rule (<c>FPDF_FILLMODE_*</c>).</summary>
    public int FillMode { get; init; }

    /// <summary>The fill colour (red, green, blue, alpha).</summary>
    public uint[] Fill { get; init; } = [0, 0, 0, 255];
}

/// <summary>A range of pixels of an image, from the top left: columns <see cref="X0"/> up to (not including) <see cref="X1"/>, the same for rows.</summary>
internal readonly record struct PixelBox(int X0, int Y0, int X1, int Y1)
{
    /// <summary>The number of columns.</summary>
    public int Width => X1 - X0;

    /// <summary>The number of rows.</summary>
    public int Height => Y1 - Y0;
}

/// <summary>
/// The native steps of redacting images and paths (and the arithmetic around them) that need pointers or care about rounding. Every
/// method that calls PDFium must be called on the PDFium thread.
/// </summary>
internal static unsafe class PdfiumRedactionObjects
{
    /// <summary>The slack, in pixels, against rounding noise when a rectangle edge lies on a pixel edge.</summary>
    private const double PixelSlack = 1e-7;

    /// <summary>The smallest side, in points, of a piece of a cropped path that is still drawn.</summary>
    private const double MinPiece = 1e-6;

    /// <summary>The tolerance, in points, when the corners of a path are compared to see whether it is a rectangle.</summary>
    private const double CornerTolerance = 1e-3;

    /// <summary>The most paths a clipping path may have for <see cref="IsClipped"/> to read them all; more counts as clipped (solid fill).</summary>
    internal const int MaxClipPaths = 64;

    /// <summary>The matrix of a page object.</summary>
    public static bool TryGetMatrix(nint pageObject, out Affine matrix)
    {
        FsMatrix value = default;
        var ok = Fpdf.FPDFPageObj_GetMatrix(pageObject, &value) != 0;
        matrix = Affine.From(value);
        return ok && matrix.IsUsable;
    }

    /// <summary>Sets the matrix of a page object.</summary>
    public static bool TrySetMatrix(nint pageObject, Affine matrix)
    {
        var value = matrix.ToFs();
        return Fpdf.FPDFPageObj_SetMatrix(pageObject, &value) != 0;
    }

    /// <summary>
    /// Whether the clipping path of a page object cuts what it shows: some clip is not an upright rectangle (or cannot be read), or a clip
    /// rectangle does not hold the bounds of the object. PDFium gives every object the clip of the page (or form) box, which does not cut a
    /// picture inside the page and so does not count. Pixelating or blurring does not apply to a cut image, because what it shows is not its
    /// whole pixel rectangle. The bounds and the clip are in the same space (the page for an object of the page, the form for its child).
    /// </summary>
    public static bool IsClipped(nint pageObject)
    {
        if (!PdfiumRedactionNative.TryGetBounds(pageObject, out var bounds))
        {
            return true;
        }

        // A null handle is the documented failure of FPDFPageObj_GetClipPath (a valid object always has one): fail closed.
        var clip = Fpdf.FPDFPageObj_GetClipPath(pageObject);
        if (clip == 0)
        {
            return true;
        }

        // The header says -1 is a failure, but PDFium answers -1 for an object whose clipping path has no content at all (found by treating
        // every -1 as "clipped": the unclipped images of the fixtures then all fell back to solid). It cannot be told apart from a failure,
        // so it counts as no clip; the guard NativeBindingsTests.ClipPathApiAnswersAsTheTreatmentAssumes pins the behaviour.
        var paths = Fpdf.FPDFClipPath_CountPaths(clip);
        if (paths <= 0)
        {
            return false;
        }

        // A hostile page can pile up clips without end; every path costs native calls for each box of the image. Too many counts as cut (solid fill).
        if (paths > MaxClipPaths)
        {
            return true;
        }

        for (var p = 0; p < paths; p++)
        {
            if (!TryReadClipRectangle(clip, p, out var rect)
                || rect.Left > bounds.Left + CornerTolerance || rect.Bottom > bounds.Bottom + CornerTolerance
                || rect.Right < bounds.Right - CornerTolerance || rect.Top < bounds.Top - CornerTolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One path of a clipping path as an upright rectangle (four corners, or five with the first repeated, made of straight lines), or <see langword="false"/>.</summary>
    private static bool TryReadClipRectangle(nint clip, int pathIndex, out UserRect rect)
    {
        rect = default;
        var count = Fpdf.FPDFClipPath_CountPathSegments(clip, pathIndex);
        if (count is not (4 or 5))
        {
            return false;
        }

        var x = new double[count];
        var y = new double[count];
        for (var i = 0; i < count; i++)
        {
            var segment = Fpdf.FPDFClipPath_GetPathSegment(clip, pathIndex, i);
            if (segment == 0 || Fpdf.FPDFPathSegment_GetType(segment) != (i == 0 ? FpdfConstants.SegmentMoveTo : FpdfConstants.SegmentLineTo))
            {
                return false;
            }

            float px = 0;
            float py = 0;
            if (Fpdf.FPDFPathSegment_GetPoint(segment, &px, &py) == 0 || !float.IsFinite(px) || !float.IsFinite(py))
            {
                return false;
            }

            x[i] = px;
            y[i] = py;
        }

        if (count == 5 && (!Same(x[4], x[0]) || !Same(y[4], y[0])))
        {
            return false;
        }

        var horizontalFirst = Same(y[0], y[1]) && Same(x[1], x[2]) && Same(y[2], y[3]) && Same(x[3], x[0]);
        var verticalFirst = Same(x[0], x[1]) && Same(y[1], y[2]) && Same(x[2], x[3]) && Same(y[3], y[0]);
        if (!horizontalFirst && !verticalFirst)
        {
            return false;
        }

        rect = new UserRect(
            Math.Min(Math.Min(x[0], x[1]), Math.Min(x[2], x[3])),
            Math.Min(Math.Min(y[0], y[1]), Math.Min(y[2], y[3])),
            Math.Max(Math.Max(x[0], x[1]), Math.Max(x[2], x[3])),
            Math.Max(Math.Max(y[0], y[1]), Math.Max(y[2], y[3])));
        return true;
    }

    /// <summary>The size of an image in pixels, from its dictionary (nothing is decoded).</summary>
    public static bool TryGetPixelSize(nint image, out long width, out long height)
    {
        uint w = 0;
        uint h = 0;
        var ok = Fpdf.FPDFImageObj_GetImagePixelSize(image, &w, &h) != 0;
        width = w;
        height = h;
        return ok && w > 0 && h > 0;
    }

    /// <summary>
    /// The pixels of an image (blue, green, red, alpha per pixel, top row first, mask and alpha applied) the way the signature code reads
    /// them: the matrix is set to the pixel size, the object is rendered and the matrix is put back. Never <c>FPDFImageObj_GetBitmap</c>.
    /// </summary>
    public static bool TryReadPixels(nint nativeDocument, nint page, nint image, int width, int height, out byte[] bgra)
    {
        bgra = [];
        FsMatrix original = default;
        if (Fpdf.FPDFPageObj_GetMatrix(image, &original) == 0)
        {
            return false;
        }

        var unit = new FsMatrix { A = width, B = 0, C = 0, D = height, E = 0, F = 0 };
        if (Fpdf.FPDFPageObj_SetMatrix(image, &unit) == 0)
        {
            return false;
        }

        try
        {
            var bitmap = Fpdf.FPDFImageObj_GetRenderedBitmap(nativeDocument, page, image);
            if (bitmap == 0)
            {
                return false;
            }

            try
            {
                var w = Fpdf.FPDFBitmap_GetWidth(bitmap);
                var h = Fpdf.FPDFBitmap_GetHeight(bitmap);
                var stride = Fpdf.FPDFBitmap_GetStride(bitmap);
                var format = Fpdf.FPDFBitmap_GetFormat(bitmap);
                var source = (byte*)Fpdf.FPDFBitmap_GetBuffer(bitmap);
                if (w != width || h != height || source is null || (format != FpdfConstants.BitmapBgra && format != FpdfConstants.BitmapBgrx) || stride < w * 4)
                {
                    return false;
                }

                var result = new byte[(long)width * height * 4];
                for (var y = 0; y < height; y++)
                {
                    var row = source + ((long)y * stride);
                    for (var x = 0; x < width; x++)
                    {
                        var at = (((long)y * width) + x) * 4;
                        result[at] = row[x * 4];
                        result[at + 1] = row[(x * 4) + 1];
                        result[at + 2] = row[(x * 4) + 2];
                        result[at + 3] = format == FpdfConstants.BitmapBgra ? row[(x * 4) + 3] : (byte)255;
                    }
                }

                bgra = result;
                return true;
            }
            finally
            {
                Fpdf.FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            _ = Fpdf.FPDFPageObj_SetMatrix(image, &original);
        }
    }

    /// <summary>Gives an image object its pixels (<c>FPDFImageObj_SetBitmap</c> encodes them as Flate RGB, with an <c>/SMask</c> for the alpha).</summary>
    public static bool TryWritePixels(nint image, int width, int height, byte[] bgra)
    {
        fixed (byte* pixels = bgra)
        {
            var bitmap = Fpdf.FPDFBitmap_CreateEx(width, height, FpdfConstants.BitmapBgra, pixels, width * 4);
            if (bitmap == 0)
            {
                return false;
            }

            try
            {
                return Fpdf.FPDFImageObj_SetBitmap(null, 0, image, bitmap) != 0;
            }
            finally
            {
                Fpdf.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    /// <summary>
    /// A new image object that shows the pixels with the matrix of <paramref name="matrix"/>, or 0 (nothing is left allocated then). It
    /// replaces an image that PDFium cannot write when it writes the page again (an inline image).
    /// </summary>
    public static nint CreateImage(nint nativeDocument, int width, int height, byte[] bgra, Affine matrix)
    {
        var image = Fpdf.FPDFPageObj_NewImageObj(nativeDocument);
        if (image == 0)
        {
            return 0;
        }

        if (!TryWritePixels(image, width, height, bgra) || !TrySetMatrix(image, matrix))
        {
            Fpdf.FPDFPageObj_Destroy(image);
            return 0;
        }

        return image;
    }

    /// <summary>
    /// The pixels of an image of the given matrix and size that lie under a rectangle in page space (any overlap of positive area counts;
    /// an edge that lies on a pixel edge does not take the pixel next to it). <see langword="false"/> when none do or the matrix turns the image.
    /// </summary>
    public static bool TryPixelBox(Affine matrix, int width, int height, UserRect area, out PixelBox box)
    {
        box = default;
        if (!matrix.IsAxisParallel)
        {
            return false;
        }

        var u0 = (area.Left - matrix.E) / matrix.A;
        var u1 = (area.Right - matrix.E) / matrix.A;
        var v0 = (area.Bottom - matrix.F) / matrix.D;
        var v1 = (area.Top - matrix.F) / matrix.D;
        var uLow = Math.Min(u0, u1) * width;
        var uHigh = Math.Max(u0, u1) * width;

        // The first row of the image is the top: v = 1.
        var rowLow = (1 - Math.Max(v0, v1)) * height;
        var rowHigh = (1 - Math.Min(v0, v1)) * height;
        if (!(uHigh > PixelSlack) || !(uLow < width - PixelSlack) || !(rowHigh > PixelSlack) || !(rowLow < height - PixelSlack))
        {
            return false;
        }

        var x0 = (int)Math.Clamp(Math.Floor(uLow + PixelSlack), 0, width - 1);
        var x1 = (int)Math.Clamp(Math.Ceiling(uHigh - PixelSlack), 1, width);
        var y0 = (int)Math.Clamp(Math.Floor(rowLow + PixelSlack), 0, height - 1);
        var y1 = (int)Math.Clamp(Math.Ceiling(rowHigh - PixelSlack), 1, height);
        box = new PixelBox(x0, y0, Math.Max(x1, x0 + 1), Math.Max(y1, y0 + 1));
        return true;
    }

    /// <summary>
    /// Reads a path object as a filled rectangle: a closed rectangle of four corners made of straight lines only (the segment types are
    /// checked: the control points of a Bézier curve can lie on the corners of a rectangle), filled and not stroked, with a matrix that does
    /// not turn it. <see langword="null"/> for every other path.
    /// </summary>
    public static PathRectangle? ReadFilledRectangle(nint path, Affine accum)
    {
        var count = Fpdf.FPDFPath_CountSegments(path);
        if (count is not (4 or 5))
        {
            return null;
        }

        int fillMode = 0;
        int stroke = 0;
        if (Fpdf.FPDFPath_GetDrawMode(path, &fillMode, &stroke) == 0 || stroke != 0 || fillMode == FpdfConstants.FillModeNone)
        {
            return null;
        }

        var x = new double[count];
        var y = new double[count];
        for (var i = 0; i < count; i++)
        {
            var segment = Fpdf.FPDFPath_GetPathSegment(path, i);
            if (segment == 0)
            {
                return null;
            }

            var type = Fpdf.FPDFPathSegment_GetType(segment);
            if (type != (i == 0 ? FpdfConstants.SegmentMoveTo : FpdfConstants.SegmentLineTo))
            {
                return null;
            }

            float px = 0;
            float py = 0;
            if (Fpdf.FPDFPathSegment_GetPoint(segment, &px, &py) == 0 || !float.IsFinite(px) || !float.IsFinite(py))
            {
                return null;
            }

            x[i] = px;
            y[i] = py;
        }

        if (count == 5 && (Math.Abs(x[4] - x[0]) > CornerTolerance || Math.Abs(y[4] - y[0]) > CornerTolerance))
        {
            return null;
        }

        // The four corners go around: either x changes first (0 to 1) and y second, or the other way round.
        var horizontalFirst = Same(y[0], y[1]) && Same(x[1], x[2]) && Same(y[2], y[3]) && Same(x[3], x[0]);
        var verticalFirst = Same(x[0], x[1]) && Same(y[1], y[2]) && Same(x[2], x[3]) && Same(y[3], y[0]);
        if (!horizontalFirst && !verticalFirst)
        {
            return null;
        }

        if (!TryGetMatrix(path, out var own))
        {
            return null;
        }

        var matrix = own.Then(accum);
        if (!matrix.IsAxisParallel)
        {
            return null;
        }

        var local = new UserRect(Math.Min(Math.Min(x[0], x[1]), Math.Min(x[2], x[3])), Math.Min(Math.Min(y[0], y[1]), Math.Min(y[2], y[3])), Math.Max(Math.Max(x[0], x[1]), Math.Max(x[2], x[3])), Math.Max(Math.Max(y[0], y[1]), Math.Max(y[2], y[3])));
        uint r = 0;
        uint g = 0;
        uint b = 0;
        uint a = 0;
        if (Fpdf.FPDFPageObj_GetFillColor(path, &r, &g, &b, &a) == 0)
        {
            return null;
        }

        return new PathRectangle { Rect = matrix.Bounds(local), FillMode = fillMode, Fill = [r, g, b, a] };
    }

    /// <summary>Whether a path is filled (a stroked line across the page is not a background, however large its bounds are).</summary>
    public static bool IsFilled(nint path)
    {
        int fillMode = 0;
        int stroke = 0;
        return Fpdf.FPDFPath_GetDrawMode(path, &fillMode, &stroke) != 0 && fillMode != FpdfConstants.FillModeNone;
    }

    /// <summary>The filled rectangles that stay of <paramref name="rect"/> when the rectangles are cut out of it (at most <paramref name="maxPieces"/>; <see langword="null"/> beyond).</summary>
    public static List<UserRect>? Subtract(UserRect rect, IEnumerable<UserRect> holes, int maxPieces)
    {
        var pieces = new List<UserRect> { rect };
        foreach (var hole in holes)
        {
            var next = new List<UserRect>(pieces.Count + 3);
            foreach (var piece in pieces)
            {
                if (!(hole.Left < piece.Right) || !(piece.Left < hole.Right) || !(hole.Bottom < piece.Top) || !(piece.Bottom < hole.Top))
                {
                    next.Add(piece);
                    continue;
                }

                var left = Math.Max(hole.Left, piece.Left);
                var right = Math.Min(hole.Right, piece.Right);
                var bottom = Math.Max(hole.Bottom, piece.Bottom);
                var top = Math.Min(hole.Top, piece.Top);
                Add(next, new UserRect(piece.Left, piece.Bottom, piece.Right, bottom));
                Add(next, new UserRect(piece.Left, top, piece.Right, piece.Top));
                Add(next, new UserRect(piece.Left, bottom, left, top));
                Add(next, new UserRect(right, bottom, piece.Right, top));
            }

            pieces = next;
            if (pieces.Count > maxPieces)
            {
                return null;
            }
        }

        return pieces;

        static void Add(List<UserRect> into, UserRect piece)
        {
            if (piece.Right - piece.Left > MinPiece && piece.Top - piece.Bottom > MinPiece)
            {
                into.Add(piece);
            }
        }
    }

    /// <summary>A new filled rectangle in page space with the colour and fill rule of the path it replaces (no stroke, no clip, identity matrix).</summary>
    public static nint CreateFilledRectangle(UserRect rect, int fillMode, uint[] color)
    {
        var path = Fpdf.FPDFPageObj_CreateNewRect((float)rect.Left, (float)rect.Bottom, (float)(rect.Right - rect.Left), (float)(rect.Top - rect.Bottom));
        if (path == 0)
        {
            return 0;
        }

        if (Fpdf.FPDFPageObj_SetFillColor(path, color[0], color[1], color[2], color[3]) == 0 || Fpdf.FPDFPath_SetDrawMode(path, fillMode, 0) == 0)
        {
            Fpdf.FPDFPageObj_Destroy(path);
            return 0;
        }

        return path;
    }

    /// <summary>The keys of the expected images that the page does not have any more (same pixel size and place to within 0.05 pt).</summary>
    public static List<long> FindLostImages(nint page, List<ExpectedImage> expected)
    {
        var buckets = new Dictionary<(long, long, long, long), List<(UserRect Bounds, int Id)>>();
        var count = Fpdf.FPDFPage_CountObjects(page);
        var id = 0;
        for (var i = 0; i < count; i++)
        {
            var obj = Fpdf.FPDFPage_GetObject(page, i);
            if (obj == 0 || Fpdf.FPDFPageObj_GetType(obj) != FpdfConstants.PageObjImage || !TryGetPixelSize(obj, out var w, out var h) || !PdfiumRedactionNative.TryGetBounds(obj, out var bounds))
            {
                continue;
            }

            var key = (w, h, (long)Math.Floor(bounds.Left), (long)Math.Floor(bounds.Bottom));
            if (!buckets.TryGetValue(key, out var list))
            {
                buckets[key] = list = [];
            }

            list.Add((bounds, id++));
        }

        var used = new HashSet<int>();
        var lost = new List<long>();
        foreach (var e in expected)
        {
            var found = false;
            var cx = (long)Math.Floor(e.Bounds.Left);
            var cy = (long)Math.Floor(e.Bounds.Bottom);
            for (var dx = -1; dx <= 1 && !found; dx++)
            {
                for (var dy = -1; dy <= 1 && !found; dy++)
                {
                    if (!buckets.TryGetValue((e.Width, e.Height, cx + dx, cy + dy), out var list))
                    {
                        continue;
                    }

                    foreach (var (bounds, candidate) in list)
                    {
                        if (!used.Contains(candidate)
                            && Math.Abs(bounds.Left - e.Bounds.Left) <= 0.05 && Math.Abs(bounds.Right - e.Bounds.Right) <= 0.05
                            && Math.Abs(bounds.Bottom - e.Bounds.Bottom) <= 0.05 && Math.Abs(bounds.Top - e.Bounds.Top) <= 0.05)
                        {
                            used.Add(candidate);
                            found = true;
                            break;
                        }
                    }
                }
            }

            if (!found)
            {
                lost.Add(e.Key);
            }
        }

        return lost;
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) <= CornerTolerance;
}
