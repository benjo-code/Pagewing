namespace Pagewing.Core.Pages;

/// <summary>What the slots of a plan use: the checks a plan cannot do for itself, and the positions of every source page.</summary>
internal sealed class PlanUsage
{
    private readonly Dictionary<(int Source, int Index), List<int>> positions = [];
    private readonly Dictionary<int, int> highest = [];

    private PlanUsage()
    {
    }

    /// <summary>Gets the inserted sources the plan uses, ascending.</summary>
    public SortedSet<int> InsertedIds { get; } = [];

    /// <summary>Gets the pages of the open document no slot uses.</summary>
    public HashSet<int> DeletedPrimaryPages { get; } = [];

    /// <summary>Gets every (source, page) that a slot uses.</summary>
    public IEnumerable<(int Source, int Index)> Keys => positions.Keys;

    public List<int> PositionsOf((int Source, int Index) key) => positions[key];

    public int HighestIndex(int source) => highest[source];

    public static PlanUsage Of(PageEditPlan plan)
    {
        var slots = plan.Slots;
        if (slots.Count < 1 || slots.Count > PageEditLimits.MaxSlots)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "A plan holds between 1 and the allowed number of pages.");
        }

        var usage = new PlanUsage();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.ExtraRotation is not (0 or 90 or 180 or 270))
            {
                throw new PageEditException(PageEditError.InvalidRotation, "The rotation of a page is 0, 90, 180 or 270 degrees.");
            }

            if (slot.Blank is { } blank)
            {
                if (!(blank.Width > 0 && blank.Height > 0 && double.IsFinite(blank.Width) && double.IsFinite(blank.Height)))
                {
                    throw new PageEditException(PageEditError.OutOfRange, "A blank page needs a positive, finite size.");
                }

                continue;
            }

            var source = slot.Source.Value;
            if (slot.SourceIndex < 0 || source < 0 || (source == 0 && slot.SourceIndex >= plan.PrimaryPageCount))
            {
                throw new PageEditException(PageEditError.OutOfRange, "A page of the plan is not a page of its source.");
            }

            if (!usage.positions.TryGetValue((source, slot.SourceIndex), out var list))
            {
                usage.positions[(source, slot.SourceIndex)] = list = [];
            }

            list.Add(i);
            usage.highest[source] = Math.Max(usage.highest.GetValueOrDefault(source, -1), slot.SourceIndex);
            if (source != 0)
            {
                usage.InsertedIds.Add(source);
            }
        }

        if (usage.InsertedIds.Count > PageEditLimits.MaxInsertedFiles)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "The plan takes pages from more files than allowed.");
        }

        for (var p = 0; p < plan.PrimaryPageCount; p++)
        {
            if (!usage.positions.ContainsKey((0, p)))
            {
                usage.DeletedPrimaryPages.Add(p);
            }
        }

        return usage;
    }

    /// <summary>Refuses a set of files that is too large, or that lacks the file of a source the plan uses, before anything is opened.</summary>
    public void EnsureFilesAreGiven(IEnumerable<PageSourceId> given, int givenCount)
    {
        if (givenCount > PageEditLimits.MaxInsertedFiles)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "More files than allowed were given.");
        }

        var known = given.Select(g => g.Value).ToHashSet();
        if (InsertedIds.Any(id => !known.Contains(id)))
        {
            throw new PageEditException(PageEditError.OutOfRange, "A source of the plan has no file.");
        }
    }
}
