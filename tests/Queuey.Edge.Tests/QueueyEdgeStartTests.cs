using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;
using Xunit;

namespace Queuey.Edge.Tests;

/// <summary>
/// The host-less entry point: one call gives a node that accepts publishes
/// durably, reports health, kicks leftover backoff on start, and stops
/// cleanly on dispose. Cloud is unreachable on purpose — nothing here
/// depends on a transfer succeeding.
/// </summary>
public sealed class QueueyEdgeStartTests
{
    private static readonly Uri NowhereIngress = new("http://127.0.0.1:9/");

    [Fact]
    public async Task One_call_starts_a_node_that_accepts_publishes_and_reports_health()
    {
        var dir = Directory.CreateTempSubdirectory("queuey-edge-start-");
        try
        {
            await using var edge = await QueueyEdge.StartAsync(o =>
            {
                o.ApiKey = "qak_id.secret";
                o.TenantPublicId = "ten_test";
                o.IngressBaseAddress = NowhereIngress;
                o.Storage.Path = Path.Combine(dir.FullName, "spool.db");
                o.Health.NodeName = "linq-node-1";
            });

            Assert.Equal("linq-node-1", edge.Name);
            var receipt = await edge.PublishAsync("orders", new { seq = 1 }, new PublishOptions { EventType = "status.update", GroupKey = "n1" });
            Assert.False(string.IsNullOrEmpty(receipt.TransferId));

            var health = edge.Health;
            Assert.Equal(1, health.PendingCount);
            Assert.NotEqual(EdgeState.StorageFaulted, health.State);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_second_start_on_the_same_spool_finds_the_pending_event_and_kicks_it()
    {
        var dir = Directory.CreateTempSubdirectory("queuey-edge-restart-");
        try
        {
            Action<QueueyEdgeOptions> configure = o =>
            {
                o.ApiKey = "qak_id.secret";
                o.TenantPublicId = "ten_test";
                o.IngressBaseAddress = NowhereIngress;
                o.Storage.Path = Path.Combine(dir.FullName, "spool.db");
            };

            await using (var first = await QueueyEdge.StartAsync(configure))
            {
                await first.PublishAsync("orders", new { seq = 1 });
                // The transfer loop has by now tried Cloud and armed a backoff.
                await Task.Delay(300);
            }

            await using var second = await QueueyEdge.StartAsync(configure);
            Assert.Equal(1, second.Health.PendingCount);
            Assert.True(second.Health.NextTransferAttemptUtc is null
                        || second.Health.NextTransferAttemptUtc <= DateTimeOffset.UtcNow.AddSeconds(1),
                "start kicks leftover backoff so the event is due now");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_handler_hook_sits_under_both_transfer_and_health_reports_and_can_cut_the_network()
    {
        var dir = Directory.CreateTempSubdirectory("queuey-edge-handler-");
        try
        {
            var seen = new List<string>();
            var cut = false;
            await using var edge = await QueueyEdge.StartAsync(o =>
            {
                o.ApiKey = "qak_id.secret";
                o.TenantPublicId = "ten_test";
                o.IngressBaseAddress = new Uri("https://ingress.test/");
                o.Storage.Path = Path.Combine(dir.FullName, "spool.db");
                o.Health.ReportToCloud = true;
                o.Health.NodeName = "cut-me";
                o.HttpMessageHandlerFactory = () => new ScriptedHandler(req =>
                {
                    lock (seen) seen.Add(req.RequestUri!.AbsolutePath);
                    // What a real outage looks like to HttpClient: a socket error under the request exception.
                    if (cut) throw new System.Net.Http.HttpRequestException("network down",
                        new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable));
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Accepted)
                    {
                        Content = new System.Net.Http.StringContent("{\"eventId\":\"evt_1\"}", System.Text.Encoding.UTF8, "application/json")
                    };
                });
            });

            await edge.PublishAsync("orders", new { seq = 1 });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !(seen.Any(p => p.Contains("/events/")) && seen.Any(p => p.EndsWith("/health"))))
                await Task.Delay(50);

            Assert.Contains(seen, p => p.Contains("/events/ten_test/orders"));
            Assert.Contains(seen, p => p.EndsWith("/health"));

            cut = true;
            await edge.PublishAsync("orders", new { seq = 2 });
            // The health snapshot caches spool stats for a second; poll past it.
            var h = edge.Health;
            var until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && h.PendingCount == 0)
            {
                await Task.Delay(100);
                h = edge.Health;
            }
            Assert.True(h.PendingCount >= 1, $"with the network cut the event stays in the spool — state={h.State} pending={h.PendingCount} fail={h.LastTransferFailure?.Reason}");
            Assert.NotEqual(EdgeState.Healthy, h.State);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class ScriptedHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> _script;
        public ScriptedHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> script) => _script = script;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_script(request));
    }

    [Fact]
    public async Task A_misconfigured_node_fails_at_start_not_at_the_first_publish()
    {
        await Assert.ThrowsAsync<QueueyConfigurationException>(() =>
            QueueyEdge.StartAsync(o => { o.TenantPublicId = "ten_test"; }));
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_releases_the_spool_file()
    {
        var dir = Directory.CreateTempSubdirectory("queuey-edge-dispose-");
        try
        {
            var edge = await QueueyEdge.StartAsync(o =>
            {
                o.ApiKey = "qak_id.secret";
                o.TenantPublicId = "ten_test";
                o.IngressBaseAddress = NowhereIngress;
                o.Storage.Path = Path.Combine(dir.FullName, "spool.db");
            });
            await edge.DisposeAsync();
            await edge.DisposeAsync();

            // A released spool can be opened again by the next node.
            await using var again = await QueueyEdge.StartAsync(o =>
            {
                o.ApiKey = "qak_id.secret";
                o.TenantPublicId = "ten_test";
                o.IngressBaseAddress = NowhereIngress;
                o.Storage.Path = Path.Combine(dir.FullName, "spool.db");
            });
            Assert.Equal(0, again.Health.PendingCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
