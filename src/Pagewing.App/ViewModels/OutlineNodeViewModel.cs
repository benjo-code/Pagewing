using System.Globalization;
using System.Text;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>
/// One entry of the table of contents (a bookmark of the PDF) in the "Contents" panel. The view models of the children
/// are created only when they are first asked for (the tree shows the entry), so an outline of up to
/// <see cref="PdfDocument.MaxOutlineItems"/> bookmarks costs only what is shown. UI thread only.
/// </summary>
public sealed class OutlineNodeViewModel : ViewModelBase
{
    private readonly PdfOutlineItem _item;
    private IReadOnlyList<OutlineNodeViewModel>? _children;
    private bool _isExpanded;

    /// <summary>Creates the entry of <paramref name="item"/>; its children follow on demand.</summary>
    public OutlineNodeViewModel(PdfOutlineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;
        _isExpanded = item.IsOpen;

        // Line breaks in a title would make a row taller than the others.
        var title = (item.Title ?? string.Empty).ReplaceLineEndings(" ").Trim();
        Title = title.Length == 0 ? Strings.OutlineNoTitle : title;
    }

    /// <summary>Gets the title in one line; "(untitled)" for a bookmark without one.</summary>
    public string Title { get; }

    /// <summary>Gets the 0-based page the bookmark points at, or <see langword="null"/> without a valid target in this document.</summary>
    public int? PageIndex => _item.PageIndex;

    /// <summary>Gets whether choosing the entry shows a page (it has a target).</summary>
    public bool CanJump => _item.PageIndex is not null;

    /// <summary>Gets the 1-based page number as shown on the right of the entry; empty without a target.</summary>
    public string PageText => _item.PageIndex is { } index ? (index + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Gets the entries one level below, created the first time they are asked for.</summary>
    public IReadOnlyList<OutlineNodeViewModel> Children => _children ??= Create(_item.Children);

    /// <summary>Gets or sets whether the children are shown; at first as the document asks (<see cref="PdfOutlineItem.IsOpen"/>).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Gets the name for assistive technology: "Title, Page 3", or "Title, No destination in this document".</summary>
    public string AutomationName => $"{Title}, {(CanJump ? string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ThumbnailName), PageText) : Strings.OutlineNoTarget)}";

    /// <summary>Gets the tooltip of an entry without a target (why it does nothing), or <see langword="null"/>.</summary>
    public string? ToolTip => CanJump ? null : Strings.OutlineNoTarget;

    /// <summary>Gets whether the view models of the children exist yet (for tests).</summary>
    internal bool AreChildrenCreated => _children is not null;

    /// <summary>The view models of one level of bookmarks.</summary>
    internal static IReadOnlyList<OutlineNodeViewModel> Create(IReadOnlyList<PdfOutlineItem> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var nodes = new OutlineNodeViewModel[items.Count];
        for (var index = 0; index < nodes.Length; index++)
        {
            nodes[index] = new OutlineNodeViewModel(items[index]);
        }

        return nodes;
    }
}
