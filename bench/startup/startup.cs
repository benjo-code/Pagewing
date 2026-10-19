// Startup-time check of the published app (CI and local), protocol in bench/startup/README.md.
//   dotnet run bench/startup/startup.cs -- --exe <app binary> --pdf <pdf> --case cold|warm|thousand|second
//        [--second-pdf <pdf>] [--out <csv>] [--phases] [--settings <settings.json>]
//   dotnet run bench/startup/startup.cs -- selftest
// Per run the script starts the app with a hidden measurement switch and redirected stdout (the app is a GUI
// program; its stdout only reaches a parent that redirects it):
//   cold, warm, thousand  --measure-startup <pdf>: the time from just before the process start to the moment the
//                         line "first-sharp" is read (the first sharp image of page 1 was drawn).
//   second                --measure-second <pdf> <second-pdf>: the value of the line "second-sharp <ms>", measured
//                         inside the app from handing the second file over to its drawn first sharp image.
// 10 runs per case; cold with 5 s pause and no warm-up, warm and thousand with 3 warm-up runs and 500 ms pause,
// second with 1 warm-up run and 500 ms pause. Prints every run, median, min and max, and writes case,run,ms to --out.
// --phases also prints the median of every startup phase the app writes after "first-sharp" ("phase <name> <ms>",
// milliseconds since its process start) over the measured runs; informational, it never changes the verdict.
// --settings passes the file on as --measure-settings: the app starts with those settings (read only, never written;
// without it a measurement run always starts with the defaults). Output and CSV name the case "<case>+settings:<name>".
// Budgets (Docs/ROADMAP.md, "Geschwindigkeitsziele"): cold 400, warm 200, second 100, thousand 300 ms. The check
// fails when the MEDIAN is above budget × 1.25. "cold" cannot empty the file cache of the operating system.
// On Linux the binary needs an X server; the CI runs the whole script under xvfb-run -a. On Windows the script first
// times the desktop composition (DwmFlush) and warns when it is missing or below 25 Hz: the times are then invalid.
//
// Exit codes: 0 within the limit, 1 above the limit or a failed run, 2 usage error.

using System.Diagnostics;
using System.Globalization;
using System.Text;

const string Usage =
    "Aufruf: dotnet run bench/startup/startup.cs -- --exe <App-Binary> --pdf <PDF> --case cold|warm|thousand|second\n" +
    "          [--second-pdf <PDF>] [--out <CSV>] [--phases] [--settings <settings.json>]\n" +
    "       dotnet run bench/startup/startup.cs -- selftest";

if (args is ["selftest"])
{
    return SelfTest.Run();
}

var options = Options.Parse(args, out var usageError);
if (options is null)
{
    Console.Error.WriteLine(usageError);
    Console.Error.WriteLine(Usage);
    return 2;
}

return Check.Run(options);

/// <summary>A case of the check: its budget and how it runs.</summary>
sealed record Case(string Name, double BudgetMs, int Runs, int Warmup, int PauseMs)
{
    public static readonly IReadOnlyList<Case> All =
    [
        new("cold", 400, 10, 0, 5000),
        new("warm", 200, 10, 3, 500),
        new("thousand", 300, 10, 3, 500),
        new("second", 100, 10, 1, 500),
    ];

    public static Case? Find(string name) => All.FirstOrDefault(c => c.Name == name);

    public bool IsSecond => Name == "second";
}

sealed class Options
{
    public string Exe { get; private set; } = string.Empty;
    public string Pdf { get; private set; } = string.Empty;
    public string? SecondPdf { get; private set; }
    public string? Out { get; private set; }
    public bool Phases { get; private set; }
    public string? Settings { get; private set; }
    public Case Case { get; private set; } = Case.All[0];

    /// <summary>The name of the run in the output and the CSV: the case, with the settings file if one is given.</summary>
    public string Label => Settings is null ? Case.Name : $"{Case.Name}+settings:{Path.GetFileNameWithoutExtension(Settings)}";

    /// <summary>The arguments of the app for one run.</summary>
    public string[] AppArguments()
    {
        string[] measure = Case.IsSecond
            ? ["--measure-second", Path.GetFullPath(Pdf), Path.GetFullPath(SecondPdf!)]
            : ["--measure-startup", Path.GetFullPath(Pdf)];
        return Settings is null ? measure : [.. measure, "--measure-settings", Path.GetFullPath(Settings)];
    }

