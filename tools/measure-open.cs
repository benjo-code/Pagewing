#:property PublishAot=false
#:property AllowUnsafeBlocks=true

// Open-time measurement for the comparison of PDF viewers (Windows only).
//   dotnet run tools/measure-open.cs -- --exe <path> --args "<arguments with {pdf}>" --pdf <file> --runs 10 --out <csv>
//        [--window-title-contains <text>] [--second-pdf <file>] [--pause-ms 2000] [--warmup 0]
//        [--timeout-ms 30000] [--settle-ms 500] [--poll-ms 10] [--label <name>] [--case <name>] [--allow-running] [--trace]
//   dotnet run tools/measure-open.cs -- --selftest
// Run from the repository root. The protocol is bench/compare/README.md.
//
// Per run the tool starts the application, polls the window every ~10 ms and copies its client area from the
// SCREEN (BitBlt of the desktop DC into a DIB section; PrintWindow returns black images for GPU windows). It
// reports, in milliseconds since just before process start:
//   t_first_content    first frame that shows page content (near-white page area and dark text pixels inside it)
//   t_visually_complete  time of the last visible change before settle-ms (500) of stillness
// --out appends to an existing CSV of the same format (one file per machine and day collects all apps and cases).
// Pixels are analysed in memory only; no screenshot is ever written to disk.
// Windows only: Win32 interop (user32, gdi32, dwmapi, winmm) is declared in this file. The window must be visible
// and not covered by other windows, because a screen copy also contains whatever lies on top of it.
// With --second-pdf the tool measures the second file in the already running instance: the first file is opened
// and settled, then a second process is started with the second file and the time is measured from that launch.
//
// Exit codes: 0 success, 1 measurement failure (no window or no page content within the timeout), 2 usage error.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

return Cli.Run(args);

static class Cli
{
    private const string Usage =
        "Aufruf: dotnet run tools/measure-open.cs -- --exe <Pfad> --args \"<Argumente mit {pdf} oder {pdfurl}>\" --pdf <Datei> --out <csv>\n" +
        "          [--runs 10] [--window-title-contains <Text>] [--second-pdf <Datei>] [--pause-ms 2000] [--warmup 0]\n" +
        "          [--timeout-ms 30000] [--settle-ms 500] [--poll-ms 10] [--label <Name>] [--case <Name>] [--allow-running] [--trace]\n" +
        "       dotnet run tools/measure-open.cs -- --selftest";

    public static int Run(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            return SelfTest.Run();
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Das Messwerkzeug läuft nur unter Windows.");
            return 2;
        }

        var options = Options.Parse(args, out var error);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        return Measurement.Run(options);
    }
}

sealed class Options
{
    public string Exe { get; private set; } = string.Empty;
    public string ArgsTemplate { get; private set; } = "{pdf}";
    public string Pdf { get; private set; } = string.Empty;
    public string? SecondPdf { get; private set; }
    public string? TitleContains { get; private set; }
    public string Out { get; private set; } = string.Empty;
    public int Runs { get; private set; } = 10;
    public int Warmup { get; private set; }
    public int PauseMs { get; private set; } = 2000;
    public int TimeoutMs { get; private set; } = 30000;
    public int SettleMs { get; private set; } = 500;
    public int PollMs { get; private set; } = 10;
    public string Label { get; private set; } = string.Empty;
    public string CaseName { get; private set; } = string.Empty;
    public bool AllowRunning { get; private set; }
    public bool Trace { get; private set; }

    public static Options? Parse(string[] args, out string error)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--allow-running")
            {
                o.AllowRunning = true;
                continue;
            }

            if (name == "--trace")
            {
                o.Trace = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"Zu {name} fehlt der Wert.";
                return null;
            }

