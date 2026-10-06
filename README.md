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

While you build, `queuey listen --queue <name> --forward-to http://localhost:5000`
delivers real events to your machine, at the path of the queue's endpoint, over an
outbound session, with no inbound port open and no tunnel.

## Delivery as code (`queuey.deploy.json`)

Where a queue delivers, and how it authenticates, differs per environment and carries secrets — so
it lives in a file, not in an attribute. The **workspace** owns the destination; each queue owns only
its path:

```jsonc
{
  "workspace": {
    "delivery": {
      "baseUrl": "https://hooks.example.com",
      "authMode": "ApiKey",
      "credentialRef": "partner-key",   // a name, never the secret
      "authHeaderName": "X-Api-Key"
    }
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
queuey plan                # asks Queuey what would change and what it would refuse
queuey apply
```

`queuey plan` sends every write `apply` would make as a server-side dry run (`?dryRun=true`), so the
answer comes from Queuey: each value that would change, and each refusal — a retention cap, a queue
limit, a bad value — with what to do about it. Each write is asked about on its own, against what is
stored now, so a refusal that depends on a workspace change in the same file shows only in `apply`.
Nothing is written, and it exits non-zero if anything would be refused. A queue that does not exist
yet shows as one that would be created, and its settings are checked locally. The first dry
run also proves that Queuey answers dry runs; against an API that does not, the plan stops there and
says what that one call may have changed — nothing, when a declared queue already exists. It is a verb of
its own rather than an `apply` flag, so a CLI too old to know it answers "Unknown command" instead of
running the apply you meant to plan. For the same reason every command rejects an option it does not
take — a typo like `--paln` fails with exit 2 and the options that command accepts. `queuey plan --json`
prints the plan as an object a script can read: `schemaVersion` (1, so check it first), `file`, `tenant`,
`planId`, `planHash`, `wouldSucceed`, `changeCount`, `queues`, and `steps`, each with its `target`, `aspect`,
`creates`, `changes`, `notes`, `state`, `desired` and `error`. A change's `from` and `to` are the values as
Queuey's config reads them back, so a number, a boolean or an object stays JSON. With `--json`, before or
after the command, every error is JSON too.

Each entry in `queues` has the queue's `name`, its `publicId` (null for one apply would create) and its
`ingressUrl`, where producers publish to it, so a provider can be pointed at a queue in the same change that
creates it. `planHash` is `sha256:` over what apply would change and the server state it rests on: each
step's `state` is the hash Queuey gave the config its dry run started from, and `desired` is what apply
would send to a queue it creates. The order of the queues, the file's formatting and the plan's notes do
not count, so the same file against the same state gives the same hash, and `planId` is `plan_` and the
start of it. When the state moves, or the file changes what apply does, the hash changes.

`queuey apply --dry-run --json` prints what the file declares, checked locally, for a script or an
agent to read:

```json
{
  "schemaVersion": 2,
  "workspace": { "environment": "staging", "policy": { … }, "delivery": { … }, "ingress": null, "notes": [] },
  "queues": [ { "name": "orders", "mode": "deliver", "policy": { … }, "delivery": null, "kind": null, "ingress": null, "notes": [] } ]
}
```

`workspace` is null when the file declares none, and `notes` says what a dry run can tell without
asking Queuey, such as a wait above its ceiling. Each declaration carries every field the file can set
on the workspace or the queue, in the file's words, grouped as Queuey's config reads them back: the
workspace's `environment`, behaviour under `policy`, then `delivery` and `ingress` (where `eventType` is
`{ "from", "name" }` and `signedRequest` is `{ "template", "credentialRef" }`), and a queue's delivery `kind`
on its own. A
field the file leaves out is null, and a `${VAR}` is shown as written, not expanded. Check
`schemaVersion` first: version 1, a bare array of queues, is what 0.1.0-preview.8 printed.

A relative `url` appends to the workspace base, so moving hosts is one edit instead of N. An absolute
URL overrides outright. A queue with no `delivery` block inherits — the shape to reach for.

### Worked example: a workspace, set up once

Most of what a producer needs is decided at the workspace level, and every queue inherits it. This
is the shape to reach for — one place to change the host, one place to change the lane strategy, and
queues that own nothing but their own path:

