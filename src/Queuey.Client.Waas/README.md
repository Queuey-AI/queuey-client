# Queuey.Client.Waas

Webhooks-as-a-Service onboarding for [Queuey](https://queuey.ai). Producers
describe their event streams with an attribute, converge them on deploy, and
publish on save. Builds on `Queuey.Client`.

```csharp
[QueueyModel("order-events",
    EventTypes = new[] { "order.created", "order.paid" },
    Packages   = new[] { "standard", "premium" })]
public sealed class OrderCreated { public string OrderId { get; init; } = ""; }
```

```csharp
builder.Services.AddQueuey(
    o => { o.ApiKey = cfg["Queuey:ApiKey"];
           o.TenantPublicId = cfg["Queuey:TenantPublicId"];
           o.LicensePublicId = cfg["Queuey:LicensePublicId"]; },
    b => b.AddStream<OrderCreated>());
```

```csharp
// on deploy: create and converge streams, packages and memberships
await queuey.SyncStreamsAsync();

// on save: publish
await queuey.PushEventAsync("order-events", "order.created", order.OrderId, order);
```

A stream can belong to several packages — tiers, for instance — and each
declared package is created and assigned on sync.

## Queues

Declare a **queue** when you deliver to your own endpoint, and a **stream**
when integration partners subscribe. A stream has a queue underneath it, so a
full sync applies queues first.

```csharp
[QueueyQueue("orders", Ordering = "bykey", RetentionDays = 30)]
public sealed class OrderQueue { }

await queuey.SyncAsync();   // queues, then streams
```

Every policy field you leave off inherits from the workspace. Declaring a field
is how the code takes ownership of it.

## What "synced" guarantees

Names are validated **once, as `AddQueuey` builds the registry** — before any
host is built and without touching the network. A bad name is a startup error
carrying the corrected name, not a server error in the middle of a deploy.

A sync is not a transaction, since each stream is its own `PUT`, so it is built
to never look like one. It stops at the first failure, throws, and reports the
streams it never attempted. Applying is idempotent, so a fixed re-run
converges. Pass `ContinueOnError` when you want the full damage report in one
go; it still throws at the end.

`SyncOnStartup()` exists but is off by default. App instances often hold a
publish-only key, and a rolling deploy would have every replica applying the
same streams at once. In production the better home for it is a deploy step:

```bash
queuey sync --assembly bin/Release/net8.0/App.dll
```

## More

Full documentation is in the
[repository README](https://github.com/Queuey-AI/queuey-client#readme).

MIT licensed.
