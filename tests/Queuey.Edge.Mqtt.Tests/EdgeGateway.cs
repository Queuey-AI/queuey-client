using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Queuey.Edge.Mqtt.Tests;

/// <summary>
/// One start of a gateway: Edge and its MQTT source as <c>AddQueueyEdge</c> and <c>AddQueueyEdgeMqttSource</c> compose
/// them, over a spool file that outlives the start, on a machine with the name given. A recreated container is a new
/// start on the same spool with a new machine name. Only the source starts, not the transfer loop, so accepted events
/// stay in the spool for the test to count.
/// </summary>
internal sealed class EdgeGateway : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private EdgeGateway(ServiceProvider provider, MqttSource source)
    {
        _provider = provider;
        Source = source;
    }

    public MqttSource Source { get; }

    public IEventSpool Spool => _provider.GetRequiredService<IEventSpool>();

    public static async Task<EdgeGateway> StartAsync(
        string spoolPath, string machineName, Action<MqttSourceOptions> mqtt, string? nodeId = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQueueyEdge(o =>
        {
            o.ApiKey = "qak_id.secret";
            o.TenantPublicId = "ten_test";
            o.Storage.Path = spoolPath;
            o.Health.NodeId = nodeId;
        });
        services.AddQueueyEdgeMqttSource(mqtt);
        var provider = services.BuildServiceProvider();

        var source = provider.GetServices<IHostedService>().OfType<MqttSource>().Single();
        source.MachineName = () => machineName;
        await source.StartAsync(CancellationToken.None);
        return new EdgeGateway(provider, source);
    }

    /// <summary>
    /// A spool as Edge 0.1.0-preview.13 and earlier left it: today's tables, but no <see cref="EdgeMetaKeys.CreatedUtc"/>,
    /// which only a newer Edge writes, and no kept MQTT client id.
    /// </summary>
    public static async Task WriteSpoolFromBeforeAsync(string spoolPath)
    {
        var services = new ServiceCollection();
        services.AddQueueyEdge(o =>
        {
            o.ApiKey = "qak_id.secret";
            o.TenantPublicId = "ten_test";
            o.Storage.Path = spoolPath;
        });
        await using (var provider = services.BuildServiceProvider())
            await provider.GetRequiredService<IEventSpool>().GetStatsAsync(CancellationToken.None);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={spoolPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM edge_meta WHERE k = $k;";
        cmd.Parameters.AddWithValue("$k", EdgeMetaKeys.CreatedUtc);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Events in the spool that wait for transfer.</summary>
    public async Task<long> PendingAsync() => (await Spool.GetStatsAsync(CancellationToken.None)).PendingCount;

    public async ValueTask DisposeAsync()
    {
        await Source.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

/// <summary>A folder for a spool that several gateway starts share, removed with the test.</summary>
internal sealed class SpoolFolder : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-edge-mqtt-gateway", Guid.NewGuid().ToString("N"));

    public SpoolFolder() => Directory.CreateDirectory(_dir);

    public string SpoolPath => Path.Combine(_dir, "spool.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
