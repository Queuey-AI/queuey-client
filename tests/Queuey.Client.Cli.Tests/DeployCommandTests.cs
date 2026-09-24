using System.Text.Json;
using Queuey.Client.Cli;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Tests;

/// <summary>Tester som bytter Console.Out, kjøres ikke samtidig — utskriften ville blandes.</summary>
[CollectionDefinition(ConsoleCollection.Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}

/// <summary>
/// CLI-siden av ønsket tilstand (2026-09-23): skjemaet, dry-run som viser modus, retry og filter,
/// og verify som krever at du velger hva mottakeren får.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class DeployCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-deploy-cli-tests", Guid.NewGuid().ToString("N"));

    public DeployCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile(string json)
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Schema_prints_the_json_schema_without_credentials()
    {
        var (exit, output) = await Run(() => Task.FromResult(SchemaCommand.Run(Array.Empty<string>())));

        Assert.Equal(ExitCodes.Success, exit);
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal(DeploymentFile.SchemaUrl, doc.RootElement.GetProperty("$id").GetString());
        Assert.Contains("logOnly", output);
    }

    [Fact]
    public async Task A_dry_run_shows_mode_retry_and_filter()
    {
        string path = DeployFile("""
        { "queues": {
            "orders": { "mode": "deliver", "maxAttempts": 5, "backoff": { "jitter": "full" },
                        "filter": { "conditions": [ { "field": "type", "op": "eq", "value": "order.created" } ] } },
            "audit": {} } }
        """);

        var (exit, output) = await Run(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("orders\tmode=deliver inherits the workspace maxAttempts=5 backoff.jitter=full filter=(all: type eq order.created)", output);
        Assert.Contains("audit\tmode=(deliver when it has a destination, if new)", output);
    }

    [Fact]
    public async Task A_dry_run_as_json_carries_the_mode_and_the_filter()
    {
        string path = DeployFile("""{ "queues": { "orders": { "mode": "logOnly", "filter": { "match": "any", "conditions": [] } } } }""");

        var (exit, output) = await Run(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run", "--json" }));

        Assert.Equal(ExitCodes.Success, exit);
        JsonElement plan = JsonDocument.Parse(output).RootElement[0];
        Assert.Equal("logOnly", plan.GetProperty("mode").GetString());
        Assert.Equal("any", plan.GetProperty("policy").GetProperty("filter").GetProperty("match").GetString());
    }

    [Fact]
    public async Task A_dry_run_fails_on_a_mode_the_file_cannot_set()
    {
        string path = DeployFile("""{ "queues": { "orders": { "mode": "paused" } } }""");

        var ex = await Assert.ThrowsAsync<Queuey.Client.QueueyConfigurationException>(
            () => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));
        Assert.Contains("not a mode a deployment file sets", ex.Message);
    }

    [Fact]
    public async Task Verify_needs_a_queue_and_the_data_to_send()
    {
        var (noQueue, noQueueOut) = await Run(() => VerifyCommand.RunAsync(Array.Empty<string>()));
        Assert.Equal(ExitCodes.Usage, noQueue);
        Assert.Contains("verify requires <queue>", noQueueOut);

        // Ingen standard-payload: eventen går til den ekte mottakeren.
        var (noData, noDataOut) = await Run(() => VerifyCommand.RunAsync(new[] { "orders" }));
        Assert.Equal(ExitCodes.Usage, noData);
        Assert.Contains("treats as harmless", noDataOut);

        var (badTimeout, badTimeoutOut) = await Run(() => VerifyCommand.RunAsync(new[] { "orders", "--data", "{}", "--timeout", "0" }));
        Assert.Equal(ExitCodes.Usage, badTimeout);
        Assert.Contains("--timeout takes whole seconds", badTimeoutOut);
    }

    [Fact]
    public void The_tenant_verify_uses_is_the_one_the_file_names()
    {
        // Samme workspace som apply skrev til; bare tenant ekspanderes, så en annen ${VAR} som
        // mangler i skallet der verify kjøres, spiller ingen rolle.
        DeploymentFile file = DeploymentFile.Parse("""
        { "tenant": "${QUEUEY_TENANT_FOR_TEST}", "queues": { "orders": { "delivery": { "url": "${UNSET_IN_THIS_SHELL}" } } } }
        """);

        Assert.Equal("ten_file", file.ResolveTenant(name => name == "QUEUEY_TENANT_FOR_TEST" ? "ten_file" : null));
    }

    private static async Task<(int Exit, string Output)> Run(Func<Task<int>> command)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        TextWriter originalOut = Console.Out, originalErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int exit = await command();
            return (exit, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
