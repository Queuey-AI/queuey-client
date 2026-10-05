using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Én regel for hvilket workspace apply, --check, --dry-run og verify treffer (review 2026-09-24). Før
/// vant fila over flagget i apply og flagget over fila i verify, så `verify --tenant X` kunne bevise
/// levering i et annet workspace enn `apply --tenant X` skrev til.
/// </summary>
public class DeploymentTenantRuleTests
{
    private static ResolvedConfig Resolve(string[] args, string? envTenant, string? jsonTenant, string? fileTenant)
    {
        ArgMap map = ArgMap.Parse(args, new HashSet<string>());
        Func<string, string?> env = name => name == "QUEUEY_TENANT" ? envTenant : null;
        string? json = jsonTenant is null ? null : JsonSerializer.Serialize(new { tenant = jsonTenant });
        return DeploymentTenant.Resolve(CliConfig.Resolve(map, env, json), map, env, fileTenant, "queuey.deploy.json");
    }

    [Theory]
    [InlineData(null, null, "ten_json", "ten_file", "ten_file")]       // queuey.json er en standard, fila vinner
    [InlineData("ten_file", null, null, "ten_file", "ten_file")]       // flagget sier det samme som fila
    [InlineData(null, "ten_file", null, "ten_file", "ten_file")]       // miljøet sier det samme som fila
    [InlineData("ten_flag", null, "ten_json", null, "ten_flag")]       // fila navngir ingen: vanlig rekkefølge
    [InlineData(null, "ten_env", "ten_json", null, "ten_env")]
    [InlineData("ten_file", "ten_env", null, "ten_file", "ten_file")]  // flagget går foran miljøet, og det er enig
    public void The_workspace_is_the_files_when_nothing_explicit_disagrees(
        string? flag, string? env, string? json, string? file, string expected)
    {
        string[] args = flag is null ? Array.Empty<string>() : new[] { "--tenant", flag };

        Assert.Equal(expected, Resolve(args, env, json, file).TenantPublicId);
    }

    [Theory]
    [InlineData("ten_flag", null, "--tenant names ten_flag")]
    [InlineData(null, "ten_env", "QUEUEY_TENANT names ten_env")]
    public void An_explicit_tenant_that_disagrees_with_the_file_fails_and_names_both(string? flag, string? env, string expected)
    {
        string[] args = flag is null ? Array.Empty<string>() : new[] { "--tenant", flag };

        var ex = Assert.Throws<Queuey.Client.QueueyConfigurationException>(() => Resolve(args, env, null, "ten_file"));

        Assert.Contains("queuey.deploy.json names workspace ten_file", ex.Message);
        Assert.Contains(expected, ex.Message);
    }
}