            var value = args[++i];
            switch (name)
            {
                case "--exe": o.Exe = value; break;
                case "--args": o.ArgsTemplate = value; break;
                case "--pdf": o.Pdf = value; break;
                case "--second-pdf": o.SecondPdf = value; break;
                case "--window-title-contains": o.TitleContains = value; break;
                case "--out": o.Out = value; break;
                case "--label": o.Label = value; break;
                case "--case": o.CaseName = value; break;
                case "--runs": if (!TryInt(value, 1, 1000, out var runs, out error, name)) { return null; } o.Runs = runs; break;
                case "--warmup": if (!TryInt(value, 0, 100, out var warmup, out error, name)) { return null; } o.Warmup = warmup; break;
                case "--pause-ms": if (!TryInt(value, 0, 600000, out var pause, out error, name)) { return null; } o.PauseMs = pause; break;
                case "--timeout-ms": if (!TryInt(value, 1000, 600000, out var timeout, out error, name)) { return null; } o.TimeoutMs = timeout; break;
                case "--settle-ms": if (!TryInt(value, 50, 60000, out var settle, out error, name)) { return null; } o.SettleMs = settle; break;
                case "--poll-ms": if (!TryInt(value, 1, 1000, out var poll, out error, name)) { return null; } o.PollMs = poll; break;
                default:
                    error = $"Unbekannte Option: {name}";
                    return null;
            }
        }

        if (o.Exe.Length == 0 || o.Pdf.Length == 0 || o.Out.Length == 0)
        {
            error = "--exe, --pdf und --out sind Pflicht.";
            return null;
        }

        if (!o.ArgsTemplate.Contains("{pdf}", StringComparison.Ordinal) && !o.ArgsTemplate.Contains("{pdfurl}", StringComparison.Ordinal))
        {
            error = "--args muss {pdf} oder {pdfurl} enthalten.";
            return null;
        }

        o.Exe = Path.GetFullPath(o.Exe);
        o.Pdf = Path.GetFullPath(o.Pdf);
        if (o.SecondPdf is not null)
        {
            o.SecondPdf = Path.GetFullPath(o.SecondPdf);
        }

        o.Out = Path.GetFullPath(o.Out);
        if (!File.Exists(o.Exe))
        {
            error = $"Programm nicht gefunden: {o.Exe}";
            return null;
        }

        foreach (var pdf in new[] { o.Pdf, o.SecondPdf }.OfType<string>())
        {
            if (!File.Exists(pdf))
            {
                error = $"PDF nicht gefunden: {pdf}";
                return null;
            }
        }

        if (o.Label.Length == 0)
        {
            o.Label = Path.GetFileNameWithoutExtension(o.Exe);
        }

        error = string.Empty;
        return o;
    }

    private static bool TryInt(string text, int min, int max, out int value, out string error, string name)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
        {
            error = string.Empty;
            return true;
        }

        error = $"{name} erwartet eine ganze Zahl von {min} bis {max}, erhalten '{text}'.";
        return false;
    }
}

