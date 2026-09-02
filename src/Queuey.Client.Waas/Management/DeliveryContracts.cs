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
/// </remarks>
public sealed class WorkspaceDelivery
{
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

    internal bool IsEmpty =>
        Url is null && !Inherit && AuthMode is null && CredentialRef is null && AuthHeaderName is null
        && TimeoutMs is null && Signing is null && RateLimit is null;
}

/// <summary>HMAC request signing. <see cref="CredentialRef"/> names the stored signing secret.</summary>
public sealed class DeliverySigning
{
    /// <summary>Whether outbound requests are signed.</summary>
    public bool Enabled { get; set; }

    /// <summary>The name of the stored signing secret. Never the secret itself.</summary>
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
}
