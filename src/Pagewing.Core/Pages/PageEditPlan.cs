using System.Collections.ObjectModel;

namespace Pagewing.Core.Pages;

/// <summary>
/// The ordered list of pages a document will have after "Apply": nothing is written until then. A plan is immutable; every
/// operation returns a new plan (or the same plan if there is nothing to do) and throws <see cref="PageEditException"/>,
/// leaving the plan as it is, if it is refused. Operations work on positions in <see cref="Slots"/>. A plan knows nothing of
/// PDFium. Every limit is checked before the new list is allocated, so a huge request never reserves memory.
/// </summary>
public sealed class PageEditPlan
{
    private readonly PageSlot[] _slots;

    private PageEditPlan(PageSlot[] slots, int primaryPageCount)
    {
        _slots = slots;
        Slots = new ReadOnlyCollection<PageSlot>(slots);
        PrimaryPageCount = primaryPageCount;
        IsIdentity = IsIdentityOf(slots, primaryPageCount);
    }

    /// <summary>Gets the pages of the result in order.</summary>
    public IReadOnlyList<PageSlot> Slots { get; }

    /// <summary>Gets the number of pages of the open document the plan started from.</summary>
    public int PrimaryPageCount { get; }

    /// <summary>
    /// Gets whether the plan equals the document as it is: every page once, in order, not rotated. It describes the slot list,
    /// so a round trip that restores the list (rotating back, moving back) is the identity again.
    /// </summary>
    public bool IsIdentity { get; }

