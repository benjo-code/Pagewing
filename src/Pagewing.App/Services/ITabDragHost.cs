using Avalonia;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Services;

/// <summary>What a window's tab strip asks when a tab is pulled out of it (the <see cref="Workspace"/> in the app).</summary>
internal interface ITabDragHost
{
    /// <summary>
    /// Starts dragging <paramref name="tab"/> of <paramref name="source"/> out of its strip: the tab gets a window that
    /// follows the pointer (or, as the only tab, its own window does).
    /// </summary>
    /// <param name="source">The window the tab is in.</param>
    /// <param name="tab">The tab; it must be one of <paramref name="source"/>.</param>
    /// <param name="pointer">The pointer on the screen, in physical pixels.</param>
    /// <param name="grip">Where the pointer holds the tab: its offset from the origin of the window's content, in physical pixels.</param>
    /// <returns>The drag, or <see langword="null"/> when this tab cannot be dragged now.</returns>
    TabDrag? BeginTabDrag(MainViewModel source, TabViewModel tab, PixelPoint pointer, PixelPoint grip);
}
