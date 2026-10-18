using System.Runtime.CompilerServices;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>
/// A position in the document that survives zoom changes and page-size updates: a page and the fraction of
/// its height at which the position lies. The fraction is below 0 above the page (margin) and above 1 below
/// the page (gap), measured in page heights.
/// </summary>
public readonly record struct PageAnchor(int Page, double Fraction);

/// <summary>
/// Layout of all pages of a document in content coordinates (DIPs): the pages arranged in rows one below the
/// other (one page or a two-page spread per row, see <see cref="Mode"/>), centered horizontally, with a gap
/// between the rows and a margin around them. Pages whose size is not yet known take the estimate. Not
/// thread-safe; for use on the UI thread only.
/// </summary>
/// <remarks>
/// Row tops are prefix sums over the row heights, recomputed lazily after <see cref="SetSizes"/>, a
/// <see cref="Zoom"/> change, a <see cref="Mode"/> change or <see cref="ShowPage"/>, so a lookup by position
/// is a binary search over the rows and stays cheap for thousands of pages on every scroll event. A row is as
/// high as its tallest page and its pages are top-aligned. The pages of a spread sit on both sides of the
/// centre axis (the middle of the content width).
/// </remarks>
public sealed class DocumentLayout
{
    /// <summary>DIPs per PDF point: 96 dpi at a zoom of 1 against 72 points per inch.</summary>
    public const double DipsPerPoint = 96.0 / 72.0;

    /// <summary>The vertical gap between two rows in DIPs (not scaled by the zoom).</summary>
    public const double PageGap = 20;

    /// <summary>The margin around the pages in DIPs (not scaled by the zoom).</summary>
    public const double Margin = 32;

    /// <summary>The horizontal gap between the two pages of a spread in DIPs (not scaled by the zoom); each page keeps half of it from the centre axis.</summary>
    public const double SpreadGap = 8;

    private readonly PdfPageSize estimate;
    private readonly PdfPageSize[] sizes;
    private readonly bool[] known;

    // The top of every page: the top of its row (pages are top-aligned). Valid for the shown pages only.
    private readonly double[] tops;

    // Per row (a row count never exceeds the page count): the top, and the bottom of its tallest page. Valid for the shown rows only.
    private readonly double[] rowTops;
    private readonly double[] rowBottoms;
    private PageLayoutMode mode = PageLayoutMode.Continuous;
    private int shownPage;
    private double zoom = ZoomSteps.Default;
    private double contentWidth;
    private double contentHeight;
    private int firstShownRow;
    private int lastShownRow = -1;
    private bool dirty = true;

    /// <summary>Creates a layout for <paramref name="pageCount"/> pages that all start with the size <paramref name="estimate"/>.</summary>
    /// <param name="pageCount">The number of pages; 0 or more.</param>
    /// <param name="estimate">The size in points used for unknown pages. A size that is not finite and positive becomes A4.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageCount"/> is negative.</exception>
    public DocumentLayout(int pageCount, PdfPageSize estimate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        this.estimate = IsValid(estimate) ? estimate : PdfPageSize.A4;
        PageCount = pageCount;
        sizes = new PdfPageSize[pageCount];
        Array.Fill(sizes, this.estimate);
        known = new bool[pageCount];
        tops = new double[pageCount];
        rowTops = new double[pageCount];
        rowBottoms = new double[pageCount];
    }

    /// <summary>Gets the number of pages.</summary>
    public int PageCount { get; }

    /// <summary>Gets or sets the zoom (1 = 100 %). Values are limited with <see cref="ZoomSteps.Clamp"/>.</summary>
    public double Zoom
    {
        get => zoom;
        set
        {
            var clamped = ZoomSteps.Clamp(value);
            if (clamped != zoom)
            {
                zoom = clamped;
                dirty = true;
            }
        }
    }

    /// <summary>Gets or sets how the pages are arranged into rows. The default is <see cref="PageLayoutMode.Continuous"/>.</summary>
    public PageLayoutMode Mode
    {
        get => mode;
        set
        {
            if (value != mode)
            {
                mode = value;
                dirty = true;
            }
        }
    }

    /// <summary>
    /// Gets the number of rows the document has in the current <see cref="Mode"/> (one per page, or one per
    /// spread; 0 without pages). In <see cref="PageLayoutMode.SinglePage"/> this is still the whole document,
    /// although only one row is shown.
    /// </summary>
    public int RowCount => mode switch
    {
        PageLayoutMode.TwoPages => (PageCount + 1) / 2,
        PageLayoutMode.TwoPagesWithCover => PageCount == 0 ? 0 : (PageCount / 2) + 1,
        _ => PageCount,
    };

    /// <summary>Gets the number of pages whose real size has been set with <see cref="SetSizes"/>.</summary>
    public int KnownPageCount { get; private set; }

