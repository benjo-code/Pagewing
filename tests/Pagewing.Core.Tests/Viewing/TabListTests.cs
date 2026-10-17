using System.Collections.Specialized;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class TabListTests
{
    private sealed class Tab(string name)
    {
        public string Name { get; } = name;

        public override string ToString() => Name;
    }

    private static (TabList<Tab> List, Tab A, Tab B, Tab C) ListOfThree()
    {
        var list = new TabList<Tab>();
        var a = new Tab("a");
        var b = new Tab("b");
        var c = new Tab("c");
        list.Add(a);
        list.Add(b);
        list.Add(c);
        return (list, a, b, c);
    }

    private static string Order(TabList<Tab> list) => string.Concat(list.Select(tab => tab.Name));

    [Fact]
    public void AddInsertsRightOfTheActiveTabAndActivatesIt()
    {
        var (list, a, b, c) = ListOfThree();
        Assert.Equal("abc", Order(list));

        list.Activate(a);
        var d = new Tab("d");
        list.Add(d);

        Assert.Equal("adbc", Order(list));
        Assert.Same(d, list.Active);
    }

    [Fact]
    public void AddWithoutActiveTabAppends()
    {
        var list = new TabList<Tab>();
        Assert.Null(list.Active);

        var a = new Tab("a");
        list.Add(a);

        Assert.Equal("a", Order(list));
        Assert.Same(a, list.Active);
    }

    [Fact]
    public void ClosingTheActiveTabActivatesTheRightNeighbour()
    {
        var (list, a, b, c) = ListOfThree();
        list.Activate(b);

        var result = list.Close(b);

        Assert.Equal("ac", Order(list));
        Assert.Same(c, result);
        Assert.Same(c, list.Active);
    }

    [Fact]
    public void ClosingTheLastTabInTheListActivatesTheLeftNeighbour()
    {
        var (list, a, b, c) = ListOfThree();
        list.Activate(c);

        var result = list.Close(c);

        Assert.Equal("ab", Order(list));
        Assert.Same(b, result);
        Assert.Same(b, list.Active);
    }

    [Fact]
    public void ClosingAnInactiveTabKeepsTheActiveOne()
    {
        var (list, a, b, c) = ListOfThree();
        list.Activate(c);
        var activeChanges = 0;
        list.ActiveChanged += () => activeChanges++;

        var result = list.Close(a);

        Assert.Equal("bc", Order(list));
        Assert.Same(c, result);
        Assert.Same(c, list.Active);
        Assert.Equal(0, activeChanges);
    }

    [Fact]
    public void ClosingTheOnlyTabLeavesNoActiveTab()
    {
        var list = new TabList<Tab>();
        var a = new Tab("a");
        list.Add(a);

        var result = list.Close(a);

        Assert.Null(result);
        Assert.Null(list.Active);
        Assert.Empty(list);
    }

    [Fact]
    public void ClosingAnUnknownItemChangesNothing()
    {
        var (list, a, b, c) = ListOfThree();
        var changes = 0;
        list.CollectionChanged += (_, _) => changes++;
        list.ActiveChanged += () => changes++;

        var result = list.Close(new Tab("x"));

        Assert.Equal("abc", Order(list));
        Assert.Same(c, result);
        Assert.Same(c, list.Active);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void MoveReordersAndKeepsTheActiveTab()
    {
        var (list, a, b, c) = ListOfThree();
        list.Activate(b);

        list.Move(a, 2);

        Assert.Equal("bca", Order(list));
        Assert.Same(b, list.Active);
        Assert.Equal(0, list.IndexOf(b));
    }

    [Fact]
    public void MoveClampsTheIndex()
    {
        var (list, a, b, c) = ListOfThree();

        list.Move(a, 99);
        Assert.Equal("bca", Order(list));

        list.Move(a, -5);
        Assert.Equal("abc", Order(list));
    }

    [Fact]
    public void ActivateNextWrapsAround()
    {
        var (list, a, b, c) = ListOfThree();
        list.Activate(b);

        list.ActivateNext();
        Assert.Same(c, list.Active);

        list.ActivateNext();
        Assert.Same(a, list.Active);
    }

    [Fact]
    public void ActivePreviousWrapsAround()
    {
        var (list, a, b, c) = ListOfThree();
        list.Activate(b);

        list.ActivatePrevious();
        Assert.Same(a, list.Active);

        list.ActivatePrevious();
        Assert.Same(c, list.Active);
    }

    [Fact]
    public void ChangesRaiseCollectionChangedWithoutReset()
    {
        var list = new TabList<Tab>();
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.CollectionChanged += (_, e) => events.Add(e);
        var a = new Tab("a");
        var b = new Tab("b");

        list.Add(a);
        list.Add(b);
        list.Move(b, 0);
        list.Close(a);

        Assert.Equal(
            [
                NotifyCollectionChangedAction.Add,
                NotifyCollectionChangedAction.Add,
                NotifyCollectionChangedAction.Move,
                NotifyCollectionChangedAction.Remove,
            ],
            events.Select(e => e.Action));
        Assert.Equal(1, events[1].NewStartingIndex);
        Assert.Equal(1, events[2].OldStartingIndex);
        Assert.Equal(0, events[2].NewStartingIndex);
        Assert.Equal(1, events[3].OldStartingIndex);
        Assert.Same(a, Assert.Single(events[3].OldItems!));
    }

    [Fact]
    public void ActiveChangedIsRaisedOnlyOnChange()
    {
        var list = new TabList<Tab>();
        var count = 0;
        list.ActiveChanged += () => count++;
        var a = new Tab("a");
        var b = new Tab("b");

        list.Add(a);
        Assert.Equal(1, count);

        list.Add(b);
        Assert.Equal(2, count);

        Assert.True(list.Activate(b));
        Assert.Equal(2, count);

        Assert.True(list.Activate(a));
        Assert.Equal(3, count);

        Assert.False(list.Activate(new Tab("x")));
        Assert.Equal(3, count);

        list.Move(a, 1);
        Assert.Equal(3, count);

        list.Close(a);
        Assert.Equal(4, count);
    }
}
