using System;
using System.Threading;
using System.Threading.Tasks;

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
/// <item><b>API host</b> (control plane: management, WaaS, SyncStreams, partner ops) —
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

    /// <summary>
    /// Returns an OAuth access token for the API host, sent as <c>Authorization: Bearer</c> on control-plane calls when no
    /// <see cref="ApiKey"/> is set. It is called before each request, so it can renew the token. The <c>queuey</c> CLI sets
    /// it from <c>queuey login</c>. The ingress never takes the token: publishing needs <see cref="ApiKey"/> or signing.
    /// </summary>
    public Func<CancellationToken, Task<string>>? AccessTokenProvider { get; set; }

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

    /// <summary>
    /// Fills each setting that is not set yet from its environment variable (<see cref="QueueyEnvironmentVariables"/>), such
    /// as the signing key <c>queuey keys mint --write .env</c> writes. A value set in code wins. Returns these options.
    /// </summary>
    /// <param name="read">Reads a variable; <see cref="System.Environment.GetEnvironmentVariable(string)"/> when null.</param>
    public QueueyOptions UseEnvironmentVariables(Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        string? Read(string name) => read(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        ApiKey ??= Read(QueueyEnvironmentVariables.ApiKey);
        SigningKeyId ??= Read(QueueyEnvironmentVariables.SigningKeyId);
        SigningSecret ??= Read(QueueyEnvironmentVariables.SigningSecret);
        TenantPublicId ??= Read(QueueyEnvironmentVariables.Tenant);
        LicensePublicId ??= Read(QueueyEnvironmentVariables.License);
        if (ApiBaseAddress is null && Read(QueueyEnvironmentVariables.ApiBase) is { } api)
            ApiBaseAddress = new Uri(api, UriKind.Absolute);
        if (IngressBaseAddress is null && Read(QueueyEnvironmentVariables.IngressBase) is { } ingress)
            IngressBaseAddress = new Uri(ingress, UriKind.Absolute);
        return this;
    }

    /// <summary>The effective ingress (publish) base address: <see cref="IngressBaseAddress"/> or the environment default.</summary>
    public Uri ResolveIngressBaseAddress() =>
        IngressBaseAddress is null ? QueueyHosts.Ingress(Environment) : ValidateOverride(IngressBaseAddress, nameof(IngressBaseAddress));

    /// <summary>The effective control-plane (API) base address: <see cref="ApiBaseAddress"/> or the environment default.</summary>
    public Uri ResolveApiBaseAddress() =>
        ApiBaseAddress is null ? QueueyHosts.Api(Environment) : ValidateOverride(ApiBaseAddress, nameof(ApiBaseAddress));

    /// <summary>
    /// Validates a host override. Any absolute <c>http</c>/<c>https</c> URI is accepted — e.g. a
    /// locally-running instance (<c>http://localhost:5084</c>), typically for testing.
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

/// <summary>The environment variables <see cref="QueueyOptions.UseEnvironmentVariables"/> reads, and the <c>queuey</c> CLI uses.</summary>
public static class QueueyEnvironmentVariables
{
    /// <summary><c>QUEUEY_API_KEY</c>: <see cref="QueueyOptions.ApiKey"/>.</summary>
    public const string ApiKey = "QUEUEY_API_KEY";

    /// <summary><c>QUEUEY_SIGNING_KEY_ID</c>: <see cref="QueueyOptions.SigningKeyId"/>, as <c>queuey keys mint --write</c> writes it.</summary>
    public const string SigningKeyId = "QUEUEY_SIGNING_KEY_ID";

    /// <summary><c>QUEUEY_SIGNING_SECRET</c>: <see cref="QueueyOptions.SigningSecret"/>, as <c>queuey keys mint --write</c> writes it.</summary>
    public const string SigningSecret = "QUEUEY_SIGNING_SECRET";

    /// <summary><c>QUEUEY_TENANT</c>: <see cref="QueueyOptions.TenantPublicId"/>.</summary>
    public const string Tenant = "QUEUEY_TENANT";

    /// <summary><c>QUEUEY_LICENSE</c>: <see cref="QueueyOptions.LicensePublicId"/>.</summary>
    public const string License = "QUEUEY_LICENSE";

    /// <summary><c>QUEUEY_API_BASE</c>: <see cref="QueueyOptions.ApiBaseAddress"/>.</summary>
    public const string ApiBase = "QUEUEY_API_BASE";

    /// <summary><c>QUEUEY_INGRESS_BASE</c>: <see cref="QueueyOptions.IngressBaseAddress"/>.</summary>
    public const string IngressBase = "QUEUEY_INGRESS_BASE";
}
