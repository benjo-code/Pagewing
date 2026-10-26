namespace Pagewing.App.ViewModels;

/// <summary>Which annotations the "Comments" panel lists.</summary>
public enum CommentFilter
{
    /// <summary>Every annotation that is a comment.</summary>
    All,

    /// <summary>Only highlights.</summary>
    Highlight,

    /// <summary>Only underlines.</summary>
    Underline,

    /// <summary>Only strike-outs.</summary>
    StrikeOut,

    /// <summary>Only drawings: freehand, rectangles, ovals, lines and arrows (since milestone 10).</summary>
    Drawings,

    /// <summary>Only the other annotations (notes, free text, stamps, lines of other programs …).</summary>
    Other,
}
