using System.Net;
using System.Text.Json;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// En falsk Queuey med autorisasjonsserveren fra kontrakten (RFC 8414, 8628, 6749 og 7009) og resten av API-et. Endepunktene
/// ligger med vilje på andre stier enn dem backend velger (/connect/…), så en test beviser at CLI-en leser metadataen.
/// Refresh-tokenene roterer, og et brukt refresh-token gir invalid_grant, som hos OpenIddict etter slakken.
/// </summary>
internal sealed class FakeAuthServer
{
    public const string Api = "https://api.test";

    /// <summary>Svarene på device-koden, i rekkefølge. Det siste gjentas.</summary>
    public Queue<string> PollAnswers { get; } = new();

    public string License { get; set; } = "lic_new";
    public string? IngressBase { get; set; } = "https://ingress.test";
    public string? User { get; set; } = "ada@example.com";
    public int Interval { get; set; } = 5;

    /// <summary>Workspacene GET /tenants svarer med.</summary>
    public List<object> Workspaces { get; } = new();

    /// <summary>Utstederen og token-endepunktet metadataen oppgir; som standard API-ets egen opprinnelse.</summary>
    /// <remarks>Null: vertens egen opprinnelse, den forespørselen kom til, så en test kan logge inn på en annen vert.</remarks>
    public string? Issuer { get; set; }
    public string? TokenEndpoint { get; set; }

    /// <summary>Scopet serveren gir, i stedet for det som ble bedt om, når satt.</summary>
    public string? GrantedScope { get; set; }

    /// <summary>Metadata-svaret, eller null for en Queuey uten innlogging (404).</summary>
    public bool OffersLogin { get; set; } = true;

    /// <summary>The verification_uri /connect/device answers with; the console's origin is taken from it.</summary>
    public string VerificationUri { get; set; } = "https://app.test/connect";

    /// <summary>Hvert gyldige refresh-token, og access-tokenet som hører til det.</summary>
    private readonly HashSet<string> _refreshTokens = new();
    private readonly HashSet<string> _accessTokens = new();
    private int _issued;
    private string _scope = "operate";

    public List<(string Path, Dictionary<string, string> Form)> Forms { get; } = new();

    public RecordingHandler Handler { get; }

    public FakeAuthServer(Func<RecordedRequest, HttpResponseMessage>? api = null)
    {
        Handler = new RecordingHandler(req => Respond(req, api));
    }

    /// <summary>Et gyldig par, som om en innlogging alt var gjort.</summary>
    public (string Access, string Refresh) Issue()
    {
        _issued++;
        string access = $"at{_issued}-opaque", refresh = $"rt{_issued}-opaque";
        _accessTokens.Add(access);
        _refreshTokens.Add(refresh);
        return (access, refresh);
    }

    public void RevokeAll()
    {
        _accessTokens.Clear();
        _refreshTokens.Clear();
    }

    public IEnumerable<Dictionary<string, string>> FormsTo(string path) => Forms.Where(f => f.Path == path).Select(f => f.Form);

    private HttpResponseMessage Respond(RecordedRequest req, Func<RecordedRequest, HttpResponseMessage>? api)
    {
        if (req.Method == HttpMethod.Post && req.Path.StartsWith("/connect/", StringComparison.Ordinal))
            Forms.Add((req.Path, Form(req.Body)));

        switch (req.Key)
        {
            case "GET /.well-known/oauth-authorization-server":
                string origin = req.Uri.GetLeftPart(UriPartial.Authority);
                return OffersLogin
                    ? RecordingHandler.Json(HttpStatusCode.OK, new Dictionary<string, object>
                    {
                        ["issuer"] = Issuer ?? origin + "/",
                        ["device_authorization_endpoint"] = origin + "/connect/device",
                        ["token_endpoint"] = TokenEndpoint ?? origin + "/connect/token",
                        ["revocation_endpoint"] = origin + "/connect/revoke",
                        ["grant_types_supported"] = new[] { "urn:ietf:params:oauth:grant-type:device_code", "refresh_token" },
                    })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);

            case "POST /connect/device":
                _scope = Form(req.Body)["scope"];
                return RecordingHandler.Json(HttpStatusCode.OK, new Dictionary<string, object>
                {
                    ["device_code"] = $"dc{Forms.Count}-secret",
                    ["user_code"] = "WDJB-MJHT",
                    ["verification_uri"] = VerificationUri,
                    ["verification_uri_complete"] = "https://app.test/connect?code=WDJB-MJHT",
                    ["expires_in"] = 600,
                    ["interval"] = Interval,
                });

            case "POST /connect/token":
                return Token(Form(req.Body));

            case "POST /connect/revoke":
                string token = Form(req.Body)["token"];
                _refreshTokens.Remove(token);
                _accessTokens.Remove(token);
                return new HttpResponseMessage(HttpStatusCode.OK);

            case "GET /tenants":
                return Authorized(req) ? RecordingHandler.Json(HttpStatusCode.OK, Workspaces) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        return api?.Invoke(req) ?? throw new InvalidOperationException($"The fake Queuey does not answer {req.Key}.");
    }

    private bool Authorized(RecordedRequest req)
    {
        int index = Handler.Requests.LastIndexOf(req);
        return index >= 0 && Handler.Headers[index].TryGetValue("Authorization", out string? value)
               && value.StartsWith("Bearer ", StringComparison.Ordinal) && _accessTokens.Contains(value["Bearer ".Length..]);
    }

    private HttpResponseMessage Token(Dictionary<string, string> form)
    {
        switch (form["grant_type"])
        {
            case "urn:ietf:params:oauth:grant-type:device_code":
                string answer = PollAnswers.Count > 1 ? PollAnswers.Dequeue() : PollAnswers.Count == 1 ? PollAnswers.Peek() : "authorization_pending";
                return answer == "approve" ? Tokens() : OAuthError(answer);

            case "refresh_token":
                // Rotasjon: det brukte refresh-tokenet virker aldri igjen, og gjenbruk trekker tilbake hele tilkoblingen.
                if (!_refreshTokens.Remove(form["refresh_token"]))
                {
                    RevokeAll();
                    return OAuthError("invalid_grant");
                }
                return Tokens();

            default:
                return OAuthError("unsupported_grant_type");
        }
    }

    private HttpResponseMessage Tokens()
    {
        (string access, string refresh) = Issue();
        var body = new Dictionary<string, object?>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = 3600,
            ["refresh_token"] = refresh,
            // Som OpenIddict: scopet som ble gitt, pluss et eget for refresh-tokenet.
            ["scope"] = $"{GrantedScope ?? _scope} offline_access",
            ["license"] = License,
            ["ingress_base"] = IngressBase,
            ["user"] = User,
        };
        return RecordingHandler.Json(HttpStatusCode.OK, body);
    }

    private static HttpResponseMessage OAuthError(string error)
        => RecordingHandler.Json(HttpStatusCode.BadRequest, new Dictionary<string, string> { ["error"] = error });

    internal static Dictionary<string, string> Form(string? body)
        => (body ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

    public static object Workspace(string id, string name, string? environment, bool archived = false)
        => new Dictionary<string, object?>
        {
            ["publicId"] = id,
            ["displayName"] = name,
            ["status"] = "Active",
            ["kind"] = "Standard",
            ["queues"] = Array.Empty<object>(),
            ["archivedAtUtc"] = archived ? "2026-10-01T00:00:00Z" : null,
            ["environment"] = environment,
        };
}
