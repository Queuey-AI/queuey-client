using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// <c>queuey credentials rotate</c> (Queuey F3.7, besluttet 2026-10-07): et bytte av hemmeligheten til en credential workspacet
/// har, som operasjonen rotate_credential. Verdien kommer fra miljøet, som for <c>set</c>, og skrives aldri ut. Et vindu der den
/// gamle hemmeligheten fortsatt verifiserer, åpnes bare med en uttrykkelig <c>--grace</c>, høyst et døgn.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CredentialsRotateTests
{
    private const string ValueVariable = "QUEUEY_TEST_CREDENTIAL_VALUE_F37";
    private const string Value = "whsec_rotated_value_never_printed";

    private static JsonElement Error(CliRun run) => JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");

    private static object Rotated(string? graceUntil = "2026-10-08T13:00:00+00:00", bool replaced = true, bool? closed = null) => new
    {
        publicId = "cred_9Lm2", name = "stripe-whsec", type = "HmacSigning", keyId = "stripe-whsec", version = 4, created = false,
        secretReplaced = replaced, boundWorkspace = false, boundQueues = Array.Empty<string>(), previousVersionValidUntil = graceUntil,
        graceWindowClosed = closed,
    };

    private static async Task<(CliRun Run, RecordingHandler Api)> Rotate(Func<HttpResponseMessage> answer, params string[] extra)
    {
        RecordingHandler api = new(req => req switch
        {
            { Method.Method: "POST", Path: "/tenants/ten_abc/credentials/rotate" } => answer(),
            _ => throw new InvalidOperationException(req.Key),
        });
        Environment.SetEnvironmentVariable(ValueVariable, Value);
        try
        {
            CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(new[]
            {
                "credentials", "rotate", "--name", "stripe-whsec", "--from-env", ValueVariable, "--tenant", "ten_abc",
            }.Concat(extra).ToArray())), api);
            return (run, api);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValueVariable, null);
        }
    }

    [Fact]
    public async Task A_rotation_sends_the_name_and_the_value_and_a_grace_window_only_when_asked_for()
    {
        (CliRun plain, RecordingHandler plainApi) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated(graceUntil: null)));
        (CliRun graced, RecordingHandler gracedApi) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated()), "--grace", "60");

        Assert.Equal(ExitCodes.Success, plain.Exit);
        Assert.Equal(ExitCodes.Success, graced.Exit);
        JsonElement plainBody = Assert.Single(plainApi.Requests).Json;
        Assert.Equal(new[] { "name", "secret" }, plainBody.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Value, plainBody.GetProperty("secret").GetString());
        Assert.Equal(60, Assert.Single(gracedApi.Requests).Json.GetProperty("graceMinutes").GetInt32());
    }

    [Fact]
    public async Task A_rotation_with_a_window_says_until_when_the_previous_secret_verifies_and_never_prints_the_value()
    {
        (CliRun run, _) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated()), "--grace", "60");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("Rotated the secret of 'stripe-whsec' (HmacSigning): it holds version 4 now, under the same id", run.Stdout);
        Assert.Contains("The previous secret still verifies at the ingress until 2026-10-08 13:00 UTC", run.Stdout);
        Assert.DoesNotContain(Value, run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task A_rotation_without_a_window_says_the_previous_secret_stopped_verifying()
    {
        (CliRun run, _) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated(graceUntil: null)));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("The previous secret stopped verifying at once.", run.Stdout);
    }

    [Fact]
    public async Task The_value_the_credential_holds_rotates_nothing_and_says_no_window_is_open()
    {
        (CliRun run, _) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated(graceUntil: null, replaced: false, closed: false)));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("already holds this value: nothing was rotated", run.Stdout);
        Assert.Contains("No grace window is open: only this value verifies.", run.Stdout);
    }

    [Fact]
    public async Task The_value_the_credential_holds_without_grace_says_the_open_window_was_closed()
    {
        // Review av #63, M1: en operatør kjører samme rotasjon uten --grace for å kutte en lekket gammel hemmelighet. Queuey
        // lukker vinduet (Queuey #462), og linjen sier det.
        (CliRun run, _) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated(graceUntil: null, replaced: false, closed: true)));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("already holds this value: nothing was rotated", run.Stdout);
        Assert.Contains("The grace window is closed: the previous secret stopped verifying at once.", run.Stdout);
    }

    [Fact]
    public async Task The_value_the_credential_holds_from_a_Queuey_that_keeps_the_window_says_it_still_verifies_and_how_to_stop_it()
    {
        // En Queuey fra før #462 lukker ikke vinduet for samme verdi. Linjen sier at den gamle hemmeligheten fortsatt verifiserer,
        // og at bare en ny verdi uten --grace, eller en tilbakekalling, stopper den.
        (CliRun run, _) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated(replaced: false)));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("The grace window is still open: the previous secret verifies at the ingress until 2026-10-08 13:00 UTC.", run.Stdout);
        Assert.Contains("rotate to a new value without --grace, or revoke the credential", run.Stdout);
        Assert.DoesNotContain("closed", run.Stdout);
    }

    [Fact]
    public async Task The_value_the_credential_holds_with_grace_leaves_the_window_and_says_how_to_close_it()
    {
        (CliRun run, _) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated(replaced: false, closed: false)), "--grace", "30");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("The grace window is still open: the previous secret verifies at the ingress until 2026-10-08 13:00 UTC.", run.Stdout);
        Assert.Contains("Run the same rotation without --grace to close it now.", run.Stdout);
    }

    [Fact]
    public async Task A_rotation_with_a_window_says_the_same_rotation_without_grace_stops_it_sooner()
    {
        (CliRun run, _) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated()), "--grace", "60");

        Assert.Contains("To stop it sooner, run the same rotation again without --grace: the same value closes the window.", run.Stdout);
    }

    [Fact]
    public async Task Expect_version_is_sent_only_when_given_and_a_rotation_of_another_version_says_so()
    {
        // Review av #63, K1: CI som kjenner versjonen, skriver aldri over en rotasjon en person gjorde imellom.
        (CliRun given, RecordingHandler givenApi) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated(graceUntil: null)),
            "--expect-version", "3");
        (CliRun refused, _) = await Rotate(() => RecordingHandler.Error(HttpStatusCode.Conflict, "credential_changed_meanwhile",
            "Credential 'stripe-whsec' (cred_9Lm2) holds version 5 of its secret now, not version 3.", "Look at what changed."),
            "--expect-version", "3", "--json");

        Assert.Equal(ExitCodes.Success, given.Exit);
        Assert.Equal(3, Assert.Single(givenApi.Requests).Json.GetProperty("expectedVersion").GetInt32());
        Assert.Equal(ExitCodes.RuntimeError, refused.Exit);
        Assert.Equal("credential_changed_meanwhile", Error(refused).GetProperty("code").GetString());
        Assert.Contains("does not hold version 3 of its secret now", Error(refused).GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("v3")]
    public async Task An_expected_version_that_is_not_a_version_is_refused_before_anything_is_sent(string version)
    {
        (CliRun run, RecordingHandler api) = await Rotate(() => throw new InvalidOperationException("nothing is sent"), "--expect-version", version, "--json");

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Contains("--expect-version", Error(run).GetProperty("message").GetString());
    }

    [Fact]
    public async Task List_shows_the_version_and_an_open_window()
    {
        RecordingHandler api = new(req => req switch
        {
            { Method.Method: "GET", Path: "/tenants/ten_abc/credentials" } => RecordingHandler.Json(HttpStatusCode.OK, new object[]
            {
                new { publicId = "cred_1", name = "stripe-whsec", type = "HmacSigning", keyId = "stripe-live", version = 4,
                      previousVersionValidUntil = "2026-10-08T13:00:00+00:00" },
                new { publicId = "cred_2", name = "partner-token", type = "BearerToken", keyId = (string?)null, version = 1,
                      previousVersionValidUntil = (string?)null },
            }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("credentials", "list", "--tenant", "ten_abc")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("  stripe-whsec\tHmacSigning\tkeyId=stripe-live\tversion=4\tprevious version verifies until 2026-10-08 13:00 UTC", run.Stdout);
        Assert.Contains("  partner-token\tBearerToken\tversion=1" + Environment.NewLine, run.Stdout);
    }

    [Fact]
    public async Task The_json_names_the_version_and_the_end_of_the_window()
    {
        (CliRun run, _) = await Rotate(() => RecordingHandler.Json(HttpStatusCode.OK, Rotated()), "--grace", "60", "--json");

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(4, json.GetProperty("version").GetInt32());
        Assert.True(json.GetProperty("secretReplaced").GetBoolean());
        Assert.Equal(DateTimeOffset.Parse("2026-10-08T13:00:00+00:00"), json.GetProperty("previousVersionValidUntil").GetDateTimeOffset());
        Assert.DoesNotContain(Value, run.Stdout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1441")]
    [InlineData("-5")]
    [InlineData("1h")]
    public async Task A_grace_window_out_of_one_minute_to_a_day_is_refused_before_anything_is_sent(string grace)
    {
        (CliRun run, RecordingHandler api) = await Rotate(() => throw new InvalidOperationException("nothing is sent"), "--grace", grace, "--json");

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Equal("--grace takes whole minutes, 1 to 1440: a grace window lasts at most a day.", Error(run).GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_rotation_someone_else_beat_says_nothing_was_stored()
    {
        (CliRun run, _) = await Rotate(() => RecordingHandler.Error(HttpStatusCode.Conflict, "credential_changed_meanwhile",
            "Credential 'stripe-whsec' (cred_9Lm2) holds version 5 of its secret now, not version 4.", "Look at what changed."), "--json");

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal("credential_changed_meanwhile", Error(run).GetProperty("code").GetString());
        Assert.Contains("nothing was stored", Error(run).GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_name_the_workspace_has_no_credential_under_is_Queueys_refusal()
    {
        (CliRun run, _) = await Rotate(() => RecordingHandler.Error(HttpStatusCode.NotFound, "credential_not_found",
            "No credential named 'stripe-whsec' is stored in this workspace, so there is no secret to rotate.", "Store it first."), "--json");

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal("credential_not_found", Error(run).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_Queuey_that_predates_rotations_says_so_and_names_set_with_replace()
    {
        (CliRun run, _) = await Rotate(() => new HttpResponseMessage(HttpStatusCode.NotFound), "--json");

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement error = Error(run);
        Assert.Equal("credential_rotation_unsupported", error.GetProperty("code").GetString());
        Assert.Contains($"queuey credentials set --name stripe-whsec --from-env {ValueVariable} --replace", error.GetProperty("action").GetString());
    }

    [Theory]
    [InlineData("--replace")]
    [InlineData("--type")]
    public async Task An_option_that_belongs_to_set_says_what_applies_instead(string option)
    {
        string[] extra = option == "--type" ? new[] { option, "HmacSigning", "--json" } : new[] { option, "--json" };
        (CliRun run, RecordingHandler api) = await Rotate(() => throw new InvalidOperationException("nothing is sent"), extra);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Contains("credentials rotate", run.Stdout);
    }
}
