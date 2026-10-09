using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Funnene fra Kenneths andre blindtest (2026-10-09), del A: workspacet fra deploy-fila, advise som følger skillen,
/// create-tenant med profilen, event-id-en i Edge og templateKey i forslagene.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class BlindTest2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-blind2", Guid.NewGuid().ToString("N"));
    private readonly string _before = Directory.GetCurrentDirectory();

    public BlindTest2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_before);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string DevProfileWithoutTenant = """
        { "profiles": { "dev": { "apiKey": "qak_dev.key-for-dev", "license": "lic_dev",
                                 "apiBase": "https://api.test", "ingressBase": "https://ingress.test" } } }
        """;

    private Dictionary<string, string> Env()
        => new(StringComparer.Ordinal) { [UserProfiles.PathVariable] = UserProfilesTests.WriteUserConfig(_dir, DevProfileWithoutTenant) };

    private void DeployFile(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "queuey.deploy.json"), json);
        Directory.SetCurrentDirectory(_dir);
    }

    private static RecordingHandler Minting(string tenant) => new(req => req.Key == $"POST /hmacclients/tenants/{tenant}"
        ? RecordingHandler.Json(HttpStatusCode.OK, new
        {
            clientPublicId = "hcl_1", clientName = "queuey-cli", keyId = "hsk_01WS", secret = "s3cr3t-value", scope = "workspace",
            tenantPublicId = tenant, type = "signing",
        })
        : throw new InvalidOperationException(req.Key));

    // ── #5: workspacet fra deploy-fila ──────────────────────────────────────

    [Fact]
    public async Task Keys_mint_with_a_profile_takes_the_workspace_the_deployment_file_names_for_it_and_says_so()
    {
        DeployFile("""
            { "tenant": "${QUEUEY_TENANT}", "queues": {},
              "profiles": { "dev": { "variables": { "QUEUEY_TENANT": "ten_file" } } } }
            """);
        RecordingHandler api = Minting("ten_file");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "keys", "mint", "--profile", "dev", "--write", ".env", "--json" }), api, Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("POST /hmacclients/tenants/ten_file", Assert.Single(api.Requests).Key);
        Assert.Contains("Workspace ten_file from queuey.deploy.json (profile dev).", run.Stderr);
        Assert.Equal("queuey.deploy.json (profile dev)", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("workspaceFrom").GetString());
    }

    [Fact]
    public async Task A_tenant_flag_wins_over_the_deployment_file()
    {
        DeployFile("""{ "tenant": "ten_file", "queues": {} }""");
        RecordingHandler api = Minting("ten_flag");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "keys", "mint", "--profile", "dev", "--tenant", "ten_flag", "--write", ".env", "--json" }), api, Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("POST /hmacclients/tenants/ten_flag", Assert.Single(api.Requests).Key);
        Assert.DoesNotContain("from queuey.deploy.json", run.Stderr);
    }

    [Fact]
    public async Task Credentials_generate_takes_the_workspace_from_the_deployment_file_too()
    {
        DeployFile("""{ "tenant": "ten_file", "queues": {} }""");
        var api = new RecordingHandler(req => req.Key == "POST /tenants/ten_file/credentials"
            ? RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "cred_1", name = "warehouse-delivery", type = "HmacSigning", created = true })
            : throw new InvalidOperationException(req.Key));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[]
        {
            "credentials", "generate", "warehouse-delivery", "--write", ".env", "--api-key", "qak_dev.key-for-dev", "--license", "lic_dev",
            "--api-base", "https://api.test", "--json",
        }), api, new Dictionary<string, string> { [UserProfiles.PathVariable] = Path.Combine(_dir, "none.json") });

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("POST /tenants/ten_file/credentials", Assert.Single(api.Requests).Key);
        Assert.Contains("Workspace ten_file from queuey.deploy.json.", run.Stderr);
    }

    [Fact]
    public async Task Without_any_workspace_the_refusal_names_the_deployment_file_as_a_place_for_it()
    {
        Directory.SetCurrentDirectory(_dir);
        var api = new RecordingHandler(req => throw new InvalidOperationException(req.Key));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "keys", "mint", "--profile", "dev", "--write", ".env" }), api, Env());

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("tenant in queuey.deploy.json", run.Stdout + run.Stderr);
        Assert.Empty(api.Requests);
    }

    // ── #7: advise sier det samme som skillen ───────────────────────────────

    private string Repo(params (string Path, string Content)[] files)
    {
        string repo = Path.Combine(_dir, "repo");
        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(repo, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return repo;
    }

    [Fact]
    public void A_small_web_api_gets_Client_and_is_told_it_is_the_disk_that_rules_out_Edge_and_to_mint_the_workspace_key()
    {
        // shop-demo i blindtesten: et lite .NET web-API uten tegn til hvor det kjører.
        string repo = Repo(("shop-demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>"),
            ("Program.cs", "var app = WebApplication.Create(); app.MapPost(\"/orders\", () => 1); app.Run();"));

        var advice = Queuey.Client.Cli.Advise.Recommendation.For(Queuey.Client.Cli.Advise.RepoScan.Scan(repo));

        Assert.Equal(Queuey.Client.Cli.Advise.SendPath.Client, advice.Send);
        Assert.Contains(advice.Reasons, r => r.Contains("shows disk that survives a restart", StringComparison.Ordinal)
                                             && r.Contains("Queuey.Edge fits instead", StringComparison.Ordinal));
        Assert.Contains(Queuey.Client.Cli.Advise.Recommendation.DiskQuestion, advice.Questions);
        Assert.Contains(advice.NextSteps, s => s.Contains("queuey keys mint --profile dev --write .env", StringComparison.Ordinal)
                                               && s.Contains("add --queue <queue> only to limit it to one", StringComparison.Ordinal));
        Assert.DoesNotContain(advice.NextSteps, s => s.Contains("keys mint --queue <queue> --profile dev --write .env", StringComparison.Ordinal));
        Assert.Contains(advice.NextSteps, s => s.Contains("workspace's signing key", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_api_on_a_server_with_a_systemd_unit_gets_Edge()
    {
        string repo = Repo(("shop-demo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>"),
            ("Program.cs", "var app = WebApplication.Create(); app.Run();"),
            ("deploy/shop.service", "[Unit]\nDescription=Shop\n[Service]\nExecStart=/opt/shop/shop"));

        var advice = Queuey.Client.Cli.Advise.Recommendation.For(Queuey.Client.Cli.Advise.RepoScan.Scan(repo));

        Assert.Equal(Queuey.Client.Cli.Advise.SendPath.Edge, advice.Send);
        Assert.Contains(advice.NextSteps, s => s.Contains("queuey keys mint --profile dev --write .env", StringComparison.Ordinal));
    }

    // ── #8: create-tenant med profilen ──────────────────────────────────────

    /// <summary>Profilen dev og en innlogging for en lokal Queuey, som queuey login --profile dev --api-base … lager dem.</summary>
    private Dictionary<string, string> LocalLogin()
    {
        string home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(home);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string config = UserProfilesTests.WriteUserConfig(home, """
            { "profiles": { "dev": { "license": "lic_dev", "apiBase": "http://localhost:5223", "ingressBase": "http://localhost:5084" } } }
            """);
        LoginStore.Write(Path.Combine(home, "credentials.json"), new CredentialsFile
        {
            Logins =
            {
                new StoredLogin
                {
                    ApiBase = "http://localhost:5223", License = "lic_dev", Scope = "operate", AccessToken = "at-local",
                    AccessTokenExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30), RefreshToken = "rt-local",
                },
            },
        });
        return new Dictionary<string, string>(StringComparer.Ordinal) { [UserProfiles.PathVariable] = config };
    }

    [Fact]
    public async Task Create_tenant_with_a_profile_goes_to_the_profiles_host_with_its_login()
    {
        var api = new RecordingHandler(req => req.Key == "POST /tenants"
            ? RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "ten_new", displayName = "shop", kind = "Standard" })
            : throw new InvalidOperationException(req.Key));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "create-tenant", "--name", "shop", "--profile", "dev", "--json" }), api, LocalLogin());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        RecordedRequest sent = Assert.Single(api.Requests);
        Assert.Equal("localhost", sent.Uri.Host);
        Assert.Equal(5223, sent.Uri.Port);
        Assert.Equal("Bearer at-local", api.Headers[0]["Authorization"]);
    }

    [Fact]
    public async Task Create_tenant_without_the_profile_sends_nothing_to_prod_and_names_the_local_login()
    {
        var api = new RecordingHandler(req => throw new InvalidOperationException("Nothing is sent: " + req.Key));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "create-tenant", "--name", "shop" }), api, LocalLogin());

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Contains("create-tenant needs a login or an API key for https://api.queuey.ai", run.Stderr);
        Assert.Contains("You are logged in to http://localhost:5223.", run.Stderr);
        Assert.Contains("Add the --profile you logged in with", run.Stderr);
        Assert.DoesNotContain("No ingress credential", run.Stderr);
    }

    // ── #1: templateKey i forslagene ────────────────────────────────────────

    [Fact]
    public async Task Credentials_generate_and_the_receiving_recipe_suggest_templateKey_queuey()
    {
        var api = new RecordingHandler(req => req.Key == "POST /tenants/ten_1/credentials"
            ? RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "cred_1", name = "orders-signing", type = "HmacSigning", created = true })
            : throw new InvalidOperationException(req.Key));
        string env = Path.Combine(_dir, ".env");

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env, "--json")), api);
        File.Delete(env);
        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env)), api);

        Assert.True(json.Exit == ExitCodes.Success, json.Stdout + json.Stderr);
        Assert.Equal("queuey", JsonDocument.Parse(json.Stdout).RootElement.GetProperty("deliverySigning").GetProperty("templateKey").GetString());
        Assert.Contains("\"credentialRef\": \"orders-signing\", \"templateKey\": \"queuey\"", human.Stdout);

        string repo = Repo(("Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>"),
            ("Program.cs", "var app = WebApplication.Create(); app.MapPost(\"/webhooks/orders\", (HttpRequest r) => 1); app.Run();"));
        var advice = Queuey.Client.Cli.Advise.Recommendation.For(Queuey.Client.Cli.Advise.RepoScan.Scan(repo));
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("\"templateKey\": \"queuey\"", StringComparison.Ordinal));
    }

    // ── apply skriver workspacet det lager, inn i profilen i fila ───────────

    private static RecordingHandler CreatesWorkspace() => new(req => req.Key switch
    {
        "POST /tenants" => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "ten_new", displayName = req.Json.GetProperty("displayName").GetString(), status = "Active", kind = "Standard",
            queues = Array.Empty<object>(), environment = req.Json.TryGetProperty("environment", out JsonElement e) ? e.GetString() : null,
        }),
        "GET /tenants/ten_new/queues" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
        "PATCH /tenants/ten_new" or "PATCH /tenants/ten_new/ingress" => RecordingHandler.NoContent(),
        _ => throw new InvalidOperationException(req.Key),
    });

    private const string Scaffolded = """
        // The scaffold, with a comment a person added.
        {
          "workspace": {
            "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}"
          },
          "queues": {},
          "profiles": {
            "dev": {
              "variables": {
                // dev only
                "QUEUEY_WORKSPACE_ENVIRONMENT": "dev"
              }
            }
          }
        }

        """;

    [Fact]
    public async Task Apply_writes_the_workspace_it_created_into_the_profile_and_changes_nothing_else()
    {
        DeployFile(Scaffolded);

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--profile", "dev", "--no-git", "--json" }), CreatesWorkspace(), Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("""
            // The scaffold, with a comment a person added.
            {
              "tenant": "${QUEUEY_TENANT}",
              "workspace": {
                "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}"
              },
              "queues": {},
              "profiles": {
                "dev": {
                  "variables": {
                    // dev only
                    "QUEUEY_WORKSPACE_ENVIRONMENT": "dev",
                    "QUEUEY_TENANT": "ten_new"
                  }
                }
              }
            }

            """, File.ReadAllText(Path.Combine(_dir, "queuey.deploy.json")));
        Assert.Contains("Wrote ten_new to profiles.dev in queuey.deploy.json", run.Stderr);
        Assert.DoesNotContain("Name it", run.Stderr);
        JsonElement updated = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("fileUpdated");
        Assert.Equal("dev", updated.GetProperty("profile").GetString());
        Assert.Equal("ten_new", updated.GetProperty("tenant").GetString());

        // Neste kommando med profilen finner workspacet.
        Assert.Equal("ten_new", DeploymentTenant.ReadFromFile(File.ReadAllText(Path.Combine(_dir, "queuey.deploy.json")), "queuey.deploy.json", "dev"));
    }

    [Fact]
    public async Task Apply_never_touches_a_value_the_profile_has()
    {
        string json = Scaffolded.Replace("\"QUEUEY_WORKSPACE_ENVIRONMENT\": \"dev\"", "\"QUEUEY_WORKSPACE_ENVIRONMENT\": \"dev\", \"QUEUEY_TENANT\": \"ten_mine\"");
        DeployFile(json);

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--profile", "dev", "--no-git", "--json" }), CreatesWorkspace(), Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal(json, File.ReadAllText(Path.Combine(_dir, "queuey.deploy.json")));
        Assert.Contains("Did not write ten_new to queuey.deploy.json: profiles.dev.variables has QUEUEY_TENANT already.", run.Stderr);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("fileUpdated").ValueKind);
    }
}
