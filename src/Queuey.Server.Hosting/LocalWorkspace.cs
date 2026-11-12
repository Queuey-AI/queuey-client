using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Server.Hosting;

/// <summary>The API of a local installation, as the developer, with the account's license header once it is known.</summary>
internal sealed class LocalApi(HttpClient http, string baseUrl, string token)
{
    public string? License { get; set; }

    public async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct) => await SendAsync(HttpMethod.Get, path, null, ct);

    public async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (License is not null)
            request.Headers.Add("X-License-PublicId", License);
        if (body is not null)
            request.Content = Json(body);
        using var response = await http.SendAsync(request, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new QueueyException($"{method} {path} answered {(int)response.StatusCode}: {LocalDeveloper.Short(text)}");
        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
    }

    public static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}

/// <summary>
/// The developer's account and workspace in a local installation, made the first time something needs them: the test
/// delivery, or the key <see cref="QueueyServer.EnsureRunningAsync"/> gives an app.
/// </summary>
internal static class LocalWorkspace
{
    /// <summary>
    /// Signs in as the developer and returns the API with the account's license, and the workspace:
    /// <paramref name="preferred"/> while it exists, so the app keeps its workspace when the developer makes another.
    /// </summary>
    public static async Task<(LocalApi Api, string Tenant)> OpenAsync(
        LocalPorts ports, DeveloperRecord developer, HttpClient http, string? preferred, CancellationToken ct)
    {
        string token = await LocalDeveloper.TokenAsync(ports, developer, http, ct);
        var api = new LocalApi(http, ports.ApiUrl, token);
        api.License = await AccountAsync(api, developer, ct);
        return (api, await WorkspaceAsync(api, preferred, ct));
    }

    /// <summary>The queue named <paramref name="name"/> in the workspace, unless archived, or null.</summary>
    public static async Task<string?> QueueAsync(LocalApi api, string tenant, string name, CancellationToken ct)
    {
        using var tenants = await api.GetJsonAsync("/tenants", ct);
        if (tenants.RootElement.ValueKind != JsonValueKind.Array)
            return null;
        JsonElement workspace = tenants.RootElement.EnumerateArray().FirstOrDefault(t => Str(t, "publicId") == tenant);
        return workspace.ValueKind == JsonValueKind.Object && workspace.TryGetProperty("queues", out JsonElement queues)
               && queues.ValueKind == JsonValueKind.Array
               && queues.EnumerateArray().FirstOrDefault(q => Str(q, "name") == name && !Archived(q)) is { ValueKind: JsonValueKind.Object } found
            ? Str(found, "publicId")
            : null;
    }

    // Kontoen: den første personen har ingen før testleveringen eller konsollet lager den. bootstrap-admin gir plattformens
    // lisens med Viewer (accessSource «platform»), som bare leser; den teller ikke.
    private static async Task<string> AccountAsync(LocalApi api, DeveloperRecord developer, CancellationToken ct)
    {
        using var me = await api.GetJsonAsync("/auth/me", ct);
        if (me.RootElement.TryGetProperty("licenses", out JsonElement licenses) && licenses.ValueKind == JsonValueKind.Array
            && licenses.EnumerateArray().FirstOrDefault(l => Str(l, "accessSource") == "direct" && Str(l, "role") is "Owner" or "Admin") is
            { ValueKind: JsonValueKind.Object } own)
            return Str(own, "licensePublicId");

        using var account = await api.SendAsync(HttpMethod.Post, "/onboarding", new
        {
            organization = new { organizationName = "Local", countryCode = "NO" },
            contact = new { email = developer.Email },
        }, ct);
        return account.RootElement.GetProperty("licenseId").GetString()!;
    }

    // Det foretrukne arbeidsområdet mens det finnes; ellers «Local», så det første etter id. /tenants har ingen fast rekkefølge
    // (security review av 4.9: en oppdatering i konsollet kunne gjort et annet arbeidsområde til det første, og appen fikk en
    // ny nøkkel dit i det stille). Ellers et nytt, «Local».
    private static async Task<string> WorkspaceAsync(LocalApi api, string? preferred, CancellationToken ct)
    {
        using var tenants = await api.GetJsonAsync("/tenants", ct);
        if (tenants.RootElement.ValueKind == JsonValueKind.Array && tenants.RootElement.GetArrayLength() > 0)
        {
            var all = tenants.RootElement.EnumerateArray().ToList();
            if (preferred is not null && all.Any(t => Str(t, "publicId") == preferred))
                return preferred;
            return Str(all.OrderBy(t => Str(t, "displayName") == "Local" ? 0 : 1).ThenBy(t => Str(t, "publicId"), StringComparer.Ordinal).First(), "publicId");
        }

        using var created = await api.SendAsync(HttpMethod.Post, "/tenants", new { displayName = "Local", createDefaultQueue = false }, ct);
        return Str(created.RootElement, "publicId");
    }

    private static bool Archived(JsonElement queue)
        => queue.TryGetProperty("archivedAtUtc", out JsonElement at) && at.ValueKind != JsonValueKind.Null;

    internal static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
