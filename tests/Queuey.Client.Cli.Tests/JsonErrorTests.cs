using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Med --json er en feil JSON på stdout, og stderr er tom (review 2026-09-24). Før ga flere feilveier
/// fortsatt prosa: en manglende fil, verify uten testevent, og --json=true, som ble sjekket som rå tekst.
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
        { new[] { "verify", "orders", "--send", "--json" }, "missing_body", ExitCodes.Usage },
        { new[] { "verify", "orders", "--json" }, "missing_argument", ExitCodes.Usage },
        { new[] { "verify", "--json" }, "missing_argument", ExitCodes.Usage },
        { new[] { "verify", "orders", "--data", "{}", "--json" }, "send_required", ExitCodes.Usage },
        { new[] { "verify", "orders", "--event", "evt_1", "--timeout", "0", "--json" }, "invalid_value", ExitCodes.Usage },
        // F2.7: --event er valgfri, så det som mangler, er eventen.
        { new[] { "publish", "orders", "--json" }, "missing_body", ExitCodes.Usage },
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
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "verify", "orders", "--send" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.Contains("--send needs the test event", run.Stderr);
        Assert.Contains("→ It is delivered to the real receiver", run.Stderr);
    }

    [Theory]
    [InlineData("--json", "plna")]
    [InlineData("--json=true", "plna")]
    public async Task Json_before_the_command_is_read_as_json_for_the_command(string json, string command)
    {
        // Review 2026-10-05: `queuey --json <cmd>` ble lest som kommandoen --json, og svaret var prosa.
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { json, command }));

        Assert.Equal("unknown_command", ErrorOn(run, ExitCodes.Usage).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Json_before_the_command_reaches_the_command_itself()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "--json", "apply", "--file", Missing }));

        Assert.Equal("missing_file", ErrorOn(run, ExitCodes.Usage).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_error_the_cli_did_not_expect_is_json_too()
    {
        // Review 2026-10-05: med --json skal hver feil være JSON. Testserveren kaster InvalidOperationException, en feil
        // ingen kommando venter, slik en NullReferenceException i CLI-en ville gjort.
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("metrics", "que_orders", "--json")));

        JsonElement error = ErrorOn(run, ExitCodes.RuntimeError);
        Assert.Equal("internal_error", error.GetProperty("code").GetString());
        Assert.StartsWith("InvalidOperationException: ", error.GetProperty("message").GetString());
        Assert.Contains("without --json for the stack trace", error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_an_error_with_its_exit_code_not_a_crash()
    {
        // Review 2026-10-05: en fil uten lesetilgang ga exit 134 og stack trace.
        if (OperatingSystem.IsWindows())
            return;

        string path = DeployFile("""{ "queues": {} }""");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--file", path, "--dry-run", "--json" }));

            JsonElement error = ErrorOn(run, ExitCodes.Configuration);
            Assert.Equal("file_unreadable", error.GetProperty("code").GetString());
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task An_io_error_that_is_not_about_a_named_file_is_not_called_an_unreadable_file()
    {
        // Re-review 2026-10-05: CliEntry gjorde hver IOException til file_unreadable, med handlingen «sjekk at stien er en
        // fil du kan lese». En IOException fra nettet har ingen sti.
        RecordingHandler api = new(_ => throw new IOException("The connection was reset."));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("metrics", "que_orders", "--json")), api);

        JsonElement error = ErrorOn(run, ExitCodes.RuntimeError);
        Assert.Equal("internal_error", error.GetProperty("code").GetString());
        Assert.Equal("IOException: The connection was reset.", error.GetProperty("message").GetString());
    }

    public static TheoryData<string[], string, string> FilesThatFailTheirChecks() => new()
    {
        { new[] { "apply" }, """{ "queues": { "orders": { "ingress": { "eventType": { "name": "X-Event" } } } } }""",
            "queues.orders.ingress.eventType needs \"from\"" },
        { new[] { "apply", "--dry-run" }, """{ "queues": { "orders": { "ingress": { "authMode": "Kerberos" } } } }""",
            "queues.orders.ingress.authMode must be one of None, ApiKey, SignedRequest, ApiKeyAndSignedRequest; got 'Kerberos'." },
        { new[] { "plan" }, """{ "queues": { "orders": { "delivery": { "authMode": "Kerberos" } } } }""",
            "queues.orders.delivery.authMode must be one of " },
        { new[] { "apply", "--check" }, """{ "queues": { "orders": { "delivery": { "url": "https://x.example/${TEST_SURELY_UNSET_PATH}" } } } }""",
            "Environment variable 'TEST_SURELY_UNSET_PATH' is referenced by queues.orders.delivery.url" },
    };

    [Theory]
    [MemberData(nameof(FilesThatFailTheirChecks))]
    public async Task A_file_that_fails_its_checks_is_named_in_front_of_the_error_and_nothing_is_sent(string[] command, string json, string error)
    {
        // Re-review 2026-10-05: en parsefeil hadde stien foran, men ikke en ugyldig ingress-kilde, en authMode eller en
        // ${VAR} som mangler. Testserveren feiler testen om noe sendes.
        string path = DeployFile(json);

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            command.Concat(new[] { "--file", path, "--tenant", "ten_abc", "--json" }).ToArray())));

        JsonElement e = ErrorOn(run, ExitCodes.Configuration);
        Assert.Equal("config_error", e.GetProperty("code").GetString());
        Assert.StartsWith($"{path}: {error}", e.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_missing_license_says_where_to_set_it_not_that_streams_need_it()
    {
        // Review 2026-10-05: feilen nevnte «(SyncStreams)» for apply og verify, og ingen sa hvor verdien settes.
        string path = DeployFile("""{ "tenant": "ten_abc", "queues": { "orders": {} } }""");
        string[] args =
        {
            "apply", "--file", path, "--json", "--api-key", "qak_kid.secret", "--api-base", "https://api.test",
            "--ingress-base", "https://ingress.test", "--config", Path.Combine(_dir, "no-config.json"),
        };

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(args));

        JsonElement error = ErrorOn(run, ExitCodes.Configuration);
        Assert.DoesNotContain("SyncStreams", error.GetProperty("message").GetString());
        Assert.Contains("--license, QUEUEY_LICENSE, or license in queuey.json", error.GetProperty("action").GetString());
    }
}
