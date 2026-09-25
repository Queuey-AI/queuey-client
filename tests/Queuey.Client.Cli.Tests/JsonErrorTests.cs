using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Med --json er en feil JSON på stdout, og stderr er tom (review 2026-09-24). Før ga flere feilveier
/// fortsatt prosa: en manglende fil, verify uten --data, og --json=true, som ble sjekket som rå tekst.
/// Hver test går hele veien gjennom <see cref="CliEntry"/>, slik et skall kaller CLI-en.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class JsonErrorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-json-error-tests", Guid.NewGuid().ToString("N"));

    public JsonErrorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Missing => Path.Combine(_dir, "nope.json");

    private string DeployFile(string json)
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static JsonElement ErrorOn(CliRun run, int exit)
    {
        Assert.Equal(exit, run.Exit);
        Assert.Equal(string.Empty, run.Stderr);
        return JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
    }

    public static TheoryData<string[], string, int> UserErrors() => new()
    {
        { new[] { "apply", "--json", "--file", "{missing}" }, "missing_file", ExitCodes.Usage },
        { new[] { "plan", "--json", "--file", "{missing}" }, "missing_file", ExitCodes.Usage },
        { new[] { "apply", "--json=true", "--file", "{missing}" }, "missing_file", ExitCodes.Usage },
        { new[] { "verify", "orders", "--json" }, "missing_body", ExitCodes.Usage },
        { new[] { "verify", "--json" }, "missing_argument", ExitCodes.Usage },
        { new[] { "verify", "orders", "--data", "{}", "--timeout", "0", "--json" }, "invalid_value", ExitCodes.Usage },
        { new[] { "publish", "orders", "--json" }, "missing_argument", ExitCodes.Usage },
        { new[] { "credentials", "nope", "--json" }, "unknown_subcommand", ExitCodes.Usage },
        { new[] { "plna", "--json" }, "unknown_command", ExitCodes.Usage },
    };

    [Theory]
    [MemberData(nameof(UserErrors))]
    public async Task A_user_error_is_json_on_stdout_when_json_is_asked_for(string[] args, string code, int exit)
    {
        string[] resolved = args.Select(a => a == "{missing}" ? Missing : a).ToArray();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(resolved));

        JsonElement error = ErrorOn(run, exit);
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task A_configuration_error_is_json_with_its_exit_code()
    {
        // Feilen oppstår inne i kommandoen og fanges av CliEntry: samme form som en brukerfeil.
        string path = DeployFile("""{ "tenant": "ten_file", "queues": {} }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--tenant", "ten_flag", "--json")));

        JsonElement error = ErrorOn(run, ExitCodes.Configuration);
        Assert.Equal("config_error", error.GetProperty("code").GetString());
        Assert.Contains("ten_flag", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Without_json_the_same_errors_stay_prose_on_stderr()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "verify", "orders" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.Contains("verify requires the event to send", run.Stderr);
        Assert.Contains("→ It is delivered to the real receiver", run.Stderr);
    }
}
