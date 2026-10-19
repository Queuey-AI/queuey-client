using Microsoft.Data.Sqlite;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;
using Xunit.Abstractions;

namespace Queuey.Edge.Tests.Spool;

/// <summary>
/// What settling leaves behind: the event leaves <c>spool</c> at once, payload and all, and only a payload-free
/// correlation stays in <c>settled</c> for <c>SettledRetention</c>. That correlation still recognises a repeat
/// accept, the sweep removes it after retention, and a v2 file's settled rows move there on open.
/// </summary>
public class SpoolSettleTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(850)]
    [InlineData(4096)]
    public async Task A_full_spool_of_small_events_accepts_again_as_soon_as_its_backlog_settles(int payloadBytes)
    {
        using var fx = new SpoolFixture(o =>
        {
            o.MaxSpoolBytes = 160 * 1024;
            o.HeadroomBytes = 32 * 1024;
        });

        var accepted = new List<SpoolAccept>();
        QueueySpoolFullException? full = null;
        for (var i = 0; i < 1000 && full is null; i++)
        {
            try
            {
                accepted.Add(await fx.Spool.EnqueueAsync(fx.Envelope(payloadBytes: payloadBytes), CancellationToken.None));
            }
            catch (QueueySpoolFullException ex)
            {
                full = ex;
            }
        }
        Assert.NotNull(full);
        var whileFull = await fx.Spool.GetStatsAsync(CancellationToken.None);

        // Ingen sweep, klokka står: Cloud-custody alene skal gi plass, også når payloaden lå inne i løvsiden.
        foreach (var accept in accepted)
            await fx.Spool.SettleAsync(accept.SpoolId, new CloudAck("evt_x", Replayed: false, fx.Clock.UtcNow), CancellationToken.None);

        var afterDrain = await fx.Spool.GetStatsAsync(CancellationToken.None);
        output.WriteLine($"{accepted.Count} × {payloadBytes} B: live {whileFull.StorageUsageBytes} B full → {afterDrain.StorageUsageBytes} B settled");
        Assert.True(afterDrain.StorageUsageBytes < whileFull.StorageUsageBytes / 2,
            $"settling must free the backlog's space ({whileFull.StorageUsageBytes} → {afterDrain.StorageUsageBytes})");

        // Like mange nye events som det var plass til første gang, får plass igjen.
        for (var i = 0; i < accepted.Count / 2; i++)
            await fx.Spool.EnqueueAsync(fx.Envelope(payloadBytes: payloadBytes), CancellationToken.None);
    }

    [Fact]
    public async Task Settling_moves_the_event_out_of_the_spool_and_keeps_only_its_correlation()
    {
        using var fx = new SpoolFixture();
        var accept = await fx.Spool.EnqueueAsync(fx.Envelope(transferId: "t-1", payloadBytes: 850), CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        var settledAt = fx.Clock.UtcNow.AddSeconds(3);

        await fx.Spool.SettleAsync(accept.SpoolId, new CloudAck("evt_cloud_1", Replayed: false, settledAt), CancellationToken.None);

        Assert.Equal(0L, Scalar(fx, "SELECT COUNT(*) FROM spool;"));
        Assert.Equal(1L, Scalar(fx, $"SELECT COUNT(*) FROM settled WHERE spool_id = {accept.SpoolId} AND queue_name = 'orders' " +
                                    "AND transfer_id = 't-1' AND cloud_event_id = 'evt_cloud_1';"));
        Assert.Equal(0L, Scalar(fx, "SELECT COUNT(*) FROM pragma_table_info('settled') WHERE name LIKE '%payload%';"));
        Assert.Equal(settledAt, (await fx.Spool.GetStatsAsync(CancellationToken.None)).LastSettledAtUtc);

        // En andre settle av samme rad (en ACK som kom to ganger) endrer ingenting.
        await fx.Spool.SettleAsync(accept.SpoolId, new CloudAck("evt_cloud_1", Replayed: true, settledAt.AddSeconds(1)), CancellationToken.None);
        Assert.Equal(1L, Scalar(fx, "SELECT COUNT(*) FROM settled;"));
        Assert.Equal(settledAt, (await fx.Spool.GetStatsAsync(CancellationToken.None)).LastSettledAtUtc);
    }

    [Fact]
    public async Task A_repeat_of_a_settled_event_is_the_original_accept_until_the_sweep_forgets_it()
    {
        using var fx = new SpoolFixture();
        var original = await fx.Spool.EnqueueAsync(fx.Envelope(transferId: "same-key"), CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        await fx.Spool.SettleAsync(original.SpoolId, new CloudAck("evt_1", false, fx.Clock.UtcNow), CancellationToken.None);

        // Innen SettledRetention: samme kvittering, AcceptedAtUtc fra første gang, ingenting nytt i spoolen.
        fx.Clock.Advance(fx.Options.SettledRetention - TimeSpan.FromMinutes(1));
        await fx.Spool.SweepAsync(CancellationToken.None);
        var repeat = await fx.Spool.EnqueueAsync(fx.Envelope(transferId: "same-key"), CancellationToken.None);

        Assert.True(repeat.AlreadyAccepted);
        Assert.Equal(original.SpoolId, repeat.SpoolId);
        Assert.Equal(original.AcceptedAtUtc, repeat.AcceptedAtUtc);
        Assert.Equal(0, (await fx.Spool.GetStatsAsync(CancellationToken.None)).PendingCount);

        // Etter sweep er korreleringen borte, og samme nøkkel blir en ny accept (Cloud dedupliserer derfra).
        fx.Clock.Advance(TimeSpan.FromMinutes(2));
        await fx.Spool.SweepAsync(CancellationToken.None);
        var later = await fx.Spool.EnqueueAsync(fx.Envelope(transferId: "same-key"), CancellationToken.None);

        Assert.False(later.AlreadyAccepted);
        Assert.True(later.SpoolId > original.SpoolId);
        Assert.Equal(1, (await fx.Spool.GetStatsAsync(CancellationToken.None)).PendingCount);
    }

    [Fact]
    public async Task The_sweep_removes_correlations_only_after_retention()
    {
        using var fx = new SpoolFixture();
        var accept = await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);
        await fx.Spool.SettleAsync(accept.SpoolId, new CloudAck("evt_1", false, fx.Clock.UtcNow), CancellationToken.None);

        fx.Clock.Advance(fx.Options.SettledRetention - TimeSpan.FromMinutes(1));
        Assert.Equal(0, await fx.Spool.SweepAsync(CancellationToken.None));
        Assert.Equal(1L, Scalar(fx, "SELECT COUNT(*) FROM settled;"));

        fx.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await fx.Spool.SweepAsync(CancellationToken.None));
        Assert.Equal(0L, Scalar(fx, "SELECT COUNT(*) FROM settled;"));
        Assert.Null((await fx.Spool.GetStatsAsync(CancellationToken.None)).LastSettledAtUtc);
    }

    [Fact]
    public async Task A_v2_spool_moves_its_transferred_rows_to_settled_and_leaves_pending_and_quarantined_alone()
    {
        using var fx = new SpoolFixture();
        V2SpoolFile.Create(fx.Options.Path,
            new("done-1", "Transferred", "2026-08-22T08:00:00.0000000Z", "2026-08-22T08:00:05.0000000Z", "evt_done_1"),
            new("done-2", "Transferred", "2026-08-22T09:00:00.0000000Z", "2026-08-22T09:00:07.0000000Z", "evt_done_2"),
            new("waiting", "Accepted", "2026-08-22T10:00:00.0000000Z"),
            new("poison", "Quarantined", "2026-08-22T10:00:01.0000000Z", GroupKey: "q", LastReason: "PayloadTooLarge"));

        var stats = await fx.Spool.GetStatsAsync(CancellationToken.None);

        Assert.Equal(3L, Scalar(fx, "PRAGMA user_version;"));
        Assert.Equal(DateTimeOffset.Parse("2026-08-22T09:00:07Z"), stats.LastSettledAtUtc);
        Assert.Equal(1, stats.PendingCount);
        Assert.Equal(1, stats.QuarantinedCount);
        Assert.Equal(0L, Scalar(fx, "SELECT COUNT(*) FROM spool WHERE state = 'Transferred';"));
        Assert.Equal(2L, Scalar(fx, "SELECT COUNT(*) FROM settled WHERE (spool_id, transfer_id, cloud_event_id) IN " +
                                    "(VALUES (1, 'done-1', 'evt_done_1'), (2, 'done-2', 'evt_done_2'));"));

        // Ventende og karantenert rad er de samme radene, med payloaden sin.
        Assert.Equal("Accepted", fx.Spool.InspectRow(3).State);
        Assert.Equal("Quarantined", fx.Spool.InspectRow(4).State);
        Assert.Equal("{}"u8.ToArray(), fx.Spool.InspectRow(3).Payload);
        Assert.Equal("waiting", Assert.Single(await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None)).Envelope.TransferId);

        // Dedup over migreringen: en gjentakelse av en flyttet rad er den opprinnelige accepten.
        var repeat = await fx.Spool.EnqueueAsync(fx.Envelope(transferId: "done-1"), CancellationToken.None);
        Assert.True(repeat.AlreadyAccepted);
        Assert.Equal(1, repeat.SpoolId);

        // Nye rader får id-er etter de gamle, også etter at Transferred-radene er slettet fra spool (AUTOINCREMENT).
        var fresh = await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);
        Assert.Equal(5, fresh.SpoolId);
    }

    /// <summary>
    /// Måler hva én korreleringsrad koster av grensen (rad + begge indeksene), med realistiske verdier: en UUIDv7
    /// transfer id, en Cloud event id og et kønavn. Tallet står i docs/edge-operations.md; testen holder det ærlig.
    /// </summary>
    [Fact]
    public async Task A_settled_correlation_costs_about_250_bytes_of_the_limit()
    {
        const int Events = 3000;
        using var fx = new SpoolFixture();
        await fx.Spool.EnqueueAsync(fx.Envelope(queue: "warmup"), CancellationToken.None); // skjemaet finnes
        var baseline = (await fx.Spool.GetStatsAsync(CancellationToken.None)).StorageUsageBytes;

        for (var i = 0; i < Events; i++)
        {
            var envelope = fx.Envelope(queue: "telemetry", transferId: Uuid7.NewString(fx.Clock.UtcNow), payloadBytes: 850);
            var accept = await fx.Spool.EnqueueAsync(envelope, CancellationToken.None);
            await fx.Spool.SettleAsync(accept.SpoolId,
                new CloudAck($"evt_{i:D21}", Replayed: false, fx.Clock.UtcNow), CancellationToken.None);
            fx.Clock.Advance(TimeSpan.FromMilliseconds(250));
        }

        var after = (await fx.Spool.GetStatsAsync(CancellationToken.None)).StorageUsageBytes;
        var perRow = (after - baseline) / (double)Events;
        output.WriteLine($"{Events} settled correlations: {after - baseline} B live → {perRow:F0} B per event");

        Assert.InRange(perRow, 150, 300);
    }

    private static long Scalar(SpoolFixture fx, string sql) => V2SpoolFile.Scalar(fx.Options.Path, sql);
}
