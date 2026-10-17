using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class DocumentLayoutTests
{
    private static readonly PdfPageSize Letter = new(612, 792);

    /// <summary>A US Letter page at zoom 1 in DIPs (792 pt * 96/72).</summary>
    private const double LetterHeight = 1056;

    private static DocumentLayout ThreeLetterPages()
    {
        var layout = new DocumentLayout(3, Letter);
        layout.SetSizes(0, [Letter, Letter, Letter]);
        return layout;
    }

    [Fact]
    public void ContentHeightSumsPagesGapsAndMargins()
    {
        var layout = ThreeLetterPages();

        Assert.Equal((3 * LetterHeight) + (2 * 20) + (2 * 32), layout.ContentHeight, 6);
        Assert.Equal((612 * DocumentLayout.DipsPerPoint) + (2 * 32), layout.ContentWidth, 6);
    }

    [Fact]
    public void PagesAreCenteredInTheContentWidth()
    {
        var layout = new DocumentLayout(2, Letter);
        layout.SetSizes(0, [Letter, new PdfPageSize(300, 400)]);

        var wide = layout.GetPageRect(0);
        var narrow = layout.GetPageRect(1);

        Assert.Equal(32, wide.X, 6);
        Assert.Equal((layout.ContentWidth - narrow.Width) / 2, narrow.X, 6);
        Assert.Equal(400, narrow.Width, 6);
        Assert.Equal(wide.Bottom + 20, narrow.Y, 6);
        Assert.Equal(32, wide.Y, 6);
    }

    [Fact]
    public void UnknownPagesUseTheEstimate()
    {
        var layout = new DocumentLayout(1000, new PdfPageSize(200, 100));
        var estimatedHeight = layout.ContentHeight;
        var estimatedTopOfPage500 = layout.GetPageRect(500).Y;

        Assert.Equal((1000 * 100 * DocumentLayout.DipsPerPoint) + (999 * 20) + (2 * 32), estimatedHeight, 6);
        Assert.Equal(0, layout.KnownPageCount);
        Assert.False(layout.IsSizeKnown(0));
        Assert.Equal(new PdfPageSize(200, 100), layout.GetPageSize(7));

        layout.SetSizes(0, Enumerable.Repeat(new PdfPageSize(200, 200), 10).ToArray());

        var grown = 10 * 100 * DocumentLayout.DipsPerPoint;
        Assert.Equal(10, layout.KnownPageCount);
        Assert.True(layout.IsSizeKnown(9));
        Assert.False(layout.IsSizeKnown(10));
        Assert.Equal(new PdfPageSize(200, 200), layout.GetPageSize(9));
        Assert.Equal(new PdfPageSize(200, 100), layout.GetPageSize(10));
        Assert.Equal(estimatedHeight + grown, layout.ContentHeight, 6);
        Assert.Equal(estimatedTopOfPage500 + grown, layout.GetPageRect(500).Y, 6);
        Assert.Equal(100 * DocumentLayout.DipsPerPoint, layout.GetPageRect(500).Height, 6);
    }

    [Fact]
    public void ZoomScalesPagesButNotMarginAndGap()
    {
        var layout = ThreeLetterPages();

        layout.Zoom = 2;

        Assert.Equal(2, layout.Zoom);
        Assert.Equal((3 * 2 * LetterHeight) + (2 * 20) + (2 * 32), layout.ContentHeight, 6);
        Assert.Equal((612 * DocumentLayout.DipsPerPoint * 2) + (2 * 32), layout.ContentWidth, 6);
        var second = layout.GetPageRect(1);
        Assert.Equal(32 + (2 * LetterHeight) + 20, second.Y, 6);
        Assert.Equal(2 * LetterHeight, second.Height, 6);
    }

    [Fact]
    public void ZoomIsClamped()
    {
        var layout = ThreeLetterPages();

        layout.Zoom = 100;
        Assert.Equal(ZoomSteps.Max, layout.Zoom);

        layout.Zoom = 0;
        Assert.Equal(ZoomSteps.Min, layout.Zoom);

        layout.Zoom = double.NaN;
        Assert.Equal(ZoomSteps.Default, layout.Zoom);
    }

    [Fact]
    public void GetVisiblePagesReturnsAnInclusiveRange()
    {
        // Tops: 32, 1108, 2184. Bottoms: 1088, 2164, 3240.
        var layout = ThreeLetterPages();

        Assert.Equal((0, 0), layout.GetVisiblePages(0, 40));
        Assert.Equal((0, 1), layout.GetVisiblePages(1000, 1200));
        Assert.Equal((1, 2), layout.GetVisiblePages(2000, 5000));
        Assert.Equal((0, 2), layout.GetVisiblePages(-500, 5000));
        Assert.Equal((0, 0), layout.GetVisiblePages(1050, 1050));

        // Inside the gap between two pages, and behind or before everything: empty ranges.
        Assert.Equal((0, -1), layout.GetVisiblePages(1090, 1100));
        Assert.Equal((0, -1), layout.GetVisiblePages(4000, 5000));
        Assert.Equal((0, -1), layout.GetVisiblePages(-100, 10));
    }

    [Fact]
    public void GetVisiblePagesWithoutPagesIsEmpty()
    {
        var layout = new DocumentLayout(0, Letter);

        Assert.Equal((0, -1), layout.GetVisiblePages(0, 1000));
        Assert.Equal(-1, layout.GetMostVisiblePage(0, 1000));
        Assert.Equal(0, layout.ContentHeight);
        Assert.Equal(0, layout.ContentWidth);
    }

    [Fact]
    public void MostVisiblePageHasTheLargestVisibleShare()
    {
        // Tops: 32, 1108, 2184. Bottoms: 1088, 2164, 3240.
        var layout = ThreeLetterPages();

        Assert.Equal(0, layout.GetMostVisiblePage(0, 800));
        Assert.Equal(0, layout.GetMostVisiblePage(600, 1500));
        Assert.Equal(1, layout.GetMostVisiblePage(700, 1500));
        Assert.Equal(2, layout.GetMostVisiblePage(2000, 9000));

        // Only gap in view: the nearest page wins.
        Assert.Equal(0, layout.GetMostVisiblePage(1090, 1100));
        Assert.Equal(2, layout.GetMostVisiblePage(9000, 9100));
        Assert.Equal(0, layout.GetMostVisiblePage(-200, -100));
    }

    [Fact]
    public void AnchorSurvivesZoomAndSizeChanges()
    {
        var layout = new DocumentLayout(100, Letter);
        var top = layout.GetPageRect(10).Y + (0.25 * layout.GetPageRect(10).Height);

        var anchor = layout.GetAnchor(top);

        Assert.Equal(10, anchor.Page);
        Assert.Equal(0.25, anchor.Fraction, 9);
        Assert.Equal(top, layout.GetOffset(anchor), 6);

        layout.Zoom = 2;
        layout.SetSizes(0, Enumerable.Repeat(new PdfPageSize(300, 400), 5).ToArray());

        var offset = layout.GetOffset(anchor);
        var rect = layout.GetPageRect(10);
        Assert.Equal(rect.Y + (0.25 * rect.Height), offset, 6);
        var moved = layout.GetAnchor(offset);
        Assert.Equal(10, moved.Page);
        Assert.Equal(0.25, moved.Fraction, 9);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1070)]
    [InlineData(1100)]
    [InlineData(9999)]
    public void AnchorRoundTripsInMarginsAndGaps(double top)
    {
        var layout = ThreeLetterPages();

        var anchor = layout.GetAnchor(top);

        Assert.Equal(top, layout.GetOffset(anchor), 6);
    }

    [Fact]
    public void NonFiniteOrNonPositiveSizesFallBackToTheEstimate()
    {
        var estimate = new PdfPageSize(200, 100);
        var layout = new DocumentLayout(5, estimate);
        var heightBefore = layout.ContentHeight;

        layout.SetSizes(
            0,
            [
                new PdfPageSize(0, 100),
                new PdfPageSize(double.NaN, 100),
                new PdfPageSize(200, -5),
                new PdfPageSize(double.PositiveInfinity, 100),
                new PdfPageSize(200, double.NegativeInfinity),
            ]);

        Assert.Equal(0, layout.KnownPageCount);
        for (var page = 0; page < 5; page++)
        {
            Assert.False(layout.IsSizeKnown(page));
            Assert.Equal(estimate, layout.GetPageSize(page));
        }

        Assert.Equal(heightBefore, layout.ContentHeight, 6);
    }

    [Fact]
    public void InvalidEstimateFallsBackToA4()
    {
        var layout = new DocumentLayout(1, new PdfPageSize(double.NaN, 0));

        Assert.Equal(new PdfPageSize(595.28, 841.89), layout.GetPageSize(0));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        var layout = ThreeLetterPages();

        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentLayout(-1, Letter));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SetSizes(2, [Letter, Letter]));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SetSizes(-1, [Letter]));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetPageRect(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetPageRect(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetPageSize(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.IsSizeKnown(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetOffset(new PageAnchor(3, 0)));
    }

    [Fact]
    public void SetSizesOverwritesKnownPages()
    {
        var layout = ThreeLetterPages();

        layout.SetSizes(1, [new PdfPageSize(100, 100)]);

        Assert.Equal(3, layout.KnownPageCount);
        Assert.Equal(new PdfPageSize(100, 100), layout.GetPageSize(1));
        Assert.Equal((2 * LetterHeight) + (100 * DocumentLayout.DipsPerPoint) + (2 * 20) + (2 * 32), layout.ContentHeight, 6);
    }

    private static readonly PdfPageSize Landscape = new(792, 612);

    private static DocumentLayout Pages(int count, PageLayoutMode mode)
    {
        var layout = new DocumentLayout(count, Letter);
        layout.SetSizes(0, Enumerable.Repeat(Letter, count).ToArray());
        layout.Mode = mode;
        return layout;
    }

    [Fact]
    public void ContinuousModeIsUnchanged()
    {
        var layout = ThreeLetterPages();

        Assert.Equal(PageLayoutMode.Continuous, layout.Mode);
        Assert.Equal(3, layout.RowCount);
        Assert.Equal(2, layout.RowOf(2));
        Assert.Equal((1, 1), layout.PagesOfRow(1));
        Assert.True(layout.IsShown(1));
        Assert.Equal((612 * DocumentLayout.DipsPerPoint) + (2 * 32), layout.ContentWidth, 6);
        Assert.Equal(32, layout.GetPageRect(0).X, 6);
        Assert.Equal(32 + LetterHeight + 20, layout.GetPageRect(1).Y, 6);
    }

    [Fact]
    public void TwoPagesPlacesSpreadsSideBySideOnTheCentreAxis()
    {
        var layout = Pages(4, PageLayoutMode.TwoPages);
        var axis = layout.ContentWidth / 2;

        var left = layout.GetPageRect(0);
        var right = layout.GetPageRect(1);

        Assert.Equal(2, layout.RowCount);
        Assert.Equal(axis - (DocumentLayout.SpreadGap / 2), left.Right, 6);
        Assert.Equal(axis + (DocumentLayout.SpreadGap / 2), right.X, 6);
        Assert.Equal(left.Y, right.Y, 6);
        Assert.Equal((2 * LetterHeight) + 20 + (2 * 32), layout.ContentHeight, 6);
        Assert.Equal((2 * ((612 * DocumentLayout.DipsPerPoint) + (DocumentLayout.SpreadGap / 2))) + (2 * 32), layout.ContentWidth, 6);
        Assert.Equal(layout.GetPageRect(0).Bottom + 20, layout.GetPageRect(2).Y, 6);
    }

    [Fact]
    public void CoverPageStandsAloneOnTheRightHalf()
    {
        var layout = Pages(5, PageLayoutMode.TwoPagesWithCover);
        var axis = layout.ContentWidth / 2;

        Assert.Equal(3, layout.RowCount);
        Assert.Equal(axis + (DocumentLayout.SpreadGap / 2), layout.GetPageRect(0).X, 6);
        Assert.Equal((1, 2), layout.PagesOfRow(1));
        Assert.Equal((3, 4), layout.PagesOfRow(2));
        Assert.Equal(1, layout.RowOf(1));
        Assert.Equal(1, layout.RowOf(2));
        Assert.Equal(2, layout.RowOf(4));
        Assert.Equal(axis - (DocumentLayout.SpreadGap / 2), layout.GetPageRect(1).Right, 6);
        Assert.Equal(axis + (DocumentLayout.SpreadGap / 2), layout.GetPageRect(2).X, 6);
    }

    [Fact]
    public void CoverModeWithOnePageStaysOnTheRight()
    {
        var layout = Pages(1, PageLayoutMode.TwoPagesWithCover);

        Assert.Equal(1, layout.RowCount);
        Assert.Equal((layout.ContentWidth / 2) + (DocumentLayout.SpreadGap / 2), layout.GetPageRect(0).X, 6);
        Assert.Equal((2 * ((612 * DocumentLayout.DipsPerPoint) + (DocumentLayout.SpreadGap / 2))) + (2 * 32), layout.ContentWidth, 6);
    }

    [Fact]
    public void LastOddPageStandsAloneOnTheLeftHalf()
    {
        var layout = Pages(3, PageLayoutMode.TwoPages);

        Assert.Equal(2, layout.RowCount);
        Assert.Equal((2, 2), layout.PagesOfRow(1));
        Assert.Equal((layout.ContentWidth / 2) - (DocumentLayout.SpreadGap / 2), layout.GetPageRect(2).Right, 6);
    }

    [Fact]
    public void RowHeightIsTheTallerPageAndPagesAreTopAligned()
    {
        var layout = new DocumentLayout(4, Letter);
        layout.SetSizes(0, [Letter, Landscape, Letter, Letter]);
        layout.Mode = PageLayoutMode.TwoPages;

        var portrait = layout.GetPageRect(0);
        var landscape = layout.GetPageRect(1);

        Assert.Equal(portrait.Y, landscape.Y, 6);
        Assert.Equal(612 * DocumentLayout.DipsPerPoint, landscape.Height, 6);
        Assert.Equal(portrait.Bottom + 20, layout.GetPageRect(2).Y, 6);
        Assert.Equal((2 * LetterHeight) + 20 + (2 * 32), layout.ContentHeight, 6);

        // The wider page decides the width of the half pages.
        Assert.Equal((2 * ((792 * DocumentLayout.DipsPerPoint) + (DocumentLayout.SpreadGap / 2))) + (2 * 32), layout.ContentWidth, 6);
    }

    [Fact]
    public void SingleModeShowsOnlyTheChosenPage()
    {
        var layout = Pages(4, PageLayoutMode.SinglePage);

        Assert.True(layout.IsShown(0));
        layout.ShowPage(2);

        Assert.True(layout.IsShown(2));
        Assert.False(layout.IsShown(1));
        Assert.Equal(LetterHeight + (2 * 32), layout.ContentHeight, 6);
        Assert.Equal((2, 2), layout.GetVisiblePages(0, 99999));
        Assert.Equal(32, layout.GetPageRect(2).Y, 6);
        Assert.Equal(32, layout.GetPageRect(2).X, 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetPageRect(1));
        Assert.Equal(2, layout.GetMostVisiblePage(0, 99999));
        Assert.Equal(2, layout.GetAnchor(500).Page);

        // The page switches with ShowPage, the page numbers stay the document's.
        layout.ShowPage(0);
        Assert.Equal((0, 0), layout.GetVisiblePages(0, 99999));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ShowPage(4));
    }

    [Fact]
    public void ShowPageHasNoEffectOutsideSingleMode()
    {
        var layout = Pages(4, PageLayoutMode.Continuous);

        layout.ShowPage(2);

        Assert.True(layout.IsShown(0));
        Assert.Equal((0, 3), layout.GetVisiblePages(0, 99999));
    }

    [Fact]
    public void VisiblePagesIncludeBothPagesOfARow()
    {
        // Row tops: 32, 1108. Row bottoms: 1088, 2164.
        var layout = Pages(4, PageLayoutMode.TwoPages);

        Assert.Equal((0, 1), layout.GetVisiblePages(0, 40));
        Assert.Equal((2, 3), layout.GetVisiblePages(1200, 1300));
        Assert.Equal((0, 3), layout.GetVisiblePages(1000, 1200));
        Assert.Equal((0, -1), layout.GetVisiblePages(1090, 1100));
    }

    [Fact]
    public void MostVisiblePageInATwoPageRowIsTheFirstOnATie()
    {
        var layout = Pages(4, PageLayoutMode.TwoPages);

        Assert.Equal(0, layout.GetMostVisiblePage(100, 200));
        Assert.Equal(2, layout.GetMostVisiblePage(1200, 1300));

        // Only the gap in view: the nearest row wins, with its first page.
        Assert.Equal(2, layout.GetMostVisiblePage(1095, 1105));
    }

    [Fact]
    public void AnchorRoundTripsInTwoPageMode()
    {
        var layout = new DocumentLayout(5, Letter);
        layout.SetSizes(0, [Letter, Landscape, Letter, Letter, Landscape]);
        layout.Mode = PageLayoutMode.TwoPagesWithCover;

        foreach (var y in new[] { 0.0, 10, 500, 1070, 1100, 1500, 2300, 9999 })
        {
            Assert.Equal(y, layout.GetOffset(layout.GetAnchor(y)), 6);
        }
    }

    [Fact]
    public void ModeChangesRelayoutTheSamePages()
    {
        var layout = Pages(4, PageLayoutMode.Continuous);
        var continuousHeight = layout.ContentHeight;

        layout.Mode = PageLayoutMode.TwoPages;
        Assert.True(layout.ContentHeight < continuousHeight);

        layout.Mode = PageLayoutMode.Continuous;
        Assert.Equal(continuousHeight, layout.ContentHeight, 6);
    }

    [Fact]
    public void FitWidthFillsTheViewportMinusMargins()
    {
        var layout = ThreeLetterPages();

        var zoom = layout.FitZoom(ZoomMode.FitWidth, 0, 1000, 800);

        Assert.Equal((1000 - 64) / (612 * DocumentLayout.DipsPerPoint), zoom, 9);
        layout.Zoom = zoom;
        Assert.Equal(1000, layout.ContentWidth, 6);
    }

    [Fact]
    public void FitWidthInCoverModeUsesTheFullSpreadWidth()
    {
        var layout = Pages(5, PageLayoutMode.TwoPagesWithCover);

        var zoom = layout.FitZoom(ZoomMode.FitWidth, 0, 1000, 800);

        Assert.Equal((1000 - 64 - DocumentLayout.SpreadGap) / (2 * 612 * DocumentLayout.DipsPerPoint), zoom, 9);
        layout.Zoom = zoom;
        Assert.Equal(1000, layout.ContentWidth, 6);
    }

    [Fact]
    public void FitPageUsesTheSmallerRatio()
    {
        var layout = ThreeLetterPages();

        var heightBound = layout.FitZoom(ZoomMode.FitPage, 1, 1000, 500);
        var widthBound = layout.FitZoom(ZoomMode.FitPage, 1, 400, 2000);

        Assert.Equal((500 - 64) / (792 * DocumentLayout.DipsPerPoint), heightBound, 9);
        Assert.Equal((400 - 64) / (612 * DocumentLayout.DipsPerPoint), widthBound, 9);
    }

    [Fact]
    public void FitPageOfASpreadUsesTheTallerPage()
    {
        var layout = new DocumentLayout(2, Letter);
        layout.SetSizes(0, [Letter, Landscape]);
        layout.Mode = PageLayoutMode.TwoPages;

        var zoom = layout.FitZoom(ZoomMode.FitPage, 1, 5000, 500);

        Assert.Equal((500 - 64) / (792 * DocumentLayout.DipsPerPoint), zoom, 9);
    }

    [Fact]
    public void ActualSizeIsOne()
    {
        var layout = ThreeLetterPages();
        layout.Zoom = 3;

        Assert.Equal(1, layout.FitZoom(ZoomMode.ActualSize, 0, 800, 600));
        Assert.Equal(3, layout.FitZoom(ZoomMode.Free, 0, 800, 600));
    }

    [Fact]
    public void FitIsClampedToTheZoomRange()
    {
        var layout = ThreeLetterPages();

        Assert.Equal(ZoomSteps.Min, layout.FitZoom(ZoomMode.FitWidth, 0, 10, 10));
        Assert.Equal(ZoomSteps.Min, layout.FitZoom(ZoomMode.FitPage, 0, 1000, 40));
        Assert.Equal(ZoomSteps.Max, layout.FitZoom(ZoomMode.FitWidth, 0, 100000, 100000));
        Assert.Equal(ZoomSteps.Min, Pages(2, PageLayoutMode.TwoPages).FitZoom(ZoomMode.FitWidth, 0, 70, 800));
    }

    [Fact]
    public void FitZoomOfAnInvalidPageReturnsTheCurrentZoom()
    {
        var layout = ThreeLetterPages();
        layout.Zoom = 2;

        Assert.Equal(2, layout.FitZoom(ZoomMode.FitWidth, 3, 1000, 800));
        Assert.Equal(2, layout.FitZoom(ZoomMode.FitWidth, -1, 1000, 800));
        Assert.Equal(2, layout.FitZoom(ZoomMode.FitPage, 0, double.NaN, 800));
        Assert.Equal(2, layout.FitZoom(ZoomMode.FitPage, 0, 1000, double.NaN));
        Assert.Equal(ZoomSteps.Default, new DocumentLayout(0, Letter).FitZoom(ZoomMode.FitWidth, 0, 1000, 800));
    }

    [Fact]
    public void ThousandPagesStayCheapToQuery()
    {
        var layout = new DocumentLayout(1000, new PdfPageSize(200, 100));
        layout.SetSizes(0, Enumerable.Repeat(new PdfPageSize(200, 100), 1000).ToArray());
        var pageHeight = 100 * DocumentLayout.DipsPerPoint;
        var middle = layout.GetPageRect(500);

        var (first, last) = layout.GetVisiblePages(middle.Y + 1, middle.Bottom - 1);

        Assert.Equal((500, 500), (first, last));
        Assert.Equal(pageHeight, middle.Height, 6);
        Assert.Equal(500, layout.GetMostVisiblePage(middle.Y, middle.Bottom));
        Assert.Equal(500, layout.GetAnchor(middle.Y + 5).Page);
    }
}
