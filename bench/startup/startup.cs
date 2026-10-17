// Startup-time check of the published app (CI and local), protocol in bench/startup/README.md.
//   dotnet run bench/startup/startup.cs -- --exe <app binary> --pdf <pdf> --case cold|warm|thousand|second
//        [--second-pdf <pdf>] [--out <csv>]
//   dotnet run bench/startup/startup.cs -- selftest
// Per run the script starts the app with a hidden measurement switch and redirected stdout (the app is a GUI
// program; its stdout only reaches a parent that redirects it):
//   cold, warm, thousand  --measure-startup <pdf>: the time from just before the process start to the moment the
//                         line "first-sharp" is read (the first sharp image of page 1 was drawn).
//   second                --measure-second <pdf> <second-pdf>: the value of the line "second-sharp <ms>", measured
//                         inside the app from handing the second file over to its drawn first sharp image.
// 10 runs per case; cold with 5 s pause and no warm-up, warm and thousand with 3 warm-up runs and 500 ms pause,
// second with 1 warm-up run and 500 ms pause. Prints every run, median, min and max, and writes case,run,ms to --out.
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
    "          [--second-pdf <PDF>] [--out <CSV>]\n" +
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
    public Case Case { get; private set; } = Case.All[0];

    public static Options? Parse(string[] args, out string error)
    {
        var o = new Options();
        string? caseName = null;
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
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

        foreach (var file in new[] { o.Exe, o.Pdf, o.SecondPdf })
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
        string[] arguments = c.IsSecond
            ? ["--measure-second", Path.GetFullPath(options.Pdf), Path.GetFullPath(options.SecondPdf!)]
            : ["--measure-startup", Path.GetFullPath(options.Pdf)];

        Console.WriteLine($"{c.Name}: {exe} {string.Join(' ', arguments)}");
        if (c.Name == "cold")
        {
            Console.WriteLine("Hinweis: \"cold\" leert den Datei-Cache des Betriebssystems nicht; ein echter Kaltstart ist eine manuelle Messung direkt nach einem Neustart.");
        }

        // Only a warning: the check itself stays as it is, whatever the machine.
        if (FrameCadence.Measure() is { } warning)
        {
            Console.WriteLine(warning);
        }

        var times = new List<double>();
        string? failure = null;
        for (var run = -c.Warmup + 1; run <= c.Runs; run++)
        {
            var result = RunOnce(exe, arguments, c.IsSecond);
            if (result.Error is not null)
            {
                failure = $"{c.Name}: Lauf {(run <= 0 ? "Aufwärmen" : run.ToString(CultureInfo.InvariantCulture))} fehlgeschlagen: {result.Error}";
                break;
            }

            var label = run <= 0 ? $"Aufwärmen {run + c.Warmup}" : $"Lauf {run}";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {label}: {result.Milliseconds:0.0} ms"));
            if (run > 0)
            {
                times.Add(result.Milliseconds);
            }

            if (run < c.Runs)
            {
                Thread.Sleep(c.PauseMs);
            }
        }

        if (options.Out is { } outPath)
        {
            WriteCsv(outPath, c.Name, times);
        }

        if (failure is not null)
        {
            Console.Error.WriteLine($"FEHLGESCHLAGEN: {failure}");
            return 1;
        }

        var median = Stats.Median(times);
        var limit = Stats.Limit(c.BudgetMs);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{c.Name}: Median {median:0.0} ms, Min {times.Min():0.0} ms, Max {times.Max():0.0} ms ({times.Count} Läufe; Budget {c.BudgetMs:0} ms, Grenze {limit:0} ms)"));
        if (Stats.Exceeds(median, c.BudgetMs))
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"FEHLGESCHLAGEN: {c.Name}: Median {median:0.0} ms liegt über der Grenze {limit:0} ms (Budget {c.BudgetMs:0} ms + 25 %)."));
            return 1;
        }

        Console.WriteLine($"{c.Name}: innerhalb der Grenze.");
        return 0;
    }

    /// <summary>One launch of the app; the time or why it failed.</summary>
    private static (double Milliseconds, string? Error) RunOnce(string exe, string[] arguments, bool second)
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
            return (0, $"{exe} konnte nicht gestartet werden");
        }

        // stdout is read line by line on a thread of its own, so the time is taken right when the line arrives.
        double? firstSharp = null;
        double? secondSharp = null;
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
            return (0, $"Zeitlimit {RunTimeout.TotalSeconds:0} s überschritten, Prozess beendet");
        }

        reader.Join(TimeSpan.FromSeconds(5));
        var error = errorTask.GetAwaiter().GetResult().Trim();
        string text;
        lock (output)
        {
            text = output.ToString().Trim();
        }

        if (process.ExitCode != 0)
        {
            return (0, $"Exit-Code {process.ExitCode}{(error.Length > 0 ? $", stderr: {error}" : string.Empty)}{(text.Length > 0 ? $", stdout: {text}" : string.Empty)}");
        }

        var value = second ? secondSharp : firstSharp;
        return value is { } ms
            ? (ms, null)
            : (0, $"keine Zeile \"{(second ? "second-sharp <ms>" : "first-sharp")}\" auf stdout (stdout: '{text}', stderr: '{error}')");
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

    /// <summary>Returns a warning line when the composition is missing or too slow, otherwise <see langword="null"/>.</summary>
    public static string? Measure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

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

        return hr != 0 ? Assess(hr, 0) : Assess(0, Stats.Median(intervals));
    }

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

        Expect("Fälle vollständig", string.Join(',', Case.All.Select(c => $"{c.Name}:{c.BudgetMs}:{c.Runs}:{c.Warmup}:{c.PauseMs}")),
            "cold:400:10:0:5000,warm:200:10:3:500,thousand:300:10:3:500,second:100:10:1:500");

        Expect("Komposition mit 60 Hz", FrameCadence.Assess(0, 16.7), null);
        Expect("Komposition mit 30 Hz", FrameCadence.Assess(0, 33.4), null);
        Expect("Komposition mit 4 Hz warnt", FrameCadence.Assess(0, 253)?.StartsWith("Warnung: Die Desktop-Komposition zeigt nur alle 253 ms", StringComparison.Ordinal), true);
        Expect("keine Komposition warnt", FrameCadence.Assess(unchecked((int)0x80263001), 0)?.Contains("DwmFlush 0x80263001", StringComparison.Ordinal), true);

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
