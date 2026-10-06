using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// En feil skriver ikke tilbake det som kan være en hemmelighet limt inn på feil sted (review 2026-10-05): et ukjent valg,
/// et argument for mye, en ukjent kommando, eller en verdi gitt til --from-env i stedet for et navn. Re-review samme dag:
/// en hemmelighet av bare bokstaver og sifre ble fortsatt vist helt. Nå viser en feil høyst tre tegn av en kommando, en
/// underkommando eller et argument, og navnet på et ukjent valg bare når det er kort kebab-case med små bokstaver.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SecretEchoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-secret-echo-tests", Guid.NewGuid().ToString("N"));

    public SecretEchoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile(string tenant)
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, $$"""{ "tenant": "{{tenant}}", "queues": { "orders": {} } }""");
        return path;
    }

    [Theory]
    [InlineData(new[] { "whoami", "sk_live_s3cr3t", "--json" }, "sk_live_s3cr3t", "Unexpected argument 'sk…' for queuey whoami.")]
    [InlineData(new[] { "whoami", "--json", "3f9c0a7e5b2d4c18a6e9f0b1c2d3e4f5" }, "3f9c0a7e5b2d4c18a6e9f0b1c2d3e4f5", "Unexpected argument '3f9…' for queuey whoami.")]
    [InlineData(new[] { "qak_kid.s3cr3t", "--json" }, "qak_kid.s3cr3t", "Unknown command 'qak…'.")]
    [InlineData(new[] { "FAKEtok3nAbc123", "--json" }, "FAKEtok3nAbc123", "Unknown command 'FAK…'.")]
    [InlineData(new[] { "credentials", "sk_test_s3cr3t", "--json" }, "sk_test_s3cr3t", "Unknown credentials subcommand 'sk…'. Expected 'set', 'request' or 'list'.")]
    [InlineData(new[] { "keys", "FAKEtok3nAbc123", "--json" }, "FAKEtok3nAbc123", "Unknown keys subcommand 'FAK…'. Expected 'mint'.")]
    [InlineData(new[] { "queue", "FAKEtok3nAbc123", "--json" }, "FAKEtok3nAbc123", "Unknown queue subcommand 'FAK…'. Expected 'plan' or 'sync'.")]
    public async Task A_command_subcommand_or_argument_shows_three_characters_at_most(string[] args, string secret, string message)
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(args));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(message, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain(secret[CliErrors.ShownCharacters..], run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task An_edge_subcommand_shows_three_characters_at_most()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "edge", "FAKEtok3nAbc123", "--json" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.StartsWith("Unknown edge subcommand 'FAK…'. Expected: ",
            JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("Etok3nAbc123", run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task A_short_word_is_shown_whole()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "whoami", "foo", "--json" }));

        Assert.Equal("Unexpected argument 'foo' for queuey whoami.",
            JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_password_given_as_one_argument_too_many_is_not_shown()
    {
        // Re-review 2026-10-05: `--mqtt-password` glemt, og passordet ble «Unexpected argument 'FAKEpw123'».
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "edge", "run", "--mqtt-user", "bob", "FAKEpw123" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("Unexpected argument 'FAK…' for queuey edge run.", run.Stderr);
        Assert.DoesNotContain("Epw123", run.Stdout + run.Stderr);
    }

    [Theory]
    [InlineData("-Xy9FAKEpw")]
    [InlineData("-hunter2-is-lowercase")]
    public async Task A_value_that_starts_with_a_dash_is_not_shown_as_an_option(string password)
    {
        // Re-review 2026-10-05: `--mqtt-password -Xy9…` ble «Unknown option --Xy9…». Et ord som kom der et valg ventet
        // verdien sin, vises ikke selv når det ser ut som et valgnavn, og feilen sier hvordan verdien gis.
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "edge", "run", "--mqtt-password", password }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("queuey edge run was given an unknown option right after --mqtt-password. It is not shown, since it may be the value of --mqtt-password.", run.Stderr);
        Assert.Contains("give it as --mqtt-password=<value>", run.Stderr);
        Assert.DoesNotContain(password[1..], run.Stdout + run.Stderr);
    }

    [Theory]
    [InlineData("--api-key:qak_kid.s3cr3t", "api-key:qak_kid.s3cr3t")]
    [InlineData("--Xy9FAKEpw", "Xy9FAKEpw")]
    [InlineData("--a-very-long-lowercase-option-name-that-is-no-name", "a-very-long-lowercase-option-name-that-is-no-name")]
    public async Task An_option_name_that_is_not_short_lowercase_kebab_case_is_not_shown(string option, string name)
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "whoami", option, "--json" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("unknown_option", error.GetProperty("code").GetString());
        Assert.Equal(
            "queuey whoami was given an unknown option. It is not shown, since it does not look like an option name and may be a secret.",
            error.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("option").ValueKind);
        Assert.DoesNotContain(name, run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task An_option_name_as_typed_is_still_shown_whole()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--paln", "--json" }));

        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("Unknown option --paln for queuey apply.", error.GetProperty("message").GetString());
        Assert.Equal("--paln", error.GetProperty("option").GetString());
    }

    [Fact]
    public async Task A_secret_given_to_from_env_is_not_written_back_as_a_variable_name()
    {
        // `--from-env sk_test_…` sa at miljøvariabelen 'sk_test_…' ikke var satt, med hemmeligheten i teksten.
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "credentials", "set", "--name", "partner-key", "--from-env", "sk_test_s3cr3t", "--tenant", "ten_abc", "--json")));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.DoesNotContain("s3cr3t", run.Stdout + run.Stderr);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Contains("never the secret itself", error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_secret_given_as_the_credential_type_is_not_written_back()
    {
        // Re-review 2026-10-05: `--type sk_live_…` skrev hemmeligheten tilbake som en ukjent type. Typen sjekkes før miljøet
        // leses og før noe sendes; testserveren feiler testen om noe sendes.
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "credentials", "set", "--name", "partner-key", "--from-env", "QUEUEY_TEST_UNSET_SECRET_VARIABLE",
            "--type", "sk_live_FAKEsecret123", "--tenant", "ten_abc", "--json")));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("invalid_value", error.GetProperty("code").GetString());
        Assert.Equal("--type is not a credential type. Its value is not shown, since it may be a secret.", error.GetProperty("message").GetString());
        Assert.StartsWith("Expected one of: ApiKeyHeader, BearerToken,", error.GetProperty("action").GetString());
        Assert.DoesNotContain("FAKEsecret123", run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task A_credential_type_in_the_wrong_case_is_shown_with_its_spelling()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "credentials", "set", "--name", "partner-key", "--from-env", "QUEUEY_TEST_UNSET_SECRET_VARIABLE",
            "--type", "bearertoken", "--tenant", "ten_abc", "--json")));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal("Unknown credential type 'bearertoken'. Did you mean BearerToken?",
            JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    private const string Key = "qak_FAKEkid.FAKEsecret";

    private static readonly Dictionary<string, string> KeyInTheEnvironment = new() { ["QUEUEY_TENANT"] = Key };

    private static readonly Dictionary<string, string> NoEnvironment = new();

    /// <summary>
    /// Kommandoer med en API-nøkkel der workspacet skal stå, fra flagget eller miljøet, med og uten et workspace i
    /// deploy-fila. {file} er en deploy-fil med workspace, {bare} en uten.
    /// </summary>
    public static TheoryData<string[], Dictionary<string, string>, string> KeysGivenAsTheWorkspace => new()
    {
        { new[] { "apply", "--file", "{file}", "--tenant", Key }, NoEnvironment, "--tenant" },
        { new[] { "apply", "--file", "{file}", "--dry-run" }, KeyInTheEnvironment, "QUEUEY_TENANT" },
        { new[] { "verify", "orders", "--event", "evt_1", "--deployment", "{file}" }, KeyInTheEnvironment, "QUEUEY_TENANT" },
        // Re-review 2026-10-05: uten workspace i fila var det ingen konflikt, og nøkkelen ble workspacet i URL-ene.
        { new[] { "apply", "--file", "{bare}" }, KeyInTheEnvironment, "QUEUEY_TENANT" },
        { new[] { "plan", "--file", "{bare}" }, KeyInTheEnvironment, "QUEUEY_TENANT" },
        { new[] { "verify", "orders", "--event", "evt_1", "--deployment", "{bare}" }, KeyInTheEnvironment, "QUEUEY_TENANT" },
        { new[] { "credentials", "list", "--tenant", Key }, NoEnvironment, "--tenant" },
        { new[] { "whoami" }, KeyInTheEnvironment, "QUEUEY_TENANT" },
        { new[] { "whoami", "--tenant", Key }, NoEnvironment, "--tenant" },
        { new[] { "issues", Key }, NoEnvironment, "The argument to queuey issues" },
        { new[] { "edge", "publish", "orders", "--spool", "{spool}", "--data", "{}", "--tenant", Key }, NoEnvironment, "--tenant" },
    };

    [Theory]
    [MemberData(nameof(KeysGivenAsTheWorkspace))]
    public async Task A_tenant_that_is_not_a_workspace_id_is_a_usage_error_that_does_not_show_it(
        string[] command, Dictionary<string, string> env, string source)
    {
        // Re-review 2026-10-05: en API-nøkkel i QUEUEY_TENANT, en forveksling i CI, ble skrevet ut ved hver apply og verify,
        // brukt som workspace i URL-ene og vist av whoami. Testserveren feiler testen om noe sendes.
        string withTenant = DeployFile("ten_file");
        string bare = Path.Combine(_dir, "bare.deploy.json");
        File.WriteAllText(bare, """{ "queues": { "orders": {} } }""");
        string spool = Path.Combine(_dir, "spool.db");
        string[] args = command
            .Select(a => a switch { "{file}" => withTenant, "{bare}" => bare, "{spool}" => spool, _ => a })
            .Append("--json").ToArray();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(args)), env: env);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(string.Empty, run.Stderr);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("invalid_value", error.GetProperty("code").GetString());
        Assert.Equal($"{source} is not a workspace id. Its value is not shown, since it may be a secret.", error.GetProperty("message").GetString());
        Assert.Equal("A workspace id starts with ten_. An API key belongs in --api-key or QUEUEY_API_KEY.", error.GetProperty("action").GetString());
        Assert.DoesNotContain("FAKE", run.Stdout);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public async Task Whoami_without_json_does_not_print_a_tenant_that_is_not_a_workspace_id()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("whoami")), env: KeyInTheEnvironment);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("QUEUEY_TENANT is not a workspace id.", run.Stderr);
        Assert.Contains("→ A workspace id starts with ten_. An API key belongs in --api-key or QUEUEY_API_KEY.", run.Stderr);
        Assert.DoesNotContain("FAKE", run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task A_tenant_in_the_config_file_that_is_not_a_workspace_id_is_named_by_the_file_not_shown()
    {
        string config = Path.Combine(_dir, "queuey.json");
        File.WriteAllText(config, $$"""{ "tenant": "{{Key}}" }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "whoami", "--config", config, "--json" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal($"tenant in {config} is not a workspace id. Its value is not shown, since it may be a secret.",
            JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("FAKE", run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task Edge_run_refuses_a_tenant_that_is_not_a_workspace_id_before_it_opens_the_spool()
    {
        string spool = Path.Combine(_dir, "edge-run.db");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "edge", "run", "--spool", spool, "--tenant", Key, "--api-key", "qak_kid.secret" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--tenant is not a workspace id.", run.Stderr);
        Assert.DoesNotContain("FAKE", run.Stdout + run.Stderr);
        Assert.False(File.Exists(spool));
    }

    [Theory]
    [InlineData("ten_abc")]
    [InlineData(" ten_abc\n")]
    public async Task A_workspace_id_is_still_taken_and_trimmed(string tenant)
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("whoami", "--json")),
            env: new Dictionary<string, string> { ["QUEUEY_TENANT"] = tenant });

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal("ten_abc", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("tenant").GetString());
    }

    // F2.7 (2026-10-06): fila sin tenant sjekkes når fila leses (DeploymentFile.Resolve), ikke bare når --tenant navngir et
    // annet workspace. Uten --tenant gikk verdien ellers ut i URL-ene.
    [Theory]
    [InlineData("apply", "--tenant", "ten_flag")]
    [InlineData("apply")]
    [InlineData("plan")]
    public async Task A_tenant_in_the_file_that_is_not_a_workspace_id_is_refused_without_showing_it(params string[] command)
    {
        string path = DeployFile("sk_live_FAKEsecret");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            new[] { command[0], "--file", path }.Concat(command.Skip(1)).Append("--json").ToArray())));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Equal($"{path}: The deployment file's tenant is not a workspace id. Its value is not shown, since it may be a secret.",
            JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("FAKEsecret", run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task A_variable_name_that_is_not_set_is_named()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "credentials", "set", "--name", "partner-key", "--from-env", "QUEUEY_TEST_UNSET_SECRET_VARIABLE", "--tenant", "ten_abc", "--json")));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("Environment variable 'QUEUEY_TEST_UNSET_SECRET_VARIABLE' is not set or is empty.", run.Stdout);
    }
}
