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
| [`Queuey.Client`](#queueyclient) | Publishing events, and verifying the ones Queuey delivers back |
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

### Worked example: publishing from Edge

Edge is for producers where the network is not a given — a factory line, a kiosk, a vehicle. The
call returns once the event is durably on local disk; everything after that is Queuey's problem.

```csharp
builder.Services.AddQueueyEdge(o =>
{
    o.ApiKey = cfg["Queuey:ApiKey"];        // publish-only, workspace-scoped
    o.TenantPublicId = cfg["Queuey:Tenant"];
    // Local durable disk — never a network share (SQLite locking over SMB/NFS is unreliable)
    // and never container-ephemeral storage unless you accept process-lifetime durability.
    o.Storage.Path = "/var/lib/myapp/queuey/spool.db";
});

// One queue name, one event type, one key. Nothing about retries anywhere.
await _queuey.PublishAsync("temperature", reading, new PublishOptions
{
    EventType = "temperature.updated",   // what happened
    GroupKey  = reading.DeviceId,        // the lane: per device, in order
    IdempotencyKey = reading.SampleId,   // makes a resend a no-op, not a duplicate
});
```

**The queue name is a route, so treat it like one.** It becomes a URL segment
(`/events/{workspace}/{queue}`), so Queuey holds it to lowercase letters, digits, `.`, `-` and `_`.
Declare your queues in `queuey.deploy.json` and publish to those names; the SDK normalizes a name it
derives from a type (`OrderCreated` → `order-created`) but never rewrites one you wrote yourself,
because the string you publish to has to be the string that exists.

**Edge does not check that the queue exists**, on purpose. A mistyped name is accepted locally and
parks at transfer as `RequiresAction`, visible in `queuey edge status` — nothing is lost. Checking at
publish time would mean a network call on the one path that must work offline.

**`EventType` and `GroupKey` are the two fields worth always setting.** The type is what filtering,
the console and the issue assessors read; the group key is the lane. If your workspace declares
ingress sources (above), you can leave both off the call and let Queuey read them out of the payload
instead — useful when the same payload shape is published from several places.

**Idempotency is the cheap insurance.** Edge already deduplicates its own transfers permanently, so
`IdempotencyKey` is for *your* retries — a request handler that runs twice, a replayed job. Use a key
derived from the event, not a fresh GUID.

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

## Receiving deliveries: verify before you trust

Queuey signs every delivery it makes. Verifying that signature is what separates
"my endpoint is public" from "my endpoint accepts events from Queuey", so do it
before you look at the body.

```csharp
var verifier = new QueueyDeliveryVerifier(signingSecret);

app.MapPost("/webhooks/queuey", async (HttpRequest request) =>
{
    // Read the RAW bytes, before anything deserializes them.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();

    var result = verifier.Verify(
        request.Method,
        new Uri($"https://{request.Host}{request.Path}{request.QueryString}"),
        name => request.Headers[name],
        body);

    if (!result.IsValid)
    {
        logger.LogWarning("Rejected a delivery: {Reason}", result.Failure);
        return Results.Unauthorized();
    }

    // result.EventId is the value to be idempotent on — a redelivery after a
    // timeout carries the same id, and processing it twice is the failure mode
    // retries create.
    await Handle(body, result.EventId);
    return Results.Ok();
});
```

**Give it the raw bytes.** The signature covers a hash of exactly the bytes
Queuey sent. A body that has been deserialized and re-serialized is a different
byte sequence even when it is the same JSON, so a typed parameter like
`[FromBody] OrderEvent` breaks verification. Read the stream first, or take the
body as `byte[]` or `string`.

**What the signature covers:** the method, the path, the query string, the body,
and the signing headers Queuey generates. It does not cover your other request
headers, so never treat an unsigned header as vouched for.

The verifier also rejects a delivery whose timestamp sits more than five minutes
from your clock, which is what stops a captured request from being replayed
tomorrow. Closing the remaining window means remembering nonces; pass
`NonceAlreadySeen` in the options if you have somewhere to keep them.

Rotating keys, or accepting more than one signer? `QueueyDeliveryVerifier.ReadKeyId`
reads the claimed key id before verification, so you can pick the right secret.
It is a lookup hint and nothing more until `Verify` passes.

While you build, `queuey listen --forward-to http://localhost:5000/webhooks/queuey`
delivers real events to your machine over an outbound session, with no inbound
port open and no tunnel.

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
queuey apply --plan        # asks Queuey what would change and what it would refuse
queuey apply
```

`--plan` sends every write `apply` would make as a server-side dry run (`?dryRun=true`), so the answer
comes from Queuey: each value that would change, and each refusal — a retention cap, a queue limit, a
bad value — with what to do about it. Nothing is written, and it exits non-zero if anything would be
refused. A queue that does not exist yet shows as one that would be created.

A relative `url` appends to the workspace base, so moving hosts is one edit instead of N. An absolute
URL overrides outright. A queue with no `delivery` block inherits — the shape to reach for.

### Worked example: a workspace, set up once

Most of what a producer needs is decided at the workspace level, and every queue inherits it. This
is the shape to reach for — one place to change the host, one place to change the lane strategy, and
queues that own nothing but their own path:

```jsonc
{
  "workspace": {
    // Behaviour every queue inherits unless it says otherwise.
    "ordering": "bykey",          // lane by the group key below — order per customer, parallel across
    "retentionDays": 30,
    "dlqEnabled": true,

    // How Queuey reads events as they arrive. Say it once here and no producer has to send
    // X-Queuey-Event-Type or X-Queuey-Group-Key on every publish — which matters most for
    // producers you do not control, like a third party's webhook.
    "ingress": {
      "authMode": "ApiKey",                                   // demand a key at the edge
      "eventType": { "from": "body",  "name": "type" },       // {"type":"order.created", …}
      "groupKey":  { "from": "body",  "name": "customerId" }  // …and lane on this
    },

    // Where events go. Queues append their path to this.
    "delivery": {
      "baseUrl": "https://hooks.example.com",
      "authMode": "ApiKey",
      "credentialRef": "partner-key",     // a name; the secret lives in Queuey
      "authHeaderName": "X-Api-Key",
      "timeoutMs": 15000
    }
  },

  "queues": {
    // The common case: a route, nothing else. Delivers to
    // https://hooks.example.com/orders, laned by customerId, 30-day retention.
    "orders":   { "delivery": { "url": "/orders" } },
    "invoices": { "delivery": { "url": "/invoices" } },

    // Overrides are per field. This one is a firehose where order does not matter;
    // everything else still comes from the workspace.
    "analytics": { "ordering": "besteffort", "delivery": { "url": "/analytics" } },

    // No delivery block at all: this queue delivers to the workspace base itself.
    "audit": { }
  }
}
```

```bash
queuey credentials set --name partner-key --from-env PARTNER_KEY
queuey apply
```

**Why `bykey` needs the group key.** Partitioning needs something to partition on. Declare
`ordering: "bykey"` without a `groupKey` source and Queuey rejects it, because every event would be
unkeyed and "by key" would quietly behave as unordered. `apply` sends the ingress block before the
policy for exactly this reason.

**Where the type can come from.** `from` is `header`, `query`, or `body` — the top level of the JSON
you post. A Stripe-style sender that puts the type in the body needs no header at all; one that sends
`?event=order.created` uses `query`. Set it per queue instead of per workspace when one producer
speaks differently from the rest.

**Retention is capped by your plan.** Declaring more days than the plan allows fails the apply with
`retention_cap_exceeded` rather than being silently clamped — a shorter window is always accepted.

**Turning on `authMode` stops traffic that has no key.** Queuey defaults a new queue to `None` so the
first webhook works without ceremony. Roll the credential out to publishers first, then declare it.

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
converges staging and production. Every name is resolved before the first write, so one that is
missing fails the apply with nothing changed. Keep it
separate from `queuey.json`, which holds your API key and must *not* be committed.

Every omitted field means **leave alone**, everywhere: a file that names only a base URL changes only
the base URL. A misspelled field is rejected rather than ignored — a declarative file that reports
success while quietly skipping what you wrote is worse than one that fails.

### Delivering, retrying and filtering

```jsonc
{
  "workspace": {
    "maxAttempts": 8,                                   // every queue retries this many times…
    "backoff": { "baseDelayMs": 1000, "maxDelayMs": 300000, "jitter": "full" },
    "delivery": { "baseUrl": "https://hooks.example.com" }
  },
  "queues": {
    "orders": {
      "delivery": { "url": "/orders" },
      "dlqAfterAttempts": 5,                            // …and this one gives up to the DLQ sooner
      "filter": { "match": "any", "conditions": [
        { "field": "type", "op": "eq", "value": "order.created" },
        { "field": "priority", "op": "exists" } ] }
    },
    "audit": { "mode": "logOnly" }                      // stores events, delivers nothing
  }
}
```

**A queue this file creates delivers when it has a destination** — its own `delivery.url`, or the
workspace's `baseUrl` — and logs events until it has one. `mode` is `deliver` or `logOnly`; declare it
to own it. An existing queue keeps the mode it has unless the file declares one, and `apply` warns
when a queue has a destination but only logs. `"mode": "deliver"` on a queue with nowhere to deliver
fails that queue instead of pretending.

**Pausing is not a mode.** An operator pauses a queue in the console, and a deploy never resumes it:
`apply` reports a paused queue and leaves it paused.

**A filter decides what is delivered.** Events that do not match are kept as `Filtered` and never
sent. `op` is `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `contains` or `exists`, on a top-level field of
the JSON body. An empty `conditions` list delivers everything — that is how a file removes a filter.

### Prove it delivers: `queuey verify`

`apply` exiting 0 says the configuration landed. It does not say events arrive. `verify` publishes
one event and follows it:

```bash
queuey verify orders --data '{"type":"order.created","test":true}'
```

```text
✓ Delivered — orders (que_…) in ten_…, event evt_…
  Delivered to https://hooks.example.com/orders: 200 in 38 ms.
```

It exits 0 only when the receiver got the event. Otherwise the verdict — `logged_not_delivered`,
`filtered`, `failed` or `timeout` with `--json` — comes with what to change: the mode, the filter,
the credential the receiver rejected, held delivery, or the earlier event that holds a fifo queue. A
failure is reported on its first attempt rather than after every retry.

The event is real: the receiver gets it like any other, so send data it treats as harmless. `verify`
and `apply` pick the workspace by the same rule: the deployment file's `tenant` when it names one,
otherwise `--tenant`, `QUEUEY_TENANT` or `queuey.json`. When `--tenant` or `QUEUEY_TENANT` names
another workspace than the file, both commands fail and name the two, rather than guessing which
one you meant. The output names the workspace. `verify` needs a key that may publish and read
events. A deploy key can.

### The schema

`queuey schema` prints the JSON Schema for the deployment file: every field, the values it accepts
and what it does. Point `$schema` at it and your editor validates the file as you type:

```jsonc
{ "$schema": "https://raw.githubusercontent.com/Queuey-AI/queuey-client/main/schema/queuey.deploy.schema.json" }
```

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

# …or install it from NuGet as a global tool (invoked as `queuey`)
# --prerelease is required while every version is a preview
dotnet tool install --global Queuey.Cli --prerelease

# …or, with no .NET at all, take the self-contained binary for your platform
# (osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64) from the latest release
curl -fsSL https://github.com/Queuey-AI/queuey-client/releases/latest/download/queuey-osx-arm64.tar.gz | tar -xz

# …or from this repo
dotnet pack src/Queuey.Client.Cli -c Release
dotnet tool install --global --add-source src/Queuey.Client.Cli/bin/Release Queuey.Cli
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
| `apply --plan` | Ask Queuey what apply would change and refuse, as dry runs. Writes nothing |
| `apply --check` | Report drift and exit non-zero. Read-only — the CI gate |
| `verify <queue>` | Publish one event and follow it: delivered, or why not and what to change |
| `schema` | Print the deployment file's JSON Schema. No credentials, no network |
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
queuey apply --plan               # what would change, and would Queuey accept it?
queuey apply
queuey verify orders --data '{"type":"order.created","test":true}'   # did it arrive?
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

Every command resolves settings as **flag → environment variable → `queuey.json` → default**. The
one exception is the workspace of `apply` and `verify`: a deployment file that names a `tenant`
decides it, and a `--tenant` or `QUEUEY_TENANT` that names another one fails the command.

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
