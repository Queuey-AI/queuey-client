using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Queuey.Client.Waas;

/// <summary>
/// A declarative deployment file — <c>queuey.deploy.json</c> by default. Describes the workspace's
/// delivery defaults and each queue's behaviour and destination, so a deploy converges Queuey from
/// something reviewable in a pull request.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a <b>separate file</b> from <c>queuey.json</c>. That one holds connection config —
/// an API key among it — and must not be committed; this one is meant to be. Keeping them apart is
/// what stops "commit your Queuey config" from becoming "commit your API key".
/// </para>
/// <para>
/// It carries no secrets by construction: auth and signing name a <c>credentialRef</c>, and the
/// value behind that name is written once with <c>queuey credentials set</c> and stored encrypted.
/// </para>
/// <para>
/// Every field is optional and an omitted one means <b>leave alone</b> — the same contract the whole
/// sync path uses. A file that names only a base URL changes only the base URL.
/// </para>
/// </remarks>
public sealed class DeploymentFile
{
    /// <summary>The default file name a deploy looks for.</summary>
    public const string DefaultFileName = "queuey.deploy.json";

    /// <summary>
    /// The JSON Schema this file follows, for editors and agents: the published one, or a local copy
    /// written by <c>queuey schema</c>. Ignored when the file is applied.
    /// </summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Where the published JSON Schema for this file lives.</summary>
    public const string SchemaUrl = "https://raw.githubusercontent.com/Queuey-AI/queuey-client/main/schema/queuey.deploy.schema.json";

    /// <summary>
    /// The JSON Schema for a deployment file: every field, the values each one accepts, and what it
    /// does. Generated from this model, so it cannot describe a field the parser would reject.
    /// </summary>
    public static string JsonSchema => LazySchema.Value;

