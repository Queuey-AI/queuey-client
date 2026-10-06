using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>
/// The workspace's default delivery endpoint — the layer every queue inherits its destination from.
/// Every property is optional and <c>null</c> means <b>leave alone</b>, so a deploy can move the base
/// URL without knowing the auth, and rotate the auth without resending the URL.
/// </summary>
/// <remarks>
/// Secrets never travel here. <see cref="CredentialRef"/> names a credential stored encrypted in the
/// workspace (see <c>IQueueyManagement.CreateCredentialAsync</c>); the value itself is written once,
/// out of band, and is never readable again. That is what makes a deployment file safe to commit.
/// <para>
/// The reference is the credential's <b>name</b>, not its id. Queuey stores the pointer as a
/// <c>cred_…</c> public id, but ids are minted per workspace, so a file carrying one could only ever
/// apply to the environment it was written in. A deploy resolves the name against the workspace's
/// credentials, which is what lets one file converge staging and production alike. A literal
/// <c>cred_…</c> is still accepted, for the case where you have the id and not the name.
/// </para>
/// </remarks>
public sealed class WorkspaceDelivery
{
    /// <summary>The values <see cref="AuthMode"/> accepts, in any casing. A queue's delivery takes the same.</summary>
    public static readonly IReadOnlyList<string> AuthModeValues = new[] { "None", "Bearer", "ApiKey", "Basic", "OAuth2ClientCredentials" };

    /// <summary>The values <see cref="Method"/> accepts, in any casing.</summary>
    public static readonly IReadOnlyList<string> MethodValues = new[] { "POST", "PUT", "PATCH" };

    /// <summary>Base URL every inheriting queue delivers to.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Outbound auth mode: <c>None</c>, <c>Bearer</c>, <c>ApiKey</c>, <c>Basic</c> or <c>OAuth2ClientCredentials</c>.</summary>
    public string? AuthMode { get; set; }

    /// <summary>The name of a stored credential to authenticate with. Never the secret itself.</summary>
    public string? CredentialRef { get; set; }

    /// <summary>Header name for <c>ApiKey</c> auth (e.g. <c>X-Api-Key</c>).</summary>
    public string? AuthHeaderName { get; set; }

    /// <summary>HTTP method: <c>POST</c>, <c>PUT</c> or <c>PATCH</c>.</summary>
    public string? Method { get; set; }

    /// <summary>Per-request timeout in milliseconds.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Outbound request signing. Null leaves signing untouched.</summary>
    public DeliverySigning? Signing { get; set; }

    /// <summary>Send budget. Null leaves the budget untouched.</summary>
    public DeliveryRateLimit? RateLimit { get; set; }

    internal bool IsEmpty =>
        BaseUrl is null && AuthMode is null && CredentialRef is null && AuthHeaderName is null
        && Method is null && TimeoutMs is null && Signing is null && RateLimit is null;
}

/// <summary>One queue's destination. Null properties are left alone.</summary>
public sealed class QueueDelivery
{
    /// <summary>The values <see cref="Kind"/> accepts, in any casing.</summary>
    public static readonly IReadOnlyList<string> KindValues = new[] { "http", "localForward" };

    /// <summary>
    /// Where the queue's events go: <c>http</c>, to its URL, or <c>localForward</c>, to a connected <c>queuey listen</c>
    /// session, for a queue in a development workspace. Omit it to leave the kind as it is. A local listener's address belongs
    /// to its session (<c>--forward-to</c>), never to this file, and the queue keeps its URL for when it delivers over HTTP
    /// again. A file applied to several workspaces takes it from a variable, such as <c>${QUEUEY_ORDERS_DELIVERY_KIND}</c>.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Where this queue delivers. A <b>relative path</b> (<c>/orders</c>) appends to the workspace
    /// base — the shape to reach for, since moving hosts is then one workspace edit. An absolute URL
    /// overrides the workspace outright.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>Hands the destination back to the workspace, discarding this queue's own override.</summary>
    public bool Inherit { get; set; }

    /// <summary>Outbound auth mode for this queue only. Null inherits the workspace.</summary>
    public string? AuthMode { get; set; }

    /// <summary>The name of a stored credential. Never the secret itself.</summary>
    public string? CredentialRef { get; set; }

    /// <summary>Header name for <c>ApiKey</c> auth.</summary>
    public string? AuthHeaderName { get; set; }

    /// <summary>Per-request timeout in milliseconds.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Outbound request signing for this queue only.</summary>
    public DeliverySigning? Signing { get; set; }

    /// <summary>Send budget for this queue only.</summary>
    public DeliveryRateLimit? RateLimit { get; set; }

    // Typen settes for seg (PATCH /queues/{que}/local-forward), så den teller ikke her: en levering som bare sier kind,
    // sender ingen PATCH av levering.
    internal bool IsEmpty =>
        Url is null && !Inherit && AuthMode is null && CredentialRef is null && AuthHeaderName is null
        && TimeoutMs is null && Signing is null && RateLimit is null;
}

