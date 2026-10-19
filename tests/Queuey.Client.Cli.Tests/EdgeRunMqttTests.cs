using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// `queuey edge run --mqtt …`: et ugyldig valg for retained-kopier stopper daemonen ved oppstart, der operatøren ser,
/// i stedet for å gjette en modus (2026-10-16).
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class EdgeRunMqttTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-edge-run-mqtt", Guid.NewGuid().ToString("N"));

    public EdgeRunMqttTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("--mqtt-retained", "sometimes")]
    [InlineData(null, "sometimes")]
    public async Task An_unknown_retained_mode_is_refused_at_startup(string? flag, string value)
    {
        string spool = Path.Combine(_dir, "spool.db");
        var args = new List<string> { "edge", "run", "--spool", spool, "--tenant", "ten_1", "--mqtt", "localhost:1883", "--mqtt-routes", "site/+/telemetry=telemetry@1" };
        var env = new Dictionary<string, string> { ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01EDGE", ["QUEUEY_SIGNING_SECRET"] = "s3cr3t" };
        if (flag is null) env["QUEUEY_MQTT_RETAINED"] = value;
        else args.AddRange(new[] { flag, value });

        // A mode that slipped through would start the daemon, which runs until stopped: fail instead of hanging.
        Task<CliRun> running = CliHarness.RunAsync(() => CliEntry.RunAsync(args.ToArray()), env: env);
        Assert.Same(running, await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(30))));
        CliRun run = await running;

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("must be first, skip or all", run.Stderr);
    }
}
