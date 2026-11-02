using Pagewing.Core.Ocr;
using Pagewing.Ocr;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>One call of a fake engine: which call it is (counted from 0 over the whole run) and the image it was given.</summary>
internal sealed record FakeCall(int Index, int Width, int Height, int Dpi, CancellationToken Cancellation);

/// <summary>Engines that answer from a function, for the tests of the runner (no native library).</summary>
internal sealed class FakeOcrFactory(Func<FakeCall, OcrRecognition> behavior) : IOcrEngineFactory
{
    private int calls;
    private int created;
    private int disposed;
    private int active;
    private int maxActive;
    private int violations;

    private Func<FakeCall, OcrRecognition> Behavior { get; } = behavior;

    /// <summary>Gets or sets an exception the next <see cref="Create"/> throws (a missing language file).</summary>
    public Exception? CreateFailure { get; set; }

    public int Created => Volatile.Read(ref created);

    public int Disposed => Volatile.Read(ref disposed);

    public int Calls => Volatile.Read(ref calls);

    public int Active => Volatile.Read(ref active);

    public int MaxActive => Volatile.Read(ref maxActive);

    /// <summary>Gets how often two threads used one engine at the same time.</summary>
    public int Violations => Volatile.Read(ref violations);

    public IOcrEngine Create(string languages)
    {
        if (CreateFailure is { } failure)
        {
            throw failure;
        }

        Interlocked.Increment(ref created);
        return new Engine(this);
    }

    private sealed class Engine(FakeOcrFactory owner) : IOcrEngine
    {
        private int inUse;

        public OcrRecognition Recognize(ReadOnlySpan<byte> gray, int width, int height, int dpi, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref inUse, 1) != 0)
            {
                Interlocked.Increment(ref owner.violations);
            }

            var now = Interlocked.Increment(ref owner.active);
            int seen;
            while (now > (seen = Volatile.Read(ref owner.maxActive)) && Interlocked.CompareExchange(ref owner.maxActive, now, seen) != seen)
            {
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return owner.Behavior(new FakeCall(Interlocked.Increment(ref owner.calls) - 1, width, height, dpi, cancellationToken));
            }
            finally
            {
                Interlocked.Decrement(ref owner.active);
                Volatile.Write(ref inUse, 0);
            }
        }

        public void Dispose() => Interlocked.Increment(ref owner.disposed);
    }
}

/// <summary>A clock the test moves by hand; timers (and so <c>Task.Delay</c>) fire when it passes them.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Gets how many timers are waiting.</summary>
    public int Waiting
    {
        get
        {
            lock (gate)
            {
                return timers.Count(static t => t.Armed);
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            now += by;
            due = [.. timers.Where(t => t.Armed && t.Due <= now)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset due;
        private bool armed;

        public bool Armed => armed;

        public DateTimeOffset Due => due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                armed = dueTime != Timeout.InfiniteTimeSpan;
                due = owner.now + (armed ? dueTime : TimeSpan.Zero);
                if (!owner.timers.Contains(this))
                {
                    owner.timers.Add(this);
                }
            }

            return true;
        }

        public void Fire()
        {
            lock (owner.gate)
            {
                if (!armed)
                {
                    return;
                }

                armed = false;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                armed = false;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
