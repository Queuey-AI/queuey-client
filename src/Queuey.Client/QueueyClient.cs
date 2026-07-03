using System;
using System.Net.Http;
using System.Reflection;

namespace Queuey.Client;

/// <summary>
/// The entry point to the Queuey SDK. Exposes the surface areas as sub-clients (currently
/// <see cref="Ingress"/>). Construct it with a <see cref="QueueyOptions"/>, optionally supplying your
/// own <see cref="HttpClient"/> (recommended via <c>AddQueueyClient</c> / <c>IHttpClientFactory</c>).
/// </summary>
public sealed class QueueyClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>Publishing operations against the ingress host.</summary>
    public IQueueyIngress Ingress { get; }

    /// <summary>Creates a client that owns and manages its own <see cref="HttpClient"/>.</summary>
    public QueueyClient(QueueyOptions options)
        : this(options ?? throw new ArgumentNullException(nameof(options)),
               CreateDefaultHttpClient(options),
               ownsHttpClient: true)
    {
    }

    /// <summary>Creates a client that uses a caller-provided <see cref="HttpClient"/> (not disposed by the SDK).</summary>
    public QueueyClient(QueueyOptions options, HttpClient httpClient)
        : this(options ?? throw new ArgumentNullException(nameof(options)),
               httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
               ownsHttpClient: false)
    {
    }

    private QueueyClient(QueueyOptions options, HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;

        IQueueyAuthenticator authenticator = BuildIngressAuthenticator(options);
        var connection = new QueueyHttpConnection(httpClient);
        Ingress = new QueueyIngressClient(
            connection,
            authenticator,
            options.ResolveIngressBaseAddress(),
            options.TenantPublicId,
            options.Source);
    }

    /// <summary>Selects the ingress authenticator from the options (API key preferred, else HMAC).</summary>
    internal static IQueueyAuthenticator BuildIngressAuthenticator(QueueyOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            return new ApiKeyAuthenticator(options.ApiKey!);

        if (!string.IsNullOrWhiteSpace(options.SigningKeyId) && !string.IsNullOrWhiteSpace(options.SigningSecret))
            return new HmacRequestSigner(options.SigningKeyId!, options.SigningSecret!);

        throw new QueueyConfigurationException(
            "No ingress credential configured. Set QueueyOptions.ApiKey, or both SigningKeyId and SigningSecret.");
    }

    private static HttpClient CreateDefaultHttpClient(QueueyOptions options)
    {
        var client = new HttpClient { Timeout = options.Timeout };
        try { client.DefaultRequestHeaders.UserAgent.ParseAdd(QueueyUserAgent.Value); }
        catch { /* a malformed UA must never break client creation */ }
        return client;
    }

    /// <summary>Disposes the owned <see cref="HttpClient"/> (no-op when one was supplied by the caller).</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

/// <summary>SDK-wide constants.</summary>
internal static class QueueyClientDefaults
{
    /// <summary>Named <see cref="HttpClient"/> registered by <c>AddQueueyClient</c>.</summary>
    public const string HttpClientName = "Queuey.Client";
}

/// <summary>Builds the SDK User-Agent string from the assembly version.</summary>
internal static class QueueyUserAgent
{
    public static readonly string Value = Build();

    private static string Build()
    {
        Assembly asm = typeof(QueueyClient).Assembly;
        string version =
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";

        int plus = version.IndexOf('+');
        if (plus >= 0)
            version = version.Substring(0, plus);

        return "Queuey.Client/" + version;
    }
}
