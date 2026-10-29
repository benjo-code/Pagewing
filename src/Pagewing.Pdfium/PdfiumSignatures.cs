using System.Globalization;
using System.Security.Cryptography;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// The image signature: a stamp annotation with one image object, made and read on a loaded page. Every method must be
/// called on the PDFium thread and throws <see cref="PdfiumThreadViolationException"/> otherwise; every annotation handle is
/// closed before a method returns.
/// </summary>
/// <remarks>
/// <para>
/// The stamp is built from the pixels the app decoded beforehand (never decoded here): an image object with the bitmap
/// (BGRA, not premultiplied, so a soft edge becomes the image's soft mask), its matrix from the three corners of the box on
/// the displayed page (so the image stands upright on a rotated page and in a CropBox), appended to the stamp. PDFium writes
/// the appearance (<c>q a b c d e f cm /FXX1 Do Q</c> with the image and its <c>/SMask</c>) when it saves. Moving and scaling
/// is <c>FPDFAnnot_SetRect</c> alone: the appearance follows the rectangle.
/// </para>
/// <para>
/// A stamp is Pagewing's <b>own</b> only with the key <c>/PagewingSignature</c> (<c>1 &lt;SHA-256 of the pixels&gt;</c>, the
/// colour of fully transparent pixels counted as 0, so that the value does not depend on what PDFium reads back for them),
/// the flag Print alone, none of <c>/IRT</c>, <c>/Popup</c>, <c>/OC</c>, an appearance of exactly the shape above, one image
/// object, and pixels (read back one to one: <see cref="ReadImage"/>) that have the fingerprint of the key. The mark is a key
/// of the annotation, not a line of the appearance, because PDFium cannot start an appearance with a comment
/// (<c>FPDFAnnot_SetAP</c> would drop the image). Anything else is another program's and locked (a false "foreign" is safe).
/// </para>
/// <para>
/// Reading the pixels sets the matrix of the image object to its pixel size for one rendering (a one to one bitmap), and
/// back; <c>FPDFAnnot_GetObjectCount</c> may set a missing <c>/Matrix</c> of the appearance, so it is called only for stamps
/// that pass the cheap checks of the mark first (a foreign stamp is never touched). See <c>Docs/DECISIONS.md</c>, entry
/// "Sichtbare Unterschrift: Erscheinungsbild und Entscheidung (M12)".
/// </para>
/// </remarks>
internal static unsafe class PdfiumSignatures
{
    /// <summary>The longest side of an image in pixels, for creating and reading (the app stores at most 1,200).</summary>
    public const int MaxImageSide = 1200;

    /// <summary>The pixels the checks of one page read may render together; a stamp beyond it counts as foreign.</summary>
    public const long PixelBudget = 16L * 1024 * 1024;

    private const string MarkVersion = "1";
    private const int AppearanceBudgetBytes = 2048;
    private const int MaxAppearanceChars = 256;
    private const int FingerprintLength = 64;
    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdef");