/// <summary>A downsampled copy of the window's client area: one 0x00RRGGBB value per <see cref="Analysis.SampleStride"/> pixels.</summary>
sealed class Grid(int width, int height, uint[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint[] Pixels { get; } = pixels;
}

/// <summary>Pure frame analysis (no Win32), exercised by <see cref="SelfTest"/>.</summary>
static class Analysis
{
    public const int SampleStride = 4;

    // A sample is "near white" (page area) when its darkest channel is at least this.
    public const int NearWhite = 235;

    // A text pixel must be flanked left and right (or above and below) by samples whose darkest channel is at least this.
    // The flank rule keeps dark window chrome (toolbars, borders next to a white page) from counting as text.
    public const int FlankWhite = 200;

    // Luma below this counts as ink. Chrome's viewer background (luma ~85) stays above it.
    public const int InkLuma = 96;

    public const double MinWhiteShare = 0.05;
    public const int MinInkSamples = 3;

    // Frame-to-frame change: a sample changed when the summed channel difference exceeds this; a frame changed with this many samples.
    public const int ChangeChannelSum = 24;
    public const int MinChangedSamples = 3;

    // For the second-file case: the central region must differ from the baseline in this many samples.
    public const int MinRegionChangedSamples = 20;

    // The region in which page content is looked for, as fractions of the client area (keeps tab strips and toolbars out).
    public const double RegionLeft = 0.2;
    public const double RegionRight = 0.8;
    public const double RegionTop = 0.12;
    public const double RegionBottom = 0.92;

    public static int MinChannel(uint p) => (int)Math.Min(Math.Min(p & 0xFF, (p >> 8) & 0xFF), (p >> 16) & 0xFF);

    public static int Luma(uint p) => (int)((((p >> 16) & 0xFF) * 299 + ((p >> 8) & 0xFF) * 587 + (p & 0xFF) * 114) / 1000);

    public static (int X0, int Y0, int X1, int Y1) Region(Grid g) =>
        ((int)(g.Width * RegionLeft), (int)(g.Height * RegionTop), (int)(g.Width * RegionRight), (int)(g.Height * RegionBottom));

    public static bool HasPageContent(Grid g, out double whiteShare, out int inkSamples)
    {
        var (x0, y0, x1, y1) = Region(g);
        var w = g.Width;
        var px = g.Pixels;
        var total = 0;
        var white = 0;
        inkSamples = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = (y * w) + x;
                var p = px[i];
                total++;
                if (MinChannel(p) >= NearWhite)
                {
                    white++;
                }

                if (Luma(p) < InkLuma && x > 0 && y > 0 && x < w - 1 && y < g.Height - 1)
                {
                    var flankedHorizontally = MinChannel(px[i - 1]) >= FlankWhite && MinChannel(px[i + 1]) >= FlankWhite;
                    var flankedVertically = MinChannel(px[i - w]) >= FlankWhite && MinChannel(px[i + w]) >= FlankWhite;
                    if (flankedHorizontally || flankedVertically)
                    {
                        inkSamples++;
                    }
                }
            }
        }

        whiteShare = total == 0 ? 0 : (double)white / total;
        return total > 0 && whiteShare >= MinWhiteShare && inkSamples >= MinInkSamples;
    }

    /// <summary>Number of samples that differ visibly; <see cref="int.MaxValue"/> when the grids differ in size.</summary>
    public static int CountChanged(Grid a, Grid b, bool centralOnly)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return int.MaxValue;
        }

        var (x0, y0, x1, y1) = centralOnly ? Region(a) : (0, 0, a.Width, a.Height);
        var changed = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = (y * a.Width) + x;
                var pa = a.Pixels[i];
                var pb = b.Pixels[i];
                if (pa == pb)
                {
                    continue;
                }

                var diff = Math.Abs((int)(pa & 0xFF) - (int)(pb & 0xFF))
                    + Math.Abs((int)((pa >> 8) & 0xFF) - (int)((pb >> 8) & 0xFF))
                    + Math.Abs((int)((pa >> 16) & 0xFF) - (int)((pb >> 16) & 0xFF));
                if (diff > ChangeChannelSum)
                {
                    changed++;
                }
            }
        }

        return changed;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Coefficient of variation in percent (standard deviation / mean).</summary>
    public static double CoefficientOfVariation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return mean == 0 ? 0 : Math.Sqrt(variance) / mean * 100;
    }

    /// <summary>Replaces {pdf} (path, quoted when it contains spaces and is not already inside quotes) and {pdfurl} (file:/// URL).</summary>
    public static string ExpandArguments(string template, string pdfPath)
    {
        var sb = new StringBuilder(template.Length + pdfPath.Length);
        for (var i = 0; i < template.Length;)
        {
            if (string.CompareOrdinal(template, i, "{pdf}", 0, 5) == 0)
            {
                var quoted = i > 0 && template[i - 1] == '"';
                sb.Append(!quoted && pdfPath.Contains(' ') ? $"\"{pdfPath}\"" : pdfPath);
                i += 5;
            }
            else if (string.CompareOrdinal(template, i, "{pdfurl}", 0, 8) == 0)
            {
                sb.Append(new Uri(pdfPath).AbsoluteUri);
                i += 8;
            }
            else
            {
                sb.Append(template[i++]);
            }
        }

        return sb.ToString();
    }

    public static string CsvField(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// Decides from a stream of frames when page content first appeared and when the picture stopped changing.
/// Pure state machine: times are passed in, so it is testable without a window.
/// </summary>
sealed class StageTracker(Grid? baseline, double settleMs, double maxSettleWaitMs)
{
    public Grid? Last { get; private set; }
    public double? FirstContentMs { get; private set; }
    public double LastChangeMs { get; private set; }
    public bool Done { get; private set; }
    public bool Settled { get; private set; }

    public double VisuallyCompleteMs => Math.Max(LastChangeMs, FirstContentMs ?? 0);

    public void Feed(double tMs, Grid frame)
    {
        if (Done)
        {
            return;
        }

        // Stillness is judged on the page area only: tab strips, avatars and spinners of the window frame keep changing in browsers.
        var changed = Last is null || Analysis.CountChanged(Last, frame, centralOnly: true) >= Analysis.MinChangedSamples;
        Last = frame;
        if (changed)
        {
            LastChangeMs = tMs;
        }

        if (FirstContentMs is null)
        {
            var content = Analysis.HasPageContent(frame, out _, out _);

            // Second-file case: the old document is still displayed, so the page area itself must have changed.
            if (content && baseline is not null)
            {
                content = Analysis.CountChanged(baseline, frame, centralOnly: true) >= Analysis.MinRegionChangedSamples;
            }

            if (content)
            {
                FirstContentMs = tMs;
            }
        }

        if (FirstContentMs is { } first)
        {
            if (tMs - LastChangeMs >= settleMs)
            {
                Done = true;
                Settled = true;
            }
            else if (tMs - first >= maxSettleWaitMs)
            {
                Done = true;
            }
        }
    }
}

sealed record StageResult(double? WindowFoundMs, double? FirstContentMs, double? VisuallyCompleteMs, bool Settled, string? Failure, Grid? Last, double FrameIntervalMs);

sealed record RunResult(StageResult Main, StageResult? FirstFile);

/// <summary>The processes started for one run and everything of the same image name that appeared since.</summary>
sealed class ProcessGroup(string imageName)
{
    private readonly HashSet<int> _preExisting = Process.GetProcessesByName(imageName).Select(p => p.Id).ToHashSet();
    private readonly HashSet<int> _tracked = [];
    private readonly List<Process> _started = [];
    private double _lastRefreshMs = double.NegativeInfinity;

    public static ProcessGroup? Current { get; set; }

    public int PreExistingCount => _preExisting.Count;

    public HashSet<uint> Pids => _tracked.Select(p => (uint)p).ToHashSet();

    public void Start(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Prozess ließ sich nicht starten.");
        _started.Add(process);
        _tracked.Add(process.Id);
    }

    /// <summary>Adds processes of the same image name that did not exist before the run (browsers and updaters spawn several).</summary>
    public void Refresh(double nowMs, double everyMs)
    {
        if (nowMs - _lastRefreshMs < everyMs)
        {
            return;
        }

        _lastRefreshMs = nowMs;
        foreach (var p in Process.GetProcessesByName(imageName))
        {
            if (!_preExisting.Contains(p.Id))
            {
                _tracked.Add(p.Id);
            }

            p.Dispose();
        }
    }

    public void KillAll()
    {
        Refresh(double.PositiveInfinity, 0);
        foreach (var p in _started)
        {
            Kill(p);
        }

        foreach (var id in _tracked.ToArray())
        {
            try
            {
                using var p = Process.GetProcessById(id);
                Kill(p);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }

            p.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Exited in the meantime or access denied; nothing more to do.
        }
    }
}

static class Measurement
{
    private const double MaxSettleWaitMs = 10000;
    private const string CsvHeader = "label,case,run,t_first_content_ms,t_visually_complete_ms,window_found_ms,settled,frame_interval_ms,first_file_t_first_content_ms,first_file_t_visually_complete_ms";

    public static int Run(Options o)
    {
        var imageName = Path.GetFileNameWithoutExtension(o.Exe);
        var running = Process.GetProcessesByName(imageName).Length;
        if (running > 0 && !o.AllowRunning)
        {
            Console.Error.WriteLine(
                $"Es laufen bereits {running} Prozesse namens '{imageName}'. Sie würden die Messung verfälschen (Übergabe an die laufende Instanz). " +
                "Alle beenden oder --allow-running angeben, falls die gemessene Instanz einen eigenen Profilordner nutzt.");
            return 2;
        }

        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: real pixel coordinates
        Native.timeBeginPeriod(1);
        Console.CancelKeyPress += (_, _) => ProcessGroup.Current?.KillAll();

        if (File.Exists(o.Out) && new FileInfo(o.Out).Length > 0 && !File.ReadLines(o.Out).First().StartsWith(CsvHeader, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{o.Out} existiert und hat ein anderes Spaltenformat; keine fremde Datei anhängen.");
            return 2;
        }

        var results = new List<RunResult>();
        try
        {
            using var capture = new ScreenCapture();
            for (var i = 0; i < o.Warmup + o.Runs; i++)
            {
                var warmup = i < o.Warmup;
                var result = MeasureOne(o, capture, imageName);
                var failure = result.Main.Failure ?? result.FirstFile?.Failure;
                var tag = warmup ? $"Aufwärmlauf {i + 1}/{o.Warmup}" : $"Lauf {i - o.Warmup + 1}/{o.Runs}";
                if (failure is not null)
                {
                    Console.Error.WriteLine($"{tag}: FEHLER: {failure}");
                    WriteCsv(o, results);
                    Report(o, results);
                    return 1;
                }

                Console.WriteLine(warmup
                    ? $"{tag}: übersprungen (nur Aufwärmen)"
                    : $"{tag}: t_first_content {result.Main.FirstContentMs:F1} ms, t_visually_complete {result.Main.VisuallyCompleteMs:F1} ms" +
                      (result.Main.Settled ? string.Empty : " (Bild wurde nicht still, Wert = letzte Änderung)"));
                if (!warmup)
                {
                    results.Add(result);
                }

                if (i < o.Warmup + o.Runs - 1)
                {
                    Thread.Sleep(o.PauseMs);
                }
            }
        }
        finally
        {
            Native.timeEndPeriod(1);
        }

        WriteCsv(o, results);
        Report(o, results);
        return 0;
    }

    private static RunResult MeasureOne(Options o, ScreenCapture capture, string imageName)
    {
        var group = new ProcessGroup(imageName);
        ProcessGroup.Current = group;
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            group.Start(o.Exe, Analysis.ExpandArguments(o.ArgsTemplate, o.Pdf));
            var first = RunStage(o, capture, group, t0, baseline: null, lockedWindow: IntPtr.Zero);
            if (o.SecondPdf is null || first.Failure is not null)
            {
                return new RunResult(first, null);
            }

            // Second file into the running instance: same window, measured from the launch of the second process.
            var window = Native.FindWindow(group.Pids, o.TitleContains)?.Handle ?? IntPtr.Zero;
            if (window == IntPtr.Zero)
            {
                return new RunResult(first with { Failure = "Fenster der ersten Datei ging vor dem Start der zweiten verloren." }, null);
            }

            var t1 = Stopwatch.GetTimestamp();
            group.Start(o.Exe, Analysis.ExpandArguments(o.ArgsTemplate, o.SecondPdf));
            var second = RunStage(o, capture, group, t1, baseline: first.Last, lockedWindow: window);
            return new RunResult(second, first);
        }
        finally
        {
            group.KillAll();
            ProcessGroup.Current = null;
        }
    }

    private static StageResult RunStage(Options o, ScreenCapture capture, ProcessGroup group, long t0, Grid? baseline, IntPtr lockedWindow)
    {
        var tracker = new StageTracker(baseline, o.SettleMs, MaxSettleWaitMs);
        double? windowFound = null;
        Grid? spare = null;
        var frameTimes = new List<double>();
        var nextTick = 0.0;
        while (true)
        {
            var now = Elapsed(t0);
            if (now > o.TimeoutMs)
            {
                return new StageResult(windowFound, tracker.FirstContentMs, null, false,
                    windowFound is null
                        ? $"Kein passendes Fenster innerhalb von {o.TimeoutMs} ms."
                        : $"Kein Seiteninhalt innerhalb von {o.TimeoutMs} ms.",
                    tracker.Last, FrameInterval(frameTimes));
            }

            group.Refresh(now, 100);
            var window = lockedWindow != IntPtr.Zero
                ? Native.DescribeWindow(lockedWindow)
                : Native.FindWindow(group.Pids, o.TitleContains);
            if (window is not null)
            {
                windowFound ??= now;
                // The tracker still compares against its last frame, so only the frame before that may donate its array.
                var previous = tracker.Last;
                var frame = capture.Capture(window.X, window.Y, window.Width, window.Height, spare);
                if (frame is not null)
                {
                    spare = previous;
                    // The snapshot is taken somewhere during the screen copy; its midpoint halves the worst-case error.
                    var frameMs = (capture.LastCopyMidpoint - t0) * 1000.0 / Stopwatch.Frequency;
                    frameTimes.Add(frameMs);
                    tracker.Feed(frameMs, frame);
                    if (o.Trace)
                    {
                        Analysis.HasPageContent(frame, out var white, out var ink);
                        Console.Error.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"  trace {frameMs,8:F1} ms  {window.Width}x{window.Height}  white {white * 100,5:F1} %  ink {ink,4}  content {(tracker.FirstContentMs is null ? "no" : "yes")}  lastChange {tracker.LastChangeMs:F1}  copy {capture.LastCopyMs:F1} ms"));
                    }

                    if (tracker.Done)
                    {
                        return new StageResult(windowFound, tracker.FirstContentMs, tracker.VisuallyCompleteMs, tracker.Settled, null, tracker.Last, FrameInterval(frameTimes));
                    }
                }
            }

            nextTick += o.PollMs;
            var wait = nextTick - Elapsed(t0);
            if (wait > 1.5)
            {
                Thread.Sleep((int)(wait - 0.5));
            }
            else if (wait < 0)
            {
                nextTick = Elapsed(t0);
            }
        }
    }

    private static double Elapsed(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    private static double FrameInterval(List<double> frameTimes) =>
        frameTimes.Count < 2 ? double.NaN : Analysis.Median(frameTimes.Zip(frameTimes.Skip(1), (a, b) => b - a).ToList());

    private static void WriteCsv(Options o, List<RunResult> results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(o.Out)!);
        var sb = new StringBuilder();
        var append = File.Exists(o.Out) && new FileInfo(o.Out).Length > 0;
        if (!append)
        {
            sb.Append(CsvHeader).Append('\n');
        }
        for (var i = 0; i < results.Count; i++)
        {
            var main = results[i].Main;
            var first = results[i].FirstFile;
            sb.Append(Analysis.CsvField(o.Label)).Append(',')
                .Append(Analysis.CsvField(o.CaseName)).Append(',')
                .Append(i + 1).Append(',')
                .Append(Ms(main.FirstContentMs)).Append(',')
                .Append(Ms(main.VisuallyCompleteMs)).Append(',')
                .Append(Ms(main.WindowFoundMs)).Append(',')
                .Append(main.Settled ? "true" : "false").Append(',')
                .Append(Ms(double.IsNaN(main.FrameIntervalMs) ? null : main.FrameIntervalMs)).Append(',')
                .Append(Ms(first?.FirstContentMs)).Append(',')
                .Append(Ms(first?.VisuallyCompleteMs)).Append('\n');
        }

        File.AppendAllText(o.Out, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine(append ? $"An vorhandene CSV angehängt: {o.Out}" : $"CSV geschrieben: {o.Out}");
    }

    private static string Ms(double? value) => value is { } v ? v.ToString("F1", CultureInfo.InvariantCulture) : string.Empty;

    private static void Report(Options o, List<RunResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        Console.WriteLine($"{o.Label}: {results.Count} Läufe");
        var intervals = results.Select(r => r.Main.FrameIntervalMs).Where(v => !double.IsNaN(v)).ToList();
        if (intervals.Count > 0)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Zeitauflösung: Median des Bildabstands {Analysis.Median(intervals):F0} ms (Maximum der Läufe {intervals.Max():F0} ms); Werte sind auf diese Auflösung genau."));
        }

        PrintStats("t_first_content", results.Select(r => r.Main.FirstContentMs).OfType<double>().ToList());
        PrintStats("t_visually_complete", results.Select(r => r.Main.VisuallyCompleteMs).OfType<double>().ToList());
        if (results.Count > 2)
        {
            Console.WriteLine("ohne Lauf 1 (Kaltstart-Effekte):");
            PrintStats("t_first_content", results.Skip(1).Select(r => r.Main.FirstContentMs).OfType<double>().ToList());
            PrintStats("t_visually_complete", results.Skip(1).Select(r => r.Main.VisuallyCompleteMs).OfType<double>().ToList());
        }
    }

    private static void PrintStats(string name, List<double> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var median = Analysis.Median(values);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {name,-20} Median {median,7:F1} ms  min {values.Min(),7:F1}  max {values.Max(),7:F1}  Spanne/Median {(values.Max() - values.Min()) / median * 100,5:F1} %  Variationskoeffizient {Analysis.CoefficientOfVariation(values),5:F1} %"));
    }
}

