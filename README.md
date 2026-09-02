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

## Durable local publishing (`Queuey.Edge`)

For producers on unreliable networks — factories, kiosks, vehicles, on-prem
services — `Queuey.Edge` makes one call transfer the operational delivery
problem to Queuey:

```csharp
builder.Services.AddQueueyEdge(o =>
{
    o.ApiKey = "qak_…";           // publish-only, tenant-scoped Edge key
    o.TenantPublicId = "ten_…";
});

// The whole delivery story in application code:
await queuey.PublishAsync("temperature.updated", payload);
```

When that call returns, the event is committed to a **local durable store**
(SQLite, fsync'd) under a permanent transfer identity, and Queuey owns
everything after: transfer to Queuey Cloud, retries, backoff, reconnect,
lost-ACK resolution, idempotent resend, recovery across process/machine
restarts, backlog draining. Kill the network for two days — events accumulate
durably and drain in order when it returns, with honest `occurred_at`
history. No buffering, retry or reconnect code ever appears in your
application.

See [`src/Queuey.Edge/README.md`](src/Queuey.Edge/README.md) for the precise
contract, [`samples/Queuey.Edge.Demo`](samples/Queuey.Edge.Demo) for a
runnable kill-Queuey-and-watch-it-recover demo, and
[`docs/edge-operations.md`](docs/edge-operations.md) for operations (spool
sizing, the one metric worth alerting on, the StorageFaulted runbook). The
CLI gains `queuey edge status | retry | discard | recover | reset` for
operating a spool alongside a running host.

## Hosts & environments

Queuey runs on **two** hosts — a control-plane **API** host and a publish **ingress** host — and
the SDK talks to both. `Environment` selects the defaults (defaults to `Production`):

| `QueueyEnvironment` | API host | Ingress host |
| --- | --- | --- |
| `Production` (default) | `https://api.queuey.ai` | `https://ingress.queuey.ai` |

### Point at a local instance

To test against a locally running Queuey, override either host with `ApiBaseAddress` /
`IngressBaseAddress`. Any absolute `http`/`https` URL works (plain `http` on `localhost` included).

```csharp
var queuey = new QueueyClient(new QueueyOptions
{
    // Local Queuey (default AppHost/project ports):
    ApiBaseAddress     = new Uri("http://localhost:5223"), // Queuey.Api
    IngressBaseAddress = new Uri("http://localhost:5084"), // Queuey.Ingress

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

Producers describe their event streams with an attribute, register the service, `SyncStreams()` on
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
await queuey.SyncStreamsAsync();          //  or:  queuey sync --assembly App.dll

// on save: publish
await queuey.PushEventAsync("order-events", "order.created", order.OrderId, order);
```

### Names, and what "synced" guarantees

Stream names follow Queuey's queue-name rules — lowercase, starting with a letter or digit, then
letters, digits, `.`, `-` or `_`. A name you write is validated as written; leave it off and it is
derived from the type (`OrderCreated` → `order-created`). The check runs **once, as `AddQueuey`
builds the registry** — before any host is built and without touching the network — so a bad name is
a startup error with the corrected name in it, not a server error mid-deploy.

A sync is not a transaction (each stream is its own `PUT`), so it is built to never look like one it
isn't: it stops at the first failure, throws, and reports the streams it never attempted. Applying is
idempotent, so a fixed re-run converges. Pass `ContinueOnError` when you want the full damage report
in one go — it still throws at the end.

```csharp
// abort startup if the streams can't be converged (dev / single-instance services)
b => b.AddStream<OrderCreated>().SyncOnStartup();
```

`SyncOnStartup` is off by default: app instances often hold a publish-only key, and a rolling deploy
would have every replica applying the same streams at once. A deploy step (`queuey sync --assembly`)
is the better home for it in production.

## Queues: declare where your events land

A **queue** is the pipeline you publish into and Queuey delivers from. Declare one when you deliver
to your own endpoint; declare a **stream** (above) when integration partners subscribe. A stream has
a queue underneath it, so a full sync applies queues first.

```csharp
[QueueyQueue("orders", Ordering = "bykey", MaxAttempts = 8)]
public sealed class OrderQueue { }

builder.Services.AddQueuey(
    o => { /* credentials */ },
    b => b.AddQueue<OrderQueue>());

await queuey.SyncQueuesAsync();     //  or:  queuey queue sync --assembly App.dll
await queuey.SyncAsync();           //  queues, then streams
```

Every policy field you leave off **inherits from the workspace** — declaring a field is how the code
takes ownership of it. The attribute covers behaviour only: `Ordering`, `MaxAttempts`, `DlqEnabled`,
`DlqAfterAttempts`, `RetentionDays`, `Idempotent`. The destination — endpoint URL, outbound auth,
signing — is deliberately not declarable here. It differs per environment and carries secrets, so it
belongs in configuration, not in a type that ships in your assembly.

```bash
queuey queue plan --assembly App.dll     # network-free, no credentials needed
```

A queue that has nowhere to deliver yet is reported as a **warning, not a failure**: the state you
declared did land, the workspace just isn't wired up. Such a queue starts in log-only mode, so it
accepts events and records them without delivering — worth knowing before you point production at it.

## CLI (`queuey`)

The `Queuey.Cli` tool mirrors the SDK and adds local-debugging verbs. Run it from source, or install
it once as a global .NET tool and call `queuey`:

```bash
# from source (this repo)
dotnet run --project src/Queuey.Client.Cli -- <command> [options]

# …or install as a global tool (invoked as `queuey`)
dotnet pack src/Queuey.Client.Cli -c Release
dotnet tool install --global --add-source src/Queuey.Client.Cli/bin/Release Queuey.Cli
# (once it's on NuGet:  dotnet tool install --global Queuey.Cli)
```

| Command | What it does |
| --- | --- |
| `sync` | Apply every `[QueueyModel]` stream found in an assembly |
| `publish` | Publish an event to a stream |
| `create-tenant` / `create-queue` | Provision a tenant / queue |
| `metrics <que_…>` | A queue's traffic snapshot |
| `issues <ten_…>` | List a tenant's issues |
| **`listen`** | Receive webhooks locally over a secure push session |
| **`replay <event-id>`** | Replay one existing event to your listener (read-only) |
| `whoami` | Resolved host / env / tenant / license (key masked) |

Run `queuey` with no args for full usage.

### `queuey listen` — receive webhooks on your machine

Opens an **outbound** authenticated push session (no inbound port exposed) and forwards each
delivered event to a local URL — Stripe-`listen` style. Scope it to one queue (`--queue`) or a whole
tenant (`--tenant`, where one listener covers every queue under it):

```bash
# receive this queue's deliveries on your machine and forward them locally — Ctrl-C to stop
queuey listen \
  --api-key qak_… \
  --queue que_… \
  --forward-to http://localhost:5094/webhook
# targets production by default; add --api-base http://localhost:5223 to point at a local instance
```

**Two forwarding modes:**

- **Path fidelity (default)** — replays the request faithfully (same method, path, query, headers,
  body), appending the original delivery path onto `--forward-to`. Use it to replay a webhook to a
  local server that expects the same route.
- **`--forward-exact`** — posts to `--forward-to` **verbatim**, ignoring the original path. Use it to
  bridge deliveries into a **fixed** local endpoint — e.g. piping a queue straight into a local
  Queuey ingress route:

  ```bash
  queuey listen --api-key qak_… \
    --queue que_… \
    --forward-to http://localhost:5084/events/ten_…/first.queue \
    --forward-exact
  ```

`--tee` also delivers to the real endpoint (default is redirect — only you receive it); a tenant-wide
redirect asks to confirm (`--yes` to skip). The API key needs **`queue.read`** on the queue/tenant (a
FullAccess or ProducerAdmin key — an ingress-only publish key can't listen).

### `queuey replay` — re-send one event to your listener

Read-only DLQ debugging: forwards an existing event (including a DLQ'd one) to your connected
`queuey listen` session. The event is **not** modified and the real endpoint is never contacted.

```bash
# terminal 1 — start a listener
queuey listen --api-key qak_… --queue que_… --forward-to http://localhost:5094/webhook
# terminal 2 — replay an event to it
queuey replay evt_… --api-key qak_… --queue que_…
```

### Configuration

Every command resolves settings as **flag → environment variable → `queuey.json` → default**:

| Setting | Flag | Env var |
| --- | --- | --- |
| API host | `--api-base <uri>` | `QUEUEY_API_BASE` |
| Ingress host | `--ingress-base <uri>` | `QUEUEY_INGRESS_BASE` |
| API key | `--api-key qak_…` | `QUEUEY_API_KEY` |
| Tenant | `--tenant ten_…` | `QUEUEY_TENANT` |
| License | `--license lic_…` | `QUEUEY_LICENSE` |

The CLI targets production (`https://api.queuey.ai`) by default; pass `--api-base <uri>` to point at a
locally-running instance. A `queuey.json` can hold `apiBase` / `apiKey` / `tenant` / `license` so you
don't repeat flags — but **keep it out of git** (it holds your key; it's already in `.gitignore`).

## License

MIT — see [LICENSE](LICENSE).
