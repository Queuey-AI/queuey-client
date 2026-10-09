using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Tests;

/// <summary>Køene queue sync finner i denne testens egen assembly.</summary>
[QueueyQueue("plan-orders", RetentionDays = 5)]
public sealed class PlanOrdersQueue
{
}

/// <summary>Den andre køen, som synkes som før.</summary>
[QueueyQueue("plan-invoices", RetentionDays = 5)]
public sealed class PlanInvoicesQueue
{
}

/// <summary>
/// queuey queue sync når Queuey vil ha en plan for en kø (Queuey F3.11, KAN 3 fra reviewen av #64): synken går videre med de
/// andre, sier hva som gjøres, og avslutter med 1, fordi endringen ikke ble skrevet.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class QueueSyncPlanRequiredTests
{
    private static RecordingHandler Server() => new(req => req.Key switch
    {
        "PUT /queues" => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "que_" + req.Json.GetProperty("displayName").GetString(),
            displayName = req.Json.GetProperty("displayName").GetString(),
            created = false,
            hasDeliveryTarget = true,
        }),
        "PATCH /queues/que_plan-orders/policy" => RecordingHandler.Error(HttpStatusCode.Forbidden, "plan_required",
            "An API key makes this write (Queues.PatchPolicy) only through a configuration plan. Nothing was changed.",
            "Make a plan … queuey plan and queuey apply do it for you."),
        _ => RecordingHandler.NoContent(),
    });

    private static string[] Args(params string[] extra) => CliHarness.With(new[]
    {
        "queue", "sync", "--assembly", typeof(PlanOrdersQueue).Assembly.Location, "--only", "plan-orders,plan-invoices", "--tenant", "ten_abc",
    }.Concat(extra).ToArray());

    [Fact]
    public async Task Queue_sync_goes_on_past_a_queue_that_needs_a_plan_says_what_to_do_and_exits_1()
    {
        RecordingHandler api = Server();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(Args()), api);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("! plan-orders\tque_plan-orders\tneeds a configuration plan — 403 plan_required", run.Stdout);
        Assert.Contains("✓ plan-invoices", run.Stdout);
        Assert.Contains("run queuey apply, which makes the configuration plan", run.Stdout);
        Assert.Contains("0 failed, 1 need a configuration plan", run.Stdout);
        Assert.Contains(api.Writes, w => w.Key == "PATCH /queues/que_plan-invoices/policy");

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(Args("--json")), Server());

        Assert.Equal(ExitCodes.RuntimeError, json.Exit);
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal("plan-orders", Assert.Single(root.GetProperty("planRequired").EnumerateArray()).GetString());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
    }
}
