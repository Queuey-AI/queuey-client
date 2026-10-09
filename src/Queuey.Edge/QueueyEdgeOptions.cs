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
    /// The signing key id (<c>hsk_…</c>), sent as <c>X-Queuey-Key-Id</c>. With <see cref="SigningSecret"/>, Edge signs
    /// every transfer with the same HMAC as <c>Queuey.Client</c>, at the moment it sends, so the timestamp and nonce are
    /// fresh even for an event that waited in the spool through an outage. <c>queuey keys mint --write …</c> makes the
    /// pair; the key reaches only its queue or workspace, and the workspace may take signed requests only. When the pair
    /// is set, events are signed, and <see cref="ApiKey"/> is not used for them.
    /// </summary>
    public string? SigningKeyId { get; set; }

    /// <summary>
    /// The signing secret for <see cref="SigningKeyId"/>. It stays in memory: the spool never holds it, and no header
    /// carries it.
    /// </summary>
    public string? SigningSecret { get; set; }

    /// <summary>
    /// API key (<c>qak_…</c>), sent as <c>X-Api-Key</c>: the alternative to the signing pair. Use a PUBLISH-ONLY key,
    /// scoped to the workspace — the key lives on a machine Queuey does not control, and must not be able to do anything
    /// but publish. Read it from the environment (<c>QUEUEY_API_KEY</c>) or configuration, never a literal in code. With
    /// the signing pair set, events are signed and this key is not used for them; the health check-in takes
    /// <see cref="EdgeHealthReportOptions.ApiKey"/>.
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

    public EdgeHealthReportOptions Health { get; } = new();

    /// <summary>
    /// The HTTP handler under every connection Edge opens to Cloud (event
    /// transfer and health reports). Null means the runtime default. Set it
    /// for a corporate proxy, client certificates, or a test double — a
    /// <see cref="System.Net.Http.DelegatingHandler"/> that refuses
    /// connections is how a demo cuts one node's network. Called once per
    /// client Edge creates; each call must return a fresh handler.
    /// </summary>
    public Func<System.Net.Http.HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    internal System.Net.Http.HttpClient CreateHttpClient()
        => HttpMessageHandlerFactory is { } factory
            ? new System.Net.Http.HttpClient(factory() ?? throw new QueueyConfigurationException("HttpMessageHandlerFactory returned null."))
            : new System.Net.Http.HttpClient();

    /// <summary>Whether events are signed: both halves of the signing pair are set.</summary>
    internal bool Signs => !string.IsNullOrWhiteSpace(SigningKeyId) && !string.IsNullOrWhiteSpace(SigningSecret);

    /// <summary>
    /// What authenticates one transfer: Client's <see cref="HmacRequestSigner"/> with the pair, else its
    /// <see cref="ApiKeyAuthenticator"/>. The signer reads <paramref name="clock"/> when it signs, which is when Edge sends.
    /// </summary>
    // Edge-signering (Kenneth 2026-10-09): samme autentikator som klienten, ikke en kopi. Signaturen lages per sending.
    internal IQueueyAuthenticator CreateEventAuthenticator(IEdgeClock clock)
        => Signs
            ? new HmacRequestSigner(SigningKeyId!, SigningSecret!, () => clock.UtcNow)
            : new ApiKeyAuthenticator(ApiKey!);

    /// <summary>
    /// Fills each setting that is not set yet from its environment variable, by the same names and rules as
    /// <see cref="QueueyOptions.UseEnvironmentVariables(Func{string, string?}?)"/>: <c>QUEUEY_SIGNING_KEY_ID</c> and
    /// <c>QUEUEY_SIGNING_SECRET</c> (in Development also from <c>.env</c> in the working folder, as a pair),
    /// <c>QUEUEY_API_KEY</c> only when the pair is not set, <c>QUEUEY_TENANT</c> and <c>QUEUEY_INGRESS_BASE</c>; and
    /// <c>QUEUEY_EDGE_HEALTH_API_KEY</c> into <see cref="EdgeHealthReportOptions.ApiKey"/>, never from <c>.env</c>. A value
    /// set in code wins. Returns these options.
    /// </summary>
    /// <param name="read">Reads a variable; <see cref="System.Environment.GetEnvironmentVariable(string)"/> when null.</param>
    public QueueyEdgeOptions UseEnvironmentVariables(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        return FillFrom(client => client.UseEnvironmentVariables(read), read);
    }

    /// <summary>
    /// Fills each setting that is not set yet from <paramref name="read"/>, by the same <c>QUEUEY_*</c> names as
    /// <see cref="UseEnvironmentVariables(Func{string, string?}?)"/>, and no <c>.env</c>: for .NET configuration, which
    /// holds user secrets, environment variables and appsettings, as <c>options.UseSettings(key =&gt; configuration[key])</c>.
    /// A value set in code wins. Returns these options.
    /// </summary>
    public QueueyEdgeOptions UseSettings(Func<string, string?> read)
    {
        if (read is null) throw new ArgumentNullException(nameof(read));
        return FillFrom(client => client.UseSettings(read), read);
    }

    /// <summary><see cref="UseSettings(Func{string, string?})"/> over a .NET configuration.</summary>
    public QueueyEdgeOptions UseSettings(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        return UseSettings(key => configuration[key]);
    }

    /// <summary>
    /// The client's rules applied by <paramref name="fill"/> to a <see cref="QueueyOptions"/> with these values, and the
    /// ones Edge uses taken back; then the health key from <paramref name="read"/>.
    /// </summary>
    // Reglene bor i QueueyOptions, så Edge og klienten aldri kan lese forskjellig. Edge går bare gjennom klientens offentlige
    // metoder (security-review av #70, K1); testene gir en fill med .env-mappa via klientens InternalsVisibleTo til testprosjektet.
    internal QueueyEdgeOptions FillFrom(Func<QueueyOptions, QueueyOptions> fill, Func<string, string?> read)
    {
        var client = fill(new QueueyOptions
        {
            Environment = Environment,
            IngressBaseAddress = IngressBaseAddress,
            TenantPublicId = TenantPublicId,
            ApiKey = ApiKey,
            SigningKeyId = SigningKeyId,
            SigningSecret = SigningSecret,
        });

        IngressBaseAddress = client.IngressBaseAddress;
        TenantPublicId = client.TenantPublicId;
        ApiKey = client.ApiKey;
        SigningKeyId = client.SigningKeyId;
        SigningSecret = client.SigningSecret;
        // Helse-nøkkelen (security-review av #70, B1): egen variabel, bare for innsjekken, aldri fra .env.
        if (string.IsNullOrWhiteSpace(Health.ApiKey) && read(QueueyEdgeEnvironmentVariables.HealthApiKey) is { } health
            && !string.IsNullOrWhiteSpace(health))
            Health.ApiKey = health.Trim();
        return this;
    }

    /// <summary>The effective ingress base address (override or environment default).</summary>
    public Uri ResolveIngressBaseAddress()
        => new QueueyOptions { Environment = Environment, IngressBaseAddress = IngressBaseAddress }
            .ResolveIngressBaseAddress();

    /// <summary>Throws <see cref="QueueyConfigurationException"/> when required settings are missing.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SigningKeyId) != string.IsNullOrWhiteSpace(SigningSecret))
            throw new QueueyConfigurationException(
                "QueueyEdgeOptions.SigningKeyId and SigningSecret go together: set both, or neither.");
        if (!Signs && string.IsNullOrWhiteSpace(ApiKey))
            throw new QueueyConfigurationException(
                "QueueyEdgeOptions needs a signing key (SigningKeyId and SigningSecret, which `queuey keys mint --write .env` " +
                "makes) or an ApiKey. UseEnvironmentVariables() reads QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET.");
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

    /// <summary>
    /// Optional 32-byte key: when set, payloads are AES-256-GCM encrypted at
    /// rest in the spool (<see cref="SpoolPayloadProtection"/>). Read it from
    /// an environment variable or a secret store the device already has —
    /// a key stored next to the spool protects nothing. Rows written before
    /// the key was set keep draining as plain rows; a row that cannot be
    /// opened with this key is quarantined, never sent, never dropped.
    /// </summary>
    public byte[]? PayloadKey { get; set; }
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

