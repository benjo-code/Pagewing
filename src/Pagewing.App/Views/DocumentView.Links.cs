using Avalonia;
using Pagewing.App.ViewModels;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// Links of the document: a click on a link (press and release of the left button on the same link, without
/// modifiers and with less than <see cref="ClickSlop"/> of movement; see DocumentView.Text.cs) is handed to the tab
/// (<see cref="TabViewModel.FollowLink"/>), never acted on here. The view shows the jumps the tab asks for and tells it
/// where the document is shown, for its history (Alt+Left, Alt+Right).
/// </summary>
public sealed partial class DocumentView
{
    private void ConnectLinks(TabViewModel tab)
    {
        tab.CurrentViewReader = CurrentView;
        tab.LinkJumpRequested += OnLinkJumpRequested;
        tab.ViewRestoreRequested += OnViewRestoreRequested;
    }

    private void DisconnectLinks(TabViewModel tab)
    {
        if (tab.CurrentViewReader == CurrentView)
        {
            tab.CurrentViewReader = null;
        }

        tab.LinkJumpRequested -= OnLinkJumpRequested;
        tab.ViewRestoreRequested -= OnViewRestoreRequested;
    }

    /// <summary>
    /// Where the shown document is now, as <see cref="TabViewModel.SavedView"/> keeps it: the anchor at the top of the
    /// viewport and the distance of the viewport's centre from the centre line of the pages. <see langword="null"/>
    /// without a shown document.
    /// </summary>
    private ViewState? CurrentView()
    {
        if (_layout is not { } layout || _document is null)
        {
            return null;
        }

        var offset = Scroller.Offset;
        var viewportCentre = offset.X - CentringShift(layout) + (Scroller.Viewport.Width / 2);
        return new ViewState(layout.GetAnchor(offset.Y), viewportCentre - (layout.ContentWidth / 2));
    }

    /// <summary>
    /// Shows the target of a link: its page (in the single-page layout that page alone), with the target point
    /// (<see cref="PageTarget.Top"/>, points from the top of the page) at the margin below the top of the viewport, or
    /// the top of the page without one; as near as the content lets it be. A fit mode first fits that row, as a page
    /// jump does. The horizontal position stays.
    /// </summary>
    private void OnLinkJumpRequested(PageTarget target)
    {
        if (_layout is not { } layout || target.PageIndex < 0 || target.PageIndex >= layout.PageCount)
        {
            return;
        }

        var index = target.PageIndex;
        _pendingRestore = null;
        if (layout.Mode == PageLayoutMode.SinglePage)
        {
            layout.ShowPage(index);
        }

        ShowCurrentRow(index);
        Refit(chosen: false);

        var rect = layout.GetPageRect(index);
        var y = rect.Y - layout.PageMargin;
        if (target.Top is { } top && double.IsFinite(top))
        {
            var height = layout.GetPageSize(index).Height;
            y += Math.Clamp(top, 0, height) * DocumentLayout.DipsPerPoint * layout.Zoom;
        }

        var max = Math.Max(0, layout.ContentHeight - Scroller.Viewport.Height);
        ChangeLayout(new Vector(Scroller.Offset.X, Math.Clamp(y, 0, max)));

        // Near the end the target cannot reach the top; its page still is the current one.
        ShowCurrentRow(index);
    }

    /// <summary>Shows a view of the history again: its page (in the single-page layout that page alone) and position.</summary>
    private void OnViewRestoreRequested(ViewState view)
    {
        if (_layout is not { } layout || view.Anchor.Page < 0 || view.Anchor.Page >= layout.PageCount)
        {
            return;
        }

        _pendingRestore = null;
        if (layout.Mode == PageLayoutMode.SinglePage)
        {
            layout.ShowPage(view.Anchor.Page);
        }

        ChangeLayout(RestoredOffset(layout, view));
    }

    /// <summary>
    /// The link a release follows: the press (a single click) went down on a link that can be followed
    /// (<see cref="PressKind.Link"/>) without modifiers, the pointer moved less than <see cref="ClickSlop"/> all the while, and it is released, without
    /// modifiers, over the same link of the same page. Otherwise <see langword="null"/>.
    /// </summary>
    private PageLink? LinkToFollow(TextPress press, Point position, Avalonia.Input.KeyModifiers modifiers)
    {
        // Only a single click: the second press of a double click may land on another link where the first one jumped to.
        if (press.Kind != PressKind.Link || press.ClickCount != 1 || press.Moved || Distance(position, press.Start) >= ClickSlop
            || press.Modifiers != Avalonia.Input.KeyModifiers.None || modifiers != Avalonia.Input.KeyModifiers.None
            || press.Link is not { } link || press.Text is not { } text
            || PageCanvas.PagePointAt(position) is not { } hit || hit.Page != press.Page)
        {
            return null;
        }

        return ReferenceEquals(text.LinkAt(hit.Position.X, hit.Position.Y), link) ? link : null;
    }
}