    private static readonly Lazy<string> LazySchema = new(() =>
    {
        using System.IO.Stream stream = typeof(DeploymentFile).Assembly
            .GetManifestResourceStream("Queuey.Client.Waas.queuey.deploy.schema.json")
            ?? throw new InvalidOperationException("The deployment file's JSON Schema is missing from this build.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// The workspace (<c>ten_…</c>) this file describes. Optional — the CLI falls back to the tenant
    /// in the connection config, so the same file can be applied to staging and production.
    /// </summary>
    public string? Tenant { get; set; }

    /// <summary>The workspace — the layer every queue inherits from when it says nothing itself.</summary>
    public DeploymentWorkspace? Workspace { get; set; }

    /// <summary>
    /// <see cref="Tenant"/> with its <c>${VAR}</c> expanded — the workspace an apply of this file
    /// writes to, for a command that must reach the same one. Null when the file names none.
    /// Only the tenant is expanded, so an unset variable elsewhere in the file does not matter here.
    /// </summary>
    public string? ResolveTenant(Func<string, string?>? lookup = null)
        => DeploymentVariables.Expand(Tenant, lookup, "tenant");

    /// <summary>The queues to converge, keyed by queue name.</summary>
    public Dictionary<string, DeploymentQueue> Queues { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Parses a deployment file. Throws <see cref="QueueyConfigurationException"/> on malformed JSON.</summary>
    public static DeploymentFile Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new DeploymentFile();

        try
        {
            return JsonSerializer.Deserialize<DeploymentFile>(json, ReadOptions) ?? new DeploymentFile();
        }
        catch (JsonException ex)
        {
            throw new QueueyConfigurationException($"Could not parse the deployment file: {ex.Message}");
        }
    }

    /// <summary>
    /// Expands every <c>${VAR}</c> in the file against the environment, returning the file with the
    /// values a deploy will actually send. An unset variable throws — see
    /// <see cref="DeploymentVariables"/> for why that is not an empty string.
    /// </summary>
    /// <param name="lookup">Variable resolver; defaults to the process environment.</param>
    public DeploymentFile Expand(Func<string, string?>? lookup = null)
    {
        var expanded = new DeploymentFile
        {
            Schema = Schema,
            Tenant = DeploymentVariables.Expand(Tenant, lookup, "tenant"),
            Workspace = Workspace is null ? null : new DeploymentWorkspace
            {
                Ordering = Workspace.Ordering,
                DlqEnabled = Workspace.DlqEnabled,
                RetentionDays = Workspace.RetentionDays,
                Idempotent = Workspace.Idempotent,
                MaxAttempts = Workspace.MaxAttempts,
                DlqAfterAttempts = Workspace.DlqAfterAttempts,
                Backoff = Workspace.Backoff,
                RetryOnNetworkErrors = Workspace.RetryOnNetworkErrors,
                RetryOnTimeouts = Workspace.RetryOnTimeouts,
                Ingress = Workspace.Ingress,
                Delivery = Workspace.Delivery is null ? null : new WorkspaceDelivery
                {
                    BaseUrl = DeploymentVariables.Expand(Workspace.Delivery.BaseUrl, lookup, "workspace.delivery.baseUrl"),
                    AuthMode = Workspace.Delivery.AuthMode,
                    CredentialRef = DeploymentVariables.Expand(Workspace.Delivery.CredentialRef, lookup, "workspace.delivery.credentialRef"),
                    AuthHeaderName = Workspace.Delivery.AuthHeaderName,
                    Method = Workspace.Delivery.Method,
                    TimeoutMs = Workspace.Delivery.TimeoutMs,
                    Signing = Workspace.Delivery.Signing,
                    RateLimit = Workspace.Delivery.RateLimit,
                },
            },
        };

        foreach (KeyValuePair<string, DeploymentQueue> entry in Queues)
        {
            DeploymentQueue q = entry.Value ?? new DeploymentQueue();
            expanded.Queues[entry.Key] = new DeploymentQueue
            {
                Mode = q.Mode,
                Ordering = q.Ordering,
                DlqEnabled = q.DlqEnabled,
                RetentionDays = q.RetentionDays,
                Idempotent = q.Idempotent,
                MaxAttempts = q.MaxAttempts,
                DlqAfterAttempts = q.DlqAfterAttempts,
                Backoff = q.Backoff,
                RetryOnNetworkErrors = q.RetryOnNetworkErrors,
                RetryOnTimeouts = q.RetryOnTimeouts,
                Filter = q.Filter,
                // Ingress på kø-nivå ble ikke kopiert her før (2026-09-23), så apply, check og
                // dry-run droppet den stille: alle tre ekspanderer fila først.
                Ingress = q.Ingress,
                Delivery = q.Delivery is null ? null : new QueueDelivery
                {
                    Url = DeploymentVariables.Expand(q.Delivery.Url, lookup, $"queues.{entry.Key}.delivery.url"),
                    Inherit = q.Delivery.Inherit,
                    AuthMode = q.Delivery.AuthMode,
                    CredentialRef = DeploymentVariables.Expand(q.Delivery.CredentialRef, lookup, $"queues.{entry.Key}.delivery.credentialRef"),
                    AuthHeaderName = q.Delivery.AuthHeaderName,
                    TimeoutMs = q.Delivery.TimeoutMs,
                    Signing = q.Delivery.Signing,
                    RateLimit = q.Delivery.RateLimit,
                },
            };
        }

        return expanded;
    }

    /// <summary>Every environment variable this file references, for a dry run's report.</summary>
    public IReadOnlyList<string> ReferencedVariables()
    {
        var names = new List<string>();
        names.AddRange(DeploymentVariables.Referenced(Tenant));
        names.AddRange(DeploymentVariables.Referenced(Workspace?.Delivery?.BaseUrl));
        names.AddRange(DeploymentVariables.Referenced(Workspace?.Delivery?.CredentialRef));

        foreach (DeploymentQueue q in Queues.Values)
        {
            names.AddRange(DeploymentVariables.Referenced(q?.Delivery?.Url));
            names.AddRange(DeploymentVariables.Referenced(q?.Delivery?.CredentialRef));
        }

        var seen = new List<string>();
        foreach (string n in names)
            if (!seen.Contains(n, StringComparer.Ordinal))
                seen.Add(n);
        return seen;
    }

    /// <summary>
    /// Resolves the file into the definitions and delivery patches a sync applies, validating names
    /// and policy locally so a typo fails before anything is written.
    /// </summary>
    public IReadOnlyList<DeploymentQueuePlan> Resolve()
    {
        var plans = new List<DeploymentQueuePlan>();

        // The workspace's retry declaration is checked like a queue's, so a typo there fails here too.
        if (Workspace?.AsPolicy().Validate() is { } workspaceReason)
            throw new QueueyConfigurationException($"The workspace has an invalid policy: {workspaceReason}");

        foreach (KeyValuePair<string, DeploymentQueue> entry in Queues)
        {
            DeploymentQueue declared = entry.Value ?? new DeploymentQueue();

            // The key is the queue name — validated as written, like every other name a caller chose.
            QueueyName.EnsureValid(entry.Key, "queue name");

            var definition = QueueDefinitionFactory.FromName(entry.Key, new QueueOptions
            {
                Policy =
                {
                    Ordering = declared.Ordering,
                    DlqEnabled = declared.DlqEnabled,
                    RetentionDays = declared.RetentionDays,
                    Idempotent = declared.Idempotent,
                    MaxAttempts = declared.MaxAttempts,
                    DlqAfterAttempts = declared.DlqAfterAttempts,
                    Backoff = declared.Backoff,
                    RetryOnNetworkErrors = declared.RetryOnNetworkErrors,
                    RetryOnTimeouts = declared.RetryOnTimeouts,
                    Filter = declared.Filter,
                },
            });

            plans.Add(new DeploymentQueuePlan(definition, declared.Delivery, declared.Ingress,
                DeploymentQueueModes.Parse(declared.Mode, $"queues.{entry.Key}.mode")));
        }

        return plans;
    }

    /// <summary>
    /// Renders the file as JSON — what <c>queuey pull</c> writes. Null properties are omitted, so the
    /// output says only what the workspace actually owns and stays diffable against a hand-written file.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelled field is a silent no-op otherwise — exactly the failure a declarative file
        // must not have, since the deploy would report success while ignoring what you wrote.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>One queue's declaration: behaviour, and optionally its own destination.</summary>
public sealed class DeploymentQueue
{
    /// <summary>
    /// <c>deliver</c> or <c>logOnly</c>. Omit it and a queue this file creates delivers when it has a
    /// destination (its own <c>delivery.url</c> or the workspace's base URL) and logs events until it
    /// has one; an existing queue keeps its mode. Pausing is not a mode and never set by a deploy.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>How many times an event is attempted before it gives up.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>After how many attempts an event goes to the DLQ. Must be below <c>maxAttempts</c>.</summary>
    public int? DlqAfterAttempts { get; set; }

    /// <summary>How long to wait between attempts.</summary>
    public RetryBackoff? Backoff { get; set; }

    /// <summary>Whether a network error (no response at all) is retried.</summary>
    public bool? RetryOnNetworkErrors { get; set; }

    /// <summary>Whether a timeout is retried.</summary>
    public bool? RetryOnTimeouts { get; set; }

    /// <summary>Which events this queue delivers. Omit it to deliver every event.</summary>
    public DeliveryFilter? Filter { get; set; }

    /// <summary>Delivery ordering: <c>fifo</c>, <c>bykey</c> or <c>besteffort</c>.</summary>
    public string? Ordering { get; set; }

    /// <summary>Whether a dead-letter queue collects exhausted events.</summary>
    public bool? DlqEnabled { get; set; }

    /// <summary>Days events are retained.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>Whether duplicate publishes are collapsed by idempotency key.</summary>
    public bool? Idempotent { get; set; }

    /// <summary>
    /// This queue's destination. Omit it — the usual case — and the queue inherits the workspace.
    /// A relative <c>url</c> appends to the workspace base, which is the shape to reach for.
    /// </summary>
    public QueueDelivery? Delivery { get; set; }

    /// <summary>
    /// How this queue reads arriving events, when it differs from the workspace. Omit it and the
    /// queue inherits — which is what you want unless one producer sends a different shape.
    /// </summary>
    public DeploymentIngress? Ingress { get; set; }
}

/// <summary>
/// The workspace: the layer every queue inherits from. Behaviour and ingress sit at the top, the
/// destination in <see cref="Delivery"/> — the same split a queue has, one level up.
/// </summary>
public sealed class DeploymentWorkspace
{
    /// <summary>Lane strategy for every queue that does not override it: <c>fifo</c>, <c>bykey</c>, <c>besteffort</c>.</summary>
    public string? Ordering { get; set; }

    /// <summary>Whether a dead-letter queue collects events the receiver rejected.</summary>
    public bool? DlqEnabled { get; set; }

    /// <summary>How many days events are retained.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>Whether duplicate publishes are collapsed by idempotency key.</summary>
    public bool? Idempotent { get; set; }

    /// <summary>How many times an event is attempted, for every queue that does not say.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>After how many attempts an event goes to the DLQ. Must be below <c>maxAttempts</c>.</summary>
    public int? DlqAfterAttempts { get; set; }

    /// <summary>How long to wait between attempts, for every queue that does not say.</summary>
    public RetryBackoff? Backoff { get; set; }

    /// <summary>Whether a network error (no response at all) is retried.</summary>
    public bool? RetryOnNetworkErrors { get; set; }

    /// <summary>Whether a timeout is retried.</summary>
    public bool? RetryOnTimeouts { get; set; }

    /// <summary>Where events are delivered — the base every queue appends its path to.</summary>
    public WorkspaceDelivery? Delivery { get; set; }

    /// <summary>How arriving events are read: ingress auth, and where the type and key come from.</summary>
    public DeploymentIngress? Ingress { get; set; }

    internal bool HasPolicy => Ordering is not null || DlqEnabled is not null
                            || RetentionDays is not null || Idempotent is not null
                            || MaxAttempts is not null || DlqAfterAttempts is not null
                            || (Backoff is not null && !Backoff.IsEmpty)
                            || RetryOnNetworkErrors is not null || RetryOnTimeouts is not null;

    /// <summary>The workspace's retry declaration as a policy, so it is validated like a queue's.</summary>
    internal QueuePolicy AsPolicy() => new()
    {
        Ordering = Ordering,
        DlqEnabled = DlqEnabled,
        RetentionDays = RetentionDays,
        Idempotent = Idempotent,
        MaxAttempts = MaxAttempts,
        DlqAfterAttempts = DlqAfterAttempts,
        Backoff = Backoff,
        RetryOnNetworkErrors = RetryOnNetworkErrors,
        RetryOnTimeouts = RetryOnTimeouts,
    };
}

/// <summary>One resolved queue from a deployment file: what to apply, and what to point it at.</summary>
public sealed class DeploymentQueuePlan
{
    internal DeploymentQueuePlan(QueueDefinition definition, QueueDelivery? delivery, DeploymentIngress? ingress = null,
        DeploymentQueueMode? mode = null)
    {
        Definition = definition;
        Delivery = delivery is null || delivery.IsEmpty ? null : delivery;
        Ingress = ingress is null || ingress.IsEmpty ? null : ingress;
        Mode = mode;
    }

    /// <summary>The mode the file declares, or <c>null</c> to leave it (see <see cref="DeploymentQueue.Mode"/>).</summary>
    public DeploymentQueueMode? Mode { get; }

    /// <summary>The queue to converge (name + behaviour).</summary>
    public QueueDefinition Definition { get; }

    /// <summary>Its destination patch, or <c>null</c> when the queue inherits the workspace.</summary>
    public QueueDelivery? Delivery { get; }

    /// <summary>Its ingress patch, or <c>null</c> when the queue reads events like the workspace does.</summary>
    public DeploymentIngress? Ingress { get; }
}

/// <summary>Whether a queue delivers events or only logs them.</summary>
/// <remarks>
/// Paused is not here on purpose: pausing is something an operator does to a running queue, and a
/// deploy that set it would unpause whatever the operator paused. A deploy never touches it.
/// </remarks>
public enum DeploymentQueueMode
{
    /// <summary>Events are delivered to the queue's destination.</summary>
    Deliver,

    /// <summary>Events are stored as <c>Logged</c> and never delivered — for a queue with nowhere to deliver yet.</summary>
    LogOnly,
}

/// <summary>The text and wire forms of <see cref="DeploymentQueueMode"/>.</summary>
public static class DeploymentQueueModes
{
    /// <summary>The values a deployment file accepts for <c>mode</c>.</summary>
    public static readonly IReadOnlyList<string> Values = new[] { "deliver", "logOnly" };

    /// <summary>The file's text form: <c>deliver</c> or <c>logOnly</c>.</summary>
    public static string ToFileText(this DeploymentQueueMode mode) => mode == DeploymentQueueMode.Deliver ? "deliver" : "logOnly";

    // Enums er tall på ledningen (se CLAUDE.md): backendens QueueMode har LogOnly = 1, Deliver = 3.
    internal static int ToWire(this DeploymentQueueMode mode) => mode == DeploymentQueueMode.Deliver ? 3 : 1;

    /// <summary>
    /// The mode a backend reports, or <c>null</c> when it is neither — Paused, or a value this
    /// client does not know. Read as text or number, since the list endpoint reports text.
    /// </summary>
    internal static DeploymentQueueMode? FromBackend(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "deliver" or "3" => DeploymentQueueMode.Deliver,
        "logonly" or "1" => DeploymentQueueMode.LogOnly,
        _ => null,
    };

    /// <summary>
    /// Parses a file's <c>mode</c>. Null stays null (leave the mode alone); anything but the two
    /// values is a configuration error that names them, and <c>paused</c> says why it is not one.
    /// </summary>
    internal static DeploymentQueueMode? Parse(string? text, string where)
    {
        if (text is null) return null;
        switch (text.Trim().ToLowerInvariant())
        {
            case "deliver": return DeploymentQueueMode.Deliver;
            case "logonly": return DeploymentQueueMode.LogOnly;
            case "paused":
                throw new QueueyConfigurationException(
                    $"{where}: 'paused' is not a mode a deployment file sets — pausing is an operator's decision about a " +
                    "running queue, and a deploy that set it would undo theirs. Use deliver or logOnly.");
            default:
                throw new QueueyConfigurationException(
                    $"{where} must be one of {string.Join(", ", Values)}; got '{text}'.");
        }
    }
}