sealed record WindowInfo(IntPtr Handle, int X, int Y, int Width, int Height);

/// <summary>Copies the screen area of a window into a DIB section and samples it. Windows only.</summary>
sealed unsafe class ScreenCapture : IDisposable
{
    private readonly IntPtr _screenDc = Native.GetDC(IntPtr.Zero);
    private readonly IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _previousBitmap;
    private void* _bits;
    private int _width;
    private int _height;

    /// <summary>Duration of the last screen copy in milliseconds (shown by --trace; it bounds the time resolution).</summary>
    public double LastCopyMs { get; private set; }

    /// <summary>Midpoint of the last screen copy in <see cref="Stopwatch"/> ticks.</summary>
    public long LastCopyMidpoint { get; private set; }

    public ScreenCapture()
    {
        _memoryDc = Native.CreateCompatibleDC(_screenDc);
    }

    /// <summary>Captures the page area of the given window rectangle and returns it sampled; <paramref name="recycle"/> donates its pixel array when the size matches.</summary>
    public Grid? Capture(int x, int y, int width, int height, Grid? recycle)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if (width != _width || height != _height)
        {
            ReleaseBitmap();
            var header = new Native.BitmapInfoHeader
            {
                Size = (uint)sizeof(Native.BitmapInfoHeader),
                Width = width,
                Height = -height, // top-down
                Planes = 1,
                BitCount = 32,
            };
            _dib = Native.CreateDIBSection(_memoryDc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero)
            {
                return null;
            }

            _bits = bits.ToPointer();
            _previousBitmap = Native.SelectObject(_memoryDc, _dib);
            _width = width;
            _height = height;
        }