    /// <summary>
    /// Creates a stamp with the image: <c>/Rect</c> (the hull of the box on the page), the image object, the mark, the flag
    /// Print, <c>/NM</c>, <c>/M</c>, <c>/CreationDate</c> and <c>/T</c> when given. On failure nothing is left on the page.
    /// </summary>
    /// <param name="document">The document of <paramref name="page"/> (the image object belongs to it).</param>
    /// <param name="page">The page.</param>
    /// <param name="box">The box in points from the top left of the displayed page; the image fills it.</param>
    /// <param name="width">The width of the image in pixels (1 to <see cref="MaxImageSide"/>).</param>
    /// <param name="height">The height of the image in pixels (1 to <see cref="MaxImageSide"/>).</param>
    /// <param name="bgra">The pixels, BGRA, not premultiplied, <paramref name="width"/> * <paramref name="height"/> * 4 bytes.</param>
    /// <param name="author">The author or <see langword="null"/>.</param>
    /// <param name="name">The <c>/NM</c>.</param>
    /// <param name="date">The date for <c>/M</c> and <c>/CreationDate</c>.</param>
    /// <returns>The index of the new annotation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is not in range.</exception>
    /// <exception cref="ArgumentException">The pixels have the wrong length, or the box is empty or not finite.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused to create or fill the annotation.</exception>
    public static int CreateImage(
        PdfiumDocument document,
        PdfiumPage page,
        (double Left, double Top, double Right, double Bottom) box,
        int width,
        int height,
        ReadOnlySpan<byte> bgra,
        string? author,
        string name,
        string date)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(date);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxImageSide);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxImageSide);
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException("The pixels must be width * height * 4 bytes.", nameof(bgra));
        }

        if (!double.IsFinite(box.Left) || !double.IsFinite(box.Top) || !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom)
            || !(box.Right > box.Left) || !(box.Bottom > box.Top))
        {
            throw new ArgumentException("The box needs a width and a height.", nameof(box));
        }

        var native = page.Access();
        var nativeDocument = document.Access();
        if (!DisplayTransform.TryCreate(native, page.Width, page.Height, out var transform))
        {
            throw new InvalidOperationException("PDFium cannot convert the coordinates of the page.");
        }

        var origin = ToUser(transform, box.Left, box.Bottom);
        var xEnd = ToUser(transform, box.Right, box.Bottom);
        var yEnd = ToUser(transform, box.Left, box.Top);
        var rect = Hull(transform, box);
        var matrix = new FsMatrix { A = xEnd.X - origin.X, B = xEnd.Y - origin.Y, C = yEnd.X - origin.X, D = yEnd.Y - origin.Y, E = origin.X, F = origin.Y };
        var fingerprint = Fingerprint(bgra);

        var annotation = Fpdf.FPDFPage_CreateAnnot(native, FpdfConstants.AnnotStamp);
        if (annotation == 0)
        {
            throw new InvalidOperationException("PDFium could not create the annotation.");
        }

        var index = -1;
        try
        {
            index = Fpdf.FPDFPage_GetAnnotIndex(native, annotation);
            if (index < 0)
            {
                throw new InvalidOperationException("PDFium did not add the annotation to the page.");
            }

            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetRect(annotation, &rect));
            AppendImage(nativeDocument, annotation, width, height, bgra, &matrix);
            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetFlags(annotation, FpdfConstants.AnnotFlagPrint));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "PagewingSignature\0"u8, MarkVersion + " " + fingerprint));
            if (!string.IsNullOrEmpty(author))
            {
                PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "T\0"u8, author));
            }

            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "NM\0"u8, name));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "M\0"u8, date));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "CreationDate\0"u8, date));
            return index;
        }
        catch
        {
            if (index >= 0)
            {
                _ = Fpdf.FPDFPage_RemoveAnnot(native, index);
            }

            throw;
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// Moves and scales the stamp at <paramref name="index"/> to <paramref name="box"/> (points from the top left of the displayed
    /// page) and sets <c>/M</c>. The caller keeps the aspect ratio; the appearance stretches to the new rectangle.
    /// </summary>
    /// <exception cref="ArgumentException">The box is empty or not finite.</exception>
    /// <exception cref="InvalidOperationException">The annotation is no stamp, or PDFium refused a change.</exception>
    public static void SetBox(PdfiumPage page, int index, (double Left, double Top, double Right, double Bottom) box, string date)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(date);
        if (!double.IsFinite(box.Left) || !double.IsFinite(box.Top) || !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom)
            || !(box.Right > box.Left) || !(box.Bottom > box.Top))
        {
            throw new ArgumentException("The box needs a width and a height.", nameof(box));
        }

        var native = page.Access();
        if (!DisplayTransform.TryCreate(native, page.Width, page.Height, out var transform))
        {
            throw new InvalidOperationException("PDFium cannot convert the coordinates of the page.");
        }

        var rect = Hull(transform, box);
        var annotation = Fpdf.FPDFPage_GetAnnot(native, index);
        if (annotation == 0)
        {
            throw new InvalidOperationException("PDFium could not load the annotation.");
        }

        try
        {
            if (Fpdf.FPDFAnnot_GetSubtype(annotation) != FpdfConstants.AnnotStamp)
            {
                throw new InvalidOperationException("The annotation is no stamp.");
            }

            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetRect(annotation, &rect));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "M\0"u8, date));
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>Whether the stamp at <paramref name="index"/> is Pagewing's own image signature (see the remarks of the class), with the budget of one read.</summary>
    public static bool IsOwnImage(PdfiumDocument document, PdfiumPage page, int index)
    {
        var budget = PixelBudget;
        return IsOwnImage(document, page, index, ref budget);
    }

    /// <summary>
    /// Like <see cref="IsOwnImage(PdfiumDocument, PdfiumPage, int)"/>, charging the pixels it renders to
    /// <paramref name="pixelBudget"/>; with the budget used up an otherwise own stamp counts as foreign (locked).
    /// </summary>
    public static bool IsOwnImage(PdfiumDocument document, PdfiumPage page, int index, ref long pixelBudget)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        if (annotation == 0)
        {
            return false;
        }

        try
        {
            return ReadOwnImage(document, page, annotation, ref pixelBudget) is not null;
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// Reads the image of an annotation that is Pagewing's own image signature (the cheap checks of the mark, the image one to one
    /// within <paramref name="pixelBudget"/>, the fingerprint of the key); <see langword="null"/> for any other.
    /// </summary>
    internal static (int Width, int Height, byte[] Bgra)? ReadOwnImage(PdfiumDocument document, PdfiumPage page, nint annotation, ref long pixelBudget) =>
        LooksOwn(annotation, out var fingerprint)
            && ReadImageObject(document, page, annotation, ref pixelBudget) is { } image
            && Fingerprint(image.Bgra) == fingerprint
            ? image
            : null;

    /// <summary>
    /// Reads the image of the stamp at <paramref name="index"/> one to one: <see langword="null"/> unless it is a stamp
    /// with exactly one image object of at most <see cref="MaxImageSide"/> pixels a side (the size is asked before anything is
    /// rendered). Whether the stamp is Pagewing's own is <see cref="IsOwnImage(PdfiumDocument, PdfiumPage, int)"/>'s question.
    /// </summary>
    public static (int Width, int Height, byte[] Bgra)? ReadImage(PdfiumDocument document, PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        if (annotation == 0)
        {
            return null;
        }

        try
        {
            var budget = PixelBudget;
            return Fpdf.FPDFAnnot_GetSubtype(annotation) == FpdfConstants.AnnotStamp ? ReadImageObject(document, page, annotation, ref budget) : null;
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// The fingerprint of pixels: SHA-256 as 64 lower-case hex digits over the BGRA bytes, the colour of a pixel with alpha 0
    /// counted as 0.
    /// </summary>
    internal static string Fingerprint(ReadOnlySpan<byte> bgra)
    {
        var normalized = bgra.ToArray();
        for (var i = 0; i < normalized.Length; i += 4)
        {
            if (normalized[i + 3] == 0)
            {
                normalized[i] = 0;
                normalized[i + 1] = 0;
                normalized[i + 2] = 0;
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(normalized));
    }

    private static void AppendImage(nint nativeDocument, nint annotation, int width, int height, ReadOnlySpan<byte> bgra, FsMatrix* matrix)
    {
        var image = Fpdf.FPDFPageObj_NewImageObj(nativeDocument);
        if (image == 0)
        {
            throw new InvalidOperationException("PDFium could not create the image.");
        }

        var appended = false;
        try
        {
            fixed (byte* pixels = bgra)
            {
                var bitmap = Fpdf.FPDFBitmap_CreateEx(width, height, FpdfConstants.BitmapBgra, pixels, width * 4);
                if (bitmap == 0)
                {
                    throw new InvalidOperationException("PDFium could not wrap the pixels.");
                }

                try
                {
                    PdfiumAnnotations.Check(Fpdf.FPDFImageObj_SetBitmap(null, 0, image, bitmap));
                }
                finally
                {
                    Fpdf.FPDFBitmap_Destroy(bitmap);
                }
            }

            PdfiumAnnotations.Check(Fpdf.FPDFPageObj_SetMatrix(image, matrix));
            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_AppendObject(annotation, image));
            appended = true;
        }
        finally
        {
            if (!appended)
            {
                Fpdf.FPDFPageObj_Destroy(image);
            }
        }
    }

    /// <summary>The cheap checks of the mark: subtype, flags, keys, the key's value and the shape of the appearance. Reads nothing that touches the stream.</summary>
    private static bool LooksOwn(nint annotation, out string fingerprint)
    {
        fingerprint = string.Empty;
        FsRectF rect;
        if (Fpdf.FPDFAnnot_GetSubtype(annotation) != FpdfConstants.AnnotStamp
            || Fpdf.FPDFAnnot_GetFlags(annotation) != FpdfConstants.AnnotFlagPrint
            || !PdfiumAnnotations.HasKey(annotation, "Rect\0"u8)
            || Fpdf.FPDFAnnot_GetRect(annotation, &rect) == 0
            || !PdfiumAnnotations.IsSane(rect.Left) || !PdfiumAnnotations.IsSane(rect.Top) || !PdfiumAnnotations.IsSane(rect.Right) || !PdfiumAnnotations.IsSane(rect.Bottom)
            || PdfiumAnnotations.HasKey(annotation, "IRT\0"u8)
            || PdfiumAnnotations.HasKey(annotation, "Popup\0"u8)
            || PdfiumAnnotations.HasKey(annotation, "OC\0"u8))
        {
            return false;
        }

        var mark = PdfiumAnnotations.ReadString(annotation, "PagewingSignature\0"u8);
        if (mark is null || mark.Length != MarkVersion.Length + 1 + FingerprintLength || !mark.StartsWith(MarkVersion + " ", StringComparison.Ordinal)
            || mark.AsSpan(MarkVersion.Length + 1).ContainsAnyExcept(HexDigits))
        {
            return false;
        }

        char[]? scratch = null;
        try
        {
            var budget = (long)AppearanceBudgetBytes;
            if (!HasOwnAppearanceShape(PdfiumTextBoxes.ReadAppearance(annotation, ref scratch, ref budget, MaxAppearanceChars)))
            {
                return false;
            }
        }
        finally
        {
            PdfiumTextBoxes.ReturnScratch(ref scratch);
        }

        fingerprint = mark[(MarkVersion.Length + 1)..];
        return true;
    }

    /// <summary>Whether an appearance is exactly <c>q a b c d e f cm /Name Do Q</c> (six finite numbers, whitespace as it is).</summary>
    internal static bool HasOwnAppearanceShape(ReadOnlySpan<char> content)
    {
        var tokens = 0;
        var position = 0;
        while (position < content.Length)
        {
            while (position < content.Length && PdfiumAnnotations.IsWhite(content[position]))
            {
                position++;
            }

            if (position >= content.Length)
            {
                break;
            }

            var start = position;
            while (position < content.Length && !PdfiumAnnotations.IsWhite(content[position]))
            {
                position++;
            }

            var token = content[start..position];
            var ok = tokens switch
            {
                0 => token is "q",
                >= 1 and <= 6 => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
                7 => token is "cm",
                8 => token.Length > 1 && token[0] == '/',
                9 => token is "Do",
                10 => token is "Q",
                _ => false,
            };
            if (!ok)
            {
                return false;
            }

            tokens++;
        }

        return tokens == 11;
    }

    /// <summary>
    /// The image of the annotation's only image object, one to one: the size is asked first and refused beyond
    /// <see cref="MaxImageSide"/> a side or the <paramref name="pixelBudget"/> (charged before rendering); the object's matrix is
    /// set to the pixel size for one rendering and restored.
    /// </summary>
    private static (int Width, int Height, byte[] Bgra)? ReadImageObject(PdfiumDocument document, PdfiumPage page, nint annotation, ref long pixelBudget)
    {
        if (Fpdf.FPDFAnnot_GetObjectCount(annotation) != 1)
        {
            return null;
        }

        var image = Fpdf.FPDFAnnot_GetObject(annotation, 0);
        uint pixelWidth, pixelHeight;
        if (image == 0 || Fpdf.FPDFImageObj_GetImagePixelSize(image, &pixelWidth, &pixelHeight) == 0
            || pixelWidth is 0 or > MaxImageSide || pixelHeight is 0 or > MaxImageSide)
        {
            return null;
        }

        var width = (int)pixelWidth;
        var height = (int)pixelHeight;
        pixelBudget -= (long)width * height;
        if (pixelBudget < 0)
        {
            return null;
        }

        FsMatrix saved;
        if (Fpdf.FPDFPageObj_GetMatrix(image, &saved) == 0)
        {
            return null;
        }

        var unit = new FsMatrix { A = width, D = height };
        if (Fpdf.FPDFPageObj_SetMatrix(image, &unit) == 0)
        {
            return null;
        }

        nint bitmap;
        try
        {
            bitmap = Fpdf.FPDFImageObj_GetRenderedBitmap(document.Access(), page.Access(), image);
        }
        finally
        {
            _ = Fpdf.FPDFPageObj_SetMatrix(image, &saved);
        }

        if (bitmap == 0)
        {
            return null;
        }

        try
        {
            var stride = Fpdf.FPDFBitmap_GetStride(bitmap);
            var buffer = (byte*)Fpdf.FPDFBitmap_GetBuffer(bitmap);
            if (buffer == null || Fpdf.FPDFBitmap_GetFormat(bitmap) != FpdfConstants.BitmapBgra
                || Fpdf.FPDFBitmap_GetWidth(bitmap) != width || Fpdf.FPDFBitmap_GetHeight(bitmap) != height || stride < width * 4)
            {
                return null;
            }

            var bgra = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>(buffer + ((long)y * stride), width * 4).CopyTo(bgra.AsSpan(y * width * 4));
            }

            return (width, height, bgra);
        }
        finally
        {
            Fpdf.FPDFBitmap_Destroy(bitmap);
        }
    }

    private static FsPointF ToUser(DisplayTransform transform, double x, double y)
    {
        var (userX, userY) = transform.ToUser(x, y);
        var point = new FsPointF { X = (float)userX, Y = (float)userY };
        if (!PdfiumAnnotations.IsSane(point.X) || !PdfiumAnnotations.IsSane(point.Y))
        {
            throw new ArgumentException("A corner of the box lies beyond the range PDFium can write.", nameof(x));
        }

        return point;
    }

    /// <summary>The hull in user space of the four corners of a box on the displayed page (the box turned by the page's rotation).</summary>
    private static FsRectF Hull(DisplayTransform transform, (double Left, double Top, double Right, double Bottom) box)
    {
        var a = ToUser(transform, box.Left, box.Top);
        var b = ToUser(transform, box.Right, box.Top);
        var c = ToUser(transform, box.Left, box.Bottom);
        var d = ToUser(transform, box.Right, box.Bottom);
        return new FsRectF
        {
            Left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
            Right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
            Bottom = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
            Top = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)),
        };
    }
}
