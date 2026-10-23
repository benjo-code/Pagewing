using Pagewing.App.Services;

namespace Pagewing.App.Tests;

/// <summary>
/// An <see cref="IProcessLauncher"/> that starts nothing: it records the programs it was asked to start or run (from any
/// thread), answers a run with the next of <see cref="ExitCodes"/> (0 when none is left), and lets a test hold the wait for
/// the old process at a gate.
/// </summary>
internal sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly Lock _lock = new();
    private readonly List<(string File, string[] Arguments)> _started = [];
    private readonly List<(string File, string CommandLine)> _runs = [];
    private readonly List<int> _waited = [];

    /// <summary>Gets the programs started without waiting, in order.</summary>
    public IReadOnlyList<(string File, string[] Arguments)> Started
    {
        get
        {
            lock (_lock)
            {
                return [.. _started];
            }
        }
    }

    /// <summary>Gets the programs run to their end (the setup program), with their command line, in order.</summary>
    public IReadOnlyList<(string File, string CommandLine)> Runs
    {
        get
        {
            lock (_lock)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>Gets the processes waited for.</summary>
    public IReadOnlyList<int> Waited
    {
        get
        {
            lock (_lock)
            {
                return [.. _waited];
            }
        }
    }

    /// <summary>Gets the exit codes of the next runs; 0 when none is left.</summary>
    public Queue<int> ExitCodes { get; } = new();

    /// <summary>Gets or sets what runs when a program is started, before it is recorded.</summary>
    public Action<string, IReadOnlyList<string>>? OnStart { get; set; }

    /// <summary>Gets or sets what runs when a program is run, before it is recorded.</summary>
    public Action<string>? OnRun { get; set; }

    /// <summary>Gets or sets whether starting fails (the program is missing, for example).</summary>
    public bool StartFails { get; set; }

    /// <summary>Gets or sets what the wait for the old process waits for (and honours cancellation while it waits).</summary>
    public TaskCompletionSource? ExitGate { get; set; }

    public bool Start(string file, IReadOnlyList<string> arguments)
    {
        OnStart?.Invoke(file, arguments);
        lock (_lock)
        {
            _started.Add((file, [.. arguments]));
        }

        return !StartFails;
    }

    public Task<int> RunAsync(string file, string commandLine)
    {
        OnRun?.Invoke(file);
        lock (_lock)
        {
            _runs.Add((file, commandLine));
            return Task.FromResult(ExitCodes.Count > 0 ? ExitCodes.Dequeue() : 0);
        }
    }

    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        lock (_lock)
        {
            _waited.Add(processId);
        }

        if (ExitGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        return true;
    }
}
