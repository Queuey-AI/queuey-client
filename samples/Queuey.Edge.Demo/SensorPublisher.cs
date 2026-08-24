using Microsoft.Extensions.Hosting;
using Queuey.Client;
using Queuey.Edge;

namespace Queuey.Edge.Demo;

/// <summary>
/// The whole application, as far as delivery is concerned. Note what is NOT
/// here: no connectivity check, no local queue, no retry, no backoff, no
/// "is Queuey up?". One call transfers the operational delivery problem to
/// Queuey — the north-star test, as running code.
/// </summary>
public sealed class SensorPublisher : BackgroundService
{
    private readonly IQueueyPublisher _queuey;
    private readonly string _queue =
        Environment.GetEnvironmentVariable("QUEUEY_DEMO_QUEUE") ?? "sensor-readings";
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("QUEUEY_DEMO_INTERVAL"), out var s) ? s : 5);

    private long _sequence;

    public SensorPublisher(IQueueyPublisher queuey) => _queuey = queuey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var deviceId = $"unit-{Environment.MachineName.ToLowerInvariant()}";
        var random = new Random();
        var temperature = 21.0;

        while (!stoppingToken.IsCancellationRequested)
        {
            temperature += (random.NextDouble() - 0.5) * 0.4; // a gentle drift
            var reading = new
            {
                deviceId,
                sequence = Interlocked.Increment(ref _sequence),
                temperatureC = Math.Round(temperature, 2),
                humidity = Math.Round(40 + random.NextDouble() * 10, 1),
                readAtUtc = DateTimeOffset.UtcNow
            };

            try
            {
                // ── This line is the product. ────────────────────────────
                await _queuey.PublishAsync(_queue, reading, new PublishOptions
                {
                    EventType = "temperature.updated",
                    GroupKey = deviceId,                       // = the ordering lane
                    OccurredAtUtc = reading.readAtUtc          // honest history when draining a backlog
                }, stoppingToken);
                // ─────────────────────────────────────────────────────────
            }
            catch (QueueySpoolFullException ex)
            {
                // The one honest pre-custody refusal: storage is provisioned
                // by the operator, and Queuey never silently drops instead.
                Console.WriteLine($"!! spool full — reading NOT accepted: {ex.Message}");
            }
            catch (QueueyStorageFaultedException ex)
            {
                Console.WriteLine($"!! storage faulted — operator action needed: {ex.Message}");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}
