using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// The marks of a session: nothing is written until "Apply". A plan is immutable; every operation returns a new plan (or the
/// same plan if there is nothing to do) and throws <see cref="RedactionException"/>, leaving the plan as it is, if it is
/// refused. Marks are copied when they enter, so a caller that changes its list later does not change the plan. A plan knows
/// nothing of PDFium or of page sizes. Every limit is checked before anything is allocated or enumerated. Besides the marks it
/// holds the settings that apply to all of them (fill, treatment of images, style of the overlay text) and the pages that are
/// turned into raster pages.
/// </summary>
public sealed class RedactionPlan
{
    private readonly RedactionMark[] _marks;
    private readonly FrozenSet<int> _rasterPages;

    private RedactionPlan(
        RedactionMark[] marks,
        RedactionFill fill,
        RedactionImageTreatment imageTreatment,
        RedactionImageStrength imageStrength,
        RedactionOverlayStyle overlay,
        FrozenSet<int> rasterPages,
        int rasterDpi)
    {
        _marks = marks;
        Marks = new ReadOnlyCollection<RedactionMark>(marks);
        Fill = fill;
        ImageTreatment = imageTreatment;
        ImageStrength = imageStrength;
        Overlay = overlay;
        _rasterPages = rasterPages;
        RasterDpi = rasterDpi;
        var pages = new HashSet<int>();
        foreach (var mark in marks)
        {
            pages.Add(mark.Page);
        }

        Pages = pages;
    }

    /// <summary>Gets the plan without any mark or raster page and with the default settings.</summary>
    public static RedactionPlan Empty { get; } = new(
        [],
        RedactionFill.Black,
        RedactionImageTreatment.Solid,
        RedactionImageStrength.Coarse,
        new RedactionOverlayStyle(),
        FrozenSet<int>.Empty,
        RedactionLimits.DefaultRasterDpi);

    /// <summary>Gets the marks in the order they were made.</summary>
    public IReadOnlyList<RedactionMark> Marks { get; }

    /// <summary>Gets the colour of the areas.</summary>
    public RedactionFill Fill { get; }

    /// <summary>Gets what happens to the part of an image under a mark.</summary>
    public RedactionImageTreatment ImageTreatment { get; }

    /// <summary>Gets how coarse pixelating and blurring work.</summary>
    public RedactionImageStrength ImageStrength { get; }

    /// <summary>Gets how the overlay text of the marks is drawn.</summary>
    public RedactionOverlayStyle Overlay { get; }

    /// <summary>Gets the 0-based pages that are turned into raster pages (whether or not they have a mark).</summary>
    public IReadOnlySet<int> RasterPages => _rasterPages;

    /// <summary>Gets the resolution, in dots per inch, of the raster pages.</summary>
    public int RasterDpi { get; }

    /// <summary>Gets whether there is neither a mark nor a raster page (the settings alone are no change).</summary>
    public bool IsEmpty => _marks.Length == 0 && _rasterPages.Count == 0;

    /// <summary>Gets the 0-based pages that have at least one mark.</summary>
    public IReadOnlySet<int> Pages { get; }

    /// <summary>Adds a mark at the end.</summary>
    /// <param name="mark">The mark.</param>
    /// <exception cref="RedactionException">The mark is refused, or the plan would hold more than <see cref="RedactionLimits.MaxMarks"/> marks.</exception>
    /// <exception cref="ArgumentException">A mark with this <see cref="RedactionMark.Id"/> is already in the plan.</exception>
    public RedactionPlan With(RedactionMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (_marks.Length >= RedactionLimits.MaxMarks)
        {
            throw new RedactionException(RedactionError.LimitExceeded, "The plan would hold more marks than allowed.");
        }

        var normalized = Normalize(mark);
        if (IndexOf(normalized.Id) >= 0)
        {
            throw new ArgumentException("A mark with this id is already in the plan.", nameof(mark));
        }

        var marks = new RedactionMark[_marks.Length + 1];
        _marks.CopyTo(marks, 0);
        marks[^1] = normalized;
        return WithMarks(marks);
    }

