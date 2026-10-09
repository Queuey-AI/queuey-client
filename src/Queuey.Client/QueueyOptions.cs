using System;
using System.Collections.Generic;
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
/// An app publishes with a <b>signing key</b> for its queue (<see cref="SigningKeyId"/> + <see cref="SigningSecret"/>, from
/// <c>queuey keys mint --write .env</c>), which reaches nothing else. A license-wide API key belongs to a person and is
/// for managing Queuey (sync, apply, the control plane), not for an app that only publishes. The optional
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
    /// API key in the form <c>qak_&lt;keyId&gt;.&lt;secret&gt;</c>, sent as the <c>X-Api-Key</c> header. A license-wide key
    /// belongs to a person and manages Queuey; an app that publishes signs with <see cref="SigningKeyId"/> instead.
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
    /// as the signing key <c>queuey keys mint --write .env</c> writes. A value set in code wins. When the signing key and its
    /// secret are both set, <c>QUEUEY_API_KEY</c> is not read: the producer signs with the key that reaches only its queue,
    /// not a license-wide one.
    /// In Development (<c>DOTNET_ENVIRONMENT</c> or <c>ASPNETCORE_ENVIRONMENT</c>), when the environment does not hold both,
    /// the signing key and its secret are read as a pair from <c>.env</c> in the working folder, and nothing else is: the
    /// hosts, the tenant and the API key never come from it. Only from a regular file of the user's own (on Windows: under the
    /// user's profile folder) that git does not track. Elsewhere <c>.env</c> is never read: production takes its secrets
    /// from the platform's environment. Returns these options.
    /// </summary>
    /// <param name="read">Reads a variable; <see cref="System.Environment.GetEnvironmentVariable(string)"/> when null.</param>
    public QueueyOptions UseEnvironmentVariables(Func<string, string?>? read = null)
        => UseEnvironmentVariables(read, System.IO.Directory.GetCurrentDirectory());

    /// <summary><see cref="UseEnvironmentVariables(Func{string, string?}?)"/> with the folder whose <c>.env</c> is read given.</summary>
    internal QueueyOptions UseEnvironmentVariables(Func<string, string?>? read, string? dotEnvFolder)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        string? Read(string name) => read(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        // Signeringsparet tas fra ett sted: miljøet når det har begge, ellers .env (bare i Development, DotEnvFile). Ingen andre
        // navn leses fra .env: en fil i arbeidsmappa skal ikke kunne velge vertene nøkkelen sendes til (security-review av #68, R1).
        (string? keyId, string? secret) = (Read(QueueyEnvironmentVariables.SigningKeyId), Read(QueueyEnvironmentVariables.SigningSecret));
        if ((keyId is null || secret is null) && dotEnvFolder is not null && DotEnvFile.IsDevelopment(read)
            && DotEnvFile.ReadSigningPair(dotEnvFolder) is { } fromFile)
            (keyId, secret) = fromFile;
        SigningKeyId ??= keyId;
        SigningSecret ??= secret;
        // Minste privilegium (security-review av #67, KAN G): med et signeringspar leses ikke QUEUEY_API_KEY, for en satt nøkkel
        // ville vunnet over signeringen ved publisering. En nøkkel satt i koden vinner fortsatt.
        if (string.IsNullOrWhiteSpace(SigningKeyId) || string.IsNullOrWhiteSpace(SigningSecret))
            ApiKey ??= Read(QueueyEnvironmentVariables.ApiKey);
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

/// <summary>The environment variables <see cref="QueueyOptions.UseEnvironmentVariables(Func{string, string?}?)"/> reads, and the <c>queuey</c> CLI uses.</summary>
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
