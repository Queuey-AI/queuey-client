using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// <c>workspace.environment</c> i deployment-fila (Queuey F2.2, 2026-10-05): dev, test, staging eller prod. apply sender det
/// først, med <c>PATCH /tenants/{ten}</c>, og bare en person senker det, så Queuey nekter en nøkkel som ville senket det
/// med 403. Da er ingenting annet skrevet, og feilen går ut med Queuey sin kode, melding og handling. Planen spør om det
/// som apply sender det, pull leser det, og drift-sjekken sammenligner det. En fil brukt mot flere workspaces tar det fra
/// en variabel.
/// </summary>
public class DeploymentEnvironmentTests
{
    // ── Fila ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("dev")]
    [InlineData("Staging")]
    [InlineData(" PROD ")]
    public void The_environment_is_one_of_four_in_any_casing(string environment)
    {
        DeploymentFile file = DeploymentFile.Parse($$"""{ "workspace": { "environment": "{{environment}}" } }""");

        file.Resolve();

        Assert.Equal(environment.Trim().ToLowerInvariant(), file.Workspace!.EnvironmentToSend);
    }

    [Theory]
    [InlineData("production", "pro…")]
    [InlineData("qa", "qa")]
    [InlineData("", "")]
    public void Another_environment_is_refused_before_anything_is_sent_showing_at_most_three_characters(string environment, string shown)
    {
        // Verdien kan komme fra en ${VAR} (Queuey F2.3-review, 2026-10-06), så feilen viser den som CLI-en viser et ukjent ord.
        var refusal = Assert.Throws<QueueyConfigurationException>(
            () => DeploymentFile.Parse($$"""{ "workspace": { "environment": "{{environment}}" } }""").Resolve());

        Assert.Equal($"workspace.environment must be one of dev, test, staging, prod; got '{shown}'.", refusal.Message);
    }

    [Fact]
    public void A_file_for_several_workspaces_takes_the_environment_from_a_variable_checked_once_it_is_expanded()
    {
        DeploymentFile file = DeploymentFile.Parse("""{ "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}" } }""");

        // Uutvidet, som en dry run viser fila: variabelen sjekkes når den er satt.
        file.Resolve();
        Assert.Contains("QUEUEY_WORKSPACE_ENVIRONMENT", file.ReferencedVariables());

        DeploymentFile staging = file.Expand(name => name == "QUEUEY_WORKSPACE_ENVIRONMENT" ? "staging" : null);
        staging.Resolve();
        Assert.Equal("staging", staging.Workspace!.EnvironmentToSend);

        DeploymentFile wrong = file.Expand(_ => "qa");
        Assert.Throws<QueueyConfigurationException>(() => wrong.Resolve());
        Assert.Throws<QueueyConfigurationException>(() => file.Expand(_ => null));
    }

    // ── apply ───────────────────────────────────────────────────────────────

    private static HttpResponseMessage Envelope(HttpStatusCode status, string code, string message, string action)
        => StubHttpMessageHandler.Json(status, new { error = new { code, message, action } });

    [Fact]
    public async Task Apply_sends_the_environment_first_in_lower_case()
    {
        var api = new StubHttpMessageHandler(req => StubHttpMessageHandler.DeployDefaults(req) ?? new HttpResponseMessage(HttpStatusCode.NoContent));

        await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(DeploymentFile.Parse("""
            { "tenant": "ten_abc", "workspace": { "environment": "Prod", "retentionDays": 7, "ingress": { "authMode": "ApiKey" } } }
            """));

        var writes = api.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}").ToList();
        Assert.Equal(new[] { "PATCH /tenants/ten_abc", "PATCH /tenants/ten_abc/ingress", "PATCH /tenants/ten_abc/policy" }, writes);

