using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Profilene i deploy-fila (F2.7, 2026-10-06): verdiene fila tar per miljø, så en promotering er en PR. En profil gir verdier
/// til fila sine ${VAR}-er; det den ikke gir, kommer fra miljøet. En verdi vises aldri i en feil, og en som ser ut som en
/// hemmelighet, avvises: fila committes.
/// </summary>
public class DeploymentProfileTests
{
    private const string TwoProfiles = """
        {
          "workspace": {
            "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}",
            "delivery": { "baseUrl": "${QUEUEY_BASE_URL}" }
          },
          "queues": {
            "stripe": { "delivery": { "url": "/api/stripe", "kind": "${QUEUEY_STRIPE_DELIVERY_KIND}" } }
          },
          "profiles": {
            "dev": {
              "variables": {
                "QUEUEY_WORKSPACE_ENVIRONMENT": "dev",
                "QUEUEY_BASE_URL": "https://dev.example.com",
                "QUEUEY_STRIPE_DELIVERY_KIND": "localForward"
              }
            },
            "prod": {
              "variables": {
                "QUEUEY_WORKSPACE_ENVIRONMENT": "prod",
                "QUEUEY_BASE_URL": "https://api.example.com",
                "QUEUEY_STRIPE_DELIVERY_KIND": "http"
              }
            }
          }
        }
        """;

    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in variables)
            map[name] = value;
        return name => map.TryGetValue(name, out string? value) ? value : null;
    }

    [Fact]
    public void A_profile_fills_the_files_variables()
    {
        DeploymentFile file = DeploymentFile.Parse(TwoProfiles);

        DeploymentFile dev = file.ForProfile("dev", Env());
        DeploymentFile prod = file.ForProfile("prod", Env());

        Assert.Equal("dev", dev.Workspace!.Environment);
        Assert.Equal("https://dev.example.com", dev.Workspace.Delivery!.BaseUrl);
        Assert.Equal("localForward", dev.Queues["stripe"].Delivery!.Kind);
        Assert.Equal("prod", prod.Workspace!.Environment);
        Assert.Equal("https://api.example.com", prod.Workspace.Delivery!.BaseUrl);
        Assert.Equal("http", prod.Queues["stripe"].Delivery!.Kind);
        dev.Resolve();
        prod.Resolve();
    }

    [Fact]
    public void A_file_expanded_for_a_profile_expands_to_itself()
    {
        // Biblioteket utvider fila igjen når den sendes (ApplyAsync), fra prosessens miljø. Med en profil er det ingenting
        // igjen å utvide, så miljøet spørres ikke.
        DeploymentFile dev = DeploymentFile.Parse(TwoProfiles).ForProfile("dev", Env());

        DeploymentFile again = dev.Expand(name => throw new InvalidOperationException($"{name} was looked up."));

        Assert.Equal("https://dev.example.com", again.Workspace!.Delivery!.BaseUrl);
        Assert.NotNull(again.Profiles);
    }

    [Fact]
    public void A_variable_the_profile_leaves_out_comes_from_the_environment()
    {
        DeploymentFile file = DeploymentFile.Parse("""
            {
              "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}", "delivery": { "baseUrl": "${QUEUEY_BASE_URL}" } },
              "queues": {},
              "profiles": { "dev": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "dev" } } }
            }
            """);

        DeploymentFile dev = file.ForProfile("dev", Env(("QUEUEY_BASE_URL", "https://from-env.example.com")));

        Assert.Equal("dev", dev.Workspace!.Environment);
        Assert.Equal("https://from-env.example.com", dev.Workspace.Delivery!.BaseUrl);
    }

    [Fact]
    public void A_profile_value_wins_over_a_default_and_the_default_still_fills_what_nothing_sets()
    {
        DeploymentFile file = DeploymentFile.Parse("""
            {
              "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT:-test}", "delivery": { "baseUrl": "${QUEUEY_BASE_URL:-https://fallback.example.com}" } },
              "queues": {},
              "profiles": { "dev": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "dev" } } }
            }
            """);

        DeploymentFile dev = file.ForProfile("dev", Env());

        Assert.Equal("dev", dev.Workspace!.Environment);
        Assert.Equal("https://fallback.example.com", dev.Workspace.Delivery!.BaseUrl);
    }

    [Fact]
    public void The_same_value_in_the_profile_and_the_environment_is_no_conflict()
    {
        DeploymentFile dev = DeploymentFile.Parse(TwoProfiles).ForProfile("dev", Env(("QUEUEY_BASE_URL", "https://dev.example.com")));

        Assert.Equal("https://dev.example.com", dev.Workspace!.Delivery!.BaseUrl);
    }

    [Fact]
    public void A_variable_with_another_value_in_the_environment_is_an_error_that_shows_neither()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() =>
            DeploymentFile.Parse(TwoProfiles).ForProfile("dev", Env(("QUEUEY_BASE_URL", "https://left-in-the-shell.example.com"))));

        Assert.Contains("${QUEUEY_BASE_URL} has a value in profile dev and another in the environment, so neither is picked.", ex.Message);
        Assert.DoesNotContain("left-in-the-shell", ex.Message + ex.SuggestedAction);
        Assert.DoesNotContain("dev.example.com", ex.Message + ex.SuggestedAction);
        Assert.Contains("Unset QUEUEY_BASE_URL", ex.SuggestedAction ?? "");
    }

    [Fact]
    public void A_variable_set_nowhere_says_the_profile_has_no_value_for_it()
    {
        DeploymentFile file = DeploymentFile.Parse("""
            {
              "workspace": { "delivery": { "baseUrl": "${QUEUEY_BASE_URL}" } },
              "queues": {},
              "profiles": { "dev": { "variables": {} } }
            }
            """);

        var ex = Assert.Throws<QueueyConfigurationException>(() => file.ForProfile("dev", Env()));

        Assert.Contains("Environment variable 'QUEUEY_BASE_URL' is referenced by workspace.delivery.baseUrl", ex.Message);
        Assert.Contains("Profile dev gives no value for it either: add it to profiles.dev.variables in the deployment file.", ex.Message);
    }

    [Fact]
    public void A_profile_the_file_does_not_have_names_the_ones_it_has()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(TwoProfiles).ForProfile("staging", Env()));

        Assert.Equal("The deployment file has no profile 'staging', so --profile staging has no values for it. Its profiles are dev, prod.", ex.Message);
        Assert.Contains("\"profiles\": { \"staging\"", ex.SuggestedAction ?? "");
    }

    [Fact]
    public void A_file_without_profiles_says_so()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() =>
            DeploymentFile.Parse("""{ "queues": {} }""").ForProfile("dev", Env()));

        Assert.EndsWith("It has no profiles.", ex.Message);
    }

    [Theory]
    [InlineData("Prod")]
    [InlineData("-dev")]
    [InlineData("dev env")]
    [InlineData("qak_kid.secret")]
    public void A_profile_name_out_of_shape_is_refused_without_showing_it(string name)
    {
        string json = $$"""{ "queues": {}, "profiles": { {{JsonSerializer.Serialize(name)}}: { "variables": { "A": "1" } } } }""";

        var ex = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(json).Resolve());

        Assert.Contains("A profile in the deployment file has a name that is not a profile name", ex.Message);
        Assert.DoesNotContain(name, ex.Message);
    }

    [Fact]
    public void ForProfile_refuses_a_name_out_of_shape_without_showing_it()
    {
        var ex = Assert.Throws<ArgumentException>(() => DeploymentFile.Parse(TwoProfiles).ForProfile("Bad Name", Env()));

        Assert.DoesNotContain("Bad Name", ex.Message);
    }

    [Theory]
    [InlineData("1BAD")]
    [InlineData("BAD-NAME")]
    [InlineData("${A}")]
    [InlineData("sk_live_abc")]
    public void A_variable_name_out_of_shape_is_refused_without_showing_it(string name)
    {
        string json = $$"""{ "queues": {}, "profiles": { "dev": { "variables": { {{JsonSerializer.Serialize(name)}}: "1" } } } }""";

        var ex = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(json).Resolve());

        Assert.Contains("profiles.dev.variables has a name that is not a variable name", ex.Message);
        Assert.DoesNotContain(name, ex.Message);
    }

    [Theory]
    [InlineData("qak_kid.secret", "starts like a secret.")]
    [InlineData("whsec_abc", "starts like a secret.")]
    [InlineData("sk_test_abc", "starts like a secret.")]
    [InlineData("0123456789abcdef0123456789abcdef", "looks like a secret")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "looks like a secret")]
    [InlineData("${OTHER}", "refers to a variable")]
    [InlineData("", "is empty")]
    [InlineData("   ", "is empty")]
    public void A_value_a_profile_may_not_hold_is_refused_without_showing_it(string value, string why)
    {
        string json = $$"""{ "queues": {}, "profiles": { "dev": { "variables": { "QUEUEY_X": {{JsonSerializer.Serialize(value)}} } } } }""";

        var ex = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(json).Resolve());

        Assert.StartsWith("profiles.dev.variables.QUEUEY_X ", ex.Message);
        Assert.Contains(why, ex.Message);
        Assert.EndsWith("Its value is not shown.", ex.Message);
        if (value.Trim().Length > 0)
            Assert.DoesNotContain(value, ex.Message + ex.SuggestedAction);
    }

    [Theory]
    [InlineData("ten_8Kx2mPq7Zr4Lw9TnB3vQ")]
    [InlineData("lic_8Kx2mPq7Zr4Lw9TnB3vQ")]
    [InlineData("https://api.example.com")]
    [InlineData("localForward")]
    [InlineData("stripe-whsec")]
    public void A_queuey_id_or_a_plain_value_passes(string value)
    {
        string json = $$"""{ "queues": {}, "profiles": { "dev": { "variables": { "QUEUEY_X": {{JsonSerializer.Serialize(value)}} } } } }""";

        DeploymentFile.Parse(json).Resolve();
    }

    [Fact]
    public void A_secret_in_a_profile_is_refused_when_another_profile_is_picked_and_when_none_is()
    {
        // Fila står i repoet uansett hvilken profil som brukes, så alle sjekkes hver gang.
        const string json = """
            {
              "queues": {},
              "profiles": {
                "dev": { "variables": { "QUEUEY_X": "dev" } },
                "prod": { "variables": { "QUEUEY_X": "qak_kid.secret" } }
              }
            }
            """;
        DeploymentFile file = DeploymentFile.Parse(json);

        Assert.Throws<QueueyConfigurationException>(() => file.ForProfile("dev", Env()));
        Assert.Throws<QueueyConfigurationException>(() => file.Resolve());
        Assert.Throws<QueueyConfigurationException>(() => file.Expand(_ => null).Resolve());
    }

    [Fact]
    public void A_profile_named_twice_is_refused()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse("""
            { "queues": {}, "profiles": { "dev": { "variables": { "A": "1" } }, "dev": { "variables": { "A": "2" } } } }
            """));

        Assert.Contains("profiles has 'dev' twice", ex.Message);
    }

    [Fact]
    public void Variable_names_are_exact_so_a_and_A_are_two_variables()
    {
        DeploymentFile file = DeploymentFile.Parse("""
            {
              "workspace": { "environment": "${a}", "delivery": { "baseUrl": "${A}" } },
              "queues": {},
              "profiles": { "dev": { "variables": { "a": "dev", "A": "https://dev.example.com" } } }
            }
            """);

        DeploymentFile dev = file.ForProfile("dev", Env());

        Assert.Equal("dev", dev.Workspace!.Environment);
        Assert.Equal("https://dev.example.com", dev.Workspace.Delivery!.BaseUrl);
    }

    [Fact]
    public void A_field_a_profile_does_not_have_is_refused()
    {
        Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse("""
            { "queues": {}, "profiles": { "dev": { "vars": { "A": "1" } } } }
            """));
    }

    [Fact]
    public void A_profile_holds_a_workspace_id_for_the_files_tenant()
    {
        DeploymentFile file = DeploymentFile.Parse("""
            {
              "tenant": "${QUEUEY_TENANT}",
              "queues": {},
              "profiles": { "prod": { "variables": { "QUEUEY_TENANT": "ten_prod1" } } }
            }
            """);

        Assert.Equal("ten_prod1", file.ForProfile("prod", Env()).Tenant);
    }
}
