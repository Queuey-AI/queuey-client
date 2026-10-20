using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Spool;

/// <summary>
/// A spool records when Edge created it, and only then: a file an older Edge wrote never gets the mark, because the
/// mark is how a newer Edge tells such a file apart (an MQTT intake keeps the client id an older Edge used).
/// </summary>
public class SpoolCreatedTests
{
    [Fact]
    public async Task A_new_spool_records_when_it_was_created_and_keeps_that_time()
    {
        using var fx = new SpoolFixture();
        var createdAt = fx.Clock.UtcNow;

        await fx.Spool.GetStatsAsync(CancellationToken.None);
        fx.Clock.Advance(TimeSpan.FromDays(3));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopened = new SqliteEventSpool(fx.Options, fx.Clock);

        Assert.Equal(createdAt, DateTimeOffset.Parse(
            (await reopened.GetMetaAsync(EdgeMetaKeys.CreatedUtc, CancellationToken.None))!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task First_opens_of_a_new_spool_that_race_all_succeed_and_record_one_creation_time()
    {
        // A daemon's first moments: the transfer loop and the MQTT intake open the new file at once (reads take no
        // lock), and an edge publish from another process may create it too. Each one bootstraps the empty file. The
        // window is narrow, so the race runs on many new files.
        for (var round = 0; round < 40; round++)
        {
            using var fx = new SpoolFixture();
            var other = new SqliteEventSpool(fx.Options, fx.Clock); // another process on the same file

            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => (i % 3) switch
            {
                0 => fx.Spool.GetStatsAsync(CancellationToken.None),
                1 => fx.Spool.GetMetaAsync(EdgeMetaKeys.MqttClientId, CancellationToken.None),
                _ => other.GetMetaAsync(EdgeMetaKeys.NodeId, CancellationToken.None)
            })));

            Assert.Equal(1L, V2SpoolFile.Scalar(fx.Options.Path, "SELECT COUNT(*) FROM edge_meta WHERE k = 'created_utc';"));
        }
    }

    [Fact]
    public async Task A_spool_an_older_Edge_wrote_is_upgraded_without_the_mark()
    {
        using var fx = new SpoolFixture();
        V2SpoolFile.Create(fx.Options.Path, new V2SpoolFile.Row("waiting", "Accepted", "2026-08-22T10:00:00.0000000Z"));

        await fx.Spool.GetStatsAsync(CancellationToken.None);

        Assert.Equal(3L, V2SpoolFile.Scalar(fx.Options.Path, "PRAGMA user_version;"));
        Assert.Null(await fx.Spool.GetMetaAsync(EdgeMetaKeys.CreatedUtc, CancellationToken.None));
    }
}
