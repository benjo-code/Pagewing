namespace Pagewing.App.ViewModels;

/// <summary>The side panel a tab shows next to its document.</summary>
public enum SidePanelKind
{
    /// <summary>No panel.</summary>
    None,

    /// <summary>The page thumbnails.</summary>
    Pages,

    /// <summary>The table of contents (bookmarks).</summary>
    Contents,

    /// <summary>The full-text search with its list of matches.</summary>
    Search,

    /// <summary>The annotations of the document (milestone 9), read when the panel first opens.</summary>
    Comments,

    /// <summary>The marks of the mode "Redact" and "find and redact" (milestone 14); shown only while that mode is on, made when it first opens.</summary>
    Redact,
}
