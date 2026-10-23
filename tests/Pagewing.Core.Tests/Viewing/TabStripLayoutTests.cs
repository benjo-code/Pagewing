using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class TabStripLayoutTests
{
    [Fact]
    public void FewTabsGetTheMaximumWidth()
    {
        var (width, onlyActive) = TabStripLayout.Compute(2, 700);

        Assert.Equal(TabStripLayout.MaxTabWidth, width);
        Assert.False(onlyActive);
    }

    [Fact]
    public void TabsShrinkEquallyDownToTheMinimum()
    {
        var (width, onlyActive) = TabStripLayout.Compute(3, 412);

        Assert.Equal((412 - TabStripLayout.MinDragArea) / 3.0, width, 6);
        Assert.InRange(width, TabStripLayout.MinTabWidth, TabStripLayout.MaxTabWidth);
        Assert.False(onlyActive);
    }

    [Fact]
    public void TooManyTabsShowOnlyTheActiveOne()
    {
        var (width, onlyActive) = TabStripLayout.Compute(4, 412);

        Assert.True(onlyActive);
        Assert.Equal(TabStripLayout.MaxTabWidth, width);
    }

    [Fact]
    public void OnlyTheActiveTabUsesWhatTheAllTabsButtonLeaves()
    {
        var (width, onlyActive) = TabStripLayout.Compute(10, 300);

        Assert.True(onlyActive);
        Assert.Equal(300 - TabStripLayout.MinDragArea - TabStripLayout.AllTabsButtonWidth, width);
    }

    [Fact]
    public void DragAreaIsNeverBelowItsMinimum()
    {
        const double Tolerance = 1e-9;
        for (var available = 0; available <= 2000; available += 7)
        {
            for (var count = 1; count <= 50; count++)
            {
                var (width, onlyActive) = TabStripLayout.Compute(count, available);

                Assert.InRange(width, TabStripLayout.MinTabWidth, TabStripLayout.MaxTabWidth);
                var used = onlyActive
                    ? width + TabStripLayout.AllTabsButtonWidth + TabStripLayout.MinDragArea
                    : (count * width) + TabStripLayout.MinDragArea;
                Assert.True(
                    used <= available + Tolerance || width == TabStripLayout.MinTabWidth,
                    $"{count} tabs in {available} px: width {width}, onlyActive {onlyActive}");
            }
        }
    }

    [Fact]
    public void ZeroOrNegativeWidthDoesNotThrow()
    {
        foreach (var available in new[] { 0.0, -1.0, -1000.0, double.NaN })
        {
            var (width, _) = TabStripLayout.Compute(3, available);
            Assert.InRange(width, TabStripLayout.MinTabWidth, TabStripLayout.MaxTabWidth);
        }

        var (noTabsWidth, noTabsOnlyActive) = TabStripLayout.Compute(0, 0);
        Assert.InRange(noTabsWidth, TabStripLayout.MinTabWidth, TabStripLayout.MaxTabWidth);
        Assert.False(noTabsOnlyActive);
    }
}
