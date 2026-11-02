namespace Pagewing.Core.Redaction;

/// <summary>The limits of redaction planning and checking (starting values, pinned by a test).</summary>
public static class RedactionLimits
{
    /// <summary>The most marks one <see cref="RedactionPlan"/> may hold; checked before anything is allocated.</summary>
    public const int MaxMarks = 10_000;

    /// <summary>The most rectangles one <see cref="RedactionMark"/> may have (a long selection has one per line).</summary>
    public const int MaxAreasPerMark = 500;

    /// <summary>The longest term, in UTF-16 code units, that a mark keeps for the check.</summary>
    public const int MaxTermLength = 1_000;

    /// <summary>The shortest removed text, in characters, that the raw-bytes check uses; shorter text would match streams by chance.</summary>
    public const int MinRawCheckLength = 4;

    /// <summary>The most bytes one stream may unpack to in the raw-bytes check (a larger stream is a decompression bomb and is reported).</summary>
    public const long MaxInflatedStream = 128L * 1024 * 1024;

    /// <summary>The most bytes all streams of one file may unpack to in the raw-bytes check.</summary>
    public const long MaxInflatedTotal = 1024L * 1024 * 1024;

    /// <summary>The most pixels (width times height) one raster page may have; about 128 MiB as BGRA.</summary>
    public const int MaxRasterPixels = 32_000_000;

    /// <summary>The lowest resolution, in dots per inch, of a raster page.</summary>
    public const int MinRasterDpi = 72;

    /// <summary>The highest resolution, in dots per inch, of a raster page.</summary>
    public const int MaxRasterDpi = 300;

    /// <summary>The resolution, in dots per inch, a plan starts with.</summary>
    public const int DefaultRasterDpi = 200;

    /// <summary>The most pages one <see cref="RedactionPlan"/> may turn into raster pages.</summary>
    public const int MaxRasterPages = 1_000;

    /// <summary>The longest overlay text, in UTF-16 code units.</summary>
    public const int MaxOverlayTextLength = 200;

    /// <summary>The smallest font size, in points, that an automatically sized overlay text may get.</summary>
    public const double MinOverlayFontSize = 6.0;

    /// <summary>
    /// The most path objects the overlay text of one redaction may take in the whole document (one object for each line, that is each tile
    /// of a repeated text). It keeps a page well under <c>PdfiumRedaction.MaxObjects</c>, which the page walks of the check count against, and a
    /// page turned into an image under what its check reads (<c>PdfiumRedactionRaster.MaxPathBounds</c>, which this must not exceed).
    /// </summary>
    public const int MaxOverlayPaths = 20_000;

    /// <summary>
    /// The most path segments (a curve counts three) the overlay text of one redaction may take in the whole document: about 17 MiB of page
    /// content at most. Whichever of the two limits is reached first stops the overlay text; the areas stay filled.
    /// </summary>
    public const long MaxOverlaySegments = 1_000_000;

    /// <summary>
    /// The most path objects the overlay text of one page may take. Writing the content of a page and reading it back are each one job on
    /// the PDFium thread, and a job takes the longer the more the page holds (measured: about 0.1 s at 150,000 segments, 0.13 s at 220,000 and
    /// 0.5 s at 900,000), so the page has a cap of its own that keeps the job in the range of a tenth of a second.
    /// </summary>
    public const int MaxOverlayPathsPerPage = 10_000;

    /// <summary>The most path segments the overlay text of one page may take (see <see cref="MaxOverlayPathsPerPage"/>).</summary>
    public const long MaxOverlaySegmentsPerPage = 150_000;

    /// <summary>The smallest side, in image pixels, of a block of pixelating or blurring.</summary>
    public const int MinBlockPixels = 8;

    /// <summary>The most blocks along each side of an area for <see cref="RedactionImageStrength.Coarse"/>.</summary>
    public const int CoarseBlocksPerAxis = 6;

    /// <summary>The most blocks along each side of an area for <see cref="RedactionImageStrength.VeryCoarse"/>.</summary>
    public const int VeryCoarseBlocksPerAxis = 3;
}
