using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;

namespace Pagewing.App.Measurement;

/// <summary>
/// <c>--measure-scroll &lt;pdf&gt;</c>: once the first sharp image of page 1 was drawn, scrolls from the top to the bottom
/// in equal steps, one per frame (<see cref="TopLevel.RequestAnimationFrame"/>), and writes
/// <c>scroll frames=&lt;n&gt; p99-ms=&lt;x&gt; max-ms=&lt;y&gt;</c> over the intervals between the frames. A measured value
/// without a budget.
/// </summary>
internal sealed class ScrollMeasurement : MeasurementRun
{
    /// <summary>Number of scroll steps (frames) in the app.</summary>
    public const int DefaultSteps = 600;

    private readonly int _steps;
    private readonly List<double> _intervals = [];
    private long _lastFrame;
    private int _step;

    /// <param name="viewModel">The view model of the window.</param>
    /// <param name="view">The document view of the window.</param>
    /// <param name="path">The file to scroll through.</param>
    /// <param name="output">Where the line goes and how the app ends.</param>
    /// <param name="timeout">Time limit of the whole measurement.</param>
    /// <param name="steps">Number of steps, at least 1.</param>
    public ScrollMeasurement(MainViewModel viewModel, DocumentView view, string path, MeasurementOutput output, TimeSpan timeout, int steps)
        : base(viewModel, view, path, output, timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        _steps = steps;
    }

    /// <summary>
    /// The <paramref name="fraction"/> percentile (0 &lt; fraction ≤ 1) by the nearest-rank method: the smallest value
    /// that at least that share of the values does not exceed.
    /// </summary>
    public static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1);
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    protected override void OnFirstSharpImage()
    {
        _step = 0;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(View) is not { } topLevel)
        {
            Fail(Failure, "the document view is not in a window");
            return;
        }

        topLevel.RequestAnimationFrame(_ => OnFrame());
    }

    /// <summary>One frame: takes the interval since the previous frame and scrolls one step further.</summary>
    private void OnFrame()
    {
        if (IsFinished)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_step > 0)
        {
            _intervals.Add(Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
        }

        _lastFrame = now;
        if (_step == _steps)
        {
            Succeed(string.Create(
                CultureInfo.InvariantCulture,
                $"scroll frames={_intervals.Count} p99-ms={Percentile(_intervals, 0.99):0.0} max-ms={_intervals.Max():0.0}"));
            return;
        }

        _step++;

        // The extent grows while page sizes arrive in the background: each step aims at its share of the current one.
        var scroller = View.ScrollViewer;
        var bottom = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(scroller.Offset.X, bottom * _step / _steps);
        RequestFrame();
    }
}
