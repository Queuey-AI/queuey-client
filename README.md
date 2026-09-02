# Queuey.Client

The official .NET SDK for [Queuey](https://queuey.ai) — a webhooks / event-distribution
platform. **Decorate, sync, publish.**

> Status: early preview — the API surface may still change, and nothing is on NuGet yet.

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

**Four packages, four jobs.** Take only the ones you need:

| | For |
| --- | --- |
| [`Queuey.Client`](#queueyclient) | Publishing events. Auth and transport, nothing else |
| [`Queuey.Edge`](#durable-local-publishing-queueyedge) | Publishing from somewhere the network is unreliable |
| [`Queuey.Client.Waas`](#queues-declare-where-your-events-land) | Declaring queues, streams and delivery in code |
| [`Queuey.Cli`](#cli-queuey) | Deploys, local webhook debugging, operating an Edge spool |

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
[QueueyQueue("orders", Ordering = "bykey", RetentionDays = 30)]
public sealed class OrderQueue { }

builder.Services.AddQueuey(
    o => { /* credentials */ },
    b => b.AddQueue<OrderQueue>());

// on deploy — pick one:
await queuey.SyncQueuesAsync();   // just the queues
await queuey.SyncAsync();         // queues, then the streams above

// …or from the deploy pipeline:  queuey queue sync --assembly App.dll
```

Every policy field you leave off **inherits from the workspace** — declaring a field is how the code
takes ownership of it. The attribute covers behaviour only: `Ordering`, `DlqEnabled`, `RetentionDays`, `Idempotent`.
There is no attempt count, on purpose — Queuey decides retry-versus-DLQ by classifying the failure,
not by counting: a receiver that rejected the event goes straight to the dead-letter queue, and a
target that is down is held and probed until it recovers. The destination — endpoint URL, outbound auth,
signing — is deliberately not declarable here. It differs per environment and carries secrets, so it
belongs in configuration, not in a type that ships in your assembly.

```bash
queuey queue plan --assembly App.dll     # network-free, no credentials needed
```

A queue that has nowhere to deliver yet is reported as a **warning, not a failure**: the state you
declared did land, the workspace just isn't wired up. Such a queue starts in log-only mode, so it
accepts events and records them without delivering — worth knowing before you point production at it.

## Delivery as code (`queuey.deploy.json`)

Where a queue delivers, and how it authenticates, differs per environment and carries secrets — so
it lives in a file, not in an attribute. The **workspace** owns the destination; each queue owns only
its path:

```jsonc
{
  "workspace": {
    "baseUrl": "https://hooks.example.com",
    "authMode": "ApiKey",
    "credentialRef": "partner-key",     // a name, never the secret
    "authHeaderName": "X-Api-Key"
  },
  "queues": {
    "orders":   { "ordering": "bykey", "retentionDays": 30, "delivery": { "url": "/orders" } },
    "invoices": { "retentionDays": 30 }   // no delivery block — inherits the workspace
  }
}
```

```bash
queuey credentials set --name partner-key --from-env PARTNER_KEY
queuey apply --dry-run     # validates locally, sends nothing
queuey apply
```

A relative `url` appends to the workspace base, so moving hosts is one edit instead of N. An absolute
URL overrides outright. A queue with no `delivery` block inherits — the shape to reach for.

Already configured things in the console? Pull it instead of retyping it:

```bash
queuey pull --stdout      # review first
queuey pull               # writes queuey.deploy.json
```

Pull is **inherit-aware** — a queue that inherits a section writes nothing for it, so the file says
what you actually own rather than freezing today's defaults as permanent per-queue overrides — and
it cannot emit a secret, because Queuey's read surfaces never return one.

**This file carries no secrets and is meant to be committed.** `credentialRef` names a credential
stored encrypted by `queuey credentials set`, which reads the value from an environment variable
(never an argument — those land in shell history and CI logs) and can never read it back. The
reference is the credential's *name*, resolved per workspace at apply time, so the same file
converges staging and production. Keep it
separate from `queuey.json`, which holds your API key and must *not* be committed.

Every omitted field means **leave alone**, everywhere: a file that names only a base URL changes only
the base URL. A misspelled field is rejected rather than ignored — a declarative file that reports
success while quietly skipping what you wrote is worse than one that fails.

### One file, every environment

Most of a deployment file is already portable — a queue that owns only `/orders` says the same thing
everywhere. For the few values that aren't, `${VAR}` is expanded from the environment at apply time:

```bash
queuey pull --as staging          # rewrites the non-portable values into ${VAR} references
queuey apply --check              # CI gate: writes nothing, exits non-zero on drift
```

An unset variable is an **error**, never an empty string — expanding to nothing would quietly give
you a base URL of `https://` and a deploy that "succeeded" while pointing at nowhere. Use
`${VAR:-default}` when a default is genuinely intended.

`--check` compares only what the file declares. A workspace holding settings your file is silent
about is inheritance working as designed, not drift — so you can put as much or as little under code
as you want.

Adopting a workspace that was configured before anyone wrote it down? `queuey pull --emit-code
Queues.cs` generates the `[QueueyQueue]` declarations. Behaviour only: destinations stay in the
deployment file, where they belong.

### Publishing with HMAC instead of an API key

```bash
queuey keys mint --queue que_...
```

The secret is shown once. This needs a credential with key-management rights — a deploy key
deliberately has none, since a key that can mint keys turns pipeline access into account access.

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

All commands share the same configuration resolution and exit codes, so they compose in a pipeline.
Run `queuey` with no arguments — or `--help` after any command — for the full usage text, which
carries the per-flag detail this table leaves out.

### Commands

**Declare and converge** — what a deploy runs:

| Command | What it does |
| --- | --- |
| `apply` | Converge a workspace from `queuey.deploy.json` — the deploy verb |
| `apply --dry-run` | Validate the file locally. No credentials, no network, nothing sent |
| `apply --check` | Report drift and exit non-zero. Read-only — the CI gate |
| `pull` | Read a workspace back into a deployment file (the inverse of `apply`) |
| `queue plan` | Preview the `[QueueyQueue]` declarations in an assembly (network-free) |
| `queue sync` | Apply those declarations |
| `sync` | Apply every `[QueueyModel]` stream in an assembly (WaaS producers) |

**Secrets** — one-off, from an admin credential:

| Command | What it does |
| --- | --- |
| `credentials set` | Store a delivery secret under a name a deployment file can refer to |
| `credentials list` | List stored credentials — names and types, never values |
| `keys mint` | Mint an ingress signing key so a producer can publish with HMAC |

**Operate and inspect:**

| Command | What it does |
| --- | --- |
| `publish` | Publish an event to a queue |
| `listen` | Receive deliveries on your machine over an outbound session |
| `replay <evt_…>` | Re-send one existing event to your listener (read-only) |
| `metrics <que_…>` | A queue's traffic snapshot |
| `issues <ten_…>` | A workspace's issues |
| `edge` | Operate an Edge spool: `run`, `publish`, `status`, `drain`, `retry`, `discard`, `recover`, `reset` |
| `create-tenant` / `create-queue` | Provision imperatively (prefer `apply`) |
| `whoami` | The resolved host, environment, tenant and license (key masked) |

### Exit codes

A stable contract, so CI can branch on them:

| Code | Meaning |
| --- | --- |
| `0` | Success — and for `--check`, no drift |
| `1` | The run did not fully converge, or `--check` found drift |
| `2` | Bad arguments |
| `3` | Missing or invalid credentials / configuration (including an unset `${VAR}`) |
| `4` | The target assembly could not be loaded |

### Recipes

**First deploy of a new service**

```bash
queuey credentials set --name partner-key --from-env PARTNER_KEY
queuey apply --dry-run            # catch typos with no credentials and no network
queuey apply
```

**Adopt a workspace someone configured in the console**

```bash
queuey pull --stdout                       # look before you write
queuey pull                                # writes queuey.deploy.json
queuey pull --emit-code Queues.cs          # …and the [QueueyQueue] declarations
```

**One file, many environments**

```bash
queuey pull --as staging --file staging.deploy.json
# tells you which ${VAR}s it introduced; set them and apply anywhere
QUEUEY_BASE_URL=https://staging.example.com queuey apply --file staging.deploy.json
```

**In CI**

```bash
queuey apply --check || echo "queuey.deploy.json has drifted — review before merge"
```

### Tips, and the traps worth knowing

**Two config files, and only one of them is committed.** `queuey.json` holds your API key and stays
out of git (it already is in `.gitignore`). `queuey.deploy.json` holds no secrets by construction —
auth and signing name a `credentialRef`, never a value — and is meant to be reviewed in pull
requests. Never merge them.

**A credential reference is a name, not an id.** Queuey stores a `cred_…` id, and those are minted
per workspace — a file carrying one would only ever apply where it was written. The file uses the
name you gave `credentials set`, and the deploy resolves it per workspace.

**Omitting a field means "leave it alone" — everywhere.** In the deployment file, in the policy
patch, in the delivery patch. A file naming only a base URL changes only the base URL. That is what
lets you put as much or as little under code as you want.

**`--check` only compares what you declared.** A workspace holding settings your file is silent
about is inheritance working, not drift. The gate stays usable even if you never put a single policy
field in the file.

**Applying is idempotent, and non-destructive.** Re-running changes nothing, and `apply` never
touches a queue's run mode, delivery config or flow levers. A deploy cannot silently reopen a queue
someone stopped.

**An unset `${VAR}` is an error, not an empty string.** Expanding to nothing would give you a base
URL of `https://` and a deploy that "succeeded" while pointing at nowhere. Use `${VAR:-default}` when
you actually mean a default.

**A misspelled field is rejected, not ignored.** A declarative file that reports success while
quietly skipping what you wrote is worse than one that fails.

**Prefer a relative `url` on a queue.** It appends to the workspace base, so moving hosts is one edit
instead of N — and relative paths travel between environments untouched. An absolute URL pins a host.

**A queue with nowhere to deliver is a warning, not a failure — and it swallows events.** A new
queue starts in log-only mode: it accepts events and records them without delivering. `apply` says so
out loud; take it seriously before pointing production at it.

**`ordering: "bykey"` wires up its own partition key.** Partitioning needs a key, and Queuey rejects
by-key ordering without one. Declaring it means "lane by the key I send", so the deploy configures
exactly that rather than bouncing you with a policy error.

**A deploy key cannot mint keys.** `keys mint` needs a credential with key-management rights, on
purpose: a pipeline key that could hand out credentials would turn repo access into account access.
Mint once from an admin credential and put the result in your secret store.

**`credentials set` reads the secret from the environment, never an argument.** Arguments land in
shell history and CI logs.

**`pull` won't overwrite.** Use `--stdout` and diff it first — replacing a committed declaration is
how an intentional, not-yet-applied edit disappears.

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
locally-running instance.

Two files, and the difference matters:

| File | Holds | Commit it? |
| --- | --- | --- |
| `queuey.json` | `apiBase` / `apiKey` / `tenant` / `license` — so you don't repeat flags | **No.** It holds your key, and it is already in `.gitignore` |
| `queuey.deploy.json` | What your workspace and queues should look like | **Yes.** It carries no secrets by construction |

## License

MIT — see [LICENSE](LICENSE).
