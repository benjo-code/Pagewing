using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// One entry of the "Comments" panel: an annotation of the document with its page mark, its kind as a symbol and in words,
/// author, date, the text under it and its comment. Changed only through the editor (the panel in the mode "Comment"); a
/// changed annotation gets a new entry. Or, since milestone 10, the row of a group: the freehand strokes without a comment
/// of a page when there are five or more of them ("Freehand, 12 strokes"), which opens and closes
/// (<see cref="IsGroup"/>, <see cref="IsExpanded"/>). UI thread only.
/// </summary>
public sealed class CommentItemViewModel : ViewModelBase
{
    // The formats of the texts, parsed once per language.
    private static (CultureInfo Culture, CompositeFormat Other, CompositeFormat Name, CompositeFormat Color, CompositeFormat Group)? _formats;

    private bool _isCurrent;
    private bool _isViewOnlyInMode;
    private bool _showsLockReason;
    private bool _isExpanded;
    private int _groupCount;
    private string _kindText;
    private string _automationName;

    /// <summary>Creates the entry of <paramref name="annotation"/>; <paramref name="excerpt"/> is the text under it, or <see langword="null"/>.</summary>
    internal CommentItemViewModel(PdfAnnotation annotation, string? excerpt)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Annotation = annotation;
        var formats = Formats();
        _kindText = !IsOtherKind(annotation.Kind) && Strings.KindName(annotation.Kind) is { } kind
            ? kind
            : string.Format(Strings.Culture, formats.Other, annotation.Subtype);

        Author = string.IsNullOrWhiteSpace(annotation.Author) ? null : OneLine(annotation.Author.Trim());
        DateText = annotation.Modified?.ToLocalTime().ToString("d", Strings.Culture);
        Details = (Author, DateText) switch
        {
            ({ } author, { } date) => $"{author} · {date}",
            ({ } author, null) => author,
            (null, { } date) => date,
            _ => null,
        };

        Excerpt = string.IsNullOrWhiteSpace(excerpt) ? null : excerpt;
        Comment = string.IsNullOrWhiteSpace(annotation.Contents) ? null : OneLine(annotation.Contents.Trim());
        if (annotation.Color is { } color)
        {
            ColorBrush = new ImmutableSolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            ColorName = string.Format(Strings.Culture, formats.Color, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
        }

        // A drawing of another program that Pagewing cannot redraw without loss: only its comment and deleting are offered.
        LockReason = IsDrawingKind(annotation.Kind) && annotation.Editable && !annotation.GeometryEditable
            ? (annotation.Kind == AnnotationKind.TextBox ? Strings.SelectionLockedForeignTextBox : Strings.SelectionLockedForeign)
            : null;

        // Only the parts that are there, in the order the entry shows them.
        var parts = new List<string>(5) { _kindText };
        AddIfThere(parts, Details);
        AddIfThere(parts, Excerpt);
        AddIfThere(parts, Comment);
        _automationName = string.Format(Strings.Culture, formats.Name, PageText, string.Join(", ", parts));
        Target = TargetOf(annotation);
    }

    /// <summary>Creates the row of a group of <paramref name="count"/> freehand strokes; <paramref name="first"/> is the first of them on the page.</summary>
    private CommentItemViewModel(PdfAnnotation first, int count)
    {
        Annotation = first;
        IsGroup = true;
        _groupCount = count;
        _kindText = string.Empty;
        _automationName = string.Empty;
        Target = TargetOf(first);
        UpdateGroupTexts();
    }

    /// <summary>
    /// Gets the annotation as read from the document; for the row of a group the first stroke of the group (its page and
    /// position).
    /// </summary>
    public PdfAnnotation Annotation { get; private set; }

    /// <summary>Gets the 0-based page of the annotation.</summary>
    public int PageIndex => Annotation.Page;

