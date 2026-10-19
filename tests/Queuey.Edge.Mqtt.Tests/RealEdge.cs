using Microsoft.Extensions.DependencyInjection;

namespace Queuey.Edge.Mqtt.Tests;

/// <summary>
/// Edge as <c>AddQueueyEdge</c> composes it — the real publisher over a real SQLite spool in a temp folder — without
/// starting the transfer loop, so every accepted event stays in the spool for the test to count.
/// </summary>
internal sealed class RealEdge : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dir;

    private RealEdge(ServiceProvider provider, string dir)
    {
        _provider = provider;
        _dir = dir;
    }

    public IQueueyPublisher Publisher => _provider.GetRequiredService<IQueueyPublisher>();

    public static RealEdge Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "queuey-edge-mqtt-real", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQueueyEdge(o =>
        {
            o.ApiKey = "qak_id.secret";
            o.TenantPublicId = "ten_test";
            o.Storage.Path = Path.Combine(dir, "spool.db");
        });
        return new RealEdge(services.BuildServiceProvider(), dir);
    }

    /// <summary>Events in the spool that wait for transfer.</summary>
    public async Task<long> PendingAsync()
        => (await _provider.GetRequiredService<IEventSpool>().GetStatsAsync(CancellationToken.None)).PendingCount;

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
