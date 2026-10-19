using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// `queuey edge run` og `edge publish` med innstillingene en test eller en liten gateway trenger (AREYTECH 2026-10-19):
/// spool-grensen, som begge må lese fordi de skriver samme spool, taket på backoff, og feltet MQTT-dedup leser
/// produsentens id fra. En ugyldig verdi stopper kommandoen før daemonen starter, der operatøren ser.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class EdgeDaemonSettingsTests : IDisposable
{
    private static readonly Dictionary<string, string> SigningPair = new()
    {
        ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01EDGE", ["QUEUEY_SIGNING_SECRET"] = "s3cr3t"
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-edge-settings", Guid.NewGuid().ToString("N"));
    private string Spool => Path.Combine(_dir, "spool.db");

    public EdgeDaemonSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("--spool-max-mb", "0", "--spool-max-mb must be a whole number of megabytes, 1 or more")]
    [InlineData("--spool-max-mb", "-1", "--spool-max-mb must be a whole number of megabytes")]
    [InlineData("--spool-max-mb", "1.5", "--spool-max-mb must be a whole number of megabytes")]
    [InlineData("--spool-max-mb", "lots", "--spool-max-mb must be a whole number of megabytes")]
    [InlineData("QUEUEY_SPOOL_MAX_MB", "0", "QUEUEY_SPOOL_MAX_MB must be a whole number of megabytes")]
    [InlineData("--backoff-cap-seconds", "0", "--backoff-cap-seconds must be a whole number of seconds from 1 to 3600")]
    [InlineData("--backoff-cap-seconds", "3601", "--backoff-cap-seconds must be a whole number of seconds from 1 to 3600")]
    [InlineData("--backoff-cap-seconds", "5m", "--backoff-cap-seconds must be a whole number of seconds")]
    [InlineData("QUEUEY_BACKOFF_CAP_SECONDS", "86400", "QUEUEY_BACKOFF_CAP_SECONDS must be a whole number of seconds from 1 to 3600")]
    public async Task Edge_run_refuses_an_invalid_limit_as_it_refuses_an_invalid_port(string setting, string value, string message)
    {
        CliRun run = await RunGuarded(Run(setting, value));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains(message, run.Stderr);
        Assert.Contains($"(got '{CliErrors.Shown(value)}')", run.Stderr); // shortened like any echoed argument
    }

    [Theory]
    [InlineData("--mqtt-id-field", "meta..id")]
    [InlineData("--mqtt-id-field", ".id")]
    [InlineData("QUEUEY_MQTT_ID_FIELD", "meta.")]
    public async Task Edge_run_refuses_an_id_field_with_an_empty_segment(string setting, string value)
    {
        var (args, env) = Run(setting, value);
        args.AddRange(new[] { "--mqtt", "localhost:1883", "--mqtt-routes", "site/+/telemetry=telemetry@1" });

        CliRun run = await RunGuarded((args, env));

        // Som de andre MQTT-valgene (--mqtt-retained): en konfigurasjonsfeil, ikke en stacktrace når verten starter.
        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("no empty segments", run.Stderr);
    }

    [Theory]
    [InlineData("--spool-max-mb", "0")]
    [InlineData("QUEUEY_SPOOL_MAX_MB", "nope")]
    public async Task Edge_publish_refuses_an_invalid_limit(string setting, string value)
    {
        var args = new List<string> { "edge", "publish", "telemetry", "--spool", Spool, "--tenant", "ten_1", "--data", "{}" };
        var env = new Dictionary<string, string>();
        if (setting.StartsWith("--", StringComparison.Ordinal)) args.AddRange(new[] { setting, value });
        else env[setting] = value;

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(args.ToArray()), env: env);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("must be a whole number of megabytes", run.Stderr);
        Assert.False(File.Exists(Spool), "a refused publish must not create the spool");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Edge_publish_holds_the_spool_to_the_limit_the_daemon_has(bool asFlag)
    {
        // 900 KB fits a default spool, not a 1 MB one (768 KB for events once the bookkeeping quarter is kept).
        string payload = "\"" + new string('x', 900 * 1024) + "\"";
        var args = new List<string> { "edge", "publish", "telemetry", "--spool", Spool, "--tenant", "ten_1", "--data", payload };
        var env = new Dictionary<string, string>();
        if (asFlag) args.AddRange(new[] { "--spool-max-mb", "1" });
        else env["QUEUEY_SPOOL_MAX_MB"] = "1";

        CliRun full = await CliHarness.RunAsync(() => CliEntry.RunAsync(args.ToArray()), env: env);
        CliRun unlimited = await CliHarness.RunAsync(() => CliEntry.RunAsync(
            new[] { "edge", "publish", "telemetry", "--spool", Spool, "--tenant", "ten_1", "--data", payload }));

        Assert.Equal(ExitCodes.RuntimeError, full.Exit);
        Assert.Contains("configured limit (1 MB)", full.Stderr);
        Assert.Equal(ExitCodes.Success, unlimited.Exit);
    }

    [Fact]
    public async Task Edge_publish_says_when_its_key_was_already_accepted()
    {
        string[] args = { "edge", "publish", "telemetry", "--spool", Spool, "--tenant", "ten_1", "--data", "{}", "--idempotency-key", "node-7:1", "--json" };

        CliRun first = await CliHarness.RunAsync(() => CliEntry.RunAsync(args));
        CliRun again = await CliHarness.RunAsync(() => CliEntry.RunAsync(args));

        Assert.Contains("\"alreadyAccepted\": false", first.Stdout);
        Assert.Contains("\"alreadyAccepted\": true", again.Stdout);
        Assert.Contains("\"pending\": 1", again.Stdout);
    }

    [Fact]
    public async Task The_flag_wins_over_the_environment_and_either_one_sets_the_value()
    {
        long? fromFlag = null, fromEnv = null, unset = null;
        TimeSpan? capFromFlag = null, capFromEnv = null, capUnset = null;
        var env = new Dictionary<string, string> { ["QUEUEY_SPOOL_MAX_MB"] = "64", ["QUEUEY_BACKOFF_CAP_SECONDS"] = "10" };

        await CliHarness.RunAsync(() =>
        {
            Assert.True(EdgeCommand.TryReadSpoolMaxBytes(Map("--spool-max-mb", "2"), out fromFlag, out _));
            Assert.True(EdgeCommand.TryReadSpoolMaxBytes(Map(), out fromEnv, out _));
            Assert.True(EdgeCommand.TryReadBackoffCap(Map("--backoff-cap-seconds", "30"), out capFromFlag, out _));
            Assert.True(EdgeCommand.TryReadBackoffCap(Map(), out capFromEnv, out _));
            return Task.FromResult(0);
        }, env: env);
        await CliHarness.RunAsync(() =>
        {
            Assert.True(EdgeCommand.TryReadSpoolMaxBytes(Map(), out unset, out _));
            Assert.True(EdgeCommand.TryReadBackoffCap(Map(), out capUnset, out _));
            return Task.FromResult(0);
        });

        Assert.Equal(2L * 1024 * 1024, fromFlag);
        Assert.Equal(64L * 1024 * 1024, fromEnv);
        Assert.Null(unset);
        Assert.Equal(TimeSpan.FromSeconds(30), capFromFlag);
        Assert.Equal(TimeSpan.FromSeconds(10), capFromEnv);
        Assert.Null(capUnset);
    }

    private static ArgMap Map(params string[] args)
    {
        Assert.True(EdgeCommand.Verbs["run"].TryParse(args, out ArgMap map, out _));
        return map;
    }

    private (List<string> Args, Dictionary<string, string> Env) Run(string setting, string value)
    {
        var args = new List<string> { "edge", "run", "--spool", Spool, "--tenant", "ten_1" };
        var env = new Dictionary<string, string>(SigningPair);
        if (setting.StartsWith("--", StringComparison.Ordinal)) args.AddRange(new[] { setting, value });
        else env[setting] = value;
        return (args, env);
    }

    private static async Task<CliRun> RunGuarded((List<string> Args, Dictionary<string, string> Env) command)
    {
        // A value that slipped through would start the daemon, which runs until stopped: fail instead of hanging.
        Task<CliRun> running = CliHarness.RunAsync(() => CliEntry.RunAsync(command.Args.ToArray()), env: command.Env);
        Assert.Same(running, await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(30))));
        return await running;
    }
}