    /// <summary>Gets the width of the scrollable content: the widest shown row plus the margins; 0 without pages.</summary>
    public double ContentWidth
    {
        get
        {
            EnsureLayout();
            return contentWidth;
        }
    }

    /// <summary>Gets the height of the scrollable content: all shown rows, the gaps between them and the margins; 0 without pages.</summary>
    public double ContentHeight
    {
        get
        {
            EnsureLayout();
            return contentHeight;
        }
    }

    /// <summary>
    /// Sets the real sizes of the pages <paramref name="firstPage"/> and the following. A size that is not
    /// finite and positive is treated as not known: the page keeps the estimate and <see cref="IsSizeKnown"/> stays false.
    /// </summary>
    /// <param name="firstPage">The index of the first page of <paramref name="newSizes"/>.</param>
    /// <param name="newSizes">The sizes in points.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range lies outside the document.</exception>
    public void SetSizes(int firstPage, IReadOnlyList<PdfPageSize> newSizes)
    {
        ArgumentNullException.ThrowIfNull(newSizes);
        ArgumentOutOfRangeException.ThrowIfNegative(firstPage);
        if (firstPage > PageCount || newSizes.Count > PageCount - firstPage)
        {
            throw new ArgumentOutOfRangeException(nameof(newSizes), newSizes.Count, "The sizes must lie within the pages of the document.");
        }

        for (var i = 0; i < newSizes.Count; i++)
        {
            var page = firstPage + i;
            var size = newSizes[i];
            var valid = IsValid(size);
            if (known[page] != valid)
            {
                known[page] = valid;
                KnownPageCount += valid ? 1 : -1;
            }

            sizes[page] = valid ? size : estimate;
        }

        if (newSizes.Count > 0)
        {
            dirty = true;
        }
    }

    /// <summary>Gets a value indicating whether the real size of <paramref name="page"/> has been set.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is not a page of the document.</exception>
    public bool IsSizeKnown(int page)
    {
        CheckPage(page);
        return known[page];
    }

    /// <summary>Gets the size of <paramref name="page"/> in points: the real size, or the estimate while it is unknown.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is not a page of the document.</exception>
    public PdfPageSize GetPageSize(int page)
    {
        CheckPage(page);
        return sizes[page];
    }

    /// <summary>Gets the index of the row that holds <paramref name="page"/> in the current <see cref="Mode"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is not a page of the document.</exception>
    public int RowOf(int page)
    {
        CheckPage(page);
        return RowOfCore(page);
    }

    /// <summary>Gets the pages of the row <paramref name="row"/> as an inclusive range (one or two pages).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is not a row of the document.</exception>
    public (int First, int Last) PagesOfRow(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        return PagesOfRowCore(row);
    }

    /// <summary>
    /// Chooses the page that <see cref="PageLayoutMode.SinglePage"/> shows (initially page 0). In every other
    /// mode the choice is remembered but has no effect.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is not a page of the document.</exception>
    public void ShowPage(int page)
    {
        CheckPage(page);
        if (page != shownPage)
        {
            shownPage = page;
            dirty = true;
        }
    }

    /// <summary>
    /// Gets a value indicating whether <paramref name="page"/> is part of the layout: every page of the
    /// document, except in <see cref="PageLayoutMode.SinglePage"/>, where it is the chosen page only. A number
    /// that is not a page of the document is not shown.
    /// </summary>
    public bool IsShown(int page) =>
        page >= 0 && page < PageCount && (mode != PageLayoutMode.SinglePage || page == shownPage);

