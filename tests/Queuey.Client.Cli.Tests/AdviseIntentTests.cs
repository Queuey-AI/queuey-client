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
        Assert.Contains("declares queues.payments already", design.Merge, StringComparison.Ordinal);
        Assert.Equal("/api/stripe", design.Content["queues"]!["payments"]!["delivery"]!["url"]!.GetValue<string>());
        Assert.Empty(design.Variables);

        // En fil uten profiler får faste verdier, som resten av den, og kommandoene tar ingen --profile.
        Assert.Null(design.Content["profiles"]);
        Assert.Equal("dev", design.Content["workspace"]!["environment"]!.GetValue<string>());
        Assert.Equal("localForward", design.Content["queues"]!["payments"]!["delivery"]!["kind"]!.GetValue<string>());
        Assert.DoesNotContain(design.NextSteps, s => s.Contains("--profile", StringComparison.Ordinal));
    }

    [Fact]
    public void A_deployment_file_with_profiles_gets_the_flows_values_in_the_profile_for_its_environment()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "workspace": { "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}" },
              "queues": { "orders": { "delivery": { "url": "https://orders.example.com/hook" } } },
              "profiles": { "prod": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "prod" } } }
            }
            """);

        FlowDesign design = Advise(StripeIntent).Design!;

        Assert.Equal("${QUEUEY_WORKSPACE_ENVIRONMENT}", design.Content["workspace"]!["environment"]!.GetValue<string>());
        Assert.Equal("dev", design.Content["profiles"]!["dev"]!["variables"]!["QUEUEY_WORKSPACE_ENVIRONMENT"]!.GetValue<string>());
        Assert.Contains("Add profiles.dev from the proposal", design.Merge, StringComparison.Ordinal);
        Assert.Contains(design.NextSteps, s => s.Contains("queuey apply --profile dev", StringComparison.Ordinal));
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
        Assert.Equal(new[] { "schemaVersion", "path", "intent", "outcome", "flow", "existing", "infrastructure", "code", "nextSteps" },
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

    // ── hjelpere ─────────────────────────────────────────────────────────

    private FlowAdvice Advise(string intent) => FlowAdvisor.Advise(DesiredFlow.Parse(intent), FlowScan.Scan(_root), _root);

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