```jsonc
{
  "workspace": {
    // What this workspace is: dev, test, staging or prod. Without it, Queuey treats it as prod.
    "environment": "staging",

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
speaks differently from the rest. A source needs its `name`, and its `from` whenever the name is not
empty: `{ "name": "" }` removes the source, and a source that leaves out either is refused before
anything is sent, rather than read as a header or as a removal.

**Only a person lowers the environment.** `environment` is `dev`, `test`, `staging` or `prod`, lowest
first, and a workspace without one counts as `prod`. `apply` writes it before anything else. A key may
raise it towards `prod`, but a file that names a lower environment than the workspace has, including
anything but `prod` on a workspace that has none, is refused with `environment_lowering_needs_a_person`
before anything else is written, and the error says where a person changes it in the Queuey console.
A file applied to several workspaces takes it from a variable: `"environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}"`,
which `pull --as` writes for you.

**Every workspace that exists today has no environment, so it counts as `prod`.** Adding `"environment": "dev"`
(or `test`, or `staging`) to the file of an existing workspace therefore stops `apply` in CI until a person
sets that environment once: open the workspace in the Queuey console, choose **Set environment…** in its
⋯ menu, and confirm the lower environment. From then on the file and the workspace agree, and `apply`
goes through. Setting `"environment": "prod"` needs no one, because it lowers nothing.

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
    // every queue waits like this between attempts…
    "backoff": { "baseDelayMs": 1000, "maxDelayMs": 300000, "jitter": "full" },
    "delivery": { "baseUrl": "https://hooks.example.com" }
  },
  "queues": {
    "orders": {
      "delivery": { "url": "/orders" },
      "backoff": { "maxDelayMs": 60000 },               // …and this one never waits more than a minute
      "filter": { "match": "any", "conditions": [
        { "field": "type", "op": "eq", "value": "order.created" },
        { "field": "priority", "op": "exists" } ] }
    },
    "audit": { "mode": "logOnly" }                      // stores events, delivers nothing
  }
}
```

**The number of attempts is not a setting.** Queuey makes the same number of attempts for every event
and decides what a failure needs: a transient failure is retried, with the backoff you declare, until
the receiver's probe takes over, and an event the receiver rejects goes to the dead-letter queue. With
the dead-letter queue off, that event locks its queue instead, or holds just its key on a `bykey`
queue, until a person acts. A file that declares `maxAttempts` or `dlqAfterAttempts` is refused before
anything is sent, naming every place it does, and `pull` never writes them.

**A backoff waits at most an hour at first and a day at most.** `baseDelayMs` is at most 3600000 and
`maxDelayMs` at most 86400000, both above 0. A longer wait that is already in place stays: Queuey
refuses only a write that changes it. `apply` checks this before it writes anything, against the wait
each declaration would replace: a queue's own, or for a new queue and a queue that inherits its wait,
the workspace's once this apply has written the workspace. When a queue's config cannot tell whether
the queue owns its wait, `apply` reads what the queue stores; if that read is refused, Queuey decides
when it writes the queue. `baseDelayMs` above `maxDelayMs` is refused up front when one backoff declares
both; when one of them comes from the workspace, Queuey checks it when the queue is written.
`queuey plan` asks Queuey about each write on its own, against what is stored now, so a refusal that
depends on the workspace this apply changes, or on a queue it would create, shows only in `apply`.

**A queue this file creates delivers when it has a destination** — its own `delivery.url`, or the
workspace's `baseUrl` — and logs events until it has one. `mode` is `deliver` or `logOnly`; declare it
to own it. An existing queue keeps the mode it has unless the file declares one, and `apply` warns
when a queue has a destination but only logs. `"mode": "deliver"` on a queue with nowhere to deliver
fails that queue instead of pretending.

**Pausing is not a mode.** An operator pauses a queue in the console, and a deploy never resumes it:
`apply` reports a paused queue and leaves it paused.

