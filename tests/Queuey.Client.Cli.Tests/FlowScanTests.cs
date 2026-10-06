using Queuey.Client.Cli.Advise;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Det skanningen kjenner igjen, og grensene den holder seg innenfor (review av #56, 2026-10-06).
///
/// Hver gjenkjenner har en test med en liten kodesnutt: en gjenkjenner uten test er en påstand ingen holder. Og et klonet
/// repo kan ha lenker til /dev/zero, til seg selv eller ut av repoet: skanningen følger ingen av dem, leser hver fil med en
/// grense, og har et budsjett for filer, mapper, bytes og tid. Testene som ville hengt uten vernet, har en egen frist.
/// </summary>
public sealed class FlowScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queuey-scan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _outside = new();

    public FlowScanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (string dir in _outside.Append(_root))
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ── rutene, per rammeverk ────────────────────────────────────────────

    [Fact]
    public void An_aspnet_minimal_api_route_under_a_group_with_its_port_and_secret()
    {
        File_("Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File_("Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://localhost:5005");
            var app = builder.Build();
            var hooks = app.MapGroup("/hooks");
            hooks.MapPost("/stripe", async (HttpRequest request) =>
            {
                var json = await new StreamReader(request.Body).ReadToEndAsync();
                var secret = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET");
                var stripeEvent = EventUtility.ConstructEvent(json, request.Headers["Stripe-Signature"], secret);
                return Results.Ok();
            });
            """);

        FlowFacts facts = Scan();

        Assert.Equal(("/hooks/stripe", "aspnet", 5), Route(facts));
        StripeVerificationFinding stripe = Assert.Single(facts.StripeVerifications);
        Assert.Equal(("Stripe.net's EventUtility.ConstructEvent", 9, "STRIPE_WEBHOOK_SECRET"), (stripe.Api, stripe.Line, stripe.SecretName));
        Assert.Contains(facts.RawBodyReads, r => r.Line == 7);
        Assert.Contains(facts.Ports, p => p is { Port: 5005, HardCoded: true, Line: 2 });
    }

    [Fact]
    public void An_aspnet_controller_route_takes_the_controllers_name_and_the_actions_template()
    {
        File_("Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File_("WebhooksController.cs", """
            [ApiController]
            [Route("api/[controller]")]
            public class WebhooksController : ControllerBase
            {
                [HttpPost("stripe")]
                public async Task<IActionResult> Stripe()
                {
                    var json = await new StreamReader(Request.Body).ReadToEndAsync();
                    var secret = _configuration.GetValue<string>("Stripe:WebhookSecret");
                    var stripeEvent = EventUtility.ConstructEvent(json, Request.Headers["Stripe-Signature"], secret);
                    return Ok();
                }
            }
            """);

        FlowFacts facts = Scan();

        Assert.Equal(("/api/Webhooks/stripe", "aspnet", 5), Route(facts));
        Assert.Equal("Stripe:WebhookSecret", Assert.Single(facts.StripeVerifications).SecretName);
    }

    [Theory]
    [InlineData("fastify", "fastify.post('/webhooks/stripe', async (request, reply) => {})")]
    [InlineData("hono", "app.post('/webhooks/stripe', async (c) => c.json({}))")]
    [InlineData("koa", "router.post('/webhooks/stripe', async (ctx) => {})")]
    [InlineData("express", "app.route('/webhooks/stripe').post((req, res) => res.sendStatus(200))")]
    public void A_node_route_is_found_and_its_framework_read_from_the_package(string framework, string line)
    {
        File_("package.json", $$"""{ "name": "api", "dependencies": { "{{framework}}": "^4" } }""");
        File_("server.js", line);

        Assert.Equal(("/webhooks/stripe", framework, 1), Route(Scan()));
    }

    [Fact]
    public void A_fastify_port_given_as_an_object_is_hard_coded()
    {
        File_("package.json", """{ "name": "api", "dependencies": { "fastify": "^4" } }""");
        File_("server.js", "fastify.listen({ port: 3005 });");

        Assert.Contains(Scan().Ports, p => p is { Port: 3005, HardCoded: true });
    }

    [Fact]
    public void A_hono_router_mounted_with_route_is_a_candidate_at_the_whole_path()
    {
        File_("package.json", """{ "name": "api", "dependencies": { "hono": "^4", "stripe": "^16" } }""");
        File_("src/index.ts", "const app = new Hono();\napp.route('/api', webhooks);\n");
        File_("src/webhooks.ts", """
            const webhooks = new Hono();
            webhooks.post('/stripe', async (c) => {
              const body = await c.req.text();
              const event = await stripe.webhooks.constructEventAsync(body, c.req.header('stripe-signature'), process.env.STRIPE_WEBHOOK_SECRET);
            });
            """);

        FlowCandidate candidate = Assert.Single(FlowAdvisor.Candidates(Scan(), _root));

        Assert.StartsWith("Stripe → Queuey → POST /api/stripe (hono, src/webhooks.ts:2)", candidate.Summary, StringComparison.Ordinal);
        Assert.Contains(candidate.Flow["destination.expectsRawBody"]!.Evidence, e => e.Line == 3);
    }

    [Fact]
    public void A_nest_controller_under_a_global_prefix_is_a_candidate_at_the_whole_path()
    {
        File_("package.json", """{ "name": "api", "dependencies": { "@nestjs/core": "^10", "stripe": "^16" } }""");
        File_("src/main.ts", "const app = await NestFactory.create(AppModule, { rawBody: true });\napp.setGlobalPrefix('api');\nawait app.listen(3007);\n");
        File_("src/webhooks.controller.ts", """
            @Controller('webhooks')
            export class WebhooksController {
              @Post('stripe')
              handle(@Req() req: RawBodyRequest<Request>, @Headers('stripe-signature') signature: string) {
                const event = this.stripe.webhooks.constructEvent(req.rawBody, signature, process.env.STRIPE_WEBHOOK_SECRET);
              }
            }
            """);

        FlowCandidate candidate = Assert.Single(FlowAdvisor.Candidates(Scan(), _root));

        Assert.StartsWith("Stripe → Queuey → POST /api/webhooks/stripe (nestjs, src/webhooks.controller.ts:3)", candidate.Summary, StringComparison.Ordinal);
        Assert.Equal(3007, candidate.Flow.Int("destination.port"));
    }

    [Fact]
    public void A_nextjs_pages_router_api_route_is_found_where_the_file_is_with_its_raw_body()
    {
        File_("package.json", """{ "name": "web", "dependencies": { "next": "15", "micro": "^10" } }""");
        File_("pages/api/webhooks/stripe.ts", """
            import { buffer } from 'micro';
            export const config = { api: { bodyParser: false } };

            export default async function handler(req, res) {
              const buf = await buffer(req);
              const event = stripe.webhooks.constructEvent(buf, req.headers['stripe-signature'], process.env.STRIPE_WEBHOOK_SECRET);
            }
            """);
        File_("pages/api/index.ts", "export default function handler(req, res) { res.json({}); }");

        FlowFacts facts = Scan();

        Assert.Contains(facts.Routes, r => r is { Route: "/api/webhooks/stripe", Framework: "nextjs", Line: 4, FromPath: true });
        Assert.Contains(facts.Routes, r => r is { Route: "/api", FromPath: true });
        Assert.Contains(facts.RawBodyReads, r => r.Line == 2);
        Assert.Contains(facts.RawBodyReads, r => r.Line == 5);
    }

    [Fact]
    public void A_fastapi_route_under_a_router_prefix_with_construct_event_its_port_and_types()
    {
        File_("requirements.txt", "fastapi\nuvicorn\nstripe\n");
        File_("app/main.py", """
            router = APIRouter(prefix="/api")

            # The webhook Stripe calls: @router.post("/not-a-route") in a comment is not one.
            @router.post("/stripe")
            async def stripe_webhook(request: Request):
                payload = await request.body()
                event = stripe.Webhook.construct_event(payload, request.headers.get("stripe-signature"), os.environ["STRIPE_WEBHOOK_SECRET"])
                if event["type"] == "checkout.session.completed":
                    pass

            if __name__ == "__main__":
                uvicorn.run(app, host="0.0.0.0", port=8001)
            """);

        FlowFacts facts = Scan();
        FlowCandidate candidate = Assert.Single(FlowAdvisor.Candidates(facts, _root));

        Assert.StartsWith("Stripe → Queuey → POST /api/stripe (fastapi, app/main.py:4)", candidate.Summary, StringComparison.Ordinal);
        Assert.Equal(("stripe.Webhook.construct_event", "STRIPE_WEBHOOK_SECRET"), (facts.StripeVerifications[0].Api, facts.StripeVerifications[0].SecretName));
        Assert.Contains(facts.RawBodyReads, r => r.Line == 6);
        Assert.Contains(facts.Ports, p => p is { Port: 8001, HardCoded: true });
        Assert.Equal(new[] { "checkout.session.completed" }, candidate.Flow.Strings("source.eventTypes"));
        Assert.DoesNotContain(facts.Routes, r => r.Route == "/not-a-route");
    }

    [Fact]
    public void A_flask_route_under_a_blueprint_with_construct_event_and_its_port()
    {
        File_("requirements.txt", "flask\nstripe\n");
        File_("app.py", """
            bp = Blueprint("hooks", __name__, url_prefix="/hooks")

            @bp.route("/stripe", methods=["POST"])
            def stripe_webhook():
                payload = request.get_data()
                event = stripe.Webhook.construct_event(payload, request.headers.get("Stripe-Signature"), os.getenv("STRIPE_WEBHOOK_SECRET"))
                return "", 200

            @app.route("/health")
            def health():
                return "ok"

            if __name__ == "__main__":
                app.run(port=4243)
            """);

        FlowFacts facts = Scan();

        Assert.StartsWith("Stripe → Queuey → POST /hooks/stripe (flask, app.py:3)",
            Assert.Single(FlowAdvisor.Candidates(facts, _root)).Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(facts.Routes, r => r.Route == "/health");   // GET, uten methods=["POST"]
        Assert.Equal("STRIPE_WEBHOOK_SECRET", facts.StripeVerifications[0].SecretName);
        Assert.Contains(facts.RawBodyReads, r => r.Line == 5);
        Assert.Contains(facts.Ports, p => p is { Port: 4243, HardCoded: true });
    }

    [Fact]
    public void A_go_net_http_handler_with_webhook_ConstructEvent_and_its_port()
    {
        File_("go.mod", "module example.com/hooks\n\ngo 1.22\n");
        File_("main.go", """
            func main() {
            	http.HandleFunc("/webhook", handleWebhook)
            	log.Fatal(http.ListenAndServe(":4242", nil))
            }

            func handleWebhook(w http.ResponseWriter, r *http.Request) {
            	payload, err := io.ReadAll(r.Body)
            	event, err := webhook.ConstructEvent(payload, r.Header.Get("Stripe-Signature"), os.Getenv("STRIPE_WEBHOOK_SECRET"))
            	switch event.Type {
            	case "checkout.session.completed":
            	}
            }
            """);

        FlowFacts facts = Scan();

        Assert.Equal(("/webhook", "go", 2), Route(facts));
        Assert.Equal(("webhook.ConstructEvent", 8, "STRIPE_WEBHOOK_SECRET"),
            (facts.StripeVerifications[0].Api, facts.StripeVerifications[0].Line, facts.StripeVerifications[0].SecretName));
        Assert.Contains(facts.RawBodyReads, r => r.Line == 7);
        Assert.Contains(facts.Ports, p => p is { Port: 4242, HardCoded: true });
        Assert.Contains(facts.EventTypes, t => t.Type == "checkout.session.completed");
    }

    [Fact]
    public void A_gin_route_under_a_group()
    {
        File_("go.mod", "module example.com/hooks\n");
        File_("main.go", """
            func main() {
            	r := gin.Default()
            	api := r.Group("/api")
            	api.POST("/stripe", handleStripe)
            	r.Run(":8088")
            }

            func handleStripe(c *gin.Context) {
            	payload, _ := io.ReadAll(c.Request.Body)
            }
            """);

        FlowFacts facts = Scan();

        Assert.Equal(("/api/stripe", "go", 4), Route(facts));
        Assert.Contains(facts.Ports, p => p is { Port: 8088, HardCoded: true });
        Assert.Contains(facts.RawBodyReads, r => r.Line == 9);
    }

    [Fact]
    public void A_fastapi_router_included_with_a_prefix_elsewhere_is_a_candidate_at_the_whole_path()
    {
        File_("requirements.txt", "fastapi\nstripe\n");
        File_("main.py", "app.include_router(webhooks.router, prefix=\"/api\")\n");
        File_("webhooks.py", """
            @router.post("/stripe")
            async def stripe_webhook(request: Request):
                event = stripe.Webhook.construct_event(await request.body(), request.headers.get("stripe-signature"), os.environ["STRIPE_WEBHOOK_SECRET"])
            """);

        Assert.StartsWith("Stripe → Queuey → POST /api/stripe (fastapi, webhooks.py:1)",
            Assert.Single(FlowAdvisor.Candidates(Scan(), _root)).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flask_blueprint_registered_with_a_prefix_elsewhere_is_a_candidate_at_the_whole_path()
    {
        File_("requirements.txt", "flask\nstripe\n");
        File_("app.py", "app.register_blueprint(hooks.bp, url_prefix=\"/hooks\")\n");
        File_("hooks.py", """
            @bp.route("/stripe", methods=["POST"])
            def stripe_webhook():
                event = stripe.Webhook.construct_event(request.data, request.headers.get("Stripe-Signature"), os.environ.get("STRIPE_WEBHOOK_SECRET"))
            """);

        FlowFacts facts = Scan();

        Assert.StartsWith("Stripe → Queuey → POST /hooks/stripe (flask, hooks.py:1)",
            Assert.Single(FlowAdvisor.Candidates(facts, _root)).Summary, StringComparison.Ordinal);
        Assert.Equal("STRIPE_WEBHOOK_SECRET", facts.StripeVerifications[0].SecretName);   // os.environ.get(…)
        Assert.Contains(facts.RawBodyReads, r => r is { File: "hooks.py", Line: 3 });     // request.data
    }

    [Theory]
    [InlineData("server.js", "app.use('/webhooks', bodyParser.raw({ type: 'application/json' }));")]
    [InlineData("raw.js", "const raw = await getRawBody(req);")]
    [InlineData("route.ts", "const body = await request.arrayBuffer();")]
    [InlineData("hook.py", "payload = request.data")]
    public void Each_way_of_reading_the_raw_body_is_found(string file, string line)
    {
        File_(file, line);

        Assert.Single(Scan().RawBodyReads);
    }

    [Fact]
    public void An_event_type_compared_with_Equals_is_found()
    {
        File_("Handler.cs", "if (stripeEvent.Type.Equals(\"invoice.paid\")) { }");

        Assert.Equal("invoice.paid", Assert.Single(Scan().EventTypes).Type);
    }

    // ── sjekker og navn ──────────────────────────────────────────────────

    [Theory]
    [InlineData("handler.cs", "if (Request.Headers[\"X-Webhook-Secret\"] != secret) return Unauthorized();")]
    [InlineData("handler.py", "if request.headers.get(\"x-webhook-secret\") != secret:")]
    [InlineData("handler.go", "if r.Header.Get(\"X-Webhook-Secret\") != secret {")]
    [InlineData("handler.js", "if (req.headers['x-webhook-secret'] !== secret) return res.sendStatus(401);")]
    [InlineData("handler.ts", "if (req.get('x-webhook-secret') !== secret) return res.sendStatus(401);")]
    [InlineData("handler.mjs", "if (req.header('x-webhook-secret') !== secret) return res.sendStatus(401);")]
    [InlineData("check.cs", "if (!Request.Headers.TryGetValue(\"X-Webhook-Secret\", out var given)) return Unauthorized();")]
    [InlineData("check.py", "if request.headers[\"x-webhook-secret\"] != secret:")]
    public void A_secret_compared_in_a_header_is_found_in_each_language(string file, string line)
    {
        File_(file, line);

        Assert.Equal("x-webhook-secret", Assert.Single(Scan().SecretChecks).Header);
    }

    [Fact]
    public void Stripes_own_signature_header_is_not_a_shared_secret()
    {
        File_("handler.js", "const sig = req.headers['stripe-signature'];");

        Assert.Empty(Scan().SecretChecks);
    }

    [Fact]
    public void A_receiver_that_verifies_queueys_signature_is_found()
    {
        File_("Receiver.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        File_("Program.cs", "var verifier = new QueueyDeliveryVerifier(signingSecret);\napp.MapPost(\"/hooks/orders\", (HttpRequest r) => Results.Ok());\n");

        FlowAdvice advice = FlowAdvisor.Advise(DesiredFlow.Parse("""
            {
              "source": { "kind": { "value": "app", "provenance": "stated" } },
              "destination": { "route": { "value": "/hooks/orders", "provenance": "stated" } }
            }
            """), Scan(), _root);

        Assert.Equal("queuey-signature", advice.Flow.String("requirements.verification"));
        Assert.Contains(advice.Flow["requirements.verification"]!.Evidence, e => e is { File: "Program.cs", Line: 1 });
    }

    // ── monorepo ─────────────────────────────────────────────────────────

    [Fact]
    public void In_a_monorepo_a_router_is_mounted_and_its_port_read_within_its_own_app()
    {
        // admin sorterer før api og monterer en fil med samme navn: uten prosjektgrensen ble ruten /hooks/stripe og porten 5000.
        File_("apps/admin/package.json", """{ "name": "admin", "dependencies": { "express": "^4" } }""");
        File_("apps/admin/src/server.js", "app.use('/hooks', require('./routes/stripe'));\napp.listen(5000);\n");
        File_("apps/admin/src/routes/stripe.js", "router.post('/stripe', (req, res) => res.sendStatus(200));\n");
        File_("apps/api/package.json", """{ "name": "api", "dependencies": { "express": "^4", "stripe": "^16" } }""");
        File_("apps/api/src/server.js", "app.use('/webhooks', require('./routes/stripe'));\napp.listen(4000);\n");
        File_("apps/api/src/routes/stripe.js", """
            router.post('/stripe', express.raw({ type: 'application/json' }), (req, res) => {
              const event = stripe.webhooks.constructEvent(req.body, req.headers['stripe-signature'], process.env.STRIPE_WEBHOOK_SECRET);
            });
            """);

        FlowFacts facts = Scan();
        FlowCandidate candidate = Assert.Single(FlowAdvisor.Candidates(facts, _root));

        Assert.StartsWith("Stripe → Queuey → POST /webhooks/stripe (express, apps/api/src/routes/stripe.js:1)", candidate.Summary, StringComparison.Ordinal);
        Assert.Equal(4000, candidate.Flow.Int("destination.port"));

        // Ruten admin serverer, verifiserer ikke Stripe: en intensjon som peker dit, er en konflikt.
        FlowAdvice advice = FlowAdvisor.Advise(DesiredFlow.Parse("""
            {
              "source": { "kind": { "value": "stripe", "provenance": "stated" } },
              "destination": { "route": { "value": "/hooks/stripe", "provenance": "stated" } }
            }
            """), facts, _root);
        Assert.Equal("requirements.verification", Assert.Single(advice.Flow.Conflicts).Field);
    }

    // ── lenker og grenser (B2) ───────────────────────────────────────────

    [Fact]
    public async Task A_link_to_dev_zero_is_never_read()
    {
        if (OperatingSystem.IsWindows())
            return;   // /dev/zero finnes ikke, og en lenke krever rettigheter der

        CopyFixture("stripe-express");
        Directory.CreateDirectory(Path.Combine(_root, "src", "app", "api"));
        File.CreateSymbolicLink(Path.Combine(_root, "src", "app", "api", "route.ts"), "/dev/zero");

        FlowFacts facts = await Bounded(() => FlowScan.Scan(_root));
        RepoFacts repo = await Bounded(() => RepoScan.Scan(_root));

        Assert.Single(FlowAdvisor.Candidates(facts, _root));
        Assert.Contains(facts.Limits, l => l.StartsWith("1 symbolic link not followed", StringComparison.Ordinal));
        Assert.Contains(repo.ScanLimits, l => l.StartsWith("1 symbolic link not followed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_links_back_to_the_repository_do_not_make_the_scan_branch()
    {
        CopyFixture("stripe-express");
        if (!TryLinkDirectory(Path.Combine(_root, "a"), ".") || !TryLinkDirectory(Path.Combine(_root, "b"), "."))
            return;

        FlowFacts facts = await Bounded(() => FlowScan.Scan(_root));
        RepoFacts repo = await Bounded(() => RepoScan.Scan(_root));

        Assert.Single(FlowAdvisor.Candidates(facts, _root));
        Assert.Contains(facts.Limits, l => l.StartsWith("2 symbolic links not followed", StringComparison.Ordinal));
        Assert.Contains(repo.ScanLimits, l => l.StartsWith("2 symbolic links not followed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_link_out_of_the_repository_is_not_scanned_into_the_evidence()
    {
        CopyFixture("stripe-express");
        string outside = Path.Combine(Path.GetTempPath(), "queuey-scan-outside-" + Guid.NewGuid().ToString("N")[..8]);
        _outside.Add(outside);
        CopyFixture("stripe-nextjs", outside);
        if (!TryLinkDirectory(Path.Combine(_root, "vendor2"), outside))
            return;

        FlowFacts facts = await Bounded(() => FlowScan.Scan(_root));

        Assert.DoesNotContain(facts.Routes, r => r.File.StartsWith("vendor2", StringComparison.Ordinal));
        Assert.Equal("Stripe → Queuey → POST /webhooks/stripe (express, src/routes/webhooks.js:7)",
            Assert.Single(FlowAdvisor.Candidates(facts, _root)).Summary);
    }

    [Theory]
    [InlineData("files", "it opened 2 files, the most a scan opens")]
    [InlineData("folders", "it listed 1 folders, the most a scan lists")]
    [InlineData("bytes", "it read 100 bytes, the most a scan reads")]
    [InlineData("deadline", "it stopped after 0 ms, the longest a scan may take")]
    [InlineData("lines", "lines longer than 20 characters skipped")]
    [InlineData("large", "larger than 64 bytes skipped")]
    public async Task A_limit_the_scan_reaches_stops_it_and_says_so(string limit, string expected)
    {
        CopyFixture("stripe-express");
        ScanBudget budget = limit switch
        {
            "files" => ScanBudget.Default with { MaxFiles = 2 },
            "folders" => ScanBudget.Default with { MaxDirectories = 1 },
            "bytes" => ScanBudget.Default with { MaxTotalBytes = 100 },
            "deadline" => ScanBudget.Default with { Deadline = TimeSpan.Zero },
            "lines" => ScanBudget.Default with { MaxLineLength = 20 },
            _ => ScanBudget.Default with { MaxFileBytes = 64 },
        };

        FlowFacts facts = await Bounded(() => FlowScan.Scan(_root, budget));
        RepoFacts repo = await Bounded(() => RepoScan.Scan(_root, budget));

        Assert.Contains(facts.Limits, l => l.Contains(expected, StringComparison.Ordinal));
        if (limit != "lines")   // linjelengden gjelder bare FlowScan, som matcher linje for linje
            Assert.Contains(repo.ScanLimits, l => l.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_scan_that_reads_everything_says_nothing_about_limits()
    {
        CopyFixture("stripe-aspnet");

        Assert.Empty(Scan().Limits);
        Assert.Empty(RepoScan.Scan(_root).ScanLimits);
    }

    // ── navn med kontrolltegn (S3) ───────────────────────────────────────

    [Theory]
    [InlineData("routes/\u001b[31mevil\u0007.js")]
    [InlineData("routes/‮sj.evil")]
    public void A_path_with_control_or_direction_characters_is_skipped(string name)
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows tillater ikke kontrolltegn i filnavn

        File_("package.json", """{ "name": "api", "dependencies": { "express": "^4" } }""");
        File_(name, """
            router.post('/stripe', (req, res) => {
              stripe.webhooks.constructEvent(req.body, req.headers['stripe-signature'], process.env.STRIPE_WEBHOOK_SECRET);
            });
            """);

        FlowFacts facts = Scan();

        Assert.Empty(facts.StripeVerifications);
        Assert.Contains(facts.Limits, l => l.Contains("control or direction characters", StringComparison.Ordinal));
    }

    [Fact]
    public void A_route_literal_with_a_control_character_is_not_a_route()
    {
        File_("package.json", """{ "name": "api", "dependencies": { "express": "^4" } }""");
        File_("server.js", "app.post('/webhooks/\u001b[2Jstripe', handler);\napp.post('/webhooks/‮stripe', handler);\n");

        Assert.Empty(Scan().Routes);
    }

    // ── hjelpere ─────────────────────────────────────────────────────────

    private FlowFacts Scan() => FlowScan.Scan(_root);

    private static (string Route, string Framework, int Line) Route(FlowFacts facts)
    {
        RouteFinding route = Assert.Single(facts.Routes);
        return (route.Route, route.Framework, route.Line);
    }

    /// <summary>Kjører en skanning med en egen frist: uten vernet ville den hengt, og da feiler testen i stedet.</summary>
    private static async Task<T> Bounded<T>(Func<T> scan) => await Task.Run(scan).WaitAsync(TimeSpan.FromSeconds(30));

    private static bool TryLinkDirectory(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // Windows uten utviklermodus lager ikke lenker
        }
    }

    private void CopyFixture(string name, string? to = null)
    {
        string from = Path.Combine(RepoRoot(), "tests", "Queuey.Client.Cli.Tests", "Fixtures", "flows", name);
        string target = to ?? _root;
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(target, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private void File_(string relativePath, string content)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Queuey.Client.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find the repository root (Queuey.Client.sln).");
    }
}
