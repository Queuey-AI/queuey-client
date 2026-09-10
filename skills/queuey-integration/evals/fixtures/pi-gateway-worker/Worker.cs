public sealed class Worker(IHttpClientFactory http, ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var reading = await ReadModbusAsync(ct);
            // Retry a few times in memory; LTE at some sites drops for hours,
            // and anything still unsent when the process restarts is lost.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var res = await http.CreateClient().PostAsJsonAsync("https://partner.example/readings", reading, ct);
                    if (res.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException ex) { log.LogWarning(ex, "send failed, attempt {A}", attempt); }
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private static Task<Reading> ReadModbusAsync(CancellationToken ct) =>
        Task.FromResult(new Reading("unit-7", Guid.NewGuid().ToString("N"), 21.5, DateTime.UtcNow));
}

public record Reading(string DeviceId, string SampleId, double Temperature, DateTime ReadAtUtc);
