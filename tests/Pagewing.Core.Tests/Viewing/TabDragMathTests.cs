using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class TabDragMathTests
{
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(40, false, false)]
    [InlineData(40.1, false, true)]
    [InlineData(-41, false, true)]
    [InlineData(0, true, true)]
    [InlineData(double.NaN, false, false)]
    public void ShouldDetachAfterMoreThanTheDetachDistanceOrOutsideTheWindow(double distance, bool outside, bool expected) =>
        Assert.Equal(expected, TabDragMath.ShouldDetach(distance, outside));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(49, 0)]
    [InlineData(50, 1)]
    [InlineData(149, 1)]
    [InlineData(150, 2)]
    [InlineData(250, 3)]
    [InlineData(10000, 3)]
    [InlineData(-20, 0)]
    public void InsertionIndexIsTheSlotWhoseBoundaryIsNearest(double x, int expected) =>
        Assert.Equal(expected, TabDragMath.InsertionIndex(x, 100, 3));

    [Theory]
    [InlineData(50, 100, 0)]
    [InlineData(150, 0, 3)]
    [InlineData(150, -10, 3)]
    [InlineData(double.NaN, 100, 3)]
    [InlineData(150, double.NaN, 3)]
    public void InsertionIndexIsZeroWithoutTabsOrWidth(double x, double width, int count) =>
        Assert.Equal(0, TabDragMath.InsertionIndex(x, width, count));
}
