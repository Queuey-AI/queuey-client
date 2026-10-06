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

    // Queuey F2.3-review (2026-10-06): de samme sperrede områdene som serveren etter #432 og #433. Vektorene er de samme
    // som Queuey sine (IsBlockedIp-teoriene i SsrfEgressPolicyTests i Queuey), så de to sidene sperrer det samme.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.9.9.9")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::127.0.0.1")] // ::/96, IPv4-kompatibel
    [InlineData("::10.0.0.1")]
    [InlineData("::8.8.8.8")]
    [InlineData("64:ff9b::a00:1")] // 64:ff9b::/96, NAT64: 10.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")] // 169.254.169.254
    [InlineData("64:ff9b::808:808")] // 8.8.8.8
    [InlineData("64:ff9b:1::a00:1")] // 64:ff9b:1::/48, NAT64 til lokal bruk
    [InlineData("64:ff9b:1:ffff::808:808")]
    [InlineData("2002:a9fe:a9fe::1")] // 2002::/16, 6to4: 169.254.169.254
    [InlineData("2002:7f00:1::1")] // 127.0.0.1
    [InlineData("2002:808:808::1")] // 8.8.8.8
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // 2001::/32, Teredo
    [InlineData("2001::f5ff:fffe")] // klientens IPv4 10.0.0.1, XOR-tilslørt
    [InlineData("168.63.129.16")] // Azure WireServer
    [InlineData("::ffff:168.63.129.16")] // samme adresse, IPv4-mappet
    [InlineData("100.100.100.200")] // Alibaba, i CGNAT
    [InlineData("fd00:ec2::254")] // AWS IMDS over IPv6, unik-lokal
    [InlineData("::ffff:0:a00:1")] // IPv4-oversatt (RFC 2765), verken mappet eller kompatibel
    [InlineData("100::1")] // discard-only (RFC 6666)
    [InlineData("fec0::1")] // site-lokal (foreldet)
    [InlineData("ff02::1")] // multicast
    [InlineData("1fff:ffff::1")] // rett under 2000::/3
    [InlineData("4000::1")] // rett over 2000::/3
    [InlineData("2001:1::1")] // 2001::/23: PCP-anycast
    [InlineData("2001:1::2")] // TURN-anycast
    [InlineData("2001:2::1")] // benchmarking (2001:2::/48)
    [InlineData("2001:3::1")] // AMT
    [InlineData("2001:4:112::1")] // AS112
    [InlineData("2001:10::1")] // ORCHID (2001:10::/28)
    [InlineData("2001:20::1")] // ORCHIDv2 (2001:20::/28)
    [InlineData("2001:1ff:ffff::1")] // i den siste /32-en i 2001::/23
    [InlineData("2001:db8::1")] // dokumentasjon
    [InlineData("2001:db8:ffff:ffff::1")]
    [InlineData("3ffe::1")] // 6bone
    [InlineData("3ffe:ffff::1")]
    [InlineData("3fff::1")] // dokumentasjon (3fff::/20)
    [InlineData("3fff:fff:ffff::1")] // i den siste /32-en i 3fff::/20
    [InlineData("192.0.2.1")] // TEST-NET-1
    [InlineData("192.0.2.255")]
    [InlineData("192.88.99.1")] // 6to4-reléet
    [InlineData("198.51.100.1")] // TEST-NET-2
    [InlineData("203.0.113.1")] // TEST-NET-3
    [InlineData("203.0.113.255")]
    [InlineData("::ffff:198.51.100.7")] // en TEST-NET-adresse, IPv4-mappet
    public void An_address_queueys_egress_guard_blocks_is_refused_before_anything_is_sent(string address)
        => Assert.NotNull(DeploymentDestinations.LocalTargetRefusal($"https://{UrlHost(address)}/hook"));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")] // just below 172.16/12
    [InlineData("172.32.0.1")] // just above 172.16/12
    [InlineData("100.63.255.255")] // just below CGNAT /10
    [InlineData("100.128.0.1")] // just above CGNAT /10
    [InlineData("168.63.129.17")] // naboen til Azure WireServer er en vanlig offentlig adresse
    [InlineData("192.0.3.1")] // rett over TEST-NET-1 (192.0.2.0/24)
    [InlineData("192.88.100.1")] // rett over 6to4-reléet (192.88.99.0/24)
    [InlineData("198.51.101.1")] // rett over TEST-NET-2
    [InlineData("203.0.114.1")] // rett over TEST-NET-3
    [InlineData("2606:4700:4700::1111")] // public v6 (Cloudflare)
    [InlineData("2001:4860:4860::8888")] // offentlig v6 (Google) i 2001::/16, utenfor 2001::/23
    [InlineData("2001:200::1")] // rett over 2001::/23 (APNIC)
    [InlineData("2001:db9::1")] // rett over dokumentasjonsblokken 2001:db8::/32
    [InlineData("2003::1")] // rett over 6to4 (2002::/16)
    [InlineData("3fff:1000::1")] // rett over 3fff::/20
    [InlineData("3ffd:ffff::1")] // rett under 6bone
    public void An_address_queueys_egress_guard_lets_through_is_left_to_the_server(string address)
        => Assert.Null(DeploymentDestinations.LocalTargetRefusal($"https://{UrlHost(address)}/hook"));

    private static string UrlHost(string address)
        => IPAddress.Parse(address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address;

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
