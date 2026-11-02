using System.Globalization;
using Pagewing.App.Resources;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>
/// One row of the list of marks in the panel "Redact": the header of a page ("Page 3"), or a mark under it with its kind, its term and
/// its page. UI thread only.
/// </summary>
public sealed class RedactionMarkRowViewModel : ViewModelBase
{
    private const int MaxTermChars = 80;

    private bool _isSelected;

    private RedactionMarkRowViewModel(int page, RedactionMark? mark, int rasterDpi = 0)
    {
        Page = page;
        Mark = mark;
        RasterDpi = rasterDpi;
    }

    /// <summary>Gets whether this row is the header of a page (it is no mark: it cannot be selected, deleted or jumped to).</summary>
    public bool IsHeader => Mark is null && RasterDpi == 0;

    /// <summary>Gets the resolution of the row "As image" of a page that becomes an image, or 0 for any other row. The row is no mark; it shows the page.</summary>
    public int RasterDpi { get; }

    /// <summary>Gets whether this row says that its page becomes an image.</summary>
    public bool IsRaster => RasterDpi > 0;

    /// <summary>Gets the mark, or <see langword="null"/> for the header of a page.</summary>
    public RedactionMark? Mark { get; }

    /// <summary>Gets the 0-based page.</summary>
    public int Page { get; }

    /// <summary>Gets the header text, "Page 3"; empty for a mark.</summary>
    public string HeaderText => IsHeader ? Strings.RedactPageHeaderOf(Page + 1) : string.Empty;

    /// <summary>Gets the 1-based page as the page mark shows it.</summary>
    public string PageText => (Page + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Gets the kind of the mark: "Rectangle", "Selected text" or "Search hit".</summary>
    public string KindText => IsRaster ? Strings.RedactRasterRowKind(RasterDpi) : Mark?.Kind switch
    {
        RedactionKind.Text => Strings.RedactKindText,
        RedactionKind.Hit => Strings.RedactKindHit,
        RedactionKind.Area => Strings.RedactKindArea,
        _ => string.Empty,
    };

    /// <summary>Gets the term of the mark in one line, cut after 80 characters; empty for a rectangle.</summary>
    public string TermText
    {
        get
        {
            if (Mark?.Term is not { Length: > 0 } term)
            {
                return string.Empty;
            }

            var line = term.ReplaceLineEndings(" ");
            if (line.Length <= MaxTermChars)
            {
                return line;
            }

            // Not inside a surrogate pair.
            var length = char.IsHighSurrogate(line[MaxTermChars - 1]) ? MaxTermChars - 1 : MaxTermChars;
            return line[..length] + "…";
        }
    }

    /// <summary>Gets whether the mark has a term to show.</summary>
    public bool HasTerm => TermText.Length > 0;

    /// <summary>
    /// Gets or sets whether the mark is selected in the plan of the tab (an accent.subtle surface and a 2 px accent.default edge on the
    /// left, so it is not marked by colour alone).
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets the name of the row for assistive technology: the header of a page, or the kind, the page and the term of a mark.</summary>
    public string AutomationName
    {
        get
        {
            if (IsHeader)
            {
                return HeaderText;
            }

            if (IsRaster)
            {
                return Strings.RedactRasterRowName(Page + 1, RasterDpi);
            }

            var name = Strings.RedactMarkRowNameFor(KindText, PageText);
            if (HasTerm)
            {
                name += ": " + TermText;
            }

            return _isSelected ? $"{name}, {Strings.RedactRowSelected}" : name;
        }
    }

    internal static RedactionMarkRowViewModel Header(int page) => new(page, null);

    internal static RedactionMarkRowViewModel Of(RedactionMark mark) => new(mark.Page, mark);

    internal static RedactionMarkRowViewModel Raster(int page, int dpi) => new(page, null, dpi);
}
