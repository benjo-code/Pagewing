using System.Diagnostics;
using System.Globalization;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;

namespace Pagewing.App.Measurement;

/// <summary>
/// <c>--measure-startup &lt;pdf&gt;</c>: writes <c>first-sharp</c> once the first sharp image of page 1 was drawn; the
/// time since the process start is taken by the caller (<c>bench/startup/startup.cs</c>).
/// <c>--measure-second &lt;a&gt; &lt;b&gt;</c>: after <c>first-sharp</c> of <c>a</c> opens <c>b</c> the way a later launch
/// hands it over (<see cref="MainViewModel.OnExternalOpen"/>) and writes <c>second-sharp &lt;ms&gt;</c>, the time from that
/// call to the drawn first sharp image of <c>b</c> (without process start and pipe, which have their own test).
/// </summary>
internal sealed class StartupMeasurement : MeasurementRun
{
    /// <summary>
    /// Pause between the first sharp image of <c>a</c> and opening <c>b</c>, so that the work still queued for
    /// <c>a</c> (further tiles, page sizes) is done, as in an instance that runs already. Not part of the measured time.
    /// </summary>
    public static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromMilliseconds(500);

    private readonly string? _secondPath;
    private readonly TimeSpan _settleDelay;
    private long _secondStart;
    private bool _waitingForSecond;

    /// <param name="viewModel">The view model of the window.</param>
    /// <param name="view">The document view of the window.</param>
    /// <param name="path">The file opened first.</param>
    /// <param name="secondPath">The file opened as a later launch would, or <see langword="null"/> for <c>--measure-startup</c>.</param>
    /// <param name="output">Where the lines go and how the app ends.</param>
    /// <param name="timeout">Time limit of the whole measurement.</param>
    /// <param name="settleDelay">Pause before opening <paramref name="secondPath"/>.</param>
    public StartupMeasurement(MainViewModel viewModel, DocumentView view, string path, string? secondPath, MeasurementOutput output, TimeSpan timeout, TimeSpan settleDelay)
        : base(viewModel, view, path, output, timeout)
    {
        _secondPath = secondPath;
        _settleDelay = settleDelay;
    }

    protected override void OnFirstSharpImage()
    {
        if (_waitingForSecond)
        {
            var milliseconds = Stopwatch.GetElapsedTime(_secondStart).TotalMilliseconds;
            Succeed(string.Create(CultureInfo.InvariantCulture, $"second-sharp {milliseconds:0.0}"));
            return;
        }

        if (_secondPath is null)
        {
            Succeed("first-sharp");
            return;
        }

        Report("first-sharp");
        if (string.IsNullOrWhiteSpace(_secondPath))
        {
            Fail(Failure, "no second file to measure");
            return;
        }

        _ = Task.Delay(_settleDelay).ContinueWith(_ => Post(OpenSecond), TaskScheduler.Default);
    }

    private void OpenSecond()
    {
        if (IsFinished)
        {
            return;
        }

        _waitingForSecond = true;
        _secondStart = Stopwatch.GetTimestamp();
        ViewModel.OnExternalOpen([_secondPath!]);
    }
}
