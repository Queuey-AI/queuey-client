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
