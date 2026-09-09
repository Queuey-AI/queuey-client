# Queuey.Edge.Mqtt

An MQTT intake for [Queuey Edge](https://www.nuget.org/packages/Queuey.Edge):
subscribe to topics on a local broker and hand every message to the durable
Edge spool before acknowledging it.

```csharp
builder.Services.AddQueueyEdge(o => { o.ApiKey = "qak_…"; o.TenantPublicId = "ten_…"; });

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

Options are validated at composition, where the operator is looking, rather
than on the first message hours later. A route without a queue, or a source
without routes, fails the host build.

The client id defaults to `queuey-edge-{machine}`, so a persistent broker
session survives restarts. Reconnects use a doubling delay from one second up
to a one-minute cap.

## More

The Edge contract and operations runbook are in the
[Queuey.Edge readme](https://github.com/Queuey-AI/queuey-client/blob/main/src/Queuey.Edge/README.md).

MIT licensed.
