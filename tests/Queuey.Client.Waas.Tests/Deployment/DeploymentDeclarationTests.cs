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
/// Ingress og levering i deployment-fila sjekkes før noe sendes (review 2026-10-05). En kilde uten navn fjernet den
/// lagrede, en kilde uten from betydde header uten at fila sa det, og backenden gjør en ukjent authMode for levering om
/// til None og en ukjent method til POST uten å si fra.
/// </summary>
public class DeploymentDeclarationTests
{
    private static Exception Refusal(string json) => Assert.Throws<QueueyConfigurationException>(() => DeploymentFile.Parse(json).Resolve());

    [Theory]
    [InlineData("""{ "workspace": { "ingress": { "eventType": { "from": "body" } } } }""", "workspace.ingress.eventType needs a name")]
    [InlineData("""{ "workspace": { "ingress": { "groupKey": {} } } }""", "workspace.ingress.groupKey needs a name")]
    [InlineData("""{ "queues": { "orders": { "ingress": { "eventType": { "name": "type" } } } } }""", "queues.orders.ingress.eventType needs \"from\": header, query, body, the place 'type' is read from.")]
    [InlineData("""{ "queues": { "orders": { "ingress": { "groupKey": { "from": "bdy", "name": "customerId" } } } } }""", "queues.orders.ingress.groupKey.from must be one of header, query, body; got 'bdy'.")]
    public void A_source_that_does_not_say_what_it_reads_is_refused_before_anything_is_sent(string json, string message)
    {
        Assert.Contains(message, Refusal(json).Message);
    }

    [Fact]
    public void A_source_reads_from_in_any_casing_and_an_empty_name_needs_no_from()
    {
        IReadOnlyList<DeploymentQueuePlan> plans = DeploymentFile.Parse("""
            { "workspace": { "ingress": { "eventType": { "from": "Body", "name": "type" }, "groupKey": { "name": "" } } },
              "queues": { "orders": { "ingress": { "eventType": { "from": "QUERY", "name": "event" } } } } }
            """).Resolve();

        Assert.Single(plans);
    }

    [Fact]
    public async Task An_empty_name_is_sent_as_a_removal_and_a_named_source_as_written()
    {
        var api = new StubHttpMessageHandler(req => StubHttpMessageHandler.DeployDefaults(req) ?? new HttpResponseMessage(HttpStatusCode.NoContent));

        await WaasTestHost.Build(apiStub: api).ApplyDeploymentAsync(DeploymentFile.Parse("""
            { "tenant": "ten_abc", "workspace": { "ingress": { "eventType": { "from": "body", "name": "type" }, "groupKey": { "name": "" } } } }
            """));

        int index = api.Requests.FindIndex(r => r.RequestUri!.AbsolutePath == "/tenants/ten_abc/ingress");
        JsonElement body = JsonDocument.Parse(api.Bodies[index]!).RootElement;
        Assert.Equal("body", body.GetProperty("eventType").GetProperty("from").GetString());
        Assert.Equal("type", body.GetProperty("eventType").GetProperty("name").GetString());

        // Backenden fjerner kilden på et tomt navn og leser ikke from, men feltet må stå i ContextSourceRequest.
        Assert.Equal("", body.GetProperty("groupKey").GetProperty("name").GetString());
        Assert.Equal("header", body.GetProperty("groupKey").GetProperty("from").GetString());
    }

    [Fact]
    public void A_removed_source_is_in_sync_when_Queuey_reads_none_and_drift_while_it_still_does()
    {
        // Før 2026-10-05 ble "header:" sammenlignet med ingenting, og en fil som fjernet en kilde meldte drift for alltid.
        DeploymentFile declared = DeploymentFile.Parse("""{ "workspace": { "ingress": { "groupKey": { "name": "" }, "eventType": { "from": "Body", "name": "type" } } } }""");
        DeploymentFile none = DeploymentFile.Parse("""{ "workspace": { "ingress": { "eventType": { "from": "body", "name": "type" } } } }""");
        DeploymentFile still = DeploymentFile.Parse("""{ "workspace": { "ingress": { "eventType": { "from": "body", "name": "type" }, "groupKey": { "from": "header", "name": "X-Customer" } } } }""");

        Assert.Empty(DeploymentDrift.Compare(declared, none));

        DriftItem drift = Assert.Single(DeploymentDrift.Compare(declared, still));
        Assert.Equal("workspace.ingress.groupKey", drift.Path);
        Assert.Equal("(removed)", drift.Declared);
        Assert.Equal("header:X-Customer", drift.Actual);
    }

    [Theory]
    [InlineData("""{ "workspace": { "delivery": { "authMode": "Api-Key" } } }""", "workspace.delivery.authMode must be one of None, Bearer, ApiKey, Basic, OAuth2ClientCredentials; got 'Api-Key'.")]
    [InlineData("""{ "workspace": { "delivery": { "method": "GET" } } }""", "workspace.delivery.method must be one of POST, PUT, PATCH; got 'GET'.")]
    [InlineData("""{ "queues": { "orders": { "delivery": { "url": "/orders", "authMode": "token" } } } }""", "queues.orders.delivery.authMode must be one of None, Bearer, ApiKey, Basic, OAuth2ClientCredentials; got 'token'.")]
    [InlineData("""{ "workspace": { "ingress": { "authMode": "Hmac" } } }""", "workspace.ingress.authMode must be one of None, ApiKey, SignedRequest, ApiKeyAndSignedRequest; got 'Hmac'.")]
    [InlineData("""{ "queues": { "orders": { "ingress": { "authMode": "signed" } } } }""", "queues.orders.ingress.authMode must be one of None, ApiKey, SignedRequest, ApiKeyAndSignedRequest; got 'signed'.")]
    public void An_auth_mode_or_method_Queuey_would_read_as_something_else_is_refused(string json, string message)
    {
        Assert.Equal(message, Refusal(json).Message);
    }

    [Fact]
    public void Auth_modes_and_methods_are_read_in_any_casing_as_Queuey_reads_them()
    {
        IReadOnlyList<DeploymentQueuePlan> plans = DeploymentFile.Parse("""
            { "workspace": { "delivery": { "baseUrl": "https://hooks.example.com", "authMode": "apikey", "method": "put" },
                             "ingress": { "authMode": "signedrequest" } },
              "queues": { "orders": { "delivery": { "url": "/orders", "authMode": "OAUTH2CLIENTCREDENTIALS" } } } }
            """).Resolve();

        Assert.Single(plans);
    }

    [Fact]
    public void Queues_null_reads_as_no_queues_like_a_file_without_them()
    {
        // Review 2026-10-05: "queues": null ga NullReferenceException.
        DeploymentFile file = DeploymentFile.Parse("""{ "queues": null, "workspace": { "retentionDays": 7 } }""");

        Assert.Empty(file.Queues);
        Assert.Empty(file.Expand().Resolve());
    }
}
