# Queuey.Client — Examples

An interactive console app that exercises the **Queuey .NET SDK** end to end: onboard a tenant, sync
event streams and packages, publish events, enable an integration partner, and read metrics/issues.
Use it as a copy-paste reference for your own producer integration.

## What it shows

| # | Example | SDK calls |
|---|---------|-----------|
| 1 | **Onboarding** | `Management.CreateTenantAsync`, `CreateQueueAsync` |
| 2 | **Sync models** | `Plan()`, `SyncModelsAsync` → streams + packages (N:N) |
| 3 | **Publish** | `PushEventAsync(stream, eventType, key, data)` |
| 4 | **Packages** | `ApplyPackageAsync`, `UpdatePackageAsync`, `AssignStreamToPackageAsync`, `ArchivePackageAsync` |
| 5 | **Integrations** | `Integrations.InviteAsync` / `GrantPackageAsync` / `ActivateAsync` |
| 6 | **Observability** | `Management.GetQueueMetricsSnapshotAsync`, `ListIssuesAsync` |
| 7 | **Full walkthrough** | 2 → 3 → 5 → 6 in sequence |

The core pattern — **decorate, sync, publish** — lives in [`DemoModel.cs`](DemoModel.cs) and
[`Program.cs`](Program.cs); each example is a short method in [`Examples.cs`](Examples.cs).

## Prerequisites

- .NET 8 or 9 SDK.
- A running Queuey backend — the **API** host (control plane) and the **Ingress** host (publish).
  Locally that's `Queuey.Api` (`http://localhost:5223`) and `Queuey.Ingress` (`http://localhost:5084`).
- A **tenant** and a **license-wide FullAccess API key** (`qak_…`) + your **license id** (`lic_…`).

## Configure

Config is read from environment variables (with local-dev defaults). Set the ones you need:

| Variable | Default | Meaning |
|----------|---------|---------|
| `QUEUEY_API_BASE` | `http://localhost:5223` | Control-plane (API) host |
| `QUEUEY_INGRESS_BASE` | `http://localhost:5084` | Publish (ingress) host |
| `QUEUEY_TENANT` | `ten_your_tenant` | Producer tenant public id |
| `QUEUEY_LICENSE` | `lic_your_license` | License public id (for control-plane calls) |
| `QUEUEY_API_KEY` | `qak_your.key` | License-wide FullAccess API key |

## Run

```bash
# Interactive menu:
QUEUEY_TENANT=ten_… QUEUEY_LICENSE=lic_… QUEUEY_API_KEY=qak_… \
dotnet run --project samples/Queuey.Examples.Console

# One-shot (handy for a quick demo / CI) — run example N and exit:
dotnet run --project samples/Queuey.Examples.Console -- 7
```

Against production instead of localhost, add `QUEUEY_API_BASE`/`QUEUEY_INGRESS_BASE`
(e.g. `https://api.queuey.ai` / `https://ingress.queuey.ai`).

## The WaaS model in 30 seconds

- A **stream** is a producer event endpoint. `[QueueyModel("order-events", …)]` + `SyncModels()` creates
  it and configures its queue to extract `eventType` + group `key` from the canonical headers.
- A **package** groups streams and is the unit of partner access. A stream can be in several packages
  (e.g. tiers). Sync creates the declared packages and assigns the stream to each.
- An **integration** is a partner you distribute to. Delivery needs **two gates**: the integration is
  **granted** a package containing the stream **and** there's an **active activation** for the group key
  — plus the integration's own subscription.

> Note: "integration partner" (this SDK) is distinct from Queuey's platform-level *partner relations*
> (cross-organization), which is out of scope here.

## Use it in your own project

```bash
dotnet add package Queuey.Client.Waas
```

```csharp
[QueueyModel("order-events", EventTypes = new[] { "order.created" }, Packages = new[] { "standard" })]
public sealed class OrderCreated { public string OrderId { get; init; } = ""; }

builder.Services.AddQueuey(
    o => { o.ApiKey = cfg["Queuey:ApiKey"]; o.TenantPublicId = cfg["Queuey:TenantPublicId"];
           o.LicensePublicId = cfg["Queuey:LicensePublicId"]; },
    b => b.AddStream<OrderCreated>());

// deploy: await queuey.SyncModelsAsync();
// save:   await queuey.PushEventAsync("order-events", "order.created", order.OrderId, order);
```

There's also a CLI (`Queuey.Cli`): `queuey sync / publish / whoami / create-tenant / create-queue / metrics / issues`.

See the [SDK README](../../README.md) for the full surface.
