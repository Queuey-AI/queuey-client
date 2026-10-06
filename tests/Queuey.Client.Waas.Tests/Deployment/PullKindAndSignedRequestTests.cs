using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Pull og drift-sjekk for leveringstypen og den signerte forespørselen (Queuey F2.3, 2026-10-06). En pull skriver det en
/// apply av fila setter tilbake, og <c>apply --check</c> melder drift når apply ville endret noe, også når en credential
/// ingressen ventet på, er lagret nå.
/// </summary>
public class PullKindAndSignedRequestTests
{
    private sealed class Workspace
    {
        public bool WhsecStored { get; init; }

        public HttpResponseMessage Route(HttpRequestMessage req)
        {
            string path = req.RequestUri!.AbsolutePath;

            if (path.EndsWith("/credentials", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, WhsecStored
                    ? new object[] { new { publicId = "cred_ws", name = "workspace-whsec", type = "HmacSigning" }, new { publicId = "cred_new", name = "stripe-whsec", type = "HmacSigning" } }
                    : new object[] { new { publicId = "cred_ws", name = "workspace-whsec", type = "HmacSigning" } });

            if (path.EndsWith("/tenants/ten_abc/config", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new { ingress = WorkspaceIngress, delivery = new { kind = "Http" } });

            if (path.EndsWith("/tenants/ten_abc/queues", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new[]
                {
                    new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true },
                    new { publicId = "que_stripe", displayName = "stripe", mode = "Deliver", hasDeliveryTarget = true },
                });

            if (path.EndsWith("/queues/que_stripe/config", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
                {
                    delivery = new { kind = "LocalForward", baseUrl = (string?)null, authMode = "None", timeoutMs = 30000 },
                    policy = Baseline,
                    inherited = new { destination = true, auth = true, signing = true, rateLimit = true, behavior = true },
                    tenantBaseline = new { policy = Baseline, ingress = WorkspaceIngress },
                    ingress = new
                    {
                        authMode = "SignedRequest", successStatusCode = 202,
                        signedRequest = new { template = "stripe", credentialRef = (string?)null, pendingCredential = "stripe-whsec" },
                    },
                });

            return StubHttpMessageHandler.Json(HttpStatusCode.OK, new
            {
                delivery = new { kind = "Http", baseUrl = (string?)null, authMode = "None", timeoutMs = 30000 },
                policy = Baseline,
                inherited = new { destination = true, auth = true, signing = true, rateLimit = true, behavior = true },
                tenantBaseline = new { policy = Baseline, ingress = WorkspaceIngress },
                ingress = WorkspaceIngress,
            });
        }

        private static object WorkspaceIngress => new
        {
            authMode = "SignedRequest", successStatusCode = 202,
            signedRequest = new { template = "stripe", credentialRef = "cred_ws", pendingCredential = (string?)null },
        };

        private static object Baseline => new { idempotent = false, dlqEnabled = true, retentionDays = 7, ordering = "fifo" };
    }

    private static QueueyService Build(Workspace workspace) => WaasTestHost.Build(apiStub: new StubHttpMessageHandler(workspace.Route));

    [Fact]
    public async Task Pull_writes_a_forwarding_queues_kind_and_each_signed_request_by_its_name()
    {
        DeploymentFile file = await Build(new Workspace()).PullDeploymentAsync("ten_abc");

        Assert.Equal("localForward", file.Queues["stripe"].Delivery!.Kind);
        Assert.Null(file.Queues["orders"].Delivery);   // http er standarden, og en arvet ingress skrives ikke

        Assert.Equal("stripe", file.Workspace!.Ingress!.SignedRequest!.Template);
        Assert.Equal("workspace-whsec", file.Workspace.Ingress.SignedRequest.CredentialRef);   // navnet, ikke cred_ws
        Assert.Equal("stripe-whsec", file.Queues["stripe"].Ingress!.SignedRequest!.CredentialRef);   // navnet den venter på

        // Det en pull skriver, er det apply leser.
        IReadOnlyList<DeploymentQueuePlan> plans = DeploymentFile.Parse(file.ToJson()).Resolve();
        Assert.Equal(DeploymentDeliveryKind.LocalForward, plans.Single(p => p.Definition.Name == "stripe").Kind);
        Assert.DoesNotContain("BoundCredentialId", file.ToJson());
    }

    [Fact]
    public async Task Check_reports_a_kind_the_queue_does_not_have_and_is_silent_on_one_it_has()
    {
        IReadOnlyList<DriftItem> drift = await Build(new Workspace()).CheckDeploymentAsync(DeploymentFile.Parse("""
        { "tenant": "ten_abc", "queues": {
            "orders": { "delivery": { "kind": "localForward" } },
            "stripe": { "delivery": { "kind": "LocalForward" } } } }
        """));

        DriftItem item = Assert.Single(drift);
        Assert.Equal("queues.orders.delivery.kind", item.Path);
        Assert.Equal("localForward", item.Declared);
        Assert.Equal("http", item.Actual);
    }

    [Fact]
    public async Task A_credential_named_by_name_or_by_id_matches_the_one_the_ingress_verifies_with()
    {
        QueueyService service = Build(new Workspace());

        Assert.Empty(await service.CheckDeploymentAsync(DeploymentFile.Parse("""
        { "tenant": "ten_abc", "workspace": { "ingress": { "signedRequest": { "template": "stripe", "credentialRef": "workspace-whsec" } } } }
        """)));
        Assert.Empty(await service.CheckDeploymentAsync(DeploymentFile.Parse("""
        { "tenant": "ten_abc", "workspace": { "ingress": { "signedRequest": { "template": "Stripe", "credentialRef": "cred_ws" } } } }
        """)));

        DriftItem other = Assert.Single(await service.CheckDeploymentAsync(DeploymentFile.Parse("""
        { "tenant": "ten_abc", "workspace": { "ingress": { "signedRequest": { "template": "stripe", "credentialRef": "rolled-whsec" } } } }
        """)));
        Assert.Equal("workspace.ingress.signedRequest.credentialRef", other.Path);
    }

    [Fact]
    public async Task An_ingress_waiting_for_a_credential_is_in_sync_until_the_credential_is_stored_and_drifts_after()
    {
        const string json = """
        { "tenant": "ten_abc", "queues": { "stripe": { "ingress": { "signedRequest": { "template": "stripe", "credentialRef": "stripe-whsec" } } } } }
        """;

        Assert.Empty(await Build(new Workspace()).CheckDeploymentAsync(DeploymentFile.Parse(json)));

        // Lagret nå: apply ville pekt ingressen på den, så filen og workspacet er ikke lenger i synk.
        DriftItem item = Assert.Single(await Build(new Workspace { WhsecStored = true }).CheckDeploymentAsync(DeploymentFile.Parse(json)));
        Assert.Equal("queues.stripe.ingress.signedRequest.credentialRef", item.Path);
        Assert.Contains("which is stored now: apply points the ingress at it", item.Actual);
    }

    [Fact]
    public void A_template_makes_each_queues_kind_a_variable_for_its_environment()
    {
        DeploymentFile template = DeploymentTemplate.ToTemplate(DeploymentFile.Parse("""
        { "queues": { "stripe": { "delivery": { "kind": "localForward" } }, "orders": { "delivery": { "url": "/orders" } } } }
        """), "dev");

        Assert.Equal("${QUEUEY_DEV_STRIPE_DELIVERY_KIND}", template.Queues["stripe"].Delivery!.Kind);
        Assert.Null(template.Queues["orders"].Delivery!.Kind);
        Assert.Equal("QUEUEY_ORDERS_DELIVERY_KIND", DeploymentTemplate.QueueKindVariable("orders"));
    }
}
