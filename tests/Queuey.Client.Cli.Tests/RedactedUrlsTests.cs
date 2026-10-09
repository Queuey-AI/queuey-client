using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Klientens side av Queuey #514 (2026-10-09): mottakerens URL leses redigert av en nøkkel og en innlogging. pull skriver aldri en
/// redigert URL inn i fila, apply sender filens verdi og viser 400 redacted_url_written_back tydelig, apply --check sammenligner
/// mot den redigerte lesingen, og plan viser en endring i den skjulte delen som det den er.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class RedactedUrlsTests
{
    // ── TargetUrlRedaction, kopiert på nytt fra #514 ────────────────────────

    [Theory]
    [InlineData("https://h.test/in/AbCdEfGhIjKlMn", "https://h.test/in/…")]          // et token av bokstaver leses ikke lenger som et ord
    [InlineData("https://h.test/orders/v1", "https://h.test/orders/v1")]
    [InlineData("/orders/s3cr3t-T0ken9", "/orders/…")]                               // en sti alene
    public void The_rules_are_queueys_from_514(string url, string shown)
        => Assert.Equal(shown, TargetUrlRedaction.Redact(url));

    [Fact]
    public void Text_redacts_a_url_without_a_scheme_and_an_encoded_one()
    {
        Assert.Equal("failed at hooks.slack.com/services/T1/B2/…", TargetUrlRedaction.RedactUrlsIn("failed at hooks.slack.com/services/T1/B2/xoxbSECRET"));
        Assert.DoesNotContain("hunter2", TargetUrlRedaction.RedactUrlsIn("ops:hunter2@shop.test/in") );
        Assert.DoesNotContain("s3cr3t", TargetUrlRedaction.RedactUrlsIn("https%3A%2F%2Fshop.test%2Fin%2Fs3cr3t9x"));
    }

    // ── pull → apply: ingen redigert URL i fila ─────────────────────────────

    private const string RedactedWorkspace = "https://hooks.shop.test/in/\u2026";
    private const string RedactedQueue = "https://warehouse.test/orders/\u2026";

    private static RecordingHandler PullServer() => new(req => req.Key switch
    {
        "GET /tenants/ten_abc/credentials" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
        "GET /tenants/ten_abc/config" => RecordingHandler.Json(HttpStatusCode.OK, new { delivery = new { baseUrl = RedactedWorkspace } }),
        "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
        "GET /queues/que_orders/config" => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            delivery = new { baseUrl = RedactedQueue },
            inherited = new { destination = false, auth = true, signing = true, rateLimit = true, behavior = true },
        }),
        _ => throw new InvalidOperationException(req.Key),
    });

    [Fact]
    public async Task Pull_writes_a_variable_for_each_redacted_url_and_says_where_the_full_one_is()
    {
        string dir = Directory.CreateTempSubdirectory("queuey-redacted-pull-").FullName;
        string path = Path.Combine(dir, "queuey.deploy.json");

        CliRun pull = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[]
        {
            "pull", "--file", path, "--tenant", "ten_abc", "--api-key", "qak_kid.secret", "--license", "lic_1",
            "--config", Path.Combine(dir, "none.json"),
        }), PullServer());

        Assert.True(pull.Exit == ExitCodes.Success, pull.Stdout + pull.Stderr);
        string written = File.ReadAllText(path);
        Assert.DoesNotContain("\u2026", written);
        Assert.DoesNotContain("%E2%80%A6", written, StringComparison.OrdinalIgnoreCase);
        JsonElement root = JsonDocument.Parse(written).RootElement;
        Assert.Equal("${QUEUEY_WORKSPACE_URL}", root.GetProperty("workspace").GetProperty("delivery").GetProperty("baseUrl").GetString());
        Assert.Equal("${QUEUEY_ORDERS_URL}", root.GetProperty("queues").GetProperty("orders").GetProperty("delivery").GetProperty("url").GetString());
        Assert.Contains("Set QUEUEY_WORKSPACE_URL to the full URL before you apply it.", pull.Stdout);
        Assert.Contains("Set QUEUEY_ORDERS_URL to the full URL before you apply it.", pull.Stdout);
        Assert.Contains("https://app.queuey.ai/console/t/ten_abc/q/que_orders", pull.Stdout);
    }

    [Fact]
    public async Task Apply_of_a_pulled_file_sends_the_full_urls_from_the_variables_and_never_a_redacted_one()
    {
        string dir = Directory.CreateTempSubdirectory("queuey-redacted-roundtrip-").FullName;
        string path = Path.Combine(dir, "queuey.deploy.json");
        await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("pull", "--file", path, "--tenant", "ten_abc")), PullServer());

        var apply = new RecordingHandler(req => req.Method.Method switch
        {
            "GET" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "PUT" => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
            _ => RecordingHandler.NoContent(),
        });
        // ${VAR} i deploy-fila utvides fra prosessens miljø.
        Environment.SetEnvironmentVariable("QUEUEY_WORKSPACE_URL", "https://hooks.shop.test/in/tok3n-Abc");
        Environment.SetEnvironmentVariable("QUEUEY_ORDERS_URL", "https://warehouse.test/orders/s3cr3t");
        CliRun run;
        try
        {
            run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git")), apply);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUEUEY_WORKSPACE_URL", null);
            Environment.SetEnvironmentVariable("QUEUEY_ORDERS_URL", null);
        }

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        string sent = string.Concat(apply.Requests.Select(r => r.Body ?? ""));
        Assert.Contains("https://warehouse.test/orders/s3cr3t", sent);
        Assert.Contains("https://hooks.shop.test/in/tok3n-Abc", sent);
        Assert.DoesNotContain("\u2026", sent);
        Assert.DoesNotContain("\\u2026", sent);
    }

    // ── apply: 400 redacted_url_written_back ────────────────────────────────

    private const string WrittenBackMessage =
        "The URL in delivery.targets[0].url is a redacted reading ('\u2026' stands for a part Queuey does not show to keys and logins), and it " +
        "matches no URL stored there, so Queuey can't tell which URL is meant. Nothing was changed.";

    private const string WrittenBackAction =
        "Send the full URL, for example from a variable (${RECEIVER_URL}) in the deployment file, or send the URL exactly as Queuey " +
        "showed it to keep the stored one.";

    [Fact]
    public async Task A_redacted_url_queuey_refuses_is_shown_with_what_to_do()
    {
        string dir = Directory.CreateTempSubdirectory("queuey-redacted-400-").FullName;
        string path = Path.Combine(dir, "queuey.deploy.json");
        File.WriteAllText(path, """{ "tenant": "ten_abc", "queues": { "orders": { "delivery": { "url": "https://warehouse.test/orders/\u2026" } } } }""");
        var api = new RecordingHandler(req => req.Method.Method switch
        {
            "GET" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            "PUT" => RecordingHandler.Error(HttpStatusCode.BadRequest, "redacted_url_written_back", WrittenBackMessage, WrittenBackAction),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git")), api);
        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--no-git", "--json")), api);

        Assert.NotEqual(ExitCodes.Success, human.Exit);
        string said = human.Stdout + human.Stderr;
        Assert.Contains("redacted_url_written_back", said);
        Assert.Contains("matches no URL stored there", said);
        Assert.Contains("Send the full URL, for example from a variable", said);
        // Før sendingen: fila har en redigert lesing, og variabelen pull ville skrevet, nevnes.
        Assert.Contains("queues.orders.delivery.url in the file is a redacted reading", human.Stderr);
        Assert.Contains("${QUEUEY_ORDERS_URL}", human.Stderr);
        Assert.Contains("redacted_url_written_back", json.Stdout);
        Assert.Contains("Send the full URL", json.Stdout);
    }

    // ── apply --check: en redigert URL sammenlignes slik den leses ──────────

    private static RecordingHandler CheckServer() => new(req => req.Key switch
    {
        "GET /tenants/ten_abc/credentials" => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
        "GET /tenants/ten_abc/config" => RecordingHandler.Json(HttpStatusCode.OK, new { }),
        "GET /tenants/ten_abc/queues" => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
        "GET /queues/que_orders/config" => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            delivery = new { baseUrl = RedactedQueue },
            inherited = new { destination = false, auth = true, signing = true, rateLimit = true, behavior = true },
        }),
        _ => RecordingHandler.Error(HttpStatusCode.NotFound, "not_found", "Not found."),
    });

    [Theory]
    [InlineData("https://warehouse.test/orders/s3cr3t-T0ken", true)]     // bare den skjulte delen kan være annerledes: ingen drift her
    [InlineData("https://elsewhere.test/orders/s3cr3t-T0ken", false)]    // verten er synlig: drift
    public async Task Check_compares_a_redacted_url_as_the_files_url_reads_redacted_and_says_plan_sees_the_rest(string url, bool inSync)
    {
        string dir = Directory.CreateTempSubdirectory("queuey-redacted-check-").FullName;
        string path = Path.Combine(dir, "queuey.deploy.json");
        File.WriteAllText(path, $$"""{ "tenant": "ten_abc", "queues": { "orders": { "delivery": { "url": "{{url}}" } } } }""");

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--check", "--json")), CheckServer());
        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", path, "--check")), CheckServer());

        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal(inSync, root.GetProperty("inSync").GetBoolean());
        Assert.Equal("queues.orders.delivery.url", root.GetProperty("comparedRedacted")[0].GetString());
        Assert.Contains("queuey plan shows it", human.Stdout);
        // Filens hemmelighet vises aldri i driften: den sammenlignes redigert.
        Assert.DoesNotContain("s3cr3t", json.Stdout + human.Stdout);
    }

    // ── plan: en endring i den skjulte delen ────────────────────────────────

    [Fact]
    public async Task Plan_shows_a_change_that_reads_the_same_on_both_sides_as_a_change_in_the_hidden_part()
    {
        string dir = Directory.CreateTempSubdirectory("queuey-redacted-plan-").FullName;
        string path = Path.Combine(dir, "queuey.deploy.json");
        File.WriteAllText(path, """{ "tenant": "ten_abc", "queues": { "orders": { "delivery": { "url": "https://warehouse.test/orders/n3w-s3cr3t" } } } }""");
        var api = new RecordingHandler(req => req switch
        {
            { Method.Method: "GET" } when req.Path.EndsWith("/queues", StringComparison.Ordinal)
                => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
            { Method.Method: "PUT", Path: "/queues" }
                => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
            _ when req.Path.Contains("delivery", StringComparison.Ordinal) => RecordingHandler.Json(HttpStatusCode.OK, new
            {
                dryRun = true, target = "queue que_orders",
                changes = new object[] { new { path = "delivery.targets[0].url", from = RedactedQueue, to = RedactedQueue } },
                notes = Array.Empty<string>(),
            }),
            _ => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "queue que_orders", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
        });

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", path)), api);
        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", path, "--json")), api);

        Assert.True(human.Exit == ExitCodes.Success, human.Stdout + human.Stderr);
        Assert.Contains("the hidden part of the URL changes", human.Stdout);
        JsonElement change = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("steps").EnumerateArray()
            .SelectMany(s => s.GetProperty("changes").EnumerateArray()).Single();
        Assert.True(change.GetProperty("hiddenPartChanged").GetBoolean());
    }

    // ── K3: listen bruker Queuey sine regler ────────────────────────────────

    [Fact]
    public void Listen_prints_addresses_by_queueys_rules_from_514()
    {
        // Et token av bokstaver ble vist med den gamle porten; #514 skjuler det.
        Assert.Equal("https://h.test/in/…", UrlRedaction.Redact("https://h.test/in/AbCdEfGhIjKlMn"));
        Assert.Equal("/in/…", UrlRedaction.EndpointPath(null, "/in/AbCdEfGhIjKlMn?token=x"));
        Assert.Equal("/services/T1/B2/…", UrlRedaction.EndpointPath("https://hooks.slack.com/services/T1/B2/xoxb", "/ignored"));
    }

    // ── N1: en ekstra runde over alle strenger ──────────────────────────────

    [Fact]
    public async Task Every_string_in_an_operator_answer_is_redacted_also_in_arrays_and_previews()
    {
        const string secret = "https://ops:hunter2@shop.test/in/s3cr3t-T0ken?sig=abc123";
        var api = new RecordingHandler(req => req.Key == "GET /events/que_1/incident-report"
            ? RecordingHandler.Json(HttpStatusCode.OK, new
            {
                incidentType = "locked",
                requiredActions = new[] { $"Fix {secret}" },
                lastFailedEvent = new { responsePreview = $"redirect to {secret}", payloadHeadersJson = $"{{\"Location\":\"{secret}\"}}" },
            })
            : req.Key == "GET /queues/que_1/metrics/snapshot" ? RecordingHandler.Json(HttpStatusCode.OK, new { })
            : RecordingHandler.Json(HttpStatusCode.OK, new { items = Array.Empty<object>() }));

        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("diagnose", "que_1", "--json")), api);

        Assert.True(json.Exit == ExitCodes.Success, json.Stdout + json.Stderr);
        foreach (string leak in new[] { "hunter2", "s3cr3t", "abc123" })
            Assert.DoesNotContain(leak, json.Stdout);
    }
}
