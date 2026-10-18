using System.Windows.Input;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>The full-text search of the tab (design system 7.4): its view model, the panel it opens and the matches it shows.</summary>
public sealed partial class TabViewModel
{
    // The search of this tab: created when it is first needed (Search), never in the startup path.
    private SearchViewModel? _search;

    // The panel shown before "Search" opened, shown again when the search ends (Esc in the search field).
    private SidePanelKind _panelBeforeSearch;

    // Ctrl+F asked for the focus in the search field; the panel takes it once it is shown.
    private bool _searchFocusPending;

    /// <summary>
    /// Raised when the document view should bring a search match into view: the 0-based page and the rectangle of the
    /// match in page points from the top left of the page. The document view scrolls to it unless it is fully visible, and
    /// makes its page the current one.
    /// </summary>
    public event Action<int, PageRect>? RevealRequested;

    /// <summary>Raised when the search field should take the keyboard focus with its content selected (Ctrl+F).</summary>
    internal event Action? SearchFocusRequested;

    /// <summary>Raised when the document view should take the keyboard focus (the search ended with Esc in its field).</summary>
    internal event Action? DocumentFocusRequested;

    /// <summary>
    /// Gets the search of this tab, created on the first access (only the "Search" panel and Ctrl+F get here, never the
    /// startup path). The tab disposes it; a new document clears it.
    /// </summary>
    public SearchViewModel Search
    {
        get
        {
            if (_search is null)
            {
                _search = new SearchViewModel(this);
                SearchCreated?.Invoke();
            }

            return _search;
        }
    }

    /// <summary>Raised once, when <see cref="Search"/> was created: the document view then listens to its highlights.</summary>
    internal event Action? SearchCreated;

    /// <summary>F3: the next match of the search, also with the panel closed; without a search it does nothing.</summary>
    public ICommand SearchNextCommand { get; }

    /// <summary>Shift+F3: the previous match of the search, also with the panel closed; without a search it does nothing.</summary>
    public ICommand SearchPreviousCommand { get; }

    /// <summary>Gets whether <see cref="Search"/> was created (for tests: nothing creates it before it is needed).</summary>
    internal bool IsSearchCreated => _search is not null;

    /// <summary>Gets whether Ctrl+F asked for the focus in the search field and the panel has not given it yet.</summary>
    internal bool IsSearchFocusPending => _searchFocusPending;

    /// <summary>
    /// Ctrl+F (through <see cref="MainViewModel.OpenSearchCommand"/>): opens the "Search" panel and asks for the focus in
    /// its field; when it is open already, the focus goes back to the field with its content selected.
    /// </summary>
    internal void OpenSearch()
    {
        if (_disposed)
        {
            return;
        }

        SidePanel = SidePanelKind.Search;
        _searchFocusPending = true;
        SearchFocusRequested?.Invoke();
    }

    /// <summary>The search panel gave the focus to its field: the request of Ctrl+F is done.</summary>
    internal void ClearSearchFocusRequest() => _searchFocusPending = false;

    /// <summary>
    /// Shows a search match: asks the view to bring its rectangle into view (<see cref="RevealRequested"/>; without a view
    /// the page is made the current one with <see cref="GoToPage"/>); a match chosen in the panel (<paramref name="fromPanel"/>) then raises
    /// <see cref="PanelPagePicked"/>, so a narrow window closes the panel.
    /// </summary>
    internal void RevealSearchHit(SearchHit hit, bool fromPanel)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (!IsReady || hit.PageIndex < 0 || hit.PageIndex >= PageCount)
        {
            return;
        }

        if (hit.Rects.Count > 0 && RevealRequested is { } reveal)
        {
            // The view scrolls to the rectangle (not to the top of the page) and only when it is not fully visible; the
            // page it ends on becomes the current page of the tab. A jump to the page first would scroll twice.
            var rect = hit.Rects[0];
            for (var i = 1; i < hit.Rects.Count; i++)
            {
                rect = rect.Union(hit.Rects[i]);
            }

            reveal(hit.PageIndex, rect);
        }
        else
        {
            // No view shows this tab (or the match has no geometry): at least the page is the current one.
            GoToPage(hit.PageIndex + 1);
        }

        if (fromPanel)
        {
            PanelPagePicked?.Invoke();
        }
    }

    /// <summary>
    /// The search ended (Esc in the search field): shows the panel that was open before "Search" again and gives the
    /// keyboard focus to the document, since the field that had it goes with the panel.
    /// </summary>
    internal void EndSearchPanel()
    {
        if (SidePanel == SidePanelKind.Search)
        {
            SidePanel = _panelBeforeSearch;
        }

        DocumentFocusRequested?.Invoke();
    }
}
