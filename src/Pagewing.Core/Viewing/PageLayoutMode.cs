namespace Pagewing.Core.Viewing;

/// <summary>How <see cref="DocumentLayout"/> arranges the pages of a document into rows.</summary>
public enum PageLayoutMode
{
    /// <summary>One page per row, all pages one below the other (scrolling).</summary>
    Continuous,

    /// <summary>One page per row, but only one row exists at a time, see <see cref="DocumentLayout.ShowPage"/>.</summary>
    SinglePage,

    /// <summary>Two pages per row (pages 0 and 1, 2 and 3, ...); a last odd page stands alone on the left half.</summary>
    TwoPages,

    /// <summary>Like <see cref="TwoPages"/>, but page 0 is a cover that stands alone on the right half (pages 1 and 2, 3 and 4, ...).</summary>
    TwoPagesWithCover,
}

/// <summary>How <see cref="DocumentLayout.FitZoom"/> derives a zoom from the viewport.</summary>
public enum ZoomMode
{
    /// <summary>The zoom stays what it is.</summary>
    Free,

    /// <summary>The row of the page fills the viewport width (minus margins).</summary>
    FitWidth,

    /// <summary>The row of the page fits the viewport completely (minus margins).</summary>
    FitPage,

    /// <summary>A zoom of 1 (100 %).</summary>
    ActualSize,
}