/// <summary>
/// Opt-in health reporting to Queuey Cloud — the fleet view. The node
/// POSTs its <see cref="EdgeHealth"/> snapshot OUTBOUND to Cloud (never the
/// other way: a node behind 4G or a plant firewall has no inbound path, and
/// Queuey never wants one). Reports are never events: they are not spooled,
/// not retried, not billed. A stale report is worthless, so a failed send is
/// simply superseded by the next one.
/// </summary>
public sealed class EdgeHealthReportOptions
{
    /// <summary>Off by default. When true the node appears under "Edge nodes" in the console.</summary>
    public bool ReportToCloud { get; set; }

    /// <summary>
    /// The publish-only API key the health check-in sends, and nothing else: events stay signed with the signing pair.
    /// Queuey's check-in takes an API key today; it will take a signed check-in, and then this key goes away. Read it from
    /// <c>QUEUEY_EDGE_HEALTH_API_KEY</c> (<see cref="QueueyEdgeOptions.UseEnvironmentVariables"/> or
    /// <see cref="QueueyEdgeOptions.UseSettings(System.Func{string, string?})"/>), never a literal in code. Without it, a
    /// node that publishes with <see cref="QueueyEdgeOptions.ApiKey"/> checks in with that key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Human-readable node name shown in the console (e.g. "barge-07",
    /// "press-line-2"). Defaults to the machine name. The node's stable
    /// IDENTITY is separate: a UUID minted once and stored in the spool.
    /// </summary>
    public string? NodeName { get; set; }

    /// <summary>
    /// Steady-state cadence. A state change reports at the next 10 s tick —
    /// unless the previous report was refused, in which case the node waits
    /// out its backoff (doubling from 10 s up to this interval) or Cloud's
    /// Retry-After (capped at one hour), whichever is longer. Values under
    /// 10 s are treated as 10 s.
    /// </summary>
    public TimeSpan ReportInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>The environment variables Edge reads beyond <see cref="QueueyEnvironmentVariables"/>.</summary>
public static class QueueyEdgeEnvironmentVariables
{
    /// <summary>
    /// <c>QUEUEY_EDGE_HEALTH_API_KEY</c>: <see cref="EdgeHealthReportOptions.ApiKey"/>, the publish-only key only the health
    /// check-in sends.
    /// </summary>
    public const string HealthApiKey = "QUEUEY_EDGE_HEALTH_API_KEY";
}