    public static Options? Parse(string[] args, out string error)
    {
        var o = new Options();
        string? caseName = null;
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--phases")
            {
                o.Phases = true;
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
                case "--pdf": o.Pdf = value; break;
                case "--second-pdf": o.SecondPdf = value; break;
                case "--out": o.Out = value; break;
                case "--case": caseName = value; break;
                case "--settings": o.Settings = value; break;
                default:
                    error = $"Unbekannte Option: {name}";
                    return null;
            }
        }

        if (o.Exe.Length == 0 || o.Pdf.Length == 0 || caseName is null)
        {
            error = "--exe, --pdf und --case sind Pflicht.";
            return null;
        }

        if (Case.Find(caseName) is not { } found)
        {
            error = $"Unbekannter Fall: {caseName} (cold, warm, thousand oder second).";
            return null;
        }

        o.Case = found;
        if (found.IsSecond && string.IsNullOrEmpty(o.SecondPdf))
        {
            error = "Der Fall second braucht --second-pdf.";
            return null;
        }

        foreach (var file in new[] { o.Exe, o.Pdf, o.SecondPdf, o.Settings })
        {
            if (file is not null && !File.Exists(file))
            {
                error = $"Datei nicht gefunden: {Path.GetFullPath(file)}";
                return null;
            }
        }

        error = string.Empty;
        return o;
    }
}

static class Check
{
    /// <summary>How long the script waits for a run; the app ends itself after 30 s with exit code 3.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(45);

    public static int Run(Options options)
    {
        var c = options.Case;
        var exe = Path.GetFullPath(options.Exe);
        var arguments = options.AppArguments();
        var name = options.Label;

        Console.WriteLine($"{name}: {exe} {string.Join(' ', arguments)}");
        if (c.Name == "cold")
        {
            Console.WriteLine("Hinweis: \"cold\" leert den Datei-Cache des Betriebssystems nicht; ein echter Kaltstart ist eine manuelle Messung direkt nach einem Neustart.");
        }

        // Only a warning: the check itself stays as it is, whatever the machine. It is repeated next to the verdict.
        var warning = FrameCadence.Measure();
        if (warning is not null)
        {
            Console.WriteLine(warning);
        }

        var times = new List<double>();
        var phaseRuns = new List<IReadOnlyList<(string Name, double Ms)>>();
        var beforeProcess = new List<double>();
        string? failure = null;
        for (var run = -c.Warmup + 1; run <= c.Runs; run++)
        {
            var result = RunOnce(exe, arguments, c.IsSecond);
            if (result.Error is not null)
            {
                failure = $"{name}: Lauf {(run <= 0 ? "Aufwärmen" : run.ToString(CultureInfo.InvariantCulture))} fehlgeschlagen: {result.Error}";
                break;
            }

            var label = run <= 0 ? $"Aufwärmen {run + c.Warmup}" : $"Lauf {run}";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {label}: {result.Milliseconds:0.0} ms"));
            if (run > 0)
            {
                times.Add(result.Milliseconds);
                phaseRuns.Add(result.Phases);
                if (!c.IsSecond && Phases.Find(result.Phases, "frame-after-tile") is { } frame)
                {
                    beforeProcess.Add(result.Milliseconds - frame);
                }
            }

            if (run < c.Runs)
            {
                Thread.Sleep(c.PauseMs);
            }
        }

        if (options.Phases)
        {
            foreach (var line in Phases.Report(phaseRuns, beforeProcess))
            {
                Console.WriteLine(line);
            }
        }

        if (options.Out is { } outPath)
        {
            WriteCsv(outPath, name, times);
        }

        RepeatWarning(warning);
        if (failure is not null)
        {
            Console.Error.WriteLine($"FEHLGESCHLAGEN: {failure}");
            return 1;
        }

        var median = Stats.Median(times);
        var limit = Stats.Limit(c.BudgetMs);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name}: Median {median:0.0} ms, Min {times.Min():0.0} ms, Max {times.Max():0.0} ms ({times.Count} Läufe; Budget {c.BudgetMs:0} ms, Grenze {limit:0} ms)"));
        if (Stats.Exceeds(median, c.BudgetMs))
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"FEHLGESCHLAGEN: {name}: Median {median:0.0} ms liegt über der Grenze {limit:0} ms (Budget {c.BudgetMs:0} ms + 25 %)."));
            return 1;
        }

        Console.WriteLine($"{name}: innerhalb der Grenze.");
        return 0;
    }

    /// <summary>
    /// Repeats the composition warning next to the verdict, where a reader of the log looks, and as a GitHub Actions
    /// annotation (<c>::warning::</c>), so it also shows on the summary of the run. Does not change the verdict.
    /// </summary>
    private static void RepeatWarning(string? warning)
    {
        if (warning is not null)
        {
            Console.WriteLine($"Ergebnis unter Vorbehalt: {warning}");
            Console.WriteLine($"::warning title=Startzeit-Check::{FrameCadence.ForAnnotation(warning)}");
        }
    }

    /// <summary>One launch of the app; the time and the phases the app wrote, or why it failed.</summary>
    private static (double Milliseconds, string? Error, IReadOnlyList<(string Name, double Ms)> Phases) RunOnce(string exe, string[] arguments, bool second)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var start = Stopwatch.GetTimestamp();
        using var process = Process.Start(info);
        if (process is null)
        {
            return (0, $"{exe} konnte nicht gestartet werden", []);
        }

        // stdout is read line by line on a thread of its own, so the time is taken right when the line arrives.
        double? firstSharp = null;
        double? secondSharp = null;
        var phases = new List<(string Name, double Ms)>();
        var output = new StringBuilder();
        var reader = new Thread(() =>
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                var now = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                lock (output)
                {
                    output.AppendLine(line);
                    if (line == "first-sharp")
                    {
                        firstSharp ??= now;
                    }
                    else if (Stats.TryParseSecondSharp(line, out var value))
                    {
                        secondSharp ??= value;
                    }
                    else if (Phases.TryParse(line, out var phase, out var phaseMs))
                    {
                        phases.Add((phase, phaseMs));
                    }
                }
            }
        })
        { IsBackground = true };
        reader.Start();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(RunTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (0, $"Zeitlimit {RunTimeout.TotalSeconds:0} s überschritten, Prozess beendet", []);
        }

        reader.Join(TimeSpan.FromSeconds(5));
        var error = errorTask.GetAwaiter().GetResult().Trim();
        string text;
        (string Name, double Ms)[] phaseLines;
        lock (output)
        {
            text = output.ToString().Trim();
            phaseLines = [.. phases];
        }

        if (process.ExitCode != 0)
        {
            return (0, $"Exit-Code {process.ExitCode}{(error.Length > 0 ? $", stderr: {error}" : string.Empty)}{(text.Length > 0 ? $", stdout: {text}" : string.Empty)}", []);
        }

        var value = second ? secondSharp : firstSharp;
        return value is { } ms
            ? (ms, null, phaseLines)
            : (0, $"keine Zeile \"{(second ? "second-sharp <ms>" : "first-sharp")}\" auf stdout (stdout: '{text}', stderr: '{error}')", []);
    }

    private static void WriteCsv(string path, string caseName, List<double> times)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var csv = new StringBuilder("case,run,ms\n");
        for (var i = 0; i < times.Count; i++)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{caseName},{i + 1},{times[i]:0.0}\n");
        }

        File.WriteAllText(full, csv.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"CSV: {full}");
    }
}

