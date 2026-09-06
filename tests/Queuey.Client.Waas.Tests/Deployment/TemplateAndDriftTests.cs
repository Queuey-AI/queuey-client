using System;
using System.Collections.Generic;
using System.Linq;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

public class TemplateAndDriftTests
{
    private static DeploymentFile Pulled() => DeploymentFile.Parse("""
    {
      "tenant": "ten_prod",
      "workspace": { "ordering": "fifo", "delivery": { "baseUrl": "https://prod.example.com", "authMode": "ApiKey", "credentialRef": "partner-key" } },
      "queues": {
        "orders":   { "retentionDays": 30, "delivery": { "url": "/orders" } },
        "billing":  { "delivery": { "url": "https://billing.prod.example.com/in" } },
        "plain":    { }
      }
    }
    """);

    // ── Templating ────────────────────────────────────────────────────────────

    [Fact]
    public void Templating_variabilises_only_what_does_not_travel()
    {
        DeploymentFile t = DeploymentTemplate.ToTemplate(Pulled(), "staging");

        // The workspace binding is what differs between environments — a deploy supplies its own.
        Assert.Null(t.Tenant);
        Assert.Equal("${QUEUEY_BASE_URL}", t.Workspace!.Delivery!.BaseUrl);

        // A relative path is already portable. That is the payoff of the thin-queue shape, and
        // substituting it would be busywork.
        Assert.Equal("/orders", t.Queues["orders"].Delivery!.Url);

        // An absolute URL pins a host, so it becomes a variable named for its queue and environment.
        Assert.Equal("${QUEUEY_STAGING_BILLING_URL}", t.Queues["billing"].Delivery!.Url);

        // Credential names are portable by design — that is why the file carries names, not ids.
        Assert.Equal("partner-key", t.Workspace.Delivery!.CredentialRef);

        // Behaviour is not environment-specific.
        Assert.Equal(30, t.Queues["orders"].RetentionDays);
    }

    [Fact]
    public void A_template_reports_the_variables_it_needs()
    {
        DeploymentFile t = DeploymentTemplate.ToTemplate(Pulled(), "staging");

        Assert.Equal(
            new[] { "QUEUEY_BASE_URL", "QUEUEY_STAGING_BILLING_URL" },
            t.ReferencedVariables().OrderBy(v => v, StringComparer.Ordinal).ToArray());
    }

    // ── Expansion ─────────────────────────────────────────────────────────────

    [Fact]
    public void Expansion_substitutes_from_the_environment()
    {
        DeploymentFile t = DeploymentTemplate.ToTemplate(Pulled(), "staging");

        var env = new Dictionary<string, string?>
        {
            ["QUEUEY_BASE_URL"] = "https://staging.example.com",
            ["QUEUEY_STAGING_BILLING_URL"] = "https://billing.staging.example.com/in",
        };

        DeploymentFile expanded = t.Expand(name => env.TryGetValue(name, out string? v) ? v : null);

        Assert.Equal("https://staging.example.com", expanded.Workspace!.Delivery!.BaseUrl);
        Assert.Equal("https://billing.staging.example.com/in", expanded.Queues["billing"].Delivery!.Url);
    }

    [Fact]
    public void An_unset_variable_is_an_error_not_an_empty_string()
    {
        // Expanding to nothing would quietly produce a base URL of "https://" and a deploy that
        // "succeeded" while pointing at nowhere.
        DeploymentFile t = DeploymentTemplate.ToTemplate(Pulled(), "staging");

        var ex = Assert.Throws<QueueyConfigurationException>(() => t.Expand(_ => null));

        Assert.Contains("QUEUEY_BASE_URL", ex.Message);
        Assert.Contains("is not set", ex.Message);
    }

    [Fact]
    public void A_default_is_honoured_when_one_is_given()
    {
        DeploymentFile file = DeploymentFile.Parse("""
        { "workspace": { "delivery": { "baseUrl": "${HOOK_HOST:-https://fallback.example.com}" } }, "queues": {} }
        """);

        Assert.Equal("https://fallback.example.com", file.Expand(_ => null).Workspace!.Delivery!.BaseUrl);
    }

    [Fact]
    public void Literal_text_around_a_variable_survives()
    {
        DeploymentFile file = DeploymentFile.Parse("""
        { "workspace": { "delivery": { "baseUrl": "https://${HOST}/hooks" } }, "queues": {} }
        """);

        Assert.Equal("https://acme.example.com/hooks",
            file.Expand(n => n == "HOST" ? "acme.example.com" : null).Workspace!.Delivery!.BaseUrl);
    }

    // ── Drift ─────────────────────────────────────────────────────────────────

    [Fact]
    public void No_drift_when_the_workspace_matches()
    {
        Assert.Empty(DeploymentDrift.Compare(Pulled(), Pulled()));
    }

    [Fact]
    public void A_changed_value_is_drift()
    {
        DeploymentFile actual = Pulled();
        actual.Queues["orders"].RetentionDays = 3;

        DriftItem item = Assert.Single(DeploymentDrift.Compare(Pulled(), actual));

        Assert.Equal("queues.orders.retentionDays", item.Path);
        Assert.Equal("30", item.Declared);
        Assert.Equal("3", item.Actual);
    }

    [Fact]
    public void A_missing_queue_is_drift()
    {
        DeploymentFile actual = Pulled();
        actual.Queues.Remove("billing");

        DriftItem item = Assert.Single(DeploymentDrift.Compare(Pulled(), actual));
        Assert.Equal("queues.billing", item.Path);
        Assert.Null(item.Actual);
    }

    [Fact]
    public void What_the_file_does_not_declare_is_never_drift()
    {
        // The property that makes the gate usable: a team need not put every field under code. A
        // workspace carrying settings the file is silent about is inheritance working as designed.
        DeploymentFile declared = DeploymentFile.Parse("""
        { "queues": { "orders": { "retentionDays": 30 } } }
        """);

        DeploymentFile actual = Pulled();
        actual.Queues["orders"].Idempotent = true;
        actual.Queues["orders"].Ordering = "fifo";

        Assert.Empty(DeploymentDrift.Compare(declared, actual));
    }

    // ── Code emission ─────────────────────────────────────────────────────────

    [Fact]
    public void Emitted_code_declares_behaviour_and_not_destinations()
    {
        string code = QueueCodeWriter.ForFile(Pulled(), "Acme.Queues");

        Assert.Contains("namespace Acme.Queues;", code);
        Assert.Contains("[QueueyQueue(\"orders\", RetentionDays = 30)]", code);
        Assert.Contains("public sealed class Orders { }", code);
        Assert.Contains("[QueueyQueue(\"plain\")]", code);

        // Destinations belong in the deployment file — baking a URL into a type would undo the whole
        // separation.
        Assert.DoesNotContain("prod.example.com", code);
        Assert.DoesNotContain("/orders", code);
    }

    [Theory]
    [InlineData("order-events", "OrderEvents")]
    [InlineData("orders", "Orders")]
    [InlineData("order.created", "OrderCreated")]
    [InlineData("2fa", "Queue2fa")]
    public void A_queue_name_becomes_a_legal_type_name(string queue, string expected)
        => Assert.Equal(expected, QueueCodeWriter.TypeName(queue));
}
