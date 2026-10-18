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
}
