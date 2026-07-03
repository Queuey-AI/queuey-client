using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>Builds a <see cref="QueueyService"/> wired to stub transports for isolated unit tests.</summary>
internal static class WaasTestHost
{
    public static object DefaultPublishBody() => new
    {
        queuePublicId = "que_1",
        eventId = "evt_1",
        receivedAtUtc = DateTimeOffset.UnixEpoch,
        mode = "Deliver",
        replayed = false,
    };

    public static object DefaultApplyBody(string name = "order-events") => new
    {
        publicId = "cat_1",
        key = name,
        status = "Published",
    };

    public static QueueyOptions Options(Action<QueueyOptions>? configure = null)
    {
        var options = new QueueyOptions
        {
            ApiKey = "qak_kid.secret",
            TenantPublicId = "ten_abc",
            LicensePublicId = "lic_1",
            ApiBaseAddress = new Uri("https://api.example"),
            IngressBaseAddress = new Uri("https://ingress.example"),
        };
        configure?.Invoke(options);
        return options;
    }

    public static QueueyService Build(
        HttpMessageHandler? apiStub = null,
        HttpMessageHandler? ingressStub = null,
        IEnumerable<StreamDefinition>? streams = null,
        Action<QueueyOptions>? configure = null)
    {
        QueueyOptions options = Options(configure);

        var ingress = ingressStub ?? new StubHttpMessageHandler(_ => StubHttpMessageHandler.Accepted(DefaultPublishBody()));
        var api = apiStub ?? new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, DefaultApplyBody()));

        var client = new QueueyClient(options, new HttpClient(ingress));
        var controlPlane = new QueueyControlPlaneClient(new HttpClient(api), options);
        var registry = new StreamRegistry(streams ?? Array.Empty<StreamDefinition>());

        return new QueueyService(client, controlPlane, registry, options);
    }
}
