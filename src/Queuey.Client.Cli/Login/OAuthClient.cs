using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// Standard OAuth, ingenting eget (Kenneth valgte OpenIddict 2026-10-09): metadata etter RFC 8414, device-flyten etter RFC
// 8628, refresh etter RFC 6749 og tilbakekall etter RFC 7009. Endepunktene leses fra metadataen, aldri fra faste stier.
// Tokenene er opake: CLI-en sender dem videre og antar ingenting om formen, heller ikke et prefiks.

/// <summary>Where Queuey's authorization server takes each request, from its metadata.</summary>
internal sealed record OAuthEndpoints(Uri DeviceAuthorization, Uri Token, Uri? Revocation);

/// <summary>A device code and what a person needs to approve it (RFC 8628 §3.2).</summary>
internal sealed record DeviceAuthorization(
    string DeviceCode, string UserCode, string VerificationUri, string? VerificationUriComplete, int ExpiresIn, int Interval);

/// <summary>A token answer: the tokens, or the OAuth error code and its description.</summary>
internal sealed record TokenAnswer(
    string? AccessToken, int? ExpiresIn, string? RefreshToken, string? Scope, string? License, string? IngressBase, string? User,
    string? Error, string? ErrorDescription)
{
    public bool Succeeded => Error is null && !string.IsNullOrWhiteSpace(AccessToken);
}

/// <summary>Talks OAuth to one Queuey API host as the public client <c>queuey-cli</c>, with no secret.</summary>
internal sealed class OAuthClient : IDisposable
{
    public const string ClientId = "queuey-cli";

    private readonly HttpClient _http;
    private readonly Uri _apiBase;

    public OAuthClient(Uri apiBase)
    {
        _apiBase = apiBase;
        _http = CliHost.TestHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"queuey-cli/{CliVersion.Current}");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// The endpoints from <c>/.well-known/oauth-authorization-server</c> on the API host. A host without it does not offer
    /// login, which is a configuration error that points at an API key instead.
    /// </summary>
    public async Task<OAuthEndpoints> DiscoverAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri(_apiBase, "/.well-known/oauth-authorization-server");
        using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new QueueyConfigurationException($"{Host} does not offer login: it has no {uri.AbsolutePath}.")
            {
                SuggestedAction = "Use an API key from the Queuey console instead (--api-key, QUEUEY_API_KEY or apiKey in the profile).",
            };
        if (!response.IsSuccessStatusCode)
            throw new QueueyException($"{Host} answered {(int)response.StatusCode} for its login metadata ({uri.AbsolutePath}).",
                (int)response.StatusCode, "login_metadata_unavailable");