    /// <summary>
    /// Adds many marks at the end in one step (the plan is copied once, not once per mark, which matters for "mark all hits"). All or
    /// nothing: if one mark is refused, or the plan would hold more than <see cref="RedactionLimits.MaxMarks"/> marks, the plan is as it
    /// was. The limit is checked while the marks are read, so a sequence that never ends is refused without being read to its end.
    /// </summary>
    /// <param name="marks">The marks, in the order they are added.</param>
    /// <returns>The new plan; this plan if there is no mark.</returns>
    /// <exception cref="RedactionException">A mark is refused, or the plan would hold more marks than allowed.</exception>
    /// <exception cref="ArgumentException">An id is in the plan already, or twice in <paramref name="marks"/>.</exception>
    public RedactionPlan WithRange(IEnumerable<RedactionMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var added = new List<RedactionMark>();
        HashSet<Guid>? ids = null;
        foreach (var mark in marks)
        {
            ArgumentNullException.ThrowIfNull(mark);
            if (_marks.Length + added.Count >= RedactionLimits.MaxMarks)
            {
                throw new RedactionException(RedactionError.LimitExceeded, "The plan would hold more marks than allowed.");
            }

            if (ids is null)
            {
                ids = new HashSet<Guid>(_marks.Length + 16);
                foreach (var existing in _marks)
                {
                    ids.Add(existing.Id);
                }
            }

            var normalized = Normalize(mark);
            if (!ids.Add(normalized.Id))
            {
                throw new ArgumentException("A mark with this id is already in the plan or in the range.", nameof(marks));
            }

            added.Add(normalized);
        }

        if (added.Count == 0)
        {
            return this;
        }

        var all = new RedactionMark[_marks.Length + added.Count];
        _marks.CopyTo(all, 0);
        added.CopyTo(all, _marks.Length);
        return WithMarks(all);
    }

    /// <summary>Removes marks; ids that are not in the plan are ignored.</summary>
    /// <param name="ids">The ids of the marks.</param>
    public RedactionPlan Without(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0 || _marks.Length == 0)
        {
            return this;
        }

        var remove = new HashSet<Guid>(ids);
        var kept = new List<RedactionMark>(_marks.Length);
        foreach (var mark in _marks)
        {
            if (!remove.Contains(mark.Id))
            {
                kept.Add(mark);
            }
        }

