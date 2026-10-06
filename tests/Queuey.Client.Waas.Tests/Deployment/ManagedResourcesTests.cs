using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Styrte ressurser (Queuey F2.4, 2026-10-06). En apply starter på serveren før første skriving, med fila den kommer fra, og
/// sender tokenet den får på hver skriving. Det en person har løsrevet, hopper apply og planen over, med mindre --adopt tar
/// det tilbake, og sjekken melder det i stedet for å kalle det drift. En Queuey fra før styrte ressurser svarer 404, og da
/// går applyen som før.
/// </summary>
public class ManagedResourcesTests
{
    private const string ApplyHeader = "X-Queuey-Apply";

    private static readonly DateTimeOffset DetachedAt = DateTimeOffset.Parse("2026-10-06T10:00:00Z");

    private static object Detached(string reason = "tuning retries by hand") => new
    {
        state = "detached",
        repo = "https://github.com/acme/app",
        path = "deploy/queuey.deploy.json",
        file = "https://github.com/acme/app/deploy/queuey.deploy.json",
        detachedAtUtc = DetachedAt,
        detachedBy = new { kind = "user", name = "Kari Nordmann" },
        detachReason = reason,
    };

    private static object Managed() => new
    {
        state = "managed",
        repo = "https://github.com/acme/app",
        path = "deploy/queuey.deploy.json",
        appliedAtUtc = DetachedAt,
        appliedBy = new { kind = "api_client", name = "ci-deploy", apiClientPublicId = "cli_1" },
    };

    /// <summary>
    /// En server for apply: køene i <paramref name="queues"/> finnes, en ny kø opprettes, og alt annet svarer 204. Starten av
    /// applyen svarer med et token, og med workspacets styring når <paramref name="workspaceState"/> er gitt.
    /// </summary>
    private static StubHttpMessageHandler Server(object[]? queues = null, string? workspaceState = null) => new(req =>
    {
        string path = req.RequestUri!.AbsolutePath;
        if (req.Method == HttpMethod.Post && path == "/tenants/ten_abc/deployment/applies")
            return StubHttpMessageHandler.ApplyStarted(workspaceState);
        if (req.Method == HttpMethod.Get && path == "/tenants/ten_abc/queues")
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, queues ?? Array.Empty<object>());
        if (req.Method == HttpMethod.Get && path.EndsWith("/credentials", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
        if (req.Method == HttpMethod.Put && path == "/queues")
        {
            string name = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.GetProperty("displayName").GetString()!;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "que_" + name, displayName = name, created = false, hasDeliveryTarget = true });
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }) { AnswersManagement = true };

    private static object Row(string name, object? deployment = null)
        => new { publicId = "que_" + name, displayName = name, mode = "Deliver", hasDeliveryTarget = true, deployment };

    private static string? Token(HttpRequestMessage request)
        => request.Headers.TryGetValues(ApplyHeader, out IEnumerable<string>? values) ? values.Single() : null;