        JsonElement root = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new OAuthEndpoints(
            Endpoint(root, "device_authorization_endpoint") ?? throw NotOffered("device_authorization_endpoint"),
            Endpoint(root, "token_endpoint") ?? throw NotOffered("token_endpoint"),
            Endpoint(root, "revocation_endpoint"));
    }

    /// <summary>Asks for a device code for <paramref name="scope"/> (RFC 8628 §3.1).</summary>
    public async Task<DeviceAuthorization> StartDeviceAsync(OAuthEndpoints endpoints, string scope, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostFormAsync(endpoints.DeviceAuthorization, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = scope,
        }, cancellationToken).ConfigureAwait(false);

        JsonElement root = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Refused("a login code", root, response);

        string? deviceCode = Text(root, "device_code"), userCode = Text(root, "user_code"), verificationUri = Text(root, "verification_uri");
        if (deviceCode is null || userCode is null || verificationUri is null)
            throw new QueueyException($"{Host} answered the login request without device_code, user_code or verification_uri.",
                errorCode: "login_answer_incomplete");

        return new DeviceAuthorization(deviceCode, userCode, verificationUri, Text(root, "verification_uri_complete"),
            Number(root, "expires_in") ?? 600, Math.Max(1, Number(root, "interval") ?? 5));
    }

    /// <summary>Asks once whether the device code is approved (RFC 8628 §3.4).</summary>
    public Task<TokenAnswer> PollAsync(OAuthEndpoints endpoints, string deviceCode, CancellationToken cancellationToken)
        => TokenAsync(endpoints, new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
            ["client_id"] = ClientId,
        }, cancellationToken);

    /// <summary>Trades a refresh token for new tokens (RFC 6749 §6). The answer carries a new refresh token to keep.</summary>
    public Task<TokenAnswer> RefreshAsync(OAuthEndpoints endpoints, string refreshToken, CancellationToken cancellationToken)
        => TokenAsync(endpoints, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        }, cancellationToken);

    /// <summary>Revokes a token (RFC 7009). True when Queuey confirmed it, which it does for a token it no longer knows too.</summary>
    public async Task<bool> RevokeAsync(OAuthEndpoints endpoints, string token, string hint, CancellationToken cancellationToken)
    {
        if (endpoints.Revocation is null)
            return false;

        using HttpResponseMessage response = await PostFormAsync(endpoints.Revocation, new Dictionary<string, string>
        {
            ["token"] = token,
            ["token_type_hint"] = hint,
            ["client_id"] = ClientId,
        }, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// <c>GET /tenants</c> with the access token, as every command sends it: the workspaces the login reaches in
    /// <paramref name="license"/>. Null when Queuey answers 401, so the login is no longer valid.
    /// </summary>
    public async Task<IReadOnlyList<LoginWorkspace>?> WorkspacesAsync(string accessToken, string license, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, "tenants"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(QueueyHeaders.LicensePublicId, license);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new QueueyException($"{Host} answered {(int)response.StatusCode} when the login listed its workspaces.",
                (int)response.StatusCode, "workspaces_unavailable");

        JsonElement root = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var workspaces = new List<LoginWorkspace>();
        if (root.ValueKind != JsonValueKind.Array)
            return workspaces;
        foreach (JsonElement item in root.EnumerateArray())
        {
            if (Text(item, "publicId") is not { } id || !WorkspaceIds.IsOne(id))
                continue;
            bool archived = item.TryGetProperty("archivedAtUtc", out JsonElement at) && at.ValueKind != JsonValueKind.Null;
            workspaces.Add(new LoginWorkspace(id, Text(item, "displayName"), Text(item, "environment")?.ToLowerInvariant(), archived));
        }

        return workspaces;
    }

    private string Host => _apiBase.GetLeftPart(UriPartial.Authority);

    private async Task<TokenAnswer> TokenAsync(OAuthEndpoints endpoints, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostFormAsync(endpoints.Token, form, cancellationToken).ConfigureAwait(false);
        JsonElement root = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        // RFC 6749 §5.2: en feil er 400 (eller 401) med error. En annen status uten error er ikke et OAuth-svar.
        if (Text(root, "error") is { } error)
            return new TokenAnswer(null, null, null, null, null, null, null, error, Text(root, "error_description"));
        if (!response.IsSuccessStatusCode)
            throw new QueueyException($"{Host} answered {(int)response.StatusCode} from its token endpoint, without an OAuth error.",
                (int)response.StatusCode, "token_endpoint_failed");

        return new TokenAnswer(Text(root, "access_token"), Number(root, "expires_in"), Text(root, "refresh_token"), Text(root, "scope"),
            Text(root, "license"), Text(root, "ingress_base"), Text(root, "user"), null, null);
    }

    private async Task<HttpResponseMessage> PostFormAsync(Uri uri, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// An endpoint from the metadata: an absolute https URL, or http on this machine (a local Queuey). Anything else is
    /// refused, since the CLI sends its tokens there.
    /// </summary>
    private Uri? Endpoint(JsonElement root, string name)
    {
        if (Text(root, name) is not { } value)
            return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            return uri;

        throw new QueueyException($"{Host} gives {name} as an address the CLI does not send tokens to: it must be https, or http on this machine.",
            errorCode: "login_metadata_invalid");
    }

    private QueueyException NotOffered(string name)
        => new($"{Host} offers no {name} in its login metadata, so `queuey login` cannot use it.", errorCode: "login_unavailable");

    private QueueyException Refused(string what, JsonElement root, HttpResponseMessage response)
    {
        string? error = Text(root, "error");
        string? description = Text(root, "error_description");
        return new QueueyException(
            $"{Host} refused {what} ({(int)response.StatusCode}{(error is null ? "" : $", {error}")}){(description is null ? "." : $": {description}")}",
            (int)response.StatusCode, error ?? "login_refused");
    }

    internal static string? Text(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static int? Number(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out int number)
            ? number
            : null;
}

/// <summary>A workspace a login reaches, as <c>GET /tenants</c> lists it.</summary>
internal sealed record LoginWorkspace(string PublicId, string? DisplayName, string? Environment, bool Archived)
{
    /// <summary>The environment Queuey counts it as: its own, or prod when it has none.</summary>
    public string EffectiveEnvironment => string.IsNullOrWhiteSpace(Environment) ? "prod" : Environment!;
}
