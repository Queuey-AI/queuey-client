using System.Security.Cryptography;
using System.Text;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Spool;

/// <summary>
/// What the spool file holds, as the file holds it: a settled row keeps its
/// identity and loses its payload; freed pages are overwritten; the file is
/// owner-only; an encrypted row is ciphertext on disk and plaintext to the
/// transfer loop; a row this key cannot open is quarantined, and the lane
/// moves on.
/// </summary>
public class SpoolAtRestTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly byte[] Card = Encoding.UTF8.GetBytes("{\"card\":\"4111 1111 1111 1111\"}");

    private static byte[] Key() => RandomNumberGenerator.GetBytes(SpoolPayloadProtection.KeyBytes);

    [Fact]
    public async Task Settling_blanks_the_payload_but_keeps_the_row_for_correlation()
    {
        using var fx = new SpoolFixture();
        var accepted = await fx.Spool.EnqueueAsync(fx.Envelope() with { Payload = Card }, CancellationToken.None);
        await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None);

        await fx.Spool.SettleAsync(accepted.SpoolId, new CloudAck(CloudEventId: "evt_cloud_1", Replayed: false, AtUtc: fx.Clock.UtcNow), CancellationToken.None);

        var row = fx.Spool.InspectRow(accepted.SpoolId);
        Assert.Equal("Transferred", row.State);
        Assert.Empty(row.Payload);
    }

    [Fact]
    public void Freed_pages_are_overwritten()
    {
        using var fx = new SpoolFixture();

        Assert.Equal(1, fx.Spool.InspectSecureDelete());
    }

    [Fact]
    public async Task The_spool_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fx = new SpoolFixture();

        await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fx.Options.Path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fx.Directory));
    }

    [Fact]
    public async Task An_encrypted_spool_holds_ciphertext_and_hands_the_loop_plaintext()
    {
        var key = Key();
        using var fx = new SpoolFixture(o => o.PayloadKey = key);

        var accepted = await fx.Spool.EnqueueAsync(fx.Envelope() with { Payload = Card }, CancellationToken.None);

        var row = fx.Spool.InspectRow(accepted.SpoolId);
        Assert.Equal(SpoolPayloadProtection.SchemeAesGcmV1, row.Scheme);
        Assert.DoesNotContain("4111", Encoding.Latin1.GetString(row.Payload));

        var claimed = Assert.Single(await fx.Spool.ClaimReadyAsync(10, Lease, CancellationToken.None));
        Assert.Equal(Card, claimed.Envelope.Payload);
    }

    [Fact]
    public async Task Rows_written_before_the_key_keep_draining_after_it_is_set()
    {
        using var fx = new SpoolFixture();
        var plain = await fx.Spool.EnqueueAsync(fx.Envelope() with { Payload = Card }, CancellationToken.None);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var withKey = new SqliteEventSpool(new EdgeStorageOptions { Path = fx.Options.Path, PayloadKey = Key() }, fx.Clock);
        var claimed = Assert.Single(await withKey.ClaimReadyAsync(10, Lease, CancellationToken.None));

        Assert.Equal(plain.SpoolId, claimed.SpoolId);
        Assert.Equal(Card, claimed.Envelope.Payload);
    }

    [Fact]
    public async Task A_row_the_configured_key_cannot_open_is_quarantined_and_the_lane_moves_on()
    {
        using var fx = new SpoolFixture(o => o.PayloadKey = Key());
        var sealedWithOldKey = await fx.Spool.EnqueueAsync(fx.Envelope(groupKey: "laneA") with { Payload = Card }, CancellationToken.None);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var rotated = new SqliteEventSpool(new EdgeStorageOptions { Path = fx.Options.Path, PayloadKey = Key() }, fx.Clock);
        var readable = await rotated.EnqueueAsync(fx.Envelope(groupKey: "laneA"), CancellationToken.None);

        // The unreadable row is the lane HEAD: this claim parks it (step-aside),
        // and the lane continues on the next claim — the same two-step every
        // quarantine takes, so nothing overtakes inside a lane.
        Assert.Empty(await rotated.ClaimReadyAsync(10, Lease, CancellationToken.None));
        Assert.Equal("Quarantined", rotated.InspectRow(sealedWithOldKey.SpoolId).State);
        Assert.Equal(1, (await rotated.GetStatsAsync(CancellationToken.None)).QuarantinedCount);

        var claimed = Assert.Single(await rotated.ClaimReadyAsync(10, Lease, CancellationToken.None));
        Assert.Equal(readable.SpoolId, claimed.SpoolId);
    }
}