        var gridWidth = width / Analysis.SampleStride;
        var gridHeight = height / Analysis.SampleStride;
        var pixels = recycle is not null && recycle.Pixels.Length == gridWidth * gridHeight
            ? recycle.Pixels
            : new uint[gridWidth * gridHeight];
        var grid = new Grid(gridWidth, gridHeight, pixels);

        // Only the page area is copied and sampled (everything the analysis looks at); the rest of the grid stays 0.
        // That keeps the screen copy small, which bounds the polling rate.
        var (x0, y0, x1, y1) = Analysis.Region(grid);
        var copyStart = Stopwatch.GetTimestamp();
        var left = x0 * Analysis.SampleStride;
        var top = y0 * Analysis.SampleStride;
        if (!Native.BitBlt(_memoryDc, left, top, (x1 - x0) * Analysis.SampleStride, (y1 - y0) * Analysis.SampleStride, _screenDc, x + left, y + top, Native.SrcCopy))
        {
            return null;
        }

        var copyEnd = Stopwatch.GetTimestamp();
        LastCopyMs = (copyEnd - copyStart) * 1000.0 / Stopwatch.Frequency;
        LastCopyMidpoint = copyStart + ((copyEnd - copyStart) / 2);

        var source = (uint*)_bits;
        for (var gy = y0; gy < y1; gy++)
        {
            var row = source + ((long)gy * Analysis.SampleStride * width);
            var target = gy * gridWidth;
            for (var gx = x0; gx < x1; gx++)
            {
                pixels[target + gx] = row[gx * Analysis.SampleStride] & 0x00FFFFFF;
            }
        }

