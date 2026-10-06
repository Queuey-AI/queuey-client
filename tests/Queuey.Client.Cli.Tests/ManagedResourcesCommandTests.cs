using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Styrte ressurser fra kommandolinjen (Queuey F2.4, 2026-10-06): apply sender hvor fila ligger, fra flaggene eller git og
/// aldri med userinfo, hopper over og melder det en person har løsrevet, og --adopt viser diffen før den tar det tilbake.
/// apply --json og apply --check --json har schemaVersion, som resten av CLI-en.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ManagedResourcesCommandTests : IDisposable
{
    private const string Token = "apply-token-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-managed-cli-tests", Guid.NewGuid().ToString("N"));
    private readonly GitSource.GitRunner _realGit = GitSource.Git;

    public ManagedResourcesCommandTests()
    {
        Directory.CreateDirectory(_dir);
        // Ingen test her spør den ekte git med mindre den sier det: en test som skulle gått uten git, feiler.
        GitSource.Git = (directory, arguments) => throw new InvalidOperationException($"git {string.Join(" ", arguments)} was not expected.");
    }

    public void Dispose()
    {
        GitSource.Git = _realGit;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile(string json = """{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 }, "invoices": { "retentionDays": 5 } } }""")
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static object Detached() => new
    {
        state = "detached",
        repo = "https://github.com/acme/app",
        path = "deploy/queuey.deploy.json",
        file = "https://github.com/acme/app/deploy/queuey.deploy.json",
        detachedAtUtc = DateTimeOffset.Parse("2026-10-06T10:00:00Z"),
        detachedBy = new { kind = "user", name = "Kari Nordmann" },
        detachReason = "tuning retries by hand",
    };

    private static object Managed() => new { state = "managed", repo = "https://github.com/acme/app", path = "deploy/queuey.deploy.json" };

    /// <summary>
    /// Workspacet ten_abc med køene orders og invoices og styringen som er gitt. En dry run svarer at retention går fra 7 til
    /// 5; en skriving svarer 204.
    /// </summary>
    private static RecordingHandler Server(object? orders = null, object? invoices = null) => new(req =>
    {
        bool dryRun = req.Uri.Query.Contains("dryRun=true", StringComparison.Ordinal);
        string? name = req is { Method.Method: "PUT", Path: "/queues" } ? req.Json.GetProperty("displayName").GetString() : null;
        return req switch
        {
            { Method.Method: "POST", Path: "/tenants/ten_abc/deployment/applies" } => RecordingHandler.Json(HttpStatusCode.OK,
                new { token = Token, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30), enforcement = "Warn", workspace = (object?)null }),
            { Method.Method: "GET", Path: "/tenants/ten_abc/queues" } => RecordingHandler.Json(HttpStatusCode.OK, new[]
            {
                new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true, deployment = orders },
                new { publicId = "que_invoices", displayName = "invoices", mode = "Deliver", hasDeliveryTarget = true, deployment = invoices },
            }),
            { Method.Method: "GET" } when req.Path.EndsWith("/credentials", StringComparison.Ordinal)
                => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            { Method.Method: "PUT", Path: "/queues" } => RecordingHandler.Json(HttpStatusCode.OK,
                new { dryRun, publicId = "que_" + name, displayName = name, created = false, hasDeliveryTarget = true }),
            _ when dryRun => RecordingHandler.Json(HttpStatusCode.OK, new
            {
                dryRun = true, target = "queue",
                changes = new[] { new { path = "policy.retentionDays", from = 7, to = 5 } },
                notes = Array.Empty<string>(),
            }),
            { Method.Method: "GET" } => throw new InvalidOperationException(req.Key),
            _ => RecordingHandler.NoContent(),
        };
    }) { AnswersManagement = true };

    private static RecordedRequest Start(RecordingHandler api)
        => Assert.Single(api.Requests, r => r.Path == "/tenants/ten_abc/deployment/applies");

    private static GitSource.GitRunner FakeGit(List<string> calls, string? remote = "https://x-access-token:ghs_secret@github.com/acme/app.git") =>
        (directory, arguments) =>
        {
            string call = string.Join(" ", arguments);
            calls.Add(call);
            return call switch
            {
                "rev-parse --is-inside-work-tree" => "true\n",
                "remote get-url origin" => remote is null ? null : remote + "\n",
                "rev-parse --show-prefix" => "deploy/\n",
                "rev-parse HEAD" => "0123456789ABCDEF0123456789abcdef01234567\n",
                _ => throw new InvalidOperationException("git " + call),
            };
        };

    // ── kilden ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_sends_where_the_file_lives_from_the_flags_without_the_token_in_the_url_and_json_reports_it()
    {
        RecordingHandler api = Server();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(),
            "--repo", "https://x-access-token:ghs_secret@github.com/acme/app.git?ref=main", "--repo-path", "deploy/queuey.deploy.json",
            "--commit", "ABCDEF1", "--json")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain("ghs_secret", run.Stdout);
        Assert.DoesNotContain("ghs_secret", Start(api).Body);
        JsonElement source = Start(api).Json.GetProperty("source");
        Assert.Equal("https://github.com/acme/app", source.GetProperty("repo").GetString());
        Assert.Equal("deploy/queuey.deploy.json", source.GetProperty("path").GetString());
        Assert.Equal("abcdef1", source.GetProperty("commit").GetString());

        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("schemaVersion", root.EnumerateObject().First().Name);
        Assert.Equal(ApplyCommand.ResultJsonSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("https://github.com/acme/app", root.GetProperty("source").GetProperty("repo").GetString());
        Assert.Equal("Warn", root.GetProperty("enforcement").GetString());
        Assert.Empty(root.GetProperty("skipped").EnumerateArray());
        Assert.Equal(2, root.GetProperty("succeeded").GetInt32());

        // Hver skriving etter starten bærer tokenet; starten selv og lesingene før den gjør det ikke.
        int start = api.Requests.IndexOf(Start(api));
        Assert.All(Enumerable.Range(0, start + 1), i => Assert.False(api.Headers[i].ContainsKey("X-Queuey-Apply")));
        var writes = Enumerable.Range(start + 1, api.Requests.Count - start - 1).Where(i => api.Requests[i].Method != HttpMethod.Get).ToList();
        Assert.Contains(writes, i => api.Requests[i].Path == "/queues/que_orders/policy");
        Assert.All(writes, i => Assert.Equal(Token, api.Headers[i]["X-Queuey-Apply"]));
    }

    [Fact]
    public async Task Apply_reads_the_source_from_git_with_the_remote_cleaned_and_a_flag_wins_for_its_own_value()
    {
        var calls = new List<string>();
        GitSource.Git = FakeGit(calls);
        RecordingHandler api = Server();

        CliRun run = await CliHarness.RunAsync(
            () => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--commit", "fedcba9876543")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement source = Start(api).Json.GetProperty("source");
        Assert.Equal("https://github.com/acme/app", source.GetProperty("repo").GetString());
        Assert.Equal("deploy/queuey.deploy.json", source.GetProperty("path").GetString());
        Assert.Equal("fedcba9876543", source.GetProperty("commit").GetString());
        Assert.DoesNotContain("rev-parse HEAD", calls);
        Assert.DoesNotContain("ghs_secret", Start(api).Body);
    }

    [Fact]
    public async Task No_git_leaves_git_alone_and_sends_no_source()
    {
        RecordingHandler api = Server();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.True(!Start(api).Json.TryGetProperty("source", out JsonElement source) || source.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void Git_gives_what_the_flags_do_not_and_outside_a_repository_only_the_flags_count()
    {
        var calls = new List<string>();
        GitSource.Git = FakeGit(calls);
        string file = Path.Combine(_dir, "deploy", "queuey.deploy.json");

        DeploymentFileSource? fromGit = GitSource.Resolve(file, null, null, null, noGit: false);
        Assert.Equal("https://github.com/acme/app", fromGit!.Repo);
        Assert.Equal("deploy/queuey.deploy.json", fromGit.Path);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", fromGit.Commit);

        calls.Clear();
        DeploymentFileSource? flags = GitSource.Resolve(file, "acme/other", "infra/queuey.deploy.json", "abcdef1", noGit: false);
        Assert.Empty(calls);
        Assert.Equal("acme/other", flags!.Repo);

        GitSource.Git = (_, _) => null;   // ikke et repo, eller git finnes ikke
        Assert.Null(GitSource.Resolve(file, null, null, null, noGit: false));
        Assert.Equal("acme/app", GitSource.Resolve(file, "acme/app", null, null, noGit: false)!.Repo);

        // En sti fra roten av maskinen er ikke en sti i repoet, og en verdi som ikke kan lagres, sendes ikke.
        DeploymentFileSource? odd = GitSource.Resolve(file, "acme/app", "/home/runner/queuey.deploy.json", "main", noGit: true);
        Assert.Null(odd!.Path);
        Assert.Null(odd.Commit);
    }

    [Fact]
    public void Against_a_real_repository_the_remote_loses_its_token_and_the_path_is_from_the_root()
    {
        GitSource.Git = _realGit;
        string repo = Path.Combine(_dir, "repo");
        string deploy = Path.Combine(repo, "infra", "queuey");
        Directory.CreateDirectory(deploy);
        Git(repo, "init", "-q");
        Git(repo, "remote", "add", "origin", "https://oauth2:glpat-secret@gitlab.example.com/acme/app.git");
        Git(repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "--allow-empty", "-m", "first");
        string head = Git(repo, "rev-parse", "HEAD").Trim();
        string file = Path.Combine(deploy, "queuey.deploy.json");

        DeploymentFileSource? source = GitSource.Resolve(file, null, null, null, noGit: false);

        Assert.Equal("https://gitlab.example.com/acme/app", source!.Repo);
        Assert.Equal("infra/queuey/queuey.deploy.json", source.Path);
        Assert.Equal(head, source.Commit);
    }

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process git = Process.Start(start)!;
        string output = git.StandardOutput.ReadToEnd();
        string error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(" ", arguments)}: {error}");
        return output;
    }

    // ── løsrevet og tatt tilbake ────────────────────────────────────────────

    [Fact]
    public async Task A_detached_queue_is_skipped_and_the_output_says_who_detached_it_when_why_and_how_to_take_it_back()
    {
        RecordingHandler api = Server(orders: Detached(), invoices: Managed());

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git")), api);

        Assert.Equal(ExitCodes.Success, human.Exit);
        Assert.DoesNotContain(api.Writes, w => w.Path.Contains("que_orders", StringComparison.Ordinal));
        Assert.Contains("PATCH /queues/que_invoices/policy", api.Writes.Select(w => w.Key));
        Assert.Contains("  – queues.orders\tdetached by Kari Nordmann at 2026-10-06 10:00:00Z: tuning retries by hand — skipped; "
                        + "`queuey apply --adopt orders` takes it back", human.Stdout);

        CliRun json = await CliHarness.RunAsync(
            () => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git", "--json")), Server(orders: Detached()));

        JsonElement skipped = Assert.Single(JsonDocument.Parse(json.Stdout).RootElement.GetProperty("skipped").EnumerateArray());
        Assert.Equal("queues.orders", skipped.GetProperty("target").GetString());
        Assert.Equal("orders", skipped.GetProperty("queue").GetString());
        Assert.Equal("detached", skipped.GetProperty("state").GetString());
        Assert.Equal("Kari Nordmann", skipped.GetProperty("detachedBy").GetProperty("name").GetString());
        Assert.Equal("tuning retries by hand", skipped.GetProperty("detachReason").GetString());
        Assert.Equal("orders", skipped.GetProperty("adoptAs").GetString());
    }

    [Fact]
    public async Task Adopt_shows_what_the_file_changes_on_what_it_takes_back_then_applies_it()
    {
        RecordingHandler api = Server(orders: Detached());

        CliRun run = await CliHarness.RunAsync(
            () => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--no-git", "--adopt", "orders")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        string[] lines = run.Stdout.Split('\n', StringSplitOptions.TrimEntries);
        int adopting = Array.IndexOf(lines, "Adopting queues.orders back into deployment management. What the file changes on it:");
        Assert.True(adopting >= 0, run.Stdout);
        Assert.Contains("~ policy.retentionDays: 7 → 5", lines.Skip(adopting));
        Assert.True(Array.FindIndex(lines, l => l.StartsWith("✓ orders", StringComparison.Ordinal)) > adopting, run.Stdout);

        // Planen og applyen starter hver sin apply, og begge ber om å ta køen tilbake.
        var starts = api.Requests.Where(r => r.Path == "/tenants/ten_abc/deployment/applies").ToList();
        Assert.Equal(2, starts.Count);
        Assert.All(starts, s => Assert.Equal("orders", Assert.Single(s.Json.GetProperty("adopt").GetProperty("queues").EnumerateArray()).GetString()));
        Assert.Contains(api.Writes, w => w.Key == "PATCH /queues/que_orders/policy" && w.Uri.Query.Length == 0);
        Assert.DoesNotContain("skipped;", run.Stdout);
    }

    [Fact]
    public async Task Check_reports_a_detached_queue_with_who_and_when_and_does_not_call_it_drift()
    {
        object policy = new { idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo" };
        RecordingHandler Check() => new(req => req switch
        {
            { Path: "/tenants/ten_abc/queues" } => RecordingHandler.Json(HttpStatusCode.OK, new[]
            {
                new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true, deployment = Detached() },
            }),
            { Path: "/tenants/ten_abc/config" } => RecordingHandler.Json(HttpStatusCode.OK, new { policy, ingress = new { authMode = "None", successStatusCode = 202 } }),
            _ when req.Path.EndsWith("/credentials", StringComparison.Ordinal) => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            { Path: "/queues/que_orders/config" } => RecordingHandler.Json(HttpStatusCode.OK, new
            {
                delivery = new { baseUrl = "/orders", authMode = "None", hasCredential = false, timeoutMs = 30000 },
                policy,
                inherited = new { destination = false, auth = true, signing = true, rateLimit = true, behavior = false },
                tenantBaseline = new { policy, ingress = new { authMode = "None", successStatusCode = 202 } },
                ingress = new { authMode = "None", successStatusCode = 202 },
            }),
            _ => throw new InvalidOperationException(req.Key),
        }) { AnswersManagement = true };
        string path = DeployFile("""{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 } } }""");

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--check")), Check());

        Assert.Equal(ExitCodes.Success, human.Exit);
        Assert.Contains("matches the workspace", human.Stdout);
        Assert.Contains("  – queues.orders\tdetached by Kari Nordmann at 2026-10-06 10:00:00Z: tuning retries by hand — apply skips it; "
                        + "`queuey apply --adopt orders` takes it back", human.Stdout);

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--check", "--json")), Check());

        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal(new[] { "schemaVersion", "file", "inSync", "drift", "detached" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("inSync").GetBoolean());
        JsonElement detached = Assert.Single(root.GetProperty("detached").EnumerateArray());
        Assert.Equal("queues.orders", detached.GetProperty("target").GetString());
        Assert.Equal("2026-10-06T10:00:00+00:00", detached.GetProperty("detachedAtUtc").GetString());
    }

    [Fact]
    public async Task Plan_lists_what_a_person_detached_under_skipped_and_plans_it_with_adopt()
    {
        CliRun plan = await CliHarness.RunAsync(
            () => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--json")), Server(orders: Detached()));

        Assert.Equal(ExitCodes.Success, plan.Exit);
        JsonElement root = JsonDocument.Parse(plan.Stdout).RootElement;
        Assert.Equal("queues.orders", Assert.Single(root.GetProperty("skipped").EnumerateArray()).GetProperty("target").GetString());
        Assert.DoesNotContain(root.GetProperty("steps").EnumerateArray(), s => s.GetProperty("target").GetString() == "queues.orders");

        CliRun adopted = await CliHarness.RunAsync(
            () => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--adopt", "orders", "--json")), Server(orders: Detached()));

        JsonElement again = JsonDocument.Parse(adopted.Stdout).RootElement;
        Assert.Empty(again.GetProperty("skipped").EnumerateArray());
        Assert.Contains(again.GetProperty("steps").EnumerateArray(), s => s.GetProperty("target").GetString() == "queues.orders");
    }
}
