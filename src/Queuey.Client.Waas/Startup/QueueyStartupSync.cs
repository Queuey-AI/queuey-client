using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Queuey.Client.Waas;

/// <summary>
/// Runs <c>SyncStreams</c> once while the host starts, and lets the failure through so the host
/// aborts. A producer that could not converge its streams cannot publish to them either — starting
/// anyway just moves the failure to the first customer-facing request.
/// </summary>
/// <remarks>
/// Registered by <c>IQueueyBuilder.SyncOnStartup()</c>. Names are already validated when
/// <c>AddQueuey</c> builds the registry — before any host is built and without touching the network —
/// so by the time this runs, only server-side failures remain.
/// </remarks>
internal sealed class QueueyStartupSync : IHostedService
{
    private readonly IQueueyService _queuey;
    private readonly SyncOptions _options;

    public QueueyStartupSync(IQueueyService queuey, SyncOptions options)
    {
        _queuey = queuey ?? throw new ArgumentNullException(nameof(queuey));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Applies every registered stream. Any failure propagates: the .NET host treats an exception from
    /// <see cref="IHostedService.StartAsync"/> as fatal and stops starting.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
        => _queuey.SyncStreamsAsync(_options, cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
