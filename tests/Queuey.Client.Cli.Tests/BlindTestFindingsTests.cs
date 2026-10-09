using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Queuey.Client;
using Queuey.Client.Cli;
using Queuey.Client.Cli.Advise;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Funnene fra Kenneths blinde test 2026-10-09: en ny chat i et rent .NET-prosjekt satte opp Queuey mot den lokale stacken.
/// Hver test er ett funn, og sier hvilket.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class BlindTestFindingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-blind-test", Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly Func<string, string[], (int Exit, string Output)?> _git = EnvFile.Git;

    public BlindTestFindingsTests()
    {
        Directory.CreateDirectory(_dir);
        _home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(_home);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(_home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        EnvFile.Git = _git;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Dictionary<string, string> Env() => new() { [UserProfiles.PathVariable] = Path.Combine(_home, "config.json") };

    private void StoreLogin(string license = "lic_1")
        => LoginStore.Write(Path.Combine(_home, "credentials.json"), new CredentialsFile
        {
            Logins =
            {
                new StoredLogin
                {
                    ApiBase = "https://api.test", License = license, Scope = "operate", AccessToken = "at-opaque",
                    AccessTokenExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30), RefreshToken = "rt-opaque",
                    IngressBase = "https://old-tunnel.loca.lt",
                },
            },
        });

    private string Repo()
    {
        string repo = Path.Combine(_dir, "shop");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Shop.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File.WriteAllText(Path.Combine(repo, "Program.cs"), "var app = WebApplication.Create(); app.Run();");
        return repo;
    }

    // ── funn 2: lagringstiden følger planen ─────────────────────────────────

    [Fact]
    public async Task Without_a_login_advise_scaffolds_seven_days_which_every_plan_allows()
    {
        string repo = Repo();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo, "--json" }), env: Env());

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(7, json.GetProperty("retentionDays").GetInt32());
        Assert.Contains("every plan allows", json.GetProperty("retentionFrom").GetString());
    }

    [Theory]
    [InlineData("Free", 7, 7)]
    [InlineData("Team", 30, 30)]
    [InlineData("Growth", 90, 30)]
    public async Task With_a_login_advise_reads_the_license_plan_and_stays_within_it(string plan, int planDays, int expected)
    {
        StoreLogin();
        string repo = Repo();
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /billing/status" => RecordingHandler.Json(HttpStatusCode.OK, new { plan, licenseStatus = "Active" }),
            "GET /billing/plans" => RecordingHandler.Json(HttpStatusCode.OK, new[]
            {
                new { level = "Free", retentionDays = (int?)7 },
                new { level = plan, retentionDays = (int?)planDays },
            }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[]
        {
            "advise", repo, "--json", "--api-base", "https://api.test", "--config", Path.Combine(_dir, "none.json"),
        }), api, Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal(expected, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("retentionDays").GetInt32());
        Assert.Equal("Bearer at-opaque", api.Headers[0]["Authorization"]);
        Assert.Equal("lic_1", api.Headers[0]["X-License-PublicId"]);
    }

    [Fact]
    public void The_scaffold_carries_the_retention_it_is_given()
    {
        string repo = Repo();
        string content = ScaffoldPlan.For(repo, "orders", 7).Single(f => f.Path == "queuey.deploy.json").Content;

        Assert.Equal(7, Queuey.Client.Waas.DeploymentFile.Parse(content).Queues["orders"].RetentionDays);
    }

    [Fact]
    public async Task A_plan_cap_queuey_answers_with_is_said_as_the_plans_cap_with_the_way_out()
    {
        CliRun run = await CliHarness.RunAsync(() =>
        {
            ApplyCommand.WriteStepError(new QueueyException("RetentionDays 30 exceeds the plan's 7.", 400, "retention_cap_exceeded"));
            return Task.FromResult(0);
        });

        Assert.Contains("retention_cap_exceeded", run.Stdout);
        Assert.Contains("This is the license's plan", run.Stdout);
        Assert.Contains("Lower retentionDays", run.Stdout);
        Assert.Null(PlanRetention.CapHint("queue_limit"));
    }

    // ── security-review av #68, B1: en fil i repoet velger ikke hvor nøkkelen går ──

    [Fact]
    public async Task A_bare_advise_never_sends_an_api_key_even_when_queuey_json_names_a_host()
    {
        string repo = Repo();
        File.WriteAllText(Path.Combine(repo, "queuey.json"), """{ "apiBase": "https://evil.test", "license": "lic_1" }""");
        var api = new RecordingHandler(_ => throw new InvalidOperationException("A bare advise sends nothing."));
        string before = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(repo);
        try
        {
            var env = Env();
            env["QUEUEY_API_KEY"] = "qak_kid.secret";
            CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo, "--json" }), api, env);

            Assert.Equal(ExitCodes.Success, run.Exit);
            Assert.Empty(api.Requests);
            Assert.Equal(7, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("retentionDays").GetInt32());
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
        }
    }

    private static RecordingHandler QueueServer() => new(req => req.Key == "POST /queues"
        ? RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_1", tenantPublicId = "ten_1", displayName = "orders" })
        : throw new InvalidOperationException(req.Key));

    private async Task<(CliRun Run, RecordingHandler Api)> CreateQueue(string configJson, Dictionary<string, string> env, params string[] extra)
    {
        string config = Path.Combine(_dir, "queuey.json");
        File.WriteAllText(config, configJson);
        RecordingHandler api = QueueServer();
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(
            new[] { "create-queue", "--tenant", "ten_1", "--name", "orders", "--license", "lic_1", "--config", config }.Concat(extra).ToArray()), api, env);
        return (run, api);
    }

    [Fact]
    public async Task A_key_from_outside_is_not_sent_to_a_host_queuey_json_chose()
    {
        var env = Env();
        env["QUEUEY_API_KEY"] = "qak_kid.secret";

        (CliRun fromEnv, RecordingHandler api) = await CreateQueue("""{ "apiBase": "https://evil.test" }""", env);
        Assert.Equal(ExitCodes.Configuration, fromEnv.Exit);
        Assert.Contains("apiBase in", fromEnv.Stderr);
        Assert.Contains("does not choose where your key is sent", fromEnv.Stderr);
        Assert.Contains("--api-base https://evil.test", fromEnv.Stderr);
        Assert.Empty(api.Requests);

        (CliRun fromFlag, RecordingHandler flagApi) = await CreateQueue("""{ "apiBase": "https://evil.test" }""", Env(), "--api-key", "qak_kid.secret");
        Assert.Equal(ExitCodes.Configuration, fromFlag.Exit);
        Assert.Empty(flagApi.Requests);

        // Publiseringsverten også: ingressen får nøkkelen.
        (CliRun ingress, _) = await CreateQueue("""{ "ingressBase": "https://evil.test" }""", env);
        Assert.Equal(ExitCodes.Configuration, ingress.Exit);
        Assert.Contains("ingressBase in", ingress.Stderr);
    }

    [Theory]
    [InlineData("""{ "apiKey": "x", "apiBase": "https://evil.test" }""", "apiBase in")]
    [InlineData("""{ "apiKey": "x", "ingressBase": "https://evil.test" }""", "ingressBase in")]
    public async Task A_dummy_key_in_queuey_json_does_not_let_a_key_from_outside_through_to_its_host(string configJson, string field)
    {
        // Security-review av #68 runde 2 (R2-1): QUEUEY_API_KEY vinner over fila sin apiKey, og gikk til fila sin vert.
        var env = Env();
        env["QUEUEY_API_KEY"] = "qak_kid.secret";

        (CliRun run, RecordingHandler api) = await CreateQueue(configJson, env);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains(field, run.Stderr);
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("""{ "apiBase": "https://evil.test" }""", "--api-base", "https://evil.test")]   // verten navngitt av den som kjører
    [InlineData("""{ "apiBase": "http://localhost:5223" }""", null, null)]                       // på denne maskinen
    [InlineData("""{ "apiBase": "https://api.queuey.ai" }""", null, null)]                       // Queueys egen
    [InlineData("""{ "apiBase": "https://evil.test", "apiKey": "qak_file.key" }""", null, null)] // nøkkelen står i samme fil
    public async Task A_host_the_caller_named_or_a_safe_one_is_used(string configJson, string? flag, string? value)
    {
        var env = Env();
        if (!configJson.Contains("apiKey"))
            env["QUEUEY_API_KEY"] = "qak_kid.secret";

        (CliRun run, RecordingHandler api) = await CreateQueue(configJson, env, flag is null ? Array.Empty<string>() : new[] { flag, value! });

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Single(api.Requests);
    }

    // ── funn 6: hvor ingress-verten kom fra ─────────────────────────────────

    [Fact]
    public void The_resolved_ingress_host_says_where_it_came_from()
    {
        var flag = ArgMap.Parse(new[] { "--ingress-base", "http://localhost:5084" }, new HashSet<string>());
        Assert.Equal("--ingress-base", CliConfig.Resolve(flag, _ => null, null).IngressSource());

        var none = ArgMap.Parse(Array.Empty<string>(), new HashSet<string>());
        Assert.Equal("QUEUEY_INGRESS_BASE", CliConfig.Resolve(none, n => n == "QUEUEY_INGRESS_BASE" ? "http://localhost:5084" : null, null).IngressSource());
        Assert.Equal("ingressBase in queuey.json", CliConfig.Resolve(none, _ => null, """{ "ingressBase": "http://localhost:5084" }""").IngressSource());
        Assert.Equal("Queuey's default ingress host", CliConfig.Resolve(none, _ => null, null).IngressSource());

        var login = new LoginTokens("unused", new Uri("https://api.test"),
            new StoredLogin { ApiBase = "https://api.test", License = "lic_1", IngressBase = "https://old-tunnel.loca.lt" });
        ResolvedConfig withLogin = CliConfig.Resolve(none, _ => null, null).WithLogin(login);
        Assert.Equal("the login (the ingress host Queuey gave)", withLogin.IngressSource());
        Assert.Equal(new Uri("https://old-tunnel.loca.lt"), withLogin.ResolvedIngressBase());
    }

    [Fact]
    public async Task Plan_shows_where_the_ingress_host_came_from()
    {
        string file = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(file, """{ "tenant": "ten_abc", "queues": { "orders": {} } }""");
        var api = new RecordingHandler(req => req switch
        {
            { Method.Method: "GET" } when req.Path.EndsWith("/queues", StringComparison.Ordinal)
                => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
            { Method.Method: "PUT", Path: "/queues" }
                => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
            _ => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, changes = Array.Empty<object>() }),
        });

        CliRun text = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", file)), api);
        Assert.Contains("ingress host https://ingress.test/  (from --ingress-base)", text.Stdout);

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", file, "--json")), api);
        JsonElement queue = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("queues")[0];
        Assert.Equal("--ingress-base", queue.GetProperty("ingressFrom").GetString());
    }

    // ── funn 8: advise etter innloggingen ───────────────────────────────────

    [Fact]
    public async Task Advise_lists_every_file_it_read_names_the_file_behind_the_ecosystem_and_points_to_keys_mint()
    {
        string repo = Repo();

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo, "--json" }), env: Env());
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        string[] read = root.GetProperty("filesRead").EnumerateArray().Select(f => f.GetString()!).ToArray();
        Assert.Contains("Shop.csproj", read);
        Assert.Contains("Program.cs", read);
        Assert.Contains(root.GetProperty("reasons").EnumerateArray(), r => r.GetString()!.Contains("Shop.csproj"));
        string next = string.Join("\n", root.GetProperty("nextSteps").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains("queuey keys mint --queue <queue> --profile dev --write .env", next);
        Assert.Contains("needs a login that may manage keys", next);
        Assert.Contains("--prerelease", next);
        Assert.DoesNotContain("minted in the console", next);

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo }), env: Env());
        Assert.Contains("Read 2 file(s): Program.cs, Shop.csproj", human.Stdout);
        Assert.Contains("keys mint --queue shop --write .env", human.Stdout);
        Assert.DoesNotContain("always minted in the console", human.Stdout);
    }

    [Fact]
    public async Task Advise_says_the_scaffold_would_loosen_a_workspace_that_demands_a_key_and_a_signature()
    {
        // Security-review av #68 (K4).
        string repo = Repo();

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo }), env: Env());
        string text = System.Text.RegularExpressions.Regex.Replace(human.Stdout, @"\s+", " "); // teksten er brutt over linjer
        Assert.Contains("ApiKeyAndSignedRequest", text);
        Assert.Contains("apply loosens it", text);

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo, "--json" }), env: Env());
        Assert.Contains("ApiKeyAndSignedRequest", JsonDocument.Parse(json.Stdout).RootElement.GetProperty("filesNote").GetString());
    }

    [Fact]
    public async Task Advise_apply_without_a_connection_says_to_log_in_not_to_mint_a_key_in_the_console()
    {
        string repo = Repo();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[]
        {
            "advise", repo, "--apply", "--api-base", "https://api.test", "--config", Path.Combine(_dir, "none.json"),
        }), env: Env());

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("queuey login", run.Stderr);
        Assert.Contains("keys mint --queue <queue> --write .env", run.Stderr);
        Assert.DoesNotContain("Manage license → API keys", run.Stderr);
    }

    // ── security-review KAN 1: en .env git sporer ───────────────────────────

    [Fact]
    public void A_dot_env_git_tracks_is_not_the_users_own()
    {
        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, "QUEUEY_SIGNING_KEY_ID=hsk_01A\nQUEUEY_SIGNING_SECRET=s\n");

        EnvFile.Git = (_, args) => args[0] == "ls-files" ? (0, ".env") : null;
        Assert.False(CliHost.IsOwnPlainFile(env));

        EnvFile.Git = (_, args) => args[0] == "ls-files" ? (1, "") : null;
        Assert.True(CliHost.IsOwnPlainFile(env));

        // git kan ikke svare inne i et repo: da leses den ikke.
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        EnvFile.Git = (_, _) => null;
        Assert.False(CliHost.IsOwnPlainFile(env));
    }

    [Fact]
    public void A_dot_env_git_tracks_is_found_by_real_git()
    {
        string repo = Path.Combine(_dir, "tracked");
        Directory.CreateDirectory(repo);
        Run(repo, "git", "init", "-q");
        File.WriteAllText(Path.Combine(repo, ".env"), "QUEUEY_SIGNING_KEY_ID=hsk_01A\n");
        Assert.False(EnvFile.TrackedByGit(Path.Combine(repo, ".env")));

        Run(repo, "git", "add", ".env");
        Assert.True(EnvFile.TrackedByGit(Path.Combine(repo, ".env")));
    }

    [Fact]
    public void The_cli_runs_git_without_a_repositorys_fsmonitor_or_hooks()
    {
        // Security-review av #68 (K3): core.fsmonitor i et klonet repo er en kommando git kjører.
        if (OperatingSystem.IsWindows())
            return;
        string bin = Path.Combine(_dir, "bin"), log = Path.Combine(_dir, "git-args.log");
        Directory.CreateDirectory(bin);
        string git = Path.Combine(bin, "git");
        File.WriteAllText(git, $"#!/bin/sh\necho \"$@\" >> '{log}'\nexit 1\n");
        File.SetUnixFileMode(git, (UnixFileMode)0x1C0);
        Func<string?> path = GitSource.PathVariable;
        GitSource.PathVariable = () => bin;
        try
        {
            GitSource.RunGitWithExit(_dir, "ls-files", "--error-unmatch", "--", ".env");
        }
        finally
        {
            GitSource.PathVariable = path;
        }

        Assert.StartsWith("-c core.fsmonitor=false -c core.hooksPath=/dev/null ls-files", File.ReadAllText(log));
    }

    private static void Run(string folder, string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    // ── security-review KAN 2: aldri et bredere scope ───────────────────────

    [Fact]
    public void A_login_with_a_broader_scope_than_asked_is_never_taken_up()
    {
        DateTimeOffset since = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var file = new CredentialsFile
        {
            Logins = { new StoredLogin { ApiBase = "https://api.test", License = "lic_1", Scope = "operate", LoggedInAt = since + TimeSpan.FromSeconds(2) } },
        };

        Assert.Null(LoginCommand.FinishedMeanwhile(file, "https://api.test", "read", since));
        Assert.NotNull(LoginCommand.FinishedMeanwhile(file, "https://api.test", "operate", since));
        Assert.True(LoginTokens.WithinScope("read", "operate"));
        Assert.False(LoginTokens.WithinScope("operate", "read"));
    }
}
