using System;

namespace Queuey.Client.Waas;

/// <summary>
/// A request for the secret of a credential, which a signed-in person fulfils in the Queuey console
/// (<see cref="IQueueyManagement.RequestCredentialAsync"/>). A request never carries the value, no API returns it, and Queuey
/// uses it only where the workspace's configuration does.
/// </summary>
public sealed class CredentialRequestResult
{
    /// <summary>The request's public id (<c>creq_…</c>).</summary>
    public string? RequestId { get; init; }

    /// <summary>The workspace the credential is stored in (<c>ten_…</c>).</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The workspace's name. Null from a Queuey that predates it.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>The organization the workspace belongs to. Null from a Queuey that predates it.</summary>
    public string? OrganizationName { get; init; }

    /// <summary>The name the credential is stored under: the one a deployment file's <c>credentialRef</c> names.</summary>
    public string? Name { get; init; }

    /// <summary>The credential's type, such as <c>HmacSigning</c>.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// The key id stored with the value. Null for a value that replaces a stored secret: the credential keeps its own.
    /// </summary>
    public string? KeyId { get; init; }

    /// <summary>The username stored with a <c>BasicPassword</c> value.</summary>
    public string? Username { get; init; }

    /// <summary><c>open</c>, <c>fulfilled</c> or <c>expired</c>.</summary>
    public string? Status { get; init; }

    /// <summary>
    /// The console page where a signed-in person pastes the value. It gives nothing by itself: the page asks the person to
    /// sign in, and only one who may manage the workspace's credentials can fulfil it. Null when the Queuey instance has no
    /// console address.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>Who asked: a person's user id, or <c>api:</c> and the key's client.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>When the request was made.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When the request stops taking a value.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When a person pasted the value.</summary>
    public DateTimeOffset? FulfilledAt { get; init; }

    /// <summary>The credential the value was stored as (<c>cred_…</c>), once fulfilled.</summary>
    public string? CredentialId { get; init; }

    /// <summary>The version of the credential's secret the value became, once fulfilled.</summary>
    public int? CredentialVersion { get; init; }

    /// <summary>
    /// While open: the credential that has the name now (<c>cred_…</c>), whose secret the value replaces as a new version.
    /// Null when the name is new in the workspace.
    /// </summary>
    public string? ReplacesCredentialId { get; init; }
}

// ── wire shapes ───────────────────────────────────────────────────────────────

/// <summary>
/// <c>POST /tenants/{ten}/credential-requests</c>. Queuey refuses a field it does not know, so this has only the four it
/// takes, and never a value.
/// </summary>
internal sealed class CreateCredentialRequestWireRequest
{
    public string Name { get; set; } = default!;
    public string? Type { get; set; }
    public string? KeyId { get; set; }
    public string? Username { get; set; }
}

internal sealed class CredentialRequestWireResponse
{
    public string? RequestId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
    public string? OrganizationName { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? KeyId { get; set; }
    public string? Username { get; set; }
    public string? Status { get; set; }
    public string? Url { get; set; }
    public string? RequestedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? FulfilledAt { get; set; }
    public string? CredentialId { get; set; }
    public int? CredentialVersion { get; set; }
    public string? ReplacesCredentialId { get; set; }
}
