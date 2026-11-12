using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Cli.Server;

/// <summary>
/// The person a local installation is made for, kept in <c>developer.json</c> in the folder (readable by the user alone):
/// who they are in the bundled Keycloak, and the password they sign in to the console with.
/// </summary>
internal sealed record DeveloperRecord(string Email, string Password, string Subject);

/// <summary>
/// The first person of an installation <c>queuey server init</c> made. On the first start it creates them in the bundled
/// Keycloak with a generated password, gives them the installation's owner role with <c>bootstrap-admin</c>, and adds a
/// Keycloak client for this CLI that takes a password, so <c>start</c> can sign in as them and run the test delivery.
/// </summary>
// Valgt av Kenneth 2026-11-12 (selfhost 4.8): testleveringen trenger et token, og på en ny installasjon finnes ingen person.
// Klienten med passord-grant opprettes bare her, i en installasjon for én maskin; realmen pakken leverer, har den ikke.
internal static class LocalDeveloper
{
    public const string DefaultEmail = "developer@queuey.localhost";
    public const string CliClientId = "queuey-server-cli";
    private const string Realm = "queuey";

    /// <summary>The developer in the folder, or null when the installation has none (not made by init, or not started yet).</summary>
    public static DeveloperRecord? Read(ServerFolder folder)
    {
        string path = folder.File(ServerFolder.DeveloperFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<DeveloperRecord>(File.ReadAllText(path), CliHost.JsonOut) : null;
    }

    /// <summary>Creates the developer when the folder has none yet, and returns them.</summary>
    public static async Task<DeveloperRecord> EnsureAsync(ServerFolder folder, string email, HttpClient http, CancellationToken ct)
    {
        if (Read(folder) is { } existing)
            return existing;

        LocalPorts ports = folder.Ports;
        string admin = await AdminTokenAsync(folder, ports, http, ct);
        string password = ServerFolder.RandomHex(16);
        string subject = await CreatePersonAsync(ports, admin, email, password, http, ct);
        await EnsureCliClientAsync(ports, admin, http, ct);

        DockerResult granted = await ServerDocker.ComposeAsync(folder.Path, ct,
            "run", "--rm", "migrate", "bootstrap-admin", "--provider", "oidc", "--sub", subject, "--email", email, "--role", "Admin.Owner", "--yes");
        if (granted.ExitCode != 0)
            throw new QueueyException($"bootstrap-admin did not give {email} access: {ServerDocker.Detail(granted)}");

        var developer = new DeveloperRecord(email, password, subject);
        ServerFolder.WritePrivate(folder.File(ServerFolder.DeveloperFile), JsonSerializer.Serialize(developer, CliHost.JsonOut) + "\n");
        return developer;
    }

    /// <summary>An access token for the API, for the developer, from the CLI's own client in Keycloak.</summary>
    public static async Task<string> TokenAsync(LocalPorts ports, DeveloperRecord developer, HttpClient http, CancellationToken ct)
    {
        using var response = await http.PostAsync($"{ports.IdpUrl}/realms/{Realm}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = CliClientId,
            ["username"] = developer.Email,
            ["password"] = developer.Password,
            ["scope"] = "openid email profile",
        }), ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new QueueyException($"Keycloak did not sign {developer.Email} in ({(int)response.StatusCode}). Its answer: {Short(body)}");
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    // Passordet til Keycloaks første administrator står i secrets-volumet, der setup la det; README-en leser det på samme måte.
    // Det skrives aldri ut, og brukes bare til de to kallene under.
    private static async Task<string> AdminTokenAsync(ServerFolder folder, LocalPorts ports, HttpClient http, CancellationToken ct)
    {
        DockerResult read = await ServerDocker.ComposeAsync(folder.Path, ct,
            "run", "--rm", "--no-deps", "--entrypoint", "cat", "setup", "/var/lib/queuey/secrets/keycloak/admin-password");
        if (read.ExitCode != 0)
            throw new QueueyException($"Could not read Keycloak's admin password from the secrets volume: {ServerDocker.Detail(read)}");

        using var response = await http.PostAsync($"{ports.IdpUrl}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = "admin",
            ["password"] = read.Stdout.Trim(),
        }), ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new QueueyException(
                $"Keycloak's temporary admin could not sign in ({(int)response.StatusCode}), so the developer was not created. " +
                "If you deleted that admin, create the person yourself and run bootstrap-admin, as the package's README says.");
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<string> CreatePersonAsync(LocalPorts ports, string admin, string email, string password, HttpClient http, CancellationToken ct)
    {
        string users = $"{ports.IdpUrl}/admin/realms/{Realm}/users";
        using var response = await Send(http, HttpMethod.Post, users, admin, new
        {
            username = email,
            email,
            emailVerified = true,
            enabled = true,
            firstName = "Local",
            lastName = "Developer",
            credentials = new[] { new { type = "password", value = password, temporary = false } },
        }, ct);

        if (response.StatusCode == HttpStatusCode.Created && response.Headers.Location is { } location)
            return location.Segments[^1];

        // Personen finnes fra før, når developer.json ble slettet: de får et nytt passord, og samme subject.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            using var found = await Send(http, HttpMethod.Get, $"{users}?exact=true&email={Uri.EscapeDataString(email)}", admin, null, ct);
            using var list = JsonDocument.Parse(await found.Content.ReadAsStringAsync(ct));
            if (list.RootElement.ValueKind == JsonValueKind.Array && list.RootElement.GetArrayLength() == 1)
            {
                string id = list.RootElement[0].GetProperty("id").GetString()!;
                using var reset = await Send(http, HttpMethod.Put, $"{users}/{id}/reset-password", admin,
                    new { type = "password", value = password, temporary = false }, ct);
                if (reset.IsSuccessStatusCode)
                    return id;
            }
        }

        throw new QueueyException($"Keycloak did not create {email} ({(int)response.StatusCode}): {Short(await response.Content.ReadAsStringAsync(ct))}");
    }

    // Klienten CLI-en logger inn med: offentlig, bare passord-grant, og med API-ets audience i tokenet, som konsollets klient.
    private static async Task EnsureCliClientAsync(LocalPorts ports, string admin, HttpClient http, CancellationToken ct)
    {
        using var response = await Send(http, HttpMethod.Post, $"{ports.IdpUrl}/admin/realms/{Realm}/clients", admin, new
        {
            clientId = CliClientId,
            name = "queuey server (this machine only)",
            enabled = true,
            protocol = "openid-connect",
            publicClient = true,
            standardFlowEnabled = false,
            implicitFlowEnabled = false,
            directAccessGrantsEnabled = true,
            serviceAccountsEnabled = false,
            protocolMappers = new[]
            {
                new
                {
                    name = "Queuey API audience",
                    protocol = "openid-connect",
                    protocolMapper = "oidc-audience-mapper",
                    consentRequired = false,
                    config = new Dictionary<string, string>
                    {
                        ["included.custom.audience"] = "queuey-api",
                        ["access.token.claim"] = "true",
                        ["id.token.claim"] = "false",
                        ["introspection.token.claim"] = "true",
                    },
                },
            },
        }, ct);
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
            throw new QueueyException($"Keycloak did not create the client {CliClientId} ({(int)response.StatusCode}): {Short(await response.Content.ReadAsStringAsync(ct))}");
    }

    private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url, string token, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return http.SendAsync(request, ct);
    }

    internal static string Short(string text) => TerminalText.Line(text.Length > 300 ? text.Substring(0, 300) + "…" : text);
}