**A filter decides what is delivered.** Events that do not match are kept as `Filtered` and never
sent. `op` is `eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `contains` or `exists`, on a top-level field of
the JSON body. A filter needs its `conditions`. An empty list delivers everything, which is how a file
removes a filter, so a filter without the list is refused rather than read as empty; leave `filter`
out to keep the queue's filter as it is. `gt`, `gte`, `lt` and `lte` compare numbers, so their value
is a plain number like `1.5`: no comma decimals, thousands separators, currency or parentheses.
`exists` takes no value, and a field is looked up exactly as written, so whitespace around it is
refused rather than trimmed. A condition Queuey stored before it checked these is pulled as it is,
with a warning, and `apply` refuses the file until it is fixed.

### A provider's webhook, end to end

A provider such as Stripe signs every webhook. The queue's ingress verifies that signature, and in a
development workspace its events go to your machine through a local listener rather than to a URL:

```jsonc
{
  "workspace": {
    "environment": "dev",
    "delivery": { "baseUrl": "https://api.example.com" }   // where queues deliver over HTTP; a path appends to it
  },
  "queues": {
    "stripe": {
      "ingress": {
        "authMode": "SignedRequest",
        // Verify Stripe's signature with the secret stored as stripe-whsec. A name, never the secret.
        "signedRequest": { "template": "stripe", "credentialRef": "stripe-whsec" }
      },
      "delivery": {
        "url": "/api/stripe",          // the path, which the listener forwards to on your machine
        "kind": "localForward",        // to a connected `queuey listen` session, not over HTTP
        "signing": { "enabled": true, "templateKey": "stripe" }   // keep Stripe's signature valid for constructEvent
      }
    }
  }
}
```

```bash
queuey plan                    # shows the queue's ingress URL before it exists
queuey apply                   # creates the queue; its ingress refuses every event until stripe-whsec is stored
stripe listen --forward-to <ingress URL>        # prints a test signing secret, whsec_…
STRIPE_WHSEC=whsec_… queuey credentials set --name stripe-whsec --type HmacSigning --key-id stripe-whsec --from-env STRIPE_WHSEC
queuey apply                   # points the ingress at the stored secret
queuey listen --queue stripe --forward-to http://localhost:5000
```

**A credential that is not stored yet is accepted.** `apply` keeps the name the ingress waits for, and
the ingress refuses every event until a credential by that name is stored and `apply` runs again, which
points the ingress at it. The plan, `apply` and Queuey's setup review all say so, with the command that
stores it, and `apply --check` reports drift once it is stored. `template` is one of Queuey's signed-request
templates; `queuey` is Queuey's own scheme, which verifies with the sending API client's signing key and
takes no `credentialRef`. A `signedRequest` is checked while `authMode` is `SignedRequest` or
`ApiKeyAndSignedRequest`, so a file that declares one with `None` or `ApiKey` beside it is refused.
Since the name shows up in the commands Queuey suggests, `credentialRef` uses only letters, digits and
`. _ : @ / -`, starting with a letter or digit; a stored credential whose name has other characters is
named by its `cred_…` id. A value that looks like a secret (a known prefix such as `whsec_`, hex, a UUID,
base64) is refused, and never repeated.

**`kind` is where a queue's events go:** `http`, to its URL, or `localForward`, to a connected
`queuey listen` session. While no session is connected, the events wait. The address a listener forwards to
belongs to its session (`--forward-to`) and never to the file, and the queue keeps its URL for when it
delivers over HTTP again. Omit `kind` to leave it as it is. A file applied to several workspaces takes it
from a variable, such as `"kind": "${QUEUEY_STRIPE_DELIVERY_KIND}"`, set to `localForward` in development
and `http` elsewhere; `pull --as` writes that variable for you. Routing a queue to a listener needs
`queue.listen` on it.

**A delivery URL on your machine is refused.** Queuey's delivery never reaches `localhost`, `*.localhost`,
a loopback address or a private one, so `plan`, `apply` and `apply --dry-run` refuse such a `url` or
`baseUrl` before anything is sent, and point to `"kind": "localForward"` instead. Queuey refuses the write
too. Against a Queuey that itself runs on your machine or a private network, only the server knows what its
delivery may reach, so the CLI leaves the check to it.

### Prove it delivers: `queuey verify`

`apply` exiting 0 says the configuration landed. It does not say events arrive. `verify` asks Queuey to
verify the queue's flow with the producer's own events, and reads the verification until Queuey has
settled it: each step from the ingress to the final state, with its evidence.

```bash
queuey publish orders --data '{"orderId":"A-1","test":true}' --idempotency-key order-A-1 --json
queuey verify orders --event evt_…     # the event publish answered: did it arrive?
queuey verify payments --event-type payment_intent.succeeded --ingress-auth stripe   # waits; trigger it now
```

- `--event` follows an event that is already in the queue: publish one the way the producer does, with
  its key, and pass the event id the ingress answered. `queuey publish` does that and prints the id, and
  the `verify` command that follows it.
- `--event-type` waits for the next event the ingress takes with that type, from the moment `verify`
  says it is waiting: trigger it then, for example with `stripe trigger payment_intent.succeeded`.
  `--ingress-auth` adds that the ingress verified it with that signed-request template, so a Stripe flow
  is proven with Stripe's own event and signature.

```text
✓ Passed — orders (que_…) in ten_…, event evt_…
  Event evt_… went from the ingress to Delivered: the receiver answered 200.
  ✓ ingress_reached     passed  eventId=evt_…
  – ingress_auth        skipped  reason=not_recorded
  ✓ persisted           passed  …
  ✓ routed              passed  eventTypeRecorded=true groupKeyRecorded=false partitionKeyRecorded=false
  ✓ delivery_attempted  passed  attemptId=att_… attemptNumber=1 attempts=1 targetHost=hooks.example.com sent=true …
  ✓ delivery_auth       passed  signing=queuey …
  ✓ receiver_response   passed  statusCode=200 durationMs=38
  ✓ final_state         passed  status=Delivered
  Verification ver_….
