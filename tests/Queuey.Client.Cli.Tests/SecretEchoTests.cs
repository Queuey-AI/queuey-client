using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// En feil skriver ikke tilbake det som kan være en hemmelighet limt inn på feil sted (review 2026-10-05): et ukjent valg,
/// et argument for mye, en ukjent kommando, eller en verdi gitt til --from-env i stedet for et navn.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SecretEchoTests
{
    [Theory]
    [InlineData(new[] { "whoami", "--api-key:qak_kid.s3cr3t", "--json" }, "Unknown option --api-key… for queuey whoami.")]
    [InlineData(new[] { "whoami", "sk_live_s3cr3t", "--json" }, "Unexpected argument 'sk…' for queuey whoami.")]
    [InlineData(new[] { "qak_kid.s3cr3t", "--json" }, "Unknown command 'qak…'.")]
    [InlineData(new[] { "credentials", "sk_test_s3cr3t", "--json" }, "Unknown credentials subcommand 'sk…'. Expected 'set' or 'list'.")]
    public async Task A_word_that_is_not_a_name_is_cut_where_a_name_would_end(string[] args, string message)
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(args));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(message, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("s3cr3t", run.Stdout + run.Stderr);
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
