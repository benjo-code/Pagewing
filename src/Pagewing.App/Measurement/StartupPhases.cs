using System.Diagnostics;
using System.Globalization;

namespace Pagewing.App.Measurement;

/// <summary>
/// Diagnostic timestamps of the startup path, recorded only in a measurement run (<c>--measure-startup</c>,
/// <c>--measure-second</c>, <c>--measure-scroll</c>) and written as <c>phase &lt;name&gt; &lt;ms&gt;</c> lines after
/// <c>first-sharp</c> (<c>bench/startup/README.md</c>, "Phasen"). Outside a measurement <see cref="Mark(string)"/>
/// does nothing but read a field.
/// </summary>
internal static class StartupPhases
{
    private static PhaseRecorder? s_current;

    /// <summary>Gets the recorder of this process, or <see langword="null"/> outside a measurement run.</summary>
    public static PhaseRecorder? Current => Volatile.Read(ref s_current);

    /// <summary>Starts recording (once per process; later calls keep the first recorder).</summary>
    public static PhaseRecorder Enable()
    {
        _ = Interlocked.CompareExchange(ref s_current, new PhaseRecorder(), null);
        return s_current!;
    }

    /// <summary>Records <paramref name="name"/> now, the first time it is reached (any thread).</summary>
    public static void Mark(string name) => Current?.Mark(name, Stopwatch.GetTimestamp());
}

/// <summary>
/// The first timestamp (<see cref="Stopwatch.GetTimestamp"/>) of each named phase, in the order they were reached.
/// Thread-safe: phases are marked on the UI thread, the PDFium thread and pool threads.
/// </summary>
internal sealed class PhaseRecorder
{
    private readonly Lock _gate = new();
    private readonly List<(string Name, long Timestamp)> _marks = [];

    /// <summary>Records <paramref name="name"/> at <paramref name="timestamp"/>, unless it was recorded before.</summary>
    public void Mark(string name, long timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            foreach (var mark in _marks)
            {
                if (mark.Name == name)
                {
                    return;
                }
            }

            _marks.Add((name, timestamp));
        }
    }

    /// <summary>
    /// Returns one line <c>phase &lt;name&gt; &lt;ms&gt;</c> per phase in the order reached, the milliseconds counted
    /// from <paramref name="originTimestamp"/> (the process start, see <see cref="ProcessStartTimestamp"/>).
    /// </summary>
    public IReadOnlyList<string> Lines(long originTimestamp)
    {
        lock (_gate)
        {
            var lines = new List<string>(_marks.Count);
            foreach (var (name, timestamp) in _marks)
            {
                lines.Add(Format(name, Stopwatch.GetElapsedTime(originTimestamp, timestamp).TotalMilliseconds));
            }

            return lines;
        }
    }

    /// <summary>One phase line, invariant culture, one decimal.</summary>
    public static string Format(string name, double milliseconds) =>
        string.Create(CultureInfo.InvariantCulture, $"phase {name} {milliseconds:0.0}");

    /// <summary>
    /// The start of this process (creation time from the operating system) as a <see cref="Stopwatch"/> timestamp:
    /// the creation time is taken against the wall clock read right next to the stopwatch. Windows stores the
    /// creation time in 100-ns units of the system time, Linux in clock ticks since boot (usually 10 ms); the phase
    /// times share this one origin, so differences between phases are exact. Called after the measuring point only:
    /// reading the creation time costs about a millisecond.
    /// </summary>
    public static long ProcessStartTimestamp()
    {
        DateTime startUtc;
        using (var process = Process.GetCurrentProcess())
        {
            startUtc = process.StartTime.ToUniversalTime();
        }

        var nowTimestamp = Stopwatch.GetTimestamp();
        var nowUtc = DateTime.UtcNow;
        return nowTimestamp - (long)((nowUtc - startUtc).TotalSeconds * Stopwatch.Frequency);
    }
}
