---
name: queuey-integration
description: Assess how a codebase should talk to Queuey and then set it up — choosing between Queuey Edge (durable local publishing), the Queuey.Client .NET SDK, plain HTTP to Queuey ingress, or receiving Queuey webhooks, and declaring queues with the queuey CLI. Use this whenever someone wants to integrate Queuey, add Queuey.Client / Queuey.Edge / Queuey.Cli / Queuey.Client.Waas, publish events to Queuey, decide whether they need Edge, set up queues or a queuey.deploy.json, or asks "how do I send to Queuey" — and also when they only say "webhooks", "reliable delivery", "offline publishing" or "store and forward" and Queuey is already a dependency or the stated destination.
---

# Queuey integration

You are deciding, for one specific codebase, the honest way to connect it to
[Queuey](https://queuey.ai) — and then building exactly that, once a human has
said go.

Queuey is an operational webhook and event delivery engine. A producer POSTs an
event to Queuey ingress and gets `202 Accepted`; Queuey owns delivery from there
(retries, backoff, dead-letter queue, signing, ordering). The interesting
decision is on the producer's side of that POST: whether the process can afford
to lose the event between "it happened" and "Queuey has it", and what already
exists in the codebase to prevent that.

The decision is the product. Any agent can add a package reference; the value
here is choosing correctly, showing the evidence, and being willing to say
"you do not need Edge" or "your existing outbox is enough — publish from it".

## Rules that hold in every phase

**Read before write.** Phases 1 and 2 change nothing: no files, no packages, no
commands with side effects. The person pasting a prompt into a repo they do not
fully know must be able to trust that the first thing you do is look. Only
phase 3 writes, and only after an explicit go.

**Secrets never enter tracked files.** Queuey keeps this split on purpose:
`queuey.json` holds the API key and is git-ignored; `queuey.deploy.json` holds
queue declarations, names credentials by reference, and is meant to be
committed. Keep the split. If you must reference a key, reference an
environment variable or a secret store the project already uses.

**Account-level actions need a human each time.** `queuey create-tenant`,
`queuey keys mint`, `queuey credentials set` and creating an API key in the
console create identities or secrets that outlive this session. Name the
command, say what it creates, and wait. A deploy key deliberately cannot mint
keys, because a key that can mint keys turns pipeline access into account
access — respect that design rather than working around it.

**Syntax comes from the docs, not from memory.** CLI flags, attribute names and
header names change between previews. Cite the published page for anything you
did not read this session in the queuey-client repository or on the docs
site, and prefer reading over recalling: the machine-readable index is
`https://app.queuey.ai/docs/llms-manifest.json`, and every page under
`https://app.queuey.ai/docs/` is plain HTML. If a Queuey MCP connector is
attached, its `queuey_search_docs` tool searches the same index; it is a
convenience, not a requirement.

**Say which versions you assumed.** At the time of writing everything Queuey
publishes is a `0.1.0-preview.*`, so `dotnet tool install` and package references need
`--prerelease` / an explicit preview version, and the API surface may still
move. State the versions you read against in the assessment.

## Phase 1 — Assess

### 1. Direction first

Ask the codebase, not the user: does this system **send** events to Queuey,
**receive** webhooks that Queuey delivers, or both? A receiver needs signature
verification and a local-dev tunnel (`queuey listen`), not a publishing
strategy. Look for inbound webhook handlers, signature-verification code,
`X-Queuey-*` headers being read, or a queue whose destination is this service.
Both is common — an integration service that receives from one system and
publishes to another — and each side gets its own answer.

### 2. Gather evidence

Read `references/evidence.md` and collect the signals it lists before forming
an opinion. The three that decide almost everything:

- **Does local disk survive a restart?** A persistent volume, a systemd
  service on a box with a real disk, a VM — yes. A container with no volume
  mount, scale-to-zero compute, serverless — no.
- **Is the network to Queuey a given?** A datacenter or cloud region — usually
  yes. A factory floor, a vehicle, a kiosk, a ship, LTE, a corporate proxy that
  drops connections — no.
- **Does the codebase already own a reliability mechanism?** An outbox table,
  MassTransit / NServiceBus / Rebus, a broker it already publishes through,
  Hangfire or a job queue with persistence. If so, that mechanism is where
  Queuey publishing belongs, and adding Edge would duplicate it.

Record each signal with the file path that shows it. An assessment without
paths is an opinion.

### 3. Decide

Apply `references/decision.md`. It is short: the direction fork, a two-by-two
on disk durability versus network reliability, and the overrides for existing
infrastructure. The outcomes are:

| Outcome | Reference |
| --- | --- |
| Edge, in-process in a .NET host | `references/outcomes/edge-inprocess-dotnet.md` |
| Edge as a daemon, any language on the same machine | `references/outcomes/edge-daemon-any-language.md` |
| Direct to cloud ingress over HTTP, any language | `references/outcomes/direct-http.md` |
| Direct to cloud with the .NET SDK | `references/outcomes/direct-dotnet-sdk.md` |
| Receive Queuey deliveries | `references/outcomes/receive.md` |
| Outbox in the project's own database, then direct | `references/outcomes/outbox-own-database.md` |

The last one is the honest answer for ephemeral compute with an unreliable
path out: Queuey has no shipped store for that case, and pretending Edge covers
it would leave events in a spool on a disk that disappears. Recommend the
outbox, and say plainly that the durable part is theirs.

### 4. Write the assessment

Use this shape every time, so a reviewer can scan it and disagree precisely:

```markdown
## Queuey integration assessment

**Direction:** sending | receiving | both
**Recommendation:** <outcome name>
**Read against:** Queuey.Client <version> · docs fetched <date>

### Why
- <signal> — `<path>`
- <signal> — `<path>`
- <signal> — `<path>`

### What I ruled out
- <outcome> — because <evidence>

### Needs a human
- <account-level action, or a question only they can answer>

### Proposed changes (phase 2 — nothing written yet)
- <file or command, one line each>
```

If the evidence is thin — a repo with no deploy manifests and no hints about
where it runs — say so and ask the one or two questions that would settle it,
rather than guessing. The two questions are usually "where does this process
run?" and "what happens today if the network is down for an hour?".

## Phase 2 — Propose

Turn the recommendation into a concrete plan: files to add or change,
packages with versions, the queue declarations that will go into
`queuey.deploy.json`, the environment variables the app will read, and the
verification step that will prove it works. List separately every action that
needs a human, with the exact command.

Then stop and ask for go. Do not treat silence, or a general "set up Queuey",
as go for phase 3 — that authorised the assessment, not the changes.

## Phase 3 — Implement

Follow the outcome file for the chosen path. Whatever the path:

- **Declare queues in `queuey.deploy.json`** and publish to those names. Queue
  names are URL segments (`/events/{workspace}/{queue}`), so lowercase letters,
  digits, `.`, `-` and `_` only. Edge does not check that a queue exists at
  publish time, on purpose; a typo parks at transfer as `RequiresAction`, so
  the declaration is what catches it.
- **Set `EventType` and `GroupKey`** on every publish where the outcome allows.
  The type is what filtering and the console read; the group key is the
  ordering lane.
- **Use an idempotency key derived from the event**, not a fresh GUID, so the
  caller's own retry is a no-op rather than a duplicate.
- **Leave a decision note.** Copy `assets/decision-note.md` into the repo's
  docs (or wherever architecture notes live), filled in from the assessment.
  Six months from now, the person who finds a `Queuey.Edge` reference will want
  to know why it is there — or why it is not.
- **Verify.** Each outcome file ends with the check that proves the path works
  against a real or local Queuey. Run it; paste the result.

Never `git add -A`; stage the files you created. Never commit `queuey.json`.

## When Queuey is the wrong answer

Say so. A service that already publishes through a broker it will keep, a
system whose consumers are internal and synchronous, a one-off script — these
do not need Queuey, and an assessment that ends in "no change" is a successful
assessment. The person reading it will trust the next one more.