static class Stats
{
    /// <summary>The check fails above budget × 1.25 (Docs/DECISIONS.md, "Vergleichsmessung und endgültige Geschwindigkeitsbudgets").</summary>
    public const double Tolerance = 1.25;

    /// <summary>The median; the mean of the two middle values for an even count.</summary>
    public static double Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("Keine Werte.", nameof(values));
        }

        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    public static double Limit(double budgetMs) => budgetMs * Tolerance;

    /// <summary>Whether a median fails the check: strictly above the limit (the limit itself passes).</summary>
    public static bool Exceeds(double medianMs, double budgetMs) => medianMs > Limit(budgetMs);

    /// <summary>Reads "second-sharp &lt;ms&gt;" (invariant culture).</summary>
    public static bool TryParseSecondSharp(string line, out double milliseconds)
    {
        const string Prefix = "second-sharp ";
        milliseconds = 0;
        return line.StartsWith(Prefix, StringComparison.Ordinal)
            && double.TryParse(line.AsSpan(Prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds)
            && milliseconds >= 0;
    }
}

/// <summary>
/// The startup phases the app writes after "first-sharp" in a measurement run ("phase &lt;name&gt; &lt;ms&gt;",
/// milliseconds since its own process start; src/Pagewing.App/Measurement/StartupPhases.cs). Informational only.
/// </summary>
static class Phases
{
    /// <summary>Reads "phase &lt;name&gt; &lt;ms&gt;" (invariant culture; the name has no spaces).</summary>
    public static bool TryParse(string line, out string name, out double milliseconds)
    {
        name = string.Empty;
        milliseconds = 0;
        if (line.Split(' ') is not ["phase", { Length: > 0 } phase, var value]
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds))
        {
            return false;
        }

