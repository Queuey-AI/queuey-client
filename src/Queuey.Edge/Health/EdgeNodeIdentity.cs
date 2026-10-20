using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Queuey.Edge;

/// <summary>
/// The node's identity, in one place for everything on the node that needs it: the health check-in, which Queuey Cloud
/// lists the node by, and an intake that names itself after the node, such as the MQTT source's broker client id.
/// <c>AddQueueyEdge</c> registers one per host.
/// <para>
/// The identity is the configured <see cref="EdgeHealthReportOptions.NodeId"/>, which is written to the spool in place
/// of any other; without one, the id in the spool's <c>edge_meta</c>; and without that, a UUIDv7 minted once and
/// written there. It lives as long as the spool file does, so a new spool is a new node unless the id is configured.
/// </para>
/// </summary>
public sealed class EdgeNodeIdentity
{
    private readonly QueueyEdgeOptions _options;
    private readonly IEventSpool _spool;
    private readonly IEdgeClock _clock;
    private readonly ILogger _logger;

    // To kilder kan spørre samtidig ved oppstart (helse og MQTT): uten porten kunne begge preget hver sin UUID.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _nodeId;

    internal EdgeNodeIdentity(QueueyEdgeOptions options, IEventSpool spool, IEdgeClock clock, ILogger logger)
    {
        _options = options;
        _spool = spool;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// The node's id. The first call reads the spool, and writes to it when the id is configured or new; later calls
    /// return the same id. Throws what the spool throws, such as <see cref="QueueyStorageFaultedException"/>; a later
    /// call tries again.
    /// </summary>
    public async Task<string> GetNodeIdAsync(CancellationToken cancellationToken)
    {
        if (_nodeId is { } known) return known;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _nodeId ??= await ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ResolveAsync(CancellationToken cancellationToken)
    {
        var existing = await _spool.GetMetaAsync(EdgeMetaKeys.NodeId, cancellationToken).ConfigureAwait(false);
        if (_options.Health.NodeId is { } configured)
        {
            if (!string.Equals(existing, configured, StringComparison.Ordinal))
            {
                await _spool.SetMetaAsync(EdgeMetaKeys.NodeId, configured, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(existing))
                    _logger.LogInformation(
                        "This node now goes by its configured id {NodeId} instead of {PreviousNodeId}; Queuey Cloud lists it as that node from its next report.",
                        configured, existing);
            }
            return configured;
        }
        if (string.IsNullOrWhiteSpace(existing))
        {
            existing = Uuid7.NewString(_clock.UtcNow);
            await _spool.SetMetaAsync(EdgeMetaKeys.NodeId, existing, cancellationToken).ConfigureAwait(false);
        }
        return existing!;
    }
}
