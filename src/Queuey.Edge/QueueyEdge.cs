using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Queuey.Client;

namespace Queuey.Edge;

/// <summary>
/// Queuey Edge without a host. Apps built on the generic host register with
/// <c>services.AddQueueyEdge(...)</c> and let the host run the transfer loop;
/// a script, a console tool or a LINQPad query has no host, and this is the
/// same thing in one call:
/// <code>
/// await using var edge = await QueueyEdge.StartAsync(o =>
/// {
///     o.ApiKey = "...";            // publish-only, tenant-scoped Edge key
///     o.TenantPublicId = "ten_...";
///     o.Health.ReportToCloud = true;
/// });
/// await edge.PublishAsync("orders", payload);
/// </code>
/// Disposing stops the transfer loop and the health reporter in the order a
/// host would — the node's last word to Cloud is whatever it reported last,
/// and Cloud notices the silence from there.
/// </summary>
public static class QueueyEdge
{
    /// <summary>
    /// Builds a self-contained Edge node and starts its background work:
    /// the transfer loop, the health service, the optional local endpoint
    /// and the optional Cloud health reporter. Backoff left over from a
    /// previous run is collapsed first, so anything still in the spool is
    /// due immediately.
    /// </summary>
    /// <param name="configure">The node's options; <see cref="QueueyEdgeOptions.Validate"/> runs at build.</param>
    /// <param name="loggerFactory">Where Edge logs; null means no logging.</param>
    public static async Task<QueueyEdgeNode> StartAsync(
        Action<QueueyEdgeOptions> configure,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        var services = new ServiceCollection();
        if (loggerFactory is not null)
            services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddQueueyEdge(configure);

        var provider = services.BuildServiceProvider();
        var node = new QueueyEdgeNode(provider);
        try
        {
            await node.StartAsync(cancellationToken).ConfigureAwait(false);
            return node;
        }
        catch
        {
            await node.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// A running Edge node: publish into it, read its health, dispose it to
/// stop. It is the <see cref="IQueueyPublisher"/> the app would otherwise
/// resolve from DI, with the node's lifetime attached.
/// </summary>
public sealed class QueueyEdgeNode : IQueueyPublisher, IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<IHostedService> _hosted;
    private readonly IQueueyPublisher _publisher;
    private readonly IQueueyEdgeHealth _health;
    private bool _started;
    private bool _disposed;

    internal QueueyEdgeNode(ServiceProvider provider)
    {
        _provider = provider;
        _hosted = provider.GetServices<IHostedService>().ToList();
        _publisher = provider.GetRequiredService<IQueueyPublisher>();
        _health = provider.GetRequiredService<IQueueyEdgeHealth>();
    }

    /// <summary>The options the node runs with, after validation.</summary>
    public QueueyEdgeOptions Options => _provider.GetRequiredService<QueueyEdgeOptions>();

    /// <summary>The node's name as reported to Cloud (<see cref="EdgeHealthReportOptions.NodeName"/>, or the machine name).</summary>
    public string Name => string.IsNullOrWhiteSpace(Options.Health.NodeName)
        ? Environment.MachineName
        : Options.Health.NodeName!;

    /// <summary>A point-in-time snapshot of the node's health — the same thing <c>queuey edge status</c> prints.</summary>
    public EdgeHealth Health => _health.Snapshot();

    /// <inheritdoc />
    public Task<PublishReceipt> PublishAsync<T>(
        string queue, T payload, PublishOptions? options = null, CancellationToken cancellationToken = default)
        => _publisher.PublishAsync(queue, payload, options, cancellationToken);

    /// <inheritdoc />
    public Task<PublishReceipt> PublishAsync(
        string queue, byte[] payload, string contentType, PublishOptions? options = null, CancellationToken cancellationToken = default)
        => _publisher.PublishAsync(queue, payload, contentType, options, cancellationToken);

    /// <summary>
    /// "The problem is fixed — unlock now": collapses every backoff wait so
    /// pending events are due immediately. Order and quarantine are untouched.
    /// Returns how many events became due.
    /// </summary>
    public Task<int> KickAsync(CancellationToken cancellationToken = default)
        => _provider.GetRequiredService<IEventSpool>().KickAsync(cancellationToken);

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var h in _hosted)
            await h.StartAsync(cancellationToken).ConfigureAwait(false);
        _started = true;
        await KickAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the background work in reverse start order, then releases the spool.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_started)
        {
            foreach (var h in Enumerable.Reverse(_hosted))
                await h.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
