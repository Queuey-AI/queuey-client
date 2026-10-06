using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Den komplette deploy-fila (Queuey F2.3, 2026-10-06): <c>delivery.kind</c> sender en kø til en lokal lytter eller over
/// HTTP, og <c>ingress.signedRequest</c> verifiserer en providers signatur med en navngitt credential, også en som ikke er
/// lagret ennå. Apply sier fra om det køen venter på: en lytter, eller en credential som gjør at ingressen avviser alt.
/// </summary>
public class DeploymentKindAndSignedRequestTests
{
    private sealed class Api
    {
        public bool Created { get; init; } = true;
        public bool HasTarget { get; init; } = true;
        public object[] Rows { get; init; } = Array.Empty<object>();
        public object[] Credentials { get; init; } = Array.Empty<object>();

        /// <summary>Svar som overstyrer standarden, nøklet på «METODE sti».</summary>
        public Dictionary<string, Func<HttpResponseMessage>> Answers { get; } = new(StringComparer.Ordinal);

        public StubHttpMessageHandler Stub { get; }

        public Api() => Stub = new StubHttpMessageHandler((_, req, body) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (Answers.TryGetValue($"{req.Method.Method} {path}", out Func<HttpResponseMessage>? answer))
                return answer();
            if (req.Method == HttpMethod.Get && path.EndsWith("/credentials", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Credentials);
            if (req.Method == HttpMethod.Get && path.EndsWith("/queues", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, Rows);
            if (req.Method == HttpMethod.Put && path == "/queues")
            {
                string name = JsonDocument.Parse(body!).RootElement.GetProperty("displayName").GetString()!;
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "que_" + name, displayName = name, created = Created, hasDeliveryTarget = HasTarget });
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        public List<string> Paths => Stub.Requests.Select(r => $"{r.Method.Method} {r.RequestUri!.AbsolutePath}").ToList();

        public JsonElement Body(string methodAndPath)
        {
            int i = Paths.IndexOf(methodAndPath);
            Assert.True(i >= 0, $"expected {methodAndPath}, got: {string.Join(", ", Paths)}");
            return JsonDocument.Parse(Stub.Bodies[i]!).RootElement;
        }
    }

    private static Task<QueueSyncResult> Apply(Api api, string json)
        => WaasTestHost.Build(apiStub: api.Stub).ApplyDeploymentAsync(DeploymentFile.Parse(json));

    private static object QueueConfig(string kind = "Http", object? signedRequest = null, string authMode = "SignedRequest") => new
    {
        delivery = new { kind },
        policy = new { },
        inherited = new { destination = true, auth = true, signing = true, rateLimit = true, behavior = true },
        ingress = new { authMode, signedRequest },
    };

    // ── delivery.kind: lesingen ────────────────────────────────────────────

    [Theory]
    [InlineData("localForward", DeploymentDeliveryKind.LocalForward)]
    [InlineData("LOCALFORWARD", DeploymentDeliveryKind.LocalForward)]
    [InlineData("http", DeploymentDeliveryKind.Http)]
    public void A_queues_kind_is_read_in_any_casing_and_set_apart_from_the_destination_patch(string kind, DeploymentDeliveryKind expected)
    {
        DeploymentQueuePlan plan = DeploymentFile.Parse($$"""{ "queues": { "stripe": { "delivery": { "kind": "{{kind}}" } } } }""").Resolve().Single();

        Assert.Equal(expected, plan.Kind);
        Assert.Null(plan.Delivery);   // en type alene sender ingen PATCH av levering
    }

    [Fact]
    public void An_unknown_kind_is_refused_and_a_variable_is_checked_once_it_has_a_value()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() =>
            DeploymentFile.Parse("""{ "queues": { "stripe": { "delivery": { "kind": "listener" } } } }""").Resolve());
        Assert.Contains("queues.stripe.delivery.kind must be one of http, localForward", ex.Message);

        DeploymentFile file = DeploymentFile.Parse("""{ "queues": { "stripe": { "delivery": { "kind": "${STRIPE_KIND}" } } } }""");
        Assert.Null(file.Resolve().Single().Kind);   // som fila står, i en dry run
        Assert.Equal(DeploymentDeliveryKind.LocalForward, file.Expand(v => v == "STRIPE_KIND" ? "localForward" : null).Resolve().Single().Kind);
        Assert.Throws<QueueyConfigurationException>(() => file.Expand(v => v == "STRIPE_KIND" ? "nowhere" : null).Resolve());
        Assert.Contains("STRIPE_KIND", file.ReferencedVariables());
    }

