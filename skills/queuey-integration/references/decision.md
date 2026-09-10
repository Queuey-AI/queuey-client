# Deciding the path

Three steps, in order. Stop at the first that settles it.

## 1. Direction

| The codebase… | Direction | Outcome |
| --- | --- | --- |
| calls out to Queuey ingress, or wants to | sending | continue to step 2 |
| has (or needs) an HTTP endpoint that Queuey delivers to | receiving | `outcomes/receive.md` |
| both — typical for an integration or gateway service | both | assess each side separately; they rarely share an answer |

A receiver's questions are signature verification, idempotent handling and a
local-dev tunnel. None of the publishing logic below applies to it.

## 2. The two-by-two (sending)

Two facts about the process that will publish:

- **Disk:** does a file written before a restart exist after it? Persistent
  volume, VM disk, a systemd service on a physical box — **durable**. Container
  without a volume mount, scale-to-zero, serverless, a CI job — **ephemeral**.
- **Network:** is the route to `ingress.queuey.ai` (or a self-hosted ingress) a
  given during normal operation? Cloud region, datacenter — **reliable**. Factory,
  vehicle, ship, kiosk, LTE, satellite, a corporate egress proxy that drops
  connections — **unreliable**.

|  | Network reliable | Network unreliable |
| --- | --- | --- |
| **Disk durable** | Direct. Edge would add a spool nobody needs to operate. | **Edge.** In-process for a .NET host; the daemon for anything else on the box. |
| **Disk ephemeral** | Direct. A spool on this disk would only look durable. | **Outbox in the project's own database, then direct.** Queuey has no shipped store for this cell. |

"Direct" means `outcomes/direct-dotnet-sdk.md` for a .NET codebase and
`outcomes/direct-http.md` for everything else. Direct is not "fire and forget":
the outcome files put a bounded retry around the POST and treat a non-202 as an
error the caller sees — because that is the honest contract when there is no
local store.

## 3. Overrides — existing infrastructure wins

Check these before recommending anything from the table. Each one changes the
answer regardless of the cell.

**An outbox already exists.** A table written in the same transaction as the
business change, drained by a worker. Publish to Queuey *from the worker*, with
direct HTTP or the SDK. Do not add Edge: the outbox is already the durable
accept, and two stores with two retry loops is the failure mode you were asked
to prevent.

**A message bus or broker already carries the events.** MassTransit, NServiceBus,
Rebus, Wolverine, a Kafka or RabbitMQ producer, Azure Service Bus. Queuey
publishing belongs in a consumer of that bus, so the bus's own delivery
guarantees carry the event to the point of the POST. Say explicitly that the
bus stays.

**A persistent job queue with retries already exists.** Hangfire with SQL
storage, Quartz with a persistent store, Sidekiq, Celery with a durable broker.
Enqueue the publish as a job. Same reasoning.

**The publish is transactional with a database write** but there is no outbox
yet, and the disk is ephemeral. This is the outbox cell above — recommend
adding the outbox, not Edge, and say the durable part is theirs to own.

**The machine already runs an MQTT broker** (Mosquitto, NanoMQ, a PLC vendor's
broker) and devices publish to it. Do not rewrite the devices: run Edge with
`Queuey.Edge.Mqtt` and subscribe, so the broker's acknowledgement happens only
after the fsync'd local commit. See `outcomes/edge-daemon-any-language.md`.

## Signals that should make you pause

- **A spool path on a network share.** SQLite locking over SMB/NFS is
  unreliable; Edge must have a local disk. If the only durable disk is remote,
  it is the outbox cell.
- **Several replicas of the same service.** Edge is per machine; each replica
  gets its own spool and its own node identity. That is fine for a fleet of
  gateways and wrong for a horizontally scaled API — the latter is the direct
  or outbox cell.
- **"We already retry."** Ask where the event lives *between* retries. In
  memory means it does not survive a crash; that is not a reliability mechanism,
  and the override does not apply.
- **A .NET Framework or `netstandard2.0` consumer.** `Queuey.Client` and
  `Queuey.Client.Waas` target `netstandard2.0`; `Queuey.Edge` is `net8.0` only.
  Edge for such a host means the daemon, not in-process.

## What would change the answer

Write these into the assessment so the reader can check your premises: the
process moving to a different host, a volume being added or removed, an outbox
being introduced, or the network path changing. The decision is only as durable
as the facts it rests on.
