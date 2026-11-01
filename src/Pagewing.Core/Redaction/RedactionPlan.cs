using System.Collections.ObjectModel;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// The marks of a session: nothing is written until "Apply". A plan is immutable; every operation returns a new plan (or the
/// same plan if there is nothing to do) and throws <see cref="RedactionException"/>, leaving the plan as it is, if it is
/// refused. Marks are copied when they enter, so a caller that changes its list later does not change the plan. A plan knows
/// nothing of PDFium or of page sizes. Every limit is checked before anything is allocated or enumerated.
/// </summary>
public sealed class RedactionPlan
{
    private readonly RedactionMark[] _marks;

    private RedactionPlan(RedactionMark[] marks, RedactionFill fill)
    {
        _marks = marks;
        Marks = new ReadOnlyCollection<RedactionMark>(marks);
        Fill = fill;
        var pages = new HashSet<int>();
        foreach (var mark in marks)
        {
            pages.Add(mark.Page);
        }

        Pages = pages;
    }

    /// <summary>Gets the plan without any mark and with the default fill.</summary>
    public static RedactionPlan Empty { get; } = new([], RedactionFill.Black);

    /// <summary>Gets the marks in the order they were made.</summary>
    public IReadOnlyList<RedactionMark> Marks { get; }

    /// <summary>Gets the colour of the areas.</summary>
    public RedactionFill Fill { get; }

    /// <summary>Gets whether there is no mark.</summary>
    public bool IsEmpty => _marks.Length == 0;

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
        return new RedactionPlan(marks, Fill);
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
        return new RedactionPlan(all, Fill);
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

        return kept.Count == _marks.Length ? this : new RedactionPlan([.. kept], Fill);
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
        return new RedactionPlan(marks, Fill);
    }

    /// <summary>Returns the plan with another fill.</summary>
    /// <param name="fill">The colour of the areas.</param>
    public RedactionPlan WithFill(RedactionFill fill) => fill == Fill ? this : new RedactionPlan(_marks, fill);

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

        return mark with { Areas = new ReadOnlyCollection<PageRect>(areas) };
    }

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
