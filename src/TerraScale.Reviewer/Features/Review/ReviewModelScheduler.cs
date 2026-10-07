using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>In-memory, start-weighted admission for the two review models.</summary>
public sealed class ReviewModelScheduler
{
    public const string SolModel = "gpt-6.1-sol";
    public const string GlmModel = "glm-5.3";

    // ponytail: one lock for two slots; partition only if admission becomes a bottleneck.
    private readonly Lock _gate = new();
    private readonly ReviewerOptions _options;
    private readonly ILogger<ReviewModelScheduler> _logger;
    private readonly TimeProvider _clock;
    private readonly ModelState _sol;
    private readonly ModelState _glm;
    private TaskCompletionSource _changed = NewSignal();

    public ReviewModelScheduler(ReviewerOptions options, ILogger<ReviewModelScheduler> logger, TimeProvider? timeProvider = null)
    {
        if (options.SolWeight <= 0 || options.GlmWeight <= 0
            || options.SolSlots is < 1 or > 2 || options.GlmSlots != 1
            || options.ModelSlotWait < TimeSpan.Zero || options.ModelBackoffDelay <= TimeSpan.Zero
            || options.ModelBackoffMaxDelay < options.ModelBackoffDelay)
            throw new ArgumentException("Invalid model weights, slots, or backoff settings", nameof(options));
        _options = options;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        _sol = new(SolModel, options.SolWeight, options.SolSlots);
        _glm = new(GlmModel, options.GlmWeight, options.GlmSlots);
    }

