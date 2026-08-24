using Microsoft.Extensions.Hosting;
using Queuey.Edge;

namespace Queuey.Edge.Demo;

/// <summary>
/// A one-line health readout so the audience can SEE the promise: pending
/// counts climbing while Queuey is down, then draining to zero — with the
/// application never doing anything but PublishAsync. Reads the same
/// IQueueyEdgeHealth any monitoring system would.
/// </summary>
public sealed class ConsoleDashboard : BackgroundService
{
    private readonly IQueueyEdgeHealth _health;
    private EdgeState? _lastState;

    public ConsoleDashboard(IQueueyEdgeHealth health) => _health = health;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine();
        Console.WriteLine("Queuey Edge demo — the app only ever calls PublishAsync. Everything below is Queuey.");
        Console.WriteLine("Try: stop Queuey (or pull the network) → watch pending climb → start it → watch the drain.");
        Console.WriteLine();

        while (!stoppingToken.IsCancellationRequested)
        {
            var h = _health.Snapshot();

            if (h.State != _lastState)
            {
                _lastState = h.State;
                Console.WriteLine();
                Console.WriteLine($"── {Narrate(h)} ──");
            }

            var contact = h.LastSuccessfulCloudContact is { } at
                ? $"{(DateTimeOffset.UtcNow - at).TotalSeconds:F0}s ago"
                : "never";
            var oldest = h.OldestPendingAge is { } age ? $"{age.TotalSeconds:F0}s" : "-";
            var failure = h.LastTransferFailure is { } f ? $"{f.Reason}({f.StatusCode?.ToString() ?? "-"})" : "-";

            Console.Write(
                $"\r[{h.State}] pending={h.PendingCount} oldest={oldest} quarantined={h.QuarantinedCount} " +
                $"cloud-contact={contact} last-failure={failure}   ");

            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    private static string Narrate(EdgeHealth h) => h.State switch
    {
        EdgeState.Healthy => "Healthy — everything accepted has reached Queuey Cloud",
        EdgeState.Backlogged => "Backlogged — events are accumulating DURABLY; nothing is lost, the app never noticed",
        EdgeState.RequiresAction => $"Requires action — {h.LastTransferFailure?.Reason}: events retained, probing until an operator fixes it",
        EdgeState.StorageFull => "Storage full — publishes now refuse honestly instead of silently dropping",
        EdgeState.StorageFaulted => "Storage faulted — halted; 'queuey edge recover' or 'reset --accept-data-loss'",
        _ => h.State.ToString()
    };
}
