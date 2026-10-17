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
/// Vertical layout of all pages of a document in content coordinates (DIPs): the pages one below the other,
/// centered horizontally, with a gap between them and a margin around them. Pages whose size is not yet
/// known take the estimate. Not thread-safe; for use on the UI thread only.
/// </summary>
/// <remarks>
/// Page tops are prefix sums over the page heights, recomputed lazily after <see cref="SetSizes"/> or a
/// <see cref="Zoom"/> change, so a page lookup by position is a binary search and stays cheap for thousands
/// of pages on every scroll event.
/// </remarks>
public sealed class DocumentLayout
{
    /// <summary>DIPs per PDF point: 96 dpi at a zoom of 1 against 72 points per inch.</summary>
    public const double DipsPerPoint = 96.0 / 72.0;

    /// <summary>The vertical gap between two pages in DIPs (not scaled by the zoom).</summary>
    public const double PageGap = 20;

    /// <summary>The margin around the pages in DIPs (not scaled by the zoom).</summary>
    public const double Margin = 32;

    private readonly PdfPageSize estimate;
    private readonly PdfPageSize[] sizes;
    private readonly bool[] known;
    private readonly double[] tops;
    private double zoom = ZoomSteps.Default;
    private double contentWidth;
    private double contentHeight;
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

    /// <summary>Gets the number of pages whose real size has been set with <see cref="SetSizes"/>.</summary>
    public int KnownPageCount { get; private set; }

    /// <summary>Gets the width of the scrollable content: the widest page plus the margins; 0 without pages.</summary>
    public double ContentWidth
    {
        get
        {
            EnsureLayout();
            return contentWidth;
        }
    }

    /// <summary>Gets the height of the scrollable content: all pages, the gaps between them and the margins; 0 without pages.</summary>
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

    /// <summary>Gets the rectangle of <paramref name="page"/> in content coordinates, centered in the content width.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="page"/> is not a page of the document.</exception>
    public DipRect GetPageRect(int page)
    {
        CheckPage(page);
        EnsureLayout();
        var size = sizes[page];
        var width = size.Width * DipsPerPoint * zoom;
        var height = size.Height * DipsPerPoint * zoom;
        return new DipRect((contentWidth - width) / 2, tops[page], width, height);
    }

    /// <summary>
    /// Gets the pages that touch the vertical range <paramref name="top"/> to <paramref name="bottom"/>
    /// (content coordinates), as an inclusive range. When no page touches it (the range lies in a gap, in a
    /// margin or there are no pages) the result is <c>(0, -1)</c>, which a <c>for (p = First; p &lt;= Last; ...)</c> loop skips.
    /// </summary>
    public (int First, int Last) GetVisiblePages(double top, double bottom)
    {
        if (PageCount == 0 || double.IsNaN(top) || double.IsNaN(bottom) || bottom < top)
        {
            return (0, -1);
        }

        EnsureLayout();

        // First page whose bottom edge is at or below the range top; last page whose top edge is at or above the range bottom.
        var first = FirstPageEndingAtOrAfter(top);
        var last = LastPageStartingAtOrBefore(bottom);
        return first <= last && first < PageCount && last >= 0 ? (first, last) : (0, -1);
    }

    /// <summary>
    /// Gets the page with the most visible height in the range <paramref name="top"/> to <paramref name="bottom"/>
    /// (the lowest page index wins a tie). When no page is visible it is the page nearest to the middle of the
    /// range; without pages the result is -1.
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

        return NearestPage(double.IsNaN(top) || double.IsNaN(bottom) ? 0 : (top + bottom) / 2);
    }

    /// <summary>Gets the anchor for the content position <paramref name="top"/>, see <see cref="PageAnchor"/>.</summary>
    public PageAnchor GetAnchor(double top)
    {
        if (PageCount == 0 || double.IsNaN(top))
        {
            return new PageAnchor(0, 0);
        }

        EnsureLayout();

        // The last page that starts at or above the position; above the first page this is page 0 with a negative fraction.
        var page = Math.Max(0, LastPageStartingAtOrBefore(top));
        var height = sizes[page].Height * DipsPerPoint * zoom;
        return new PageAnchor(page, (top - tops[page]) / height);
    }

    /// <summary>Gets the content position of <paramref name="anchor"/> in the current layout.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The anchor's page is not a page of the document.</exception>
    public double GetOffset(PageAnchor anchor)
    {
        if (PageCount == 0)
        {
            return 0;
        }

        CheckPage(anchor.Page);
        EnsureLayout();
        return tops[anchor.Page] + (anchor.Fraction * sizes[anchor.Page].Height * DipsPerPoint * zoom);
    }

    private static bool IsValid(PdfPageSize size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;

    private void CheckPage(int page, [CallerArgumentExpression(nameof(page))] string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(page, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, PageCount, name);
    }

    private void EnsureLayout()
    {
        if (!dirty)
        {
            return;
        }

        var scale = DipsPerPoint * zoom;
        var maxWidth = 0.0;
        var y = Margin;
        for (var page = 0; page < PageCount; page++)
        {
            tops[page] = y;
            y += (sizes[page].Height * scale) + PageGap;
            maxWidth = Math.Max(maxWidth, sizes[page].Width * scale);
        }

        // The loop added one gap too many after the last page; the margin takes its place.
        contentHeight = PageCount == 0 ? 0 : y - PageGap + Margin;
        contentWidth = PageCount == 0 ? 0 : maxWidth + (2 * Margin);
        dirty = false;
    }

    private double BottomOf(int page) => tops[page] + (sizes[page].Height * DipsPerPoint * zoom);

    /// <summary>Binary search: the first page whose bottom edge is at or below <paramref name="y"/>; <see cref="PageCount"/> when none.</summary>
    private int FirstPageEndingAtOrAfter(double y)
    {
        int low = 0, high = PageCount;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (BottomOf(mid) >= y)
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

    /// <summary>Binary search: the last page whose top edge is at or above <paramref name="y"/>; -1 when none.</summary>
    private int LastPageStartingAtOrBefore(double y)
    {
        int low = 0, high = PageCount;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (tops[mid] <= y)
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

    private int NearestPage(double y)
    {
        EnsureLayout();

        // The page after which y lies, and the one before it, are the only candidates.
        var before = Math.Clamp(LastPageStartingAtOrBefore(y), 0, PageCount - 1);
        var after = Math.Min(before + 1, PageCount - 1);
        return Distance(after, y) < Distance(before, y) ? after : before;
    }

    private double Distance(int page, double y)
    {
        var top = tops[page];
        var bottom = BottomOf(page);
        return y < top ? top - y : y > bottom ? y - bottom : 0;
    }
}
