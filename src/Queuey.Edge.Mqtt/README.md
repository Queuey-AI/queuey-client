# Queuey.Edge.Mqtt

An MQTT intake for [Queuey Edge](https://www.nuget.org/packages/Queuey.Edge):
subscribe to topics on a local broker and hand every message to the durable
Edge spool before acknowledging it.

```csharp
builder.Services.AddQueueyEdge(o => o.UseEnvironmentVariables()); // the signing pair from `queuey keys mint --write`, and QUEUEY_TENANT

builder.Services.AddQueueyEdgeMqttSource(m =>
{
    m.Server = "localhost";
    m.Routes.Add(new MqttRoute
    {
        TopicFilter     = "plant/+/alarms",
        Queue           = "alarms",
        GroupKeySegment = 1          // one FIFO lane per machine
    });
});
```

## What it is, and is not

This is an **intake adapter, not a bridge**. There is no transformation, no
fan-out and no delivery here. The broker is typically on the same gateway —
Mosquitto, NanoMQ, the PLC vendor's broker. The only thing Edge adds is that a
message the broker handed over is durably ours before it is acknowledged, so a
power cut between receive and ack cannot lose it.

Delivery from there is `Queuey.Edge`'s problem: retries, backoff, reconnect,
lost-ACK resolution, idempotent resend, restart recovery and backlog draining.

It lives in its own package so `Queuey.Edge` stays dependency-light. Only hosts
that actually speak MQTT pay for `MQTTnet`.

## Routes

Each route maps one topic filter to one queue. `+` and `#` wildcards are
allowed.

| Field | Meaning |
| --- | --- |
| `TopicFilter` | The subscription. Required. |
| `Queue` | The Queuey queue the messages publish to. Required. |
| `GroupKeySegment` | Zero-based topic segment to use as the ordering lane. Null uses the queue's default lane. |
| `EventType` | Recorded event type. Null uses the full topic. |
| `DefaultContentType` | Content type for payloads that arrive without one. Defaults to JSON. |
| `IdempotencyKeyField` | Path to the producer's event id in a JSON payload (`eventId`, `meta.id`). Null turns deduplication on the id off. See below. |

Options are validated at composition, where the operator is looking, rather
than on the first message hours later. A route without a queue, or a source
without routes, fails the host build.

The client id defaults to `queuey-edge-{machine}`, so a persistent broker
session survives restarts. Reconnects use a doubling delay from one second up
to a one-minute cap.

## When the spool is full

A message is acknowledged to the broker only after the spool has it on disk.
When the spool cannot take one (full, or faulted), the source does not move on
to the next message. It **steps back**:

1. The refused message, and everything after it, stays unacknowledged.
2. The source disconnects from the broker. The session is persistent, so the
   broker keeps the unacknowledged messages and queues what is published meanwhile.
3. Once the spool has room for the refused message, the source reconnects. The
   broker re-sends what was left unacknowledged, refused message first.

MQTT re-sends an unacknowledged message only on a reconnect, so a source that
stayed connected would strand the refused message until something else broke
the connection, and let later messages in the same lane overtake it.

While the source stands back, the backlog lives on the broker, and the broker's
limits apply. Size them for the outage you plan for; Mosquitto's defaults, for
example, queue 1000 messages per session and then drop. The
[operations notes](https://github.com/Queuey-AI/queuey-client/blob/main/docs/edge-operations.md#the-mqtt-broker-in-front-of-edge)
have a configuration.

Two cases are acknowledged without being accepted, because no retry could ever
accept them, and each is logged as an error: a payload over the local
`MaxPayloadBytes`, and one larger than the whole spool. A QoS 0 message the
spool cannot take is gone, since QoS 0 has no redelivery; it is counted as
`lost`. Devices should publish at QoS 1.

## Deduplication on the producer's event id

MQTT has no message identity, so a message can reach Edge twice before Edge
has a say: a device re-sends at QoS 1 because its acknowledgement was lost and
the broker delivers both, or the broker re-sends a message because Edge
crashed after writing it to the spool and before acknowledging it. Edge's own
transfer identity is minted when it accepts a message, so each copy would
become an event.

When the devices put a stable event id in their JSON payload, name the field:

```csharp
m.Routes.Add(new MqttRoute
{
    TopicFilter         = "site/+/telemetry",
    Queue               = "telemetry",
    GroupKeySegment     = 1,
    IdempotencyKeyField = "eventId"   // or "meta.id" for a nested field
});
```

The daemon takes the same field for every route: `queuey edge run --mqtt-id-field eventId`,
or `QUEUEY_MQTT_ID_FIELD=eventId`.

The event's idempotency key is then the id plus the first 16 hex characters of
the SHA-256 of the payload: `node-07-000123:3f9a…`. The spool and Queuey Cloud
deduplicate on it, so a copy with the same id and the same bytes is
acknowledged to the broker as a **duplicate** and is not a new event. The hash
is there because Cloud's deduplication is permanent: a device that reuses an id
(a counter that restarts at 1 after a reboot, say) sends new content under it,
and that is a new event, not a repeat to swallow.

- The id is a JSON string or number; a number is used as it was written. An id
  that is longer than 128 characters, or that has characters outside visible
  ASCII, is replaced by `h` and 32 hex characters of its own SHA-256, because
  the key travels as an HTTP header.
- The id must be unique per queue. Two devices that send the same id with
  byte-identical payloads to the same queue become one event; a payload that
  names its device, or an id that does, avoids that.
- A message without a usable id (the field is missing, null, an object, an
  array or a boolean, or the payload is not JSON) is taken as before, as its
  own event. It is counted under `withoutId`, and the first one is logged as a
  warning; it never holds up the intake.
- Edge itself recognises a repeat while the original is still in its spool:
  waiting to transfer, or for `SettledRetention` (24 hours) after. A later
  repeat is sent, and Queuey Cloud answers that it already has the event.

## Retained messages

A broker re-sends every retained message on every subscribe, and the source
subscribes on every connect. `RetainedMessages` (CLI `--mqtt-retained`) decides
what happens to those copies; live messages are never affected.

| Value | Retained copies |
| --- | --- |
| `FirstSubscribe` (default, `first`) | Taken only on a new broker session: the first start, or after the broker lost the session. Reconnects never repeat them. A route added later starts from its next live message. |
| `Skip` (`skip`) | Never taken; acknowledged and counted. |
| `All` (`all`) | Taken on every subscribe. Each reconnect repeats them as new events. |

A retained copy the source meant to take but left unacknowledged (the spool was
full) is taken when the broker sends it again, even on a resumed session.

## Health

The source registers with the node's health: `IQueueyEdgeHealth.Snapshot().Intakes`,
the `queuey.edge.intake.*` instruments on the `Queuey.Edge` meter, `GET /health` on
the daemon's local endpoint, and the health report to Cloud. Each message is
counted once by outcome (`accepted`, `duplicate`, `deferred`, `rejected`,
`unrouted`, `retained_skipped`, `lost`), and `queuey.edge.intake.without_id`
counts, across those, the messages that had no event id to deduplicate on. Alert on
`queuey.edge.intake.connected` = 0: a broker that went away leaves an empty spool,
which looks exactly like a quiet, healthy node.

## More

The Edge contract and operations runbook are in the
[Queuey.Edge readme](https://github.com/Queuey-AI/queuey-client/blob/main/src/Queuey.Edge/README.md).

MIT licensed.