    /// <summary>Gets the rectangle of <paramref name="page"/> in content coordinates, placed in its row around the centre axis.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is not a page of the document, or is not shown (<see cref="IsShown"/>).</exception>
    public DipRect GetPageRect(int page)
    {
        CheckPage(page);
        if (!IsShown(page))
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "The page is not shown in the single-page mode.");
        }

        EnsureLayout();
        var size = sizes[page];
        var width = size.Width * DipsPerPoint * zoom;
        var height = size.Height * DipsPerPoint * zoom;
        return new DipRect(LeftOf(page, width), tops[page], width, height);
    }

    /// <summary>
    /// Gets the pages that touch the vertical range <paramref name="top"/> to <paramref name="bottom"/>
    /// (content coordinates), as an inclusive range of whole rows: both pages of a spread count even when only
    /// the taller one reaches into the range. When no row touches it (the range lies in a gap, in a margin or
    /// there are no pages) the result is <c>(0, -1)</c>, which a <c>for (p = First; p &lt;= Last; ...)</c> loop skips.
    /// </summary>
    public (int First, int Last) GetVisiblePages(double top, double bottom)
    {
        if (PageCount == 0 || double.IsNaN(top) || double.IsNaN(bottom) || bottom < top)
        {
            return (0, -1);
        }

        EnsureLayout();

        // First row whose bottom edge is at or below the range top; last row whose top edge is at or above the range bottom.
        var first = FirstRowEndingAtOrAfter(top);
        var last = LastRowStartingAtOrBefore(bottom);
        return first <= last && first <= lastShownRow && last >= firstShownRow
            ? (PagesOfRowCore(first).First, PagesOfRowCore(last).Last)
            : (0, -1);
    }

    /// <summary>
    /// Gets the page with the most visible height in the range <paramref name="top"/> to <paramref name="bottom"/>
    /// (the lowest page index wins a tie). When no page is visible it is the first page of the row nearest to
    /// the middle of the range; without pages the result is -1.
    /// </summary>
    public int GetMostVisiblePage(double top, double bottom)
    {
        if (PageCount == 0)
        {
            return -1;
        }

        var (first, last) = GetVisiblePages(top, bottom);
        if (first <= last)
        {
            var best = first;
            var bestShare = double.NegativeInfinity;
            for (var page = first; page <= last; page++)
            {
                var pageTop = tops[page];
                var pageBottom = pageTop + (sizes[page].Height * DipsPerPoint * zoom);
                var share = Math.Min(bottom, pageBottom) - Math.Max(top, pageTop);
                if (share > bestShare)
                {
                    best = page;
                    bestShare = share;
                }
            }

            return best;
        }

        return PagesOfRowCore(NearestRow(double.IsNaN(top) || double.IsNaN(bottom) ? 0 : (top + bottom) / 2)).First;
    }

    /// <summary>Gets the anchor for the content position <paramref name="top"/>, see <see cref="PageAnchor"/>; it refers to the first page of the row.</summary>
    public PageAnchor GetAnchor(double top)
    {
        if (PageCount == 0 || double.IsNaN(top))
        {
            return new PageAnchor(0, 0);
        }

        EnsureLayout();

        // The last row that starts at or above the position; above the first row this is the first shown row with a negative fraction.
        var row = Math.Max(firstShownRow, LastRowStartingAtOrBefore(top));
        var page = PagesOfRowCore(row).First;
        var height = sizes[page].Height * DipsPerPoint * zoom;
        return new PageAnchor(page, (top - tops[page]) / height);
    }

    /// <summary>
    /// Gets the content position of <paramref name="anchor"/> in the current layout. In
    /// <see cref="PageLayoutMode.SinglePage"/> an anchor on a page that is not shown refers to the shown page.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The anchor's page is not a page of the document.</exception>
    public double GetOffset(PageAnchor anchor)
    {
        if (PageCount == 0)
        {
            return 0;
        }

        CheckPage(anchor.Page);
        EnsureLayout();
        var page = IsShown(anchor.Page) ? anchor.Page : shownPage;
        return tops[page] + (anchor.Fraction * sizes[page].Height * DipsPerPoint * zoom);
    }

    /// <summary>
    /// Gets the zoom with which the row of <paramref name="page"/> fits the viewport in the current
    /// <see cref="Mode"/>, limited with <see cref="ZoomSteps.Clamp"/>. A viewport smaller than the margins gives
    /// <see cref="ZoomSteps.Min"/>. Does not change <see cref="Zoom"/>.
    /// </summary>
    /// <param name="mode">How to fit; <see cref="ZoomMode.Free"/> keeps the current zoom, <see cref="ZoomMode.ActualSize"/> is 1.</param>
    /// <param name="page">The page whose row is fitted.</param>
    /// <param name="viewportWidth">The width of the visible area in DIPs.</param>
    /// <param name="viewportHeight">The height of the visible area in DIPs.</param>
    /// <returns>The zoom; the current <see cref="Zoom"/> when <paramref name="page"/> is not a page of the document or a viewport size is NaN.</returns>
    public double FitZoom(ZoomMode mode, int page, double viewportWidth, double viewportHeight)
    {
        if (mode == ZoomMode.Free || page < 0 || page >= PageCount || double.IsNaN(viewportWidth) || double.IsNaN(viewportHeight))
        {
            return zoom;
        }

        if (mode == ZoomMode.ActualSize)
        {
            return 1;
        }

        var (first, last) = PagesOfRowCore(RowOfCore(page));
        var maxWidth = 0.0;
        var maxHeight = 0.0;
        for (var p = first; p <= last; p++)
        {
            maxWidth = Math.Max(maxWidth, sizes[p].Width);
            maxHeight = Math.Max(maxHeight, sizes[p].Height);
        }

        // Fixed DIPs besides the pages, and the DIPs per point at which the row is exactly as wide as the viewport.
        var spread = IsSpreadMode;
        var availableWidth = viewportWidth - (2 * Margin) - (spread ? SpreadGap : 0);
        if (!(availableWidth > 0))
        {
            return ZoomSteps.Min;
        }

        var scale = availableWidth / (spread ? 2 * maxWidth : maxWidth);
        if (mode == ZoomMode.FitPage)
        {
            var availableHeight = viewportHeight - (2 * Margin);
            if (!(availableHeight > 0))
            {
                return ZoomSteps.Min;
            }

            scale = Math.Min(scale, availableHeight / maxHeight);
        }

        return ZoomSteps.Clamp(scale / DipsPerPoint);
    }

    private static bool IsValid(PdfPageSize size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;

    private bool IsSpreadMode => mode is PageLayoutMode.TwoPages or PageLayoutMode.TwoPagesWithCover;

    private void CheckPage(int page, [CallerArgumentExpression(nameof(page))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, PageCount, name);
    }

    private int RowOfCore(int page) => mode switch
    {
        PageLayoutMode.TwoPages => page / 2,
        PageLayoutMode.TwoPagesWithCover => (page + 1) / 2,
        _ => page,
    };

    private (int First, int Last) PagesOfRowCore(int row) => mode switch
    {
        PageLayoutMode.TwoPages => (row * 2, Math.Min((row * 2) + 1, PageCount - 1)),
        PageLayoutMode.TwoPagesWithCover => row == 0 ? (0, 0) : ((row * 2) - 1, Math.Min(row * 2, PageCount - 1)),
        _ => (row, row),
    };

    /// <summary>The left edge of <paramref name="page"/>: centered on the axis, or beside the axis in a spread.</summary>
    private double LeftOf(int page, double width)
    {
        if (!IsSpreadMode)
        {
            return (contentWidth - width) / 2;
        }

        var axis = contentWidth / 2;
        var (first, last) = PagesOfRowCore(RowOfCore(page));
        var onTheRight = (mode == PageLayoutMode.TwoPagesWithCover && first == 0) || (first != last && page == last);
        return onTheRight ? axis + (SpreadGap / 2) : axis - (SpreadGap / 2) - width;
    }

    private void EnsureLayout()
    {
        if (!dirty)
        {
            return;
        }

        var scale = DipsPerPoint * zoom;
        var spread = IsSpreadMode;
        if (PageCount == 0)
        {
            firstShownRow = 0;
            lastShownRow = -1;
        }
        else if (mode == PageLayoutMode.SinglePage)
        {
            firstShownRow = shownPage;
            lastShownRow = shownPage;
        }
        else
        {
            firstShownRow = 0;
            lastShownRow = RowCount - 1;
        }

        var halfWidth = 0.0;
        var y = Margin;
        for (var row = firstShownRow; row <= lastShownRow; row++)
        {
            var (first, last) = PagesOfRowCore(row);
            var rowHeight = 0.0;
            var rowWidth = 0.0;
            for (var page = first; page <= last; page++)
            {
                tops[page] = y;
                rowHeight = Math.Max(rowHeight, sizes[page].Height);
                rowWidth = Math.Max(rowWidth, sizes[page].Width);
            }

            rowTops[row] = y;
            rowBottoms[row] = y + (rowHeight * DipsPerPoint * zoom);
            y += (rowHeight * scale) + PageGap;

            // The distance of the outer edge from the centre axis: a spread reaches one page wide to each side, a single page half of its width.
            halfWidth = Math.Max(halfWidth, spread ? (SpreadGap / 2) + (rowWidth * scale) : rowWidth * scale / 2);
        }

        // The loop added one gap too many after the last row; the margin takes its place.
        contentHeight = PageCount == 0 ? 0 : y - PageGap + Margin;
        contentWidth = PageCount == 0 ? 0 : (2 * halfWidth) + (2 * Margin);
        dirty = false;
    }

    /// <summary>Binary search: the first shown row whose bottom edge is at or below <paramref name="y"/>; one behind the last shown row when none.</summary>
    private int FirstRowEndingAtOrAfter(double y)
    {
        int low = firstShownRow, high = lastShownRow + 1;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (rowBottoms[mid] >= y)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }

    /// <summary>Binary search: the last shown row whose top edge is at or above <paramref name="y"/>; one before the first shown row when none.</summary>
    private int LastRowStartingAtOrBefore(double y)
    {
        int low = firstShownRow, high = lastShownRow + 1;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (rowTops[mid] <= y)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low - 1;
    }

    private int NearestRow(double y)
    {
        EnsureLayout();

        // The row after which y lies, and the one behind it, are the only candidates.
        var before = Math.Clamp(LastRowStartingAtOrBefore(y), firstShownRow, lastShownRow);
        var after = Math.Min(before + 1, lastShownRow);
        return Distance(after, y) < Distance(before, y) ? after : before;
    }

    private double Distance(int row, double y)
    {
        var top = rowTops[row];
        var bottom = rowBottoms[row];
        return y < top ? top - y : y > bottom ? y - bottom : 0;
    }
}
