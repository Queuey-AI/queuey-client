using Microsoft.Data.Sqlite;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Spool;

/// <summary>
/// The durability boundary itself: FULL is the product, accepts survive
/// abandoned writers, corruption halts instead of resetting. These are the
/// tests the plan calls "the product, not tuning".
/// </summary>
public class SpoolDurabilityTests
{
    [Fact]
    public async Task Spool_connections_run_wal_with_synchronous_full()
    {
        using var fx = new SpoolFixture();
        await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);

        var (journal, synchronous) = fx.Spool.InspectDurabilityPragmas();

        Assert.Equal("wal", journal, ignoreCase: true);
        // 2 = FULL. NORMAL(1) in WAL mode can lose the most recent commits on
        // power loss — exactly the events PublishAsync just promised to own.
        Assert.Equal(2, synchronous);
    }

    [Fact]
    public async Task Accepted_row_is_readable_by_a_completely_fresh_connection()
    {
        using var fx = new SpoolFixture();
        var accept = await fx.Spool.EnqueueAsync(fx.Envelope(transferId: "t-1"), CancellationToken.None);

        // Not the spool's connection, not its pool: proof the row is in the
        // FILE, not in some in-process state.
        SqliteConnection.ClearAllPools();
        await using var conn = new SqliteConnection($"Data Source={fx.Options.Path}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT transfer_id, state FROM spool WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", accept.SpoolId);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("t-1", reader.GetString(0));
        Assert.Equal("Accepted", reader.GetString(1));
    }

    [Fact]
    public async Task Abandoned_writer_transaction_leaves_no_half_accepted_row()
    {
        using var fx = new SpoolFixture();
        await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None); // bootstrap schema

        // A writer that dies mid-transaction (the kill -9 simulation SQLite
        // lets us do in-process): begin, insert, vanish without commit.
        await using (var conn = new SqliteConnection($"Data Source={fx.Options.Path}"))
        {
            await conn.OpenAsync();
            await using var tx = conn.BeginTransaction();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO spool (envelope_version, transfer_id, queue_name, tenant_public_id,
                                   content_type, payload, lane, occurred_at_utc, enqueued_mono,
                                   state, next_attempt_utc, accepted_at_utc)
                VALUES (1, 'torn', 'orders', 'ten_test', 'application/json', x'00', 'orders',
                        '2026-08-22T12:00:00.0000000Z', 0, 'Accepted',
                        '2026-08-22T12:00:00.0000000Z', '2026-08-22T12:00:00.0000000Z');
                """;
            await cmd.ExecuteNonQueryAsync();
            // no commit — the "process" dies here
        }
        SqliteConnection.ClearAllPools();

        var stats = await fx.Spool.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.PendingCount); // only the committed accept survives
    }

    [Fact]
    public async Task Corrupt_file_faults_the_spool_and_preserves_the_bytes()
    {
        using var fx = new SpoolFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fx.Options.Path)!);
        var garbage = new byte[4096];
        Random.Shared.NextBytes(garbage);
        await File.WriteAllBytesAsync(fx.Options.Path, garbage);

        await Assert.ThrowsAsync<QueueyStorageFaultedException>(
            () => fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None));

        // Faulted is sticky: subsequent operations refuse too, and mention
        // the explicit recovery verbs.
        var again = await Assert.ThrowsAsync<QueueyStorageFaultedException>(
            () => fx.Spool.GetStatsAsync(CancellationToken.None));
        Assert.Contains("recover", again.Message);
        Assert.Contains("accept-data-loss", again.Message);

        // Never a silent fresh spool: the corrupt bytes are untouched.
        Assert.Equal(garbage, await File.ReadAllBytesAsync(fx.Options.Path));
    }

    [Fact]
    public async Task Spool_written_by_a_newer_version_refuses_loudly()
    {
        using var fx = new SpoolFixture();
        await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);

        SqliteConnection.ClearAllPools();
        await using (var conn = new SqliteConnection($"Data Source={fx.Options.Path}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var reopened = new SqliteEventSpool(fx.Options, fx.Clock);
        var ex = await Assert.ThrowsAsync<Client.QueueyConfigurationException>(
            () => reopened.EnqueueAsync(fx.Envelope(), CancellationToken.None));
        Assert.Contains("NEWER", ex.Message);
    }

    [Fact]
    public async Task Spool_full_rejects_new_accepts_but_bookkeeping_still_works()
    {
        using var fx = new SpoolFixture(o =>
        {
            o.MaxSpoolBytes = 96 * 1024;   // tiny spool
            o.HeadroomBytes = 32 * 1024;   // large reserved margin
        });

        // Fill until the limit refuses.
        SpoolAccept? lastAccept = null;
        QueueySpoolFullException? full = null;
        for (var i = 0; i < 200 && full is null; i++)
        {
            try
            {
                lastAccept = await fx.Spool.EnqueueAsync(fx.Envelope(payloadBytes: 4096), CancellationToken.None);
            }
            catch (QueueySpoolFullException ex)
            {
                full = ex;
            }
        }

        Assert.NotNull(full);
        Assert.Contains("lossless retention", full!.Message);

        // The headroom exists so recording SUCCESS still works at the limit —
        // a spool that cannot settle re-sends forever.
        await fx.Spool.SettleAsync(lastAccept!.SpoolId,
            new CloudAck("evt_x", Replayed: false, fx.Clock.UtcNow), CancellationToken.None);
        var stats = await fx.Spool.GetStatsAsync(CancellationToken.None);
        Assert.True(stats.PendingCount < 200);
    }

    [Fact]
    public async Task Duplicate_transfer_identity_returns_the_original_accept()
    {
        using var fx = new SpoolFixture();
        var first = await fx.Spool.EnqueueAsync(fx.Envelope(transferId: "same-key"), CancellationToken.None);
        var second = await fx.Spool.EnqueueAsync(fx.Envelope(transferId: "same-key"), CancellationToken.None);

        Assert.Equal(first.SpoolId, second.SpoolId);
        Assert.Equal(first.TransferId, second.TransferId);
        var stats = await fx.Spool.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.PendingCount);
    }
}