        return grid;
    }

    public void Dispose()
    {
        ReleaseBitmap();
        Native.DeleteDC(_memoryDc);
        Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }

    private void ReleaseBitmap()
    {
        if (_dib != IntPtr.Zero)
        {
            Native.SelectObject(_memoryDc, _previousBitmap);
            Native.DeleteObject(_dib);
            _dib = IntPtr.Zero;
            _bits = null;
            _width = 0;
            _height = 0;
        }
    }
}

static partial class Native
{
    public const uint SrcCopy = 0x00CC0020;

    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int DwmwaCloaked = 14;
    private const int MinWindowWidth = 400;
    private const int MinWindowHeight = 300;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint milliseconds);

    /// <summary>The largest visible top-level window of the given processes whose title contains the filter (if any).</summary>
    public static WindowInfo? FindWindow(HashSet<uint> pids, string? titleContains)
    {
        WindowInfo? best = null;
        var title = new char[512];
        EnumWindows(
            (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains(pid) || GetWindow(hwnd, GwOwner) != IntPtr.Zero || (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0)
                {
                    return true;
                }

                if (titleContains is not null)
                {
                    var length = GetWindowTextW(hwnd, title, title.Length);
                    if (new string(title, 0, length).IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return true;
                    }
                }

                var info = DescribeWindow(hwnd);
                if (info is not null && (best is null || (long)info.Width * info.Height > (long)best.Width * best.Height))
                {
                    best = info;
                }

                return true;
            },
            IntPtr.Zero);
        return best;
    }

    /// <summary>Client area of a visible, non-minimised, non-cloaked window in screen coordinates; null if unsuitable.</summary>
    public static WindowInfo? DescribeWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return null;
        }

        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null;
        }

        if (!GetClientRect(hwnd, out var client))
        {
            return null;
        }

        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin))
        {
            return null;
        }

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        return width < MinWindowWidth || height < MinWindowHeight ? null : new WindowInfo(hwnd, origin.X, origin.Y, width, height);
    }
}

