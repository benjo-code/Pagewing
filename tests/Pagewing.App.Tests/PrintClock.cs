namespace Pagewing.App.Tests;

/// <summary>A clock that moves only when told to; its one-shot timers fire (on the calling thread) when it moves past them.</summary>
public sealed class PrintClock : TimeProvider
{
    private readonly List<PrintTimer> _timers = [];
    private long _ticks;

    /// <summary>Gets how many timers were created on this clock.</summary>
    public int TimersCreated { get; private set; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        TimersCreated++;
        var timer = new PrintTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _ticks += by.Ticks;
        foreach (var timer in _timers.ToArray())
        {
            timer.FireIfDue(_ticks);
        }
    }

    private sealed class PrintTimer(PrintClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long? _due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock._timers.Remove(this);
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
            if (_due is not null)
            {
                clock._timers.Add(this);
            }

            return true;
        }

        public void FireIfDue(long now)
        {
            if (_due is { } due && due <= now)
            {
                Dispose();
                callback(state);
            }
        }

        public void Dispose()
        {
            _due = null;
            clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
