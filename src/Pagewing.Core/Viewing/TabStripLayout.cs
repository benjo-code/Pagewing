namespace Pagewing.Core.Viewing;

/// <summary>
/// Sizes the tabs of the title-bar tab strip. The tabs share their row with a drag area that always keeps a
/// minimum width. When even minimum-width tabs do not fit, the strip shows only the active tab plus an
/// "all tabs" button.
/// </summary>
public static class TabStripLayout
{
    /// <summary>The narrowest a tab gets while all tabs are shown, in device-independent pixels.</summary>
    public const double MinTabWidth = 120;

    /// <summary>The widest a tab gets, in device-independent pixels.</summary>
    public const double MaxTabWidth = 280;

    /// <summary>The width the window drag area keeps at least, in device-independent pixels.</summary>
    public const double MinDragArea = 48;

    /// <summary>The width of the "all tabs" button shown next to a lone active tab, in device-independent pixels.</summary>
    public const double AllTabsButtonWidth = 50;

    /// <summary>Computes the tab width for a strip.</summary>
    /// <param name="tabCount">The number of open tabs.</param>
    /// <param name="availableWidth">
    /// The width for the tabs and the drag area together: the window width minus the app icon, the "open file"
    /// button and, on Windows, the window buttons. Zero, negative and NaN values are treated as no space.
    /// </param>
    /// <returns>
    /// The width of each shown tab, and whether only the active tab is shown (all tabs do not fit at
    /// <see cref="MinTabWidth"/>). With only the active tab, the width is what the drag area and the "all tabs"
    /// button leave, but never below <see cref="MinTabWidth"/>.
    /// </returns>
    public static (double TabWidth, bool ShowOnlyActive) Compute(int tabCount, double availableWidth)
    {
        var available = double.IsNaN(availableWidth) ? 0 : Math.Max(availableWidth, 0);
        if (tabCount <= 0)
        {
            return (MaxTabWidth, false);
        }

        var forTabs = available - MinDragArea;
        if (forTabs >= tabCount * MinTabWidth)
        {
            return (Math.Clamp(forTabs / tabCount, MinTabWidth, MaxTabWidth), false);
        }

        var forActive = available - MinDragArea - AllTabsButtonWidth;
        return (Math.Clamp(forActive, MinTabWidth, MaxTabWidth), true);
    }
}
