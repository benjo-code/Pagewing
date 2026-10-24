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

    /// <summary>Only the other annotations (notes, ink, free text, stamps …).</summary>
    Other,
}
