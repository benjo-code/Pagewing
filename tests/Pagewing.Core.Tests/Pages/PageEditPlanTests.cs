using System.Collections;
using Pagewing.Core.Pages;
using Xunit;

namespace Pagewing.Core.Tests.Pages;

public sealed class PageEditPlanTests
{
    private static readonly PageSourceId Other = new(1);

    private static int[] Order(PageEditPlan plan) => [.. plan.Slots.Select(s => s.SourceIndex)];

    private static PageEditError ErrorOf(Action action) => Assert.Throws<PageEditException>(action).Error;

    /// <summary>A collection that claims a huge size and fails if anyone enumerates it.</summary>
    private sealed class HostileList(int count) : IReadOnlyList<int>
    {
        public int Count => count;

        public int this[int index] => throw new InvalidOperationException("The list must not be read.");

        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("The list must not be enumerated.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void LimitsArePinned()
    {
        Assert.Equal(20_000, PageEditLimits.MaxSlots);
        Assert.Equal(20, PageEditLimits.MaxInsertedFiles);
        Assert.Equal(50, PageEditLimits.ImportBatch);
    }

    [Fact]
    public void IdentityPlanIsIdentityUntilChanged()
    {
        var plan = PageEditPlan.Identity(4);
        Assert.True(plan.IsIdentity);
        Assert.Equal([0, 1, 2, 3], Order(plan));
        Assert.All(plan.Slots, slot =>
        {
            Assert.Equal(new PageSourceId(0), slot.Source);
            Assert.Equal(0, slot.ExtraRotation);
            Assert.Null(slot.Blank);
        });

        Assert.False(plan.Rotate([1], 90).IsIdentity);
        Assert.False(plan.Delete([3]).IsIdentity);
        Assert.False(plan.Move([0], 3).IsIdentity);
        Assert.False(plan.Duplicate([0]).IsIdentity);
        Assert.False(plan.InsertBlank(0, new BlankPage(10, 10)).IsIdentity);
        Assert.False(plan.InsertFrom(Other, [0], 4).IsIdentity);
    }

    [Fact]
    public void IdentityIsRestoredByARoundTrip()
    {
        var plan = PageEditPlan.Identity(4);
        Assert.True(plan.Rotate([1], 90).Rotate([1], 270).IsIdentity);
        Assert.True(plan.Rotate([2], 360).IsIdentity);
        Assert.True(plan.Move([1], 3).Move([2], 1).IsIdentity);
        Assert.True(plan.Move([1], 1).IsIdentity);
        Assert.True(plan.Duplicate([1]).Delete([2]).IsIdentity);
        Assert.False(plan.Duplicate([1]).Delete([1]).Rotate([1], 90).IsIdentity);
        Assert.False(plan.Delete([3]).IsIdentity);
    }

    [Fact]
    public void IdentityRejectsAnEmptyOrTooLargeDocument()
    {
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => PageEditPlan.Identity(0)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => PageEditPlan.Identity(-1)));
        Assert.Equal(PageEditError.LimitExceeded, ErrorOf(() => PageEditPlan.Identity(PageEditLimits.MaxSlots + 1)));
        Assert.Equal(PageEditLimits.MaxSlots, PageEditPlan.Identity(PageEditLimits.MaxSlots).Slots.Count);
        Assert.Equal(PageEditError.LimitExceeded, ErrorOf(() => PageEditPlan.Identity(int.MaxValue)));
    }

    [Fact]
    public void DeleteRejectsTheLastPage()
    {
        var single = PageEditPlan.Identity(1);
        Assert.Equal(PageEditError.LastPage, ErrorOf(() => single.Delete([0])));
        Assert.Single(single.Slots);

        var plan = PageEditPlan.Identity(3);
        Assert.Equal(PageEditError.LastPage, ErrorOf(() => plan.Delete([0, 1, 2])));
        Assert.Equal(PageEditError.LastPage, ErrorOf(() => plan.Delete([2, 1, 0, 0])));
        Assert.Equal([0, 1, 2], Order(plan));
        Assert.True(plan.IsIdentity);

        Assert.Equal([1], Order(plan.Delete([0, 2])));
    }

    [Fact]
    public void RotateAddsAndWrapsAt360()
    {
        var plan = PageEditPlan.Identity(3);
        var rotated = plan.Rotate([0, 2], 90);
        Assert.Equal([90, 0, 90], rotated.Slots.Select(s => s.ExtraRotation));

        var wrapped = rotated.Rotate([0], 270);
        Assert.Equal(0, wrapped.Slots[0].ExtraRotation);
        Assert.Equal(180, plan.Rotate([1], 90).Rotate([1], 90).Slots[1].ExtraRotation);
        Assert.Equal(270, plan.Rotate([1], -90).Slots[1].ExtraRotation);
        Assert.Equal(90, plan.Rotate([1], 450).Slots[1].ExtraRotation);

        Assert.Equal(PageEditError.InvalidRotation, ErrorOf(() => plan.Rotate([0], 45)));
        Assert.Equal(PageEditError.InvalidRotation, ErrorOf(() => plan.Rotate([0], int.MinValue + 1)));
        Assert.Equal(0, plan.Slots[0].ExtraRotation);
    }