    // ── delivery.kind: apply ───────────────────────────────────────────────

    [Fact]
    public async Task Apply_sends_a_queue_to_a_local_listener_after_its_destination_and_before_its_mode()
    {
        var api = new Api { HasTarget = false };

        QueueSyncResult result = await Apply(api, """
        { "queues": { "stripe": { "delivery": { "url": "/api/stripe", "kind": "localForward" } } } }
        """);

        Assert.True(api.Body("PATCH /queues/que_stripe/local-forward").GetProperty("enabled").GetBoolean());
        Assert.False(api.Body("PATCH /queues/que_stripe/delivery").TryGetProperty("kind", out _), "the kind is not a field of the delivery patch");
        Assert.True(api.Paths.IndexOf("PATCH /queues/que_stripe/delivery") < api.Paths.IndexOf("PATCH /queues/que_stripe/local-forward"));
        Assert.True(api.Paths.IndexOf("PATCH /queues/que_stripe/local-forward") < api.Paths.IndexOf("PATCH /queues/que_stripe/mode-change"));

        // En lokal lytter er et sted å levere: den nye køen leverer, og eventene logges ikke bort.
        Assert.Equal(3, api.Body("PATCH /queues/que_stripe/mode-change").GetProperty("mode").GetInt32());
        Assert.Equal("deliver", result.Applied.Single().Mode);
        Assert.Contains(result.Warnings, w => w.Contains("delivers to a local listener") && w.Contains("queuey listen --queue stripe"));
    }

    [Fact]
    public async Task Apply_of_kind_http_turns_forwarding_off_and_says_when_the_workspace_keeps_it_on()
    {
        var api = new Api { Created = false, Rows = new object[] { new { publicId = "que_stripe", displayName = "stripe", mode = "Deliver", hasDeliveryTarget = true } } };
        api.Answers["GET /queues/que_stripe/config"] = () => StubHttpMessageHandler.Json(HttpStatusCode.OK, QueueConfig(kind: "LocalForward"));

        QueueSyncResult result = await Apply(api, """{ "queues": { "stripe": { "delivery": { "kind": "http" } } } }""");

        Assert.False(api.Body("PATCH /queues/que_stripe/local-forward").GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain("PATCH /queues/que_stripe/delivery", api.Paths);
        Assert.Contains(result.Warnings, w => w.Contains("declares \"kind\": \"http\"") && w.Contains("keeps forwarding"));
    }

    [Fact]
    public async Task A_file_that_says_nothing_about_the_kind_leaves_it_and_reads_nothing_more()
    {
        var api = new Api();

        await Apply(api, """{ "queues": { "orders": { "delivery": { "url": "/orders" } } } }""");

        Assert.DoesNotContain(api.Paths, p => p.EndsWith("/local-forward", StringComparison.Ordinal));
        Assert.DoesNotContain(api.Paths, p => p.EndsWith("/config", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_key_without_queue_listen_fails_the_queue_with_queueys_refusal()
    {
        var api = new Api();
        api.Answers["PATCH /queues/que_stripe/local-forward"] = () => StubHttpMessageHandler.Json(HttpStatusCode.Forbidden,
            new { error = new { code = "queue_listen_required", message = "Routing this queue to a local listener needs queue.listen on the queue." } });

        QueueySyncException ex = await Assert.ThrowsAsync<QueueySyncException>(() => Apply(api, """
        { "queues": { "stripe": { "delivery": { "kind": "localForward" } } } }
        """));

        QueueApplyResult stripe = ex.Queues!.Applied.Single();
        Assert.False(stripe.Succeeded);
        Assert.Equal("queue_listen_required", stripe.Error!.ErrorCode);
        Assert.DoesNotContain("PATCH /queues/que_stripe/mode-change", api.Paths);
    }

    // ── ingress.signedRequest ─────────────────────────────────────────────

    [Fact]
    public void A_signed_request_needs_its_template_and_an_auth_mode_that_checks_it()
    {
        var noTemplate = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(
            """{ "queues": { "stripe": { "ingress": { "signedRequest": { "credentialRef": "stripe-whsec" } } } } }""").Resolve());
        Assert.Contains("queues.stripe.ingress.signedRequest needs a template", noTemplate.Message);

        var unchecked_ = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(
            """{ "workspace": { "ingress": { "authMode": "ApiKey", "signedRequest": { "template": "stripe", "credentialRef": "whsec" } } } }""").Resolve());
        Assert.Contains("authMode ApiKey checks no signature", unchecked_.Message);

        var empty = Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(
            """{ "queues": { "stripe": { "ingress": { "signedRequest": { "template": "stripe", "credentialRef": " " } } } } }""").Resolve());
        Assert.Contains("credentialRef is empty", empty.Message);

        // Uten authMode kan den være arvet, og da er den gyldig.
        DeploymentFile.Parse("""{ "queues": { "stripe": { "ingress": { "signedRequest": { "template": "stripe", "credentialRef": "whsec" } } } } }""").Resolve();
    }

    [Fact]
    public async Task Apply_sends_the_credential_by_name_as_written_and_does_not_need_it_to_exist()
    {
        // Queuey slår navnet opp og venter på en credential som ikke er lagret ennå. CLI-en stopper ikke apply for det, slik den
        // gjør for en leverings-credential som mangler.
        var api = new Api();
        api.Answers["GET /queues/que_stripe/config"] = () => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            QueueConfig(signedRequest: new { template = "stripe", credentialRef = (string?)null, pendingCredential = "stripe-whsec" }));

        QueueSyncResult result = await Apply(api, """
        { "queues": { "stripe": { "ingress": { "authMode": "SignedRequest", "signedRequest": { "template": "stripe", "credentialRef": "stripe-whsec" } } } } }
        """);

        JsonElement signed = api.Body("PATCH /queues/que_stripe/ingress").GetProperty("signedRequest");
        Assert.Equal("stripe", signed.GetProperty("template").GetString());
        Assert.Equal("stripe-whsec", signed.GetProperty("credentialRef").GetString());

        string warning = Assert.Single(result.Warnings, w => w.Contains("stripe-whsec"));
        Assert.Contains("which is not stored yet, so its ingress refuses every event", warning);
        Assert.Contains("queuey credentials set --name stripe-whsec --type HmacSigning --key-id stripe-whsec --from-env", warning);
    }

