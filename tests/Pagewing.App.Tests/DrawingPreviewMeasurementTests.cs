using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Measurement of Milestone 10 (<c>Docs/DECISIONS.md</c>, "Zeichnen: Messwerte (M10)"): what the live preview of a freehand
/// stroke costs per pointer move while the stroke has 100, 1,000 and 5,000 points. Runs only with
/// <c>PAGEWING_MEASURE_OUT=&lt;folder&gt;</c> (best in Release) and writes <c>drawing-preview-measurements.txt</c> there;
/// never asserts a time.
/// </summary>
/// <remarks>
/// What this can show: the time of a pointer move through the real input path of the window (hit test, gesture, the new point,
/// the invalidation), and the time to record and rasterise the drawing layer with <c>n</c> segments on the CPU (software Skia,
/// the whole layer). What it cannot show: the real compositor and the GPU (the headless platform has none), and the time from
/// the pointer event to the pixel on a screen. Those need a manual measurement on the target machine (see DECISIONS).
/// </remarks>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DrawingPreviewMeasurementTests : IDisposable
{
    private const RawInputModifiers Left = RawInputModifiers.LeftMouseButton;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-preview-" + Guid.NewGuid().ToString("N"))).FullName;

    public DrawingPreviewMeasurementTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    [AvaloniaFact]
    public async Task MeasuresThePreviewOfAFreehandStroke()
    {
        var output = Environment.GetEnvironmentVariable("PAGEWING_MEASURE_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "Measurement only: set PAGEWING_MEASURE_OUT=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Pagewing drawing preview measurements, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"OS {Environment.OSVersion}, {Environment.ProcessorCount} logical processors, {(IsRelease() ? "Release" : "Debug")} build of the tests, headless Avalonia with software Skia (no compositor, no GPU)");
        report.AppendLine("window 1024 x 720 DIP, text-dense.pdf at 100 % (page 612 x 792 pt), freehand tool, the stroke wanders over the page; ms, median / max of 20");
        report.AppendLine("(1) pointer move: window.MouseMove through the input path (hit test, gesture, the new point, InvalidateVisual) without drawing");
        report.AppendLine("(2) overlay render: the drawing layer drawn to a bitmap (RenderTargetBitmap.Render of the whole layer: Render() records one DrawLine per segment, Skia rasterises them)");
        report.AppendLine("(1b) the same move again at the same position: the gesture ignores it (no new point, no invalidation); what is left is the cost of the input itself");
        report.AppendLine("(3) overlay render with a stroke of a few points (the fixed cost of the layer without the stroke)");
        report.AppendLine();

        var settings = UserSettings.Detached();
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = window.GetVisualDescendants().OfType<DocumentView>().First(v => v.Name == "DocumentView");

        var path = System.IO.Path.Combine(_folder, "dense.pdf");
        File.Copy(TestData.Pdf("text-dense.pdf"), path);
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);

        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        Settle(window);

        // The path of the stroke: a Lissajous curve over the first page, steps of up to 5 pt, inside the visible part of the page.
        Point At(int i) => ScreenPoint(view, window, new Point(306 + (240 * Math.Sin(i * 0.0113)), 200 + (150 * Math.Cos(i * 0.0171))));

        window.MouseDown(At(0), MouseButton.Left);
        Settle(window);
        var overlay = view.DrawingOverlay ?? throw new InvalidOperationException("No drawing layer after the press.");
        var count = 1;
        var lines = new StringBuilder();
        foreach (var target in new[] { 100, 1_000, 5_000 })
        {
            while (count < target - 20)
            {
                window.MouseMove(At(count), Left);
                count++;
            }

            Settle(window);
            var moves = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var started = Stopwatch.GetTimestamp();
                window.MouseMove(At(count), Left);
                moves.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                count++;
            }

            Settle(window);

            // The same move again: the gesture ignores a point that is not 0.25 pt from the last one and draws nothing new.
            var ignored = new List<double>();
            var last = At(count - 1);
            for (var i = 0; i < 20; i++)
            {
                var started = Stopwatch.GetTimestamp();
                window.MouseMove(last, Left);
                ignored.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            Settle(window);
            var renders = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var started = Stopwatch.GetTimestamp();
                using var frame = FramePixels.Snapshot(overlay);
                renders.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            lines.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    {target,5} points in the stroke: (1) pointer move {Median(moves),6:0.00} / {moves.Max(),6:0.00} | (1b) same position again {Median(ignored),6:0.00} / {ignored.Max(),6:0.00} | (2) overlay render {Median(renders),7:0.00} / {renders.Max(),7:0.00}"));
        }

        window.MouseUp(At(count), MouseButton.Left);
        Settle(window);

        // The fixed cost of the layer: a stroke of two points.
        window.MouseDown(At(0), MouseButton.Left);
        Settle(window);
        window.MouseMove(At(1), Left);
        window.MouseMove(At(3), Left);
        Settle(window);
        var fewMoves = new List<double>();
        for (var i = 4; i < 24; i++)
        {
            var started = Stopwatch.GetTimestamp();
            window.MouseMove(At(i), Left);
            fewMoves.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        Settle(window);
        var empty = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using var frame = FramePixels.Snapshot(overlay);
            empty.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        window.MouseUp(At(23), MouseButton.Left);
        Settle(window);

        report.Append(lines);
        report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    {3,5} to 23 points in the stroke: (1) pointer move {Median(fewMoves),6:0.00} / {fewMoves.Max(),6:0.00} | (3) overlay render {Median(empty),7:0.00} / {empty.Max(),7:0.00}"));
        report.AppendLine();
        report.AppendLine("Not measured: the compositor (Render() is recorded once per invalidation and the compositor draws only the dirty rectangle, see DrawingOverlay),");
        report.AppendLine("the GPU path, and the time from the pointer event to the new pixel on a screen. The headless platform has neither.");
        await File.WriteAllTextAsync(System.IO.Path.Combine(output, "drawing-preview-measurements.txt"), report.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), TestContext.Current.CancellationToken);
    }

    private static bool IsRelease() =>
        typeof(DrawingPreviewMeasurementTests).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>The window point of a point on page 1 (PDF points from the top left of the page as displayed).</summary>
    private static Point ScreenPoint(DocumentView view, Window window, Point pagePoint)
    {
        var layout = view.Layout!;
        var rect = layout.GetPageRect(0);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var canvasPoint = new Point(rect.X - view.Canvas.Viewport.X + (pagePoint.X * scale), rect.Y - view.Canvas.Viewport.Y + (pagePoint.Y * scale));
        return view.Canvas.TranslatePoint(canvasPoint, window)!.Value;
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
                throw new TimeoutException("The window did not reach the expected state.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
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

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
