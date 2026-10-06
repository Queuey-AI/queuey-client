using System.Text.Json;
using System.Text.Json.Nodes;
using Queuey.Client.Cli.Advise;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// advise --intent (F2.10, T18) mot repoene i Fixtures/flows: en Stripe-handler i ASP.NET Core, Express, Next.js og en
/// Supabase-funksjon, og en Supabase-funksjon som tar imot en Database Webhook. Hver test kopierer et repo til en
/// midlertidig mappe, legger til det git ikke skal ha (en .env), og kjører advise der.
///
/// Det som må holde: hvert felt sier hvor det kom fra, med fil og linje når repoet viser det; der repoet motsier
/// intensjonen, stopper advise og foreslår ingenting; og forslaget er en fil apply leser, der hver innstilling har en grunn.
/// Ingen verdi fra en .env og ingen payload fra repoet havner i svaret.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class AdviseIntentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queuey-flow-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string StripeIntent = """
        {
          "source": { "kind": { "value": "stripe", "provenance": "stated" } },
          "destination": { "route": { "value": "/api/stripe", "provenance": "stated" } }
        }
        """;

    // ── Stripe ───────────────────────────────────────────────────────────

    [Fact]
    public void A_stripe_handler_in_aspnet_is_found_with_its_file_and_line()
    {
        Fixture("stripe-aspnet");
        const string controller = "Controllers/StripeWebhookController.cs";

        DesiredFlow flow = Advise(StripeIntent).Flow;

        Assert.Empty(flow.Conflicts);
        AssertField(flow, "destination.route", "/api/stripe", Provenance.Stated, controller, LineOf(controller, "[HttpPost]"));
        AssertField(flow, "destination.framework", "aspnet", Provenance.Evidence, controller, LineOf(controller, "[HttpPost]"));
        AssertField(flow, "requirements.verification", "stripe-signature", Provenance.Evidence, controller, LineOf(controller, "EventUtility.ConstructEvent"));
        AssertField(flow, "source.authentication", "stripe-signature", Provenance.Evidence, controller, LineOf(controller, "EventUtility.ConstructEvent"));
        AssertField(flow, "destination.expectsRawBody", true, Provenance.Evidence, controller, LineOf(controller, "Request.Body"));
        AssertField(flow, "destination.port", 5080, Provenance.Evidence, "Properties/launchSettings.json", LineOf("Properties/launchSettings.json", "applicationUrl"));
        AssertField(flow, "requirements.idempotent", true, Provenance.Evidence, controller, LineOf(controller, "HasProcessedEventAsync"));
        Assert.Equal(new[] { "checkout.session.completed", "invoice.paid" }, flow.Strings("source.eventTypes"));
        Assert.Equal(Provenance.Assumed, flow["requirements.ordering"]!.Provenance);
        Assert.Equal(("dev", Provenance.Assumed), (flow.String("environment"), flow["environment"]!.Provenance));
        Assert.Equal(("stripe", Provenance.Assumed), (flow.String("queue"), flow["queue"]!.Provenance));
    }

    [Fact]
    public void The_stripe_design_verifies_at_the_ingress_and_signs_again_so_constructEvent_keeps_working()
    {
        Fixture("stripe-aspnet");

        FlowDesign design = Advise(StripeIntent).Design!;
        JsonNode queue = design.Content["queues"]!["stripe"]!;

        // Det som skiller miljøene, står i profilen dev, så `queuey plan --profile dev` virker og prod er en profil til (#53).
        Assert.Equal("${QUEUEY_WORKSPACE_ENVIRONMENT}", design.Content["workspace"]!["environment"]!.GetValue<string>());
        Assert.Equal("""{"dev":{"variables":{"QUEUEY_STRIPE_DELIVERY_KIND":"localForward","QUEUEY_WORKSPACE_ENVIRONMENT":"dev"}}}""",
            design.Content["profiles"]!.ToJsonString());
        Assert.Equal("SignedRequest", queue["ingress"]!["authMode"]!.GetValue<string>());
        Assert.Equal("stripe", queue["ingress"]!["signedRequest"]!["template"]!.GetValue<string>());
        Assert.Equal("stripe-whsec", queue["ingress"]!["signedRequest"]!["credentialRef"]!.GetValue<string>());
        Assert.Equal("""{"from":"body","name":"type"}""", queue["ingress"]!["eventType"]!.ToJsonString());
        Assert.Equal("${QUEUEY_STRIPE_DELIVERY_KIND}", queue["delivery"]!["kind"]!.GetValue<string>());
        Assert.Equal("${QUEUEY_BASE_URL}/api/stripe", queue["delivery"]!["url"]!.GetValue<string>());
        Assert.Equal("""{"enabled":true,"templateKey":"stripe"}""", queue["delivery"]!["signing"]!.ToJsonString());
        Assert.True(queue["idempotent"]!.GetValue<bool>());
        Assert.Equal(new[] { "QUEUEY_BASE_URL" }, design.Variables);
        Assert.Equal("stripe-whsec", Assert.Single(design.Credentials).Name);

        Assert.Contains(design.Code, c => c.Action == "keep" && c.What.Contains("EventUtility.ConstructEvent", StringComparison.Ordinal));
        Assert.Contains(design.Code, c => c.Action == "configure" && c.What.Contains("Stripe:WebhookSecret", StringComparison.Ordinal));
        Assert.DoesNotContain(design.Code, c => c.Action == "add");   // handleren dedupliserer alt
        Assert.Contains(design.NextSteps, s => s.Contains("queuey plan --profile dev", StringComparison.Ordinal));
        Assert.Contains(design.NextSteps, s => s.Contains("queuey listen --profile dev --queue stripe --forward-to http://localhost:5080", StringComparison.Ordinal));
        Assert.Contains(design.NextSteps, s => s.Contains("stripe trigger checkout.session.completed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_router_mounted_under_a_prefix_is_found_at_the_whole_route()
    {
        Fixture("stripe-express");

        DesiredFlow flow = Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/webhooks/stripe", "provenance": "stated" } }
            }
            """).Flow;

        Assert.Empty(flow.Conflicts);
        Assert.Contains(flow["destination.route"]!.Evidence, e => e.File == "src/routes/webhooks.js" && e.What == "POST /stripe");
        Assert.Contains(flow["destination.route"]!.Evidence, e => e.File == "src/server.js" && e.What == "mounted under /webhooks");
        Assert.Equal("express", flow.String("destination.framework"));
        AssertField(flow, "destination.port", 4242, Provenance.Evidence, "src/server.js", LineOf("src/server.js", "PORT ||"));
        Assert.Equal(new[] { "payment_intent.succeeded" }, flow.Strings("source.eventTypes"));
    }

    [Fact]
    public void A_handler_that_does_not_deduplicate_gets_a_code_step_and_a_queue_that_holds_on_a_timeout()
    {
        Fixture("stripe-express");

        FlowAdvice advice = Advise("""{ "source": { "kind": { "value": "stripe", "provenance": "stated" } }, "destination": { "route": { "value": "/webhooks/stripe", "provenance": "stated" } } }""");

        Assert.Equal((false, Provenance.Assumed), (advice.Flow.Bool("requirements.idempotent"), advice.Flow["requirements.idempotent"]!.Provenance));
        Assert.False(advice.Design!.Content["queues"]!["stripe"]!["idempotent"]!.GetValue<bool>());
        CodeStep dedup = Assert.Single(advice.Design.Code, c => c.Action == "add");
        Assert.Equal(("src/routes/webhooks.js", LineOf("src/routes/webhooks.js", "constructEvent")), (dedup.File, dedup.Line ?? 0));
        Assert.Contains("event.id", dedup.What, StringComparison.Ordinal);
        Assert.Equal("recommendation", dedup.Basis);
    }

    [Fact]
    public void A_json_parser_ahead_of_the_webhook_is_a_change_in_the_code_plan()
    {
        File_("package.json", """{ "name": "api", "dependencies": { "express": "^4", "stripe": "^16" } }""");
        File_("server.js", """
            const express = require('express');
            const app = express();
            app.use(express.json());
            app.post('/api/stripe', (req, res) => {
              const event = stripe.webhooks.constructEvent(req.body, req.headers['stripe-signature'], process.env.STRIPE_WEBHOOK_SECRET);
              res.sendStatus(200);
            });
            app.listen(3000);
            """);

        FlowDesign design = Advise(StripeIntent).Design!;

        CodeStep change = Assert.Single(design.Code, c => c.Action == "change");
        Assert.Equal(("server.js", 3), (change.File, change.Line ?? 0));
        Assert.Contains("express.raw", change.What, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nextjs_route_handler_is_found_where_the_file_is()
    {
        Fixture("stripe-nextjs");
        const string route = "app/api/webhooks/stripe/route.ts";

        DesiredFlow flow = Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/api/webhooks/stripe", "provenance": "stated" } }
            }
            """).Flow;

        Assert.Empty(flow.Conflicts);
        AssertField(flow, "destination.framework", "nextjs", Provenance.Evidence, route, LineOf(route, "export async function POST"));
        AssertField(flow, "destination.expectsRawBody", true, Provenance.Evidence, route, LineOf(route, "await req.text()"));
        AssertField(flow, "destination.port", 3001, Provenance.Evidence, "package.json", LineOf("package.json", "next dev -p 3001"));
        Assert.Equal(new[] { "checkout.session.completed", "customer.subscription.deleted" }, flow.Strings("source.eventTypes"));
    }

    [Fact]
    public void A_stripe_handler_in_a_supabase_function_gets_its_route_port_and_jwt_setting()
    {
        Fixture("lovable-supabase");
        const string function = "supabase/functions/stripe-webhook/index.ts";

        FlowAdvice advice = Advise("""{ "source": { "kind": { "value": "stripe", "provenance": "stated" } } }""");
        DesiredFlow flow = advice.Flow;

        Assert.Empty(flow.Conflicts);
        AssertField(flow, "destination.route", "/functions/v1/stripe-webhook", Provenance.Evidence, function, LineOf(function, "Deno.serve"));
        Assert.Equal("supabase-edge", flow.String("destination.framework"));
        AssertField(flow, "destination.port", 54321, Provenance.Evidence, "supabase/config.toml", LineOf("supabase/config.toml", "port = 54321"));
        AssertField(flow, "requirements.verification", "stripe-signature", Provenance.Evidence, function, LineOf(function, "constructEventAsync"));
        AssertField(flow, "requirements.idempotent", true, Provenance.Evidence, function, LineOf(function, "stripe_event_id"));
        Assert.Contains(advice.Design!.Code, c => c.Action == "keep" && c.What.Contains("verify_jwt off for stripe-webhook", StringComparison.Ordinal));
        Assert.Contains(advice.Design.Code, c => c.Action == "configure" && c.What.Contains("STRIPE_WEBHOOK_SIGNING_SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public void A_call_from_a_browser_app_is_not_a_route_it_serves()
    {
        // axios.post('/api/checkout', …) i en Vite-app er et kall til en server, ikke en rute.
        Fixture("lovable-supabase");

        FlowFacts facts = FlowScan.Scan(_root);

        Assert.DoesNotContain(facts.Routes, r => r.Route == "/api/checkout");
        Assert.Single(facts.Routes);
    }

    [Fact]
    public void A_supabase_function_without_verify_jwt_off_gets_a_step_to_turn_it_off()
    {
        Fixture("supabase-db-webhook");

        FlowDesign design = Advise("""{ "source": { "kind": { "value": "supabase", "provenance": "stated" } } }""").Design!;

        CodeStep jwt = Assert.Single(design.Code, c => c.File == "supabase/config.toml");
        Assert.Equal("configure", jwt.Action);
        Assert.Contains("verify_jwt = false under [functions.orders-hook]", jwt.What, StringComparison.Ordinal);
    }

    // ── Supabase ─────────────────────────────────────────────────────────

    [Fact]
    public void A_function_that_receives_a_database_webhook_gives_the_table_the_operations_and_the_secret_header()
    {
        Fixture("supabase-db-webhook");
        const string function = "supabase/functions/orders-hook/index.ts";
        const string migration = "supabase/migrations/20260901120000_orders_hook.sql";

        DesiredFlow flow = Advise("""{ "source": { "kind": { "value": "supabase", "provenance": "stated" } } }""").Flow;

        Assert.Empty(flow.Conflicts);
        AssertField(flow, "destination.route", "/functions/v1/orders-hook", Provenance.Evidence, function, 1);
        AssertField(flow, "source.table", "public.orders", Provenance.Evidence, migration, LineOf(migration, "create trigger"));
        AssertField(flow, "requirements.verification", "shared-secret", Provenance.Evidence, function, LineOf(function, "x-webhook-secret"));
        Assert.Equal(("api-key", Provenance.Assumed), (flow.String("source.authentication"), flow["source.authentication"]!.Provenance));
        Assert.Equal(new[] { "UPDATE", "INSERT" }, flow.Strings("source.eventTypes"));
        Assert.Equal("orders", flow.String("queue"));
    }

    [Fact]
    public void The_supabase_design_takes_the_webhooks_key_and_sends_the_header_the_handler_checks()
    {
        Fixture("supabase-db-webhook");
        const string migration = "supabase/migrations/20260901120000_orders_hook.sql";

        FlowDesign design = Advise("""{ "source": { "kind": { "value": "supabase", "provenance": "stated" } } }""").Design!;
        JsonNode queue = design.Content["queues"]!["orders"]!;

        Assert.Equal("ApiKey", queue["ingress"]!["authMode"]!.GetValue<string>());
        Assert.Equal("ApiKey", queue["delivery"]!["authMode"]!.GetValue<string>());
        Assert.Equal("x-webhook-secret", queue["delivery"]!["authHeaderName"]!.GetValue<string>());
        Assert.Equal("orders-webhook-secret", queue["delivery"]!["credentialRef"]!.GetValue<string>());
        CredentialNeed credential = Assert.Single(design.Credentials);
        Assert.Equal("queuey credentials set --profile dev --name orders-webhook-secret --type ApiKeyHeader --from-env ORDERS_WEBHOOK_SECRET",
            credential.Store);

        CodeStep trigger = Assert.Single(design.Code, c => c.File == migration);
        Assert.Equal(("change", LineOf(migration, "create trigger")), (trigger.Action, trigger.Line ?? 0));
        Assert.Contains("not committed", trigger.What, StringComparison.Ordinal);
        Assert.Contains(design.Code, c => c.Action == "configure" && c.What.Contains("queuey listen does not pass", StringComparison.Ordinal));
    }

    // ── konflikter ───────────────────────────────────────────────────────

    [Fact]
    public void A_route_no_handler_serves_is_a_conflict_and_nothing_is_proposed()
    {
        Fixture("stripe-aspnet");

        FlowAdvice advice = Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/api/payments", "provenance": "stated" } }
            }
            """);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("contradiction", "destination.route"), (conflict.Kind, conflict.Field));
        Assert.Equal("\"/api/payments\"", conflict.Stated!.ToJsonString());
        Assert.Equal("""["/api/stripe"]""", conflict.Found!.ToJsonString());
        Assert.Contains(conflict.Evidence, e => e.File == "Controllers/StripeWebhookController.cs");
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_stripe_intent_at_a_handler_that_does_not_verify_stripes_signature_is_a_conflict()
    {
        // Koordinatorens eksempel: intensjonen sier Stripe, men koden verifiserer ikke signaturen.
        File_("package.json", """{ "name": "api", "dependencies": { "express": "^4" } }""");
        File_("server.js", """
            const express = require('express');
            const app = express();
            app.post('/api/stripe', express.json(), (req, res) => {
              handle(req.body);
              res.sendStatus(200);
            });
            """);

        FlowAdvice advice = Advise(StripeIntent);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("contradiction", "requirements.verification"), (conflict.Kind, conflict.Field));
        Assert.Equal("\"none\"", conflict.Found!.ToJsonString());
        Assert.Contains("does not verify Stripe's signature", conflict.Message, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_stripe_intent_at_a_handler_that_checks_a_shared_secret_is_a_conflict()
    {
        Fixture("supabase-db-webhook");

        FlowAdvice advice = Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/functions/v1/orders-hook", "provenance": "stated" } }
            }
            """);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("contradiction", "requirements.verification", "\"shared-secret\""), (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_stated_verification_the_handler_does_not_do_is_a_conflict()
    {
        Fixture("stripe-aspnet");

        FlowConflict conflict = Assert.Single(Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "requirements": { "verification": { "value": "queuey-signature", "provenance": "stated" } }
            }
            """).Flow.Conflicts);

        Assert.Equal(("contradiction", "requirements.verification", "\"stripe-signature\""), (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));
    }

    [Fact]
    public void A_stated_framework_the_handler_is_not_served_by_is_a_conflict()
    {
        Fixture("stripe-aspnet");

        FlowConflict conflict = Assert.Single(Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "framework": { "value": "express", "provenance": "stated" } }
            }
            """).Flow.Conflicts);

        Assert.Equal(("contradiction", "destination.framework", "\"aspnet\""), (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));
    }

    [Fact]
    public void A_stated_raw_body_the_signature_check_contradicts_is_a_conflict()
    {
        Fixture("stripe-aspnet");

        FlowConflict conflict = Assert.Single(Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "expectsRawBody": { "value": false, "provenance": "stated" } }
            }
            """).Flow.Conflicts);

        Assert.Equal(("contradiction", "destination.expectsRawBody"), (conflict.Kind, conflict.Field));
    }

    [Fact]
    public void A_hard_coded_port_the_intent_contradicts_is_a_conflict_and_a_configurable_one_is_not()
    {
        File_("package.json", """{ "name": "api", "dependencies": { "express": "^4", "stripe": "^16" } }""");
        File_("server.js", """
            const app = require('express')();
            app.post('/api/stripe', require('express').raw({ type: 'application/json' }), (req, res) => {
              stripe.webhooks.constructEvent(req.body, req.headers['stripe-signature'], process.env.STRIPE_WEBHOOK_SECRET);
            });
            app.listen(4000);
            """);
        const string intent = """
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "port": { "value": 3000, "provenance": "stated" } }
            }
            """;

        FlowConflict conflict = Assert.Single(Advise(intent).Flow.Conflicts);
        Assert.Equal(("contradiction", "destination.port", "4000"), (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));

        File_("server.js", File.ReadAllText(Path.Combine(_root, "server.js")).Replace("app.listen(4000);", "app.listen(process.env.PORT || 4000);"));
        Assert.Empty(Advise(intent).Flow.Conflicts);
    }

    [Fact]
    public void A_nested_ordering_key_is_one_queuey_cannot_read()
    {
        Fixture("stripe-aspnet");

        FlowConflict conflict = Assert.Single(Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "requirements": {
                "ordering": { "value": "per-key", "provenance": "stated" },
                "orderingKey": { "value": "data.object.customer", "provenance": "stated" }
              }
            }
            """).Flow.Conflicts);

        Assert.Equal(("unsupported", "requirements.orderingKey"), (conflict.Kind, conflict.Field));
        Assert.Contains("top-level", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Per_key_ordering_on_a_top_level_field_lanes_by_it()
    {
        Fixture("stripe-aspnet");

        FlowDesign design = Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "requirements": {
                "ordering": { "value": "per-key", "provenance": "stated" },
                "orderingKey": { "value": "header:X-Customer-Id", "provenance": "stated" }
              }
            }
            """).Design!;

        JsonNode queue = design.Content["queues"]!["stripe"]!;
        Assert.Equal("bykey", queue["ordering"]!.GetValue<string>());
        Assert.Equal("""{"from":"header","name":"X-Customer-Id"}""", queue["ingress"]!["groupKey"]!.ToJsonString());
    }

    [Fact]
    public void A_source_advise_does_not_design_is_unsupported()
    {
        Fixture("stripe-aspnet");

        FlowConflict conflict = Assert.Single(Advise("""{ "source": { "kind": { "value": "github", "provenance": "stated" } } }""").Flow.Conflicts);

        Assert.Equal(("unsupported", "source.kind"), (conflict.Kind, conflict.Field));
    }

    [Theory]
    [InlineData("stripe", "api-key")]
    [InlineData("supabase", "stripe-signature")]
    public void An_authentication_the_source_cannot_give_is_unsupported(string kind, string authentication)
    {
        Fixture(kind == "stripe" ? "stripe-aspnet" : "supabase-db-webhook");

        FlowConflict conflict = Assert.Single(Advise($$"""
            { "source": { "kind": { "value": "{{kind}}", "provenance": "stated" }, "authentication": { "value": "{{authentication}}", "provenance": "stated" } } }
            """).Flow.Conflicts);

        Assert.Equal(("unsupported", "source.authentication"), (conflict.Kind, conflict.Field));
    }

    [Fact]
    public void A_local_base_url_is_one_queueys_delivery_does_not_reach()
    {
        Fixture("stripe-aspnet");

        FlowConflict conflict = Assert.Single(Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "baseUrl": { "value": "http://localhost:5080", "provenance": "stated" } }
            }
            """).Flow.Conflicts);

        Assert.Equal(("unsupported", "destination.baseUrl"), (conflict.Kind, conflict.Field));
    }

    [Fact]
    public void An_intent_without_a_source_lists_the_flows_rather_than_picking_one()
    {
        // Repoet forteller hvordan, ikke om (C1): selv med én handler velger ikke advise flyten for personen.
        Fixture("stripe-aspnet");

        FlowAdvice advice = Advise("{}");

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("missing", "source.kind"), (conflict.Kind, conflict.Field));
        Assert.Contains("POST /api/stripe", conflict.Found!.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void Several_handlers_for_the_source_are_ambiguous_until_the_intent_names_the_route()
    {
        File_("package.json", """{ "name": "api", "dependencies": { "express": "^4", "stripe": "^16" } }""");
        File_("payments.js", """
            router.post('/payments/stripe', express.raw({ type: 'application/json' }), (req, res) => {
              stripe.webhooks.constructEvent(req.body, req.headers['stripe-signature'], process.env.STRIPE_WEBHOOK_SECRET);
            });
            """);
        File_("billing.js", """
            router.post('/billing/stripe', express.raw({ type: 'application/json' }), (req, res) => {
              stripe.webhooks.constructEvent(req.body, req.headers['stripe-signature'], process.env.STRIPE_BILLING_WEBHOOK_SECRET);
            });
            """);

        FlowConflict conflict = Assert.Single(Advise("""{ "source": { "kind": { "value": "stripe", "provenance": "stated" } } }""").Flow.Conflicts);
        Assert.Equal(("ambiguous", "destination.route"), (conflict.Kind, conflict.Field));
        Assert.Equal("""["/billing/stripe","/payments/stripe"]""", conflict.Found!.ToJsonString());

        FlowAdvice named = Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/billing/stripe", "provenance": "stated" } }
            }
            """);
        Assert.Empty(named.Flow.Conflicts);
        Assert.Contains(named.Design!.Code, c => c.Action == "configure" && c.What.Contains("STRIPE_BILLING_WEBHOOK_SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public void An_environment_the_deployment_file_contradicts_is_a_conflict()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "workspace": { "environment": "prod" }, "queues": {} }""");

        FlowConflict conflict = Assert.Single(Advise("""
            {
              "environment": { "value": "dev", "provenance": "stated" },
              "source": { "kind": { "value": "stripe", "provenance": "stated" } }
            }
            """).Flow.Conflicts);

        Assert.Equal(("contradiction", "environment", "\"prod\""), (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));
    }

    // ── designet ─────────────────────────────────────────────────────────

    public static TheoryData<string, string> Proposals => new()
    {
        { "stripe-aspnet", StripeIntent },
        { "stripe-express", """{ "source": { "kind": { "value": "stripe", "provenance": "stated" } } }""" },
        { "stripe-nextjs", """{ "source": { "kind": { "value": "stripe", "provenance": "stated" } }, "environment": { "value": "prod", "provenance": "stated" } }""" },
        { "lovable-supabase", """{ "source": { "kind": { "value": "stripe", "provenance": "stated" } } }""" },
        { "supabase-db-webhook", """{ "source": { "kind": { "value": "supabase", "provenance": "stated" } }, "destination": { "baseUrl": { "value": "https://orders.example.com", "provenance": "stated" } } }""" },
    };

    [Theory]
    [MemberData(nameof(Proposals))]
    public void Every_proposal_is_a_file_apply_reads_and_every_setting_in_it_has_a_reason(string fixture, string intent)
    {
        Fixture(fixture);

        FlowDesign design = Advise(intent).Design!;

        // Det apply --profile gjør med fila før noe sendes: lese den, utvide variablene med profilens verdier, sjekke navn,
        // policy og leveranse.
        DeploymentFile file = DeploymentFile.Parse(design.Content.ToJsonString());
        file.Resolve();
        string profile = Assert.Single(file.Profiles!.Keys);
        Dictionary<string, string> values = file.Profiles[profile].Variables!;
        DeploymentFile expanded = file.ForProfile(profile, name => name == "QUEUEY_BASE_URL" && !values.ContainsKey(name) ? "https://api.example.com" : null);
        expanded.Resolve();
        Assert.Empty(expanded.LocalDestinationProblems());

        // Hver innstilling i fila har en begrunnelse, og hver begrunnelse sier hva den hviler på.
        var leaves = new List<string>();
        Leaves(design.Content, "", leaves);
        foreach (string leaf in leaves.Where(l => l != "$schema"))
            Assert.Contains(design.Settings, s => leaf == s.Path || leaf.StartsWith(s.Path + ".", StringComparison.Ordinal));
        Assert.All(design.Settings, s =>
        {
            Assert.Contains(s.Basis, new[] { "stated", "evidence", "default", "recommendation" });
            Assert.False(string.IsNullOrWhiteSpace(s.Because));
        });
        Assert.All(design.Code, c => Assert.Contains(c.Action, new[] { "keep", "change", "add", "configure" }));
    }

    [Fact]
    public void Prod_delivers_over_http_to_the_base_the_intent_gives()
    {
        Fixture("stripe-nextjs");

        FlowDesign design = Advise("""
            {
              "environment": { "value": "prod", "provenance": "stated" },
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "baseUrl": { "value": "https://shop.example.com/", "provenance": "stated" } }
            }
            """).Design!;

        JsonNode delivery = design.Content["queues"]!["stripe"]!["delivery"]!;
        Assert.Equal("${QUEUEY_STRIPE_DELIVERY_KIND}", delivery["kind"]!.GetValue<string>());
        Assert.Equal("${QUEUEY_BASE_URL}/api/webhooks/stripe", delivery["url"]!.GetValue<string>());
        JsonNode variables = design.Content["profiles"]!["prod"]!["variables"]!;
        Assert.Equal("http", variables["QUEUEY_STRIPE_DELIVERY_KIND"]!.GetValue<string>());
        Assert.Equal("https://shop.example.com", variables["QUEUEY_BASE_URL"]!.GetValue<string>());
        Assert.Equal("prod", variables["QUEUEY_WORKSPACE_ENVIRONMENT"]!.GetValue<string>());
        Assert.Empty(design.Variables);
        Assert.DoesNotContain(design.NextSteps, s => s.Contains("queuey listen", StringComparison.Ordinal));
        Assert.Contains(design.NextSteps, s => s.Contains("Stripe webhook endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_deployment_file_is_merged_into_and_its_base_url_kept()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "workspace": { "environment": "dev", "delivery": { "baseUrl": "https://api.example.com" } },
              "queues": { "payments": { "ingress": { "authMode": "SignedRequest", "signedRequest": { "template": "stripe", "credentialRef": "stripe-whsec" } } } }
            }
            """);

        FlowAdvice advice = Advise("""{ "source": { "kind": { "value": "stripe", "provenance": "stated" } } }""");

        Assert.Equal(("payments", Provenance.Evidence), (advice.Flow.String("queue"), advice.Flow["queue"]!.Provenance));
        Assert.Equal(("dev", Provenance.Evidence), (advice.Flow.String("environment"), advice.Flow["environment"]!.Provenance));
        FlowDesign design = advice.Design!;
        Assert.True(design.Exists);
        Assert.Contains("everything that is there kept", design.Merge, StringComparison.Ordinal);
        Assert.Equal("/api/stripe", design.Content["queues"]!["payments"]!["delivery"]!["url"]!.GetValue<string>());
        Assert.Equal("https://api.example.com", design.Content["workspace"]!["delivery"]!["baseUrl"]!.GetValue<string>());
        Assert.Empty(design.Variables);

        // En fil uten profiler får faste verdier, som resten av den, og kommandoene tar ingen --profile.
        Assert.Null(design.Content["profiles"]);
        Assert.Equal("dev", design.Content["workspace"]!["environment"]!.GetValue<string>());
        Assert.Equal("localForward", design.Content["queues"]!["payments"]!["delivery"]!["kind"]!.GetValue<string>());
        Assert.DoesNotContain(design.NextSteps, s => s.Contains("--profile", StringComparison.Ordinal));
    }

    [Fact]
    public void A_deployment_file_with_one_profile_gets_the_flows_values_in_that_profile_and_no_new_one()
    {
        // Re-review av #59: med profiler og et miljø ingen har oppgitt, lager advise aldri en ny profil. Før fikk denne fila en
        // profil dev ved siden av prod. Den ene profilen gir miljøet (QUEUEY_WORKSPACE_ENVIRONMENT er prod), og kommandoene tar den.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}" },
              "queues": { "orders": { "delivery": { "url": "https://orders.example.com/hook" } } },
              "profiles": { "prod": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "prod" } } }
            }
            """);

        FlowAdvice advice = Advise(StripeIntent);
        FlowDesign design = advice.Design!;

        Assert.Equal(("prod", Provenance.Evidence), (advice.Flow.String("environment"), advice.Flow["environment"]!.Provenance));
        Assert.Equal("${QUEUEY_WORKSPACE_ENVIRONMENT}", design.Content["workspace"]!["environment"]!.GetValue<string>());
        Assert.Equal(new[] { "prod" }, design.Content["profiles"]!.AsObject().Select(p => p.Key).ToArray());
        JsonNode variables = design.Content["profiles"]!["prod"]!["variables"]!;
        Assert.Equal("prod", variables["QUEUEY_WORKSPACE_ENVIRONMENT"]!.GetValue<string>());
        Assert.Equal("http", variables["QUEUEY_STRIPE_DELIVERY_KIND"]!.GetValue<string>());
        Assert.Equal("https://orders.example.com/hook", design.Content["queues"]!["orders"]!["delivery"]!["url"]!.GetValue<string>());
        Assert.Contains("profiles.prod.variables.QUEUEY_STRIPE_DELIVERY_KIND", design.Merge, StringComparison.Ordinal);
        Assert.Contains(design.NextSteps, s => s.Contains("queuey apply --profile prod", StringComparison.Ordinal));
        Assert.DoesNotContain(design.NextSteps, s => s.Contains("--profile dev", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_with_one_profile_and_no_environment_uses_that_profile_and_designs_for_prod()
    {
        // Re-review av #59: fila har bare profilen staging og intet miljø. advise lager ikke profiles.prod, som med en
        // prod-tilkobling ville sendt flyten rett til produksjon. Den bruker staging, og lagringen følger miljøet, som er prod.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "queues": {}, "profiles": { "staging": { "variables": { "ORDERS_HOST": "orders.example.com" } } } }""");

        FlowAdvice advice = Advise(StripeIntent);
        FlowDesign design = advice.Design!;

        Assert.Equal(("prod", Provenance.Assumed), (advice.Flow.String("environment"), advice.Flow["environment"]!.Provenance));
        Assert.Equal(new[] { "staging" }, design.Content["profiles"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal("http", design.Content["profiles"]!["staging"]!["variables"]!["QUEUEY_STRIPE_DELIVERY_KIND"]!.GetValue<string>());
        Assert.Equal("queuey credentials request stripe-whsec --profile staging", Assert.Single(design.Credentials).Store);
        Assert.Contains(design.NextSteps, s => s.StartsWith("queuey apply --profile staging.", StringComparison.Ordinal));
        Assert.DoesNotContain(design.NextSteps, s => s.Contains("--profile prod", StringComparison.Ordinal)
                                                   || s.Contains("--profile dev", StringComparison.Ordinal));
    }

    // ── profilen etter miljøet den gir, aldri etter navnet (før tag, etter #59) ──
    //
    // Konflikten for flere profiler ba om miljøet som et profilnavn, men environment tar bare dev, test, staging og prod. Og
    // med prod oppgitt laget advise profiles.prod ved siden av production, med --profile prod.

    private const string LocalAndProduction = """
        {
          "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}" },
          "queues": {},
          "profiles": {
            "local": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "dev" } },
            "production": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "prod" } }
          }
        }
        """;

    private static string StripeIntentFor(string environment) =>
        "{ \"environment\": { \"value\": \"" + environment + "\", \"provenance\": \"stated\" }, " +
        "\"source\": { \"kind\": { \"value\": \"stripe\", \"provenance\": \"stated\" } }, " +
        "\"destination\": { \"route\": { \"value\": \"/api/stripe\", \"provenance\": \"stated\" } } }";

    [Fact]
    public void A_file_with_several_profiles_and_no_environment_asks_which_by_the_environment_each_gives()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", LocalAndProduction);

        FlowAdvice advice = Advise(StripeIntent);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("ambiguous", "environment", "{\"local\":\"dev\",\"production\":\"prod\"}"),
            (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));
        Assert.Contains("has the profiles local (dev), production (prod)", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("State it in the intent as environment: dev for local, prod for production.", conflict.Question, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_stated_environment_takes_the_profile_that_gives_it_and_never_makes_one_by_its_name()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", LocalAndProduction);

        FlowDesign design = Advise(StripeIntentFor("prod")).Design!;

        Assert.Equal(new[] { "local", "production" }, design.Content["profiles"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal("http", design.Content["profiles"]!["production"]!["variables"]!["QUEUEY_STRIPE_DELIVERY_KIND"]!.GetValue<string>());
        Assert.Equal("queuey credentials request stripe-whsec --profile production", Assert.Single(design.Credentials).Store);
        Assert.Contains(design.NextSteps, s => s.StartsWith("queuey apply --profile production.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stated_environment_no_profile_gives_is_a_conflict_that_names_what_each_gives()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", LocalAndProduction);

        FlowAdvice advice = Advise(StripeIntentFor("staging"));

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("contradiction", "environment", "\"staging\""), (conflict.Kind, conflict.Field, conflict.Stated!.ToJsonString()));
        Assert.Contains("No profile in queuey.deploy.json gives staging: local (dev), production (prod).", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("dev for local, prod for production", conflict.Question, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void Two_profiles_that_give_the_stated_environment_are_a_conflict()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", LocalAndProduction.Replace("\"production\"", "\"laptop\"").Replace("\"prod\"", "\"dev\""));

        FlowAdvice advice = Advise(StripeIntentFor("dev"));

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("ambiguous", "environment"), (conflict.Kind, conflict.Field));
        Assert.Contains("has 2 profiles that give dev (laptop, local)", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("Name it with advise --profile (laptop, local).", conflict.Question, StringComparison.Ordinal);
        Assert.Contains("give each profile its own environment in QUEUEY_WORKSPACE_ENVIRONMENT", conflict.Question, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void Profiles_that_give_no_environment_are_never_chosen_by_their_name()
    {
        // Re-review av #59 lot et oppgitt prod velge profilen som het prod. Navnet sier ikke hvilket workspace profilen går til.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "queues": {},
              "profiles": {
                "dev": { "variables": { "ORDERS_HOST": "dev.example.com" } },
                "prod": { "variables": { "ORDERS_HOST": "orders.example.com" } }
              }
            }
            """);

        FlowConflict unstated = Assert.Single(Advise(StripeIntent).Flow.Conflicts);
        Assert.Equal(("ambiguous", "{\"dev\":null,\"prod\":null}"), (unstated.Kind, unstated.Found!.ToJsonString()));
        Assert.Contains("Name it with advise --profile (dev, prod).", unstated.Question, StringComparison.Ordinal);
        Assert.Contains("take workspace.environment from ${QUEUEY_WORKSPACE_ENVIRONMENT}", unstated.Question, StringComparison.Ordinal);

        FlowConflict stated = Assert.Single(Advise(StripeIntentFor("prod")).Flow.Conflicts);
        Assert.Equal(("contradiction", "environment"), (stated.Kind, stated.Field));
        Assert.Contains("never by its name", stated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_that_forwards_the_queue_outside_dev_is_a_conflict_that_points_at_the_profile()
    {
        // Før tag: konflikten gjaldt bare et antatt prod. En profil prod med KIND=localForward, kopiert fra dev, ga et ekte
        // endepunkt mot en kø som leverer til en laptop også med prod oppgitt.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}" },
              "queues": { "stripe": { "delivery": { "url": "${QUEUEY_BASE_URL}/api/stripe", "kind": "${QUEUEY_STRIPE_DELIVERY_KIND}" } } },
              "profiles": { "prod": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "prod", "QUEUEY_STRIPE_DELIVERY_KIND": "localForward" } } }
            }
            """);

        FlowAdvice advice = Advise(StripeIntentFor("prod"));

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("contradiction", "environment", "\"localForward\""), (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));
        Assert.Contains("and the intent states prod", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("For prod, set profiles.prod.variables.QUEUEY_STRIPE_DELIVERY_KIND to http", conflict.Question, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_kind_from_a_variable_without_profiles_is_named_with_the_value_it_needs()
    {
        // Før tag: pull --as skriver kind som ${VAR} uten profiler. Uten verdien sa ingenting at den skal være http i prod.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "queues": { "stripe": { "delivery": { "url": "/api/stripe", "kind": "${QUEUEY_STRIPE_DELIVERY_KIND}" } } } }""");

        FlowDesign design = Advise(StripeIntent).Design!;

        Assert.Contains(design.NextSteps, s => s.StartsWith("Set QUEUEY_STRIPE_DELIVERY_KIND=http where plan and apply run", StringComparison.Ordinal));
    }

    [Fact]
    public void A_default_in_the_environment_variable_is_the_environment_as_plan_and_apply_read_it()
    {
        // Før tag: ${VAR:-dev} uten profiler ble lest som et umerket workspace, mens plan og apply bruker dev.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT:-dev}" }, "queues": {} }""");

        FlowAdvice advice = Advise(StripeIntent);
        FlowDesign design = advice.Design!;

        Assert.Equal(("dev", Provenance.Evidence), (advice.Flow.String("environment"), advice.Flow["environment"]!.Provenance));
        Assert.Contains(design.NextSteps, s => s.StartsWith("Test mode", StringComparison.Ordinal));
        Assert.StartsWith("queuey credentials set ", Assert.Single(design.Credentials).Store, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("${QUEUEY_WORKSPACE_ENVIRONMENT:-dev}", false, "localForward")]
    [InlineData("${QUEUEY_WORKSPACE_ENVIRONMENT:-dev}", true, "localForward")]
    [InlineData("${QUEUEY_WORKSPACE_ENVIRONMENT}", false, "http")]
    public void A_file_without_profiles_that_takes_its_environment_from_a_variable_takes_the_kind_from_one_too(
        string environment, bool devStated, string kind)
    {
        // Review av #60, B1: med ${VAR:-dev} så CI kan sette prod, skrev advise kind localForward som fast verdi, og køen ble
        // opprettet i prod med localForward. Nå tar kind en variabel uten standardverdi: glemmer CI den, stopper apply.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", "{ \"workspace\": { \"environment\": \"" + environment + "\" }, \"queues\": {} }");

        FlowDesign design = Advise(devStated ? StripeIntentFor("dev") : StripeIntent).Design!;

        Assert.Equal("${QUEUEY_STRIPE_DELIVERY_KIND}", design.Content["queues"]!["stripe"]!["delivery"]!["kind"]!.GetValue<string>());
        Assert.Null(design.Content["profiles"]);
        Assert.Contains(design.NextSteps, s => s.StartsWith("Set ", StringComparison.Ordinal)
                                               && s.Contains($"QUEUEY_STRIPE_DELIVERY_KIND={kind}", StringComparison.Ordinal)
                                               && s.Contains("where plan and apply run", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("${QUEUEY_WORKSPACE_ENVIRONMENT}", "\"local\": { \"variables\": { \"QUEUEY_WORKSPACE_ENVIRONMENT\": \"development\" } }",
        "profiles.local.variables.QUEUEY_WORKSPACE_ENVIRONMENT in queuey.deploy.json is \"development\", which apply refuses")]
    [InlineData("${QUEUEY_WORKSPACE_ENVIRONMENT:-development}", null,
        "the default in workspace.environment (${QUEUEY_WORKSPACE_ENVIRONMENT:-…}) in queuey.deploy.json is \"development\", which apply refuses")]
    public void An_environment_apply_would_refuse_is_a_conflict_not_a_fallback(string environment, string? profile, string message)
    {
        // Review av #60: en ugyldig profilverdi falt tilbake på standardverdien, mens plan og apply bruker profilverdien og nekter.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", "{ \"workspace\": { \"environment\": \"" + environment + "\" }, \"queues\": {}" +
                                    (profile is null ? "" : ", \"profiles\": { " + profile + " }") + " }");

        FlowAdvice advice = Advise(StripeIntent);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("unsupported", "environment"), (conflict.Kind, conflict.Field));
        Assert.Contains(message, conflict.Message, StringComparison.Ordinal);
        Assert.Contains("Set it to one of dev, test, staging, prod", conflict.Question, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Theory]
    [InlineData("${ENV_A}x")]
    [InlineData("${ENV_A}${ENV_B}")]
    public void An_environment_that_is_not_one_variable_or_a_fixed_value_is_a_conflict(string environment)
    {
        // Review av #60: advise leste den første variabelen og så bort fra resten.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", "{ \"workspace\": { \"environment\": \"" + environment + "\" }, \"queues\": {} }");

        FlowConflict conflict = Assert.Single(Advise(StripeIntent).Flow.Conflicts);

        Assert.Equal(("unsupported", "environment"), (conflict.Kind, conflict.Field));
        Assert.Contains("reads variables in a form advise cannot follow", conflict.Message, StringComparison.Ordinal);
    }

    // ── advise --profile (review av #60) ─────────────────────────────────
    //
    // Profiler som deler et miljø, som eu og us i prod, kan ikke skilles av et oppgitt miljø, og spørsmålet ba om det.

    private const string EuAndUs = """
        {
          "workspace": { "environment": "prod" },
          "queues": {},
          "profiles": {
            "eu": { "variables": { "ORDERS_HOST": "eu.example.com" } },
            "us": { "variables": { "ORDERS_HOST": "us.example.com" } }
          }
        }
        """;

    [Fact]
    public void Profiles_that_share_an_environment_ask_for_the_profile_by_name()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", EuAndUs);

        FlowConflict conflict = Assert.Single(Advise(StripeIntent).Flow.Conflicts);

        Assert.Equal(("ambiguous", "environment"), (conflict.Kind, conflict.Field));
        Assert.Contains("Every profile gives prod, so the environment cannot tell them apart: name the profile with advise --profile (eu, us).",
            conflict.Question, StringComparison.Ordinal);
    }

    [Fact]
    public void Advise_profile_names_the_profile_and_its_environment_decides_the_rest()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", EuAndUs);

        FlowAdvice advice = Advise(StripeIntent, profile: "us");
        FlowDesign design = advice.Design!;

        Assert.Equal(("prod", Provenance.Evidence), (advice.Flow.String("environment"), advice.Flow["environment"]!.Provenance));
        Assert.Equal(new[] { "eu", "us" }, design.Content["profiles"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal("http", design.Content["profiles"]!["us"]!["variables"]!["QUEUEY_STRIPE_DELIVERY_KIND"]!.GetValue<string>());
        Assert.Equal("queuey credentials request stripe-whsec --profile us", Assert.Single(design.Credentials).Store);
        Assert.Contains(design.NextSteps, s => s.StartsWith("queuey apply --profile us.", StringComparison.Ordinal));
    }

    [Fact]
    public void Advise_profile_must_name_a_profile_the_file_has()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", EuAndUs);

        FlowConflict conflict = Assert.Single(Advise(StripeIntent, profile: "asia").Flow.Conflicts);

        Assert.Equal(("missing", "environment"), (conflict.Kind, conflict.Field));
        Assert.Contains("--profile asia names a profile queuey.deploy.json does not have: it has eu (prod), us (prod).", conflict.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Advise_profile_names_the_first_profile_of_a_new_file()
    {
        Fixture("stripe-aspnet");

        FlowDesign design = Advise(StripeIntent, profile: "laptop").Design!;

        Assert.Equal(new[] { "laptop" }, design.Content["profiles"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal("dev", design.Content["profiles"]!["laptop"]!["variables"]!["QUEUEY_WORKSPACE_ENVIRONMENT"]!.GetValue<string>());
        Assert.Contains(design.NextSteps, s => s.StartsWith("queuey apply --profile laptop.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Advise_profile_goes_with_intent_and_takes_a_profile_name()
    {
        Fixture("stripe-aspnet");
        File_("flow.json", StripeIntent);

        CliRun alone = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--profile", "eu", "--json" }));
        CliRun bad = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(
            new[] { _root, "--intent", Path.Combine(_root, "flow.json"), "--profile", "qak_kid.secret", "--json" }));

        Assert.Equal(ExitCodes.Usage, alone.Exit);
        Assert.Equal("profile_needs_intent", JsonDocument.Parse(alone.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(ExitCodes.Usage, bad.Exit);
        Assert.Equal("invalid_value", JsonDocument.Parse(bad.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("qak_kid", bad.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_queue_the_file_already_forwards_to_a_listener_in_a_workspace_counted_as_prod_is_a_conflict()
    {
        // Re-review av #59: en fil fra en eldre advise kan ha kind localForward og intet miljø. Designet for prod ville pekt et
        // ekte endepunkt mot en kø som leverer til en laptop, og ingen starter lytteren.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "queues": { "stripe": { "delivery": { "url": "/api/stripe", "kind": "localForward" } } } }""");

        FlowAdvice advice = Advise(StripeIntent);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("ambiguous", "environment", "\"localForward\""), (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));
        Assert.Contains("forwards queues.stripe to a local listener", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("State the environment dev in the intent", conflict.Question, StringComparison.Ordinal);
        Assert.Contains("set queues.stripe.delivery.kind to http", conflict.Question, StringComparison.Ordinal);
        Assert.Null(advice.Design);

        // Med dev oppgitt er lytteren riktig, og designet beholder den.
        FlowDesign dev = Advise("""
            {
              "environment": { "value": "dev", "provenance": "stated" },
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/api/stripe", "provenance": "stated" } }
            }
            """).Design!;
        Assert.Equal("localForward", dev.Content["queues"]!["stripe"]!["delivery"]!["kind"]!.GetValue<string>());
        Assert.Contains(dev.NextSteps, s => s.StartsWith("queuey listen --queue stripe", StringComparison.Ordinal));
    }

    [Fact]
    public void An_app_flow_reuses_the_queuey_registration_that_is_there()
    {
        File_("Orders.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="Queuey.Client" Version="0.1.0" /></ItemGroup></Project>""");
        File_("Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddQueueyClient(o => o.ApiKey = builder.Configuration["Queuey:ApiKey"]!);
            """);

        FlowAdvice advice = FlowAdvisor.Advise(DesiredFlow.Parse("""
            {
              "queue": { "value": "orders", "provenance": "stated" },
              "source": { "kind": { "value": "app", "provenance": "stated" } },
              "destination": {
                "route": { "value": "/orders", "provenance": "stated" },
                "baseUrl": { "value": "https://fulfilment.example.com", "provenance": "stated" }
              }
            }
            """), FlowScan.Scan(_root), _root, sending: Recommendation.For(RepoScan.Scan(_root)));

        Assert.Empty(advice.Flow.Conflicts);
        Assert.Contains(advice.Flow.Assumptions, a => a.Contains("not in this repository", StringComparison.Ordinal));
        Assert.Contains(advice.Existing, e => e.File == "Orders.csproj" && e.What == "Queuey.Client is a package reference");
        Assert.Contains(advice.Existing, e => e.File == "Program.cs" && e.Line == 2 && e.What == "registers Queuey with AddQueueyClient");
        CodeStep keep = Assert.Single(advice.Design!.Code);
        Assert.Equal(("keep", "Program.cs", 2), (keep.Action, keep.File, keep.Line ?? 0));
        Assert.Equal("ApiKey", advice.Design.Content["queues"]!["orders"]!["ingress"]!["authMode"]!.GetValue<string>());
    }

    // ── hemmeligheter og payload ─────────────────────────────────────────

    [Fact]
    public async Task A_value_in_a_dotenv_file_or_a_migration_never_reaches_the_output()
    {
        Fixture("supabase-db-webhook");
        File_(".env", "ORDERS_WEBHOOK_SECRET=dotenv-value-that-must-never-be-printed\n# a comment\nexport OTHER_TOKEN='another-value-never-printed'\n");
        File_(".env.local", "STRIPE_WEBHOOK_SECRET=local-value-never-printed\n");
        File_("intent.json", """{ "source": { "kind": { "value": "supabase", "provenance": "stated" } } }""");

        FlowFacts facts = FlowScan.Scan(_root);
        Assert.Contains(facts.EnvNames, e => e.Name == "ORDERS_WEBHOOK_SECRET" && e.File == ".env" && e.Line == 1);
        Assert.Contains(facts.EnvNames, e => e.Name == "OTHER_TOKEN" && e.Line == 3);

        string intent = Path.Combine(_root, "intent.json");
        CliRun json = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", intent, "--json" }));
        CliRun text = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", intent }));
        CliRun plain = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--json" }));

        Assert.Equal(ExitCodes.Success, json.Exit);
        Assert.Contains(".env", json.Stdout, StringComparison.Ordinal);   // navnet og fila står der …
        foreach (CliRun run in new[] { json, text, plain })
        {
            string all = run.Stdout + run.Stderr;
            Assert.DoesNotContain("dotenv-value-that-must-never-be-printed", all, StringComparison.Ordinal);   // … verdien aldri
            Assert.DoesNotContain("another-value-never-printed", all, StringComparison.Ordinal);
            Assert.DoesNotContain("local-value-never-printed", all, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-secret-in-a-migration-never-shown", all, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdefghijklmnop", all, StringComparison.Ordinal);   // migrasjonens URL viser bare stien
        }
    }

    [Fact]
    public void A_payload_example_in_the_repository_is_not_shown()
    {
        Fixture("stripe-express");
        File_("src/routes/sample-event.js", """
            module.exports = { id: 'evt_payload_example_never_shown', type: 'checkout.session.completed', customer_email: 'jane@example.com' };
            """);
        File_("fixtures/checkout.json", """{ "id": "evt_fixture_never_shown", "data": { "object": { "amount": 4200 } } }""");

        FlowAdvice advice = Advise("""{ "source": { "kind": { "value": "stripe", "provenance": "stated" } } }""");
        string output = advice.Flow.ToJson(null).ToJsonString() + advice.Design!.Content.ToJsonString()
                        + string.Join("", advice.Design.Code.Select(c => c.What + c.Why)) + string.Join("", advice.Design.NextSteps);

        Assert.DoesNotContain("evt_payload_example_never_shown", output, StringComparison.Ordinal);
        Assert.DoesNotContain("jane@example.com", output, StringComparison.Ordinal);
        Assert.DoesNotContain("evt_fixture_never_shown", output, StringComparison.Ordinal);
    }

    // ── kandidatene ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("stripe-aspnet", "Stripe → Queuey → POST /api/stripe (aspnet, Controllers/StripeWebhookController.cs:")]
    [InlineData("stripe-express", "Stripe → Queuey → POST /webhooks/stripe (express, src/routes/webhooks.js:")]
    [InlineData("stripe-nextjs", "Stripe → Queuey → POST /api/webhooks/stripe (nextjs, app/api/webhooks/stripe/route.ts:")]
    [InlineData("lovable-supabase", "Stripe → Queuey → POST /functions/v1/stripe-webhook (supabase-edge, supabase/functions/stripe-webhook/index.ts:")]
    [InlineData("supabase-db-webhook", "Supabase → Queuey → POST /functions/v1/orders-hook (supabase-edge, supabase/functions/orders-hook/index.ts:")]
    public void Without_an_intent_each_handler_is_a_candidate_whose_fields_all_rest_on_evidence_or_assumption(string fixture, string summary)
    {
        Fixture(fixture);

        FlowCandidate candidate = Assert.Single(FlowAdvisor.Candidates(FlowScan.Scan(_root), _root));

        Assert.StartsWith(summary, candidate.Summary, StringComparison.Ordinal);
        Assert.Empty(candidate.Flow.Conflicts);
        Assert.All(candidate.Flow.Fields, f => Assert.NotEqual(Provenance.Stated, f.Value.Provenance));
        Assert.All(candidate.Flow.Fields.Where(f => f.Value.Provenance == Provenance.Evidence), f =>
            Assert.All(f.Value.Evidence, e => Assert.True(e.Line is >= 1 || e.File.EndsWith(".json", StringComparison.Ordinal), $"{f.Spec.Path}: {e}")));
    }

    [Fact]
    public void A_candidate_passed_back_as_the_intent_gives_the_same_flow()
    {
        Fixture("stripe-express");
        DesiredFlow candidate = FlowAdvisor.Candidates(FlowScan.Scan(_root), _root).Single().Flow;

        // Agenten bekrefter kilden og ruten, og sender resten tilbake som den fikk det.
        JsonObject json = candidate.ToJson(FlowSchema.Url);
        json["source"]!["kind"]!["provenance"] = "stated";
        json["destination"]!["route"]!["provenance"] = "stated";
        FlowAdvice advice = Advise(json.ToJsonString());

        Assert.Empty(advice.Flow.Conflicts);
        Assert.Equal(candidate.Fields.Where(f => f.Spec.Path is not ("source.kind" or "destination.route")).Select(f => (f.Spec.Path, f.Value.Value.ToJsonString())),
                     advice.Flow.Fields.Where(f => f.Spec.Path is not ("source.kind" or "destination.route")).Select(f => (f.Spec.Path, f.Value.Value.ToJsonString())));
    }

    // ── kommandoen ───────────────────────────────────────────────────────

    [Fact]
    public async Task advise_intent_json_is_versioned_and_says_whether_it_proposed()
    {
        Fixture("stripe-aspnet");
        File_("flow.json", StripeIntent);

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json"), "--json" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal(new[] { "schemaVersion", "path", "intent", "outcome", "flow", "existing", "scanLimited", "infrastructure", "code", "nextSteps" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("proposed", root.GetProperty("outcome").GetString());
        Assert.Equal(FlowSchema.Url, root.GetProperty("flow").GetProperty("$schema").GetString());
        Assert.Equal("queuey.deploy.json", root.GetProperty("infrastructure").GetProperty("file").GetString());
        Assert.Contains(root.GetProperty("infrastructure").GetProperty("settings").EnumerateArray(),
            s => s.GetProperty("path").GetString() == "queues.stripe.delivery.signing" && s.GetProperty("basis").GetString() == "evidence");
        Assert.False(File.Exists(Path.Combine(_root, "queuey.deploy.json")));   // den skriver ingenting

        // Flyten i svaret er en Desired Flow som kan leses inn igjen.
        DesiredFlow.Parse(root.GetProperty("flow").GetRawText());
    }

    [Fact]
    public async Task advise_intent_stops_at_a_conflict_with_exit_1_and_proposes_nothing()
    {
        Fixture("stripe-aspnet");
        File_("flow.json", StripeIntent.Replace("/api/stripe", "/api/payments", StringComparison.Ordinal));

        CliRun json = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json"), "--json" }));
        CliRun text = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json") }));

        Assert.Equal(ExitCodes.RuntimeError, json.Exit);
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal("conflicts", root.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("infrastructure").ValueKind);
        Assert.Empty(root.GetProperty("code").EnumerateArray());
        Assert.Equal(AdviseCommand.ConflictStep, Assert.Single(root.GetProperty("nextSteps").EnumerateArray()).GetString());
        Assert.Equal("destination.route", Assert.Single(root.GetProperty("flow").GetProperty("conflicts").EnumerateArray()).GetProperty("field").GetString());

        Assert.Equal(ExitCodes.RuntimeError, text.Exit);
        Assert.Contains("Stopped: nothing is proposed", text.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Why each setting", text.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task advise_intent_for_a_person_shows_where_each_field_came_from_and_why_each_setting()
    {
        Fixture("stripe-aspnet");
        File_("flow.json", StripeIntent);

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json") }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("Controllers/StripeWebhookController.cs:", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Why each setting", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Code (the application's half", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_queue_option_names_the_queue_as_stated()
    {
        Fixture("stripe-aspnet");
        File_("flow.json", StripeIntent);

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(
            new[] { _root, "--intent", Path.Combine(_root, "flow.json"), "--queue", "Payments", "--json" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        JsonElement queue = root.GetProperty("flow").GetProperty("queue");
        Assert.Equal(("payments", "stated"), (queue.GetProperty("value").GetString(), queue.GetProperty("provenance").GetString()));
        Assert.True(root.GetProperty("infrastructure").GetProperty("content").GetProperty("queues").TryGetProperty("payments", out _));
    }

    [Theory]
    [InlineData("--write-files")]
    [InlineData("--apply")]
    [InlineData("--force")]
    public async Task advise_intent_refuses_a_flag_that_would_write(string flag)
    {
        Fixture("stripe-aspnet");
        File_("flow.json", StripeIntent);

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json"), flag, "--json" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal("intent_writes_nothing", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(_root, "queuey.deploy.json")));
    }

    [Fact]
    public async Task An_intent_that_cannot_be_read_is_a_usage_error_naming_the_place()
    {
        Fixture("stripe-aspnet");
        File_("flow.json", """{ "source": { "kind": { "value": "stripe" } } }""");

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json"), "--json" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("invalid_intent", error.GetProperty("code").GetString());
        Assert.Contains("source.kind needs a provenance", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_intent_file_is_a_file_error()
    {
        Fixture("stripe-aspnet");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", _root, "--intent", Path.Combine(_root, "nope.json"), "--json" }));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Equal("file_unreadable", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task advise_json_without_an_intent_is_versioned_and_lists_the_candidates()
    {
        Fixture("lovable-supabase");

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--json" }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("schemaVersion", root.EnumerateObject().First().Name);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        JsonElement candidate = Assert.Single(root.GetProperty("candidates").EnumerateArray());
        Assert.StartsWith("Stripe → Queuey → POST /functions/v1/stripe-webhook", candidate.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("evidence", candidate.GetProperty("flow").GetProperty("source").GetProperty("kind").GetProperty("provenance").GetString());
        Assert.True(root.TryGetProperty("headline", out _));   // det som var der, er der fortsatt
    }

    [Fact]
    public async Task schema_flow_prints_the_desired_flows_schema()
    {
        CliRun flow = await CliHarness.RunAsync(() => Task.FromResult(SchemaCommand.Run(new[] { "--flow" })));
        CliRun deploy = await CliHarness.RunAsync(() => Task.FromResult(SchemaCommand.Run(Array.Empty<string>())));

        Assert.Equal(ExitCodes.Success, flow.Exit);
        Assert.Equal(FlowSchema.Json, flow.Stdout);
        Assert.Equal(DeploymentFile.JsonSchema, deploy.Stdout);
    }

    // ── hele fila (B1 i review av #56) ───────────────────────────────────

    [Fact]
    public void An_existing_deployment_file_comes_back_whole_with_the_flows_queue_added()
    {
        // Agenten skriver content som det er. Før var det et utdrag, og orders, refunds og workspace-blokka forsvant.
        Fixture("stripe-aspnet");
        const string existing = """
            {
              "workspace": { "delivery": { "baseUrl": "https://api.example.com", "timeoutMs": 10000 }, "retentionDays": 14 },
              "queues": {
                "orders": { "delivery": { "url": "/orders" }, "ordering": "bykey", "ingress": { "groupKey": { "from": "body", "name": "customer_id" } } },
                "refunds": { "delivery": { "url": "/refunds" }, "dlqEnabled": true }
              }
            }
            """;
        File_("queuey.deploy.json", existing);

        FlowDesign design = Advise(StripeIntent).Design!;

        JsonObject before = JsonNode.Parse(existing)!.AsObject();
        Assert.True(JsonNode.DeepEquals(before["workspace"], design.Content["workspace"]));
        Assert.True(JsonNode.DeepEquals(before["queues"]!["orders"], design.Content["queues"]!["orders"]));
        Assert.True(JsonNode.DeepEquals(before["queues"]!["refunds"], design.Content["queues"]!["refunds"]));
        Assert.Equal(new[] { "orders", "refunds", "stripe" }, design.Content["queues"]!.AsObject().Select(q => q.Key).ToArray());
        Assert.Equal("/api/stripe", design.Content["queues"]!["stripe"]!["delivery"]!["url"]!.GetValue<string>());
        Assert.Null(design.Content["workspace"]!["environment"]);   // antatt dev skrives ikke inn i en fil som ikke har noe
        Assert.Contains("it adds queues.stripe", design.Merge, StringComparison.Ordinal);
        Assert.StartsWith("Write infrastructure.content to queuey.deploy.json as it is: it is the whole file", design.NextSteps[0], StringComparison.Ordinal);

        DeploymentFile.Parse(design.Content.ToJsonString()).Resolve();
    }

    [Fact]
    public void What_the_intent_does_not_state_gives_way_to_the_file_and_the_reason_says_so()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "queues": {
                "stripe": {
                  "ordering": "fifo",
                  "dlqEnabled": false,
                  "delivery": { "url": "https://hooks.example.com/api/stripe", "kind": "http" }
                }
              }
            }
            """);

        FlowDesign design = Advise(StripeIntent).Design!;
        JsonNode queue = design.Content["queues"]!["stripe"]!;

        Assert.Equal("fifo", queue["ordering"]!.GetValue<string>());
        Assert.False(queue["dlqEnabled"]!.GetValue<bool>());
        Assert.Equal("https://hooks.example.com/api/stripe", queue["delivery"]!["url"]!.GetValue<string>());
        Assert.Equal("http", queue["delivery"]!["kind"]!.GetValue<string>());
        Assert.Equal("SignedRequest", queue["ingress"]!["authMode"]!.GetValue<string>());   // det fila ikke hadde, legges til

        DesignSetting ordering = Assert.Single(design.Settings, s => s.Path == "queues.stripe.ordering");
        Assert.Equal(("evidence", "\"fifo\""), (ordering.Basis, ordering.Value!.ToJsonString()));
        Assert.Contains("Kept as queuey.deploy.json has it", ordering.Because, StringComparison.Ordinal);
        Assert.Contains("queues.stripe.ordering stays as the file has it", design.Merge, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stated_route_the_file_contradicts_on_the_same_queue_is_a_conflict_not_an_overwrite()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "queues": { "stripe": { "delivery": { "url": "https://api.example.com/webhooks/stripe" } } } }""");

        FlowAdvice advice = Advise(StripeIntent);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("contradiction", "destination.route"), (conflict.Kind, conflict.Field));
        Assert.Equal(("\"/api/stripe\"", "\"/webhooks/stripe\""), (conflict.Stated!.ToJsonString(), conflict.Found!.ToJsonString()));
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_stated_source_the_files_ingress_contradicts_is_a_conflict()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "queues": { "stripe": { "ingress": { "authMode": "ApiKey" } } } }""");

        FlowAdvice advice = Advise(StripeIntent);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("contradiction", "source.kind", "\"ApiKey\""), (conflict.Kind, conflict.Field, conflict.Found!.ToJsonString()));
        Assert.Contains("queues.stripe.ingress.authMode", conflict.Message, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stated_profile_value_the_file_contradicts_is_a_conflict(bool devStated)
    {
        // Uten oppgitt miljø brukes den ene profilen fila har (re-review av #59), så verdien går i profilen dev også da, selv om
        // fila uten miljø gjelder et workspace Queuey regner som prod.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "queues": {},
              "profiles": { "dev": { "variables": { "QUEUEY_BASE_URL": "https://dev.example.com" } } }
            }
            """);

        string environment = devStated ? "\"environment\": { \"value\": \"dev\", \"provenance\": \"stated\" }, " : "";
        FlowConflict conflict = Assert.Single(Advise("{ " + environment +
            "\"source\": { \"kind\": { \"value\": \"stripe\", \"provenance\": \"stated\" } }, " +
            "\"destination\": { \"baseUrl\": { \"value\": \"https://staging.example.com\", \"provenance\": \"stated\" } } }").Flow.Conflicts);

        Assert.Equal(("contradiction", "destination.baseUrl"), (conflict.Kind, conflict.Field));
        Assert.Equal("\"https://dev.example.com\"", conflict.Found!.ToJsonString());
    }

    [Fact]
    public void A_deployment_file_apply_cannot_read_is_a_conflict_not_a_fragment()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "queues": { "stripe": { "mode": "sometimes" } } }""");

        FlowAdvice advice = Advise(StripeIntent);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("unsupported", "queuey.deploy.json"), (conflict.Kind, conflict.Field));
        Assert.Contains("apply cannot read it as it is", conflict.Message, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_deployment_file_with_comments_is_kept_and_the_merge_says_they_are_not_in_the_content()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              // the orders queue
              "queues": { "orders": { "delivery": { "url": "https://orders.example.com/hook" } } },
            }
            """);

        FlowDesign design = Advise(StripeIntent).Design!;

        Assert.Equal("https://orders.example.com/hook", design.Content["queues"]!["orders"]!["delivery"]!["url"]!.GetValue<string>());
        Assert.Contains("comments or trailing commas", design.Merge, StringComparison.Ordinal);
    }

    // ── det fila ikke kan holde, blir en konflikt (S1) ───────────────────

    [Fact]
    public void A_variable_a_deployment_file_may_not_read_is_a_conflict_with_the_way_out()
    {
        // ${QUEUEY_STAGE} er en av CLI-ens egne innstillinger (#55), og profilen kan ikke gi den en verdi.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "workspace": { "environment": "${QUEUEY_STAGE}" },
              "queues": {},
              "profiles": { "prod": { "variables": { "ORDERS_HOST": "orders.example.com" } } }
            }
            """);

        FlowAdvice advice = Advise(StripeIntent);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("unsupported", "environment"), (conflict.Kind, conflict.Field));
        Assert.Contains("QUEUEY_STAGE", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("Rename ${QUEUEY_STAGE}", conflict.Question, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_base_url_a_profile_cannot_hold_is_a_conflict_that_points_to_the_environment()
    {
        // Et ngrok-navn i heks ser ut som en hemmelighet for profilregelen, og ble en InternalError før.
        Fixture("stripe-aspnet");

        FlowAdvice advice = Advise("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "baseUrl": { "value": "https://9f86d081884c7d659a2feaa0c55ad015.ngrok-free.app", "provenance": "stated" } }
            }
            """);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("unsupported", "destination.baseUrl"), (conflict.Kind, conflict.Field));
        Assert.Contains("set QUEUEY_BASE_URL where plan and apply run", conflict.Question, StringComparison.Ordinal);
        Assert.DoesNotContain("9f86d081884c7d659a2feaa0c55ad015", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_design_apply_would_refuse_is_a_conflict_in_the_output_never_an_internal_error()
    {
        Fixture("stripe-aspnet");
        File_("flow.json", """
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "baseUrl": { "value": "https://9f86d081884c7d659a2feaa0c55ad015.ngrok-free.app", "provenance": "stated" } }
            }
            """);

        CliRun json = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json"), "--json" }));
        CliRun text = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json") }));

        Assert.Equal(ExitCodes.RuntimeError, json.Exit);
        Assert.Equal("conflicts", JsonDocument.Parse(json.Stdout).RootElement.GetProperty("outcome").GetString());
        Assert.Equal(ExitCodes.RuntimeError, text.Exit);
        Assert.Contains("Stopped: nothing is proposed", text.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", text.Stdout + text.Stderr, StringComparison.Ordinal);
    }

    // ── hvem som lagrer hemmeligheten, og hva en lokal leveranse bærer (playbookene fra F2.11) ──
    //
    // F2.9: en hemmelighet agenten ikke skal holde, limer en person inn på siden credentials request åpner. Testhemmeligheten
    // Stripe CLI-en gir, kan agenten holde, i en variabel og aldri i utdata. Før sa advise credentials set også for et ekte
    // Stripe-endepunkt, og at en lokal leveranse bar Queuey sin signatur, i et Supabase-design uten signering.

    [Fact]
    public void A_real_stripe_endpoints_secret_is_asked_of_a_person_and_stored_before_the_endpoint_points_here()
    {
        Fixture("stripe-nextjs");

        FlowDesign design = Advise("""
            {
              "environment": { "value": "prod", "provenance": "stated" },
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "baseUrl": { "value": "https://shop.example.com/", "provenance": "stated" } }
            }
            """).Design!;

        Assert.Equal("queuey credentials request stripe-whsec --profile prod", Assert.Single(design.Credentials).Store);
        List<string> steps = design.NextSteps.ToList();
        int request = steps.FindIndex(s => s.Contains("queuey credentials request stripe-whsec --profile prod prints a link where a person pastes it",
            StringComparison.Ordinal));
        int endpoint = steps.FindIndex(s => s.Contains("Stripe webhook endpoint", StringComparison.Ordinal));
        Assert.True(request >= 0 && request < endpoint, string.Join(" | ", steps));
        Assert.Contains("queuey credentials list --profile prod --json lists stripe-whsec", steps[request], StringComparison.Ordinal);
        Assert.DoesNotContain(steps, s => s.Contains("STRIPE_WHSEC", StringComparison.Ordinal) || s.Contains("queuey credentials set", StringComparison.Ordinal));
        Assert.Contains("credentials set --from-env is only for the test secret", steps[request], StringComparison.Ordinal);
    }

    [Fact]
    public void Test_mode_stores_the_stripe_clis_own_secret_from_a_variable_and_says_a_real_endpoints_is_asked_for()
    {
        Fixture("stripe-aspnet");

        FlowDesign design = Advise(StripeIntent).Design!;

        string set = "queuey credentials set --profile dev --name stripe-whsec --type HmacSigning --key-id stripe-whsec --from-env STRIPE_WHSEC";
        Assert.Equal(set, Assert.Single(design.Credentials).Store);
        string step = Assert.Single(design.NextSteps, s => s.StartsWith("Test mode", StringComparison.Ordinal));
        // Én kommando (K1 i runde 2 av #58): i et agentverktøy er hvert kall et nytt skall.
        Assert.Contains("In a POSIX shell: STRIPE_WHSEC=\"$(stripe listen --print-secret)\" " + set + ".", step, StringComparison.Ordinal);
        // $env: varer hele økten, så variabelen fjernes etterpå (re-review av #58).
        Assert.Contains("In PowerShell: $env:STRIPE_WHSEC = stripe listen --print-secret; " + set + "; Remove-Item Env:STRIPE_WHSEC,",
            step, StringComparison.Ordinal);
        Assert.Contains("so it stays out of the output", step, StringComparison.Ordinal);
        Assert.Contains("A real endpoint's secret is never yours to hold: a person pastes it on the page queuey credentials request " +
                        "stripe-whsec --profile dev opens", step, StringComparison.Ordinal);
        Assert.DoesNotContain(design.NextSteps, s => s.Contains("prints this session's signing secret", StringComparison.Ordinal));
    }

    // Et umerket workspace er prod, og et antatt miljø skrives bare inn i en ny fil (review av #58, B2). En fil som finnes uten
    // miljø, får derfor credentials request og et ekte endepunkt, som plan og apply gir, og testmodus bare når dev er oppgitt.

    [Fact]
    public void An_existing_file_without_an_environment_gets_a_real_endpoint_and_a_request_not_test_mode()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "queues": {} }""");

        FlowDesign design = Advise(StripeIntent).Design!;

        Assert.Equal("queuey credentials request stripe-whsec", Assert.Single(design.Credentials).Store);
        Assert.DoesNotContain(design.NextSteps, s => s.StartsWith("Test mode", StringComparison.Ordinal)
                                                   || s.StartsWith("Apply it to a workspace set to dev", StringComparison.Ordinal)
                                                   || s.Contains("queuey credentials set", StringComparison.Ordinal));
        Assert.Contains(design.NextSteps, s => s.Contains("queuey credentials request stripe-whsec prints a link where a person pastes it",
            StringComparison.Ordinal));
        Assert.Contains(design.NextSteps, s => s.StartsWith("For test mode with stripe listen instead, state the environment dev in the " +
                                                            "intent", StringComparison.Ordinal));
        // Hele designet er for prod (oppfølging av #58): HTTP, og ingen lytter som et ekte endepunkt ville levert til.
        Assert.DoesNotContain(design.NextSteps, s => s.Contains("queuey listen", StringComparison.Ordinal));
        Assert.Equal("http", design.Content["queues"]!["stripe"]!["delivery"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void An_existing_file_without_an_environment_gets_test_mode_when_the_intent_states_dev()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "queues": {} }""");

        FlowDesign design = Advise("""
            {
              "environment": { "value": "dev", "provenance": "stated" },
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/api/stripe", "provenance": "stated" } }
            }
            """).Design!;

        Assert.Equal("queuey credentials set --name stripe-whsec --type HmacSigning --key-id stripe-whsec --from-env STRIPE_WHSEC",
            Assert.Single(design.Credentials).Store);
        Assert.Contains(design.NextSteps, s => s.StartsWith("Test mode", StringComparison.Ordinal));
        Assert.DoesNotContain(design.NextSteps, s => s.StartsWith("For test mode", StringComparison.Ordinal));
        Assert.Equal("dev", design.Content["workspace"]!["environment"]!.GetValue<string>());   // det oppgitte skrives inn
    }

    [Fact]
    public void A_file_that_takes_its_environment_from_a_variable_without_profiles_is_not_taken_for_dev()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """{ "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}" }, "queues": {} }""");

        FlowDesign design = Advise(StripeIntent).Design!;

        Assert.Equal("queuey credentials request stripe-whsec", Assert.Single(design.Credentials).Store);
        Assert.DoesNotContain(design.NextSteps, s => s.StartsWith("Test mode", StringComparison.Ordinal));
    }

    // Hele designet følger miljøet i praksis (oppfølging av #58, før tag). Før antok advise dev også for en fil som fantes uten
    // miljø, og designet ble en blanding: localForward og queuey listen, mens Stripe-stegene pekte et ekte endepunkt mot køen,
    // i et workspace Queuey regner som prod.

    [Theory]
    [InlineData("stripe", false)]
    [InlineData("stripe", true)]
    [InlineData("supabase", false)]
    [InlineData("supabase", true)]
    [InlineData("app", false)]
    [InlineData("app", true)]
    public void An_existing_file_without_an_environment_is_designed_for_prod_and_for_dev_only_when_the_intent_says_so(string kind, bool devStated)
    {
        (string queue, string intent) = Repository(kind, devStated);
        File_("queuey.deploy.json", """{ "queues": {} }""");

        FlowAdvice advice = Advise(intent);
        FlowDesign design = advice.Design!;

        Assert.Equal((devStated ? "dev" : "prod", devStated ? Provenance.Stated : Provenance.Assumed),
            (advice.Flow.String("environment"), advice.Flow["environment"]!.Provenance));
        Assert.Equal(devStated ? "localForward" : "http", design.Content["queues"]![queue]!["delivery"]!["kind"]!.GetValue<string>());
        Assert.Equal(devStated, design.NextSteps.Any(s => s.Contains("queuey listen", StringComparison.Ordinal)));
        Assert.Equal(devStated, design.NextSteps.Any(s => s.StartsWith("Apply it to a workspace set to dev", StringComparison.Ordinal)));
        Assert.StartsWith(devStated ? "queuey credentials set " : "queuey credentials request ", Assert.Single(design.Credentials).Store,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_that_already_gives_the_environment_another_value_decides_it()
    {
        // Re-review av #58: profilen dev i fila sier test, og fila vinner over det advise bare antar.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}" },
              "queues": {},
              "profiles": { "dev": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "test" } } }
            }
            """);

        FlowDesign design = Advise(StripeIntent).Design!;

        JsonNode variables = design.Content["profiles"]!["dev"]!["variables"]!;
        Assert.Equal("test", variables["QUEUEY_WORKSPACE_ENVIRONMENT"]!.GetValue<string>());
        Assert.Equal("http", variables["QUEUEY_STRIPE_DELIVERY_KIND"]!.GetValue<string>());
        Assert.Equal("queuey credentials request stripe-whsec --profile dev", Assert.Single(design.Credentials).Store);
        Assert.DoesNotContain(design.NextSteps, s => s.StartsWith("Test mode", StringComparison.Ordinal)
                                                   || s.Contains("queuey listen", StringComparison.Ordinal));
    }

    /// <summary>A repository with a handler of <paramref name="kind"/>, and an intent for it: the queue's name, and the JSON.</summary>
    private (string Queue, string Intent) Repository(string kind, bool devStated)
    {
        string environment = devStated ? "\"environment\": { \"value\": \"dev\", \"provenance\": \"stated\" }, " : "";
        switch (kind)
        {
            case "stripe":
                Fixture("stripe-aspnet");
                return ("stripe", "{ " + environment + "\"source\": { \"kind\": { \"value\": \"stripe\", \"provenance\": \"stated\" } }, " +
                                  "\"destination\": { \"route\": { \"value\": \"/api/stripe\", \"provenance\": \"stated\" } } }");
            case "supabase":
                Fixture("supabase-db-webhook");
                return ("orders", "{ " + environment + "\"source\": { \"kind\": { \"value\": \"supabase\", \"provenance\": \"stated\" } } }");
            default:
                File_("package.json", """{ "name": "orders", "dependencies": { "express": "^4" } }""");
                File_("server.js", """
                    app.post('/hooks/orders', express.json(), (req, res) => {
                      if (req.get('x-webhook-secret') !== process.env.ORDERS_HOOK_SECRET) return res.sendStatus(401);
                      res.sendStatus(200);
                    });
                    """);
                return ("orders", "{ " + environment + "\"queue\": { \"value\": \"orders\", \"provenance\": \"stated\" }, " +
                                  "\"source\": { \"kind\": { \"value\": \"app\", \"provenance\": \"stated\" } }, " +
                                  "\"destination\": { \"route\": { \"value\": \"/hooks/orders\", \"provenance\": \"stated\" } } }");
        }
    }

    [Fact]
    public void Outside_dev_the_secret_the_handler_checks_is_asked_of_a_person_with_its_type()
    {
        Fixture("supabase-db-webhook");

        FlowDesign design = Advise("""
            {
              "environment": { "value": "prod", "provenance": "stated" },
              "source": { "kind": { "value": "supabase", "provenance": "stated" } },
              "destination": { "baseUrl": { "value": "https://abc.supabase.co", "provenance": "stated" } }
            }
            """).Design!;

        // --type må med: uten den ber en forespørsel om en HmacSigning-hemmelighet, som Queuey aldri sender som den er.
        string request = "queuey credentials request orders-webhook-secret --type ApiKeyHeader --profile prod";
        Assert.Equal(request, Assert.Single(design.Credentials).Store);
        string step = Assert.Single(design.NextSteps, s => s.StartsWith("Store the secret the handler checks", StringComparison.Ordinal));
        Assert.Contains(request + " prints a link for them", step, StringComparison.Ordinal);
        Assert.Contains("A value that is yours to hold goes in with queuey credentials set --profile prod --name orders-webhook-secret " +
                        "--type ApiKeyHeader --from-env ORDERS_WEBHOOK_SECRET instead.", step, StringComparison.Ordinal);
    }

    [Fact]
    public void In_dev_the_secret_the_handler_checks_is_set_from_a_variable_or_asked_for_when_it_is_not_yours()
    {
        Fixture("supabase-db-webhook");

        FlowDesign design = Advise("""{ "source": { "kind": { "value": "supabase", "provenance": "stated" } } }""").Design!;

        string step = Assert.Single(design.NextSteps, s => s.StartsWith("Store the secret the handler checks", StringComparison.Ordinal));
        Assert.Contains("Run queuey credentials set --profile dev --name orders-webhook-secret --type ApiKeyHeader --from-env " +
                        "ORDERS_WEBHOOK_SECRET from a shell where ORDERS_WEBHOOK_SECRET holds the value", step, StringComparison.Ordinal);
        Assert.Contains("a person pastes it instead: queuey credentials request orders-webhook-secret --type ApiKeyHeader --profile dev",
            step, StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_delivery_to_a_handler_that_checks_a_header_is_not_said_to_carry_a_signature_the_design_does_not_turn_on()
    {
        // T27: queuey listen sender ingen autentiseringshoder videre. Designet slår ikke på signering, så advise sier hva som
        // faktisk skjer: hodesjekken avviser lokale leveranser, og flyten bevises mot den deployede mottakeren.
        Fixture("supabase-db-webhook");

        FlowDesign design = Advise("""{ "source": { "kind": { "value": "supabase", "provenance": "stated" } } }""").Design!;

        Assert.Null(design.Content["queues"]!["orders"]!["delivery"]!["signing"]);
        CodeStep local = Assert.Single(design.Code, c => c.Action == "configure" && c.What.Contains("queuey listen does not pass", StringComparison.Ordinal));
        Assert.Contains("Prove the flow against the deployed receiver instead", local.What, StringComparison.Ordinal);
        Assert.Contains("only for a queue that signs its deliveries", local.Why, StringComparison.Ordinal);
        Assert.DoesNotContain("signature instead", local.Why, StringComparison.Ordinal);
        DesignSetting url = Assert.Single(design.Settings, s => s.Path == "queues.orders.delivery.url");
        Assert.DoesNotContain("Queuey signs a delivery for a listener", url.Because, StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_stripe_delivery_keeps_the_rule_for_when_a_listener_gets_the_signature()
    {
        Fixture("stripe-aspnet");

        FlowDesign design = Advise(StripeIntent).Design!;

        DesignSetting url = Assert.Single(design.Settings, s => s.Path == "queues.stripe.delivery.url");
        Assert.Contains("Queuey signs a delivery for a listener only when its URL is absolute", url.Because, StringComparison.Ordinal);
    }

    // ── rekkefølgen på stegene (S2) ──────────────────────────────────────

    [Fact]
    public void A_delivery_credential_is_stored_before_plan_and_apply_look_it_up()
    {
        Fixture("supabase-db-webhook");

        FlowDesign design = Advise("""{ "source": { "kind": { "value": "supabase", "provenance": "stated" } } }""").Design!;

        int store = design.NextSteps.ToList().FindIndex(s => s.StartsWith("Store the secret the handler checks", StringComparison.Ordinal));
        int plan = design.NextSteps.ToList().FindIndex(s => s.Contains("queuey plan", StringComparison.Ordinal));
        int apply = design.NextSteps.ToList().FindIndex(s => s.StartsWith("queuey apply --profile dev.", StringComparison.Ordinal));
        Assert.True(store >= 0 && store < plan && plan < apply, string.Join(" | ", design.NextSteps));
        Assert.Equal("delivery", Assert.Single(design.Credentials).For);
    }

    [Fact]
    public void An_app_flow_to_a_receiver_that_checks_a_secret_gets_the_credential_and_the_step_to_store_it()
    {
        File_("package.json", """{ "name": "orders", "dependencies": { "express": "^4" } }""");
        File_("server.js", """
            app.post('/hooks/orders', express.json(), (req, res) => {
              if (req.get('x-webhook-secret') !== process.env.ORDERS_HOOK_SECRET) return res.sendStatus(401);
              res.sendStatus(200);
            });
            """);

        FlowDesign design = Advise("""
            {
              "queue": { "value": "orders", "provenance": "stated" },
              "source": { "kind": { "value": "app", "provenance": "stated" } },
              "destination": { "route": { "value": "/hooks/orders", "provenance": "stated" } }
            }
            """).Design!;

        CredentialNeed credential = Assert.Single(design.Credentials);
        Assert.Equal(("orders-webhook-secret", "delivery"), (credential.Name, credential.For));
        Assert.Equal("x-webhook-secret", design.Content["queues"]!["orders"]!["delivery"]!["authHeaderName"]!.GetValue<string>());
        int store = design.NextSteps.ToList().FindIndex(s => s.Contains("--from-env ORDERS_HOOK_SECRET", StringComparison.Ordinal));
        int plan = design.NextSteps.ToList().FindIndex(s => s.Contains("queuey plan", StringComparison.Ordinal));
        Assert.True(store >= 0 && store < plan, string.Join(" | ", design.NextSteps));
    }

    [Fact]
    public void The_stripe_ingress_credential_may_wait_until_after_apply()
    {
        Fixture("stripe-aspnet");

        FlowDesign design = Advise(StripeIntent).Design!;

        Assert.Equal("ingress", Assert.Single(design.Credentials).For);
        int apply = design.NextSteps.ToList().FindIndex(s => s.StartsWith("queuey apply --profile dev.", StringComparison.Ordinal));
        int store = design.NextSteps.ToList().FindIndex(s => s.Contains("--name stripe-whsec", StringComparison.Ordinal));
        Assert.True(apply >= 0 && apply < store, string.Join(" | ", design.NextSteps));
    }

    // ── tekst fra repoet til terminalen (S3) ─────────────────────────────

    [Fact]
    public async Task Text_from_the_repository_reaches_the_terminal_without_escape_sequences_or_direction_overrides()
    {
        Fixture("stripe-aspnet");
        // En deploy-fil kan ha hva som helst i en streng, og den skrives ut som content og i konflikter.
        File_("queuey.deploy.json", "{ \"queues\": { \"orders\": { \"delivery\": { \"url\": \"https://orders.example.com/x\\u001b]0;pwned\\u0007\\u202e\" } } } }");
        File_("flow.json", StripeIntent);

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json") }));

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("https://orders.example.com/x", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', run.Stdout);
        Assert.DoesNotContain('\u0007', run.Stdout);
        Assert.DoesNotContain('‮', run.Stdout);
    }

    [Fact]
    public async Task A_parser_message_that_quotes_the_file_reaches_the_terminal_clean()
    {
        // Et kønavn som ikke er et kønavn, siteres av parseren, og meldingen står i konflikten.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", "{ \"queues\": { \"orders\\u001b[2J\\u0007\": {} } }");
        File_("flow.json", StripeIntent);

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json") }));

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("1. queuey.deploy.json (unsupported): queuey.deploy.json is there", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', run.Stdout);
        Assert.DoesNotContain('\u0007', run.Stdout);
    }

    // ── grensene på intensjonen ──────────────────────────────────────────

    [Fact]
    public async Task An_intent_larger_than_advise_reads_is_refused_without_being_read_whole()
    {
        Fixture("stripe-aspnet");
        File_("flow.json", "{ \"assumptions\": [\"" + new string('x', AdviseCommand.MaxIntentBytes) + "\"] }");

        CliRun run = await CliHarness.RunAsync(() => AdviseCommand.RunAsync(new[] { _root, "--intent", Path.Combine(_root, "flow.json"), "--json" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal("intent_too_large", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{ "source": { "eventTypes": { "value": ["checkout.session.completed; rm -rf ~"], "provenance": "stated" } } }""", "event type names")]
    [InlineData("""{ "destination": { "route": { "value": "/api/${HOME}", "provenance": "stated" } } }""", "may not contain ${")]
    [InlineData("""{ "destination": { "baseUrl": { "value": "https://${API_HOST}", "provenance": "stated" } } }""", "may not contain ${")]
    public void A_stated_value_that_would_go_into_a_command_or_be_read_as_a_variable_is_refused(string json, string expected)
    {
        FlowFormatException ex = Assert.Throws<FlowFormatException>(() => DesiredFlow.Parse(json));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    // ── hjelpere ─────────────────────────────────────────────────────────

    private FlowAdvice Advise(string intent, string? profile = null)
        => FlowAdvisor.Advise(DesiredFlow.Parse(intent), FlowScan.Scan(_root), _root, profile: profile);

    private void Fixture(string name)
    {
        string from = Path.Combine(RepoRoot(), "tests", "Queuey.Client.Cli.Tests", "Fixtures", "flows", name);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(_root, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }

    private void File_(string relativePath, string content)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>The line in the copied fixture that holds <paramref name="text"/>, so a test does not pin a line number.</summary>
    private int LineOf(string relativePath, string text)
    {
        string[] lines = File.ReadAllLines(Path.Combine(_root, relativePath));
        int index = Array.FindIndex(lines, l => l.Contains(text, StringComparison.Ordinal));
        Assert.True(index >= 0, $"{relativePath} has no line with {text}");
        return index + 1;
    }

    private static void AssertField(DesiredFlow flow, string path, object value, Provenance provenance, string file, int line)
    {
        FlowValue? field = flow[path];
        Assert.True(field is not null, $"{path} is missing");
        Assert.Equal(JsonSerializer.Serialize(value), field!.Value.ToJsonString());
        Assert.Equal(provenance, field.Provenance);
        Assert.True(field.Evidence.Any(e => e.File == file && e.Line == line),
            $"{path}: expected evidence at {file}:{line}, got {string.Join(", ", field.Evidence)}");
    }

    private static void Leaves(JsonNode node, string path, List<string> into)
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach ((string key, JsonNode? child) in obj)
                Leaves(child!, path.Length == 0 ? key : path + "." + key, into);
            return;
        }
        into.Add(path);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Queuey.Client.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find the repository root (Queuey.Client.sln).");
    }
}