    [Fact]
    public void RotateAlsoTurnsBlankPages()
    {
        var plan = PageEditPlan.Identity(1).InsertBlank(1, new BlankPage(100, 200)).Rotate([1], 90);
        Assert.Equal(90, plan.Slots[1].ExtraRotation);
        Assert.Equal(new BlankPage(100, 200), plan.Slots[1].Blank);
    }

    [Fact]
    public void MoveKeepsTheOrderOfTheMovedPages()
    {
        var plan = PageEditPlan.Identity(6);

        // The destination is the gap in the list as it is now (0 = before the first page, 6 = after the last).
        Assert.Equal([0, 2, 4, 1, 3, 5], Order(plan.Move([3, 1], 5)));
        Assert.Equal([1, 3, 0, 2, 4, 5], Order(plan.Move([1, 3], 0)));
        Assert.Equal([0, 2, 4, 5, 1, 3], Order(plan.Move([1, 3], 6)));
        Assert.Equal([0, 1, 2, 3, 4, 5], Order(plan.Move([2, 3], 2)));
        Assert.Equal([0, 1, 2, 3, 4, 5], Order(plan.Move([2, 3], 4)));
    }

    [Fact]
    public void MoveKeepsEverythingElseOnTheSlots()
    {
        var plan = PageEditPlan.Identity(3).Rotate([0], 90).Move([0], 3);
        Assert.Equal([1, 2, 0], Order(plan));
        Assert.Equal(90, plan.Slots[2].ExtraRotation);
    }

    [Fact]
    public void DuplicateInsertsRightAfterEachSource()
    {
        var plan = PageEditPlan.Identity(4).Rotate([1], 90);
        var result = plan.Duplicate([3, 1]);
        Assert.Equal([0, 1, 1, 2, 3, 3], Order(result));
        Assert.Equal([0, 90, 90, 0, 0, 0], result.Slots.Select(s => s.ExtraRotation));
    }

