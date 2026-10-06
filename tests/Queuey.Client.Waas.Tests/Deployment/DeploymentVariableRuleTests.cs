using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Hvilke variabler en deploy-fil får lese (herding før tag, review av #53, 2026-10-06). Fila kunne lese hvilken som helst
/// variabel, også ${QUEUEY_API_KEY} i en leverings-URL, og i CI med nøkkelen i miljøet havnet den i Queuey og hos mottakeren.
/// En variabel som holder en hemmelighet, leses aldri, og en verdi som starter som en hemmelighet, brukes aldri.
/// </summary>
public class DeploymentVariableRuleTests
{
    private static string Url(string variable) => $$"""
        { "queues": { "orders": { "delivery": { "url": "https://receiver.example.com/in?k=${{{variable}}}" } } } }
        """;

    private static string NeverRead(string name) => throw new InvalidOperationException($"{name} was read.");

    [Theory]
    [InlineData("QUEUEY_API_KEY")]
    [InlineData("queuey_api_key")]
    [InlineData("QUEUEY_LICENSE")]
    [InlineData("QUEUEY_API_BASE")]
    [InlineData("QUEUEY_PROFILE")]
    [InlineData("QUEUEY_USER_CONFIG")]
    [InlineData("QUEUEY_TENANT_ID")]
    [InlineData("API_KEY")]
    [InlineData("STRIPE_APIKEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("DB_PASSWORD")]
    [InlineData("SMTP_PASSWD")]
    [InlineData("signing_passphrase")]
    public void A_variable_that_holds_a_secret_is_never_read(string variable)
    {
        DeploymentFile file = DeploymentFile.Parse(Url(variable));

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Expand(NeverRead));

        Assert.StartsWith($"${{{variable}}} in queues.orders.delivery.url is a variable a deployment file may not read: ", ex.Message);
        Assert.EndsWith("It was not read.", ex.Message);
        Assert.NotNull(ex.SuggestedAction);
    }

    [Fact]
    public void A_queuey_setting_and_a_secret_name_say_what_to_do_instead()
    {
        var setting = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(Url("QUEUEY_API_KEY")).Expand(NeverRead));
        Assert.Contains("it is one of the CLI's own QUEUEY_ settings, such as the API key it connects with", setting.Message);
        Assert.StartsWith("A deployment file may use QUEUEY_TENANT, QUEUEY_WORKSPACE_ENVIRONMENT, and the QUEUEY_…_URL", setting.SuggestedAction);

        var secret = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(Url("GITHUB_TOKEN")).Expand(NeverRead));
        Assert.Contains("its name says it holds a secret (TOKEN)", secret.Message);
        Assert.StartsWith("Store a secret as a credential (queuey credentials set) and name it in credentialRef", secret.SuggestedAction);
    }

    [Theory]
    [InlineData("QUEUEY_TENANT", "tenant", "ten_dev")]
    [InlineData("QUEUEY_WORKSPACE_ENVIRONMENT", "environment", "dev")]
    [InlineData("QUEUEY_BASE_URL", "baseUrl", "https://hooks.example.com")]
    [InlineData("QUEUEY_STAGING_ORDERS_URL", "url", "https://orders.example.com/in")]
    [InlineData("QUEUEY_API_KEYS_URL", "url", "https://keys.example.com/in")]
    [InlineData("QUEUEY_STRIPE_DELIVERY_KIND", "kind", "http")]
    [InlineData("WEBHOOK_HOST", "url", "https://hooks.example.com/in")]
    public void The_names_pull_as_writes_and_ones_own_variables_are_read(string variable, string field, string value)
    {
        // QUEUEY_API_KEYS_URL er en kø som heter api-keys, slik pull --as skriver den: navnet hører til Queuey sine egne.
        string json = field switch
        {
            "tenant" => $$"""{ "tenant": "${{{variable}}}", "queues": {} }""",
            "environment" => $$"""{ "workspace": { "environment": "${{{variable}}}" }, "queues": {} }""",
            "baseUrl" => $$"""{ "workspace": { "delivery": { "baseUrl": "${{{variable}}}" } }, "queues": {} }""",
            "kind" => $$"""{ "queues": { "orders": { "delivery": { "url": "https://orders.example.com", "kind": "${{{variable}}}" } } } }""",
            _ => $$"""{ "queues": { "orders": { "delivery": { "url": "${{{variable}}}" } } } }""",
        };

        DeploymentFile expanded = DeploymentFile.Parse(json).Expand(name => name == variable ? value : null);

        expanded.Resolve();
        Assert.DoesNotContain("${", JsonSerializer.Serialize(expanded));
    }

    [Theory]
    [InlineData("qak_kid.copied-into-another-name")]
    [InlineData("whsec_abc123")]
    [InlineData("sk_live_abc")]
    public void A_value_that_starts_like_a_secret_is_not_used_whatever_its_variable_is_called(string value)
    {
        DeploymentFile file = DeploymentFile.Parse(Url("RECEIVER_QUERY"));

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Expand(name => name == "RECEIVER_QUERY" ? value : null));

        Assert.Equal("${RECEIVER_QUERY} in queues.orders.delivery.url has a value that starts like a secret (a key prefix such as qak_ " +
                     "or whsec_), so it was not used. The value is not shown.", ex.Message);
        Assert.DoesNotContain(value, ex.Message + ex.SuggestedAction);
    }

    [Fact]
    public void A_default_that_starts_like_a_secret_is_not_used_either()
    {
        DeploymentFile file = DeploymentFile.Parse("""
            { "queues": { "orders": { "delivery": { "url": "https://receiver.example.com/in?k=${RECEIVER_QUERY:-qak_kid.in-the-file}" } } } }
            """);

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Expand(_ => null));

        Assert.Contains("has a value that starts like a secret", ex.Message);
    }

    [Fact]
    public void A_credential_name_comes_from_a_variable_that_is_not_named_like_a_secret()
    {
        // credentialRef tar et navn, aldri en verdi, så den trenger aldri en variabel som heter SECRET eller KEY.
        const string json = """
            { "queues": { "stripe": { "ingress": { "signedRequest": { "template": "stripe", "credentialRef": "${STRIPE_WHSEC_CREDENTIAL}" } } } } }
            """;

        DeploymentFile expanded = DeploymentFile.Parse(json).Expand(name => name == "STRIPE_WHSEC_CREDENTIAL" ? "stripe-whsec-live" : null);

        Assert.Equal("stripe-whsec-live", expanded.Queues["stripe"].Ingress!.SignedRequest!.CredentialRef);

        var refused = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(json.Replace("STRIPE_WHSEC_CREDENTIAL", "STRIPE_SIGNING_SECRET"))
            .Expand(NeverRead));
        Assert.Contains("which takes the credential's name, never its value", refused.SuggestedAction);
    }

    [Fact]
    public void A_profile_cannot_give_a_value_to_a_variable_the_file_may_not_read()
    {
        DeploymentFile file = DeploymentFile.Parse("""
            { "queues": {}, "profiles": { "dev": { "variables": { "PARTNER_TOKEN": "partner-hook" } } } }
            """);

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Resolve());

        Assert.Equal("profiles.dev.variables.PARTNER_TOKEN is a variable a deployment file may not read: its name says it holds a " +
                     "secret (TOKEN). A profile cannot give it a value either.", ex.Message);
    }

    // ── én utvidelse ───────────────────────────────────────────────────────

    [Fact]
    public void An_expanded_file_is_never_expanded_again()
    {
        // En verdi fra miljøet som selv har ${…}, settes inn som den står. Før utvidet biblioteket en fil CLI-en hadde utvidet med en
        // profil én gang til, og leste da ${OTHER} fra prosessens miljø.
        DeploymentFile file = DeploymentFile.Parse("""{ "workspace": { "delivery": { "baseUrl": "https://${HOOK_HOST}" } }, "queues": {} }""");

        DeploymentFile once = file.Expand(name => name == "HOOK_HOST" ? "hooks.example.com/${OTHER}" : null);
        DeploymentFile twice = once.Expand(NeverRead);

        Assert.Same(once, twice);
        Assert.Equal("https://hooks.example.com/${OTHER}", twice.Workspace!.Delivery!.BaseUrl);
    }

    [Fact]
    public async Task Apply_sends_an_expanded_file_as_it_is_and_reads_no_variable_again()
    {
        // Som CLI-en med en profil: fila er utvidet før den når biblioteket, og en verdi har ${…} i seg. Variabelen den nevner, er
        // satt i prosessen, så en ny utvidelse i ApplyDeploymentAsync ville ha lest den.
        const string inner = "F27_PATH_PART";
        DeploymentFile expanded = DeploymentFile.Parse("""
            { "queues": { "orders": { "delivery": { "url": "https://${ORDERS_HOST_F27}/in" } } } }
            """).Expand(name => name == "ORDERS_HOST_F27" ? "orders.example.com/${" + inner + "}" : null);
        var api = new StubHttpMessageHandler((_, req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/queues", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>());
            if (req.Method == HttpMethod.Put && path == "/queues")
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = true, hasDeliveryTarget = true });
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        Environment.SetEnvironmentVariable(inner, "from-the-process");
        try
        {
            await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(expanded);
        }
        finally
        {
            Environment.SetEnvironmentVariable(inner, null);
        }

        string sent = string.Concat(api.Bodies.Where(b => b is not null).Select(b => Encoding.UTF8.GetString(b!)));
        Assert.Contains("orders.example.com/${" + inner + "}", sent);
        Assert.DoesNotContain("from-the-process", sent);
    }
}

