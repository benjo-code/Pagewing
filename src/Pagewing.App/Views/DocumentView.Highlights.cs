using Avalonia;
using Pagewing.App.Controls;
using Pagewing.App.ViewModels;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// The marks on the pages: the search matches of the shown tab (all of them, the current one framed), its text selection
/// (composed here, made in DocumentView.Text.cs) and the jump to a match. The marks belong to the active tab: the view listens to the search of the tab it shows and to nothing else,
/// and it never creates a search (<see cref="TabViewModel.Search"/>) that the user did not start.
/// </summary>
public sealed partial class DocumentView
{
    // The search of the shown tab while it exists; the view listens to its highlights.
    private SearchViewModel? _searchSource;

    /// <summary>Starts listening to the marks of <paramref name="tab"/>: its search now, or once it is created.</summary>
    private void ConnectHighlights(TabViewModel tab)
    {
        tab.RevealRequested += OnRevealRequested;
        tab.SearchCreated += OnSearchCreated;
        if (tab.IsSearchCreated)
        {
            ListenToSearch(tab.Search);
        }
    }

    private void DisconnectHighlights()
    {
        if (_viewModel is { } tab)
        {
            tab.RevealRequested -= OnRevealRequested;
            tab.SearchCreated -= OnSearchCreated;
        }

        if (_searchSource is not null)
        {
            _searchSource.HighlightsChanged -= RefreshHighlights;
            _searchSource = null;
        }
    }

    private void OnSearchCreated()
    {
        if (_viewModel is { } tab)
        {
            ListenToSearch(tab.Search);
        }
    }

    private void ListenToSearch(SearchViewModel search)
    {
        if (ReferenceEquals(_searchSource, search))
        {
            return;
        }

        if (_searchSource is not null)
        {
            _searchSource.HighlightsChanged -= RefreshHighlights;
        }

        _searchSource = search;
        search.HighlightsChanged += RefreshHighlights;
        RefreshHighlights();
    }

    /// <summary>
    /// Hands the canvas everything that is marked on the pages, replacing what it had. The one place that composes the
    /// marks: the text selection of the tab on its visible pages (DocumentView.Text.cs) and the search matches.
    /// </summary>
    private void RefreshHighlights()
    {
        var marks = new List<PageHighlight>();
        AddSelectionMarks(marks);
        if (_layout is not null && _searchSource is { } search)
        {
            var hits = search.Highlights;
            marks.EnsureCapacity(hits.Count);
            foreach (var (page, rect, isCurrent) in hits)
            {
                marks.Add(new PageHighlight(page, rect, isCurrent ? HighlightKind.SearchCurrent : HighlightKind.SearchHit));
            }
        }

        PageCanvas.SetHighlights(marks);
    }

    /// <summary>
    /// Brings a rectangle of a page (0-based page, PDF points from the top left) into view, for a search match. Fully
    /// inside the viewport already: nothing scrolls. Otherwise the view scrolls so that the middle of the rectangle is in
    /// the middle of the viewport (as far as the content allows) and sideways only as far as needed. In the single-page
    /// layout the page of the match is shown first. The page of the match becomes the current page; a match in another
    /// row fits that row first in a fit mode (design system 7.5: the modes fit again on a jump to a page).
    /// </summary>
    private void OnRevealRequested(int page, PageRect rect)
    {
        var layout = _layout;
        if (layout is null || page < 0 || page >= layout.PageCount)
        {
            return;
        }

        var viewport = Scroller.Viewport;
        if (!(viewport.Width > 0 && viewport.Height > 0))
        {
            // Not laid out yet: at least the page is shown and current, like a page jump.
            OnPageRequested(page + 1);
            return;
        }

        // The match replaces a saved view that still waits for the viewport.
        _pendingRestore = null;
        if (layout.Mode == PageLayoutMode.SinglePage && !layout.IsShown(page))
        {
            // The content is another page: shown from its top; the rectangle is placed below.
            layout.ShowPage(page);
            ShowCurrentRow(page);
            Refit(chosen: false);
            ChangeLayout(new Vector(Scroller.Offset.X, layout.GetPageRect(page).Y - layout.PageMargin));
        }
        else if (_viewModel is { } tab && (page < tab.CurrentPage - 1 || page > Math.Max(tab.CurrentPage, tab.CurrentPageLast) - 1))
        {
            // Another row: it becomes the current one and a fit mode fits it, before the rectangle is placed.
            ShowCurrentRow(page);
            Refit(chosen: false);
        }

        viewport = Scroller.Viewport;
        if (viewport.Width > 0 && viewport.Height > 0 && layout.IsShown(page))
        {
            var pageRect = layout.GetPageRect(page);
            var dipsPerPoint = DocumentLayout.DipsPerPoint * layout.Zoom;
            var left = pageRect.X + (rect.Left * dipsPerPoint);
            var right = pageRect.X + (rect.Right * dipsPerPoint);
            var top = pageRect.Y + (rect.Top * dipsPerPoint);
            var bottom = pageRect.Y + (rect.Bottom * dipsPerPoint);

            var offset = Scroller.Offset;
            var visibleLeft = offset.X - CentringShift(layout);
            var visibleRight = visibleLeft + viewport.Width;
            var inside = left >= visibleLeft && right <= visibleRight && top >= offset.Y && bottom <= offset.Y + viewport.Height;
            if (!inside)
            {
                var dx = 0.0;
                if (right - left > viewport.Width || left < visibleLeft)
                {
                    dx = left - visibleLeft;
                }
                else if (right > visibleRight)
                {
                    dx = right - visibleRight;
                }

                ScrollTo(new Vector(offset.X + dx, ((top + bottom) / 2) - (viewport.Height / 2)));
            }
        }

        // After the scroll, which sets the most visible page: the page of the match is the current one.
        ShowCurrentRow(page);
    }
}