        name = phase;
        return true;
    }

    /// <summary>The value of the phase <paramref name="name"/> in one run, or <see langword="null"/>.</summary>
    public static double? Find(IReadOnlyList<(string Name, double Ms)> run, string name)
    {
        foreach (var (phase, ms) in run)
        {
            if (phase == name)
            {
                return ms;
            }
        }

        return null;
    }

    /// <summary>The median of every phase over the runs, in the order the phases first appear, and how many runs had it.</summary>
    public static IReadOnlyList<(string Name, double Median, int Count)> Medians(IReadOnlyList<IReadOnlyList<(string Name, double Ms)>> runs)
    {
        var order = new List<string>();
        var values = new Dictionary<string, List<double>>();
        foreach (var run in runs)
        {
            foreach (var (name, ms) in run)
            {
                if (!values.TryGetValue(name, out var list))
                {
                    values[name] = list = [];
                    order.Add(name);
                }

                list.Add(ms);
            }
        }

        return [.. order.Select(name => (name, Stats.Median(values[name]), values[name].Count))];
    }

    /// <summary>
    /// The lines printed with --phases: one per phase (median and runs), then the median of the time of the script
    /// minus the phase "frame-after-tile" per run: from just before Process.Start to the process start, plus the way
    /// of the line through the pipe.
    /// </summary>
    public static IReadOnlyList<string> Report(IReadOnlyList<IReadOnlyList<(string Name, double Ms)>> runs, IReadOnlyCollection<double> beforeProcess)
    {
        var medians = Medians(runs);
        if (medians.Count == 0)
        {
            return ["Phasen (Median): keine Zeilen \"phase <Name> <ms>\" erhalten."];
        }

        var lines = new List<string> { "Phasen (Median, ms seit Prozessstart; nur zur Information):" };
        foreach (var (name, median, count) in medians)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {name,-26} {median,7:0.0} ms ({count} Läufe)"));
        }

        if (beforeProcess.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {"Skript - frame-after-tile",-26} {Stats.Median(beforeProcess),7:0.0} ms ({beforeProcess.Count} Läufe; vor dem Prozessstart und Weg der Zeile)"));
        }

        return lines;
    }
}

/// <summary>
/// How often the desktop composition of Windows (DWM) shows a new image. The measuring point waits for the frame after
/// the drawing, and Avalonia paces its frames on DWM: when DWM composes only a few times per second (seen in a VM
/// whose only display is the VirtIO GPU DOD controller: one image every 253 ms), every run contains up to two of
/// these waits and says nothing about the app. Not measured on Linux (Xvfb has no vertical sync).
/// </summary>
static class FrameCadence
{
    /// <summary>A median DWM interval above this (below 25 Hz) makes the measured times invalid.</summary>
    public const double SlowIntervalMs = 40;

    /// <summary>DwmFlush calls whose intervals are timed (after one call that only waits for the next composition).</summary>
    private const int Samples = 5;

    /// <summary>The probe never holds up the check longer than this (6 compositions at 4 Hz take about 1.5 s).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns a warning line when the composition is missing or too slow, otherwise <see langword="null"/>. Never
    /// throws and never takes much longer than <see cref="ProbeTimeout"/>: a failing or blocked probe counts as "no
    /// composition".
    /// </summary>
    public static string? Measure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // On a pool thread, so that a DwmFlush that does not return cannot stall the check.
            var probe = Task.Run(Probe);
            if (!probe.Wait(ProbeTimeout))
            {
                return AssessFailure($"DwmFlush kam nicht innerhalb von {ProbeTimeout.TotalSeconds:0} s zurück");
            }