    /// <summary>Gets the 1-based page number as the page mark shows it ("3"), like the list of the search.</summary>
    public string PageText => (Annotation.Page + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the kind of the annotation (of a group: freehand).</summary>
    public AnnotationKind Kind => Annotation.Kind;

    /// <summary>Gets whether the annotation is a highlight (its symbol: Tabler "highlight").</summary>
    public bool IsHighlight => Kind == AnnotationKind.Highlight;

    /// <summary>Gets whether the annotation is an underline (its symbol: Tabler "underline").</summary>
    public bool IsUnderline => Kind == AnnotationKind.Underline;

    /// <summary>Gets whether the annotation is a strike-out (its symbol: Tabler "strikethrough").</summary>
    public bool IsStrikeOut => Kind == AnnotationKind.StrikeOut;

    /// <summary>Gets whether the annotation is a freehand drawing, also of another program (its symbol: Tabler "pencil").</summary>
    public bool IsInk => Kind == AnnotationKind.Ink;

    /// <summary>Gets whether the annotation is a rectangle (its symbol: Tabler "square").</summary>
    public bool IsRectangle => Kind == AnnotationKind.Rectangle;

    /// <summary>Gets whether the annotation is an oval (its symbol: Tabler "circle").</summary>
    public bool IsEllipse => Kind == AnnotationKind.Ellipse;

    /// <summary>Gets whether the annotation is a line Pagewing recognises (its symbol: Tabler "line").</summary>
    public bool IsLine => Kind == AnnotationKind.Line;

    /// <summary>Gets whether the annotation is an arrow Pagewing recognises (its symbol: Tabler "arrow-up-right").</summary>
    public bool IsArrow => Kind == AnnotationKind.Arrow;

    /// <summary>Gets whether the annotation is a visible signature, drawn or imported (its symbol: Tabler "signature").</summary>
    public bool IsSignature => Kind == AnnotationKind.Signature;

    /// <summary>Gets whether the entry is a text box (its text is its comment).</summary>
    public bool IsTextBox => Kind == AnnotationKind.TextBox;

    /// <summary>Gets whether the annotation is a drawing: freehand, rectangle, oval, line or arrow (the filter "Drawings").</summary>
    public bool IsDrawing => IsDrawingKind(Kind);

    /// <summary>
    /// Gets whether the annotation is of another kind than a markup or a drawing (its symbol: Tabler "note"): notes, free
    /// text, stamps, a line of another program that Pagewing does not recognise.
    /// </summary>
    public bool IsOther => IsOtherKind(Kind);

    /// <summary>
    /// Gets the kind in words, next to its symbol (never by the symbol or the colour alone): "Highlight", "Freehand",
    /// "Rectangle" …, "Other: " with the PDF subtype of any other annotation ("Other: Line"); for a group "Freehand, 12 strokes".
    /// </summary>
    public string KindText
    {
        get => _kindText;
        private set => SetProperty(ref _kindText, value);
    }

    /// <summary>Gets the author, or <see langword="null"/>.</summary>
    public string? Author { get; }

    /// <summary>Gets the date of the last change as a short local date in the language of the interface, or <see langword="null"/>.</summary>
    public string? DateText { get; }

    /// <summary>Gets author and date in one line ("Alice · 01/01/2026"), or <see langword="null"/> without both.</summary>
    public string? Details { get; }

    /// <summary>Gets whether <see cref="Details"/> is there.</summary>
    public bool HasDetails => Details is not null;

    /// <summary>Gets the text of the page under a highlight, underline or strike-out, or <see langword="null"/>.</summary>
    public string? Excerpt { get; }

    /// <summary>Gets whether <see cref="Excerpt"/> is there.</summary>
    public bool HasExcerpt => Excerpt is not null;

    /// <summary>Gets the comment of the annotation in one line, or <see langword="null"/>.</summary>
    public string? Comment { get; }

    /// <summary>Gets whether <see cref="Comment"/> is there.</summary>
    public bool HasComment => Comment is not null;

    /// <summary>Gets the colour of the annotation for its swatch (content of the PDF, not a token), or <see langword="null"/>.</summary>
    public IBrush? ColorBrush { get; }

    /// <summary>Gets whether the annotation has a colour (the swatch is shown).</summary>
    public bool HasColor => ColorBrush is not null;

    /// <summary>Gets the name of the swatch for assistive technology and its tooltip ("Colour #FFE600"), or <see langword="null"/>.</summary>
    public string? ColorName { get; }

    /// <summary>
    /// Gets the name of the entry for assistive technology: "Page 3: Highlight, Alice · 01/01/2026, text, comment"; for a
    /// group "Page 3: Freehand, 12 strokes, collapsed".
    /// </summary>
    public string AutomationName
    {
        get => _automationName;
        private set => SetProperty(ref _automationName, value);
    }

    /// <summary>
    /// Gets why the drawing cannot be moved or changed (another program drew it in a way Pagewing cannot keep), or
    /// <see langword="null"/>: only its comment and deleting are offered.
    /// </summary>
    public string? LockReason { get; }

    /// <summary>
    /// Gets the help text of the row for assistive technology: for a group how it opens, for a locked drawing
    /// <see cref="LockReason"/>, else <see langword="null"/>.
    /// </summary>
    public string? HelpText => IsGroup ? Strings.CommentGroupHint : LockReason;

    /// <summary>Gets whether the row says <see cref="LockReason"/>: in the mode "Comment" only, like "View only".</summary>
    public bool ShowsLockReason
    {
        get => _showsLockReason;
        internal set => SetProperty(ref _showsLockReason, value);
    }

    /// <summary>Gets whether this is the row of a group of freehand strokes, not an annotation.</summary>
    public bool IsGroup { get; }

    /// <summary>Gets how many strokes the group has (0 for an entry).</summary>
    public int GroupCount => _groupCount;

    /// <summary>Gets whether the group is open: its strokes follow in the list (its chevron points down, not only a colour).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                UpdateGroupTexts();
            }
        }
    }

    /// <summary>
    /// Gets whether this entry was the last one jumped to: the list gives it an accent.subtle surface and a 2 px
    /// accent.default edge on the left (design system 7.4, never by colour alone).
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>
    /// Gets whether the entry shows "View only": in the mode "Comment" an annotation Pagewing does not edit (any other kind,
    /// or a markup with replies). Never outside the mode.
    /// </summary>
    public bool IsViewOnlyInMode
    {
        get => _isViewOnlyInMode;
        internal set => SetProperty(ref _isViewOnlyInMode, value);
    }

    /// <summary>
    /// Gets the area a jump shows: the quadrilaterals of the annotation as one rectangle, else its bounds; <see langword="null"/>
    /// when neither has a size (the jump then only shows the page).
    /// </summary>
    internal PageRect? Target { get; private set; }

    /// <summary>Whether the kind is a drawing (freehand, rectangle, oval, line, arrow).</summary>
    internal static bool IsDrawingKind(AnnotationKind kind) =>
        kind is AnnotationKind.Ink or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.TextBox or AnnotationKind.Signature;

    /// <summary>Creates the row of a group of strokes.</summary>
    internal static CommentItemViewModel Group(PdfAnnotation first, int count) => new(first, count);

    /// <summary>The group got other strokes after a page was read again: its count and first stroke follow.</summary>
    internal void UpdateGroup(PdfAnnotation first, int count)
    {
        Annotation = first;
        Target = TargetOf(first);
        if (_groupCount != count)
        {
            _groupCount = count;
            OnPropertyChanged(nameof(GroupCount));
            UpdateGroupTexts();
        }
    }

    /// <summary>Gets whether this entry shows <paramref name="annotation"/> with <paramref name="excerpt"/> as it is (a page read again).</summary>
    internal bool Shows(PdfAnnotation annotation, string? excerpt)
    {
        var other = Annotation;
        return !IsGroup && other.Page == annotation.Page && other.Index == annotation.Index && other.Name == annotation.Name
            && other.Kind == annotation.Kind && other.Subtype == annotation.Subtype && other.Color == annotation.Color
            && other.Bounds == annotation.Bounds && other.Contents == annotation.Contents && other.Author == annotation.Author
            && other.Modified == annotation.Modified && other.Editable == annotation.Editable
            && other.GeometryEditable == annotation.GeometryEditable
            && other.QuadRects.SequenceEqual(annotation.QuadRects)
            && Excerpt == (string.IsNullOrWhiteSpace(excerpt) ? null : excerpt);
    }

    private static bool IsOtherKind(AnnotationKind kind) =>
        kind is not (AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut) && !IsDrawingKind(kind);

    private static PageRect? TargetOf(PdfAnnotation annotation)
    {
        PageRect? area = null;
        foreach (var rect in annotation.QuadRects)
        {
            area = area is { } union ? union.Union(rect) : rect;
        }

        if (area is not { } found || !HasSize(found))
        {
            area = annotation.Bounds;
        }

        return HasSize(area.Value) ? area : null;
    }

    private static bool HasSize(PageRect rect) =>
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width > 0 && rect.Height > 0;

    private static void AddIfThere(List<string> parts, string? part)
    {
        if (part is not null)
        {
            parts.Add(part);
        }
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    private static (CultureInfo Culture, CompositeFormat Other, CompositeFormat Name, CompositeFormat Color, CompositeFormat Group) Formats()
    {
        var culture = Strings.Culture;
        if (_formats is not { } cached || !ReferenceEquals(cached.Culture, culture))
        {
            cached = (culture, CompositeFormat.Parse(Strings.CommentKindOtherNamed), CompositeFormat.Parse(Strings.CommentEntryName),
                CompositeFormat.Parse(Strings.CommentColor), CompositeFormat.Parse(Strings.CommentInkGroup));
            _formats = cached;
        }

        return cached;
    }

    private void UpdateGroupTexts()
    {
        if (!IsGroup)
        {
            return;
        }

        var formats = Formats();
        KindText = string.Format(Strings.Culture, formats.Group, _groupCount);
        var state = _isExpanded ? Strings.CommentGroupExpanded : Strings.CommentGroupCollapsed;
        AutomationName = string.Format(Strings.Culture, formats.Name, PageText, $"{KindText}, {state}");
    }
}
