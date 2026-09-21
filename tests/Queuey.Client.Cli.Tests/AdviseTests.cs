using System;
using System.Collections.Generic;
using System.IO;
using Queuey.Client.Cli.Advise;
using Xunit;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// The recommendation, against repositories shaped like the real ones. Each
/// fixture is a shape we have actually met: a Pi on LTE, a service that scales
/// to zero, a billing system that already has a bus, a receiver.
///
/// The cases worth reading are the two where a simpler rule gets it wrong.
/// A repository with no retry logic but no durable disk must NOT be told to
/// use Edge — there is nowhere to put the spool. And a repository that already
/// has an outbox must not be told to build anything: it gets a publish call in
/// the consumer it already runs.
/// </summary>
public sealed class AdviseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queuey-advise-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void File_(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
    }

    private Advice Advise() => Recommendation.For(RepoScan.Scan(_root));

    // ── the fixtures ──────────────────────────────────────────────────

    [Fact]
    public void A_gateway_on_a_pi_with_a_systemd_unit_gets_Edge()
    {
        File_("SensorGateway.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Worker\"></Project>");
        File_("Worker.cs", "public class Worker : BackgroundService { }");
        File_("deploy/sensor-gateway.service", "[Unit]\nDescription=Sensor gateway\n[Service]\nExecStart=/opt/gateway/app");

        var advice = Advise();

        Assert.Equal(SendPath.Edge, advice.Send);
        Assert.Contains(advice.Reasons, r => r.Contains("systemd", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(advice.NextSteps, s => s.Contains("Queuey.Edge", StringComparison.Ordinal));
    }

    [Fact]
    public void A_node_box_with_a_durable_volume_gets_the_Edge_daemon_because_the_SDK_is_dotnet_only()
    {
        File_("package.json", "{ \"name\": \"collector\", \"dependencies\": { \"axios\": \"^1\" } }");
        File_("src/collector.js", "const axios = require('axios');");
        File_("Dockerfile", "FROM node:20\nVOLUME /data\nCMD [\"node\", \"src/collector.js\"]");

        var advice = Advise();

        Assert.Equal(SendPath.EdgeDaemon, advice.Send);
        Assert.Contains(advice.NextSteps, s => s.Contains("queuey edge run", StringComparison.Ordinal));
        Assert.Contains(advice.NextSteps, s => s.Contains("localhost:7300", StringComparison.Ordinal));
    }

    [Fact]
    public void A_service_that_scales_to_zero_is_NOT_told_to_use_Edge_however_bad_its_network()
    {
        // The case a "no retries, so use Edge" rule gets wrong: there is nowhere
        // to put a spool, so the network never enters into it.
        File_("OrdersApi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File_("Program.cs", "var app = WebApplication.Create();");
        File_("infra/orders.bicep", "resource app 'Microsoft.App/containerApps@2023-05-01' = {\n  properties: { template: { scale: { minReplicas: 0 } } }\n}");

        var advice = Advise();

        Assert.Equal(SendPath.Client, advice.Send);
        Assert.Contains(advice.Reasons, r => r.Contains("does not survive", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(advice.NextSteps, s => s.Contains("Queuey.Edge", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pod_using_emptyDir_is_ephemeral_even_though_it_looks_like_a_volume()
    {
        File_("app/main.py", "import requests");
        File_("pyproject.toml", "[project]\nname = \"ingest\"");
        File_("k8s/deployment.yaml", "spec:\n  volumes:\n    - name: scratch\n      emptyDir: {}");

        var advice = Advise();

        Assert.Equal(SendPath.PlainHttp, advice.Send);
        Assert.Contains(advice.Reasons, r => r.Contains("emptyDir", StringComparison.Ordinal));
    }

    [Fact]
    public void A_system_that_already_has_a_bus_is_told_to_publish_from_its_consumer_not_to_rebuild()
    {
        File_("Billing.csproj", "<Project><ItemGroup><PackageReference Include=\"MassTransit\" Version=\"8.0.0\" /></ItemGroup></Project>");
        File_("Consumers/InvoiceIssuedConsumer.cs", "public class InvoiceIssuedConsumer : IConsumer<InvoiceIssued> { }");

        var advice = Advise();

        Assert.Equal(SendPath.ClientFromExistingDurability, advice.Send);
        Assert.Contains(advice.Reasons, r => r.Contains("MassTransit", StringComparison.Ordinal));
        Assert.Contains(advice.NextSteps, s => s.Contains("Keep your bus", StringComparison.Ordinal));
        Assert.DoesNotContain(advice.NextSteps, s => s.Contains("Queuey.Edge", StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_outbox_counts_as_durability_whatever_library_surrounds_it()
    {
        File_("Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File_("Data/OutboxMessage.cs", "public class OutboxMessage { public Guid Id { get; set; } }");

        var advice = Advise();

        Assert.Equal(SendPath.ClientFromExistingDurability, advice.Send);
        Assert.Contains(advice.Reasons, r => r.Contains("outbox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_receiver_gets_the_recipe_rather_than_a_choice()
    {
        File_("Receiver.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File_("Program.cs", "app.MapPost(\"/webhooks/queuey\", (HttpRequest r) => Results.Ok());");

        var advice = Advise();

        Assert.True(advice.Receives);
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("RAW body", StringComparison.Ordinal));
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("QueueyDeliveryVerifier", StringComparison.Ordinal));
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("X-Queuey-Event-Id", StringComparison.Ordinal));
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("queuey listen", StringComparison.Ordinal));
    }

    [Fact]
    public void A_receiver_outside_dotnet_is_given_a_check_it_can_run_not_a_class_it_cannot_install()
    {
        // Naming QueueyDeliveryVerifier to a Node project sends the agent off to
        // write the HMAC by hand — the very thing the recipe is there to stop.
        File_("package.json", "{ \"name\": \"receiver\" }");
        File_("src/server.js", "app.post('/webhooks/queuey', (req, res) => res.sendStatus(200));");

        var advice = Advise();

        Assert.True(advice.Receives);
        Assert.DoesNotContain(advice.ReceivingSteps, s => s.Contains("QueueyDeliveryVerifier", StringComparison.Ordinal));
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("constant time", StringComparison.Ordinal));
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("RAW body", StringComparison.Ordinal));
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("https://queuey.ai/docs/how-to/verify-deliveries", StringComparison.Ordinal));
        Assert.Contains(advice.ReceivingSteps, s => s.Contains("X-Queuey-Event-Id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_named_for_webhooks_is_not_an_endpoint_that_takes_them()
    {
        // The shape of queuey.ai itself: an article whose image is called
        // "...-before-webhook.webp". A quoted path is not always a route.
        File_("package.json", "{ \"name\": \"site\", \"devDependencies\": { \"vite\": \"^5\" } }");
        File_("src/data/articles.ts", "export const a = { thumbnail: \"/article-iot-reliability-gap-before-webhook.webp\" };");

        Assert.False(Advise().Receives);
    }

    [Theory]
    [InlineData("app.post('/webhooks/queuey', handler)")]
    [InlineData("router.post(\"/api/stripe-webhook\", handler)")]
    [InlineData("app.post(`/webhooks/${provider}`, handler)")]
    [InlineData("app.post('/webhooks/:provider', handler)")]
    [InlineData("app.use(\"/webhook\", router)")]
    public void A_route_that_takes_webhooks_is_still_found(string line)
    {
        File_("package.json", "{ \"name\": \"receiver\" }");
        File_("src/server.ts", line);

        Assert.True(Advise().Receives);
    }

    // ── publishing without .NET ───────────────────────────────────────

    [Fact]
    public void A_repository_without_dotnet_gets_the_ingress_call_itself_rather_than_a_hint_to_find_one()
    {
        File_("package.json", "{ \"name\": \"shop\", \"dependencies\": { \"express\": \"^4\" } }");
        File_("src/server.ts", "app.get('/orders', list);");

        var advice = Advise();

        Assert.Equal(SendPath.PlainHttp, advice.Send);
        Assert.Contains(advice.NextSteps, s => s.Contains("https://ingress.queuey.ai/events/{tenantPublicId}/{queueName}", StringComparison.Ordinal));
        Assert.Contains(advice.NextSteps, s => s.Contains("X-Api-Key", StringComparison.Ordinal));
        Assert.Contains(advice.NextSteps, s => s.Contains("fetch(url", StringComparison.Ordinal));
        Assert.Contains(advice.NextSteps, s => s.Contains("Express (package.json)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_vite_app_with_supabase_functions_is_told_to_publish_from_the_functions_and_not_the_browser()
    {
        // The repository the agent-setup prompt dead-ended on (2026-09-20):
        // React and Vite, Supabase Deno edge functions, no .NET anywhere.
        File_("package.json", "{ \"name\": \"app\", \"devDependencies\": { \"vite\": \"^5\", \"typescript\": \"^5\" } }");
        File_("src/main.ts", "import { createRoot } from 'react-dom/client';");
        File_("supabase/functions/create-order/index.ts", "Deno.serve(async (req) => new Response('ok'));");

        var advice = Advise();

        Assert.Equal(SendPath.PlainHttp, advice.Send);
        Assert.Contains(advice.NextSteps, s => s.Contains("Supabase Edge Functions (supabase/functions/)", StringComparison.Ordinal));
        Assert.Contains(advice.NextSteps, s => s.Contains("VITE_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_browser_app_with_nowhere_server_side_is_told_not_to_publish_from_the_app()
    {
        File_("package.json", "{ \"name\": \"spa\", \"devDependencies\": { \"vite\": \"^5\" } }");
        File_("src/main.ts", "import { createRoot } from 'react-dom/client';");

        var advice = Advise();

        Assert.Contains(advice.NextSteps, s => s.Contains("found no server-side code", StringComparison.Ordinal)
                                            && s.Contains("bundle", StringComparison.Ordinal));
    }

    [Fact]
    public void A_nextjs_route_handler_is_where_the_call_goes()
    {
        File_("package.json", "{ \"name\": \"web\", \"dependencies\": { \"next\": \"15.0.0\" } }");
        File_("app/api/orders/route.ts", "export async function POST(req: Request) { return Response.json({}); }");

        var advice = Advise();

        Assert.Contains(advice.NextSteps, s => s.Contains("Next.js route handlers (app/api/orders/route.ts)", StringComparison.Ordinal));
        Assert.Contains(advice.NextSteps, s => s.Contains("NEXT_PUBLIC_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_python_service_is_given_the_call_in_python()
    {
        File_("pyproject.toml", "[project]\nname = \"ingest\"");
        File_("app/main.py", "import requests");

        var advice = Advise();

        Assert.Contains(advice.NextSteps, s => s.Contains("requests.post(url", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("package.json", "{ \"name\": \"web\" }")]
    [InlineData("pyproject.toml", "[project]\nname = \"ingest\"")]
    [InlineData("go.mod", "module example.com/ingest")]
    public void No_repository_without_dotnet_is_told_to_add_a_dotnet_package(string manifest, string content)
    {
        File_(manifest, content);
        File_("src/server.ts", "app.post('/webhooks/queuey', handler);");

        var advice = Advise();

        var everything = advice.NextSteps.Concat(advice.ReceivingSteps).ToArray();
        Assert.DoesNotContain(everything, s => s.Contains("dotnet add package", StringComparison.Ordinal));
        Assert.DoesNotContain(everything, s => s.StartsWith("Add Queuey.", StringComparison.Ordinal));
        Assert.DoesNotContain(everything, s => s.Contains("QueueyDeliveryVerifier", StringComparison.Ordinal));
    }

    [Fact]
    public void The_edge_daemon_step_says_where_the_binary_for_a_machine_without_dotnet_is()
    {
        File_("package.json", "{ \"name\": \"collector\" }");
        File_("Dockerfile", "FROM node:20\nVOLUME /data");

        var advice = Advise();

        Assert.Contains(advice.NextSteps, s => s.Contains("https://github.com/Queuey-AI/queuey-client/releases/latest", StringComparison.Ordinal));
    }

    // ── the rules that hold everywhere ────────────────────────────────

    [Fact]
    public void The_network_is_asked_about_wherever_it_would_change_the_answer()
    {
        File_("Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File_("Program.cs", "var app = WebApplication.Create();");

        var advice = Advise();

        Assert.Contains(advice.Questions, q => q.Contains("network", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_network_is_not_asked_about_when_it_cannot_change_the_answer()
    {
        // Ephemeral hosting rules Edge out already; asking would be noise.
        File_("Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File_("host.json", "{ \"version\": \"2.0\" }");

        var advice = Advise();

        Assert.DoesNotContain(advice.Questions, q => q.Contains("network", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_conclusion_names_the_file_it_came_from()
    {
        File_("Billing.csproj", "<Project><ItemGroup><PackageReference Include=\"Hangfire\" Version=\"1\" /></ItemGroup></Project>");

        var advice = Advise();

        Assert.Contains(advice.Reasons, r => r.Contains("Billing.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rule_against_rebuilding_Edge_is_stated_outright()
    {
        Assert.Contains("Queuey.Edge", Recommendation.DoNotRebuild, StringComparison.Ordinal);
        Assert.Contains("Do not hand-write", Recommendation.DoNotRebuild, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_Queuey_setup_is_noticed_and_reported()
    {
        File_("App.csproj", "<Project><ItemGroup><PackageReference Include=\"Queuey.Edge\" Version=\"0.1.0\" /></ItemGroup></Project>");
        File_("queuey.deploy.json", "{ \"queues\": [] }");

        var advice = Advise();

        Assert.Contains(advice.Reasons, r => r.Contains("already here", StringComparison.OrdinalIgnoreCase));
    }

    // ── the scanner's own boundaries ──────────────────────────────────

    [Fact]
    public void Dependencies_and_build_output_are_not_statements_about_this_repository()
    {
        File_("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File_("node_modules/bullmq/package.json", "{ \"name\": \"bullmq\" }");
        File_("bin/Debug/MassTransit.dll.txt", "MassTransit");

        var facts = RepoScan.Scan(_root);

        Assert.Empty(facts.Durability);
    }

    [Fact]
    public void An_unrecognised_repository_falls_back_to_plain_HTTP_rather_than_assuming_dotnet()
    {
        Directory.CreateDirectory(_root);

        var advice = Advise();

        // Nothing identifies the ecosystem, so the answer is the one that works
        // anywhere: POST to the ingress. Assuming .NET would put an install
        // command in front of someone who cannot run it.
        Assert.Equal(SendPath.PlainHttp, advice.Send);
        Assert.NotEmpty(advice.Headline);
    }

    [Fact]
    public void A_missing_directory_is_an_error_the_caller_can_act_on()
    {
        Assert.Throws<DirectoryNotFoundException>(() => RepoScan.Scan(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void Evidence_is_deduplicated_so_one_finding_is_not_repeated_per_file()
    {
        File_("A.csproj", "<PackageReference Include=\"MassTransit\" />");
        File_("B.csproj", "<PackageReference Include=\"MassTransit\" />");
        File_("C.csproj", "<PackageReference Include=\"MassTransit\" />");

        var facts = RepoScan.Scan(_root);

        Assert.Single(facts.Durability, e => e.What == "MassTransit");
    }
}

/// <summary>
/// What --write-files would put in the repository. The plan is computed apart
/// from the writing so the safe command can show exactly what the writing one
/// would do; these pin that it stays a small, honest scaffold.
/// </summary>
public sealed class ScaffoldPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queuey-scaffold-" + Guid.NewGuid().ToString("N")[..8]);

    public ScaffoldPlanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void File_(string name, string content) => System.IO.File.WriteAllText(Path.Combine(_root, name), content);

    [Fact]
    public void It_plans_a_deployment_file_and_a_gitignore_line_and_nothing_else()
    {
        var plan = ScaffoldPlan.For(_root, "orders");

        Assert.Equal(new[] { "queuey.deploy.json", ".gitignore" }, plan.Select(p => p.Path).ToArray());
    }

    [Fact]
    public void The_deployment_file_names_the_queue_and_carries_no_secrets()
    {
        var deploy = ScaffoldPlan.For(_root, "orders").Single(p => p.Path == "queuey.deploy.json");

        Assert.Contains("\"orders\"", deploy.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", deploy.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("qak_", deploy.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", deploy.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_file_we_generate_is_one_apply_can_actually_read()
    {
        // The test that matters most here. A scaffold apply cannot parse is
        // worse than none: it looks like progress and fails at the deploy.
        // An earlier draft carried a "$schema" line and did exactly that.
        var deploy = ScaffoldPlan.For(_root, "orders").Single(p => p.Path == "queuey.deploy.json");

        var parsed = Queuey.Client.Waas.DeploymentFile.Parse(deploy.Content);

        Assert.True(parsed.Queues.ContainsKey("orders"));
        parsed.Expand().Resolve();   // throws if the policy does not hold together
    }

    [Fact]
    public void An_existing_deployment_file_is_reported_as_existing_so_it_is_not_clobbered()
    {
        File_("queuey.deploy.json", "{ \"queues\": { \"mine\": {} } }");

        var deploy = ScaffoldPlan.For(_root, "orders").Single(p => p.Path == "queuey.deploy.json");

        Assert.True(deploy.Exists);
        Assert.Contains("left alone", deploy.Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_gitignore_keeps_what_is_there_and_adds_the_config_file()
    {
        File_(".gitignore", "bin/\nobj/\n");

        var ignore = ScaffoldPlan.For(_root, "orders").Single(p => p.Path == ".gitignore");

        Assert.Contains("bin/", ignore.Content, StringComparison.Ordinal);
        Assert.Contains("obj/", ignore.Content, StringComparison.Ordinal);
        Assert.Contains("queuey.json", ignore.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_file_that_is_already_ignored_is_left_alone_entirely()
    {
        File_(".gitignore", "bin/\nqueuey.json\n");

        var plan = ScaffoldPlan.For(_root, "orders");

        Assert.DoesNotContain(plan, p => p.Path == ".gitignore");
    }

    [Fact]
    public void The_queue_name_defaults_to_the_repository_rather_than_something_generic()
    {
        var root = Path.Combine(Path.GetTempPath(), "Order Service");
        Directory.CreateDirectory(root);
        try
        {
            // "events" in every workspace helps nobody find anything later.
            Assert.Equal("order-service", ScaffoldPlan.DefaultQueueName(root));
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void An_explicit_queue_name_is_slugged_into_something_Queuey_accepts()
    {
        Assert.Equal("customer-events", ScaffoldPlan.DefaultQueueName(_root, "Customer Events"));
        Assert.Equal("orders.v2", ScaffoldPlan.DefaultQueueName(_root, "orders.v2"));
    }
}