            var (hr, median) = probe.Result;
            return Assess(hr, median);
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException { InnerException: { } first } ? first : ex;
            return AssessFailure($"{inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>One DwmFlush to reach the next composition, then <see cref="Samples"/> timed intervals.</summary>
    private static (int HResult, double MedianMs) Probe()
    {
        var hr = DwmFlush();
        var intervals = new List<double>(Samples);
        var last = Stopwatch.GetTimestamp();
        for (var i = 0; i < Samples && hr == 0; i++)
        {
            hr = DwmFlush();
            var now = Stopwatch.GetTimestamp();
            intervals.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;
        }

        return hr != 0 ? (hr, 0) : (0, Stats.Median(intervals));
    }

    /// <summary>The warning when the probe itself failed (exception or time limit): treated as no composition.</summary>
    public static string AssessFailure(string reason) =>
        $"Warnung: Keine Desktop-Komposition messbar ({reason}). Die App bekommt dann vermutlich keine oder nur wenige Frames, die Messung ist ungültig.";

    /// <summary>A message for a GitHub Actions workflow command (escapes %, CR and LF).</summary>
    public static string ForAnnotation(string message) =>
        message.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    /// <summary>The warning for a DwmFlush result and the median interval between two compositions.</summary>
    public static string? Assess(int hresult, double medianIntervalMs)
    {
        if (hresult != 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Warnung: Keine Desktop-Komposition (DwmFlush 0x{hresult:X8}), etwa in einer Dienstsitzung ohne Desktop. Die App bekommt dann vermutlich keine Frames, die Messung ist ungültig.");
        }

        return medianIntervalMs > SlowIntervalMs
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Warnung: Die Desktop-Komposition zeigt nur alle {medianIntervalMs:0} ms ein neues Bild ({1000 / medianIntervalMs:0.0} Hz, erwartet um 60 Hz). Jeder Lauf enthält bis zu zwei solcher Wartezeiten; die Zeiten sagen nichts über die App. Ursache ist meist eine VM ohne GPU (z. B. VirtIO-GPU-DOD); gültig misst ein Rechner mit GPU und angeschlossener oder aktiver Anzeige.")
            : null;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}

static class SelfTest
{
    private static int s_checks;
    private static int s_failures;

    public static int Run()
    {
        Expect("Median ungerade", Stats.Median([5, 1, 3]), 3);
        Expect("Median gerade", Stats.Median([4, 1, 3, 2]), 2.5);
        Expect("Median ein Wert", Stats.Median([7]), 7);
        Expect("Median zehn Läufe", Stats.Median([210, 180, 190, 950, 185, 200, 175, 195, 205, 188]), 192.5);

        Expect("Grenze cold", Stats.Limit(400), 500);
        Expect("Grenze warm", Stats.Limit(200), 250);
        Expect("Grenze second", Stats.Limit(100), 125);
        Expect("Grenze thousand", Stats.Limit(300), 375);

        Expect("Median auf der Grenze besteht", Stats.Exceeds(250, 200), false);
        Expect("Median knapp über der Grenze", Stats.Exceeds(250.1, 200), true);
        Expect("Median über dem Budget, unter der Grenze", Stats.Exceeds(240, 200), false);
        Expect("Median unter dem Budget", Stats.Exceeds(90, 100), false);
        Expect("Ausreißer kippt den Median nicht", Stats.Exceeds(Stats.Median([90, 95, 100, 2000, 98, 97, 93, 99, 94, 96]), 100), false);
        Expect("zehn langsame Läufe", Stats.Exceeds(Stats.Median([130, 128, 140, 126, 135, 131, 129, 133, 127, 138]), 100), true);

        Expect("second-sharp lesen", Stats.TryParseSecondSharp("second-sharp 42.5", out var ms) ? ms : -1, 42.5);
        Expect("second-sharp ohne Wert", Stats.TryParseSecondSharp("second-sharp ", out _), false);
        Expect("andere Zeile", Stats.TryParseSecondSharp("first-sharp", out _), false);

        Expect("phase lesen", Phases.TryParse("phase tile-applied 123.4", out var phaseName, out var phaseMs) ? $"{phaseName}={phaseMs.ToString(CultureInfo.InvariantCulture)}" : "-", "tile-applied=123.4");
        Expect("phase ohne Wert", Phases.TryParse("phase main", out _, out _), false);
        Expect("phase mit Leerzeichen im Namen", Phases.TryParse("phase a b 1.0", out _, out _), false);
        Expect("phase ohne Zahl", Phases.TryParse("phase main x", out _, out _), false);
        Expect("first-sharp ist keine Phase", Phases.TryParse("first-sharp", out _, out _), false);
        IReadOnlyList<IReadOnlyList<(string Name, double Ms)>> phaseRuns =
        [
            [("main", 10), ("window-opened", 100), ("frame-after-tile", 300)],
            [("main", 30), ("window-opened", 120), ("frame-after-tile", 310)],
            [("main", 20), ("frame-after-tile", 290), ("extra", 5)],
        ];
        Expect("Phasen-Median", string.Join(',', Phases.Medians(phaseRuns).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Name}:{p.Median}:{p.Count}"))),
            "main:20:3,window-opened:110:2,frame-after-tile:300:3,extra:5:1");
        Expect("Phase finden", Phases.Find(phaseRuns[0], "frame-after-tile"), 300);
        Expect("Phase fehlt", Phases.Find(phaseRuns[2], "window-opened"), null);
        var phaseReport = Phases.Report(phaseRuns, [12, 10, 11]);
        Expect("Phasen-Bericht Zeilen", phaseReport.Count, 6);
        Expect("Phasen-Bericht Kopf", phaseReport[0], "Phasen (Median, ms seit Prozessstart; nur zur Information):");
        Expect("Phasen-Bericht Zeile", phaseReport[2], "  window-opened                110.0 ms (2 Läufe)");
        Expect("Phasen-Bericht Skript", phaseReport[5].StartsWith("  Skript - frame-after-tile     11.0 ms (3 Läufe;", StringComparison.Ordinal), true);
        Expect("Phasen-Bericht ohne Zeilen", Phases.Report([], [])[0].StartsWith("Phasen (Median): keine Zeilen", StringComparison.Ordinal), true);
        Expect("--phases braucht keinen Wert", Options.Parse(["--phases", "--case", "warm"], out var phasesError) is null && phasesError.StartsWith("--exe", StringComparison.Ordinal), true);

        var someFile = Path.GetTempFileName();
        try
        {
            var plain = Options.Parse(["--exe", someFile, "--pdf", someFile, "--case", "warm"], out _);
            Expect("ohne --settings", plain is null ? "-" : $"{plain.Label}|{string.Join(' ', plain.AppArguments())}", $"warm|--measure-startup {Path.GetFullPath(someFile)}");
            var withSettings = Options.Parse(["--exe", someFile, "--pdf", someFile, "--case", "warm", "--settings", someFile], out _);
            Expect("mit --settings", withSettings is null ? "-" : $"{withSettings.Label}|{string.Join(' ', withSettings.AppArguments())}",
                $"warm+settings:{Path.GetFileNameWithoutExtension(someFile)}|--measure-startup {Path.GetFullPath(someFile)} --measure-settings {Path.GetFullPath(someFile)}");
            Expect("--settings muss existieren", Options.Parse(["--exe", someFile, "--pdf", someFile, "--case", "warm", "--settings", someFile + ".missing"], out var settingsError) is null
                && settingsError.StartsWith("Datei nicht gefunden", StringComparison.Ordinal), true);
        }
        finally
        {
            File.Delete(someFile);
        }

        Expect("Fälle vollständig", string.Join(',', Case.All.Select(c => $"{c.Name}:{c.BudgetMs}:{c.Runs}:{c.Warmup}:{c.PauseMs}")),
            "cold:400:10:0:5000,warm:200:10:3:500,thousand:300:10:3:500,second:100:10:1:500");

        Expect("Komposition mit 60 Hz", FrameCadence.Assess(0, 16.7), null);
        Expect("Komposition mit 30 Hz", FrameCadence.Assess(0, 33.4), null);
        Expect("Komposition mit 4 Hz warnt", FrameCadence.Assess(0, 253)?.StartsWith("Warnung: Die Desktop-Komposition zeigt nur alle 253 ms", StringComparison.Ordinal), true);
        Expect("keine Komposition warnt", FrameCadence.Assess(unchecked((int)0x80263001), 0)?.Contains("DwmFlush 0x80263001", StringComparison.Ordinal), true);
        Expect("gescheiterte Messung warnt", FrameCadence.AssessFailure("DllNotFoundException: dwmapi.dll").StartsWith("Warnung: Keine Desktop-Komposition messbar (DllNotFoundException", StringComparison.Ordinal), true);
        Expect("Annotation maskiert", FrameCadence.ForAnnotation("50 % \r\nweiter"), "50 %25 %0D%0Aweiter");
        Expect("Messung höchstens 2 s", FrameCadence.ProbeTimeout, TimeSpan.FromSeconds(2));

        Console.WriteLine(s_failures == 0 ? $"selftest: {s_checks} Prüfungen bestanden." : $"selftest: {s_failures} von {s_checks} Prüfungen fehlgeschlagen.");
        return s_failures == 0 ? 0 : 1;
    }

    private static void Expect<T>(string name, T actual, T expected)
    {
        s_checks++;
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            s_failures++;
            Console.Error.WriteLine($"selftest: {name}: erwartet {expected}, erhalten {actual}");
        }
    }
}