    /// <summary>Creates the plan that leaves a document of <paramref name="pageCount"/> pages as it is.</summary>
    /// <param name="pageCount">The number of pages of the open document.</param>
    /// <exception cref="PageEditException"><paramref name="pageCount"/> is less than 1 (<see cref="PageEditError.OutOfRange"/>) or more than <see cref="PageEditLimits.MaxSlots"/> (<see cref="PageEditError.LimitExceeded"/>).</exception>
    public static PageEditPlan Identity(int pageCount)
    {
        if (pageCount < 1)
        {
            throw new PageEditException(PageEditError.OutOfRange, "A document has at least one page.");
        }

        if (pageCount > PageEditLimits.MaxSlots)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "The document has more pages than a plan can hold.");
        }

        var slots = new PageSlot[pageCount];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = new PageSlot(default, i, 0, null);
        }

        return new PageEditPlan(slots, pageCount);
    }

    /// <summary>Turns pages by an additional <paramref name="degrees"/>; the total wraps at 360.</summary>
    /// <param name="positions">The positions of the pages (a set: a repeated position counts once).</param>
    /// <param name="degrees">A multiple of 90, also negative or larger than 360.</param>
    /// <exception cref="PageEditException">The rotation is no multiple of 90, or a position is out of range.</exception>
    public PageEditPlan Rotate(IReadOnlyCollection<int> positions, int degrees)
    {
        if (degrees % 90 != 0)
        {
            throw new PageEditException(PageEditError.InvalidRotation, "Pages turn in steps of 90 degrees.");
        }

        var selected = Select(positions);
        if (selected.Length == 0)
        {
            return this;
        }

        var turn = (degrees % 360 + 360) % 360;
        var slots = Copy();
        foreach (var position in selected)
        {
            slots[position] = slots[position] with { ExtraRotation = (slots[position].ExtraRotation + turn) % 360 };
        }

        return new PageEditPlan(slots, PrimaryPageCount);
    }

    /// <summary>Removes pages from the plan. At least one page stays.</summary>
    /// <param name="positions">The positions of the pages (a set).</param>
    /// <exception cref="PageEditException">A position is out of range (<see cref="PageEditError.OutOfRange"/>) or no page would be left (<see cref="PageEditError.LastPage"/>).</exception>
    public PageEditPlan Delete(IReadOnlyCollection<int> positions)
    {
        var selected = Select(positions);
        if (selected.Length == 0)
        {
            return this;
        }

        if (selected.Length == Slots.Count)
        {
            throw new PageEditException(PageEditError.LastPage, "The last page cannot be deleted.");
        }

        var slots = new PageSlot[Slots.Count - selected.Length];
        var next = 0;
        var skip = 0;
        for (var i = 0; i < Slots.Count; i++)
        {
            if (skip < selected.Length && selected[skip] == i)
            {
                skip++;
                continue;
            }

            slots[next++] = Slots[i];
        }

        return new PageEditPlan(slots, PrimaryPageCount);
    }

    /// <summary>Moves pages to a gap of the list, keeping their order.</summary>
    /// <param name="positions">The positions of the pages (a set).</param>
    /// <param name="destination">The gap in the list as it is now: 0 is before the first page, <see cref="Slots"/>.Count after the last. The pages come to rest before the page that is now at this position.</param>
    /// <exception cref="PageEditException">A position or the destination is out of range.</exception>
    public PageEditPlan Move(IReadOnlyCollection<int> positions, int destination)
    {
        var selected = Select(positions);
        if (destination < 0 || destination > Slots.Count)
        {
            throw new PageEditException(PageEditError.OutOfRange, "The destination is not in the list.");
        }

        if (selected.Length == 0)
        {
            return this;
        }

        var slots = new PageSlot[Slots.Count];
        var next = 0;
        var skip = 0;
        for (var i = 0; i <= Slots.Count; i++)
        {
            if (i == destination)
            {
                foreach (var position in selected)
                {
                    slots[next++] = Slots[position];
                }
            }

            if (i == Slots.Count)
            {
                break;
            }

            if (skip < selected.Length && selected[skip] == i)
            {
                skip++;
                continue;
            }

            slots[next++] = Slots[i];
        }

        return new PageEditPlan(slots, PrimaryPageCount);
    }

    /// <summary>Inserts a copy of each page right after it.</summary>
    /// <param name="positions">The positions of the pages (a set).</param>
    /// <exception cref="PageEditException">A position is out of range, or the plan would grow beyond <see cref="PageEditLimits.MaxSlots"/>.</exception>
    public PageEditPlan Duplicate(IReadOnlyCollection<int> positions)
    {
        var selected = Select(positions);
        if (selected.Length == 0)
        {
            return this;
        }

        EnsureRoomFor(selected.Length);
        var slots = new PageSlot[Slots.Count + selected.Length];
        var next = 0;
        var skip = 0;
        for (var i = 0; i < Slots.Count; i++)
        {
            slots[next++] = Slots[i];
            if (skip < selected.Length && selected[skip] == i)
            {
                skip++;
                slots[next++] = Slots[i];
            }
        }

        return new PageEditPlan(slots, PrimaryPageCount);
    }

    /// <summary>Inserts a blank page.</summary>
    /// <param name="at">The gap to insert at: 0 is before the first page, <see cref="Slots"/>.Count after the last.</param>
    /// <param name="size">The size of the page in points; both sides must be positive and finite.</param>
    /// <exception cref="PageEditException">The gap or the size is out of range, or the plan would grow beyond <see cref="PageEditLimits.MaxSlots"/>.</exception>
    public PageEditPlan InsertBlank(int at, BlankPage size)
    {
        ArgumentNullException.ThrowIfNull(size);
        CheckGap(at);
        if (!(size.Width > 0 && size.Height > 0 && double.IsFinite(size.Width) && double.IsFinite(size.Height)))
        {
            throw new PageEditException(PageEditError.OutOfRange, "A blank page needs a positive, finite size.");
        }

        EnsureRoomFor(1);
        return Insert(at, [new PageSlot(default, -1, 0, size)]);
    }

    /// <summary>Inserts pages of another file.</summary>
    /// <param name="source">The file: any value except 0 (the open document).</param>
    /// <param name="sourceIndices">The 0-based pages of that file, in the order they get; a page may repeat.</param>
    /// <param name="at">The gap to insert at: 0 is before the first page, <see cref="Slots"/>.Count after the last.</param>
    /// <exception cref="PageEditException">The source, an index or the gap is out of range, or the plan would hold more than <see cref="PageEditLimits.MaxSlots"/> pages or take pages from more than <see cref="PageEditLimits.MaxInsertedFiles"/> files.</exception>
    public PageEditPlan InsertFrom(PageSourceId source, IReadOnlyList<int> sourceIndices, int at)
    {
        ArgumentNullException.ThrowIfNull(sourceIndices);
        if (source.Value <= 0)
        {
            throw new PageEditException(PageEditError.OutOfRange, "Pages of the open document are duplicated, not inserted.");
        }

        CheckGap(at);
        var count = sourceIndices.Count;
        if (count == 0)
        {
            return this;
        }

        // The size is checked before any index is read, so a hostile list is refused without touching it.
        EnsureRoomFor(count);
        EnsureRoomForFile(source);
        var inserted = new PageSlot[count];
        for (var i = 0; i < inserted.Length; i++)
        {
            var index = sourceIndices[i];
            if (index < 0)
            {
                throw new PageEditException(PageEditError.OutOfRange, "A page index cannot be negative.");
            }

            inserted[i] = new PageSlot(source, index, 0, null);
        }

        return Insert(at, inserted);
    }

    /// <summary>
    /// Returns where a page of the open document is in the plan, for the view to follow it after an edit.
    /// </summary>
    /// <param name="oldPrimaryIndex">The 0-based page of the open document.</param>
    /// <returns>
    /// The position of its first slot; if the page is gone, the position of the next page of the open document that is left,
    /// else of the previous one, else 0.
    /// </returns>
    /// <exception cref="PageEditException">The page is not a page of the open document.</exception>
    public int FollowPage(int oldPrimaryIndex)
    {
        if (oldPrimaryIndex < 0 || oldPrimaryIndex >= PrimaryPageCount)
        {
            throw new PageEditException(PageEditError.OutOfRange, "The page is not in the open document.");
        }

        var nextIndex = int.MaxValue;
        var nextPosition = 0;
        var previousIndex = -1;
        var previousPosition = 0;
        for (var position = 0; position < Slots.Count; position++)
        {
            var slot = Slots[position];
            if (slot.Blank is not null || slot.Source.Value != 0)
            {
                continue;
            }

            if (slot.SourceIndex == oldPrimaryIndex)
            {
                return position;
            }

            if (slot.SourceIndex > oldPrimaryIndex && slot.SourceIndex < nextIndex)
            {
                nextIndex = slot.SourceIndex;
                nextPosition = position;
            }
            else if (slot.SourceIndex < oldPrimaryIndex && slot.SourceIndex > previousIndex)
            {
                previousIndex = slot.SourceIndex;
                previousPosition = position;
            }
        }

        if (nextIndex != int.MaxValue)
        {
            return nextPosition;
        }

        return previousIndex >= 0 ? previousPosition : 0;
    }

    private static bool IsIdentityOf(PageSlot[] slots, int primaryPageCount)
    {
        if (slots.Length != primaryPageCount)
        {
            return false;
        }

        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot.Source.Value != 0 || slot.SourceIndex != i || slot.ExtraRotation != 0 || slot.Blank is not null)
            {
                return false;
            }
        }

        return true;
    }

    private PageSlot[] Copy()
    {
        var slots = new PageSlot[Slots.Count];
        _slots.CopyTo(slots, 0);
        return slots;
    }

    private PageEditPlan Insert(int at, PageSlot[] inserted)
    {
        var slots = new PageSlot[Slots.Count + inserted.Length];
        for (var i = 0; i < at; i++)
        {
            slots[i] = Slots[i];
        }

        inserted.CopyTo(slots, at);
        for (var i = at; i < Slots.Count; i++)
        {
            slots[i + inserted.Length] = Slots[i];
        }

        return new PageEditPlan(slots, PrimaryPageCount);
    }

    private void CheckGap(int at)
    {
        if (at < 0 || at > Slots.Count)
        {
            throw new PageEditException(PageEditError.OutOfRange, "The position is not in the list.");
        }
    }

    private void EnsureRoomFor(int extra)
    {
        // Slots.Count is at most MaxSlots, so the sum cannot overflow before it is compared.
        if (extra > PageEditLimits.MaxSlots - Slots.Count)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "The plan would hold more pages than allowed.");
        }
    }

    private void EnsureRoomForFile(PageSourceId source)
    {
        var files = new HashSet<int>();
        foreach (var slot in Slots)
        {
            if (slot.Blank is null && slot.Source.Value != 0)
            {
                files.Add(slot.Source.Value);
            }
        }

        if (!files.Contains(source.Value) && files.Count >= PageEditLimits.MaxInsertedFiles)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "The plan takes pages from more files than allowed.");
        }
    }

    /// <summary>
    /// Returns the distinct positions ascending. More entries than a plan can hold are refused before the
    /// list is read; otherwise the work and the memory are bounded by the page count.
    /// </summary>
    private int[] Select(IReadOnlyCollection<int> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count > PageEditLimits.MaxSlots)
        {
            throw new PageEditException(PageEditError.OutOfRange, "More positions than a plan can hold.");
        }

        var marked = new bool[Slots.Count];
        var distinct = 0;
        foreach (var position in positions)
        {
            if (position < 0 || position >= marked.Length)
            {
                throw new PageEditException(PageEditError.OutOfRange, "A position is not in the list.");
            }

            if (!marked[position])
            {
                marked[position] = true;
                distinct++;
            }
        }

        var selected = new int[distinct];
        var next = 0;
        for (var i = 0; i < marked.Length; i++)
        {
            if (marked[i])
            {
                selected[next++] = i;
            }
        }

        return selected;
    }
}
