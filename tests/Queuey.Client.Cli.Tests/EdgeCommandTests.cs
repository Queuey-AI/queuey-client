using System.Text;
using Queuey.Client.Cli;
using Queuey.Edge;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// The operator verbs against a real spool file. The rules under test are
/// the deletion rules: discard reaches only quarantined rows, reset demands
/// the explicit acknowledgement, and both preserve the old file.
/// </summary>
[Collection(ConsoleCollection.Name)]
public class EdgeCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _spoolPath;
    private readonly SqliteEventSpool _spool;

    public EdgeCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "queuey-edge-cli-tests", Guid.NewGuid().ToString("N"));
        _spoolPath = Path.Combine(_dir, "spool.db");
        _spool = new SqliteEventSpool(new EdgeStorageOptions { Path = _spoolPath }, new Clock());
    }

    [Fact]
    public async Task Status_reports_pending_and_quarantined()
    {
        await Seed(pending: 2, quarantined: 1);

        var (exit, output) = await Run("status", "--spool", _spoolPath, "--json");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("\"pending\": 2", output);
        Assert.Contains("\"quarantined\": 1", output);
        Assert.Contains("PayloadTooLarge", output);
    }

    [Fact]
    public async Task Retry_returns_a_quarantined_event_to_the_drain()
    {
        var quarantinedId = await Seed(pending: 0, quarantined: 1);

        var (exit, _) = await Run("retry", "--spool", _spoolPath, "--id", quarantinedId.ToString());

        Assert.Equal(ExitCodes.Success, exit);
        var stats = await _spool.GetStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.PendingCount);
        Assert.Equal(0, stats.QuarantinedCount);
    }

    [Fact]
    public async Task Discard_reaches_only_quarantined_rows()
    {
        var quarantinedId = await Seed(pending: 1, quarantined: 1);
        var pendingId = quarantinedId - 1; // seeded first

        var (okExit, okOut) = await Run("discard", "--spool", _spoolPath, "--id", quarantinedId.ToString());
        var (failExit, _) = await Run("discard", "--spool", _spoolPath, "--id", pendingId.ToString());

        Assert.Equal(ExitCodes.Success, okExit);
        Assert.Contains("DISCARDED", okOut);
        Assert.NotEqual(ExitCodes.Success, failExit);
        Assert.Equal(1, (await _spool.GetStatsAsync(CancellationToken.None)).PendingCount);
    }

    [Fact]
    public async Task Reset_demands_the_explicit_acknowledgement_and_preserves_the_file()
    {
        await Seed(pending: 1, quarantined: 0);

        var (refused, refusedOut) = await Run("reset", "--spool", _spoolPath);
        Assert.NotEqual(ExitCodes.Success, refused);
        Assert.Contains("--accept-data-loss", refusedOut);
        Assert.True(File.Exists(_spoolPath), "a refused reset must not touch the spool");

        var (accepted, acceptedOut) = await Run("reset", "--spool", _spoolPath, "--accept-data-loss");
        Assert.Equal(ExitCodes.Success, accepted);
        Assert.False(File.Exists(_spoolPath));
        Assert.Contains("preserved", acceptedOut);
        Assert.Single(Directory.GetFiles(_dir, "spool.db.faulted-*"));
    }

    [Fact]
    public async Task Publish_durably_enqueues_with_full_envelope()
    {
        var (exit, output) = await Run("publish", "sensor-readings",
            "--spool", _spoolPath, "--tenant", "ten_cli",
            "--data", """{"temp":21.5}""",
            "--event-type", "temperature.updated",
            "--group-key", "unit-7",
            "--occurred-at", "2026-08-20T09:14:02Z",
            "--json");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("transferId", output);

        // The row is durably in the file with the whole envelope intact —
        // exactly what a co-resident Edge host's transfer loop will claim.
        var claims = await _spool.ClaimReadyAsync(1, TimeSpan.FromMinutes(1), CancellationToken.None);
        var envelope = Assert.Single(claims).Envelope;
        Assert.Equal("sensor-readings", envelope.Queue);
        Assert.Equal("ten_cli", envelope.TenantPublicId);
        Assert.Equal("temperature.updated", envelope.EventType);
        Assert.Equal("unit-7", envelope.GroupKey);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 9, 14, 2, TimeSpan.Zero), envelope.OccurredAtUtc);
        Assert.Matches("^[0-9a-f-]{36}$", envelope.TransferId);
    }

    [Fact]
    public async Task Publish_with_idempotency_key_dedupes_locally()
    {
        var first = await Run("publish", "orders", "--spool", _spoolPath, "--tenant", "ten_cli",
            "--data", "{}", "--idempotency-key", "order-1", "--json");
        var second = await Run("publish", "orders", "--spool", _spoolPath, "--tenant", "ten_cli",
            "--data", "{}", "--idempotency-key", "order-1", "--json");

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Equal(ExitCodes.Success, second.ExitCode);
        Assert.Equal(1, (await _spool.GetStatsAsync(CancellationToken.None)).PendingCount);
    }

    [Fact]
    public async Task Publish_requires_queue_tenant_and_payload()
    {
        var noQueue = await Run("publish", "--spool", _spoolPath, "--tenant", "ten_cli", "--data", "{}");
        var noTenant = await Run("publish", "orders", "--spool", _spoolPath, "--data", "{}");
        var noPayload = await Run("publish", "orders", "--spool", _spoolPath, "--tenant", "ten_cli");

        Assert.Equal(ExitCodes.Usage, noQueue.ExitCode);
        Assert.Equal(ExitCodes.Usage, noTenant.ExitCode);
        Assert.Equal(ExitCodes.Usage, noPayload.ExitCode);
    }

    [Fact]
    public async Task Drain_succeeds_on_empty_and_times_out_with_pending()
    {
        var empty = await Run("drain", "--spool", _spoolPath, "--timeout", "1");
        Assert.Equal(ExitCodes.Success, empty.ExitCode);

        await Seed(pending: 1, quarantined: 0);
        var stuck = await Run("drain", "--spool", _spoolPath, "--timeout", "1");

        // No Edge host is running in the test — the backlog cannot empty,
        // and the verb must say so instead of pretending.
        Assert.NotEqual(ExitCodes.Success, stuck.ExitCode);
        Assert.Contains("RUNNING", stuck.Output);
        Assert.Contains("Do not delete the spool", stuck.Output);
    }

    [Fact]
    public async Task Recover_salvages_owned_rows_into_a_fresh_spool()
    {
        await Seed(pending: 2, quarantined: 1);

        var (exit, output) = await Run("recover", "--spool", _spoolPath);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Salvaged 3 event(s)", output);
        Assert.Single(Directory.GetFiles(_dir, "spool.db.faulted-*"));

        // The fresh spool is fully operational and still owns the events.
        var reopened = new SqliteEventSpool(new EdgeStorageOptions { Path = _spoolPath }, new Clock());
        var stats = await reopened.GetStatsAsync(CancellationToken.None);
        Assert.Equal(2, stats.PendingCount);
        Assert.Equal(1, stats.QuarantinedCount);
    }

    // ── plumbing ────────────────────────────────────────────────────────

    private async Task<long> Seed(int pending, int quarantined)
    {
        long lastId = 0;
        for (var i = 0; i < pending; i++)
        {
            var accept = await _spool.EnqueueAsync(Envelope($"pending-{i}"), CancellationToken.None);
            lastId = accept.SpoolId;
        }
        for (var i = 0; i < quarantined; i++)
        {
            var accept = await _spool.EnqueueAsync(Envelope($"poison-{i}", groupKey: $"q{i}"), CancellationToken.None);
            await _spool.ClaimReadyAsync(50, TimeSpan.FromMinutes(1), CancellationToken.None);
            await _spool.QuarantineAsync(accept.SpoolId,
                new TransferOutcome(TransferClass.EventRejected, TransferReason.PayloadTooLarge,
                    TransferEvidence.Create(413, "too large", DateTimeOffset.UtcNow, 1)),
                CancellationToken.None);
            lastId = accept.SpoolId;
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return lastId;
    }

    private static EventEnvelope Envelope(string transferId, string? groupKey = null) => new(
        Version: EventEnvelope.CurrentVersion,
        TransferId: transferId,
        Queue: "orders",
        TenantPublicId: "ten_test",
        ContentType: "application/json",
        Payload: [1, 2, 3],
        OccurredAtUtc: DateTimeOffset.UtcNow,
        GroupKey: groupKey);

    private static async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await EdgeCommand.RunAsync(args);
            return (exit, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Clock : IEdgeClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long MonotonicMilliseconds => Environment.TickCount64;
    }
}
