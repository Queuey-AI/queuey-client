using System;

namespace Queuey.Edge;

/// <summary>
/// THE backoff policy — the spool stores schedules, the loop asks here.
/// Three ladders:
/// <list type="bullet">
/// <item>Transient/Unknown: decorrelated jitter,
///   <c>min(cap, rand(base, prev × 3))</c> — avoids both the retry-storm of
///   pure exponential and the convergence of full-jitter.</item>
/// <item>Throttled: <c>Retry-After</c> honoured EXACTLY when present.</item>
/// <item>RequiresAction: slow doubling ladder (5 min → 1 h by default) —
///   bad credentials are an operator problem, and hot-looping them helps
///   no one.</item>
/// </list>
/// </summary>
internal sealed class BackoffPolicy
{
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
                return hinted > TimeSpan.Zero ? hinted : _options.BackoffBase;

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
