using System;

namespace Queuey.Edge;

/// <summary>
/// Wall-clock and monotonic time, separately. Wall time stamps rows and
/// headers; monotonic time ages backlogs so a device whose RTC jumps
/// (NTP correction, dead battery) cannot make pending events look younger
/// or older than they are. Also the seam that makes 48-hour backoff
/// testable without waiting 48 hours.
/// </summary>
public interface IEdgeClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Milliseconds from an arbitrary fixed origin; never goes backwards.</summary>
    long MonotonicMilliseconds { get; }
}

internal sealed class SystemEdgeClock : IEdgeClock
{
    public static readonly SystemEdgeClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long MonotonicMilliseconds => Environment.TickCount64;
}
