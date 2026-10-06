using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// VerifyFlowAsync i SDK-en (F2.6, 2026-10-06): hva som sendes til Queuey sin flytverifisering, at lesingen følger den til den
/// er avgjort, og hva som stoppes før noe sendes. CLI-testene dekker resten gjennom `queuey verify`.
/// </summary>
public class FlowVerificationTests
{
    private static object Answer(string outcome, bool settled) => new
    {
        schemaVersion = 1, verificationId = "ver_1", kind = "flow", probeType = "real_delivery", mode = "observed_event",
        subject = new { workspacePublicId = "ten_abc", queuePublicId = "que_1", eventPublicId = "evt_1" },
        expectations = new { }, outcome, settled, summary = "s", steps = Array.Empty<object>(),
        createdAt = "2026-10-06T10:00:00Z", observeUntil = "2026-10-06T10:01:00Z", expiresAt = "2026-10-07T10:00:00Z",
    };

    private static StubHttpMessageHandler NothingSent() => new(req => throw new InvalidOperationException($"Sent {req.Method} {req.RequestUri}"));

    private sealed class Heard(List<string?> outcomes) : IProgress<FlowVerification>
    {
        public void Report(FlowVerification value) => outcomes.Add(value.Outcome);
    }

    [Fact]
    public async Task Progress_hears_the_start_and_each_read_until_Queuey_settles_it()
    {
        var api = new StubHttpMessageHandler((n, _, _) => n == 0
            ? StubHttpMessageHandler.Json(HttpStatusCode.Created, Answer("pending", settled: false))
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, Answer("passed", settled: true)));
        QueueyService service = WaasTestHost.Build(apiStub: api);
        var heard = new List<string?>();

        FlowVerification v = await service.VerifyFlowAsync("que_1", new FlowVerificationRequest { EventPublicId = "evt_1" }, new Heard(heard));

        Assert.True(v.Passed);
        Assert.Equal(new[] { "pending", "passed" }, heard);
        Assert.Equal(new[] { "POST /queues/que_1/verifications", "GET /queues/que_1/verifications/ver_1" },
            api.Requests.Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}"));
    }

    [Fact]
    public async Task The_test_event_goes_as_a_json_value_without_its_byte_order_mark()
    {
        var api = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.Created, Answer("passed", settled: true)));
        QueueyService service = WaasTestHost.Build(apiStub: api);
        byte[] payload = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("""{ "a": 1 }""")).ToArray();

        await service.VerifyFlowAsync("que_1", new FlowVerificationRequest { Send = true, Payload = payload, Timeout = TimeSpan.FromSeconds(90) });

        Assert.Equal("""{"timeoutSeconds":90,"send":true,"payload":{"a":1}}""", Encoding.UTF8.GetString(api.Bodies[0]!));
    }

    [Fact]
    public async Task A_payload_that_is_not_json_is_refused_before_anything_is_sent_and_is_not_shown()
    {
        StubHttpMessageHandler api = NothingSent();
        QueueyService service = WaasTestHost.Build(apiStub: api);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.VerifyFlowAsync(
            "que_1", new FlowVerificationRequest { Send = true, Payload = Encoding.UTF8.GetBytes("card=4242-SECRET") }));

        Assert.StartsWith("Payload is the test event's body, and it is not JSON (line 1, byte ", ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_queue_name_needs_the_workspace_and_a_queue_id_does_not()
    {
        StubHttpMessageHandler api = NothingSent();
        QueueyService service = WaasTestHost.Build(apiStub: api, configure: o => o.TenantPublicId = null);

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => service.VerifyFlowAsync("orders", new FlowVerificationRequest { EventPublicId = "evt_1" }));

        Assert.StartsWith("Finding a queue by its name needs the workspace (ten_…)", ex.Message);
        Assert.Contains("que_…", ex.SuggestedAction);
        Assert.Empty(api.Requests);
    }
}