/// <summary>HMAC request signing. <see cref="CredentialRef"/> names the stored signing secret.</summary>
public sealed class DeliverySigning
{
    /// <summary>Whether outbound requests are signed.</summary>
    public bool Enabled { get; set; }

    /// <summary>The name of the stored signing secret (or its <c>cred_…</c> id). Never the secret itself.</summary>
    public string? CredentialRef { get; set; }

    /// <summary>Which signature template to use.</summary>
    public string? TemplateKey { get; set; }
}

/// <summary>A send budget: at most <see cref="MaxRequests"/> per <see cref="PerSeconds"/>.</summary>
public sealed class DeliveryRateLimit
{
    /// <summary>Requests allowed per window.</summary>
    public int? MaxRequests { get; set; }

    /// <summary>Window length in seconds.</summary>
    public int? PerSeconds { get; set; }
}

/// <summary>A stored delivery credential — label and type only. The value is never returned.</summary>
public sealed class CredentialResult
{
    /// <summary>Credential public id.</summary>
    public string? PublicId { get; set; }

    /// <summary>The name a <c>credentialRef</c> points at.</summary>
    public string? Name { get; set; }

    /// <summary>Credential type.</summary>
    public string? Type { get; set; }

    /// <summary>Key id, for types that carry one.</summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// The version of the secret the credential holds: 1 for the one it was created with, one more each time a secret is
    /// stored under its name again. Set when it was just stored; null from a list, and from a Queuey that predates
    /// versions.
    /// </summary>
    public int? Version { get; set; }

    /// <summary>
    /// When it was just stored: true when the name was new in the workspace, false when the secret replaced the one the
    /// credential under that name held, which keeps its id. Null from a list, and from a Queuey that predates it.
    /// </summary>
    public bool? Created { get; set; }

    /// <summary>
    /// When it was just stored: true when the workspace's ingress waited for a credential by this name and verifies with it
    /// now. Null from a list, and from a Queuey that predates it, where the next <c>apply</c> points the ingress at it.
    /// </summary>
    public bool? BoundWorkspace { get; set; }

    /// <summary>When it was just stored: the queues (<c>que_…</c>) whose ingress waited for it and verifies with it now.</summary>
    public IReadOnlyList<string>? BoundQueues { get; set; }
}

// ── wire shapes ───────────────────────────────────────────────────────────────

internal sealed class PatchTenantDeliveryWireRequest
{
    public string? BaseUrl { get; set; }
    public string? AuthMode { get; set; }
    public string? CredentialRef { get; set; }
    public string? AuthHeaderName { get; set; }
    public string? Method { get; set; }
    public int? TimeoutMs { get; set; }
    public PatchSigningWire? Signing { get; set; }
    public PatchRateLimitWire? RateLimit { get; set; }
}

internal sealed class PatchQueueDeliveryWireRequest
{
    public string? Url { get; set; }
    public bool Inherit { get; set; }
    public string? AuthMode { get; set; }
    public string? CredentialRef { get; set; }
    public string? AuthHeaderName { get; set; }
    public int? TimeoutMs { get; set; }
    public PatchSigningWire? Signing { get; set; }
    public PatchRateLimitWire? RateLimit { get; set; }
}

internal sealed class PatchSigningWire
{
    public bool Enabled { get; set; }
    public string? CredentialRef { get; set; }
    public string? TemplateKey { get; set; }
}

internal sealed class PatchRateLimitWire
{
    public int? MaxRequests { get; set; }
    public int? PerSeconds { get; set; }
}

internal sealed class CreateCredentialWireRequest
{
    public string Name { get; set; } = default!;
    public string Type { get; set; } = default!;
    public string Secret { get; set; } = default!;
    public string? KeyId { get; set; }
    public string? Username { get; set; }
}

internal sealed class CredentialWireResponse
{
    public string? PublicId { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? KeyId { get; set; }
    public int? Version { get; set; }
    public bool? Created { get; set; }
    public bool? BoundWorkspace { get; set; }
    public List<string>? BoundQueues { get; set; }
}

/// <summary>Wire shape of <c>GET /tenants/{ten}/config</c> — the workspace's delivery + policy.</summary>
internal sealed class TenantConfigResponse
{
    public TenantDeliveryResponse? Delivery { get; set; }
    public QueuePolicyResponse? Policy { get; set; }
    public IngressResponse? Ingress { get; set; }

    // Miljø-merket (Queuey F2.2): null når workspacet ikke har noe, eller når API-et er eldre enn merket.
    public string? Environment { get; set; }
}

/// <summary>Wire shape of <c>GET /queues/{que}/config</c>: effective values plus per-section inherit flags.</summary>
internal sealed class QueueConfigResponse
{
    public TenantDeliveryResponse? Delivery { get; set; }
    public QueuePolicyResponse? Policy { get; set; }
    public QueueInheritResponse? Inherited { get; set; }

