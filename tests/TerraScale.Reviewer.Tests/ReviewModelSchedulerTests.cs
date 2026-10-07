using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Review;
using TUnit.Assertions;
using TUnit.Core;

public sealed class ReviewModelSchedulerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static ReviewerOptions Options() => new("http://+:8080", "https://forgejo.test", "forgejo",
        "https://proxy.test", "proxy", ReviewModelScheduler.SolModel, "TerraScale", "bot", 2,
        TimeSpan.FromMinutes(1), "webhook", "context", "unused.db", 100);

    private static ReviewModelScheduler Create(ReviewerOptions? options = null, TimeProvider? clock = null) =>
        new(options ?? Options(), NullLogger<ReviewModelScheduler>.Instance, clock);

    [Test]
    public async Task HundredSequentialReviewsUseSixtyFortyStarts()
    {
        var scheduler = Create();
        for (var i = 0; i < 100; i++)
        {
            using var lease = await scheduler.AcquireAsync(null, CancellationToken.None);
            lease.Complete();
            lease.Complete();
        }
        var snapshot = scheduler.Snapshot();
        await Assert.That(snapshot.Sol.Started).IsEqualTo(60L);
        await Assert.That(snapshot.Glm.Started).IsEqualTo(40L);
        await Assert.That(snapshot.Completed).IsEqualTo(100L);
        await Assert.That(snapshot.InFlight).IsEqualTo(0);
        await Assert.That(snapshot.Sol.Weight).IsEqualTo(60);
        await Assert.That(snapshot.Glm.Weight).IsEqualTo(40);
    }

    [Test]
    public async Task WeightsUseInFlightAndUnsuccessfulStartsRatherThanCompletions()
    {
        var scheduler = Create();
        using var first = await scheduler.AcquireAsync(null, CancellationToken.None);
        using var second = await scheduler.AcquireAsync(null, CancellationToken.None);
        await Assert.That(first.Model).IsEqualTo(ReviewModelScheduler.SolModel);
        await Assert.That(second.Model).IsEqualTo(ReviewModelScheduler.GlmModel);
        await Assert.That(scheduler.Snapshot().Completed).IsEqualTo(0L);
        await Assert.That(scheduler.Snapshot().InFlight).IsEqualTo(2);
        first.Dispose();
        second.Dispose();
        using var third = await scheduler.AcquireAsync(null, CancellationToken.None);
        await Assert.That(third.Model).IsEqualTo(ReviewModelScheduler.SolModel);
        await Assert.That(scheduler.Snapshot().Started).IsEqualTo(3L);
    }

    [Test]
    public async Task ConfiguredWeightsAndPreferredStartsFeedTheSameDeficits()
    {
        var scheduler = Create(Options() with { SolWeight = 20, GlmWeight = 80 });
        using (var preferred = await scheduler.AcquireAsync(ReviewModelScheduler.SolModel, CancellationToken.None))
            preferred.Complete();
        for (var i = 1; i < 100; i++)
        {
            using var lease = await scheduler.AcquireAsync(null, CancellationToken.None);
            lease.Complete();
        }
        await Assert.That(scheduler.Snapshot().Sol.Started).IsEqualTo(20L);
        await Assert.That(scheduler.Snapshot().Glm.Started).IsEqualTo(80L);
    }

    [Test]
    public async Task ConcurrentReviewsRespectGlobalAndActualModelCaps()
    {
        var scheduler = Create(Options() with { ModelSlotWaitDuration = TimeSpan.Zero });
        using var timeout = new CancellationTokenSource(Wait);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            using var lease = await scheduler.AcquireAsync(null, timeout.Token);
            var snapshot = scheduler.Snapshot();
            await Assert.That(snapshot.InFlight).IsLessThanOrEqualTo(2);
            await Assert.That(snapshot.Sol.InFlight).IsLessThanOrEqualTo(2);
            await Assert.That(snapshot.Glm.InFlight).IsLessThanOrEqualTo(1);
            await Task.Yield();
            lease.Complete();
        }));
        await Assert.That(scheduler.Snapshot().Started).IsEqualTo(100L);
        await Assert.That(scheduler.Snapshot().Completed).IsEqualTo(100L);
        await Assert.That(scheduler.Snapshot().InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task BlockedPreferredModelSpillsOnlyAfterTwoMinutes()
    {
        var clock = new ManualClock();
        var scheduler = Create(clock: clock);
        using var occupied = await scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, CancellationToken.None);
        var waiting = scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromMilliseconds(1));
        await Assert.That(waiting.IsCompleted).IsFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var spilled = await waiting.WaitAsync(Wait);
        await Assert.That(spilled.Model).IsEqualTo(ReviewModelScheduler.SolModel);
        await Assert.That(scheduler.Snapshot().Glm.InFlight).IsEqualTo(1);
    }

    [Test]
    public async Task WeightedBlockedModelWaitsThenSpillsAndAccountsActualStart()
    {
        var clock = new ManualClock();
        var scheduler = Create(clock: clock);
        using var occupied = await scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            using var sol = await scheduler.AcquireAsync(ReviewModelScheduler.SolModel, CancellationToken.None);
        }
        var waiting = scheduler.AcquireAsync(null, CancellationToken.None);
        await Assert.That(waiting.IsCompleted).IsFalse();
        clock.Advance(TimeSpan.FromMinutes(2));
        using var spilled = await waiting.WaitAsync(Wait);
        await Assert.That(spilled.Model).IsEqualTo(ReviewModelScheduler.SolModel);
        await Assert.That(scheduler.Snapshot().Sol.Started).IsEqualTo(4L);
        await Assert.That(scheduler.Snapshot().Glm.Started).IsEqualTo(1L);
    }

    [Test]
    public async Task RateLimitImmediatelyReroutesAnAlreadyWaitingLease()
    {
        var clock = new ManualClock();
        var scheduler = Create(clock: clock);
        using var occupied = await scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, CancellationToken.None);
        var waiting = scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, CancellationToken.None);
        scheduler.RateLimited(ReviewModelScheduler.GlmModel);
        using var rerouted = await waiting.WaitAsync(Wait);
        await Assert.That(rerouted.Model).IsEqualTo(ReviewModelScheduler.SolModel);
        await Assert.That(clock.GetUtcNow()).IsEqualTo(ManualClock.Epoch);
        await Assert.That(scheduler.Snapshot().Glm.RateLimits).IsEqualTo(1L);
    }

    [Test]
    public async Task NoSpillWaitsForItsModelThroughCooldownAndSlotTimeout()
    {
        var clock = new ManualClock();
        var scheduler = Create(clock: clock);
        using var occupied = await scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, CancellationToken.None);
        scheduler.RateLimited(ReviewModelScheduler.GlmModel);
        var waiting = scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, CancellationToken.None, allowSpill: false);
        clock.Advance(TimeSpan.FromMinutes(3));
        await Assert.That(waiting.IsCompleted).IsFalse();
        occupied.Dispose();
        using var lease = await waiting.WaitAsync(Wait);
        await Assert.That(lease.Model).IsEqualTo(ReviewModelScheduler.GlmModel);
        await Assert.That(scheduler.Snapshot().Sol.Started).IsEqualTo(0L);
    }

    [Test]
    public async Task BothModelsCoolingWaitForFirstDeadline()
    {
        var clock = new ManualClock();
        var scheduler = Create(clock: clock);
        scheduler.RateLimited(ReviewModelScheduler.SolModel);
        scheduler.RateLimited(ReviewModelScheduler.SolModel);
        scheduler.RateLimited(ReviewModelScheduler.GlmModel);
        var waiting = scheduler.AcquireAsync(null, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(29));
        await Assert.That(waiting.IsCompleted).IsFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        using var lease = await waiting.WaitAsync(Wait);
        await Assert.That(lease.Model).IsEqualTo(ReviewModelScheduler.GlmModel);
    }

    [Test]
    public async Task BackoffDoublesCapsAtTenMinutesAndSuccessResetsStreak()
    {
        var clock = new ManualClock();
        var scheduler = Create(clock: clock);
        foreach (var seconds in new[] { 30, 60, 120, 240, 480, 600, 600 })
        {
            scheduler.RateLimited(ReviewModelScheduler.SolModel);
            await Assert.That(scheduler.Snapshot().Sol.BackoffUntil).IsEqualTo(clock.GetUtcNow() + TimeSpan.FromSeconds(seconds));
        }
        await Assert.That(scheduler.Snapshot().Sol.ConsecutiveRateLimits).IsEqualTo(7);
        clock.Advance(TimeSpan.FromMinutes(10));
        using var successful = await scheduler.AcquireAsync(ReviewModelScheduler.SolModel, CancellationToken.None);
        successful.Complete();
        await Assert.That(scheduler.Snapshot().Sol.ConsecutiveRateLimits).IsEqualTo(0);
        await Assert.That(scheduler.Snapshot().Sol.BackoffUntil).IsEqualTo(DateTimeOffset.MinValue);
        scheduler.RateLimited(ReviewModelScheduler.SolModel);
        await Assert.That(scheduler.Snapshot().Sol.BackoffUntil).IsEqualTo(clock.GetUtcNow() + TimeSpan.FromSeconds(30));
        await Assert.That(scheduler.Snapshot().Sol.RateLimits).IsEqualTo(8L);
    }

    [Test]
    public async Task OlderSuccessPreservesConcurrentRateLimitBackoff()
    {
        var clock = new ManualClock();
        var scheduler = Create(clock: clock);
        using var older = await scheduler.AcquireAsync(ReviewModelScheduler.SolModel, CancellationToken.None);
        using var newer = await scheduler.AcquireAsync(ReviewModelScheduler.SolModel, CancellationToken.None);
        scheduler.RateLimited(newer.Model);
        newer.Dispose();
        older.Complete();
        await Assert.That(scheduler.Snapshot().Sol.ConsecutiveRateLimits).IsEqualTo(1);
        await Assert.That(scheduler.Snapshot().Sol.BackoffUntil).IsEqualTo(clock.GetUtcNow() + TimeSpan.FromSeconds(30));
        using var rerouted = await scheduler.AcquireAsync(ReviewModelScheduler.SolModel, CancellationToken.None);
        await Assert.That(rerouted.Model).IsEqualTo(ReviewModelScheduler.GlmModel);
    }

    [Test]
    public async Task CancellationAndRepeatedDisposalDoNotLeakOrOverReleaseSlots()
    {
        var scheduler = Create();
        using var first = await scheduler.AcquireAsync(ReviewModelScheduler.SolModel, CancellationToken.None);
        using var second = await scheduler.AcquireAsync(ReviewModelScheduler.SolModel, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, cancellation.Token);
        cancellation.Cancel();
        var cancelled = false;
        try { using var unexpected = await waiting; }
        catch (OperationCanceledException) { cancelled = true; }
        await Assert.That(cancelled).IsTrue();
        await Assert.That(scheduler.Snapshot().Started).IsEqualTo(2L);
        first.Dispose();
        first.Dispose();
        first.Complete();
        using var replacement = await scheduler.AcquireAsync(ReviewModelScheduler.GlmModel, CancellationToken.None);
        await Assert.That(scheduler.Snapshot().InFlight).IsEqualTo(2);
        await Assert.That(scheduler.Snapshot().Completed).IsEqualTo(0L);
        second.Dispose();
        replacement.Dispose();
        await Assert.That(scheduler.Snapshot().InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task UnsupportedModelsAreRejectedWithoutTakingSlots()
    {
        var scheduler = Create();
        foreach (var model in new[] { "gpt-6-astra", "other", "sol", "" })
        {
            var rejected = false;
            try { using var unexpected = await scheduler.AcquireAsync(model, CancellationToken.None); }
            catch (ArgumentException) { rejected = true; }
            await Assert.That(rejected).IsTrue();
        }
        await Assert.That(scheduler.Snapshot().Started).IsEqualTo(0L);
    }

    private sealed class ManualClock : TimeProvider
    {
        public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToList();
                foreach (var timer in due) _timers.Remove(timer);
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Tests use one-shot timers");
                lock (clock._gate)
                {
                    if (_disposed) return false;
                    clock._timers.Remove(this);
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                    clock._timers.Add(this);
                    return true;
                }
            }
            public void Fire()
            {
                lock (clock._gate) { if (_disposed) return; }
                callback(state);
            }
            public void Dispose()
            {
                lock (clock._gate)
                {
                    _disposed = true;
                    clock._timers.Remove(this);
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
