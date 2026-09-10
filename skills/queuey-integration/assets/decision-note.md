# Queuey integration — decision note

_Written by the queuey-integration skill on <date>. Keep it next to the other
architecture notes; update it when a premise below stops being true._

## Decision

**<outcome name>** — e.g. "Edge, in-process" / "direct with the SDK, from the
outbox drainer" / "receive only".

## Premises this rests on

| Premise | Evidence |
| --- | --- |
| Local disk <durable / ephemeral> | `<path>` |
| Network to Queuey <reliable / unreliable> | `<path or statement>` |
| Existing reliability mechanism: <none / outbox / bus / job queue> | `<path>` |
| Direction: <sending / receiving / both> | `<path>` |

## What was ruled out, and why

- <outcome> — <one line>

## What would change this decision

- The process moves to <different host / gains or loses a volume>.
- An outbox or bus is introduced or removed.
- The network path changes (new site, proxy, connectivity).

## Versions read against

Queuey.Client <version> · Queuey.Edge <version or n/a> · docs fetched <date>
