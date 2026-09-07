using System;

namespace Queuey.Edge;

/// <summary>
/// When the health reporter sends: at startup, when the state changes, and
/// every report interval — but never faster than Cloud wants. A failed send
/// used to be retried at the 10 s tick until it went through, which turned
/// a node with a flipping state and a refusing Cloud into a small flood of
/// its own. Now a failure holds the next attempt back with a doubling delay
/// (tick → interval), and a <c>Retry-After</c> from Cloud is the floor.
/// A held-back report is loss-free: the next one carries the newer truth.
///
/// Pure and clock-free so the loop's timing is testable without waiting.
/// </summary>
internal sealed class EdgeReportCadence
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private readonly TimeSpan _interval;
    private readonly TimeSpan _tick;

    private EdgeState? _lastSentState;
    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _holdUntil;
    private TimeSpan _backoff;

    public EdgeReportCadence(TimeSpan interval, TimeSpan tick)
    {
        _interval = interval;
        _tick = tick;
        _backoff = tick;
    }

    /// <summary>True when a report for <paramref name="state"/> should go out now.</summary>
    public bool IsDue(EdgeState state, DateTimeOffset now)
    {
        if (_holdUntil is { } until && now < until)
            return false;

        return _lastAttemptAt is null
            || state != _lastSentState
            || now - _lastAttemptAt.Value >= _interval;
    }

    /// <summary>Records the outcome of one send and arms the next hold.</summary>
    public void Attempted(EdgeState state, DateTimeOffset now, bool sent, TimeSpan? retryAfter)
    {
        _lastAttemptAt = now;

        if (sent)
        {
            _lastSentState = state;
            _holdUntil = null;
            _backoff = _tick;
            return;
        }

        // Even a failed send counts as "attempted" for the interval cadence;
        // a still-unreported state change retries after the hold, not at
        // every tick. Retry-After is Cloud's word and floors the hold;
        // the backoff doubles up to the interval, where the regular cadence
        // takes over anyway.
        var hold = _backoff;
        if (retryAfter is { } ra)
        {
            if (ra > MaxRetryAfter) ra = MaxRetryAfter;
            if (ra > hold) hold = ra;
        }
        _holdUntil = now + hold;

        var doubled = _backoff + _backoff;
        _backoff = doubled < _interval ? doubled : _interval;
    }
}
