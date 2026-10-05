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
public sealed class SecretEchoTests
{
    [Theory]
    [InlineData(new[] { "whoami", "sk_live_s3cr3t", "--json" }, "sk_live_s3cr3t", "Unexpected argument 'sk…' for queuey whoami.")]
    [InlineData(new[] { "whoami", "--json", "3f9c0a7e5b2d4c18a6e9f0b1c2d3e4f5" }, "3f9c0a7e5b2d4c18a6e9f0b1c2d3e4f5", "Unexpected argument '3f9…' for queuey whoami.")]
    [InlineData(new[] { "qak_kid.s3cr3t", "--json" }, "qak_kid.s3cr3t", "Unknown command 'qak…'.")]
    [InlineData(new[] { "FAKEtok3nAbc123", "--json" }, "FAKEtok3nAbc123", "Unknown command 'FAK…'.")]
    [InlineData(new[] { "credentials", "sk_test_s3cr3t", "--json" }, "sk_test_s3cr3t", "Unknown credentials subcommand 'sk…'. Expected 'set' or 'list'.")]
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
    public async Task A_variable_name_that_is_not_set_is_named()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
            "credentials", "set", "--name", "partner-key", "--from-env", "QUEUEY_TEST_UNSET_SECRET_VARIABLE", "--tenant", "ten_abc", "--json")));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("Environment variable 'QUEUEY_TEST_UNSET_SECRET_VARIABLE' is not set or is empty.", run.Stdout);
    }
}