    public async Task<Lease> AcquireAsync(string? preferred, CancellationToken ct, bool allowSpill = true)
    {
        var fixedModel = preferred is null ? null : FindModel(preferred);
        ModelState? blockedModel = null;
        long blockedAt = 0;
        while (true)
        {
            Task changed;
            var delay = Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                var now = _clock.GetUtcNow();
                var wanted = fixedModel ?? (_sol.Deficit + _sol.Weight >= _glm.Deficit + _glm.Weight ? _sol : _glm);
                var other = wanted == _sol ? _glm : _sol;
                if (_sol.InFlight + _glm.InFlight < 2)
                {
                    if (Available(wanted, now)) return Start(wanted);
                    if (wanted.BackoffUntil > now)
                    {
                        blockedModel = null;
                        if (allowSpill && Available(other, now)) return Start(other);
                        delay = wanted.BackoffUntil - now;
                        if (allowSpill && other.BackoffUntil > now)
                            delay = Min(delay, other.BackoffUntil - now);
                    }
                    else if (allowSpill)
                    {
                        if (blockedModel != wanted)
                        {
                            blockedModel = wanted;
                            blockedAt = _clock.GetTimestamp();
                        }
                        var remaining = _options.ModelSlotWait - _clock.GetElapsedTime(blockedAt);
                        if (remaining <= TimeSpan.Zero)
                        {
                            if (Available(other, now)) return Start(other);
                            if (other.BackoffUntil > now) delay = other.BackoffUntil - now;
                        }
                        else delay = remaining;
                    }
                }
                changed = _changed.Task;
            }
            // WaitAsync disposes its timeout timer on notification/cancellation.
            try { await changed.WaitAsync(delay, _clock, ct); }
            catch (TimeoutException) { }
        }
    }

    public void RateLimited(string model)
    {
        var state = FindModel(model);
        lock (_gate)
        {
            state.RateLimits++;
            if (state.ConsecutiveRateLimits < int.MaxValue) state.ConsecutiveRateLimits++;
            var seconds = Math.Min(_options.ModelBackoffMaxDelay.TotalSeconds,
                _options.ModelBackoffDelay.TotalSeconds * Math.Pow(2, Math.Min(63, state.ConsecutiveRateLimits - 1)));
            state.BackoffUntil = _clock.GetUtcNow() + TimeSpan.FromSeconds(seconds);
            _logger.LogWarning("review model {Model} rate limited: {RateLimits} total, {ConsecutiveRateLimits} consecutive; backoff until {BackoffUntil}",
                state.Model, state.RateLimits, state.ConsecutiveRateLimits, state.BackoffUntil);
            Notify();
        }
    }

    public SchedulerSnapshot Snapshot()
    {
        lock (_gate) return new(Capture(_sol), Capture(_glm));
    }

    private Lease Start(ModelState model)
    {
        // Smooth weighted deficits count every actual start, including preferred/spilled starts.
        _sol.Deficit += _sol.Weight;
        _glm.Deficit += _glm.Weight;
        model.Deficit -= (long)_sol.Weight + _glm.Weight;
        model.Started++;
        model.InFlight++;
        LogState(model);
        Notify();
        return new(this, model.Model, model.RateLimits);
    }

    private void Complete(Lease lease)
    {
        lock (_gate)
        {
            if (lease.Released || lease.Succeeded) return;
            lease.Succeeded = true;
            var model = FindModel(lease.Model);
            model.Completed++;
            // An older successful request must not erase a newer concurrent 429.
            if (lease.RateLimitsAtStart == model.RateLimits)
            {
                model.ConsecutiveRateLimits = 0;
                model.BackoffUntil = DateTimeOffset.MinValue;
            }
            LogState(model);
            Notify();
        }
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (lease.Released) return;
            lease.Released = true;
            FindModel(lease.Model).InFlight--;
            Notify();
        }
    }

    private void LogState(ModelState model) => _logger.LogInformation(
        "review model {Model}: weight {Weight}, started {Started}, completed {Completed}, in flight {InFlight}, rate limits {RateLimits}, backoff until {BackoffUntil}, start share {Share}",
        model.Model, model.Weight, model.Started, model.Completed, model.InFlight, model.RateLimits, model.BackoffUntil,
        (double)model.Started / (_sol.Started + _glm.Started));

    private ModelState FindModel(string model) => model switch
    {
        SolModel => _sol,
        GlmModel => _glm,
        _ => throw new ArgumentException("Review model must be gpt-6.1-sol or glm-5.3", nameof(model))
    };

    private static bool Available(ModelState model, DateTimeOffset now) => model.InFlight < model.Slots && model.BackoffUntil <= now;
    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first <= second ? first : second;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Notify()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static ModelSnapshot Capture(ModelState model) => new(model.Model, model.Weight, model.Slots,
        model.Started, model.Completed, model.InFlight, model.RateLimits, model.ConsecutiveRateLimits, model.BackoffUntil);

    private sealed class ModelState(string model, int weight, int slots)
    {
        public string Model { get; } = model;
        public int Weight { get; } = weight;
        public int Slots { get; } = slots;
        public long Deficit;
        public long Started;
        public long Completed;
        public int InFlight;
        public long RateLimits;
        public int ConsecutiveRateLimits;
        public DateTimeOffset BackoffUntil = DateTimeOffset.MinValue;
    }

    public sealed class Lease : IDisposable
    {
        private readonly ReviewModelScheduler _owner;
        internal bool Released;
        internal bool Succeeded;
        internal long RateLimitsAtStart { get; }
        internal Lease(ReviewModelScheduler owner, string model, long rateLimitsAtStart) =>
            (_owner, Model, RateLimitsAtStart) = (owner, model, rateLimitsAtStart);
        public string Model { get; }
        public void Complete() => _owner.Complete(this);
        public void Dispose() => _owner.Release(this);
    }

    public sealed record ModelSnapshot(string Model, int Weight, int Slots, long Started, long Completed,
        int InFlight, long RateLimits, int ConsecutiveRateLimits, DateTimeOffset BackoffUntil);

    public sealed record SchedulerSnapshot(ModelSnapshot Sol, ModelSnapshot Glm)
    {
        public long Started => Sol.Started + Glm.Started;
        public long Completed => Sol.Completed + Glm.Completed;
        public int InFlight => Sol.InFlight + Glm.InFlight;
    }
}
