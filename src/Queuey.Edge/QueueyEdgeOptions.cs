using System;
using System.IO;
using Queuey.Client;

namespace Queuey.Edge;

/// <summary>
/// Startup configuration for Queuey Edge. Explicit, local and small: the
/// goal is not zero configuration — it is that APPLICATION CODE carries
/// none. Everything beyond credentials, tenant and storage path has a
/// defensible default.
/// </summary>
public sealed class QueueyEdgeOptions
{
    /// <summary>Named deployment used to resolve the default ingress host. Defaults to Production.</summary>
    public QueueyEnvironment Environment { get; set; } = QueueyEnvironment.Production;

    /// <summary>Override for the ingress host (e.g. a local instance for testing).</summary>
    public Uri? IngressBaseAddress { get; set; }

    /// <summary>
    /// API key (<c>qak_…</c>), sent as <c>X-Api-Key</c>. Use a PUBLISH-ONLY,
    /// TENANT-SCOPED key for Edge nodes — the key lives on a machine Queuey
    /// does not control, and must not be able to do anything but publish to
    /// its own tenant.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>The tenant public id (<c>ten_…</c>) events publish under.</summary>
    public string? TenantPublicId { get; set; }

    /// <summary>Default value for the <c>X-Queuey-Source</c> trace header.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Optional local payload cap. Set it to your queue's server-side limit
    /// so an oversized payload fails AT THE CALL (the caller's side of the
    /// boundary) instead of being accepted and quarantined after transfer.
    /// Null = no local cap.
    /// </summary>
    public int? MaxPayloadBytes { get; set; }

    public EdgeStorageOptions Storage { get; } = new();

    public EdgeTransferOptions Transfer { get; } = new();

    public EdgeLocalEndpointOptions LocalEndpoint { get; } = new();

    /// <summary>The effective ingress base address (override or environment default).</summary>
    public Uri ResolveIngressBaseAddress()
        => new QueueyOptions { Environment = Environment, IngressBaseAddress = IngressBaseAddress }
            .ResolveIngressBaseAddress();

    /// <summary>Throws <see cref="QueueyConfigurationException"/> when required settings are missing.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new QueueyConfigurationException("QueueyEdgeOptions.ApiKey is required.");
        if (string.IsNullOrWhiteSpace(TenantPublicId))
            throw new QueueyConfigurationException("QueueyEdgeOptions.TenantPublicId is required.");
        if (string.IsNullOrWhiteSpace(Storage.Path))
            throw new QueueyConfigurationException("QueueyEdgeOptions.Storage.Path is required.");
        if (Storage.MaxSpoolBytes <= Storage.HeadroomBytes)
            throw new QueueyConfigurationException(
                "QueueyEdgeOptions.Storage.MaxSpoolBytes must exceed HeadroomBytes.");
    }
}

/// <summary>Durable-store knobs. The deletion surface is deliberately tiny — see each remark.</summary>
public sealed class EdgeStorageOptions
{
    /// <summary>
    /// Spool file path. Must be LOCAL durable disk (never a network share —
    /// SQLite locking over SMB/NFS is unreliable; never container-ephemeral
    /// storage unless you accept process-lifetime durability). Defaults to
    /// <c>queuey-edge/spool.db</c> under the application base directory.
    /// </summary>
    public string Path { get; set; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "queuey-edge", "spool.db");

    /// <summary>
    /// Hard storage limit — the ONLY backpressure of lossless retention.
    /// When reached, <c>PublishAsync</c> throws <see cref="QueueySpoolFullException"/>
    /// (draining continues). Default 512 MB ≈ a year of autonomy at
    /// 1 event/minute × 1 KB. Size from the docs' autonomy table.
    /// </summary>
    public long MaxSpoolBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Reserved margin so BOOKKEEPING writes (settling transfers) still
    /// succeed at the limit — a spool that cannot record success re-sends
    /// forever.
    /// </summary>
    public long HeadroomBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>
    /// Health-only age threshold: a pending event older than this raises
    /// <c>StorageDurabilityWarning</c>-adjacent health signals. IT NEVER
    /// DELETES — age alone never deletes an accepted event (rev 4 F4/D1).
    /// </summary>
    public TimeSpan SpoolAgeWarningThreshold { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long settled (Transferred) rows are kept for correlation before the sweep removes them.</summary>
    public TimeSpan SettledRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>v1 supports only <see cref="SpoolDurability.Durable"/>.</summary>
    public SpoolDurability Durability { get; set; } = SpoolDurability.Durable;
}

/// <summary>
/// The opt-in loopback publish endpoint — the polyglot one-liner. When
/// <see cref="Port"/> is set, the daemon serves
/// <c>POST http://localhost:{port}/events/{tenant}/{queue}</c> with the SAME
/// wire shape as cloud ingress, answering 202 only after the durable local
/// commit — so any language's existing publish snippet gains offline
/// survival by swapping the base URL. Loopback only, always: the machine is
/// the trust boundary, exactly as for the spool file.
/// </summary>
public sealed class EdgeLocalEndpointOptions
{
    /// <summary>Null (default) = endpoint off. Set a port to enable.</summary>
    public int? Port { get; set; }
}

/// <summary>Transfer-loop knobs. Defaults tuned for the motivating workload (~1 event/min/node).</summary>
public sealed class EdgeTransferOptions
{
    /// <summary>
    /// Concurrent transfers ACROSS lanes. Within a lane there is always at
    /// most one in-flight transfer — that invariant is FIFO, not a knob.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Per-attempt HTTP timeout. A timeout is indeterminate, never a failure — the retry is idempotent.</summary>
    public TimeSpan TransferTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Decorrelated-jitter base for transient failures.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Decorrelated-jitter cap for transient failures.</summary>
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>First probe delay on RequiresAction (auth/route/billing) — never hot-loop a known non-transient failure.</summary>
    public TimeSpan RequiresActionProbeInitial { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Probe interval while the queue is PAUSED — deliberately faster and
    /// flat (no doubling): pausing is an intentional operator state, and
    /// the operator who unpauses expects flow to resume within about this.
    /// </summary>
    public TimeSpan QueuePausedProbe { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Probe-delay ceiling on RequiresAction.</summary>
    public TimeSpan RequiresActionProbeCap { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Claim-lease length; expired leases return to ready (restart recovery).</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Idle poll interval when the spool is empty (publishes also wake the loop directly).</summary>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(1);
}