    [Fact]
    public async Task A_credential_name_comes_from_a_variable_like_a_delivery_credential()
    {
        var api = new Api();
        api.Answers["GET /queues/que_stripe/config"] = () => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            QueueConfig(signedRequest: new { template = "stripe", credentialRef = "cred_live", pendingCredential = (string?)null }));
        Environment.SetEnvironmentVariable("QUEUEY_TEST_STRIPE_SECRET_NAME", "stripe-whsec-live");
        try
        {
            QueueSyncResult result = await Apply(api, """
            { "queues": { "stripe": { "ingress": { "signedRequest": { "template": "stripe", "credentialRef": "${QUEUEY_TEST_STRIPE_SECRET_NAME}" } } } } }
            """);

            Assert.Equal("stripe-whsec-live", api.Body("PATCH /queues/que_stripe/ingress").GetProperty("signedRequest").GetProperty("credentialRef").GetString());
            Assert.DoesNotContain(result.Warnings, w => w.Contains("not stored yet"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUEUEY_TEST_STRIPE_SECRET_NAME", null);
        }
    }

    [Fact]
    public async Task A_workspace_ingress_that_waits_is_said_once_for_the_workspace()
    {
        var api = new Api();
        api.Answers["GET /tenants/ten_abc/config"] = () => StubHttpMessageHandler.Json(HttpStatusCode.OK, new
        {
            ingress = new { authMode = "SignedRequest", signedRequest = new { template = "stripe", pendingCredential = "stripe-whsec" } },
        });

        QueueSyncResult result = await Apply(api, """
        { "workspace": { "ingress": { "authMode": "SignedRequest", "signedRequest": { "template": "stripe", "credentialRef": "stripe-whsec" } } },
          "queues": { "orders": {}, "refunds": {} } }
        """);

        string warning = Assert.Single(result.WorkspaceWarnings);
        Assert.Contains("The workspace's ingress verifies stripe signatures with the credential 'stripe-whsec'", warning);
        Assert.Contains("every queue that inherits it refuses every event", warning);
        Assert.Single(result.Warnings, w => w.Contains("stripe-whsec"));   // ikke én per kø
        Assert.Equal(warning, result.Warnings[0]);
    }
}
