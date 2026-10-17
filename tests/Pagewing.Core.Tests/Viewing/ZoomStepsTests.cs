using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class ZoomStepsTests
{
    [Theory]
    [InlineData(1.0, 1.25)]
    [InlineData(1.1, 1.25)]
    [InlineData(0.05, 0.1)]
    [InlineData(0.1, 0.25)]
    [InlineData(6.0, 8.0)]
    [InlineData(8.0, 8.0)]
    [InlineData(100.0, 8.0)]
    [InlineData(double.NaN, 1.0)]
    public void NextMovesToTheNextHigherStep(double zoom, double expected) =>
        Assert.Equal(expected, ZoomSteps.Next(zoom));

    [Theory]
    [InlineData(1.0, 0.75)]
    [InlineData(1.1, 1.0)]
    [InlineData(0.25, 0.1)]
    [InlineData(0.1, 0.1)]
    [InlineData(0.0, 0.1)]
    [InlineData(100.0, 8.0)]
    [InlineData(double.NaN, 1.0)]
    public void PreviousMovesToTheNextLowerStep(double zoom, double expected) =>
        Assert.Equal(expected, ZoomSteps.Previous(zoom));

    [Theory]
    [InlineData(100.0, 8.0)]
    [InlineData(0.0, 0.1)]
    [InlineData(-3.0, 0.1)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 8.0)]
    [InlineData(double.NegativeInfinity, 0.1)]
    [InlineData(1.37, 1.37)]
    public void ClampKeepsTheZoomInRange(double zoom, double expected) =>
        Assert.Equal(expected, ZoomSteps.Clamp(zoom));

    [Fact]
    public void StepsFollowTheDesignList()
    {
        var steps = new List<double>();
        var zoom = ZoomSteps.Min;
        while (true)
        {
            steps.Add(zoom);
            var next = ZoomSteps.Next(zoom);
            if (next == zoom)
            {
                break;
            }

            zoom = next;
        }

        Assert.Equal([0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8], steps);
    }
}
