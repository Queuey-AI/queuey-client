using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// En leverings-URL på maskinen eller et privat nett (Queuey F2.3, 2026-10-06): Queuey sin levering når den aldri, og Queuey
/// avviser skrivingen. CLI-en sier det før noe er sendt, med lokal videresending som veien. Står API-et selv på maskinen
/// eller et privat nett, er det en selv-hostet eller lokal Queuey, og bare serveren vet hva den får nå.
/// </summary>
public class DeploymentDestinationsTests
{
    [Theory]
    [InlineData("http://localhost:3000/api/stripe", "on this machine")]
    [InlineData("http://api.localhost/hook", "on this machine")]
    [InlineData("http://127.0.0.1:5000", "on this machine")]
    [InlineData("http://[::1]/", "on this machine")]
    [InlineData("http://10.0.0.7/hook", "a private or reserved address")]
    [InlineData("http://192.168.1.20:8080/", "a private or reserved address")]
    [InlineData("http://169.254.169.254/latest", "a private or reserved address")]
    [InlineData("http://[fd00::1]/", "a private or reserved address")]
    public void A_url_queueys_delivery_never_reaches_is_named_with_the_local_listener_as_the_way(string url, string where)
    {
        string? refusal = DeploymentDestinations.LocalTargetRefusal(url);

        Assert.NotNull(refusal);
        Assert.Contains(where, refusal);
        Assert.Contains("\"kind\": \"localForward\"", refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/orders")]
    [InlineData("https://hooks.example.com/stripe")]
    [InlineData("https://localhost.example.com/")]
    [InlineData("${QUEUEY_ORDERS_URL}")]
    public void Anything_else_is_left_to_the_rest_of_the_checks(string? url)
        => Assert.Null(DeploymentDestinations.LocalTargetRefusal(url));

    [Fact]
    public void Every_problem_in_the_file_is_named_with_where_it_is()
    {
        DeploymentFile file = DeploymentFile.Parse("""
        { "workspace": { "delivery": { "baseUrl": "http://localhost:8080" } },
          "queues": { "orders": { "delivery": { "url": "/orders" } }, "stripe": { "delivery": { "url": "http://127.0.0.1:5000/stripe" } } } }
        """);

        Assert.Equal(new[] { "workspace.delivery.baseUrl", "queues.stripe.delivery.url" },
            file.LocalDestinationProblems().Select(p => p.Substring(0, p.IndexOf(':'))).ToArray());
    }

    [Fact]
    public async Task Plan_and_apply_refuse_a_local_destination_before_anything_is_sent()
    {
        var api = new StubHttpMessageHandler(_ => throw new InvalidOperationException("nothing should be sent"));
        QueueyService service = WaasTestHost.Build(apiStub: api);
        const string json = """{ "queues": { "stripe": { "delivery": { "url": "http://localhost:3000/api/stripe" } } } }""";

        var plan = await Assert.ThrowsAsync<QueueyConfigurationException>(() => service.PlanDeploymentAsync(DeploymentFile.Parse(json)));
        var apply = await Assert.ThrowsAsync<QueueyConfigurationException>(() => service.ApplyDeploymentAsync(DeploymentFile.Parse(json)));

        Assert.Contains("queues.stripe.delivery.url: it points at localhost, which is on this machine", plan.Message);
        Assert.Contains("nothing was sent", apply.Message);
        Assert.Contains("localForward", apply.SuggestedAction);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Against_an_api_on_this_machine_the_server_decides()
    {
        // En lokal Queuey kan levere til loopback (Delivery:Egress:AllowedHosts i Local-målet), og bare serveren vet det.
        var api = new StubHttpMessageHandler(req =>
            req.Method.Method == "GET"
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, Array.Empty<object>())
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, new { publicId = "que_stripe", displayName = "stripe", created = true, hasDeliveryTarget = true }));
        QueueyService service = WaasTestHost.Build(apiStub: api, configure: o => o.ApiBaseAddress = new Uri("http://localhost:5100"));

        await service.ApplyDeploymentAsync(DeploymentFile.Parse("""{ "queues": { "stripe": { "delivery": { "url": "http://localhost:3000/api/stripe" } } } }"""));

        Assert.Contains(api.Requests, r => r.RequestUri!.AbsolutePath == "/queues/que_stripe/delivery");
    }

    [Theory]
    [InlineData("http://localhost:5100", true)]
    [InlineData("http://10.1.2.3", true)]
    [InlineData("https://api.queuey.ai", false)]
    public void An_api_on_this_machine_or_a_private_network_counts_as_local(string apiBase, bool local)
        => Assert.Equal(local, DeploymentDestinations.IsLocal(new Uri(apiBase)));
}
