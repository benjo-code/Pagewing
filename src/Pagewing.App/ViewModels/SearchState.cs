namespace Pagewing.App.ViewModels;

/// <summary>How far the search of a tab is (<see cref="SearchViewModel.State"/>).</summary>
public enum SearchState
{
    /// <summary>No search: the query is empty, or the search was ended.</summary>
    Idle,

    /// <summary>The search is about to start (the query is still being typed) or runs; the list fills while it runs.</summary>
    Searching,

    /// <summary>The search went through the whole document or stopped at <see cref="Core.Text.DocumentSearch.MaxHits"/>.</summary>
    Done,
}