        int index = api.Requests.FindIndex(r => r.Method == HttpMethod.Patch && r.RequestUri!.AbsolutePath == "/tenants/ten_abc");
        Assert.Equal("""{"environment":"prod"}""", Encoding.UTF8.GetString(api.Bodies[index]!));
    }

    [Fact]
    public async Task A_key_that_would_lower_it_gets_Queueys_refusal_and_nothing_else_is_written()
    {
        var api = new StubHttpMessageHandler(req =>
            StubHttpMessageHandler.DeployDefaults(req)
            ?? (req.Method == HttpMethod.Patch && req.RequestUri!.AbsolutePath == "/tenants/ten_abc"
                ? Envelope(HttpStatusCode.Forbidden, "environment_lowering_needs_a_person",
                    "Only a person can lower a workspace's environment, and this would lower workspace ten_abc from prod to dev.",
                    "Ask a person to change it with Set environment… on the workspace's page in the Queuey console: https://app.queuey.ai/console/t/ten_abc?set=environment")
                : new HttpResponseMessage(HttpStatusCode.NoContent)));

        var refusal = await Assert.ThrowsAnyAsync<QueueyException>(() => WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "workspace": { "environment": "dev", "retentionDays": 7 }, "queues": { "orders": {} } }""")));

        Assert.Equal(403, refusal.StatusCode);
        Assert.Equal("environment_lowering_needs_a_person", refusal.ErrorCode);
        Assert.Contains("from prod to dev", refusal.Message);
        Assert.Contains("Queuey console", refusal.SuggestedAction);
        Assert.Equal(new[] { "PATCH /tenants/ten_abc" },
            api.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}").ToArray());
    }

    // ── plan, pull og drift ─────────────────────────────────────────────────

    [Fact]
    public async Task A_plan_asks_about_the_environment_as_apply_sends_it_and_shows_the_refusal_as_its_step()
    {
        var api = new StubHttpMessageHandler(req =>
        {
            string key = $"{req.Method.Method} {req.RequestUri!.AbsolutePath}";
            return key switch
            {
                "GET /tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
                "GET /tenants/ten_abc/credentials" => StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
                // Proben: en tom policy-patch, som endrer ingenting.
                "PATCH /tenants/ten_abc/policy" => StubHttpMessageHandler.Json(HttpStatusCode.OK,
                    new { dryRun = true, target = "workspace ten_abc", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
                "PATCH /tenants/ten_abc" => Envelope(HttpStatusCode.Forbidden, "environment_lowering_needs_a_person",
                    "Only a person can lower a workspace's environment.", "Ask a person to change it in the Queuey console."),
                _ => throw new InvalidOperationException("unexpected " + key),
            };
        });

        DeploymentPlan plan = await WaasTestHost.Build(apiStub: api).PlanDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "workspace": { "environment": "test" } }"""));

        Assert.False(plan.WouldSucceed);
        DeploymentPlanStep step = Assert.Single(plan.Steps, s => s.Aspect == "environment");
        Assert.Equal("workspace", step.Target);
        Assert.Equal("environment_lowering_needs_a_person", step.Error!.ErrorCode);
        int index = api.Requests.FindIndex(r => r.RequestUri!.AbsolutePath == "/tenants/ten_abc" && r.Method == HttpMethod.Patch);
        Assert.Equal("?dryRun=true", api.Requests[index].RequestUri!.Query);
        Assert.Equal("""{"environment":"test"}""", Encoding.UTF8.GetString(api.Bodies[index]!));
    }

    private static HttpResponseMessage Workspace(HttpRequestMessage req, string? environment)
    {
        string path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/credentials", StringComparison.Ordinal) || path.EndsWith("/queues", StringComparison.Ordinal))
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
        if (path == "/tenants/ten_abc/config")
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { environment, policy = new { ordering = "fifo" } });
        throw new InvalidOperationException("unexpected " + path);
    }

    [Fact]
    public async Task Pull_writes_the_environment_the_workspace_has_and_nothing_when_it_has_none()
    {
        DeploymentFile tagged = await WaasTestHost.Build(apiStub: new StubHttpMessageHandler(req => Workspace(req, "staging")))
            .PullDeploymentAsync("ten_abc");
        DeploymentFile untagged = await WaasTestHost.Build(apiStub: new StubHttpMessageHandler(req => Workspace(req, null)))
            .PullDeploymentAsync("ten_abc");

        Assert.Equal("staging", tagged.Workspace!.Environment);
        Assert.Contains("\"environment\": \"staging\"", tagged.ToJson());
        Assert.Null(untagged.Workspace!.Environment);
        Assert.DoesNotContain("environment", untagged.ToJson());
    }

    [Fact]
    public async Task The_drift_check_compares_the_environment_in_any_casing()
    {
        QueueyService service = WaasTestHost.Build(apiStub: new StubHttpMessageHandler(req => Workspace(req, "staging")));

        Assert.Empty(await service.CheckDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "workspace": { "environment": "Staging" } }""")));

        DriftItem drift = Assert.Single(await service.CheckDeploymentAsync(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "workspace": { "environment": "prod" } }""")));
        Assert.Equal("workspace.environment", drift.Path);
        Assert.Equal("prod", drift.Declared);
        Assert.Equal("staging", drift.Actual);
    }

    [Fact]
    public void A_template_takes_the_environment_from_a_variable()
    {
        DeploymentFile template = DeploymentTemplate.ToTemplate(
            DeploymentFile.Parse("""{ "tenant": "ten_abc", "workspace": { "environment": "prod" } }"""), "prod");

        Assert.Equal("${QUEUEY_WORKSPACE_ENVIRONMENT}", template.Workspace!.Environment);
        Assert.Contains(DeploymentTemplate.EnvironmentVariable, template.ReferencedVariables());
    }
}