/// <summary>
/// JSON-stien i en feil fra brukerfila eller deploy-filas profiler (herding før tag): et navn vises bare når det er et felt
/// fila har eller har formen til et profil- eller variabelnavn, så en nøkkel limt inn som navn aldri står i feilen.
/// </summary>
public class JsonErrorPathsTests
{
    private static readonly Func<string, bool>[] UserFile =
    {
        JsonErrorPaths.Fields("profiles"), DeploymentProfiles.IsName, JsonErrorPaths.Fields("apiKey", "license", "tenant"),
    };

    [Theory]
    [InlineData("$.profiles.dev.apiKey", "$.profiles.dev.apiKey")]
    [InlineData("$.Profiles.dev.APIKEY", "$.Profiles.dev.APIKEY")]
    [InlineData("$.profiles.dev.api_key", "$.profiles.dev.…")]
    [InlineData("$.profiles.dev['qak_kid.secret']", "$.profiles.dev.…")]
    [InlineData("$.profiles['qak_kid.secret'].apiKey", "$.profiles.….apiKey")]
    [InlineData("$.profiles.qak_kid", "$.profiles.…")]
    [InlineData("$.qak_kid", "$.…")]
    [InlineData("$.profiles.dev.apiKey.deeper", "$.profiles.dev.apiKey.…")]
    [InlineData("$.profiles.dev[0]", "$.profiles.dev[0]")]
    [InlineData("$.profiles['unterminated", "$.profiles.…")]
    [InlineData("$", "$")]
    public void A_name_is_shown_only_when_it_is_a_field_or_has_a_names_shape(string path, string expected)
    {
        Assert.Equal(expected, JsonErrorPaths.Mask(path, UserFile));
    }

    [Fact]
    public void No_path_is_none()
    {
        Assert.Null(JsonErrorPaths.Mask(null, UserFile));
        Assert.Null(JsonErrorPaths.Mask("", UserFile));
        Assert.Null(JsonErrorPaths.Mask("profiles.dev", UserFile));
    }

    [Fact]
    public void A_deployment_files_profiles_mask_a_pasted_key_in_their_error()
    {
        using JsonDocument document = JsonDocument.Parse("""
            { "profiles": { "dev": { "variables": { "QUEUEY_BASE_URL": "https://dev.example.com" }, "qak_kid.secret": "x" } } }
            """);

        var ex = Assert.Throws<QueueyConfigurationException>(() => DeploymentProfiles.ReadFrom(document.RootElement));

        Assert.StartsWith("Could not read the deployment file's profiles ($.dev.…)", ex.Message);
        Assert.DoesNotContain("qak_kid", ex.Message);
    }
}