    private static List<string> Writes(StubHttpMessageHandler api)
        => api.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}").ToList();

    private static JsonElement StartBody(StubHttpMessageHandler api)
    {
        int start = api.Requests.FindIndex(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/tenants/ten_abc/deployment/applies");
        Assert.True(start >= 0, "The apply was never started.");
        return JsonDocument.Parse(Encoding.UTF8.GetString(api.Bodies[start]!)).RootElement;
    }

    // ── apply ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_starts_with_the_source_cleaned_and_sends_the_token_on_each_write_after_it_and_on_nothing_outside()
    {
        StubHttpMessageHandler api = Server();
        QueueyService service = WaasTestHost.Build(apiStub: api);

        QueueSyncResult result = await service.ApplyDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "workspace": { "retentionDays": 7 }, "queues": { "orders": { "retentionDays": 5 } } }"""),
            new SyncOptions
            {
                Source = new DeploymentFileSource
                {
                    Repo = "https://ghp_secret123@github.com/acme/app.git?token=abc#main",
                    Path = "./deploy/queuey.deploy.json",
                    Commit = "ABCDEF1234567",
                },
            });

        // Repo-URL-en fra .git/config kan bære et token: det sendes aldri, og heller ikke query og fragment.
        string sent = string.Join("\n", api.Bodies.Where(b => b is not null).Select(b => Encoding.UTF8.GetString(b!)));
        Assert.DoesNotContain("ghp_secret123", sent);
        Assert.DoesNotContain("token=abc", sent);
        JsonElement source = StartBody(api).GetProperty("source");
        Assert.Equal("https://github.com/acme/app", source.GetProperty("repo").GetString());
        Assert.Equal("deploy/queuey.deploy.json", source.GetProperty("path").GetString());
        Assert.Equal("abcdef1234567", source.GetProperty("commit").GetString());

        // Lesingen av køene kommer før starten og har ikke tokenet; hver skriving etter den har det.
        int start = api.Requests.FindIndex(r => r.RequestUri!.AbsolutePath.EndsWith("/deployment/applies", StringComparison.Ordinal));
        Assert.All(api.Requests.Take(start + 1), r => Assert.Null(Token(r)));
        List<HttpRequestMessage> writes = api.Requests.Skip(start + 1).Where(r => r.Method != HttpMethod.Get).ToList();
        Assert.Contains(writes, r => r.RequestUri!.AbsolutePath == "/tenants/ten_abc/policy");
        Assert.Contains(writes, r => r.RequestUri!.AbsolutePath == "/queues/que_orders/policy");
        Assert.All(writes, r => Assert.Equal(StubHttpMessageHandler.ApplyToken, Token(r)));
        Assert.Equal("Enforce", result.Enforcement);
        Assert.Empty(result.Skipped);

        // Etter applyen er tokenet borte: et kall utenfor den er et vanlig kall.
        await service.Management.ListQueuesAsync("ten_abc");
        Assert.Null(Token(api.LastRequest!));
    }

    [Fact]
    public async Task A_queue_a_person_detached_is_skipped_and_reported_with_who_when_and_why()
    {
        StubHttpMessageHandler api = Server(new[] { Row("orders", Detached()), Row("invoices", Managed()) });

        QueueSyncResult result = await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(DeploymentFile.Parse(
            """{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 }, "invoices": { "retentionDays": 5 } } }"""));

        Assert.DoesNotContain(Writes(api), w => w.Contains("que_orders", StringComparison.Ordinal));
        Assert.Contains("PATCH /queues/que_invoices/policy", Writes(api));
        Assert.DoesNotContain(result.Applied, r => r.Name == "orders");
        Assert.True(result.AllSucceeded);

        SkippedResource skipped = Assert.Single(result.Skipped);
        Assert.Equal("queues.orders", skipped.Target);
        Assert.Equal("orders", skipped.AdoptAs);
        Assert.Equal("detached by Kari Nordmann at 2026-10-06 10:00:00Z: tuning retries by hand", skipped.Management.DetachedText());
    }

    [Fact]
    public async Task A_detached_workspace_is_skipped_and_its_queues_are_still_applied()
    {
        StubHttpMessageHandler api = Server(workspaceState: "detached");

        QueueSyncResult result = await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(DeploymentFile.Parse(
            """{ "tenant": "ten_abc", "workspace": { "retentionDays": 7, "environment": "prod" }, "queues": { "orders": { "retentionDays": 5 } } }"""));

        Assert.DoesNotContain(Writes(api), w => w.StartsWith("PATCH /tenants/", StringComparison.Ordinal));
        Assert.Contains("PATCH /queues/que_orders/policy", Writes(api));
        SkippedResource skipped = Assert.Single(result.Skipped);
        Assert.Equal("workspace", skipped.Target);
        Assert.Equal("workspace", skipped.AdoptAs);
        Assert.Null(skipped.QueueName);
    }

    [Fact]
    public async Task Adopt_asks_for_what_it_takes_back_when_the_apply_starts_and_then_writes_it()
    {
        StubHttpMessageHandler api = Server(new[] { Row("orders", Detached()) }, workspaceState: "detached");

        QueueSyncResult result = await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "workspace": { "retentionDays": 7 }, "queues": { "orders": { "retentionDays": 5 } } }"""),
            new SyncOptions { Adopt = DeploymentAdopt.Parse("orders, workspace") });

        JsonElement adopt = StartBody(api).GetProperty("adopt");
        Assert.True(adopt.GetProperty("workspace").GetBoolean());
        Assert.Equal(new[] { "orders" }, adopt.GetProperty("queues").EnumerateArray().Select(q => q.GetString()).ToArray());
        Assert.Contains("PATCH /tenants/ten_abc/policy", Writes(api));
        Assert.Contains("PATCH /queues/que_orders/policy", Writes(api));
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Against_a_queuey_from_before_managed_resources_the_apply_goes_as_before_without_a_token()
    {
        // Ingen AnswersManagement: starten svarer 404, som en Queuey uten endepunktet.
        var api = new StubHttpMessageHandler(req => StubHttpMessageHandler.DeployDefaults(req)
            ?? (req.Method == HttpMethod.Put
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = true, hasDeliveryTarget = true })
                : new HttpResponseMessage(HttpStatusCode.NoContent)));

        QueueSyncResult result = await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 } } }"""),
            new SyncOptions { Source = new DeploymentFileSource { Repo = "acme/app" } });

        Assert.True(result.AllSucceeded);
        Assert.Single(api.ManagementRequests);
        Assert.All(api.Requests, r => Assert.Null(Token(r)));
        Assert.Null(result.Enforcement);
        Assert.Empty(result.Skipped);
        Assert.False(result.ApplyStarted);
    }

    [Fact]
    public async Task Without_an_apply_what_a_person_detached_is_still_skipped_and_adopt_takes_nothing_back()
    {
        // Sikkerhetsreviewen 2026-10-06: før ble skip-lista regnet ut bare med et token, så en Queuey som kjenner løsriving,
        // men ikke svarte med en apply, ville fått den løsrevne køen skrevet over.
        var api = new StubHttpMessageHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/tenants/ten_abc/queues")
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[] { Row("orders", Detached()), Row("invoices") });
            if (req.Method == HttpMethod.Get && path.EndsWith("/credentials", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
            if (req.Method == HttpMethod.Put && path == "/queues")
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "que_invoices", displayName = "invoices", created = false, hasDeliveryTarget = true });
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        QueueSyncResult result = await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 }, "invoices": { "retentionDays": 5 } } }"""),
            new SyncOptions { Adopt = DeploymentAdopt.Parse("orders") });

        Assert.False(result.ApplyStarted);
        Assert.Equal("queues.orders", Assert.Single(result.Skipped).Target);
        Assert.DoesNotContain(Writes(api), w => w.Contains("que_orders", StringComparison.Ordinal));
        Assert.Contains("PATCH /queues/que_invoices/policy", Writes(api));
    }

    [Fact]
    public async Task Without_an_apply_a_detached_workspace_is_read_and_skipped_when_the_file_declares_one()
    {
        var api = new StubHttpMessageHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path.EndsWith("/deployment/applies", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Get && path == "/tenants/ten_abc")
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "ten_abc", deployment = Detached() });
            if (req.Method == HttpMethod.Get && path == "/tenants/ten_abc/queues")
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
            if (req.Method == HttpMethod.Get && path.EndsWith("/credentials", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }) { AnswersManagement = true };

        QueueSyncResult result = await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "workspace": { "retentionDays": 7 } }"""));

        Assert.False(result.ApplyStarted);
        Assert.Equal("workspace", Assert.Single(result.Skipped).Target);
        Assert.DoesNotContain(Writes(api), w => w.StartsWith("PATCH /tenants/", StringComparison.Ordinal));
    }

    // ── plan ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_plan_runs_its_dry_runs_inside_an_apply_and_plans_nothing_for_what_a_person_detached()
    {
        var api = new StubHttpMessageHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path == "/tenants/ten_abc/deployment/applies")
                return StubHttpMessageHandler.ApplyStarted();
            if (req.Method == HttpMethod.Get && path == "/tenants/ten_abc/queues")
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[] { Row("orders", Detached()), Row("invoices", Managed()) });
            if (req.Method == HttpMethod.Get && path.EndsWith("/credentials", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
            if (req.Method == HttpMethod.Put && path == "/queues")
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_invoices", displayName = "invoices", created = false, hasDeliveryTarget = true });
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "queue que_invoices", changes = Array.Empty<object>(), notes = Array.Empty<string>() });
        }) { AnswersManagement = true };

        DeploymentPlan plan = await WaasTestHost.Build(apiStub: api).PlanDeploymentAsync(DeploymentFile.Parse(
            """{ "tenant": "ten_abc", "queues": { "orders": { "retentionDays": 5 }, "invoices": { "retentionDays": 5 } } }"""));

        Assert.DoesNotContain(plan.Steps, s => s.Target == "queues.orders");
        Assert.Contains(plan.Steps, s => s.Target == "queues.invoices");
        Assert.Equal("queues.orders", Assert.Single(plan.Skipped).Target);
        // Køen står likevel i lista, med ingress-URL-en: planen sier hva fila nevner.
        Assert.Contains(plan.Queues, q => q.Name == "orders");

        // Dry runs mot det fila styrer slippes gjennom bare inne i en apply: hver av dem har tokenet.
        List<HttpRequestMessage> dryRuns = api.Requests.Where(r => r.RequestUri!.Query.Contains("dryRun=true", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(dryRuns);
        Assert.All(dryRuns, r => Assert.Equal(StubHttpMessageHandler.ApplyToken, Token(r)));
    }

    // ── sjekken ─────────────────────────────────────────────────────────────

    private static StubHttpMessageHandler CheckServer(object? queueDeployment, object? workspaceDeployment)
        => new(req => CheckAnswer(req, queueDeployment, workspaceDeployment)) { AnswersManagement = true };

    // Workspacet og køen orders, med samme policy og retention 7, og styringen som er gitt.
    private static HttpResponseMessage CheckAnswer(HttpRequestMessage req, object? queueDeployment, object? workspaceDeployment)
    {
        string path = req.RequestUri!.AbsolutePath;
        object policy = new { idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo" };
        if (path == "/tenants/ten_abc")
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "ten_abc", deployment = workspaceDeployment });
        if (path.EndsWith("/credentials", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
        if (path == "/tenants/ten_abc/config")
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { policy, ingress = new { authMode = "None", successStatusCode = 202 } });
        if (path == "/tenants/ten_abc/queues")
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[] { Row("orders", queueDeployment) });
        return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
        {
            delivery = new { baseUrl = "/orders", authMode = "None", hasCredential = false, timeoutMs = 30000 },
            policy,
            inherited = new { destination = false, auth = true, signing = true, rateLimit = true, behavior = false },
            tenantBaseline = new { policy, ingress = new { authMode = "None", successStatusCode = 202 } },
            ingress = new { authMode = "None", successStatusCode = 202 },
        });
    }

    private const string DriftingFile = """{ "tenant": "ten_abc", "workspace": { "retentionDays": 3 }, "queues": { "orders": { "retentionDays": 5 } } }""";

    [Fact]
    public async Task The_check_reports_a_detached_queue_and_workspace_with_who_and_when_and_not_their_drift()
    {
        DeploymentCheck check = await WaasTestHost.Build(apiStub: CheckServer(Detached(), Detached("moved off the file")))
            .InspectDeploymentAsync(DeploymentFile.Parse(DriftingFile));

        Assert.True(check.InSync);
        Assert.Equal(new[] { "workspace", "queues.orders" }, check.Detached.Select(d => d.Target).ToArray());
        Assert.Equal("Kari Nordmann", check.Detached[1].Management.DetachedBy!.Name);
        Assert.Equal(DetachedAt, check.Detached[1].Management.DetachedAtUtc);
        Assert.Equal("moved off the file", check.Detached[0].Management.DetachReason);
    }

    [Fact]
    public async Task A_managed_queue_and_workspace_still_drift_as_before()
    {
        DeploymentCheck check = await WaasTestHost.Build(apiStub: CheckServer(Managed(), Managed()))
            .InspectDeploymentAsync(DeploymentFile.Parse(DriftingFile));

        Assert.Empty(check.Detached);
        Assert.Equal(new[] { "workspace.retentionDays", "queues.orders.retentionDays" }, check.Drift.Select(d => d.Path).ToArray());
    }

    [Fact]
    public async Task The_check_does_not_fail_when_the_workspace_cannot_be_read()
    {
        // Ingen AnswersManagement: GET /tenants/ten_abc svarer 404. Driften er riktig uansett.
        var api = new StubHttpMessageHandler(req => CheckAnswer(req, null, null));

        DeploymentCheck check = await WaasTestHost.Build(apiStub: api).InspectDeploymentAsync(DeploymentFile.Parse(DriftingFile));

        Assert.Empty(check.Detached);
        Assert.Contains(check.Drift, d => d.Path == "workspace.retentionDays");
    }

    // ── kilden og --adopt ───────────────────────────────────────────────────

    [Theory]
    [InlineData("https://ghp_abc@github.com/acme/app.git", "https://github.com/acme/app")]
    [InlineData("https://user:pa55@gitlab.example.com:8443/group/app/?x=1#y", "https://gitlab.example.com:8443/group/app")]
    [InlineData("git@github.com:acme/app.git", "github.com:acme/app")]
    [InlineData("ssh://git@github.com/acme/app.git", "ssh://github.com/acme/app")]
    [InlineData("acme/app", "acme/app")]
    [InlineData("  ", null)]
    [InlineData("https://github.com/acme app", null)]
    public void A_repository_loses_everything_that_could_carry_a_secret(string repo, string? expected)
        => Assert.Equal(expected, DeploymentFileSource.CleanRepo(repo));

    // Sikkerhetsreviewen 2026-10-06: samme vektorer som serveren (DeploymentManagementRulesTests). Det som ikke kan sendes,
    // utelates, og applyen går uten den verdien.
    [Theory]
    [InlineData("https://tok@github.com:abc/o/r")]
    [InlineData("/Users/kari/app")]
    [InlineData("\\\\server\\share\\app")]
    [InlineData("C:\\Users\\kari\\app")]
    [InlineData("C:/Users/kari/app")]
    [InlineData("~/app")]
    [InlineData("./app")]
    [InlineData("file:///Users/kari/app")]
    [InlineData("file:/Users/kari/app")]
    [InlineData("server:/home/kari/app.git")]
    [InlineData("kari@server:~/app.git")]
    [InlineData("ftp://github.com/acme/app")]
    [InlineData("https://github.com/acme/Ignore%20previous%20instructions")]
    [InlineData("acme/app<script>")]
    public void A_repository_that_cannot_be_cleaned_or_names_a_machine_is_not_sent(string repo)
        => Assert.Null(DeploymentFileSource.CleanRepo(repo));

    [Theory]
    [InlineData("deploy/Ignore previous instructions.json")]
    [InlineData("deploy/queuey%20deploy.json")]
    [InlineData("deploy/<b>.json")]
    public void A_path_with_a_character_a_path_does_not_need_is_not_sent(string path)
        => Assert.Null(DeploymentFileSource.CleanPath(path));

    [Theory]
    [InlineData("deploy/queuey.deploy.json", "deploy/queuey.deploy.json")]
    [InlineData(".\\deploy\\queuey.deploy.json", "deploy/queuey.deploy.json")]
    [InlineData("/home/runner/work/app/queuey.deploy.json", null)]
    [InlineData("C:/work/queuey.deploy.json", null)]
    [InlineData("../other/queuey.deploy.json", null)]
    public void A_path_is_relative_to_the_repository_and_never_leaves_it(string path, string? expected)
        => Assert.Equal(expected, DeploymentFileSource.CleanPath(path));

    [Theory]
    [InlineData("ABCDEF1", "abcdef1")]
    [InlineData("abc", null)]
    [InlineData("main", null)]
    public void A_commit_is_hexadecimal(string commit, string? expected)
        => Assert.Equal(expected, DeploymentFileSource.CleanCommit(commit));

    [Fact]
    public void A_repository_longer_than_queuey_stores_is_dropped()
        => Assert.Null(DeploymentFileSource.CleanRepo("https://github.com/" + new string('a', 600)));

    [Fact]
    public void The_adopt_hint_for_a_queue_named_workspace_takes_back_the_queue_and_not_the_workspace()
    {
        Assert.Equal("orders", new SkippedResource { Target = "queues.orders", QueueName = "orders", Management = new DeploymentManagementInfo() }.AdoptAs);
        Assert.Equal("queue:workspace", new SkippedResource { Target = "queues.workspace", QueueName = "workspace", Management = new DeploymentManagementInfo() }.AdoptAs);
        Assert.Equal("workspace", new SkippedResource { Target = "workspace", Management = new DeploymentManagementInfo() }.AdoptAs);
        // Og verdien tilbake gjennom --adopt tar køen, ikke workspacet.
        Assert.Equal("queues.workspace", DeploymentAdopt.TargetOf(DeploymentAdopt.Parse("queue:workspace").Single()));
    }

    [Fact]
    public void Adopt_takes_queues_and_the_workspace_and_a_queue_named_workspace_by_its_prefix()
    {
        Assert.Equal(new[] { "orders", "workspace", "invoices" }, DeploymentAdopt.Parse("orders, Workspace,,queue:invoices,orders"));
        Assert.Equal(new[] { "queue:workspace" }, DeploymentAdopt.Parse("queue:workspace"));
        Assert.Equal("queues.workspace", DeploymentAdopt.TargetOf("queue:workspace"));
        Assert.Equal("workspace", DeploymentAdopt.TargetOf("workspace"));
        Assert.Equal("queues.orders", DeploymentAdopt.TargetOf("orders"));
        Assert.Empty(DeploymentAdopt.Parse(null));
    }
}
