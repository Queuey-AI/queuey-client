using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Queuey (vedtatt av Kenneth 2026-10-06): bare et workspace merket dev videresender til en lytter, og Queuey nekter
/// <c>localForward</c> ellers med <c>local_forward_needs_dev_workspace</c>. En kø som finnes, spørres med dry run av
/// leveringstypen. En ny kø kan ikke tørrkjøres felt for felt, så planen leser miljøet workspacet får, det fila setter eller
/// det workspacet har, og sier fra før apply lager køen og får avslaget.
/// </summary>
public class LocalForwardDevOnlyPlanTests
{
    private const string NewStripeQueue = """
    { "tenant": "ten_abc", @WORKSPACE@"queues": { "stripe": { "delivery": { "url": "/api/stripe", "kind": "localForward" } } } }
    """;

    /// <summary>A server where the queue does not exist yet, and the workspace has <paramref name="environment"/>.</summary>
    private static StubHttpMessageHandler Server(string? environment, List<string>? reads = null) => new(req =>
    {
        string key = $"{req.Method.Method} {req.RequestUri!.AbsolutePath}";
        if (req.Method == HttpMethod.Get)
            reads?.Add(key);
        return key switch
        {
            "GET /tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/credentials" => StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/config" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { environment }),
            // Planen beviser først at serveren planlegger, med en dry run av workspacets policy.
            "PATCH /tenants/ten_abc" or "PATCH /tenants/ten_abc/policy" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "workspace ten_abc", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
            "PUT /queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = (string?)null, displayName = "stripe", created = true, hasDeliveryTarget = false }),
            string other => throw new InvalidOperationException("unexpected " + other),
        };
    });

    private static Task<DeploymentPlan> PlanAsync(StubHttpMessageHandler server, string? fileEnvironment = null)
        => WaasTestHost.Build(apiStub: server).PlanDeploymentAsync(DeploymentFile.Parse(NewStripeQueue.Replace("@WORKSPACE@",
            fileEnvironment is null ? "" : $"\"workspace\": {{ \"environment\": \"{fileEnvironment}\" }}, ")));

    [Theory]
    [InlineData(null, "has no environment, which counts as prod")]
    [InlineData("prod", "is marked prod")]
    [InlineData("staging", "is marked staging")]
    public async Task A_new_queue_that_would_forward_outside_a_dev_workspace_is_refused_in_the_plan(string? environment, string described)
    {
        DeploymentPlan plan = await PlanAsync(Server(environment));

        DeploymentPlanStep create = Assert.Single(plan.Steps, s => s.Creates);
        QueueyException error = Assert.IsAssignableFrom<QueueyException>(create.Error);
        Assert.Equal("local_forward_needs_dev_workspace", error.ErrorCode);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(
            "Queue 'stripe' would be created with \"kind\": \"localForward\", and only a workspace marked dev forwards to a local "
            + $"listener (queuey listen): workspace ten_abc {described}. apply would create the queue, and Queuey would refuse its kind.",
            error.Message);
        Assert.Contains("${QUEUEY_STRIPE_DELIVERY_KIND}", error.SuggestedAction);
        Assert.Contains("`queuey create-tenant --name <name> --environment dev`", error.SuggestedAction);
        Assert.False(plan.WouldSucceed);
    }

    [Fact]
    public async Task A_new_queue_that_would_forward_in_a_dev_workspace_is_planned_as_before()
    {
        DeploymentPlan plan = await PlanAsync(Server("dev"));

        DeploymentPlanStep create = Assert.Single(plan.Steps, s => s.Creates);
        Assert.Null(create.Error);
        Assert.Contains(create.Notes, n => n.Contains("deliver to a local listener"));
        Assert.True(plan.WouldSucceed);
    }

    [Theory]
    [InlineData("prod", false)]
    [InlineData("dev", true)]
    public async Task The_environment_the_file_sets_is_the_one_the_queue_gets_and_nothing_is_read_for_it(string fileEnvironment, bool succeeds)
    {
        // Apply setter miljøet før køene, så det fila sier, er det køen lages i. Workspacet leses ikke for det.
        var reads = new List<string>();

        DeploymentPlan plan = await PlanAsync(Server(environment: "dev", reads), fileEnvironment);

        DeploymentPlanStep create = Assert.Single(plan.Steps, s => s.Creates);
        Assert.Equal(succeeds, create.Error is null);
        if (!succeeds)
            Assert.Contains("workspace ten_abc would be marked prod by this file", create.Error!.Message);
        Assert.DoesNotContain("GET /tenants/ten_abc/config", reads);
    }

    [Fact]
    public async Task A_key_without_tenant_write_gets_the_plan_with_a_note_instead_of_a_refusal_for_the_whole_plan()
    {
        // Review av #62: GET /tenants/{t}/config krever tenant.write. En nøkkel med queue.write uten den fikk 403, og hele planen
        // feilet, mens apply hadde gått gjennom. Nå hoppes forsjekken over, og steget sier at apply sjekker det.
        var server = new StubHttpMessageHandler(req => $"{req.Method.Method} {req.RequestUri!.AbsolutePath}" switch
        {
            "GET /tenants/ten_abc/queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "GET /tenants/ten_abc/config" => StubHttpMessageHandler.Json(HttpStatusCode.Forbidden,
                new { error = new { code = "forbidden", message = "You are not authorized to perform this action." } }),
            "PATCH /tenants/ten_abc/policy" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "workspace ten_abc", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
            "PUT /queues" => StubHttpMessageHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = (string?)null, displayName = "stripe", created = true, hasDeliveryTarget = false }),
            string other => throw new InvalidOperationException("unexpected " + other),
        });

        DeploymentPlan plan = await PlanAsync(server);

        DeploymentPlanStep create = Assert.Single(plan.Steps, s => s.Creates);
        Assert.Null(create.Error);
        Assert.Contains(create.Notes, n => n.StartsWith("Plan could not read the workspace's environment (needs tenant.write); apply checks it.",
            StringComparison.Ordinal));
        Assert.Contains(create.Notes, n => n.Contains("deliver to a local listener", StringComparison.Ordinal));
        Assert.True(plan.WouldSucceed);
    }

    [Fact]
    public async Task A_plan_without_a_new_queue_that_forwards_does_not_read_the_workspace()
    {
        var reads = new List<string>();

        await WaasTestHost.Build(apiStub: Server(environment: null, reads)).PlanDeploymentAsync(
            DeploymentFile.Parse(NewStripeQueue.Replace("@WORKSPACE@", "").Replace("localForward", "http")));

        Assert.DoesNotContain("GET /tenants/ten_abc/config", reads);
    }
}