```

`--send` has Queuey send a test event through the queue's ingress instead, with `--event-type` as its
type. It works only where all three hold, so it is not the check to reach for first:

- the Queuey you talk to has active verification switched on. Production Queuey does not today, and
  answers `active_verification_disabled`;
- the key has `event.publish` on the queue, in a workspace tagged `dev`, `test` or `staging`. A
  workspace tagged `prod`, or with no tag, counts as production and answers `production_workspace`;
- the queue's ingress takes events without a key or a signature. Queuey never sends your key and cannot
  make a provider's signature, so a keyed or signed ingress answers `not_tried`.

```bash
queuey verify orders --send --data '{"type":"order.created","test":true}'   # a dev, test or staging workspace
```

The test event is real: the receiver gets it like any other, so send data it treats as harmless. It is
one JSON value of at most 64 KB, and Queuey never signs it as a provider.

`--timeout` (or `--wait`) is how long Queuey follows the event, in seconds: a minute when left out, at
most 900. `verify` exits 0 only when the verification passed. `failed`, `timed_out` and `not_tried`
exit 1 with Queuey's summary, and a refusal exits 1 with Queuey's message. With `--json`, the result is
`{ "schemaVersion": 2, "tenant", "queue", "queuePublicId", "verification": { … } }`: the verification in
Queuey's own shape, with its own `schemaVersion`, the same one Queuey's agent tools answer with. Neither
output shows a payload value or a secret: the evidence is ids, statuses, times and header names.

`queuey publish <queue>` publishes one event the way a producer does: with the configured key, to the
queue's ingress URL. A fixed `--idempotency-key` makes the event recognizable: publishing it again answers
with the same event (`replayed`), and nothing new is stored. When the key may read the queue, publish
reads what its ingress requires first, and refuses before anything is sent what it cannot give, saying
what is required: a provider's signature (Stripe's event comes from Stripe), Queuey's own signature, or a
key and a signature together. `--json` prints `{ "schemaVersion": 1, "tenant", "tenantFrom", "queue",
"queuePublicId", "eventPublicId", "receivedAtUtc", "mode", "replayed", "verify" }`, never the payload or a
key. When the deployment file decides the workspace, publish says so before it sends, and `tenantFrom`
names the file.

`queuey events get <evt_…> --queue <queue>` reads the event as Queuey's REST API serves it: its status,
times and each attempt with what Queuey decided after it. The payload, header values and the receiver's
responses are content: `--content` reveals them, only when the key has `event.payload.read` and the
queue's payload visibility lets values out, and Queuey records every look.

`<queue>` is the queue's name or its id (`que_…`). `verify` finds a name in the workspace by the same
rule as `apply`: the deployment file's `tenant` when it names one, otherwise `--tenant`,
`QUEUEY_TENANT` or `queuey.json`. When `--tenant` or `QUEUEY_TENANT` names another workspace than the
file, both commands fail and name the two, rather than guessing which one you meant. The output names
the workspace. `verify` needs a key that may read the queue and its events (`queue.read`,
`event.read`). Against a Queuey without flow verification it fails with
`flow_verification_unavailable` and sends nothing, rather than falling back to something else.
`--file` is the test event to send: a deployment file there is refused, since `--deployment` names
that one.

### The schema

`queuey schema` prints the JSON Schema for the deployment file: every field, the values it accepts
and what it does. Its `$id` is the copy published with that release, at its tag, so it describes the
fields the CLI you run accepts. Point `$schema` at it and your editor validates the file as you type:

```jsonc
{ "$schema": "https://raw.githubusercontent.com/Queuey-AI/queuey-client/v<version>/schema/queuey.deploy.schema.json" }
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

