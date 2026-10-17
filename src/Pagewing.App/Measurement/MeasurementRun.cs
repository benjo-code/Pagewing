using System.ComponentModel;
using Avalonia.Threading;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Measurement;

/// <summary>Where a measurement writes its result line and its error, and how it ends the app.</summary>
/// <param name="Output">Receives the result line (stdout in the app).</param>
/// <param name="Error">Receives <c>error: …</c> (stderr in the app).</param>
/// <param name="Exit">Asked once to end the app with the exit code.</param>
/// <param name="Phases">The startup phases written after <c>first-sharp</c> (<see cref="StartupPhases"/>), or <see langword="null"/>.</param>
internal sealed record MeasurementOutput(TextWriter Output, TextWriter Error, Action<int> Exit, PhaseRecorder? Phases = null);

/// <summary>
/// A run of one of the hidden measurement switches (<c>--measure-startup</c>, <c>--measure-second</c>,
/// <c>--measure-scroll</c>, see <c>bench/startup/README.md</c>): opens its file in the window, waits for the first
/// sharp image of page 1 to be drawn, measures, writes one result line and asks to exit with 0. A file that does not
/// open ends with exit code 1, no result within the time limit with 3; both write <c>error: …</c>.
/// </summary>
/// <remarks>UI thread only, except the time limit, which posts to it.</remarks>
internal abstract class MeasurementRun : IDisposable
{
    /// <summary>Exit code of a successful measurement.</summary>
    public const int Success = 0;

    /// <summary>Exit code of a measurement whose file could not be opened.</summary>
    public const int Failure = 1;

    /// <summary>Exit code of a measurement that did not finish within its time limit.</summary>
    public const int TimedOut = 3;

    /// <summary>The time limit of a measurement in the app.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly MeasurementOutput _output;
    private readonly TimeSpan _timeout;
    private Timer? _timer;
    private bool _started;

    protected MeasurementRun(MainViewModel viewModel, DocumentView view, string path, MeasurementOutput output, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(output);
        ViewModel = viewModel;
        View = view;
        Path = path;
        _output = output;
        _timeout = timeout;
    }

    /// <summary>Gets whether the measurement wrote its result or its error and asked to exit.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Gets the view model of the window.</summary>
    protected MainViewModel ViewModel { get; }

    /// <summary>Gets the document view of the window.</summary>
    protected DocumentView View { get; }

    /// <summary>Gets the file the measurement opens first, as given on the command line.</summary>
    protected string Path { get; }

    /// <summary>
    /// Creates the measurement the options ask for (<c>--measure-startup</c> before <c>--measure-second</c> before
    /// <c>--measure-scroll</c>), or <see langword="null"/> without a measurement switch.
    /// </summary>
    public static MeasurementRun? Create(StartupOptions options, MainViewModel viewModel, DocumentView view, MeasurementOutput output)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options switch
        {
            { MeasureStartup: { } path } => new StartupMeasurement(viewModel, view, path, null, output, DefaultTimeout, StartupMeasurement.DefaultSettleDelay),
            { MeasureSecond: var (first, second) } => new StartupMeasurement(viewModel, view, first, second, output, DefaultTimeout, StartupMeasurement.DefaultSettleDelay),
            { MeasureScroll: { } path } => new ScrollMeasurement(viewModel, view, path, output, DefaultTimeout, ScrollMeasurement.DefaultSteps),
            _ => null,
        };
    }

    /// <summary>
    /// Starts the measurement: from now on the time limit runs. Opens the file, unless the session already opens it
    /// (<see cref="Program.Main"/> starts opening the file before the window exists, as for a normal launch).
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        View.FirstSharpTileDrawn += OnFirstSharpTileDrawn;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _timer = new Timer(_ => Post(() => Fail(TimedOut, $"no sharp image of page 1 within {_timeout.TotalSeconds:0.###} s")), null, _timeout, Timeout.InfiniteTimeSpan);

        if (string.IsNullOrWhiteSpace(Path))
        {
            Fail(Failure, "no file to measure");
            return;
        }

        Open(Path);
        CheckState();
    }

    public void Dispose()
    {
        Unsubscribe();
        GC.SuppressFinalize(this);
    }

    /// <summary>The first sharp image of page 1 of the document now shown was drawn (UI thread).</summary>
    protected abstract void OnFirstSharpImage();

    /// <summary>Opens <paramref name="path"/>, unless the session already holds or opens exactly that file.</summary>
    protected void Open(string path)
    {
        if (!string.Equals(ViewModel.Session.Path, FullPathOrAsGiven(path), StringComparison.Ordinal))
        {
            _ = ViewModel.OpenAsync(path);
        }
    }

    /// <summary>Writes the result line and asks to exit with 0.</summary>
    protected void Succeed(string line)
    {
        Report(line);
        Succeed();
    }

    /// <summary>Asks to exit with 0 (the result lines are written).</summary>
    protected void Succeed()
    {
        if (IsFinished)
        {
            return;
        }

        IsFinished = true;
        Unsubscribe();
        _output.Exit(Success);
    }

    /// <summary>Writes the result line without exiting (a measurement with more than one line).</summary>
    protected void Report(string line)
    {
        if (!IsFinished)
        {
            _output.Output.WriteLine(line);
            _output.Output.Flush();
        }
    }

    /// <summary>
    /// Writes the startup phases (<c>phase &lt;name&gt; &lt;ms&gt;</c>), if there are any. Only after the result line
    /// that the script times: reading the process start costs time of its own.
    /// </summary>
    protected void ReportPhases()
    {
        if (IsFinished || _output.Phases is not { } phases)
        {
            return;
        }

        foreach (var line in phases.Lines(PhaseRecorder.ProcessStartTimestamp()))
        {
            _output.Output.WriteLine(line);
        }

        _output.Output.Flush();
    }

    /// <summary>Writes <c>error: <paramref name="message"/></c> and asks to exit with <paramref name="exitCode"/>.</summary>
    protected void Fail(int exitCode, string message)
    {
        if (IsFinished)
        {
            return;
        }

        IsFinished = true;
        Unsubscribe();
        _output.Error.WriteLine($"error: {message}");
        _output.Error.Flush();
        _output.Exit(exitCode);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread; does nothing while the dispatcher shuts down.</summary>
    protected static void Post(Action action)
    {
        try
        {
            Dispatcher.UIThread.Post(action);
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: the app ends anyway.
        }
    }

    private static string FullPathOrAsGiven(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return path;
        }
    }

    private void OnFirstSharpTileDrawn()
    {
        if (!IsFinished)
        {
            OnFirstSharpImage();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.State))
        {
            CheckState();
        }
    }

    /// <summary>A file that fails or needs a password never shows a sharp image: the measurement fails at once.</summary>
    private void CheckState()
    {
        var path = ViewModel.FullPath ?? Path;
        switch (ViewModel.State)
        {
            case ViewerState.Failed:
                Fail(Failure, $"cannot open {path}: {ViewModel.Error}");
                break;
            case ViewerState.PasswordRequired or ViewerState.PasswordIncorrect:
                Fail(Failure, $"cannot open {path}: it needs a password");
                break;
        }
    }

    private void Unsubscribe()
    {
        View.FirstSharpTileDrawn -= OnFirstSharpTileDrawn;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _timer?.Dispose();
        _timer = null;
    }
}
