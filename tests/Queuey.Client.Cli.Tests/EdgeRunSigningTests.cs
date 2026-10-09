using Queuey.Client.Cli;
using Queuey.Edge;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// `queuey edge run` leser nøklene som SDK-en (Edge-signering, Kenneth 2026-10-09): signeringsparet fra miljøet, og
/// QUEUEY_API_KEY bare uten paret. Hemmeligheten har ingen flagg, siden argv er synlig for alle på maskinen.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class EdgeRunSigningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-edge-run-signing", Guid.NewGuid().ToString("N"));

    public EdgeRunSigningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static async Task<QueueyEdgeOptions> Credentials(Dictionary<string, string> env, params string[] args)
    {
        QueueyEdgeOptions? read = null;
        await CliHarness.RunAsync(() =>
        {
            read = EdgeCommand.EdgeCredentials(ArgMap.Parse(args, new HashSet<string>()));
            return Task.FromResult(0);
        }, env: env);
        return read!;
    }

    [Fact]
    public async Task The_signing_pair_in_the_environment_is_read_and_then_QUEUEY_API_KEY_is_not()
    {
        QueueyEdgeOptions read = await Credentials(new()
        {
            ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01EDGE", ["QUEUEY_SIGNING_SECRET"] = "s3cr3t", ["QUEUEY_API_KEY"] = "qak_id.licensewide",
        });

        Assert.Equal("hsk_01EDGE", read.SigningKeyId);
        Assert.Equal("s3cr3t", read.SigningSecret);
        Assert.Null(read.ApiKey);
    }

    [Fact]
    public async Task The_health_key_comes_from_its_own_variable_and_leaves_the_api_key_unset()
    {
        // Security-review av #70 (B1): helse-nøkkelen står i edge.env, aldri i argv, og bare innsjekken bruker den.
        QueueyEdgeOptions read = await Credentials(new()
        {
            ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01EDGE", ["QUEUEY_SIGNING_SECRET"] = "s3cr3t", ["QUEUEY_EDGE_HEALTH_API_KEY"] = "qak_h.healthonly",
        });

        Assert.Equal("hsk_01EDGE", read.SigningKeyId);
        Assert.Equal("qak_h.healthonly", read.Health.ApiKey);
        Assert.Null(read.ApiKey);
    }

    [Fact]
    public void No_edge_text_puts_a_key_in_argv_and_the_health_key_is_named()
    {
        string text = Usage.Text.Replace("\r\n", "\n");
        int start = text.IndexOf("\nEDGE\n", StringComparison.Ordinal);
        string edge = text.Substring(start, text.IndexOf("\n  queuey edge publish", start, StringComparison.Ordinal) - start);

        Assert.DoesNotContain("--api-key <qak", edge);
        Assert.DoesNotContain("--mqtt-password <", edge); // K-c: passordet bare som QUEUEY_MQTT_PASSWORD
        Assert.Contains("only as QUEUEY_MQTT_PASSWORD, never in argv", edge);
        Assert.DoesNotContain("with the pair it serves only", edge);
        Assert.Contains("QUEUEY_EDGE_HEALTH_API_KEY", edge);
        Assert.Contains("argv is visible to every user", edge);
    }

    [Fact]
    public async Task An_api_key_flag_is_kept_beside_the_pair_for_the_health_check_in()
    {
        QueueyEdgeOptions read = await Credentials(new()
        {
            ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01EDGE", ["QUEUEY_SIGNING_SECRET"] = "s3cr3t",
        }, "--api-key", "qak_id.publishonly");

        Assert.Equal("hsk_01EDGE", read.SigningKeyId);
        Assert.Equal("qak_id.publishonly", read.ApiKey);
    }

    [Fact]
    public async Task Without_the_pair_QUEUEY_API_KEY_works_as_before()
    {
        QueueyEdgeOptions read = await Credentials(new() { ["QUEUEY_API_KEY"] = "qak_id.publishonly" });

        Assert.Equal("qak_id.publishonly", read.ApiKey);
        Assert.Null(read.SigningKeyId);
    }

    [Fact]
    public async Task Without_any_key_edge_run_names_the_signing_pair_first_and_opens_no_spool()
    {
        string spool = Path.Combine(_dir, "spool.db");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "edge", "run", "--spool", spool, "--tenant", "ten_1" }));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET", run.Stderr);
        Assert.Contains("queuey keys mint --write", run.Stderr);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public async Task An_api_key_flag_is_warned_about_and_the_refusal_never_suggests_it()
    {
        // Security-review av #70 (K-a).
        string spool = Path.Combine(_dir, "spool.db");

        CliRun flagged = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "edge", "run", "--spool", spool, "--tenant", "ten_1", "--api-key", "qak_id.publishonly" }),
            env: new Dictionary<string, string> { ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01EDGE" });
        CliRun none = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "edge", "run", "--spool", spool, "--tenant", "ten_1" }));

        Assert.Contains("argv is visible to every user on this machine; move it to QUEUEY_API_KEY or QUEUEY_EDGE_HEALTH_API_KEY in edge.env", flagged.Stderr);
        Assert.DoesNotContain("qak_id.publishonly", flagged.Stdout + flagged.Stderr);
        Assert.DoesNotContain("--api-key", none.Stderr);
        Assert.DoesNotContain("argv is visible", none.Stderr);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public async Task Half_a_pair_is_refused()
    {
        string spool = Path.Combine(_dir, "spool.db");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "edge", "run", "--spool", spool, "--tenant", "ten_1" }),
            env: new Dictionary<string, string> { ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01EDGE" });

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("go together", run.Stderr);
        Assert.False(File.Exists(spool));
    }
}
