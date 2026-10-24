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
/// changed annotation gets a new entry. UI thread only.
/// </summary>
public sealed class CommentItemViewModel : ViewModelBase
{
    // The formats of the texts, parsed once per language.
    private static (CultureInfo Culture, CompositeFormat Other, CompositeFormat Name, CompositeFormat Color)? _formats;

    private bool _isCurrent;
    private bool _isViewOnlyInMode;

    /// <summary>Creates the entry of <paramref name="annotation"/>; <paramref name="excerpt"/> is the text under it, or <see langword="null"/>.</summary>
    internal CommentItemViewModel(PdfAnnotation annotation, string? excerpt)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Annotation = annotation;
        var formats = Formats();
        KindText = annotation.Kind switch
        {
            AnnotationKind.Highlight => Strings.CommentKindHighlight,
            AnnotationKind.Underline => Strings.CommentKindUnderline,
            AnnotationKind.StrikeOut => Strings.CommentKindStrikeOut,
            _ => string.Format(Strings.Culture, formats.Other, annotation.Subtype),
        };

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

        // Only the parts that are there, in the order the entry shows them.
        var parts = new List<string>(5) { KindText };
        AddIfThere(parts, Details);
        AddIfThere(parts, Excerpt);
        AddIfThere(parts, Comment);
        AutomationName = string.Format(Strings.Culture, formats.Name, PageText, string.Join(", ", parts));
        Target = TargetOf(annotation);
    }

    /// <summary>Gets the annotation as read from the document.</summary>
    public PdfAnnotation Annotation { get; }

    /// <summary>Gets the 0-based page of the annotation.</summary>
    public int PageIndex => Annotation.Page;

    /// <summary>Gets the 1-based page number as the page mark shows it ("3"), like the list of the search.</summary>
    public string PageText => (Annotation.Page + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the kind of the annotation.</summary>
    public AnnotationKind Kind => Annotation.Kind;

    /// <summary>Gets whether the annotation is a highlight (its symbol: Tabler "highlight").</summary>
    public bool IsHighlight => Kind == AnnotationKind.Highlight;

    /// <summary>Gets whether the annotation is an underline (its symbol: Tabler "underline").</summary>
    public bool IsUnderline => Kind == AnnotationKind.Underline;

    /// <summary>Gets whether the annotation is a strike-out (its symbol: Tabler "strikethrough").</summary>
    public bool IsStrikeOut => Kind == AnnotationKind.StrikeOut;

    /// <summary>Gets whether the annotation is of another kind (its symbol: Tabler "note").</summary>
    public bool IsOther => Kind == AnnotationKind.Other;

    /// <summary>
    /// Gets the kind in words, next to its symbol (never by the symbol or the colour alone): "Highlight", "Underline",
    /// "Strike-out", or "Other: " with the PDF subtype of any other annotation ("Other: Ink").
    /// </summary>
    public string KindText { get; }

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

    /// <summary>Gets the name of the entry for assistive technology: "Page 3: Highlight, Alice · 01/01/2026, text, comment".</summary>
    public string AutomationName { get; }

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
    internal PageRect? Target { get; }

    /// <summary>Gets whether this entry shows <paramref name="annotation"/> with <paramref name="excerpt"/> as it is (a page read again).</summary>
    internal bool Shows(PdfAnnotation annotation, string? excerpt)
    {
        var other = Annotation;
        return other.Page == annotation.Page && other.Index == annotation.Index && other.Name == annotation.Name
            && other.Kind == annotation.Kind && other.Subtype == annotation.Subtype && other.Color == annotation.Color
            && other.Bounds == annotation.Bounds && other.Contents == annotation.Contents && other.Author == annotation.Author
            && other.Modified == annotation.Modified && other.Editable == annotation.Editable
            && other.QuadRects.SequenceEqual(annotation.QuadRects)
            && Excerpt == (string.IsNullOrWhiteSpace(excerpt) ? null : excerpt);
    }

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

    private static (CultureInfo Culture, CompositeFormat Other, CompositeFormat Name, CompositeFormat Color) Formats()
    {
        var culture = Strings.Culture;
        if (_formats is not { } cached || !ReferenceEquals(cached.Culture, culture))
        {
            cached = (culture, CompositeFormat.Parse(Strings.CommentKindOtherNamed), CompositeFormat.Parse(Strings.CommentEntryName), CompositeFormat.Parse(Strings.CommentColor));
            _formats = cached;
        }

        return cached;
    }
}