/// <summary>Self-test of the pure logic (no window, no screen).</summary>
static class SelfTest
{
    private static int _checks;
    private static int _failures;

    public static int Run()
    {
        TestPageContent();
        TestChange();
        TestTracker();
        TestSecondFileTracker();
        TestStatistics();
        TestArguments();
        Console.WriteLine(_failures == 0 ? $"selftest: {_checks} Prüfungen bestanden." : $"selftest: {_failures} von {_checks} Prüfungen fehlgeschlagen.");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition)
    {
        _checks++;
        if (!condition)
        {
            _failures++;
            Console.Error.WriteLine($"FEHLER: {name}");
        }
    }

    private static Grid Filled(uint color, int width = 200, int height = 150)
    {
        var pixels = new uint[width * height];
        Array.Fill(pixels, color);
        return new Grid(width, height, pixels);
    }

    private static void Fill(Grid g, int x0, int y0, int x1, int y1, uint color)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                g.Pixels[(y * g.Width) + x] = color;
            }
        }
    }

    // Chrome-like layout: dark toolbar on top, grey background, white page in the middle; text stems are single dark samples.
    private static Grid PageFrame(bool withText, uint background = 0x525659)
    {
        var g = Filled(background);
        Fill(g, 0, 0, g.Width, 12, 0x323639);
        Fill(g, 50, 20, 150, 140, 0xFFFFFF);
        if (withText)
        {
            for (var i = 0; i < 12; i++)
            {
                g.Pixels[(60 * g.Width) + 60 + (i * 3)] = 0x000000;
            }
        }

        return g;
    }

    private static void TestPageContent()
    {
        Check("blank white has no content", !Analysis.HasPageContent(Filled(0xFFFFFF), out _, out _));
        Check("black has no content", !Analysis.HasPageContent(Filled(0x000000), out _, out _));
        Check("grey background has no content", !Analysis.HasPageContent(Filled(0x525659), out _, out _));
        Check("white page without text has no content", !Analysis.HasPageContent(PageFrame(false), out _, out _));
        Check("white page with text has content", Analysis.HasPageContent(PageFrame(true), out var share, out var ink) && share > 0.2 && ink >= 12);
        Check("dark theme background with text has content", Analysis.HasPageContent(PageFrame(true, 0x1E1E1E), out _, out _));

        // A solid dark block on a white page has dark neighbours inside and white on one side at its edge only: no text.
        var block = PageFrame(false);
        Fill(block, 80, 40, 120, 90, 0x000000);
        Check("solid dark block is not text", !Analysis.HasPageContent(block, out _, out _));

        // Dark window chrome around the page (page edge next to a dark border) is not text either.
        var border = PageFrame(false);
        Fill(border, 48, 20, 50, 140, 0x000000);
        Check("dark border next to the page is not text", !Analysis.HasPageContent(border, out _, out _));

        // Toolbar text outside the content region must not count.
        var toolbar = Filled(0xFFFFFF);
        for (var i = 0; i < 20; i++)
        {
            toolbar.Pixels[(3 * toolbar.Width) + 10 + (i * 3)] = 0x000000;
        }

        Check("text in the toolbar strip does not count", !Analysis.HasPageContent(toolbar, out _, out _));
    }

    private static void TestChange()
    {
        var a = PageFrame(true);
        var b = PageFrame(true);
        Check("identical frames: no change", Analysis.CountChanged(a, b, false) == 0);
        b.Pixels[100] = 0x000000;
        b.Pixels[101] = 0xFFFFFF;
        Check("two changed samples are below the frame threshold", Analysis.CountChanged(a, b, false) == 2 && 2 < Analysis.MinChangedSamples);
        var faint = PageFrame(true);
        faint.Pixels[(60 * faint.Width) + 60] = 0x080808; // sum of channel differences 24: not above the threshold
        Check("tiny colour drift is ignored", Analysis.CountChanged(a, faint, false) == 0);
        Check("different size counts as changed", Analysis.CountChanged(a, Filled(0, 100, 100), false) == int.MaxValue);
        var top = PageFrame(true);
        Fill(top, 0, 0, top.Width, 10, 0xFF0000);
        Check("change outside the central region is ignored there", Analysis.CountChanged(a, top, true) == 0 && Analysis.CountChanged(a, top, false) > 0);
    }

    private static void TestTracker()
    {
        var tracker = new StageTracker(null, 500, 10000);
        var blank = Filled(0xFFFFFF);
        var grey = PageFrame(false);
        var sharp = PageFrame(true);
        var refined = PageFrame(true);
        Fill(refined, 100, 100, 110, 110, 0xE0E0E0); // visible change after first content
        var t = 0.0;
        for (; t < 200; t += 10) { tracker.Feed(t, Clone(blank)); }
        for (; t < 300; t += 10) { tracker.Feed(t, Clone(grey)); }
        Check("no content before text appears", tracker.FirstContentMs is null);
        for (; t < 420; t += 10) { tracker.Feed(t, Clone(sharp)); }
        Check("first content at the first frame with text", tracker.FirstContentMs == 300);
        for (; t < 1000 && !tracker.Done; t += 10) { tracker.Feed(t, Clone(t < 430 ? sharp : refined)); }
        Check("done after settle time", tracker.Done && tracker.Settled);
        Check("visually complete is the last change", tracker.VisuallyCompleteMs == 430);

        // Content in the very first frame (window appears fully rendered).
        var immediate = new StageTracker(null, 100, 10000);
        for (var i = 0; i < 20 && !immediate.Done; i++) { immediate.Feed(i * 10, Clone(sharp)); }
        Check("window appearing fully rendered: first content = visually complete", immediate.FirstContentMs == 0 && immediate.VisuallyCompleteMs == 0 && immediate.Settled);

        // A picture that never stops changing ends after the maximum wait, not settled.
        var busy = new StageTracker(null, 500, 1000);
        for (var i = 0; i < 400 && !busy.Done; i++)
        {
            var frame = Clone(sharp);
            Fill(frame, 70, 70 + (i % 2 * 5), 90, 75 + (i % 2 * 5), 0x000000);
            busy.Feed(i * 10, frame);
        }

        Check("endless animation: done but not settled", busy.Done && !busy.Settled);
    }

    private static void TestSecondFileTracker()
    {
        var old = PageFrame(true);
        var tracker = new StageTracker(old, 100, 10000);
        tracker.Feed(0, Clone(old));
        Check("second file: old document still shown is not content", tracker.FirstContentMs is null);

        var withTab = Clone(old);
        Fill(withTab, 0, 0, withTab.Width, 10, 0x00FF00); // new tab appears in the top strip only
        tracker.Feed(10, withTab);
        Check("second file: tab strip change alone is not content", tracker.FirstContentMs is null);

        var other = PageFrame(false);
        for (var i = 0; i < 30; i++)
        {
            other.Pixels[(80 * other.Width) + 55 + (i * 3)] = 0x000000;
        }

        tracker.Feed(20, other);
        Check("second file: changed page with text is content", tracker.FirstContentMs == 20);
    }

    private static void TestStatistics()
    {
        Check("median of odd count", Analysis.Median([3, 1, 2]) == 2);
        Check("median of even count", Analysis.Median([4, 1, 2, 3]) == 2.5);
        Check("median of nothing is NaN", double.IsNaN(Analysis.Median([])));
        Check("zero variation for equal values", Analysis.CoefficientOfVariation([5, 5, 5]) == 0);
        Check("coefficient of variation", Math.Abs(Analysis.CoefficientOfVariation([100, 110, 90]) - 10) < 1e-9);
    }

    private static void TestArguments()
    {
        Check("plain path", Analysis.ExpandArguments("{pdf}", @"C:\a\b.pdf") == @"C:\a\b.pdf");
        Check("path with space is quoted", Analysis.ExpandArguments("--x {pdf}", @"C:\a b\c.pdf") == "--x \"C:\\a b\\c.pdf\"");
        Check("already quoted path is not quoted twice", Analysis.ExpandArguments("\"{pdf}\"", @"C:\a b\c.pdf") == "\"C:\\a b\\c.pdf\"");
        Check("url form", Analysis.ExpandArguments("{pdfurl}", @"C:\a b\c.pdf") == "file:///C:/a%20b/c.pdf");
        Check("csv field with comma is quoted", Analysis.CsvField("a,b") == "\"a,b\"" && Analysis.CsvField("ab") == "ab");
        Check("usage error for missing options", Options.Parse(["--exe", "x"], out var error) is null && error.Length > 0);
        Check("usage error for unknown option", Options.Parse(["--nope", "1"], out _) is null);
    }

    private static Grid Clone(Grid g) => new(g.Width, g.Height, (uint[])g.Pixels.Clone());
}
