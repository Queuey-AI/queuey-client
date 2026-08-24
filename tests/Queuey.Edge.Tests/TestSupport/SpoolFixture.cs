using Queuey.Edge;

namespace Queuey.Edge.Tests.TestSupport;

/// <summary>A fresh spool in a per-test temp directory, cleaned on dispose.</summary>
public sealed class SpoolFixture : IDisposable
{
    public string Directory { get; }
    public EdgeStorageOptions Options { get; }
    public FakeClock Clock { get; } = new();
    public SqliteEventSpool Spool { get; }

    public SpoolFixture(Action<EdgeStorageOptions>? configure = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "queuey-edge-tests", Guid.NewGuid().ToString("N"));
        Options = new EdgeStorageOptions { Path = Path.Combine(Directory, "spool.db") };
        configure?.Invoke(Options);
        Spool = new SqliteEventSpool(Options, Clock);
    }

    public EventEnvelope Envelope(
        string queue = "orders",
        string? groupKey = null,
        string? transferId = null,
        int payloadBytes = 16) => new(
        Version: EventEnvelope.CurrentVersion,
        TransferId: transferId ?? Guid.NewGuid().ToString("N"),
        Queue: queue,
        TenantPublicId: "ten_test",
        ContentType: "application/json",
        Payload: new byte[payloadBytes],
        OccurredAtUtc: Clock.UtcNow,
        GroupKey: groupKey);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best effort */ }
    }
}
