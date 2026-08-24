namespace Queuey.Edge;

/// <summary>
/// The durability level <c>PublishAsync</c>'s success contract is phrased
/// against. The contract names a LEVEL, not a pragma: no layer outside the
/// spool implementation may assume fsync semantics, so a future weaker
/// level is a new member and a receipt annotation — not a rewrite.
/// </summary>
public enum SpoolDurability
{
    /// <summary>
    /// Success means the event has crossed the durable persistence boundary —
    /// for the SQLite spool, a committed transaction under
    /// <c>synchronous = FULL</c> (fsync per commit; survives power loss on
    /// hardware that honours flush). The only supported level in v1.
    /// </summary>
    Durable = 0
}