        return kept.Count == _marks.Length ? this : WithMarks([.. kept]);
    }

    /// <summary>Replaces the mark with the same <see cref="RedactionMark.Id"/>, keeping its position.</summary>
    /// <param name="mark">The new state of the mark.</param>
    /// <exception cref="RedactionException">The mark is refused.</exception>
    /// <exception cref="ArgumentException">No mark with this id is in the plan.</exception>
    public RedactionPlan Replace(RedactionMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        var index = IndexOf(mark.Id);
        if (index < 0)
        {
            throw new ArgumentException("No mark with this id is in the plan.", nameof(mark));
        }

        var marks = new RedactionMark[_marks.Length];
        _marks.CopyTo(marks, 0);
        marks[index] = Normalize(mark);
        return WithMarks(marks);
    }

    /// <summary>
    /// Sets the overlay text of the marks with the given ids in one step (the plan is copied once, not once per mark, which matters for a
    /// selection of thousands). Ids that are not in the plan are ignored. The text is checked once, as it is when a mark enters.
    /// </summary>
    /// <param name="ids">The ids of the marks.</param>
    /// <param name="text">The text, or <see langword="null"/> (or white space alone) for none.</param>
    /// <returns>The new plan; this plan if no mark changes.</returns>
    /// <exception cref="RedactionException">The text is longer than allowed or has a character that cannot be drawn; the plan is as it was.</exception>
    public RedactionPlan WithOverlayText(IReadOnlySet<Guid> ids, string? text)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var normalized = NormalizeOverlayText(text);
        if (ids.Count == 0)
        {
            return this;
        }

        RedactionMark[]? changed = null;
        for (var i = 0; i < _marks.Length; i++)
        {
            if (ids.Contains(_marks[i].Id) && !string.Equals(_marks[i].OverlayText, normalized, StringComparison.Ordinal))
            {
                changed ??= (RedactionMark[])_marks.Clone();
                changed[i] = _marks[i] with { OverlayText = normalized };
            }
        }

        return changed is null ? this : WithMarks(changed);
    }

    /// <summary>Returns the plan with another fill.</summary>
    /// <param name="fill">The colour of the areas.</param>
    public RedactionPlan WithFill(RedactionFill fill) =>
        fill == Fill ? this : new RedactionPlan(_marks, fill, ImageTreatment, ImageStrength, Overlay, _rasterPages, RasterDpi);

    /// <summary>Returns the plan with another treatment of the images under the marks.</summary>
    /// <param name="treatment">The treatment.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="treatment"/> is not a defined value.</exception>
    public RedactionPlan WithImageTreatment(RedactionImageTreatment treatment)
    {
        if (!Enum.IsDefined(treatment))
        {
            throw new ArgumentOutOfRangeException(nameof(treatment), "The treatment is not a defined value.");
        }

        return treatment == ImageTreatment ? this : new RedactionPlan(_marks, Fill, treatment, ImageStrength, Overlay, _rasterPages, RasterDpi);
    }

    /// <summary>Returns the plan with another strength of pixelating and blurring.</summary>
    /// <param name="strength">The strength.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="strength"/> is not a defined value.</exception>
    public RedactionPlan WithImageStrength(RedactionImageStrength strength)
    {
        if (!Enum.IsDefined(strength))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), "The strength is not a defined value.");
        }

        return strength == ImageStrength ? this : new RedactionPlan(_marks, Fill, ImageTreatment, strength, Overlay, _rasterPages, RasterDpi);
    }

    /// <summary>Returns the plan with another style of the overlay text.</summary>
    /// <param name="style">The style.</param>
    public RedactionPlan WithOverlayStyle(RedactionOverlayStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return style == Overlay ? this : new RedactionPlan(_marks, Fill, ImageTreatment, ImageStrength, style, _rasterPages, RasterDpi);
    }

    /// <summary>Returns the plan with another resolution of the raster pages.</summary>
    /// <param name="dpi">The dots per inch, from <see cref="RedactionLimits.MinRasterDpi"/> to <see cref="RedactionLimits.MaxRasterDpi"/>.</param>
    /// <exception cref="RedactionException">The resolution is out of range.</exception>
    public RedactionPlan WithRasterDpi(int dpi)
    {
        if (dpi < RedactionLimits.MinRasterDpi || dpi > RedactionLimits.MaxRasterDpi)
        {
            throw new RedactionException(RedactionError.LimitExceeded, "The resolution of a raster page is out of range.");
        }

        return dpi == RasterDpi ? this : new RedactionPlan(_marks, Fill, ImageTreatment, ImageStrength, Overlay, _rasterPages, dpi);
    }

    /// <summary>Turns a page into a raster page, or back into a normal one.</summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="on">Whether the page becomes a raster page.</param>
    /// <returns>The new plan; this plan if nothing changes.</returns>
    /// <exception cref="RedactionException">The page is negative, or the plan would hold more than <see cref="RedactionLimits.MaxRasterPages"/> raster pages.</exception>
    public RedactionPlan WithRasterPage(int page, bool on)
    {
        if (page < 0)
        {
            throw new RedactionException(RedactionError.InvalidArea, "A page index cannot be negative.");
        }

        if (_rasterPages.Contains(page) == on)
        {
            return this;
        }

        if (on && _rasterPages.Count >= RedactionLimits.MaxRasterPages)
        {
            throw new RedactionException(RedactionError.LimitExceeded, "The plan would hold more raster pages than allowed.");
        }

        var pages = new HashSet<int>(_rasterPages);
        if (on)
        {
            pages.Add(page);
        }
        else
        {
            pages.Remove(page);
        }

        return new RedactionPlan(_marks, Fill, ImageTreatment, ImageStrength, Overlay, pages.ToFrozenSet(), RasterDpi);
    }

    /// <summary>Returns the rectangles of all marks on a page, in the order of the marks.</summary>
    /// <param name="page">The 0-based page.</param>
    public IReadOnlyList<PageRect> AreasOn(int page)
    {
        var areas = new List<PageRect>();
        foreach (var mark in _marks)
        {
            if (mark.Page == page)
            {
                areas.AddRange(mark.Areas);
            }
        }

        return areas;
    }

    /// <summary>
    /// Returns the overlay text of every rectangle <see cref="AreasOn"/> returns for a page, in the same order (a mark's text for each of its
    /// rectangles), <see langword="null"/> where the mark has none.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    public IReadOnlyList<string?> OverlayTextsOn(int page)
    {
        var texts = new List<string?>();
        foreach (var mark in _marks)
        {
            if (mark.Page == page)
            {
                for (var i = 0; i < mark.Areas.Count; i++)
                {
                    texts.Add(mark.OverlayText);
                }
            }
        }

        return texts;
    }

    /// <summary>
    /// Checks a mark and returns a copy that owns its list. The count of the rectangles is checked before the list is read, so a
    /// hostile list is refused without touching it.
    /// </summary>
    private static RedactionMark Normalize(RedactionMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark.Areas);
        if (mark.Page < 0)
        {
            throw new RedactionException(RedactionError.InvalidArea, "A page index cannot be negative.");
        }

        if (mark.Areas.Count > RedactionLimits.MaxAreasPerMark)
        {
            throw new RedactionException(RedactionError.LimitExceeded, "The mark has more rectangles than allowed.");
        }

        if (mark.Areas.Count == 0)
        {
            throw new RedactionException(RedactionError.InvalidArea, "A mark needs at least one rectangle.");
        }

        var areas = new PageRect[mark.Areas.Count];
        for (var i = 0; i < areas.Length; i++)
        {
            var area = mark.Areas[i];
            var finite = double.IsFinite(area.Left) && double.IsFinite(area.Top) && double.IsFinite(area.Right) && double.IsFinite(area.Bottom);
            if (!finite || !(area.Right > area.Left) || !(area.Bottom > area.Top))
            {
                throw new RedactionException(RedactionError.InvalidArea, "A rectangle needs finite edges and a positive size.");
            }

            areas[i] = area;
        }

        if (mark.Pattern is { } pattern && !Enum.IsDefined(pattern))
        {
            throw new RedactionException(RedactionError.InvalidArea, "The pattern kind of the mark is not one that exists.");
        }

        if (mark.Term is null)
        {
            if (mark.Kind != RedactionKind.Area || mark.Everywhere)
            {
                throw new RedactionException(RedactionError.EmptyTerm, "A text mark needs a term.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(mark.Term))
            {
                throw new RedactionException(RedactionError.EmptyTerm, "The term is empty or only white space.");
            }

            if (mark.Term.Length > RedactionLimits.MaxTermLength)
            {
                throw new RedactionException(RedactionError.LimitExceeded, "The term is longer than allowed.");
            }
        }

        return mark with { Areas = new ReadOnlyCollection<PageRect>(areas), OverlayText = NormalizeOverlayText(mark.OverlayText) };
    }

    /// <summary>
    /// Checks the overlay text: the length first (before any character is read), then the characters of the text box font. White
    /// space alone is no text. A text with any other character is refused as a whole, never changed.
    /// </summary>
    private static string? NormalizeOverlayText(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.Length > RedactionLimits.MaxOverlayTextLength)
        {
            throw new RedactionException(RedactionError.LimitExceeded, "The overlay text is longer than allowed.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var character in text)
        {
            if (!TextBoxCharacters.IsSupported(character))
            {
                throw new RedactionException(RedactionError.OverlayInvalid, "The overlay text has a character that cannot be drawn.");
            }
        }

        return text;
    }

    private RedactionPlan WithMarks(RedactionMark[] marks) =>
        new(marks, Fill, ImageTreatment, ImageStrength, Overlay, _rasterPages, RasterDpi);

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < _marks.Length; i++)
        {
            if (_marks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
