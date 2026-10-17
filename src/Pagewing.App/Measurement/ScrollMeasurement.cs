using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Measurement;

/// <summary>
/// <c>--measure-scroll &lt;pdf&gt; [--zoom &lt;percent&gt;]</c>: once the first sharp image of page 1 was drawn, sets the
/// zoom (default 100 %) and goes back to the top, waits one frame, then scrolls from the top to the bottom in equal
/// steps, one per frame (<see cref="TopLevel.RequestAnimationFrame"/>), and writes over the intervals between the
/// frames <c>scroll frames=&lt;n&gt; p99-ms=&lt;x&gt; max-ms=&lt;y&gt;</c> and then
/// <c>scroll-detail zoom-pct=&lt;z&gt; mean-ms=&lt;m&gt; over-20ms-pct=&lt;a&gt; over-30ms-pct=&lt;b&gt;</c> (mean interval
/// and share of the intervals above 20 and 30 ms, in percent). A measured value without a budget.
/// </summary>
internal sealed class ScrollMeasurement : MeasurementRun
{
    /// <summary>Number of scroll steps (frames) in the app.</summary>
    public const int DefaultSteps = 600;

    /// <summary>The zoom without <c>--zoom</c>, in percent.</summary>
    public const int DefaultZoomPercent = 100;

    /// <summary>The smallest zoom <c>--zoom</c> takes, in percent (<see cref="ZoomSteps.Min"/>).</summary>
    public const int MinZoomPercent = 10;

    /// <summary>The largest zoom <c>--zoom</c> takes, in percent (<see cref="ZoomSteps.Max"/>).</summary>
    public const int MaxZoomPercent = 800;

    private readonly int _steps;
    private readonly int _zoomPercent;
    private readonly List<double> _intervals = [];
    private long _lastFrame;
    private int _step;
    private bool _settling;

    /// <param name="viewModel">The view model of the window.</param>
    /// <param name="view">The document view of the window.</param>
    /// <param name="path">The file to scroll through.</param>
    /// <param name="output">Where the lines go and how the app ends.</param>
    /// <param name="timeout">Time limit of the whole measurement.</param>
    /// <param name="steps">Number of steps, at least 1.</param>
    /// <param name="zoomPercent">The zoom while scrolling, <see cref="MinZoomPercent"/> to <see cref="MaxZoomPercent"/>.</param>
    public ScrollMeasurement(MainViewModel viewModel, DocumentView view, string path, MeasurementOutput output, TimeSpan timeout, int steps, int zoomPercent = DefaultZoomPercent)
        : base(viewModel, view, path, output, timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(zoomPercent, MinZoomPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zoomPercent, MaxZoomPercent);
        _steps = steps;
        _zoomPercent = zoomPercent;
    }

    /// <summary>
    /// Reads the value of <c>--zoom</c>: <see langword="null"/> (no option) gives <see cref="DefaultZoomPercent"/>,
    /// otherwise a whole number of percent from <see cref="MinZoomPercent"/> to <see cref="MaxZoomPercent"/> (digits
    /// only, invariant culture). Anything else, also an empty value, is rejected.
    /// </summary>
    public static bool TryParseZoomPercent(string? text, out int percent)
    {
        if (text is null)
        {
            percent = DefaultZoomPercent;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out percent)
            && percent is >= MinZoomPercent and <= MaxZoomPercent;
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

    /// <summary>The share of <paramref name="values"/> strictly above <paramref name="limit"/>, in percent.</summary>
    public static double ShareAbove(IReadOnlyList<double> values, double limit)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);
        return 100.0 * values.Count(value => value > limit) / values.Count;
    }

    /// <summary>The second result line (<c>scroll-detail …</c>) for the intervals and the zoom.</summary>
    public static string DetailLine(IReadOnlyList<double> intervals, int zoomPercent)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentOutOfRangeException.ThrowIfZero(intervals.Count);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"scroll-detail zoom-pct={zoomPercent} mean-ms={intervals.Average():0.0} over-20ms-pct={ShareAbove(intervals, 20):0.0} over-30ms-pct={ShareAbove(intervals, 30):0.0}");
    }

    protected override void OnFirstSharpImage()
    {
        _step = 0;
        if (_zoomPercent != DefaultZoomPercent)
        {
            // The view keeps the middle of the viewport in place; the scroll starts at the top all the same. The frame
            // that lays out and draws the new zoom is not one of the measured intervals.
            ViewModel.Zoom = _zoomPercent / 100.0;
            var scroller = View.ScrollViewer;
            scroller.Offset = new Vector(scroller.Offset.X, 0);
            _settling = true;
        }

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

        if (_settling)
        {
            _settling = false;
            RequestFrame();
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
            Report(string.Create(
                CultureInfo.InvariantCulture,
                $"scroll frames={_intervals.Count} p99-ms={Percentile(_intervals, 0.99):0.0} max-ms={_intervals.Max():0.0}"));
            Succeed(DetailLine(_intervals, _zoomPercent));
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
