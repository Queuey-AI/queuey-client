using System;

namespace Queuey.Client;

/// <summary>
/// Configuration for a <see cref="QueueyClient"/>.
/// </summary>
/// <remarks>
/// Queuey runs on <b>two</b> hosts and the SDK talks to both:
/// <list type="bullet">
/// <item><b>Ingress host</b> (publish) — <c>https://ingress.queuey.ai</c>. Authenticated with an
/// <c>X-Api-Key</c> (<see cref="ApiKey"/>) or HMAC signing (<see cref="SigningKeyId"/> +
/// <see cref="SigningSecret"/>). No license header.</item>
/// <item><b>API host</b> (control plane: management, WaaS, SyncModels, partner ops) —
/// <c>https://api.queuey.ai</c>. Authenticated with the same <c>X-Api-Key</c> plus the
/// <c>X-License-PublicId</c> header (<see cref="LicensePublicId"/>).</item>
/// </list>
/// A single <b>license-wide FullAccess</b> API key covers publish + sync + management. The optional
/// <see cref="ManagementToken"/> (Entra bearer) is only needed for the cross-organization
/// partner-relation surface, which is out of scope for V1.
/// <para>Set <see cref="Environment"/> to pick default hosts (defaults to <see cref="QueueyEnvironment.Production"/>);
/// override either host explicitly with <see cref="ApiBaseAddress"/> / <see cref="IngressBaseAddress"/>.</para>
/// </remarks>
public sealed class QueueyOptions
{
    /// <summary>Named deployment used to resolve default host addresses. Defaults to Production.</summary>
    public QueueyEnvironment Environment { get; set; } = QueueyEnvironment.Production;

    /// <summary>Override for the control-plane (API) host. When null, derived from <see cref="Environment"/>.</summary>
    public Uri? ApiBaseAddress { get; set; }

    /// <summary>Override for the ingress (publish) host. When null, derived from <see cref="Environment"/>.</summary>
    public Uri? IngressBaseAddress { get; set; }

    /// <summary>The tenant public id (<c>ten_…</c>) events are published under.</summary>
    public string? TenantPublicId { get; set; }

    /// <summary>
    /// API key in the form <c>qak_&lt;keyId&gt;.&lt;secret&gt;</c>, sent as the <c>X-Api-Key</c> header.
    /// A license-wide FullAccess key is the primary credential for the whole SDK.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>HMAC signing key id (sent as <c>X-Queuey-Key-Id</c>). Alternative ingress auth to <see cref="ApiKey"/>.</summary>
    public string? SigningKeyId { get; set; }

    /// <summary>HMAC signing secret used to compute the request signature. Alternative ingress auth to <see cref="ApiKey"/>.</summary>
    public string? SigningSecret { get; set; }

    /// <summary>License public id sent as <c>X-License-PublicId</c> to scope control-plane calls.</summary>
    public string? LicensePublicId { get; set; }

    /// <summary>
    /// Optional Entra (Azure AD) bearer token. Not required for V1 — only the cross-organization
    /// partner-relation surface needs a user bearer token.
    /// </summary>
    public string? ManagementToken { get; set; }

    /// <summary>Optional default value for the <c>X-Queuey-Source</c> trace header on publishes.</summary>
    public string? Source { get; set; }

    /// <summary>Request timeout applied when the SDK owns the <see cref="System.Net.Http.HttpClient"/>.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>The effective ingress (publish) base address: <see cref="IngressBaseAddress"/> or the environment default.</summary>
    public Uri ResolveIngressBaseAddress() =>
        IngressBaseAddress is null ? QueueyHosts.Ingress(Environment) : ValidateOverride(IngressBaseAddress, nameof(IngressBaseAddress));

    /// <summary>The effective control-plane (API) base address: <see cref="ApiBaseAddress"/> or the environment default.</summary>
    public Uri ResolveApiBaseAddress() =>
        ApiBaseAddress is null ? QueueyHosts.Api(Environment) : ValidateOverride(ApiBaseAddress, nameof(ApiBaseAddress));

    /// <summary>
    /// Validates a host override. Any absolute <c>http</c>/<c>https</c> URI is accepted — a local instance
    /// (<c>http://localhost:5084</c>) or a self-hosted one (<c>https://queuey.acme.io</c>) alike.
    /// </summary>
    private static Uri ValidateOverride(Uri value, string propertyName)
    {
        if (!value.IsAbsoluteUri ||
            (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps))
        {
            throw new QueueyConfigurationException(
                $"QueueyOptions.{propertyName} must be an absolute http(s) URI " +
                $"(e.g. new Uri(\"http://localhost:5084\")). Got: '{value}'.");
        }

        return value;
    }
}
