# Queuey.Client

The official .NET SDK for [Queuey](https://queuey.ai) — a webhooks / event-distribution
platform. **Decorate, sync, publish.**

> Status: early preview. The packages are on NuGet as previews, so add them with `--prerelease`, and the API
> surface may still change.

```bash
dotnet add package Queuey.Client --prerelease
queuey keys mint --write .env              # the app's signing key, for its workspace; the secret is never shown
queuey keys mint --write user-secrets      # or into the .NET project's user secrets
```

```csharp
var queuey = new QueueyClient(new QueueyOptions
{
    // Environment defaults to Production (api.queuey.ai + ingress.queuey.ai).
    TenantPublicId = "ten_…",
}.UseEnvironmentVariables());   // QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET, from the environment or, in Development, .env

await queuey.Ingress.PublishAsync("orders", order);
```

**Which key.** An app signs what it publishes with a signing key that reaches only its workspace (or, with
`--queue`, one queue): that is the app's primary way in, and `queuey keys mint --write .env` makes one. A license-wide API key (`ApiKey = "qak_…"`) belongs
to a person, who mints it in the console; give it to an app only when the app manages Queuey, not to publish.

**From .NET configuration.** With `--write user-secrets` the values are in the project's user secrets, which
`builder.Configuration` reads in Development, with appsettings and environment variables. Bind the options from it
by the same names; no other package is needed:

```csharp
var options = new QueueyOptions { TenantPublicId = "ten_…" }
    .UseSettings(key => builder.Configuration[key]);   // QUEUEY_SIGNING_KEY_ID, QUEUEY_SIGNING_SECRET, QUEUEY_DELIVERY_SECRET, …
builder.Services.AddSingleton(new QueueyClient(options));
```

**`.env` in Development.** `UseEnvironmentVariables()` fills each setting not set in code from its `QUEUEY_*`
variable. In Development (`DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT`), when the environment does not hold
both, `QUEUEY_SIGNING_KEY_ID` and `QUEUEY_SIGNING_SECRET` are read as a pair from `.env` in the working folder, and
nothing else is: hosts, tenant and API key never come from a file. Only from a regular file of your own (on Windows,
under your profile folder, with no link or junction on the way) that git does not track. The environment always wins. Outside Development `.env` is never read: production takes
its secrets from the platform, and a file in the working folder read silently there would be a surprise.

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
    SigningKeyId   = "hsk_…",   // or .UseEnvironmentVariables(), as above
    SigningSecret  = "…",
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

Make the secret once, so Queuey and the receiver hold the same value and neither ever shows it:

```bash
queuey credentials generate orders-signing --write .env   # or --write user-secrets
```

It stores a random 32-byte value in Queuey as the credential deliveries are signed with, and writes it as
`QUEUEY_DELIVERY_SECRET`. Point the queue's delivery at it in `queuey.deploy.json`, then apply:
`"delivery": { "signing": { "enabled": true, "credentialRef": "orders-signing" } }`.

```csharp
var verifier = QueueyDeliveryVerifier.FromEnvironment();   // QUEUEY_DELIVERY_SECRET; or new QueueyDeliveryVerifier(secret)

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
take — a typo like `--paln` fails with exit 2 and the options that command accepts.

By default the plan is made here and stored nowhere, as before, which is what a pull request wants.
`queuey plan --store` stores it in Queuey as a **configuration plan** (`plan_…`): the dry runs are its
steps, the diff a person approves is Queuey's own, and Queuey seals it with a hash and the policy's
decision. When the policy runs it (`execute`), `queuey apply --plan plan_…` applies it. When a person
approves it (`requires_approval`), `queuey apply --plan plan_…` sends it to Queuey's approval inbox, and
`queuey plan --submit` does so at once: it prints where a person approves it and exits `5`. Once approved,
`queuey apply --plan plan_…` applies it, and `--wait` waits for the approval. `denied` exits 1. A stored
plan carries where the file is, and in GitHub Actions the branch, workflow and pull request, so the person
who approves it sees where it comes from. Against a Queuey that stores no plans, `--store` plans here and
says so.

`queuey plan --json` prints the plan as an object a script can read; check `schemaVersion` first. A stored
plan (`--store`, `--submit`) is version 2: `file`, `tenant`, `planId`, `planHash` (Queuey's, 64 hex characters), `stored`, `version`,
`status`, `decision`, `rule`, `class`, `approvalUrl`, `expiresAt`, `wouldSucceed`, `changeCount`, `queues`,
`steps`, `skipped` and `warnings`. A local plan is version 1: the same without what only Queuey knows, with
`planId` null and the client's `planHash`. Each step has its `target`, `aspect`, `creates`, `changes`,
`notes`, `state`, `desired` and `error`. A change's `from` and `to` are the values as Queuey's config reads
them back, so a number, a boolean or an object stays JSON. With `--json`, before or after the command,
every error is JSON too.

Each entry in `queues` has the queue's `name`, its `publicId` (null for one apply would create) and its
`ingressUrl`, where producers publish to it, so a provider can be pointed at a queue in the same change that
creates it. A local plan's `planHash` is `sha256:` over what apply would change and the server state it
rests on: each step's `state` is the hash Queuey gave the config its dry run started from, and `desired` is
what apply would send to a queue it creates. The order of the queues, the file's formatting and the plan's
notes do not count, so the same file against the same state gives the same hash. When the state moves, or
the file changes what apply does, the hash changes.

**Applying through a plan.** Where Queuey applies from an API key only through a configuration plan (a
production workspace, once Queuey enforces plans), `queuey apply` makes the plan itself and applies it at
once when the policy runs it; when a person approves it, apply writes nothing, prints where to approve it
and exits `5`, and `queuey apply --wait [--timeout <seconds>]` waits for the approval (30 minutes unless
`--timeout` says otherwise) and then applies it. Each write of an apply bound to a plan is one of its
steps, sent once, and Queuey answering that the step is already written counts as written. A write whose
answer was lost is looked up in the plan after a short, growing wait, and sent again only when the plan
does not show it. When what the plan rests on has moved (`plan_stale`), or the file no longer matches it
(`not_in_plan`), the apply stops with exit 1: plan again, and the new plan shows what is left. Until
Queuey enforces plans, an apply without one goes through, and Queuey's `would_require_approval` warning
is printed on stderr with what to do, and is in the result's `Warnings`. A sync from code
(`SyncQueuesAsync`, `queuey queue sync`) that gets `plan_required` for a queue's change leaves that change
out, reports it on the queue with what to do (`QueueApplyResult.NeedsPlan`), and goes on with the other
queues, so an app that syncs when it starts still starts. It is in the result's `Warnings`, and logged as
a warning when the host has logging; check `AllSucceeded`, not only for an exception.

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
before anything else is written. A key sets the environment when it creates a workspace, so the error
says how to make one marked with it: `queuey create-tenant --name <name> --environment dev`. And when no
workspace is named anywhere (the file's `tenant`, `--tenant`, `QUEUEY_TENANT`, `queuey.json` or the
profile) and the file says `dev` or `test`, `apply` creates one marked so itself, named `queuey-dev` or
`queuey-test`, says its id, and applies to it. Every output after that names it, errors too
(`createdWorkspace` in `--json`, with `"tenantOption": "--tenant ten_…"`): name it, or the next `apply`
creates another. It never does so in CI, and never for a file whose delivery names a `credentialRef`, which
a new workspace does not have yet; those fail as a missing workspace, with `create-tenant` as the way out.
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
# POSIX sh, one command: the Stripe CLI's own test secret is never printed, and needs no variable from an earlier shell
STRIPE_WHSEC="$(stripe listen --print-secret)" queuey credentials set --name stripe-whsec --type HmacSigning --key-id stripe-whsec --from-env STRIPE_WHSEC
                               # the ingress verifies with it at once (a Queuey that predates this: run apply again)
                               # another machine's or account's test secret is refused: replacing it is a person's decision (--replace)
stripe listen --forward-to <ingress URL>        # in the background: Stripe's test events, signed with that secret
queuey listen --queue stripe --forward-to http://localhost:5000
```

**The one who sets this up never needs to hold the secret.** An agent, or a script, runs
`queuey credentials request stripe-whsec` instead of `credentials set`. Queuey opens a one-time request and the
command prints the console link for it. A person who can manage the workspace's credentials signs in there and
pastes the value, which is stored exactly as `credentials set` stores it, and the ingress waiting for the name
verifies with it at once. The value never passes through the command, its logs or a conversation with an agent,
no API returns it, and Queuey uses it only where the workspace's configuration does. Without `--type` the request
is for an `HmacSigning` secret, which Queuey never sends as it is; a key or token Queuey sends to a receiver is
asked for with its `--type`. The link works once, for a day; asking again for the same name while it is open
prints the same link. For a name the workspace has, only the secret is replaced, with the credential's key id and
username as they are.

**A credential that is not stored yet is accepted.** `apply` keeps the name the ingress waits for, and
the ingress refuses every event until a credential by that name is stored. Storing it, with `credentials set`
or by a person fulfilling `credentials request`, points the ingress at it at once; with a Queuey that predates
credential requests, `apply` must run again to do that. The plan, `apply` and Queuey's setup review all say so, with the command that
stores it, and `apply --check` reports drift once it is stored. Plan and `apply` suggest `credentials request`, where a person
pastes the value, unless the file's `workspace.environment` is `dev`: there `credentials set --from-env` stores a value you
hold. Each names the other way too, and so does a delivery credential plan and `apply` cannot find. Without `--profile`, the
commands name the file's `tenant` with `--tenant`: `credentials` otherwise goes to the configured workspace, while plan
and `apply` go to the file's. `template` is one of Queuey's signed-request
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
`queue.listen` on it, and a workspace marked `dev`: Queuey refuses `localForward` in any other workspace, and in
one without an environment, which counts as prod (`local_forward_needs_dev_workspace`). `plan` says so before
`apply`, also for a queue it would create, and `apply` shows Queuey's refusal with the way out. Queues that
already forward are left as they are. Raising a workspace's environment above `dev` while a queue forwards is
refused too (`local_forward_blocks_environment_raise`), with the queues to set to `http` first.

**A delivery URL on your machine is refused.** Queuey's delivery never reaches `localhost`, `*.localhost`,
a loopback address or a private one, so `plan` and `apply` against Queuey's hosts refuse such a `url` or
`baseUrl` before anything is sent, and point to `"kind": "localForward"` instead. Queuey refuses the write
too. Against a Queuey that itself runs on your machine or a private network, only the server knows what its
delivery may reach, so the CLI leaves the check to it. `apply --dry-run` does not connect, so it only warns:
the answer comes from Queuey when you plan or apply.

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
exit 1 with Queuey's summary, and a refusal exits 1 with Queuey's message. With `--json`, verify prints
one JSON object per line, each with `"schemaVersion": 3` and a `status`. Once Queuey follows the event,
and before verify waits, the first line is `{ "status": "waiting", "verificationId", "eventType",
"observeUntil", "message", … }`: trigger the event then. The last is `{ "status": "done", "tenant", "queue",
"queuePublicId", "verification": { … } }`: the verification in Queuey's own shape, with its own
`schemaVersion`, the same one Queuey's agent tools answer with. Neither output shows a payload value or a
secret: the evidence is ids, statuses, times and header names.

`--background` starts the verification and returns at once with its id and the command that reads the
outcome later, so the same shell can trigger the event. It exits 0 once the verification is started,
whatever comes of it, so it is no gate in CI: `--wait ver_…` is, with the exit codes above.

```bash
queuey verify stripe --event-type checkout.session.completed --ingress-auth stripe --background
stripe trigger checkout.session.completed
queuey verify que_… --wait ver_…    # reads it until Queuey has settled it
```

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

`<queue>` is the queue's name or its id (`que_…`). `--queue <queue>` is another name for it, as in
`queuey verify --queue orders --event evt_…`: give one or the other. `verify` finds a name in the
workspace by the same rule as `apply`: the deployment file's `tenant` when it names one, otherwise `--tenant`,
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

**A file never reads the CLI's key.** Whatever a file expands is stored in Queuey, and a delivery URL
sends it on to the receiver, so `${QUEUEY_API_KEY}` in a URL would hand your key to whoever the URL
points at. Two things are refused:

- **The CLI's own `QUEUEY_` settings**, refused before the variable is read. A file may still use
  `QUEUEY_TENANT`, `QUEUEY_WORKSPACE_ENVIRONMENT`, and the `QUEUEY_…_URL` and `QUEUEY_…_DELIVERY_KIND`
  names `pull --as` writes.
- **A value that starts like a secret** (`qak_`, `whsec_`, `sk_live_`/`sk_test_`, `rk_live_`/`rk_test_`),
  whatever the variable is called, and from a `${VAR:-default}` too.

Your own variables are yours, whatever they are called. Where a secret goes:

- **A receiver whose URL carries a token** (a Slack or Zapier hook, an Azure Function's `?code=`) takes
  the whole URL from one variable: `"url": "${ORDERS_HOOK_URL}"`.
- **A secret sent in a header** is a credential (`queuey credentials set`), named in `credentialRef` with
  an `authMode`. A `credentialRef` takes the credential's name, never its value.

`--check` compares only what the file declares. A workspace holding settings your file is silent
about is inheritance working as designed, not drift — so you can put as much or as little under code
as you want.

Adopting a workspace that was configured before anyone wrote it down? `queuey pull --emit-code
Queues.cs` generates the `[QueueyQueue]` declarations. Behaviour only: destinations stay in the
deployment file, where they belong.

### Profiles: one flag for an environment

`--profile <name>` (or `QUEUEY_PROFILE`) picks one environment, such as `dev` or `prod`, and takes both
halves of it. If either half is missing, the command fails and says which one.

**The values, in the deployment file.** Committed with the file, so promoting a change is a pull
request. They fill the file's `${VAR}` references:

```json
{
  "workspace": {
    "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}",
    "delivery": { "baseUrl": "${QUEUEY_BASE_URL}" }
  },
  "queues": {
    "stripe": { "delivery": { "url": "/api/stripe", "kind": "${QUEUEY_STRIPE_DELIVERY_KIND}" } }
  },
  "profiles": {
    "dev": {
      "variables": {
        "QUEUEY_WORKSPACE_ENVIRONMENT": "dev",
        "QUEUEY_BASE_URL": "https://dev.example.com",
        "QUEUEY_STRIPE_DELIVERY_KIND": "localForward"
      }
    },
    "prod": {
      "variables": {
        "QUEUEY_WORKSPACE_ENVIRONMENT": "prod",
        "QUEUEY_BASE_URL": "https://api.example.com",
        "QUEUEY_STRIPE_DELIVERY_KIND": "http"
      }
    }
  }
}
```

A profile gives values to whatever names the file uses, including the ones `pull --as` writes.

- A value is taken as it is written and never expanded again.
- A value that looks like a secret is refused: a key prefix, hex, a UUID or base64 outside a Queuey id.
  Keep secrets in credentials, by name.
- A variable a profile leaves out comes from the environment, as without a profile.
- A variable set in both the profile and the environment, to different values, is an error. Neither
  is picked.

**The connection, with you.** It never goes in the repository. It lives in `~/.queuey/config.json`, or in
the file `QUEUEY_USER_CONFIG` names. `queuey login --profile dev` writes it (see [Log in](#log-in)):

```json
{
  "profiles": {
    "dev":  { "license": "lic_…", "tenant": "ten_…",
              "apiBase": "https://api.queuey.ai", "ingressBase": "https://ingress.queuey.ai" },
    "prod": { "apiKey": "qak_…", "license": "lic_…", "tenant": "ten_…" }
  }
}
```

A profile without `apiKey` connects with the login for its API host and license. One with `apiKey`
connects with the key, as before. `apiBase` and `ingressBase` default to Queuey's hosts. A profile may
also set `source`.

The file holds keys, so the CLI reads it only when nobody else can reach it, checked as ssh checks
`~/.ssh`:

- It must belong to you and be readable and writable by you alone: `chmod 600 ~/.queuey/config.json`.
- A link is followed to the file it points to, and that file is the one checked and read.
- Each folder above it, up to and including your home folder, must belong to you or root, and others
  must not be able to write to it. A folder with the sticky bit, such as `/tmp`, is allowed.
- The CLI reads the mode and the owner, not ACLs. An ACL (`setfacl`, or `chmod +a` on macOS) can let
  others in without the mode showing it, so do not put one on the file or its folders.

A file others could write could point the CLI at another host and catch the key, and a warning is easy
to miss in CI. On Windows, the CLI does not check: your user profile's ACL protects the file.
In CI there is no person to log in: write the file from the pipeline's secrets, with an `apiKey`, to a
file only the job can read, and point `QUEUEY_USER_CONFIG` at it.

```bash
queuey plan --profile prod           # CI, in the pull request that promotes a change: stores no plan
queuey plan --submit --profile prod  # on main, or for a release: a plan a person approves in Queuey's inbox
queuey apply --profile prod     # on merge
queuey apply --profile dev && queuey listen --profile dev --queue stripe --forward-to http://localhost:5000
```

Which commands use it:

- `apply`, `plan`, `verify`, `publish`, `listen`, `events get` and `credentials` take both halves.
  `listen` and `credentials` read `./queuey.deploy.json` for theirs.
- `whoami --profile` shows the connection it resolves.
- `apply --dry-run` needs only the file's half, since it never connects. If the connection is missing
  or cannot be read, it says so on stderr and goes on.
- A command that takes no profile refuses to run while `QUEUEY_PROFILE` is set, instead of connecting
  somewhere else.

With a profile, these rules hold:

- A flag still wins for its value.
- `queuey.json` is not read.
- A `QUEUEY_` variable for the key, license, workspace or a host must agree with the profile, or the
  command fails. A value left in your shell from another environment never mixes in.
- The workspace comes from the connection. A file that names one too, such as
  `"tenant": "${QUEUEY_TENANT}"` with the id in each profile, must name the same one.

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
queuey keys mint --write .env                    # for every queue in the workspace
queuey keys mint --queue orders --write .env     # for one queue
queuey keys mint --type api-key --write .env     # a QUEUEY_API_KEY that can only publish there
queuey keys mint --write user-secrets            # into the .NET project's user secrets
```

This writes `QUEUEY_SIGNING_KEY_ID` and `QUEUEY_SIGNING_SECRET` (or `QUEUEY_API_KEY`) where the app reads
them, and never prints the secret, not in `--json` either. Without `--write` nothing is minted: the command
says which variables the app needs, the `--write` that fits the folder, and the console page where a person
makes or looks at the key. `--show-secret` prints it instead, once, and warns.

- `user-secrets` runs `dotnet user-secrets set` for the project in the working folder, with the values on its
  stdin and never as arguments, which other users can see in the process list. The project needs a
  `UserSecretsId`; `dotnet user-secrets init` adds one. `dotnet user-secrets list` runs first, before anything is
  minted or stored, and the answer names the id it uses. Both evaluate the project with MSBuild, so they run its
  build logic: trust the project as you would for `dotnet build`.

- The lines that set them are replaced, and every other line stays. Afterwards the file is readable and
  writable only by you (0600). A file others could read is tightened, and the command says from what.
- In a git repository the file must be one git ignores, or nothing is minted. So must a file that is a
  link, or one you cannot read and write.
- The producer reads them from the environment: `options.UseEnvironmentVariables()` in .NET fills each
  setting that is not set in code, from `QUEUEY_SIGNING_KEY_ID`, `QUEUEY_SIGNING_SECRET`,
  `QUEUEY_TENANT`, `QUEUEY_INGRESS_BASE` and `QUEUEY_API_KEY`. With both signing values set, it does not
  read `QUEUEY_API_KEY`: the producer signs with the key that reaches only its queue.
- `queuey publish` reads them too, from the environment or else from `./.env`, when no API key is set,
  as with a login. Only those two names are read from `.env`, only when it is a plain file of your own,
  and a key that is set wins. `--json` says where the key came from (`signingKeyFrom`).

Minting needs a login (`queuey login`) for a person who may manage keys, or a credential with
key-management rights. A deploy key deliberately has none, since a key that can mint keys turns pipeline
access into account access. In a prod workspace a login gets `approval_required`: the command exits `5`
with the link where a person makes the key, and nothing is minted or written. `queuey keys list` shows the
workspace's keys (`--queue orders` a queue's), with who minted each, and `queuey keys revoke <keyId>` ends
one.

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

### Log in

```bash
queuey login --profile dev
```

It prints a link and a code. Open the link, check that the page shows the same code, and approve it in
the Queuey console. There is no key to copy. New to Queuey? The link lets you sign up first.

- **In a terminal** it opens the browser and waits.
- **Without one**, as when an agent runs it, it prints the link and exits `5`: waiting for a person.
  With `--json` that is one line, `{"status":"waiting_for_person","link":…,"userCode":…,"expiresAt":…}`.
  The code is kept for ten minutes, and `queuey login --wait` (or `queuey login` again) finishes once
  it is approved. An agent shows the person the link, then runs `--wait`.
- **Already logged in?** It says so and exits `0`, so it is safe to run first.

The login is a connection for one person, one license and a scope: `operate` (the default) acts as the
person may, and `--scope read` only looks. It never approves anything: what needs a person in prod
still goes to the inbox. Every command without an API key uses it for the API host it was made for.

- `--profile <name>` writes that profile in `~/.queuey/config.json`: the license, the hosts, and the
  workspace marked with the profile's environment (`dev`, `test`, `staging` or `prod`), or the one
  `--tenant` names. Other profiles, and the profile's other fields, stay as they were. A profile with an
  `apiKey` keeps its hosts, license and workspace: login never writes its hosts (a missing host is
  Queuey's own), and adds a license or workspace only where none is. A `QUEUEY_`
  variable that disagrees with the profile fails, as with any `--profile`, and `--api-base` overrides the
  host, for a test or a self-hosted Queuey. When the license
  has no workspace for the environment yet, it says how to make one. Comments in the file are not kept.
- The tokens are in `~/.queuey/credentials.json`, next to `config.json`, readable only by you and checked
  the same way. They renew themselves. Two commands at once take turns, so a refresh token is never
  spent twice, which would end the login.
- `queuey logout` ends the login with Queuey and removes it from this machine. `queuey whoami` shows
  it, never a token.
- A profile or `queuey.json` with an `apiKey` works as before, and the key wins over a login.
- Publishing to the ingress (`queuey publish`, your producer) still needs a key. The ingress does not
  take a login. `queuey keys mint --queue <queue> --write .env` makes one for the producer.

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
| `credentials request` | Ask a person to paste a secret in the Queuey console, so it never passes through you |
| `credentials list` | List stored credentials — names and types, never values |
| `keys mint` | Mint an ingress signing key so a producer can publish with HMAC; `--write .env` puts it in a git-ignored file and never prints the secret |
| `keys list` / `keys revoke` | A queue's signing keys (never a secret), or end one |

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
| `create-tenant` / `create-queue` | Provision imperatively (prefer `apply`); `create-tenant --environment dev` makes a dev workspace |
| `login` / `logout` | Log in with your Queuey account (open the link, approve the code), or end the login |
| `whoami` | The resolved hosts (Production, Local or Custom), tenant and license, and the key (masked) or the login |

### Exit codes

A stable contract, so CI can branch on them:

| Code | Meaning |
| --- | --- |
| `0` | Success — and for `--check`, no drift |
| `1` | The run did not fully converge, or `--check` found drift |
| `2` | Bad arguments — among them an option the command does not take |
| `3` | Missing or invalid credentials / configuration (including an unset `${VAR}`) |
| `4` | The target assembly could not be loaded |
| `5` | Waits for a person: a configuration plan's approval in Queuey's inbox (`plan --submit`, `apply`), nothing applied; or a login code to approve (`login`) |

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
Mint once while logged in, or from an admin credential, with `--write .env` or into your secret store.

**`credentials set` reads the secret from the environment, never an argument.** Arguments land in
shell history and CI logs. Setting the value a credential already holds keeps its version, and makes it usable again
if it had expired, so running the same `set` again in CI changes nothing.

**A different value for a name the workspace has needs `--replace`.** Without it, `credentials set` refuses the
value (`credential_exists`, exit 1) and stores nothing: replacing the secret changes every queue and ingress that
uses the credential at once, and a test secret set against prod by mistake would make prod's ingress refuse every
real event. With `--replace` the credential gets the new value as a new version of its secret, under the same id,
so everything that refers to it uses it. Replacing a secret is a decision for a person: the commands advise, plan and
apply suggest never carry `--replace`, not even in test mode. When the value is not yours to hold, `credentials request` asks the person who has it to paste it
in the console instead.

**`pull` won't overwrite.** Use `--stdout` and diff it first — replacing a committed declaration is
how an intentional, not-yet-applied edit disappears.

### From what you want to a deployment file: `queuey advise --intent`

`queuey advise` reads a repository and says how Queuey fits. Give it a **Desired Flow**, what you want as JSON, and
it designs the queue: it reads the repository for how, proposes `queuey.deploy.json` with the reason for each setting,
and keeps the code changes apart from it. It writes nothing.

```json
{
  "source": { "kind": { "value": "stripe", "provenance": "stated" } },
  "destination": { "route": { "value": "/api/stripe", "provenance": "stated" } }
}
```

```bash
queuey advise --intent flow.json --json
```

Each field has its `value` and its `provenance`: `stated` (you said it), `evidence` (advise found it, with the file and
line) or `assumed` (advise's default, an assumption to check). Only stated fields are intent. advise works the others
out again on each run, so the flow it returns can go back in, with what you confirm marked `stated`. `queuey schema
--flow` prints the schema.

What differs between environments goes in a profile named after the flow's environment: the workspace's environment,
where the queue delivers, and the base URL when the intent gives it. So `queuey plan --profile dev` works on the file as
proposed, and production is one more profile. A deployment file that is there without profiles keeps fixed values.

**Without an environment in the intent, advise goes by the file.** A new file is for `dev`, which advise writes into it.
A file that is there without `workspace.environment` applies to a workspace Queuey counts as `prod`, so advise proposes
for prod: delivery over HTTP, `credentials request` for a secret, and Stripe's path for a real endpoint. A file that
takes the environment from a variable gets what its profile gives that variable, or the variable's `${VAR:-default}`, as
plan and apply read it; without either, it is for prod too. The dev path, a local listener and Stripe's test mode, needs a
new file or one that gives dev: a stated `dev` against a file or profile that gives no environment, or another one, is a
conflict, never a file rewritten to dev. If the file is for dev, give it dev first: `workspace.environment` set to `dev`,
`${QUEUEY_WORKSPACE_ENVIRONMENT:-dev}` where CI sets prod, or the profile's value.

advise stops rather than guess. A file with profiles never gets a new one: advise picks a profile by the environment it
gives, never by its name. Without an environment in the intent, one profile takes the flow, and several are an
`ambiguous` conflict that names what each gives. With one, the profile that gives it takes the flow, and the commands
take it with `--profile`; the file's only profile also takes it when that profile gives no environment, and gets it
written in, except for `dev`: a profile that gives no environment points at a workspace Queuey treats as prod, so a
stated `dev` there is a conflict, and the profile is never rewritten to dev. When no profile gives it, or several do, the
flow has a conflict that says what to change. Profiles that
share an environment, such as `eu` and `us` in prod, are told apart with `advise --intent … --profile <name>`, which
names a profile the file has, or the first one in a new file, where the intent must state the environment it is for: a
profile's name does not say. Its environment still decides the rest. A queue the
file already forwards to a listener (`"kind": "localForward"`, directly or through its profile) outside dev is a
conflict too: make the flow dev, or give it `http` where the file sets it; so is a fixed `localForward` in a file that can run
outside dev, because its environment comes from a variable or a profile gives another. So is an environment apply would refuse, such as
`"development"` in a profile, and a `workspace.environment` that is not one fixed value or exactly one `${VAR}`.

Stripe's test mode in the next steps comes after `apply`, and only an `apply` that went through shows the workspace is
dev: if it refused the file, the steps say to stop and store no test secret. In a file without profiles, the
`credentials` commands name the file's `--tenant`, since without a profile they go to the configured workspace while
`apply` uses the file's.

A file that takes its environment from a variable, such as `${QUEUEY_WORKSPACE_ENVIRONMENT:-dev}` so CI can set prod,
gets the delivery kind from a variable too, `${QUEUEY_<QUEUE>_DELIVERY_KIND}` without a default, when nothing in the file
holds the environment for the flow: the file has no profiles, or the profile does not set the variable and the intent
states no environment. A stated environment is written into the profile instead, and then the profile holds the kind
too. The kind from a variable has no profile value, and the next steps say to set it to `localForward` in dev and
`http` elsewhere. If CI forgets it, `apply` stops rather than create the queue in prod with a local listener. A profile
that does not set the variable but sets the kind to `localForward` is a conflict, also in dev.

**`infrastructure.content` is the whole file, to write as it is.** With a `queuey.deploy.json` already there, it is that
file with the flow's queue and profile values merged in. The file wins over everything the intent does not state. A
stated value the file contradicts, such as the same queue with another route, is a conflict rather than an overwrite,
and so is a file `apply` cannot read. `infrastructure.merge` says what was added and what the file kept. Comments in the
file are not carried over, and `merge` says so.

advise reads the `queuey.deploy.json` in the repository's root on its own, before the scan and outside its limits. When
the file is there in a form advise does not read, the result is a conflict that says what to do, never a new file over
it. That covers a symbolic link (in a monorepo, to `infra/queuey.deploy.json`), a folder, a file larger than 512 KiB,
one this user cannot read, and anything that is not a regular file, such as a pipe. The file gets 5 seconds to open,
since a pipe without a writer never does. The conflict names where a link points only when that is a plain path in the
repository: letters, digits and `. _ / -`.

**Stripe.** advise finds the handler and its route, `constructEvent`, the raw body, the framework and the port. It
proposes a queue whose ingress verifies Stripe's signature and whose deliveries Queuey signs again in Stripe's format,
so the handler keeps `constructEvent` as it is. In dev the queue delivers to `queuey listen` on your machine.

**Supabase.** For a function that receives a Database Webhook, advise finds the table, the operations and the secret
the handler compares in a header. It proposes a queue the webhook publishes to with a key, and that delivers the header
the handler checks.

**When the repository contradicts the intent, advise stops.** A route no handler serves, a Stripe intent at a handler
that does not verify Stripe's signature, or an order key Queuey cannot read: the flow lists `conflicts`, nothing is
proposed, and the command exits 1. Answer them in the intent and run it again.

`--json` prints `schemaVersion` 1, the `outcome` (`proposed` or `conflicts`), the enriched `flow`, `existing` (Queuey
where the repository has it already: a package, a registration, a deployment file), `scanLimited`, `infrastructure` (the
file, each setting with its basis and reason, and the credentials it names), `code` and `nextSteps`. Each credential
comes with the command that stores it: `credentials set --from-env` in dev, for a value you hold, such as the Stripe
CLI's test secret, and `credentials request` elsewhere, where a person pastes the value so it never passes through you.
Keep the flow beside the code or in the pull request: it explains `queuey.deploy.json`, and `apply` never reads it. Without
`--intent`, `advise --json` lists the `candidates` the repository shows. advise reads the names in a `.env` file and
never a value.

**The scan has limits, and says when it reaches one.**

- It never follows a symbolic link, not even one inside the repository.
- It reads only regular files: at most 512 KiB of each, 6000 files, 20,000 folders, 10,000 entries of one folder and 64 MiB
  in all, for at most 15 seconds.
- It skips lines longer than 4 KiB, folders with tests, fixtures and docs, and names with control or direction characters.

`scanLimited` lists what it left out, and is empty when it read everything. A router mounted from another app in a
monorepo is not followed: mounting counts within the app's own folder.

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
decides it, and a `--tenant` or `QUEUEY_TENANT` that names another one fails the command. With
`--profile`, the connection is **flag → the profile in `~/.queuey/config.json` → default** instead;
see [Profiles](#profiles-one-flag-for-an-environment). When none of them gives an API key, the
command uses the login for its API host ([Log in](#log-in)), with the login's license unless one is
named.

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
| `queuey.deploy.json` | What your workspace and queues should look like, and each environment's values under `profiles` | **Yes.** It carries no secrets by construction |
| `~/.queuey/config.json` | Each profile's connection: `license` / `tenant` / `apiBase` / `ingressBase`, and `apiKey` unless you log in | **Never.** It lives in your home folder, readable only by you |
| `~/.queuey/credentials.json` | The login's tokens, written by `queuey login` | **Never.** Only the CLI writes it, readable only by you |

## License

MIT — see [LICENSE](LICENSE).
