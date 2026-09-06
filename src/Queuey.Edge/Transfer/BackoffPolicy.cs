using System;

namespace Queuey.Edge;

/// <summary>
/// THE backoff policy — the spool stores schedules, the loop asks here.
/// Three ladders:
/// <list type="bullet">
/// <item>Transient/Unknown: decorrelated jitter,
///   <c>min(cap, rand(base, prev × 3))</c> — avoids both the retry-storm of
///   pure exponential and the convergence of full-jitter.</item>
/// <item>Throttled: <c>Retry-After</c> is a FLOOR when present — never
///   shorter, plus up to <see cref="RetryAfterJitterFraction"/> on top. A
///   fleet that went dark together comes back together, and Cloud's burst
///   guard hands every node the same hint at the same second; without the
///   jitter they would all knock again in lockstep.</item>
/// <item>RequiresAction: slow doubling ladder (5 min → 1 h by default) —
///   bad credentials are an operator problem, and hot-looping them helps
///   no one.</item>
/// </list>
/// </summary>
internal sealed class BackoffPolicy
{
    /// <summary>Upper bound of the random extension applied on top of a <c>Retry-After</c> hint (25 %).</summary>
    public const double RetryAfterJitterFraction = 0.25;

    private readonly EdgeTransferOptions _options;
    private readonly Random _random;

    public BackoffPolicy(EdgeTransferOptions options, Random? random = null)
    {
        _options = options;
        _random = random ?? Random.Shared;
    }

    public TimeSpan NextDelay(TransferOutcome outcome, TimeSpan lastDelay)
    {
        switch (outcome.Class)
        {
            case TransferClass.Throttled when outcome.RetryAfter is { } hinted:
                var floor = hinted > TimeSpan.Zero ? hinted : _options.BackoffBase;
                return floor + TimeSpan.FromMilliseconds(
                    floor.TotalMilliseconds * RetryAfterJitterFraction * _random.NextDouble());

            case TransferClass.Throttled:
                return Decorrelated(lastDelay);

            case TransferClass.RequiresAction:
                if (lastDelay < _options.RequiresActionProbeInitial)
                    return _options.RequiresActionProbeInitial;
                var doubled = lastDelay * 2;
                return doubled > _options.RequiresActionProbeCap ? _options.RequiresActionProbeCap : doubled;

            default:
                return Decorrelated(lastDelay);
        }
    }

    private TimeSpan Decorrelated(TimeSpan lastDelay)
    {
        var floorMs = _options.BackoffBase.TotalMilliseconds;
        var ceilingMs = Math.Max(floorMs, lastDelay.TotalMilliseconds * 3);
        var capMs = _options.BackoffCap.TotalMilliseconds;

        var next = floorMs + _random.NextDouble() * (Math.Min(ceilingMs, capMs) - floorMs);
        return TimeSpan.FromMilliseconds(Math.Min(capMs, Math.Max(floorMs, next)));
    }
}
