using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.LocalEndpoint;

/// <summary>
/// The polyglot contract, proven with RAW HTTP — no SDK anywhere in these
/// tests, because that is the point: what a TypeScript/Java/Python one-liner
/// sees against the loopback endpoint. 202 = durably committed locally
/// (verified in the spool file), headers ride the same names as cloud
/// ingress, and every failure is the honest pre-custody kind.
/// </summary>
public class EdgeLocalEndpointTests : IAsyncLifetime
{
    private SpoolFixture _fx = null!;
    private EdgeLocalEndpoint _endpoint = null!;
    private HttpClient _http = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _fx = new SpoolFixture();
        _port = 17000 + Random.Shared.Next(1000);

        var options = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_local" };
        options.Storage.Path = _fx.Options.Path;
        options.LocalEndpoint.Port = _port;

        var clock = SystemEdgeClock.Instance;
        var spool = new SqliteEventSpool(_fx.Options, clock);
        var publisher = new QueueyEdgePublisher(options, spool, clock, new EdgeWake());
        var health = new EdgeHealthService(spool, new EdgeRuntimeState(), options, clock,
            NullLogger<EdgeHealthService>.Instance);

        _endpoint = new EdgeLocalEndpoint(options, publisher, health, NullLogger<EdgeLocalEndpoint>.Instance);
        await _endpoint.StartAsync(CancellationToken.None);
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
        await WaitForListenerAsync();
    }

    [Fact]
    public async Task A_plain_http_post_is_durably_accepted_with_full_context()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/events/ten_local/sensor-readings")
        {
            Content = new StringContent("""{"temp":21.5}""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "reading-0001");
        request.Headers.TryAddWithoutValidation("X-Queuey-Event-Type", "temperature.updated");
        request.Headers.TryAddWithoutValidation("X-Queuey-Group-Key", "unit-7");
        request.Headers.TryAddWithoutValidation("X-Queuey-Occurred-At", "2026-08-20T09:14:02Z");

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("\"transferId\":\"reading-0001\"", body);
        Assert.Contains("\"custody\":\"edge\"", body);

        // The 202 means the row is in the FILE — same accept boundary as
        // the in-process PublishAsync.
        var reader = new SqliteEventSpool(_fx.Options, SystemEdgeClock.Instance);
        var claims = await reader.ClaimReadyAsync(1, TimeSpan.FromMinutes(1), CancellationToken.None);
        var envelope = Assert.Single(claims).Envelope;
        Assert.Equal("reading-0001", envelope.TransferId);
        Assert.Equal("temperature.updated", envelope.EventType);
        Assert.Equal("unit-7", envelope.GroupKey);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 9, 14, 2, TimeSpan.Zero), envelope.OccurredAtUtc);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_returns_the_same_transfer_identity()
    {
        var first = await PostAsync("orders", "{}", ("Idempotency-Key", "order-1"));
        var second = await PostAsync("orders", "{}", ("Idempotency-Key", "order-1"));

        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.Equal(HttpStatusCode.Accepted, second.Status);
        Assert.Equal(
            ExtractTransferId(first.Body),
            ExtractTransferId(second.Body));
    }

    [Fact]
    public async Task Wrong_tenant_is_a_404_this_daemon_serves_one_workspace()
    {
        var (status, body) = await PostAsync("orders", "{}", tenant: "ten_other");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("tenant_mismatch", body);
    }

    [Fact]
    public async Task Health_is_readable_over_the_same_port()
    {
        var response = await _http.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("pendingCount", body);
        Assert.Contains("state", body);
    }

    [Fact]
    public async Task Unknown_routes_are_404_with_a_pointer()
    {
        var response = await _http.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── plumbing ────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string queue, string json, (string Name, string Value)? header = null, string tenant = "ten_local")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/events/{tenant}/{queue}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (header is { } h) request.Headers.TryAddWithoutValidation(h.Name, h.Value);
        var response = await _http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string ExtractTransferId(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("transferId").GetString()!;
    }

    private async Task WaitForListenerAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                await _http.GetAsync("/health");
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50);
            }
        }
        Assert.Fail("local endpoint did not start");
    }

    public async Task DisposeAsync()
    {
        await _endpoint.StopAsync(CancellationToken.None);
        _endpoint.Dispose();
        _http.Dispose();
        _fx.Dispose();
    }
}