    [Fact]
    public void InsertBlankTakesTheSizeGiven()
    {
        var plan = PageEditPlan.Identity(2);
        var result = plan.InsertBlank(1, new BlankPage(300, 400.5));
        Assert.Equal(3, result.Slots.Count);
        Assert.Equal(new BlankPage(300, 400.5), result.Slots[1].Blank);
        Assert.Equal(0, result.Slots[1].ExtraRotation);
        Assert.Equal(1, result.Slots[2].SourceIndex);
        Assert.Null(result.Slots[0].Blank);

        Assert.Equal(new BlankPage(1, 2), plan.InsertBlank(0, new BlankPage(1, 2)).Slots[0].Blank);
        Assert.Equal(new BlankPage(1, 2), plan.InsertBlank(2, new BlankPage(1, 2)).Slots[2].Blank);
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertBlank(0, new BlankPage(0, 10))));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertBlank(0, new BlankPage(10, double.NaN))));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertBlank(0, new BlankPage(double.PositiveInfinity, 10))));
    }

    [Fact]
    public void InsertFromAppendsForeignSlots()
    {
        var plan = PageEditPlan.Identity(2);
        var result = plan.InsertFrom(Other, [4, 0, 4], 1);
        Assert.Equal(5, result.Slots.Count);
        Assert.Equal([0, 4, 0, 4, 1], Order(result));
        Assert.Equal([0, 1, 1, 1, 0], result.Slots.Select(s => s.Source.Value));
        Assert.All(result.Slots.Skip(1).Take(3), slot =>
        {
            Assert.Equal(0, slot.ExtraRotation);
            Assert.Null(slot.Blank);
        });

        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertFrom(Other, [-1], 0)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertFrom(new PageSourceId(0), [0], 0)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertFrom(new PageSourceId(-3), [0], 0)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertFrom(Other, [0], 3)));
    }

    [Fact]
    public void InsertFromRefusesTheTwentyFirstFile()
    {
        var plan = PageEditPlan.Identity(1);
        for (var id = 1; id <= PageEditLimits.MaxInsertedFiles; id++)
        {
            plan = plan.InsertFrom(new PageSourceId(id), [0], plan.Slots.Count);
        }

        Assert.Equal(21, plan.Slots.Count);

        // Another page of a file already in the plan is fine, a 21st file is not.
        Assert.Equal(22, plan.InsertFrom(new PageSourceId(7), [3], 0).Slots.Count);
        Assert.Equal(
            PageEditError.LimitExceeded,
            ErrorOf(() => plan.InsertFrom(new PageSourceId(PageEditLimits.MaxInsertedFiles + 1), [0], 0)));
        Assert.Equal(21, plan.Slots.Count);

        // Deleting the pages of a file frees its place.
        var freed = plan.Delete([1]);
        Assert.Equal(21 - 1, freed.Slots.Count);
        Assert.Equal(21, freed.InsertFrom(new PageSourceId(PageEditLimits.MaxInsertedFiles + 1), [0], 0).Slots.Count);
    }

    [Fact]
    public void DuplicateBeyondMaxSlotsFails()
    {
        var full = PageEditPlan.Identity(PageEditLimits.MaxSlots);
        Assert.Equal(PageEditError.LimitExceeded, ErrorOf(() => full.Duplicate([0])));
        Assert.Equal(PageEditError.LimitExceeded, ErrorOf(() => full.InsertBlank(0, new BlankPage(1, 1))));
        Assert.Equal(PageEditError.LimitExceeded, ErrorOf(() => full.InsertFrom(Other, [0], 0)));
        Assert.Equal(PageEditLimits.MaxSlots, full.Slots.Count);

        var nearly = PageEditPlan.Identity(PageEditLimits.MaxSlots - 1);
        Assert.Equal(PageEditLimits.MaxSlots, nearly.Duplicate([5]).Slots.Count);
        Assert.Equal(PageEditError.LimitExceeded, ErrorOf(() => nearly.Duplicate([5, 6])));

        // Duplicating again and again ends in LimitExceeded, never beyond the limit.
        var plan = PageEditPlan.Identity(1);
        var steps = 0;
        var error = (PageEditError?)null;
        while (error is null && steps < 100)
        {
            try
            {
                plan = plan.Duplicate([.. Enumerable.Range(0, plan.Slots.Count)]);
                steps++;
            }
            catch (PageEditException ex)
            {
                error = ex.Error;
            }
        }

        Assert.Equal(PageEditError.LimitExceeded, error);
        Assert.InRange(plan.Slots.Count, 1, PageEditLimits.MaxSlots);
    }

    [Fact]
    public void AHugeListIsRefusedWithoutBeingReadOrAllocated()
    {
        var plan = PageEditPlan.Identity(5);
        var hostile = new HostileList(2_000_000_000);

        Assert.Equal(PageEditError.LimitExceeded, ErrorOf(() => plan.InsertFrom(Other, hostile, 0)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Duplicate(hostile)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Delete(hostile)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Rotate(hostile, 90)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Move(hostile, 0)));
        Assert.Equal(PageEditError.LimitExceeded, ErrorOf(() => plan.InsertFrom(Other, new HostileList(PageEditLimits.MaxSlots), 0)));
        Assert.True(plan.IsIdentity);
    }

    [Fact]
    public void OutOfRangePositionsFail()
    {
        var plan = PageEditPlan.Identity(3);
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Rotate([3], 90)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Rotate([-1], 90)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Delete([0, 5])));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Duplicate([int.MaxValue])));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Move([3], 0)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Move([0], 4)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.Move([0], -1)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertBlank(4, new BlankPage(1, 1))));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.InsertBlank(-1, new BlankPage(1, 1))));
        Assert.True(plan.IsIdentity);
    }

    [Fact]
    public void EmptySelectionsChangeNothing()
    {
        var plan = PageEditPlan.Identity(3);
        Assert.Same(plan, plan.Rotate([], 90));
        Assert.Same(plan, plan.Delete([]));
        Assert.Same(plan, plan.Move([], 1));
        Assert.Same(plan, plan.Duplicate([]));
        Assert.Same(plan, plan.InsertFrom(Other, [], 0));
    }

    [Fact]
    public void RepeatedPositionsCountOnce()
    {
        var plan = PageEditPlan.Identity(4);
        Assert.Equal([0, 1, 1, 2, 3], Order(plan.Duplicate([1, 1, 1])));
        Assert.Equal([0, 3], Order(plan.Delete([1, 2, 2, 1])));
        Assert.Equal(90, plan.Rotate([1, 1], 90).Slots[1].ExtraRotation);
    }

    [Fact]
    public void PlansAreImmutable()
    {
        var plan = PageEditPlan.Identity(3);
        var changed = plan.Rotate([0], 90).Delete([1]).Duplicate([0]);
        Assert.True(plan.IsIdentity);
        Assert.Equal(3, plan.Slots.Count);
        Assert.Equal(3, changed.Slots.Count);
        Assert.False(plan.Slots is PageSlot[]);
        Assert.False(plan.Slots is List<PageSlot>);
    }

    [Fact]
    public void FollowPageMapsAfterDeleteAndMove()
    {
        var plan = PageEditPlan.Identity(6);
        Assert.Equal(4, plan.FollowPage(4));

        var deleted = plan.Delete([1, 2]);
        Assert.Equal([0, 3, 4, 5], Order(deleted));
        Assert.Equal(0, deleted.FollowPage(0));
        Assert.Equal(1, deleted.FollowPage(3));
        Assert.Equal(1, deleted.FollowPage(1));
        Assert.Equal(1, deleted.FollowPage(2));

        // Nothing after it: the previous page that is left.
        var tailGone = plan.Delete([4, 5]);
        Assert.Equal(3, tailGone.FollowPage(5));
        Assert.Equal(3, tailGone.FollowPage(4));

        var moved = plan.Move([0], 6);
        Assert.Equal([1, 2, 3, 4, 5, 0], Order(moved));
        Assert.Equal(5, moved.FollowPage(0));
        Assert.Equal(0, moved.FollowPage(1));

        // Duplicates: the first one; blanks and foreign pages never count.
        var duplicated = plan.Duplicate([2]).InsertBlank(0, new BlankPage(1, 1)).InsertFrom(Other, [2], 0);
        Assert.Equal(4, duplicated.FollowPage(2));
        Assert.Equal(6, duplicated.FollowPage(3));
    }

    [Fact]
    public void FollowPageWithoutAnyPrimaryPageFallsBackToTheFirst()
    {
        var plan = PageEditPlan.Identity(2).InsertBlank(0, new BlankPage(1, 1)).Delete([1, 2]);
        Assert.Equal(0, plan.FollowPage(0));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.FollowPage(2)));
        Assert.Equal(PageEditError.OutOfRange, ErrorOf(() => plan.FollowPage(-1)));
    }

    [Fact]
    public void HistoryUndoRedoAndVersion()
    {
        var start = PageEditPlan.Identity(3);
        var history = new PageEditHistory(start);
        Assert.Same(start, history.Current);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        var version = history.Version;

        var first = start.Rotate([0], 90);
        var second = first.Delete([1]);
        history.Do(first);
        Assert.Same(first, history.Current);
        Assert.True(history.CanUndo);
        Assert.NotEqual(version, history.Version);
        version = history.Version;
        history.Do(second);
        Assert.NotEqual(version, history.Version);
        version = history.Version;

        history.Undo();
        Assert.Same(first, history.Current);
        Assert.True(history.CanRedo);
        Assert.NotEqual(version, history.Version);
        version = history.Version;

        history.Redo();
        Assert.Same(second, history.Current);
        Assert.False(history.CanRedo);
        Assert.NotEqual(version, history.Version);

        history.Undo();
        history.Undo();
        Assert.Same(start, history.Current);
        Assert.False(history.CanUndo);

        // A new step after undo forgets what could be redone.
        var other = start.Duplicate([2]);
        history.Do(other);
        Assert.False(history.CanRedo);
        Assert.Same(other, history.Current);
        history.Undo();
        Assert.Same(start, history.Current);
    }

    [Fact]
    public void HistoryVersionNeverGoesBack()
    {
        var start = PageEditPlan.Identity(2);
        var history = new PageEditHistory(start);
        var seen = new HashSet<int> { history.Version };
        history.Do(start.Rotate([0], 90));
        Assert.True(seen.Add(history.Version));
        history.Undo();
        Assert.True(seen.Add(history.Version));
        history.Redo();
        Assert.True(seen.Add(history.Version));
        history.Undo();
        Assert.True(seen.Add(history.Version));
        history.Do(start.Delete([0]));
        Assert.True(seen.Add(history.Version));
    }

    [Fact]
    public void HistoryDoesNothingWhereThereIsNothingToDo()
    {
        var start = PageEditPlan.Identity(2);
        var history = new PageEditHistory(start);
        var version = history.Version;
        history.Undo();
        history.Redo();
        Assert.Equal(version, history.Version);
        Assert.Same(start, history.Current);
        Assert.Throws<ArgumentNullException>(() => history.Do(null!));
        Assert.Throws<ArgumentNullException>(() => new PageEditHistory(null!));
    }

    [Fact]
    public void HistoryKeepsAtMostAHundredSteps()
    {
        var start = PageEditPlan.Identity(2);
        var history = new PageEditHistory(start);
        var plan = start;
        for (var i = 0; i < 150; i++)
        {
            plan = plan.Rotate([0], 90);
            history.Do(plan);
        }

        var undone = 0;
        while (history.CanUndo)
        {
            history.Undo();
            undone++;
        }

        Assert.Equal(100, undone);
    }
}
