# Outcome: direct to cloud with the .NET SDK

**Chosen when:** a .NET codebase publishes from somewhere the network is a
given, or from a worker that drains an outbox, bus or job queue the project
already owns. Same durability contract as `direct-http.md`: nothing before the
202 is durable unless the caller made it so.

## Shape

```csharp
var queuey = new QueueyClient(new QueueyOptions
{
    TenantPublicId = cfg["Queuey:Tenant"],
    ApiKey         = cfg["Queuey:ApiKey"],
    // Environment defaults to Production (api.queuey.ai + ingress.queuey.ai)
});

await queuey.Ingress.PublishAsync("orders", order, new PublishOptions
{
    EventType      = "order.created",
    GroupKey       = order.CustomerId,      // ordering lane
    IdempotencyKey = order.OrderId,         // derived from the event
});
```

For hosts with DI, `Queuey.Client` ships an `IHttpClientFactory`-friendly
registration; read the package README for the current extension name rather
than recalling it. Package targets `netstandard2.0` and `net8.0`, so .NET
Framework consumers are fine.

Packages, with explicit preview versions:

- `Queuey.Client` — publish, auth, transport. Nothing else.
- `Queuey.Client.Waas` — only if the project wants to *declare* queues and
  streams in code with `[QueueyQueue]` / `[QueueyModel]` and converge them on
  deploy. Otherwise declare in `queuey.deploy.json` and skip this package.

## Declaring the queue

Either is fine; pick one and say why:

- **Deployment file** (default): `queuey.deploy.json`, applied by
  `queuey apply` in the deploy pipeline, checked by `queuey apply --check` in
  CI. No secrets in the file; auth and signing name a `credentialRef`.
- **In code:** `[QueueyQueue("orders", Ordering = "bykey")]` on a marker
  type, `services.AddQueuey(o => …, b => b.AddQueue<OrderQueue>())`, and
  `queuey queue sync --assembly App.dll` on deploy. `SyncOnStartup()` exists
  and is off by default, because replicas in a rolling deploy would all apply
  the same declarations at once.

## Around the call

`PublishAsync` throws on non-202. Wrap it with a bounded retry for transient
failures and let `4xx` surface; the idempotency key makes the retry safe.

## Verify

One publish returns a `PublishResult` with the event id; the console shows the
event delivered. `queuey apply --check` exits zero.

## Read

- Package README on https://www.nuget.org/packages/Queuey.Client
- https://app.queuey.ai/docs/quickstart
- https://app.queuey.ai/docs/reference/waas-api (streams, packages,
  partners — only if `Queuey.Client.Waas` is in play)
- `README.md` in https://github.com/Queuey-AI/queuey-client