    /// <summary>
    /// The workspace's own effective config — what an inherited section resolves to. Pull compares
    /// against it per field, because <c>Inherited.Behavior</c> is a single flag for the whole policy
    /// block: a queue overriding one field reports the entire block as owned.
    /// </summary>
    public TenantConfigResponse? TenantBaseline { get; set; }

    /// <summary>The queue's EFFECTIVE ingress — what it resolves to after inheriting the workspace.</summary>
    public IngressResponse? Ingress { get; set; }
}

/// <summary>
/// Wire shape of <c>GET /queues/{que}</c>, read for one thing: the backoff the queue stores itself, in its
/// raw <c>overrides</c>. A field set there is the queue's own; a field left out is inherited. The rest of
/// the read is not mapped.
/// </summary>
/// <remarks>
/// Its own types, not <see cref="RetryBackoffWire"/>: the stored overrides carry jitter as the enum's
/// number, which that type reads as text.
/// </remarks>
internal sealed class QueueStoredResponse
{
    public StoredOverridesWire? Overrides { get; set; }
}

internal sealed class StoredOverridesWire
{
    public StoredRetryWire? Retry { get; set; }
}

internal sealed class StoredRetryWire
{
    public StoredBackoffWire? Backoff { get; set; }
}

internal sealed class StoredBackoffWire
{
    public int? BaseDelayMs { get; set; }
    public int? MaxDelayMs { get; set; }
}

/// <summary>Which sections a queue inherits from the workspace (true) versus overrides (false).</summary>
internal sealed class QueueInheritResponse
{
    public bool Destination { get; set; }
    public bool Auth { get; set; }
    public bool Signing { get; set; }
    public bool RateLimit { get; set; }
    public bool Behavior { get; set; }
}

/// <summary>The flat delivery read-back. Secret VALUES are never present — only refs and flags.</summary>
internal sealed class TenantDeliveryResponse
{
    // Den effektive typen, "Http" eller "LocalForward" (Queuey F2.3). Null fra et API som er eldre enn feltet.
    public string? Kind { get; set; }
    public string? BaseUrl { get; set; }
    public string? AuthMode { get; set; }
    public bool HasCredential { get; set; }
    public string? CredentialRef { get; set; }
    public string? Method { get; set; }
    public int TimeoutMs { get; set; }
    public string? AuthHeaderName { get; set; }
    public DeliveryRateLimitResponse? RateLimit { get; set; }
    public DeliverySigningResponse? Signing { get; set; }
}

internal sealed class DeliveryRateLimitResponse
{
    public int? MaxRequests { get; set; }
    public int? PerSeconds { get; set; }
}

internal sealed class DeliverySigningResponse
{
    public bool Enabled { get; set; }
    public bool HasCredential { get; set; }
    public string? TemplateKey { get; set; }
    public string? CredentialRef { get; set; }
}

/// <summary>The flat policy read-back — the fields a deployment file can declare.</summary>
internal sealed class QueuePolicyResponse
{
    public bool Idempotent { get; set; }
    public bool DlqEnabled { get; set; }
    public int RetentionDays { get; set; }
    public string? Ordering { get; set; }

    // Nullable on purpose: an API from before 2026-09-23 does not send the backoff or the filter,
    // and "not sent" must read as unknown rather than as a value.
    public RetryBackoffWire? Backoff { get; set; }
    public DeliveryFilterWire? Filter { get; set; }

    // Ingen maxAttempts eller dlqAfterAttempts: antall forsøk er ikke en innstilling (Queuey#391,
    // 2026-10-04). Et API fra før det sender dem fortsatt; de leses ikke, så pull skriver dem aldri.
}

/// <summary>An ingress signing key minted for a queue. The secret is returned <b>once</b>.</summary>
public sealed class IngressSigningKey
{
    /// <summary>The client this key belongs to.</summary>
    public string? ClientPublicId { get; init; }

    /// <summary>The client's display name.</summary>
    public string? ClientName { get; init; }

    /// <summary>Goes in <c>QueueyOptions.SigningKeyId</c>.</summary>
    public string? KeyId { get; init; }

    /// <summary>Goes in <c>QueueyOptions.SigningSecret</c>. Shown once and never retrievable again.</summary>
    public string? Secret { get; init; }

    /// <summary>The queue the key signs for.</summary>
    public string? QueuePublicId { get; init; }
}

internal sealed class CreateQueueHmacClientWireRequest
{
    public string Name { get; set; } = default!;
}

internal sealed class CreateQueueHmacClientWireResponse
{
    public string? ClientPublicId { get; set; }
    public string? ClientName { get; set; }
    public string? KeyId { get; set; }
    public string? Secret { get; set; }
    public string? QueuePublicId { get; set; }
}
