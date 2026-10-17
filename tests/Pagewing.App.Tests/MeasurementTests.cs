using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Measurement;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed partial class MeasurementTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly List<int> _exits = [];

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        _output.Dispose();
        _error.Dispose();
        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task StartupMeasurementWritesFirstSharpOnceAndRequestsExit()
    {
        var (window, viewModel, view) = Show();
        using var measurement = new StartupMeasurement(viewModel, view, TestData.Pdf("text-basic.pdf"), null, Sink(), Timeout, TimeSpan.Zero);

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        Assert.Equal(["first-sharp"], Lines(_output));
        Assert.Equal([0], _exits);
        Assert.Empty(_error.ToString());
        Assert.Contains(view.Canvas.Bitmaps.Keys, key => key.Page == 0 && !key.IsPreview);

        // Later sharp images (zoom, another document) report nothing more and do not exit again.
        viewModel.ActiveTab!.Zoom = 2;
        Settle(window);
        var drawn = false;
        view.FirstSharpTileDrawn += () => drawn = true;
        await viewModel.OpenAsync(TestData.Pdf("hello_world.pdf"));
        await WaitUntilAsync(window, () => drawn);
        Assert.Equal(["first-sharp"], Lines(_output));
        Assert.Equal([0], _exits);
    }

    [AvaloniaTheory]
    [InlineData("--measure-startup")]
    [InlineData("--measure-second")]
    [InlineData("--measure-scroll")]
    public async Task MeasurementRunsOpenNoPanel(string mode)
    {
        // The window is wide enough for the "Pages" panel to open after the first sharp tile in a normal run.
        var (window, viewModel, view) = Show();
        using var measurement = mode switch
        {
            "--measure-second" => MeasurementRun.Create(StartupOptions.Parse([mode, TestData.Pdf("text-basic.pdf"), TestData.Pdf("hello_world.pdf")]), viewModel, view, Sink())!,
            "--measure-startup" => MeasurementRun.Create(StartupOptions.Parse([mode, TestData.Pdf("text-basic.pdf")]), viewModel, view, Sink())!,

            // A few steps instead of the 600 of the switch, as in ScrollMeasurementReportsFramesAndPercentile.
            _ => new ScrollMeasurement(viewModel, view, TestData.Pdf("thousand-pages.pdf"), Sink(), Timeout, steps: 12),
        };

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);
        Settle(window);

        // The run measured as before and left every tab without a panel; nothing of the panel was built.
        Assert.Equal([0], _exits);
        Assert.Empty(_error.ToString());
        var expected = mode switch
        {
            "--measure-startup" => "first-sharp",
            "--measure-second" => "second-sharp",
            _ => "scroll ",
        };
        Assert.Contains(Lines(_output), line => line.StartsWith(expected, StringComparison.Ordinal));
        Assert.All(viewModel.Tabs, tab => Assert.Equal(SidePanelKind.None, tab.SidePanel));
        Assert.Empty(window.GetVisualDescendants().OfType<SidePanel>());
    }

    [AvaloniaFact]
    public async Task StartupMeasurementWritesThePhasesAfterFirstSharp()
    {
        var (window, viewModel, view) = Show();
        var phases = new PhaseRecorder();
        phases.Mark("main", System.Diagnostics.Stopwatch.GetTimestamp());
        using var measurement = new StartupMeasurement(viewModel, view, TestData.Pdf("text-basic.pdf"), null, Sink(phases), Timeout, TimeSpan.Zero);

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        // "first-sharp" comes first: the script times its arrival, reading the process start must not delay it.
        var lines = Lines(_output);
        Assert.Equal("first-sharp", lines[0]);
        Assert.Equal(2, lines.Count);
        Assert.Matches(PhaseLine(), lines[1]);
        Assert.StartsWith("phase main ", lines[1], StringComparison.Ordinal);
        Assert.Equal([0], _exits);
    }

    [AvaloniaFact]
    public async Task StartupMeasurementSucceedsWithoutPhasesWhenTheProcessStartCannotBeRead()
    {
        var (window, viewModel, view) = Show();
        var phases = new PhaseRecorder();
        phases.Mark("main", System.Diagnostics.Stopwatch.GetTimestamp());
        var output = new MeasurementOutput(_output, _error, _exits.Add, phases, () => throw new InvalidOperationException("no process information"));
        using var measurement = new StartupMeasurement(viewModel, view, TestData.Pdf("text-basic.pdf"), null, output, Timeout, TimeSpan.Zero);

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        // The phases are only information: the result line stands, no phase line, no error, exit code 0.
        Assert.Equal(["first-sharp"], Lines(_output));
        Assert.Empty(_error.ToString());
        Assert.Equal([0], _exits);
    }

    [Fact]
    public void PhaseRecorderKeepsTheFirstMarkOfEachPhaseInOrder()
    {
        var recorder = new PhaseRecorder();
        var origin = 1_000_000L;
        var tick = System.Diagnostics.Stopwatch.Frequency / 1000; // 1 ms
        recorder.Mark("main", origin + (12 * tick));
        recorder.Mark("window-opened", origin + (150 * tick));
        recorder.Mark("main", origin + (400 * tick));
        recorder.Mark("tile-applied", origin + (170 * tick));

        Assert.Equal(["phase main 12.0", "phase window-opened 150.0", "phase tile-applied 170.0"], recorder.Lines(origin));
    }

    [Fact]
    public void PhaseLinesUseTheInvariantCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("phase frame-after-tile 1234.6", PhaseRecorder.Format("frame-after-tile", 1234.56));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void TheProcessStartLiesBeforeNow()
    {
        var start = PhaseRecorder.ProcessStartTimestamp();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);

        // The test process started before this test and less than a day ago.
        Assert.InRange(elapsed, TimeSpan.Zero, TimeSpan.FromDays(1));
    }

    [AvaloniaFact]
    public async Task MeasureSecondReportsAPositiveTime()
    {
        var (window, viewModel, view) = Show();
        var second = TestData.Pdf("hello_world.pdf");
        using var measurement = new StartupMeasurement(viewModel, view, TestData.Pdf("text-basic.pdf"), second, Sink(), Timeout, TimeSpan.FromMilliseconds(50));

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        Assert.Empty(_error.ToString());
        Assert.Equal([0], _exits);
        var lines = Lines(_output);
        Assert.Equal(2, lines.Count);
        Assert.Equal("first-sharp", lines[0]);
        var match = SecondSharpLine().Match(lines[1]);
        Assert.True(match.Success, $"unexpected line '{lines[1]}'");
        Assert.True(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) > 0);

        // The second file came in like a later launch and is the one shown now, in a tab of its own.
        Assert.Equal(2, viewModel.Tabs.Count);
        Assert.Equal(Path.GetFullPath(second), viewModel.ActiveTab!.Session.Path);
        Assert.Equal(viewModel.ActiveTab.Session.Document, view.Document);
    }

    [AvaloniaFact]
    public async Task MeasureStartupWithAMissingFileExitsWithOne()
    {
        var (window, viewModel, view) = Show();
        var missing = Path.Combine(Path.GetTempPath(), "pagewing-missing-" + Guid.NewGuid().ToString("N") + ".pdf");
        using var measurement = new StartupMeasurement(viewModel, view, missing, null, Sink(), Timeout, TimeSpan.Zero);

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        Assert.Equal([1], _exits);
        Assert.Empty(_output.ToString());
        var error = Lines(_error);
        Assert.Single(error);
        Assert.StartsWith("error: ", error[0], StringComparison.Ordinal);
        Assert.Contains(nameof(PdfOpenError.FileNotFound), error[0], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task MeasurementWithoutASharpImageTimesOutWithThree()
    {
        // A view that is not in a window never draws, so the first sharp image never comes.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker());
        _cleanup.Add(viewModel);
        var view = new DocumentView();
        using var measurement = new StartupMeasurement(viewModel, view, TestData.Pdf("text-basic.pdf"), null, Sink(), TimeSpan.FromMilliseconds(200), TimeSpan.Zero);

        measurement.Start();
        var deadline = DateTime.UtcNow + Timeout;
        while (_exits.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the measurement did not time out");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal([3], _exits);
        Assert.Empty(_output.ToString());
        Assert.StartsWith("error: ", _error.ToString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ScrollMeasurementReportsFramesAndPercentile()
    {
        var (window, viewModel, view) = Show();
        using var measurement = new ScrollMeasurement(viewModel, view, TestData.Pdf("thousand-pages.pdf"), Sink(), Timeout, steps: 12);

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        Assert.Empty(_error.ToString());
        Assert.Equal([0], _exits);
        var lines = Lines(_output);
        Assert.Equal(2, lines.Count);
        var match = ScrollLine().Match(lines[0]);
        Assert.True(match.Success, $"unexpected line '{lines[0]}'");
        Assert.Equal("12", match.Groups[1].Value);
        var p99 = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var max = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        Assert.InRange(p99, 0, max);

        // The detail line follows the line that existing parsers read, and does not match it.
        var detail = ScrollDetailLine().Match(lines[1]);
        Assert.True(detail.Success, $"unexpected line '{lines[1]}'");
        Assert.Equal("100", detail.Groups[1].Value);
        Assert.InRange(double.Parse(detail.Groups[2].Value, CultureInfo.InvariantCulture), 0, max);
        Assert.DoesNotMatch(ScrollLine(), lines[1]);
        Assert.Equal(1, view.Layout!.Zoom);
        var scroller = view.ScrollViewer;
        Assert.Equal(Math.Ceiling(scroller.Extent.Height - scroller.Viewport.Height), double.Parse(detail.Groups[5].Value, CultureInfo.InvariantCulture));

        // Scrolled from the top to the very bottom (all pages of this file have the same size, so the position does
        // not move when the last page sizes arrive).
        Assert.True(scroller.Offset.Y > 0);
        Assert.Equal(scroller.Extent.Height - scroller.Viewport.Height, scroller.Offset.Y, 3);
        Assert.InRange(view.Layout!.GetPageRect(999).Bottom, scroller.Offset.Y, scroller.Offset.Y + scroller.Viewport.Height);
    }

    [AvaloniaFact]
    public async Task ScrollMeasurementScrollsAtTheRequestedZoom()
    {
        var (window, viewModel, view) = Show();
        using var measurement = new ScrollMeasurement(viewModel, view, TestData.Pdf("thousand-pages.pdf"), Sink(), Timeout, steps: 12, zoomPercent: 200);

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        Assert.Empty(_error.ToString());
        Assert.Equal([0], _exits);
        var lines = Lines(_output);
        Assert.Equal(2, lines.Count);
        Assert.Equal("12", ScrollLine().Match(lines[0]).Groups[1].Value);
        Assert.Equal("200", ScrollDetailLine().Match(lines[1]).Groups[1].Value);

        // Scrolled at 200 % from the top to the very bottom.
        Assert.Equal(2, viewModel.ActiveTab!.Zoom);
        Assert.Equal(2, view.Layout!.Zoom);
        var scroller = view.ScrollViewer;
        Assert.Equal(scroller.Extent.Height - scroller.Viewport.Height, scroller.Offset.Y, 3);
        Assert.InRange(view.Layout.GetPageRect(999).Bottom, scroller.Offset.Y, scroller.Offset.Y + scroller.Viewport.Height);
    }

    [AvaloniaTheory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("801")]
    [InlineData("")]
    [InlineData("200%")]
    [InlineData("-100")]
    [InlineData("1.5")]
    public async Task ScrollMeasurementWithAnInvalidZoomExitsWithOne(string zoom)
    {
        var (window, viewModel, view) = Show();
        var options = StartupOptions.Parse(["--measure-scroll", TestData.Pdf("thousand-pages.pdf"), "--zoom", zoom]);
        using var measurement = MeasurementRun.Create(options, viewModel, view, Sink());

        Assert.NotNull(measurement);
        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        Assert.Equal([1], _exits);
        Assert.Empty(_output.ToString());
        var error = Lines(_error);
        Assert.Single(error);
        Assert.StartsWith("error: --zoom takes a whole percentage from 10 to 800", error[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true, 100)]
    [InlineData("10", true, 10)]
    [InlineData("100", true, 100)]
    [InlineData("800", true, 800)]
    [InlineData("0400", true, 400)]
    [InlineData("9", false, 0)]
    [InlineData("801", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(" 200", false, 0)]
    [InlineData("+200", false, 0)]
    public void ZoomPercentIsAWholeNumberInTheZoomRange(string? text, bool valid, int expected)
    {
        Assert.Equal(valid, ScrollMeasurement.TryParseZoomPercent(text, out var percent));
        if (valid)
        {
            Assert.Equal(expected, percent);
        }
    }

    [Fact]
    public void ScrollDetailShowsTheMeanAndTheShareOfSlowFrames()
    {
        // Mostly a 30-fps cadence: four intervals around 33.4 ms, one at 25 ms.
        double[] intervals = [16.6, 33.4, 16.6, 33.4, 16.7, 33.3, 16.7, 33.3, 25.0, 15.0];

        Assert.Equal(50, ScrollMeasurement.ShareAbove(intervals, 20));
        Assert.Equal(40, ScrollMeasurement.ShareAbove(intervals, 30));
        Assert.Equal(0, ScrollMeasurement.ShareAbove(intervals, 33.4));
        Assert.Equal("scroll-detail zoom-pct=400 mean-ms=24.0 over-20ms-pct=50.0 over-30ms-pct=40.0 range-dips=1201", ScrollMeasurement.DetailLine(intervals, 400, 1200.2));
        Assert.EndsWith(" range-dips=0", ScrollMeasurement.DetailLine(intervals, 100, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyARunWithoutScrollRangeWarns()
    {
        Assert.Null(ScrollMeasurement.NothingToScrollWarning(0.5, 100));
        Assert.Null(ScrollMeasurement.NothingToScrollWarning(152_730, 100));
        Assert.StartsWith("nothing to scroll at 100 %: ", ScrollMeasurement.NothingToScrollWarning(0, 100), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ScrollMeasurementWarnsWhenTheContentFitsTheViewport()
    {
        // One page of 300 x 300 pt: 464 DIPs high with the margins at 100 %, less than the viewport of the window.
        var (window, viewModel, view) = Show();
        using var measurement = new ScrollMeasurement(viewModel, view, TestData.Pdf("vector-shapes.pdf"), Sink(), Timeout, steps: 12);

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        // Still a result (exit code 0, both lines), but the range says that nothing scrolled, and a warning says why
        // the intervals mean nothing.
        Assert.Equal([0], _exits);
        var lines = Lines(_output);
        Assert.Equal(2, lines.Count);
        Assert.Matches(ScrollLine(), lines[0]);
        var detail = ScrollDetailLine().Match(lines[1]);
        Assert.True(detail.Success, $"unexpected line '{lines[1]}'");
        Assert.Equal("0", detail.Groups[5].Value);
        Assert.Equal(0, view.ScrollViewer.Offset.Y);
        var error = Lines(_error);
        Assert.Single(error);
        Assert.StartsWith("warning: nothing to scroll at 100 %: ", error[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SetupPhasesWrapEverySubsystemInitializerInOrder()
    {
        var calls = new List<string>();
        var builder = Avalonia.AppBuilder.Configure<App>()
            .UseRuntimePlatformSubsystem(() => calls.Add("runtime"), "Runtime")
            .UseTextShapingSubsystem(() => calls.Add("text"), "Text")
            .UseRenderingSubsystem(() => calls.Add("render"), "Render")
            .UseWindowingSubsystem(() => calls.Add("windowing"), "Windowing");
        var phases = new PhaseRecorder();

        builder = Program.WithSetupPhases(builder, phases);
        builder.RuntimePlatformServicesInitializer!();
        builder.TextShapingSubsystemInitializer!();
        builder.RenderingSubsystemInitializer!();
        builder.WindowingSubsystemInitializer!();

        // The initializers still run, under their names; each is enclosed by its two phases.
        Assert.Equal(["runtime", "text", "render", "windowing"], calls);
        Assert.Equal("Runtime", builder.RuntimePlatformServicesName);
        Assert.Equal("Text", builder.TextShapingSubsystemName);
        Assert.Equal("Render", builder.RenderingSubsystemName);
        Assert.Equal("Windowing", builder.WindowingSubsystemName);
        Assert.Equal(
            ["runtime-init", "runtime-ready", "text-shaping-init", "text-shaping-ready", "render-init", "render-ready", "windowing-init", "windowing-ready"],
            phases.Lines(0).Select(line => line.Split(' ')[1]));
        Assert.NotNull(builder.AfterSetupCallback);
        Assert.NotNull(builder.AfterPlatformServicesSetupCallback);
    }
    [Fact]
    public void PercentileUsesTheNearestRank()
    {
        var hundred = Enumerable.Range(1, 100).Select(value => (double)value).ToArray();
        Assert.Equal(99, ScrollMeasurement.Percentile(hundred, 0.99));
        Assert.Equal(100, ScrollMeasurement.Percentile(hundred, 1));
        Assert.Equal(50, ScrollMeasurement.Percentile(hundred, 0.5));

        // Unsorted input; with ten values the 99th percentile is the largest.
        double[] ten = [16, 17, 15, 40, 16, 16, 18, 16, 17, 16];
        Assert.Equal(40, ScrollMeasurement.Percentile(ten, 0.99));
        Assert.Equal(7, ScrollMeasurement.Percentile([7], 0.99));
    }

    [Theory]
    [InlineData("--measure-startup", "a.pdf")]
    [InlineData("--measure-second", "a.pdf", "b.pdf")]
    [InlineData("--measure-scroll", "a.pdf")]
    public void MeasurementModesBypassTheSingleInstanceElection(params string[] args)
    {
        var options = StartupOptions.Parse(args);
        var asked = false;

        var (instance, isSecondary) = Program.ElectInstance(options, () =>
        {
            asked = true;
            return null;
        });

        // Never asked: a measured run neither hands over to a running Pagewing nor holds the lock or the pipe.
        Assert.False(asked);
        Assert.Null(instance);
        Assert.False(isSecondary);
        Assert.Equal(Path.GetFullPath("a.pdf"), Path.GetFullPath(options.MeasurementPath!));
    }

    [Fact]
    public void NormalLaunchesTakePartInTheSingleInstanceElection()
    {
        var options = StartupOptions.Parse(["a.pdf"]);
        var asked = 0;

        var (instance, isSecondary) = Program.ElectInstance(options, () =>
        {
            asked++;
            return null;
        });

        Assert.Equal(1, asked);
        Assert.Null(instance);
        Assert.True(isSecondary);
        Assert.Null(options.MeasurementPath);
    }

    private MeasurementOutput Sink(PhaseRecorder? phases = null) => new(_output, _error, _exits.Add, phases);

    private static List<string> Lines(StringWriter writer) =>
        writer.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show()
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = window.GetVisualDescendants().OfType<DocumentView>().Single(control => control.Name == "DocumentView");
        return (window, viewModel, view);
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The measurement did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    [GeneratedRegex(@"^second-sharp (\d+\.\d)$")]
    private static partial Regex SecondSharpLine();

    [GeneratedRegex(@"^phase [a-z-]+ \d+\.\d$")]
    private static partial Regex PhaseLine();

    [GeneratedRegex(@"^scroll frames=(\d+) p99-ms=(\d+\.\d) max-ms=(\d+\.\d)$")]
    private static partial Regex ScrollLine();

    [GeneratedRegex(@"^scroll-detail zoom-pct=(\d+) mean-ms=(\d+\.\d) over-20ms-pct=(\d+\.\d) over-30ms-pct=(\d+\.\d) range-dips=(\d+)$")]
    private static partial Regex ScrollDetailLine();

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
