using Pagewing.Core.Printing;
using Xunit;

namespace Pagewing.Core.Tests.Printing;

public sealed class PrintPlanTests
{
    private static void AssertPages(IReadOnlyList<int> actual, params int[] expected) => Assert.Equal(expected, actual);

    [Fact]
    public void AllPagesInOrder()
    {
        var plan = PrintPlan.Create(4, 0, new PrintSettings());

        AssertPages(plan.Sequence, 0, 1, 2, 3);
        Assert.True(plan.IsWholeDocument);
    }

    [Fact]
    public void CurrentPageOnly()
    {
        var plan = PrintPlan.Create(10, 6, new PrintSettings { Pages = PrintPageChoice.CurrentPage });

        AssertPages(plan.Sequence, 6);
        Assert.Equal("7", plan.CupsPageRanges);
        Assert.False(plan.IsWholeDocument);
    }

    [Fact]
    public void CurrentPageBeyondTheDocumentIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintPlan.Create(3, 3, new PrintSettings { Pages = PrintPageChoice.CurrentPage }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintPlan.Create(3, -1, new PrintSettings { Pages = PrintPageChoice.CurrentPage }));
    }

    [Fact]
    public void RangeIsExpandedAndSorted()
    {
        var plan = PrintPlan.Create(8, 0, new PrintSettings { Pages = PrintPageChoice.Range, Range = "5-6,1,3-4" });

        AssertPages(plan.Pages, 0, 2, 3, 4, 5);
        Assert.Equal("1,3-6", plan.CupsPageRanges);
    }

    [Fact]
    public void OverlappingRangesCountOnce()
    {
        var plan = PrintPlan.Create(8, 0, new PrintSettings { Pages = PrintPageChoice.Range, Range = "1-3,2-4,3" });

        AssertPages(plan.Sequence, 0, 1, 2, 3);
    }

    [Fact]
    public void CollatedCopiesRepeatTheWholeRange()
    {
        var plan = PrintPlan.Create(3, 0, new PrintSettings { Copies = 2, Collate = true });

        AssertPages(plan.Sequence, 0, 1, 2, 0, 1, 2);
        AssertPages(plan.Pages, 0, 1, 2);
    }

    [Fact]
    public void UncollatedCopiesRepeatEachPage()
    {
        var plan = PrintPlan.Create(3, 0, new PrintSettings { Copies = 2, Collate = false });

        AssertPages(plan.Sequence, 0, 0, 1, 1, 2, 2);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(999, 999)]
    [InlineData(1000, 999)]
    public void CopiesAreClamped(int copies, int expected)
    {
        var plan = PrintPlan.Create(1, 0, new PrintSettings { Copies = copies });

        Assert.Equal(expected, plan.PageCountToPrint);
    }

    [Fact]
    public void OutOfRangePageNamesThePage()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PrintPlan.Create(10, 0, new PrintSettings { Pages = PrintPageChoice.Range, Range = "8-12" }));

        Assert.Equal(12, ex.ActualValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("5-3")]
    [InlineData("1-")]
    [InlineData("abc")]
    public void InvalidRangesAreRejected(string range)
    {
        Assert.Throws<FormatException>(() => PrintPlan.Create(10, 0, new PrintSettings { Pages = PrintPageChoice.Range, Range = range }));
    }

    [Fact]
    public void HugeRangeNeverAllocates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PrintPlan.Create(10, 0, new PrintSettings { Pages = PrintPageChoice.Range, Range = "1-2000000000", Copies = 999 }));
    }

    [Fact]
    public void SequenceHasAnUpperLimit()
    {
        var copies = new PrintSettings { Copies = PrintSettings.MaxCopies };

        Assert.Equal(1001 * PrintSettings.MaxCopies, PrintPlan.Create(1001, 0, copies).PageCountToPrint);
        Assert.Throws<InvalidOperationException>(() => PrintPlan.Create(1002, 0, copies));
    }

    [Fact]
    public void CupsPageRangesAreNormalized()
    {
        var plan = PrintPlan.Create(5, 0, new PrintSettings { Pages = PrintPageChoice.Range, Range = "3,1-2,2" });

        Assert.Equal("1-3", plan.CupsPageRanges);
    }

    [Fact]
    public void ARangeOfAllPagesIsTheWholeDocument()
    {
        var plan = PrintPlan.Create(3, 0, new PrintSettings { Pages = PrintPageChoice.Range, Range = "1-3" });

        Assert.True(plan.IsWholeDocument);
        Assert.Equal("1-3", plan.CupsPageRanges);
    }

    [Fact]
    public void ZeroPagesIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintPlan.Create(0, 0, new PrintSettings()));
    }
}