/// <summary>Regelen der den gjelder: i kommandoene, før noe sendes, og likt for apply og verify.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class DeploymentTenantCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-tenant-cli-tests", Guid.NewGuid().ToString("N"));

    public DeploymentTenantCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile(string? tenant)
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, tenant is null
            ? """{ "queues": { "orders": {} } }"""
            : $$"""{ "tenant": "{{tenant}}", "queues": { "orders": {} } }""");
        return path;
    }

    /// <summary>En server som godtar apply og leverer verify-eventen, i det workspacet den blir spurt om.</summary>
    private static RecordingHandler Server() => new(req => req switch
    {
        { Method.Method: "GET" } when req.Path.StartsWith("/tenants/", StringComparison.Ordinal)
            => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
        { Method.Method: "PUT", Path: "/queues" }
            => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = true, hasDeliveryTarget = false }),
        { Method.Method: "POST" } when req.Path.StartsWith("/events/", StringComparison.Ordinal)
            => RecordingHandler.Json(HttpStatusCode.Accepted, new { queuePublicId = "que_orders", eventId = "evt_1", receivedAtUtc = DateTimeOffset.UnixEpoch, mode = "Deliver", replayed = false }),
        { Method.Method: "GET", Path: "/events/que_orders/evt_1" }
            => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "evt_1", status = 2, attemptCount = 1, attempts = new[] { new { attemptNumber = 1, targetEndpoint = "https://hooks.example.com/orders", responseCode = 200, durationMs = 12 } } }),
        _ => throw new InvalidOperationException(req.Key),
    });

    private static Task<int> Apply(string path, params string[] extra)
        => ApplyCommand.RunAsync(CliHarness.With(new[] { "--file", path }.Concat(extra).ToArray()));

    private static Task<int> Verify(string path, params string[] extra)
        => VerifyCommand.RunAsync(CliHarness.With(new[] { "orders", "--data", "{}", "--deployment", path }.Concat(extra).ToArray()));

    public static TheoryData<string[], Dictionary<string, string>, string> Disagreements => new()
    {
        { new[] { "--tenant", "ten_flag" }, new Dictionary<string, string>(), "--tenant names ten_flag" },
        { Array.Empty<string>(), new Dictionary<string, string> { ["QUEUEY_TENANT"] = "ten_env" }, "QUEUEY_TENANT names ten_env" },
    };

    [Theory]
    [MemberData(nameof(Disagreements))]
    public async Task Every_deployment_command_fails_before_sending_when_an_explicit_tenant_disagrees_with_the_file(
        string[] tenantArgs, Dictionary<string, string> env, string named)
    {
        string path = DeployFile("ten_file");
        var commands = new (string Name, Func<Task<int>> Run)[]
        {
            ("apply", () => Apply(path, tenantArgs)),
            ("apply --check", () => Apply(path, tenantArgs.Append("--check").ToArray())),
            ("apply --dry-run", () => Apply(path, tenantArgs.Append("--dry-run").ToArray())),
            ("verify", () => Verify(path, tenantArgs)),
        };

        foreach ((string name, Func<Task<int>> run) in commands)
        {
            RecordingHandler api = Server();
            var ex = await Assert.ThrowsAsync<Queuey.Client.QueueyConfigurationException>(
                () => CliHarness.RunAsync(run, api, env));

            Assert.True(ex.Message.Contains("names workspace ten_file", StringComparison.Ordinal) && ex.Message.Contains(named, StringComparison.Ordinal),
                $"{name}: {ex.Message}");
            Assert.True(api.Requests.Count == 0, $"{name} sent {api.Requests.Count} request(s)");
        }
    }

    [Theory]
    [InlineData("ten_file", new[] { "--tenant", "ten_file" }, "ten_file")]   // flagget og fila er enige
    [InlineData("ten_file", new string[0], "ten_file")]                      // bare fila
    [InlineData(null, new[] { "--tenant", "ten_flag" }, "ten_flag")]         // fila navngir ingen
    public async Task Apply_and_verify_reach_the_same_workspace(string? fileTenant, string[] tenantArgs, string expected)
    {
        string path = DeployFile(fileTenant);

        RecordingHandler applied = Server();
        CliRun apply = await CliHarness.RunAsync(() => Apply(path, tenantArgs), applied);
        Assert.Equal(ExitCodes.Success, apply.Exit);
        Assert.Contains($"GET /tenants/{expected}/queues", applied.Requests.Select(r => r.Key));
        Assert.Equal(expected, applied.Requests.Single(r => r.Key == "PUT /queues").Json.GetProperty("tenantPublicId").GetString());
        Assert.Contains($"(tenant {expected})", apply.Stdout);

        RecordingHandler verified = Server();
        CliRun verify = await CliHarness.RunAsync(() => Verify(path, tenantArgs), verified);
        Assert.Equal(ExitCodes.Success, verify.Exit);
        Assert.Contains($"POST /events/{expected}/orders", verified.Requests.Select(r => r.Key));
        Assert.Contains($"in {expected}, event evt_1", verify.Stdout);

        CliRun verifyJson = await CliHarness.RunAsync(() => Verify(path, tenantArgs.Append("--json").ToArray()), Server());
        Assert.Equal(expected, JsonDocument.Parse(verifyJson.Stdout).RootElement.GetProperty("tenant").GetString());
    }
}
