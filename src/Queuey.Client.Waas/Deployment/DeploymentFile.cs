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
    /// The workspace (<c>ten_…</c>) this file describes. Optional — the CLI falls back to the tenant
    /// in the connection config, so the same file can be applied to staging and production.
    /// </summary>
    public string? Tenant { get; set; }

    /// <summary>The workspace's default delivery endpoint — what queues inherit their destination from.</summary>
    public WorkspaceDelivery? Workspace { get; set; }

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
            Tenant = DeploymentVariables.Expand(Tenant, lookup, "tenant"),
            Workspace = Workspace is null ? null : new WorkspaceDelivery
            {
                BaseUrl = DeploymentVariables.Expand(Workspace.BaseUrl, lookup, "workspace.baseUrl"),
                AuthMode = Workspace.AuthMode,
                CredentialRef = DeploymentVariables.Expand(Workspace.CredentialRef, lookup, "workspace.credentialRef"),
                AuthHeaderName = Workspace.AuthHeaderName,
                Method = Workspace.Method,
                TimeoutMs = Workspace.TimeoutMs,
                Signing = Workspace.Signing,
                RateLimit = Workspace.RateLimit,
            },
        };

        foreach (KeyValuePair<string, DeploymentQueue> entry in Queues)
        {
            DeploymentQueue q = entry.Value ?? new DeploymentQueue();
            expanded.Queues[entry.Key] = new DeploymentQueue
            {
                Ordering = q.Ordering,
                MaxAttempts = q.MaxAttempts,
                DlqEnabled = q.DlqEnabled,
                DlqAfterAttempts = q.DlqAfterAttempts,
                RetentionDays = q.RetentionDays,
                Idempotent = q.Idempotent,
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
        names.AddRange(DeploymentVariables.Referenced(Workspace?.BaseUrl));
        names.AddRange(DeploymentVariables.Referenced(Workspace?.CredentialRef));

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
                    MaxAttempts = declared.MaxAttempts,
                    DlqEnabled = declared.DlqEnabled,
                    DlqAfterAttempts = declared.DlqAfterAttempts,
                    RetentionDays = declared.RetentionDays,
                    Idempotent = declared.Idempotent,
                },
            });

            plans.Add(new DeploymentQueuePlan(definition, declared.Delivery));
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
    /// <summary>Delivery ordering: <c>fifo</c>, <c>bykey</c> or <c>besteffort</c>.</summary>
    public string? Ordering { get; set; }

    /// <summary>Delivery attempts before an event is parked.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>Whether a dead-letter queue collects exhausted events.</summary>
    public bool? DlqEnabled { get; set; }

    /// <summary>Attempts before an event is dead-lettered.</summary>
    public int? DlqAfterAttempts { get; set; }

    /// <summary>Days events are retained.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>Whether duplicate publishes are collapsed by idempotency key.</summary>
    public bool? Idempotent { get; set; }

    /// <summary>
    /// This queue's destination. Omit it — the usual case — and the queue inherits the workspace.
    /// A relative <c>url</c> appends to the workspace base, which is the shape to reach for.
    /// </summary>
    public QueueDelivery? Delivery { get; set; }
}

/// <summary>One resolved queue from a deployment file: what to apply, and what to point it at.</summary>
public sealed class DeploymentQueuePlan
{
    internal DeploymentQueuePlan(QueueDefinition definition, QueueDelivery? delivery)
    {
        Definition = definition;
        Delivery = delivery is null || delivery.IsEmpty ? null : delivery;
    }

    /// <summary>The queue to converge (name + behaviour).</summary>
    public QueueDefinition Definition { get; }

    /// <summary>Its destination patch, or <c>null</c> when the queue inherits the workspace.</summary>
    public QueueDelivery? Delivery { get; }
}
