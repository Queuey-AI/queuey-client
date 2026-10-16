using System;
using System.Collections.Generic;

namespace Queuey.Edge;

/// <summary>
/// A source that feeds the spool from outside the application, such as an
/// MQTT broker on the gateway. The spool's own numbers cannot show that a
/// source has stopped delivering: a broker that went away leaves an empty
/// spool, which reads exactly like a healthy, quiet node. So an intake
/// reports its own side, and the snapshot, the meter and the health report
/// carry it next to the spool's.
/// </summary>
public interface IEdgeIntake
{
    /// <summary>The intake's current side of the node's health. Cheap and in memory; called per scrape.</summary>
    EdgeIntakeHealth Snapshot();
}

/// <summary>
/// One intake's side of the node's health.
/// </summary>
/// <param name="Name">Stable name for dashboards and alerts, e.g. <c>mqtt:localhost:1883</c>.</param>
/// <param name="Connected">The intake holds a live connection to its source.</param>
/// <param name="Paused">
/// The intake stepped back from its source on purpose because the spool could
/// not take a message. It resumes by itself when the spool has room again.
/// </param>
/// <param name="LastMessageAtUtc">When the source last handed the intake a message; null before the first.</param>
/// <param name="LastConnectedAtUtc">When the intake last connected to its source; null before the first connection.</param>
/// <param name="LastError">Why the last connection attempt failed, or why the intake paused; null while there is nothing to say.</param>
/// <param name="Counters">What happened to every message the source handed over, since the process started.</param>
public sealed record EdgeIntakeHealth(
    string Name,
    bool Connected,
    bool Paused,
    DateTimeOffset? LastMessageAtUtc,
    DateTimeOffset? LastConnectedAtUtc,
    string? LastError,
    EdgeIntakeCounters Counters);

/// <summary>
/// Every message an intake was handed, by outcome. <see cref="Received"/> is
/// the sum of the others. Counts start at zero with the process.
/// </summary>
/// <param name="Received">Messages the source handed over.</param>
/// <param name="Accepted">Durably in the spool, then acknowledged to the source.</param>
/// <param name="Deferred">
/// Left unacknowledged so the source delivers them again: the spool could not
/// take them, or the intake was stepping back from its source.
/// </param>
/// <param name="Rejected">
/// Acknowledged without being accepted, because no retry could ever accept
/// them: larger than the local <c>MaxPayloadBytes</c>, or than the whole spool.
/// Each one is logged as an error.
/// </param>
/// <param name="Unrouted">Acknowledged and ignored: no route claims the topic.</param>
/// <param name="RetainedSkipped">Acknowledged and ignored: a retained copy the source re-sent on subscribe.</param>
/// <param name="Lost">
/// Messages the source sent without delivery guarantee (MQTT QoS 0) that the
/// spool could not take. The source will not send them again.
/// </param>
public sealed record EdgeIntakeCounters(
    long Received,
    long Accepted,
    long Deferred,
    long Rejected,
    long Unrouted,
    long RetainedSkipped,
    long Lost);

/// <summary>
/// The intakes running in this process. A source registers itself when it is
/// constructed; the health snapshot, the meter and the health report read
/// them from here. Registered by <c>AddQueueyEdge</c>.
/// </summary>
public sealed class EdgeIntakeRegistry
{
    private readonly object _gate = new();
    private IEdgeIntake[] _intakes = Array.Empty<IEdgeIntake>();

    /// <summary>Adds an intake. Registering the same instance twice is a no-op.</summary>
    public void Register(IEdgeIntake intake)
    {
        if (intake is null) throw new ArgumentNullException(nameof(intake));
        lock (_gate)
        {
            if (Array.IndexOf(_intakes, intake) >= 0) return;
            var next = new IEdgeIntake[_intakes.Length + 1];
            _intakes.CopyTo(next, 0);
            next[_intakes.Length] = intake;
            _intakes = next;
        }
    }

    /// <summary>Every registered intake's snapshot, in registration order.</summary>
    public IReadOnlyList<EdgeIntakeHealth> Snapshot()
    {
        var intakes = System.Threading.Volatile.Read(ref _intakes);
        var snapshots = new EdgeIntakeHealth[intakes.Length];
        for (var i = 0; i < intakes.Length; i++)
            snapshots[i] = intakes[i].Snapshot();
        return snapshots;
    }
}
