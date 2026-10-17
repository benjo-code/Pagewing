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
    // The format of the automation name, parsed once per language.
    private static (CultureInfo Culture, CompositeFormat Format)? _automationFormat;

    private readonly PdfOutlineItem _item;
    private IReadOnlyList<OutlineNodeViewModel>? _children;
    private bool _isExpanded;

    /// <summary>Creates the entry of <paramref name="item"/> on the top level; its children follow on demand.</summary>
    public OutlineNodeViewModel(PdfOutlineItem item)
        : this(item, parent: null, index: 0)
    {
    }

    private OutlineNodeViewModel(PdfOutlineItem item, OutlineNodeViewModel? parent, int index)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;
        _isExpanded = item.IsOpen;
        Parent = parent;
        Index = index;

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
    public IReadOnlyList<OutlineNodeViewModel> Children => _children ??= Create(_item.Children, this);

    /// <summary>Gets whether the entry has entries below it (without creating their view models).</summary>
    public bool HasChildren => _item.Children.Count > 0;

    /// <summary>Gets or sets whether the children are shown; at first as the document asks (<see cref="PdfOutlineItem.IsOpen"/>).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Gets the name for assistive technology: "Title, page 3", or "Title, No destination in this document".</summary>
    public string AutomationName => CanJump
        ? string.Format(Strings.Culture, AutomationFormat(), Title, PageText)
        : $"{Title}, {Strings.OutlineNoTarget}";

    /// <summary>Gets the tooltip of an entry without a target (why it does nothing), or <see langword="null"/>.</summary>
    public string? ToolTip => CanJump ? null : Strings.OutlineNoTarget;

    /// <summary>Gets the entry one level up, or <see langword="null"/> on the top level.</summary>
    internal OutlineNodeViewModel? Parent { get; }

    /// <summary>Gets the position of the entry among the entries of its level.</summary>
    internal int Index { get; }

    /// <summary>Gets whether the view models of the children exist yet (for tests).</summary>
    internal bool AreChildrenCreated => _children is not null;

    /// <summary>The view models of the top level of an outline.</summary>
    internal static IReadOnlyList<OutlineNodeViewModel> Create(IReadOnlyList<PdfOutlineItem> items) => Create(items, parent: null);

    /// <summary>The last entry the tree shows: the last one of the top level, or of its open children, and so on.</summary>
    internal static OutlineNodeViewModel? Last(IReadOnlyList<OutlineNodeViewModel> roots)
    {
        var node = roots.Count > 0 ? roots[^1] : null;
        while (node is { IsExpanded: true, HasChildren: true })
        {
            node = node.Children[^1];
        }

        return node;
    }

    /// <summary>The entry the tree shows below this one (its first child if it is open), or <see langword="null"/> after the last one.</summary>
    internal OutlineNodeViewModel? Next(IReadOnlyList<OutlineNodeViewModel> roots)
    {
        if (IsExpanded && HasChildren)
        {
            return Children[0];
        }

        for (var node = this; node is not null; node = node.Parent)
        {
            var siblings = node.Parent?.Children ?? roots;
            if (node.Index + 1 < siblings.Count)
            {
                return siblings[node.Index + 1];
            }
        }

        return null;
    }

    /// <summary>The entry the tree shows above this one (the last shown entry of the previous sibling), or <see langword="null"/> before the first one.</summary>
    internal OutlineNodeViewModel? Previous(IReadOnlyList<OutlineNodeViewModel> roots)
    {
        if (Index == 0)
        {
            return Parent;
        }

        var node = (Parent?.Children ?? roots)[Index - 1];
        while (node is { IsExpanded: true, HasChildren: true })
        {
            node = node.Children[^1];
        }

        return node;
    }

    private static OutlineNodeViewModel[] Create(IReadOnlyList<PdfOutlineItem> items, OutlineNodeViewModel? parent)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var nodes = new OutlineNodeViewModel[items.Count];
        for (var index = 0; index < nodes.Length; index++)
        {
            nodes[index] = new OutlineNodeViewModel(items[index], parent, index);
        }

        return nodes;
    }

    private static CompositeFormat AutomationFormat()
    {
        var culture = Strings.Culture;
        if (_automationFormat is not { } cached || !ReferenceEquals(cached.Culture, culture))
        {
            cached = (culture, CompositeFormat.Parse(Strings.OutlineEntryName));
            _automationFormat = cached;
        }

        return cached.Format;
    }
}
