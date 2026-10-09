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
}
