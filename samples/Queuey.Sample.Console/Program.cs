using System;
using System.Net.Http;
using System.Threading.Tasks;
using Queuey.Client;

// Minimal "publish" happy path.
//
// Defaults to Production (api.queuey.ai + ingress.queuey.ai). To test against a local or
// self-hosted Queuey, point the SDK at its hosts with QUEUEY_API_BASE / QUEUEY_INGRESS_BASE:
//
//   QUEUEY_TENANT=ten_...  QUEUEY_API_KEY=qak_... \
//   QUEUEY_INGRESS_BASE=http://localhost:5084  QUEUEY_API_BASE=http://localhost:5223 \
//   dotnet run

var options = new QueueyOptions
{
    Environment = QueueyEnvironment.Production, // default hosts; overridden below when the env vars are set
    TenantPublicId = Environment.GetEnvironmentVariable("QUEUEY_TENANT") ?? "ten_your_tenant",
    ApiKey = Environment.GetEnvironmentVariable("QUEUEY_API_KEY") ?? "qak_your.key",
    IngressBaseAddress = ReadUri("QUEUEY_INGRESS_BASE"),
    ApiBaseAddress = ReadUri("QUEUEY_API_BASE"),
};

using var queuey = new QueueyClient(options);

Console.WriteLine($"Publishing to {options.ResolveIngressBaseAddress()} …");

try
{
    PublishResult result = await queuey.Ingress.PublishAsync(
        queueName: "orders",
        payload: new { orderId = "ord_123", total = 4200, currency = "NOK" },
        options: new PublishOptions { EventType = "order.created", GroupKey = "cust_42" });

    Console.WriteLine($"Published {result.EventId} → {result.QueuePublicId} (mode={result.Mode}, replayed={result.Replayed})");
}
catch (QueueyException ex)
{
    // The request reached Queuey but was rejected — status + code come from the mapped exception.
    Console.Error.WriteLine($"Queuey rejected the publish: {ex.StatusCode} {ex.ErrorCode} — {ex.Message}");
    Environment.ExitCode = 1;
}
catch (HttpRequestException ex)
{
    // Could not reach the host at all (e.g. nothing listening locally).
    Console.Error.WriteLine(
        $"Could not reach Queuey at {options.ResolveIngressBaseAddress()} ({ex.Message}). " +
        "Set QUEUEY_INGRESS_BASE / QUEUEY_API_BASE to a running instance, e.g. http://localhost:5084.");
    Environment.ExitCode = 1;
}

static Uri? ReadUri(string name)
{
    string? value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? null : new Uri(value, UriKind.Absolute);
}