### Managed by the file

`apply` marks each queue it writes, and the workspace's settings when the file declares them, as
managed by the file: the repository, the file's path in it and the commit. The Queuey console shows
the mark with a link to the file, and a change to a managed queue's or workspace's configuration from
anywhere else (the console, the API, an agent) is refused with `managed_by_deployment` and where the
file is, so the next deploy cannot quietly undo it. While Queuey only warns about such changes, the
change goes through and the queue records who made it. Operating a queue (pausing it, its lock,
verify, recovery, replay) is never refused.

The CLI reads the source from git: `origin`'s URL without user info, the file's path from the
repository root, and `HEAD`, which it leaves out when the file has changes git has not committed.
`--repo`, `--repo-path` and `--commit` give each one instead, and `--no-git` leaves git alone. A token
in the remote's URL (`https://token@github.com/…`) is never sent, and Queuey strips it again before it
stores anything. A path on your machine, or a URL the CLI cannot read, is not sent at all. The CLI
runs the `git` it finds in your `PATH`, never one in the directory it runs in, and passes it none of
your `QUEUEY_*` variables.

A person can detach a queue or the workspace from the file in the console, with a reason. `apply`
then skips it, and `apply --check` reports it, with who detached it, when and why, instead of as
drift. To take it back:

```bash
queuey apply --adopt orders               # or --adopt workspace, or both: --adopt orders,workspace
```

`--adopt` shows what the file changes on what it takes back, then applies. It needs the key `apply`
needs; adopting the workspace also needs `tenant.write`. `queuey plan --adopt` shows the same without
writing. `apply --json` and `apply --check --json` print an object with `schemaVersion` (1) first;
what apply skipped is under `skipped`, and what the check found detached under `detached`.

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
| `plan` | Ask Queuey what apply would change and refuse, as dry runs. Writes nothing |
| `apply --check` | Report drift and exit non-zero. Read-only — the CI gate |
| `apply --adopt <queue>` | Take a queue (or `workspace`) a person detached back under the file |
| `verify <queue>` | Verify the queue's flow with Queuey, step by step from the ingress to the final state |
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
| `publish <queue>` | Publish one event the way a producer does, and print its id for `verify --event` |
| `events get <evt_…>` | Read an event's status and attempts as Queuey serves them; `--content` reveals its payload where allowed |
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
| `2` | Bad arguments — among them an option the command does not take |
| `3` | Missing or invalid credentials / configuration (including an unset `${VAR}`) |
| `4` | The target assembly could not be loaded |

### Recipes

**First deploy of a new service**

