using Avalonia;
using Avalonia.Input;
using Pagewing.App.ViewModels;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// The layouts of the tab (<see cref="TabViewModel.LayoutMode"/>): a change keeps the current page, the current page is
/// the first page of the most visible row, and the single page turns with Space and the mouse wheel at its edges.
/// </summary>
public sealed partial class DocumentView
{
    /// <summary>DIPs from an edge within which the scroll position counts as being at that edge.</summary>
    private const double EdgeTolerance = 0.5;

    // Wheel steps toward an edge at which the single page already is; a whole step turns the page.
    private double _flipSteps;

    private bool IsSinglePage => _layout?.Mode == PageLayoutMode.SinglePage;

    private bool IsAtTop => Scroller.Offset.Y <= EdgeTolerance;

    private bool IsAtBottom => Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - EdgeTolerance;

    /// <summary>
    /// Arranges the pages as the tab says and keeps the current page: in the single-page layout that page alone from its
    /// top, otherwise the current page where it was in the viewport (and the same spot in the middle horizontally). A
    /// fit mode then fits the new rows (two pages fill the width as a spread).
    /// </summary>
    private void ApplyLayoutMode()
    {
        if (_viewModel is not { } tab || _layout is not { PageCount: > 0 } layout || layout.Mode == tab.LayoutMode)
        {
            return;
        }

        var page = Math.Clamp(tab.CurrentPage - 1, 0, layout.PageCount - 1);
        var offset = Scroller.Offset;
        var anchor = new PageAnchor(page, 0);
        if (layout.IsShown(page))
        {
            var rect = layout.GetPageRect(page);
            anchor = new PageAnchor(page, (offset.Y - rect.Y) / rect.Height);
        }

        var fromCentre = offset.X - CentringShift(layout) + (Scroller.Viewport.Width / 2) - (layout.ContentWidth / 2);

        layout.Mode = tab.LayoutMode;
        var y = 0.0;
        if (layout.Mode == PageLayoutMode.SinglePage)
        {
            layout.ShowPage(page);
        }
        else
        {
            y = layout.GetOffset(anchor);
        }

        ChangeLayout(new Vector(fromCentre + (layout.ContentWidth / 2) - (Scroller.Viewport.Width / 2) + CentringShift(layout), y));
        ShowCurrentRow(page);
        Refit(chosen: false);
    }

    /// <summary>Makes the row of <paramref name="page"/> (0-based) the current one: its first and its last page.</summary>
    private void ShowCurrentRow(int page)
    {
        if (_viewModel is not { } tab || _layout is not { } layout || page < 0 || page >= layout.PageCount)
        {
            return;
        }

        var (first, last) = layout.PagesOfRow(layout.RowOf(page));
        tab.CurrentPage = first + 1;
        tab.CurrentPageLast = last + 1;
    }

    /// <summary>
    /// Space at the end of the single page shows the next page, Shift+Space at its start the end of the page before.
    /// Anywhere else Space scrolls (<see cref="ScrollFor"/>).
    /// </summary>
    private bool TryTurnPageWithSpace(Key key, KeyModifiers modifiers) =>
        key == Key.Space && IsSinglePage && modifiers switch
        {
            KeyModifiers.None => IsAtBottom && TurnPage(1),
            KeyModifiers.Shift => IsAtTop && TurnPage(-1),
            _ => false,
        };

    /// <summary>
    /// A wheel step (without modifiers) toward an edge at which the single page already is turns the page: down at the
    /// end shows the next page from its top, up at the start the page before from its end. The step that reaches the
    /// edge only scrolls: the scroll viewer handles it after this tunnelling handler.
    /// </summary>
    /// <returns>Whether the wheel event belongs to the page turn (the scroll viewer would not scroll with it anyway).</returns>
    private bool TryTurnPageWithWheel(PointerWheelEventArgs e)
    {
        var delta = e.Delta;
        var direction = Math.Abs(delta.Y) > Math.Abs(delta.X) ? -Math.Sign(delta.Y) : 0;
        if (!IsSinglePage || e.KeyModifiers != KeyModifiers.None || direction == 0 || !(direction > 0 ? IsAtBottom : IsAtTop))
        {
            _flipSteps = 0;
            return false;
        }

        // A wheel notch is 1; touchpads send fractions, which add up to a whole step.
        if (Math.Sign(delta.Y) != Math.Sign(_flipSteps))
        {
            _flipSteps = 0;
        }

        _flipSteps += delta.Y;
        if (Math.Abs(_flipSteps) >= 1)
        {
            _flipSteps = 0;
            TurnPage(direction);
        }

        return true;
    }

    /// <summary>Shows the page after (1) or before (-1) the current one, the one before from its end.</summary>
    /// <returns>Whether there was such a page.</returns>
    private bool TurnPage(int direction)
    {
        if (_viewModel is not { } tab || _layout is not { } layout)
        {
            return false;
        }

        var page = tab.CurrentPage + direction;
        if (page < 1 || page > layout.PageCount)
        {
            return false;
        }

        // Through the tab, so the page field and the arrows follow; it shows the page from its top.
        tab.GoToPage(page);
        if (direction < 0)
        {
            ScrollTo(new Vector(Scroller.Offset.X, Scroller.Extent.Height));
        }

        return true;
    }
}
