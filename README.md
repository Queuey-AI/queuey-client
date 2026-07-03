# Queuey.Client

The official .NET SDK for [Queuey](https://queuey.ai) — a webhooks / event-distribution
platform. **Decorate, sync, publish.**

> Status: early preview. Phase 1 (ingress publish) is being built first.

```csharp
var queuey = new QueueyClient(new QueueyOptions
{
    // Environment defaults to Production (api.queuey.ai + ingress.queuey.ai).
    TenantPublicId = "ten_…",
    ApiKey         = "qak_…",   // license-wide FullAccess key
});

await queuey.Ingress.PublishAsync("orders", order);
```

- Tiny and dependency-light: `System.Text.Json` + `HttpClient`, no Newtonsoft.
- Multi-targets `netstandard2.0` and `net8.0`; `IHttpClientFactory`-friendly.
- Public IDs only (`ten_`, `que_`, `cat_`, `pkg_`).
- HMAC and API-key auth handled invisibly.

## Hosts & environments

Queuey runs on **two** hosts — a control-plane **API** host and a publish **ingress** host — and
the SDK talks to both. `Environment` selects the defaults (defaults to `Production`):

| `QueueyEnvironment` | API host | Ingress host |
| --- | --- | --- |
| `Production` (default) | `https://api.queuey.ai` | `https://ingress.queuey.ai` |
| `Development` | `https://devapi.queuey.ai` | `https://devingress.queuey.ai` |

### Point at a local or self-hosted instance

To test against a locally running Queuey — or any self-hosted / dedicated instance — override
either host with `ApiBaseAddress` / `IngressBaseAddress`. Any absolute `http`/`https` URL works
(plain `http` on `localhost` included); a base path (e.g. behind a gateway prefix) is preserved.

```csharp
var queuey = new QueueyClient(new QueueyOptions
{
    // Local Queuey (default AppHost/project ports):
    ApiBaseAddress     = new Uri("http://localhost:5223"), // Queuey.Api
    IngressBaseAddress = new Uri("http://localhost:5084"), // Queuey.Ingress
    // …or a self-hosted instance: new Uri("https://queuey.acme.io")

    TenantPublicId = "ten_…",
    ApiKey         = "qak_…",
});
```

The `samples/Queuey.Sample.Console` project reads these from `QUEUEY_INGRESS_BASE` /
`QUEUEY_API_BASE` (plus `QUEUEY_TENANT` / `QUEUEY_API_KEY`), so you can run it straight against a
local backend:

```bash
QUEUEY_TENANT=ten_… QUEUEY_API_KEY=qak_… \
QUEUEY_INGRESS_BASE=http://localhost:5084 QUEUEY_API_BASE=http://localhost:5223 \
dotnet run --project samples/Queuey.Sample.Console
```

## Webhooks-as-a-Service: decorate, sync, publish (`Queuey.Client.Waas`)

Producers describe their event streams with an attribute, register the service, `SyncModels()` on
deploy, and `PushEvent(...)` on save. Streams reach partners through **packages** — a stream can
belong to several (e.g. tiers), and each declared package is created and assigned on sync.

```csharp
[QueueyModel("order-events",
    EventTypes = new[] { "order.created", "order.paid" },
    Packages   = new[] { "standard", "premium" })]   // stream is published in both tiers
public sealed class OrderCreated { public string OrderId { get; init; } = ""; }

builder.Services.AddQueuey(
    o => { o.ApiKey = cfg["Queuey:ApiKey"]; o.TenantPublicId = cfg["Queuey:TenantPublicId"];
           o.LicensePublicId = cfg["Queuey:LicensePublicId"]; },
    b => b.AddStream<OrderCreated>());

// on deploy: create/converge streams + packages + memberships
await queuey.SyncModelsAsync();          //  or:  queuey sync --assembly App.dll

// on save: publish
await queuey.PushEventAsync("order-events", "order.created", order.OrderId, order);
```

The CLI (`Queuey.Cli`) mirrors the same core: `queuey sync`, `queuey publish`, `queuey whoami`.

## License

MIT — see [LICENSE](LICENSE).
