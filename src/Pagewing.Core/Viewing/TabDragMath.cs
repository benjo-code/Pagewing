namespace Pagewing.Core.Viewing;

/// <summary>
/// The arithmetic of dragging a tab out of the tab strip and onto another one: when the tab comes loose, and where
/// it goes in. Pure functions, no UI.
/// </summary>
public static class TabDragMath
{
    /// <summary>
    /// How far the pointer has to leave the strip (up or down from where the tab was pressed) before the tab comes
    /// loose into a window of its own, in device-independent pixels.
    /// </summary>
    public const double DetachDistance = 40;

    /// <summary>Gets whether a pressed tab comes loose: the pointer left the window, or is more than <see cref="DetachDistance"/> off.</summary>
    /// <param name="verticalDistance">The pointer's distance from where the tab was pressed, across the strip; NaN counts as none.</param>
    /// <param name="pointerOutsideWindow">Whether the pointer is outside the client area of the window.</param>
    public static bool ShouldDetach(double verticalDistance, bool pointerOutsideWindow) =>
        pointerOutsideWindow || Math.Abs(verticalDistance) > DetachDistance;

    /// <summary>
    /// Gets the position a dropped tab takes among <paramref name="tabCount"/> tabs: the boundary between slots that
    /// the pointer is nearest to, 0 (before the first) up to <paramref name="tabCount"/> (after the last).
    /// </summary>
    /// <param name="x">The pointer's x from the left edge of the first tab.</param>
    /// <param name="tabWidth">The width of each tab; zero, negative and NaN give 0.</param>
    /// <param name="tabCount">The number of tabs; zero or fewer gives 0.</param>
    public static int InsertionIndex(double x, double tabWidth, int tabCount)
    {
        if (tabCount <= 0 || !(tabWidth > 0) || double.IsNaN(x))
        {
            return 0;
        }

        return (int)Math.Clamp(Math.Floor((x / tabWidth) + 0.5), 0, tabCount);
    }
}
