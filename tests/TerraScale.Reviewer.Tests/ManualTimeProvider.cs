// One-shot timers for deterministically advancing the OCR inactivity watchdog.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        ManualTimer[] due;
        lock (_timers)
        {
            _ticks += elapsed.Ticks;
            due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
            foreach (var timer in due) timer.Due = long.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        internal long Due = long.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are used");
            lock (clock._timers)
            {
                if (!clock._timers.Contains(this)) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
        }
        internal void Fire() => callback(state);
        public void Dispose() { lock (clock._timers) clock._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