```bash
queuey credentials set --name partner-key --from-env PARTNER_KEY
queuey apply --dry-run            # catch typos with no credentials and no network
queuey plan                       # what would change, and would Queuey accept it?
queuey apply
queuey publish orders --data '{"orderId":"A-1","test":true}' --idempotency-key order-A-1   # prints evt_…
queuey verify orders --event evt_…   # did it arrive?
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
delivery of a queue set to forward to a local listener (Local forward, `"kind": "localForward"`) to
your machine — Stripe-`listen` style. Scope it to one queue (`--queue`, by name or by id) or a whole
workspace (`--tenant`, where one listener covers every queue under it):

```bash
# receive this queue's deliveries on your machine — Ctrl-C to stop
queuey listen --queue orders --tenant ten_… --forward-to http://localhost:5000
# targets production by default; add --api-base http://localhost:5223 to point at a local instance
```

**`--forward-to` is the origin only.** Each delivery keeps the path and query of the queue's endpoint:
a queue that delivers to `https://api.example.com/api/orders` arrives at
`http://localhost:5000/api/orders`, with the same method, headers and body, and only the host swapped.
A path in `--forward-to` goes in front of the endpoint's path. `--forward-exact` posts to
`--forward-to` **verbatim** instead, to bridge deliveries into a **fixed** local endpoint — e.g. a
local Queuey ingress route:

  ```bash
  queuey listen --api-key qak_… \
    --queue que_… \
    --forward-to http://localhost:5084/events/ten_…/first.queue \
    --forward-exact
  ```

**One listener per queue.** The first session that listens on a queue owns it. Another one is
refused with `listener_already_connected`, and told since when the queue has been held, until the
first one stops. `--take-over` takes the queue over on purpose, and the session it took over from
stops (exit 1). A queue under a workspace another session listens on is that session's too:
listening on the queue is refused the same way unless you pass `--take-over`, and then the workspace
session is told it lost that queue and keeps the others. Only the owner gets the queue's
deliveries, and only its answer counts: your local response is the delivery's outcome — a 2xx
delivers the event, anything else dead-letters it, and `queuey replay` sends it again. Your app gets
18 seconds to answer, then the delivery records a 504. A listener that goes away before it answers
leaves the event waiting for the next listener, so **the same event can arrive more than once**:
make your handler idempotent. The API key needs **`queue.listen`** on the queue or workspace (a
Build, Full access or ProducerAdmin key).

**`--json` for agents and scripts:** one JSON object per line on stdout, each with
`"schemaVersion": 1` and a `type` — `listening` first, a `delivery` per forward, `lost` when a
workspace session loses one of its queues, and one last line: `refused` (it never listened, an error
before the session included), `superseded` (another session took the queue over) or `closed`
(stopped, terminated, or the connection was lost for good). The last line comes after the session
has stopped, so the queue is free when you read it. When whatever reads the output closes it (as
`head -n 5` does), or falls more than about 1024 lines behind, the session stops and frees the queue
too: the stream then ends without a last line, and the exit code is 1. After a lost connection the
session reconnects at once and again after 1, 2, 4 and 8 seconds, so it is back within the 10
seconds Queuey keeps its queue for it. `path` and `localUrl` leave out the query and
any part of the path that may be a secret; your app still gets the whole URL. Exit codes: 0 after
Ctrl-C, 1 when refused, taken over or the connection is lost for good, 2 on a usage error, 3 when
the key is missing or refused, and 143 after SIGTERM.

```json
{"schemaVersion":1,"type":"delivery","eventId":"evt_…","eventType":"invoice.paid","queue":"que_…","method":"POST","path":"/api/stripe","localUrl":"http://localhost:5000/api/stripe","status":200,"durationMs":12,"signatureHeaders":["Stripe-Signature"],"error":null}
```

`signatureHeaders` names the headers Queuey's signing set on the delivery, such as a recalculated
`Stripe-Signature`. A queue without an endpoint has no path to keep, and nothing signs its deliveries.

### `queuey replay` — re-send one event to your listener

Read-only DLQ debugging: forwards an existing event (including a DLQ'd one) to your connected
`queuey listen` session. The event is **not** modified and the real endpoint is never contacted.

It works on a queue that forwards its deliveries to the listener (Local forward) and shares its payloads
in full, and the key needs `queue.write` and `queue.listen` on it. Any other queue is refused, and
`queuey replay` prints the server's reason, which says what to change. On a queue whose payload privacy
is shape only, a person changes it in the console first; live deliveries still reach the listener.

```bash
# terminal 1 — start a listener
queuey listen --api-key qak_… --queue que_… --forward-to http://localhost:5094
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
