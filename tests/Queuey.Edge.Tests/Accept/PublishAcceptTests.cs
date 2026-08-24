using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Accept;

/// <summary>
/// The accept contract (plan rev 4 §1): success = durable local commit +
/// stable identity, synchronous throws only for pre-custody causes, and no
/// network anywhere in the path (there is none to mock — that is the test).
/// </summary>
public class PublishAcceptTests
{
    [Fact]
    public async Task Publish_returns_a_receipt_backed_by_a_durable_row()
    {
        using var fx = new PublisherFixture();

        var receipt = await fx.Publisher.PublishAsync("orders", new { temp = 21.5 });

        Assert.Matches("^[0-9a-f-]{36}$", receipt.TransferId);
        Assert.Equal("orders", receipt.Queue);
        Assert.Equal(fx.Spool.Clock.UtcNow, receipt.AcceptedAtUtc);
        Assert.Equal(receipt.AcceptedAtUtc, receipt.OccurredAtUtc);

        var stats = await fx.Spool.Spool.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.PendingCount);
    }

    [Fact]
    public async Task Caller_idempotency_key_becomes_the_transfer_identity()
    {
        using var fx = new PublisherFixture();

        var first = await fx.Publisher.PublishAsync("orders", new { n = 1 },
            new PublishOptions { IdempotencyKey = "order-123" });
        var second = await fx.Publisher.PublishAsync("orders", new { n = 1 },
            new PublishOptions { IdempotencyKey = "order-123" });

        // One key, one dedup domain, one spool row.
        Assert.Equal("order-123", first.TransferId);
        Assert.Equal("order-123", second.TransferId);
        Assert.Equal(1, (await fx.Spool.Spool.GetStatsAsync(CancellationToken.None)).PendingCount);
    }

    [Fact]
    public async Task Caller_supplied_occurrence_time_rides_the_receipt()
    {
        using var fx = new PublisherFixture();
        var occurred = fx.Spool.Clock.UtcNow.AddDays(-2);

        var receipt = await fx.Publisher.PublishAsync("orders", new { temp = 20.1 },
            new PublishOptions { OccurredAtUtc = occurred });

        Assert.Equal(occurred, receipt.OccurredAtUtc);
    }

    [Fact]
    public async Task Unserializable_payload_is_rejected_at_the_call()
    {
        using var fx = new PublisherFixture();

        await Assert.ThrowsAsync<QueueyPayloadRejectedException>(
            () => fx.Publisher.PublishAsync("orders", new Unserializable()));
        await Assert.ThrowsAsync<QueueyPayloadRejectedException>(
            () => fx.Publisher.PublishAsync<object?>("orders", null));
    }

    [Fact]
    public async Task Local_payload_cap_rejects_at_the_call_not_after_transfer()
    {
        using var fx = new PublisherFixture(o => o.MaxPayloadBytes = 64);

        var ex = await Assert.ThrowsAsync<QueueyPayloadRejectedException>(
            () => fx.Publisher.PublishAsync("orders", new byte[128], "application/octet-stream"));
        Assert.Contains("MaxPayloadBytes", ex.Message);
    }

    [Fact]
    public async Task Missing_queue_name_is_a_configuration_error()
    {
        using var fx = new PublisherFixture();

        await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => fx.Publisher.PublishAsync("  ", new { }));
        await Assert.ThrowsAsync<QueueyConfigurationException>(
            () => fx.Publisher.PublishAsync("orders", [1], contentType: " "));
    }

    [Fact]
    public async Task Raw_overload_preserves_bytes_and_content_type()
    {
        using var fx = new PublisherFixture();

        var receipt = await fx.Publisher.PublishAsync("orders", [0xDE, 0xAD], "application/octet-stream");

        var claims = await fx.Spool.Spool.ClaimReadyAsync(1, TimeSpan.FromMinutes(1), CancellationToken.None);
        var envelope = Assert.Single(claims).Envelope;
        Assert.Equal(new byte[] { 0xDE, 0xAD }, envelope.Payload);
        Assert.Equal("application/octet-stream", envelope.ContentType);
        Assert.Equal(receipt.TransferId, envelope.TransferId);
    }

    [Fact]
    public async Task Context_options_flow_onto_the_envelope()
    {
        using var fx = new PublisherFixture();

        await fx.Publisher.PublishAsync("orders", new { n = 1 }, new PublishOptions
        {
            EventType = "order.created",
            GroupKey = "cust-42",
            Source = "unit-7"
        });

        var envelope = Assert.Single(
            await fx.Spool.Spool.ClaimReadyAsync(1, TimeSpan.FromMinutes(1), CancellationToken.None)).Envelope;
        Assert.Equal("order.created", envelope.EventType);
        Assert.Equal("cust-42", envelope.GroupKey);
        Assert.Equal("unit-7", envelope.Source);
        Assert.Equal("ten_test", envelope.TenantPublicId);
    }

    [Fact]
    public async Task Add_queuey_edge_wires_the_whole_accept_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "queuey-edge-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddQueueyEdge(o =>
            {
                o.ApiKey = "qak_id.secret";
                o.TenantPublicId = "ten_di";
                o.Storage.Path = Path.Combine(dir, "spool.db");
            });
            using var provider = services.BuildServiceProvider();

            var publisher = provider.GetRequiredService<IQueueyPublisher>();
            var receipt = await publisher.PublishAsync("orders", new { hello = "edge" });

            Assert.NotNull(receipt.TransferId);
            var spool = provider.GetRequiredService<IEventSpool>();
            Assert.Equal(1, (await spool.GetStatsAsync(CancellationToken.None)).PendingCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Add_queuey_edge_fails_at_composition_when_misconfigured()
    {
        var services = new ServiceCollection();
        services.AddQueueyEdge(o => o.TenantPublicId = "ten_x"); // no ApiKey
        using var provider = services.BuildServiceProvider();

        Assert.Throws<QueueyConfigurationException>(
            () => provider.GetRequiredService<IQueueyPublisher>());
    }

    private sealed class Unserializable
    {
        public string Boom => throw new NotSupportedException("no");
    }

    private sealed class PublisherFixture : IDisposable
    {
        public SpoolFixture Spool { get; }
        public IQueueyPublisher Publisher { get; }

        public PublisherFixture(Action<QueueyEdgeOptions>? configure = null)
        {
            Spool = new SpoolFixture();
            var options = new QueueyEdgeOptions
            {
                ApiKey = "qak_id.secret",
                TenantPublicId = "ten_test"
            };
            options.Storage.Path = Spool.Options.Path;
            configure?.Invoke(options);
            Publisher = new QueueyEdgePublisher(options, Spool.Spool, Spool.Clock, new EdgeWake());
        }

        public void Dispose() => Spool.Dispose();
    }
}
