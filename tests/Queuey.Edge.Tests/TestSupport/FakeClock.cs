using Queuey.Edge;

namespace Queuey.Edge.Tests.TestSupport;

/// <summary>Controllable clock — 48-hour backoff and retention tests without waiting.</summary>
public sealed class FakeClock : IEdgeClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    public long MonotonicMilliseconds { get; set; }

    public void Advance(TimeSpan by)
    {
        UtcNow += by;
        MonotonicMilliseconds += (long)by.TotalMilliseconds;
    }
}
