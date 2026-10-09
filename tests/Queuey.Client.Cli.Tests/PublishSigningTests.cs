using System.Net;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// `queuey publish` med signeringsnøkkelen `keys mint --write .env` skrev (2026-10-09): innloggingen kan ikke publisere, så
/// publish signerer med QUEUEY_SIGNING_KEY_ID og QUEUEY_SIGNING_SECRET fra miljøet eller ./.env. Bare de to leses fra .env,
/// og en API-nøkkel som er satt, vinner.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class PublishSigningTests : IDisposable
{
    private const string Secret = "s3cr3t-From-Dot-Env";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-publish-signing-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _before = Directory.GetCurrentDirectory();

    public PublishSigningTests()
    {
        _home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(_home);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        LoginStore.Write(Path.Combine(_home, "credentials.json"), new CredentialsFile
        {
            Logins =
            {
                new StoredLogin
                {
                    ApiBase = "https://api.test", License = "lic_1", Scope = "operate", AccessToken = "at-opaque",
                    AccessTokenExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30), RefreshToken = "rt-opaque",
                },
            },
        });
        Directory.SetCurrentDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_before);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static RecordingHandler Server() => new(req => req switch
    {
        { Method.Method: "GET", Path: "/tenants/ten_abc/queues" } => FlowAnswers.Queues(),
        { Method.Method: "GET", Path: "/queues/que_orders/config" } => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            ingress = new { authMode = "SignedRequest", signedRequest = new { template = "queuey" } },
        }),
        { Method.Method: "POST", Path: "/events/ten_abc/orders" } => RecordingHandler.Json(HttpStatusCode.Accepted, new
        {
            queuePublicId = "que_orders", eventId = "evt_7", receivedAtUtc = "2026-10-09T10:00:00Z", mode = "Deliver", replayed = false,
        }),
        _ => throw new InvalidOperationException(req.Key),
    });

    private Task<CliRun> Publish(RecordingHandler api, Dictionary<string, string>? env = null, params string[] extra)
    {
        var environment = new Dictionary<string, string> { [UserProfiles.PathVariable] = Path.Combine(_home, "config.json") };
        foreach ((string name, string value) in env ?? new())
            environment[name] = value;
        string[] args = new[]
        {
            "publish", "orders", "--data", "{}", "--tenant", "ten_abc", "--api-base", "https://api.test", "--ingress-base",
            "https://ingress.test", "--config", Path.Combine(_dir, "no-queuey.json"),
        }.Concat(extra).ToArray();
        return CliHarness.RunAsync(() => CliEntry.RunAsync(args), api, environment);
    }

    private static Dictionary<string, string> Sent(RecordingHandler api)
        => api.Headers[api.Requests.FindIndex(r => r.Key == "POST /events/ten_abc/orders")];

    [Fact]
    public async Task With_a_login_publish_signs_with_the_key_in_dot_env_and_reads_nothing_else_from_it()
    {
        File.WriteAllText(Path.Combine(_dir, ".env"),
            $"QUEUEY_API_KEY=qak_never.read\nexport QUEUEY_SIGNING_KEY_ID=hk_dotenv\nQUEUEY_SIGNING_SECRET='{Secret}'\nQUEUEY_TENANT=ten_other\n");
        RecordingHandler api = Server();

        CliRun run = await Publish(api);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Dictionary<string, string> sent = Sent(api);
        Assert.Equal("hk_dotenv", sent["X-Queuey-Key-Id"]);
        Assert.True(sent.ContainsKey("X-Queuey-Signature"));
        Assert.False(sent.ContainsKey("X-Api-Key"));
        Assert.False(sent.ContainsKey("Authorization")); // tokenet går aldri til ingressen
        Assert.Contains("Signing with key hk_dotenv from .env.", run.Stderr);
        Assert.DoesNotContain(Secret, run.Stdout + run.Stderr);
        Assert.All(api.Requests.Where(r => r.Uri.Host == "api.test"), r => Assert.Equal("Bearer at-opaque", api.Headers[api.Requests.IndexOf(r)]["Authorization"]));
    }

    [Fact]
    public async Task The_environment_wins_over_dot_env_and_half_a_pair_is_refused()
    {
        File.WriteAllText(Path.Combine(_dir, ".env"), $"QUEUEY_SIGNING_KEY_ID=hk_dotenv\nQUEUEY_SIGNING_SECRET={Secret}\n");
        RecordingHandler api = Server();

        CliRun run = await Publish(api, new() { ["QUEUEY_SIGNING_KEY_ID"] = "hk_env", ["QUEUEY_SIGNING_SECRET"] = "env-secret" });

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("hk_env", Sent(api)["X-Queuey-Key-Id"]);
        Assert.Contains("from the environment", run.Stderr);

        RecordingHandler none = Server();
        CliRun half = await Publish(none, new() { ["QUEUEY_SIGNING_KEY_ID"] = "hk_env" });
        Assert.Equal(ExitCodes.Configuration, half.Exit);
        Assert.Contains("QUEUEY_SIGNING_KEY_ID is set without QUEUEY_SIGNING_SECRET", half.Stderr);
        Assert.Empty(none.Requests);
    }

    [Fact]
    public async Task An_api_key_that_is_set_wins_and_dot_env_is_not_read()
    {
        File.WriteAllText(Path.Combine(_dir, ".env"), $"QUEUEY_SIGNING_KEY_ID=hk_dotenv\nQUEUEY_SIGNING_SECRET={Secret}\n");
        RecordingHandler api = new(req => req switch
        {
            { Method.Method: "GET", Path: "/tenants/ten_abc/queues" } => FlowAnswers.Queues(),
            { Method.Method: "GET", Path: "/queues/que_orders/config" } => RecordingHandler.Json(HttpStatusCode.OK, new { ingress = new { authMode = "ApiKey" } }),
            { Method.Method: "POST", Path: "/events/ten_abc/orders" } => RecordingHandler.Json(HttpStatusCode.Accepted, new
            {
                queuePublicId = "que_orders", eventId = "evt_7", receivedAtUtc = "2026-10-09T10:00:00Z", mode = "Deliver", replayed = false,
            }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await Publish(api, null, "--api-key", "qak_kid.secret", "--license", "lic_1");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("qak_kid.secret", Sent(api)["X-Api-Key"]);
        Assert.False(Sent(api).ContainsKey("X-Queuey-Key-Id"));
        Assert.DoesNotContain("Signing with", run.Stderr);
    }

    [Fact]
    public async Task Without_a_key_or_a_signing_key_a_login_cannot_publish_and_says_to_mint_one()
    {
        RecordingHandler api = Server();

        CliRun run = await Publish(api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("this publish has no signing key", run.Stderr);
        Assert.Contains("queuey keys mint --queue orders --write .env", run.Stderr);
        Assert.DoesNotContain("POST /events/ten_abc/orders", api.Requests.Select(r => r.Key));
    }
}
