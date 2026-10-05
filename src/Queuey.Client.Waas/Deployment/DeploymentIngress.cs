using System;
using System.Collections.Generic;
using System.Linq;

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
    /// <summary>The values <see cref="AuthMode"/> accepts, in any casing.</summary>
    public static readonly IReadOnlyList<string> AuthModeValues = new[] { "None", "ApiKey", "SignedRequest", "ApiKeyAndSignedRequest" };

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

    /// <summary>
    /// Refuses what Queuey would refuse, or quietly read another way, before anything is sent.
    /// <paramref name="where"/> is the declaration's path in the file, e.g. <c>queues.orders.ingress</c>.
    /// </summary>
    internal void Validate(string where)
    {
        if (AuthMode is not null && !AuthModeValues.Contains(AuthMode.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new QueueyConfigurationException(
                $"{where}.authMode must be one of {string.Join(", ", AuthModeValues)}; got '{AuthMode}'.");

        EventType?.Validate($"{where}.eventType");
        GroupKey?.Validate($"{where}.groupKey");
    }
}

/// <summary>
/// Where one context value is read from. <c>name</c> is required, and an empty name removes the source;
/// <c>from</c> is required whenever the name names something.
/// </summary>
public sealed class ContextSource
{
    /// <summary>The values <see cref="From"/> accepts, in any casing.</summary>
    public static readonly IReadOnlyList<string> FromValues = new[] { "header", "query", "body" };

    /// <summary>Creates an empty source (for deserialization).</summary>
    public ContextSource() { }

    /// <summary>Creates a source.</summary>
    public ContextSource(string? from, string? name)
    {
        From = from;
        Name = name;
    }

    /// <summary>
    /// Where the value is read: <c>header</c>, <c>query</c>, or <c>body</c>, the top level of the JSON you post.
    /// Required whenever <c>name</c> is not empty.
    /// </summary>
    // Før 2026-10-05 var from «header» og name tom når fila ikke skrev dem. {"from":"body"} ble da sendt som et tomt navn
    // og fjernet kilden som var lagret, og {"name":"type"} betydde header uten at fila sa det.
    public string? From { get; set; }

    /// <summary>
    /// The header, query parameter, or body field to read. Required: <c>""</c> removes the source, so the
    /// value is no longer read from anywhere.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>True when the declaration removes the source rather than setting one.</summary>
    internal bool Clears => Name is not null && Name.Trim().Length == 0;

    /// <summary>Refuses a source Queuey would read another way than the file says, before anything is sent.</summary>
    internal void Validate(string where)
    {
        if (Name is null)
            throw new QueueyConfigurationException(
                $"{where} needs a name: the header, query parameter or body field to read. To remove the source, write \"name\": \"\".");

        if (Clears)
            return;

        if (From is null)
            throw new QueueyConfigurationException(
                $"{where} needs \"from\": {string.Join(", ", FromValues)}, the place '{Name}' is read from.");

        if (!FromValues.Contains(From.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new QueueyConfigurationException(
                $"{where}.from must be one of {string.Join(", ", FromValues)}; got '{From}'.");
    }
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
    public RetryBackoffWire? Backoff { get; set; }
}

internal sealed class IngressResponse
{
    public string? AuthMode { get; set; }
    public ContextSourceWire? EventType { get; set; }
    public ContextSourceWire? GroupKey { get; set; }
    public int SuccessStatusCode { get; set; }
}
