using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
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

        FlowVerification v = await service.VerifyFlowAsync("que_1", FlowVerificationRequest.FollowEvent("evt_1"), new Heard(heard));

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

        await service.VerifyFlowAsync("que_1", FlowVerificationRequest.SendTestEvent(payload, timeout: TimeSpan.FromSeconds(90)));

        Assert.Equal("""{"timeoutSeconds":90,"send":true,"payload":{"a":1}}""", Encoding.UTF8.GetString(api.Bodies[0]!));
    }

    [Fact]
    public void A_test_event_that_is_not_JSON_cannot_be_made_and_the_error_shows_no_part_of_it()
    {
        var ex = Assert.Throws<ArgumentException>(() => FlowVerificationRequest.SendTestEvent(Encoding.UTF8.GetBytes("card=4242-SECRET")));
        Assert.StartsWith("The test event is not JSON (line 1, byte ", ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);

        var nested = Assert.Throws<ArgumentException>(() => FlowVerificationRequest.SendTestEvent(Encoding.UTF8.GetBytes("{\n  \"a\": SECRET }")));
        Assert.StartsWith("The test event is not JSON (line 2, byte ", nested.Message);
        Assert.DoesNotContain("SECRET", nested.Message);
    }

    [Fact]
    public void A_test_event_is_at_most_64_KB_as_Queuey_receives_it()
    {
        // Review av queuey-client#51: over 128 KB svarte Kestrel 413 uten kropp. Grensen er Queuey sin (64 KB), målt på JSON-en
        // slik den sendes: uten mellomrom, med escaping.
        static byte[] OfSize(int bytes) => Encoding.UTF8.GetBytes("{\"x\":\"" + new string('a', bytes - 8) + "\"}");

        Assert.True(FlowVerificationRequest.SendTestEvent(OfSize(FlowVerifications.MaxTestEventBytes)).Send);

        var over = Assert.Throws<ArgumentException>(() => FlowVerificationRequest.SendTestEvent(OfSize(FlowVerifications.MaxTestEventBytes + 1)));
        Assert.Equal("The test event is 65,537 bytes as JSON, and Queuey takes at most 65,536 (64 KB).", over.Message);

        byte[] spaced = Encoding.UTF8.GetBytes("[" + string.Join(",        ", Enumerable.Repeat("1", 20_000)) + "]");
        Assert.True(spaced.Length > FlowVerifications.MaxTestEventBytes);
        Assert.True(FlowVerificationRequest.SendTestEvent(spaced).Send, "whitespace is not sent, so it does not count");
    }

    [Fact]
    public void A_request_is_made_only_by_its_factories()
    {
        // Feltene utelukker hverandre (review av queuey-client#51): ingen offentlig konstruktør og ingen offentlige settere,
        // så en kombinasjon Queuey nekter, kan ikke bygges.
        Assert.Empty(typeof(FlowVerificationRequest).GetConstructors());
        Assert.All(typeof(FlowVerificationRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => Assert.False(p.SetMethod?.IsPublic ?? false, $"{p.Name} has a public setter"));

        FlowVerificationRequest follow = FlowVerificationRequest.FollowEvent("evt_1", TimeSpan.FromSeconds(30));
        Assert.Equal(("evt_1", (string?)null, false), (follow.EventPublicId, follow.EventType, follow.Send));
        Assert.Equal(TimeSpan.FromSeconds(30), follow.Timeout);

        FlowVerificationRequest wait = FlowVerificationRequest.WaitForEvent("invoice.paid", ingressAuth: "stripe");
        Assert.Equal(((string?)null, "invoice.paid", "stripe", false), (wait.EventPublicId, wait.EventType, wait.IngressAuth, wait.Send));

        FlowVerificationRequest send = FlowVerificationRequest.SendTestEvent(Encoding.UTF8.GetBytes("{}"), eventType: "order.created");
        Assert.Equal(((string?)null, "order.created", (string?)null, true), (send.EventPublicId, send.EventType, send.IngressAuth, send.Send));
    }

    [Fact]
    public void The_factories_refuse_what_cannot_be_a_verification()
    {
        Assert.Throws<ArgumentException>(() => FlowVerificationRequest.FollowEvent("qak_kid.secret"));
        Assert.Throws<ArgumentException>(() => FlowVerificationRequest.FollowEvent(" "));
        Assert.Throws<ArgumentException>(() => FlowVerificationRequest.WaitForEvent(""));
        Assert.Throws<ArgumentException>(() => FlowVerificationRequest.WaitForEvent("invoice.paid", ingressAuth: " "));
        Assert.Throws<ArgumentNullException>(() => FlowVerificationRequest.SendTestEvent(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlowVerificationRequest.FollowEvent("evt_1", TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task A_queue_name_needs_the_workspace_and_a_queue_id_does_not()
    {
        StubHttpMessageHandler api = NothingSent();
        QueueyService service = WaasTestHost.Build(apiStub: api, configure: o => o.TenantPublicId = null);

        var ex = await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => service.VerifyFlowAsync("orders", FlowVerificationRequest.FollowEvent("evt_1")));

        Assert.StartsWith("Finding a queue by its name needs the workspace (ten_…)", ex.Message);
        Assert.Contains("que_…", ex.SuggestedAction);
        Assert.Empty(api.Requests);
    }
}
