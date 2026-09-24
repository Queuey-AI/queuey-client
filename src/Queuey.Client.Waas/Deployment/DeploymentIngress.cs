namespace Queuey.Client.Waas;

/// <summary>
/// How Queuey reads events as they arrive — declared on the workspace so every queue inherits it,
/// or on one queue to override.
/// </summary>
/// <remarks>
/// <para>
/// The point of the sources is that an event describes itself. Without them every producer has to
/// send <c>X-Queuey-Event-Type</c> and <c>X-Queuey-Group-Key</c> on every publish; with them the
/// workspace says once that the type is the <c>type</c> field in the body, and every queue reads it
/// the same way — including producers you do not control, like a third-party webhook.
/// </para>
/// <para>
/// Declaring a <see cref="GroupKey"/> also means "lane by it": partitioning needs a key, and a key
/// with nothing partitioning on it would be decoration.
/// </para>
/// </remarks>
public sealed class DeploymentIngress
{
    /// <summary>
    /// What the ingress edge demands of a publisher: <c>None</c>, <c>ApiKey</c>,
    /// <c>SignedRequest</c> or <c>ApiKeyAndSignedRequest</c>.
    /// </summary>
    /// <remarks>
    /// The one field here that can stop traffic. Queuey defaults a new queue to <c>None</c> so a
    /// first webhook works without key ceremony — turning it on against publishers that carry no
    /// key rejects them from the next event onward. Roll the credential out first.
    /// </remarks>
    public string? AuthMode { get; set; }

    /// <summary>Where the event type is read from. Null leaves it as it is.</summary>
    public ContextSource? EventType { get; set; }

    /// <summary>Where the group key is read from — and, by declaring it, what to lane on.</summary>
    public ContextSource? GroupKey { get; set; }

    /// <summary>
    /// The status the ingress returns on accept. Default 202. Some senders are stricter than the
    /// HTTP spec and treat anything but 200 as a failed webhook, so those queues set 200.
    /// </summary>
    public int? SuccessStatusCode { get; set; }

    internal bool IsEmpty => AuthMode is null && EventType is null && GroupKey is null && SuccessStatusCode is null;
}

/// <summary>Where one context value is read from.</summary>
public sealed class ContextSource
{
    /// <summary>Creates an empty source (for deserialization).</summary>
    public ContextSource() { }

    /// <summary>Creates a source.</summary>
    public ContextSource(string from, string name)
    {
        From = from;
        Name = name;
    }

    /// <summary>
    /// <c>header</c>, <c>query</c>, or <c>body</c> — the top level of the JSON you post.
    /// </summary>
    public string From { get; set; } = "header";

    /// <summary>The header, query parameter, or field name. Empty clears the source.</summary>
    public string Name { get; set; } = string.Empty;
}

// ── wire ──────────────────────────────────────────────────────────────────────

internal sealed class PatchIngressWireRequest
{
    public string? AuthMode { get; set; }
    public ContextSourceWire? EventType { get; set; }
    public ContextSourceWire? GroupKey { get; set; }
    public int? SuccessStatusCode { get; set; }
}

internal sealed class ContextSourceWire
{
    public string From { get; set; } = default!;
    public string Name { get; set; } = default!;
}

internal sealed class PatchTenantPolicyWireRequest
{
    public string? Ordering { get; set; }
    public bool? DlqEnabled { get; set; }
    public int? RetentionDays { get; set; }
    public bool? Idempotent { get; set; }
    public int? MaxAttempts { get; set; }
    public int? DlqAfterAttempts { get; set; }
    public RetryBackoffWire? Backoff { get; set; }
    public bool? RetryOnNetworkErrors { get; set; }
    public bool? RetryOnTimeouts { get; set; }
}

internal sealed class IngressResponse
{
    public string? AuthMode { get; set; }
    public ContextSourceWire? EventType { get; set; }
    public ContextSourceWire? GroupKey { get; set; }
    public int SuccessStatusCode { get; set; }
}
