# Outcome: outbox in the project's own database, then direct

**Chosen when:** the disk is ephemeral (containers without volumes,
scale-to-zero, serverless) *and* the path to Queuey is not a given — or the
publish must be atomic with a database write and no outbox exists yet.

**Be plain about this:** Queuey does not ship a durable store for this cell.
`Queuey.Edge` keeps its spool in SQLite on local disk, and on an ephemeral disk
that spool only looks durable. The durable step here is the project's own
database, and the project owns it. Recommending Edge would be the wrong kind
of helpful.

## Shape

1. **Outbox table** in the same database as the business data, written in the
   **same transaction** as the change that produced the event: id, queue name,
   event type, group key, payload, `created_at`, `sent_at` (null until
   accepted).
2. **A drainer** — a background worker, a scheduled job, or a hosted service —
   selects unsent rows in `created_at` order per group key, POSTs each to
   Queuey (`direct-http.md` or `direct-dotnet-sdk.md`), and sets `sent_at` on
   `202`. Anything else leaves the row unsent for the next pass.
3. **Idempotency key = the outbox row id.** A drainer that crashes after the
   202 and before the update will resend; the key turns that into a no-op at
   Queuey.
4. **Ordering:** one in-flight POST per group key at a time if the queue's
   ordering matters; otherwise drain concurrently.
5. **Alert on the oldest unsent row's age.** Same signal Edge exposes as
   `spool.oldest_age_seconds`, for the same reason: it grows when anything
   stops the drain and falls by itself when the cause clears.

If the project already runs MassTransit, NServiceBus or a persistent job
queue, use *its* outbox or job store instead of writing a new one — that is the
override in `decision.md`, and it applies here too.

## What not to do

- Do not add `Queuey.Edge` "as well". Two stores with two retry loops is the
  failure mode this outcome prevents.
- Do not retry in memory and call it durable.
- Do not put the drainer's API key in the outbox table or in a tracked file.

## Verify

Block egress; create a few business changes; the outbox fills and the
business transactions still commit. Restore egress; rows drain in order per
group key and `sent_at` is set; the console shows each event once, even after
you kill the drainer between a 202 and its update.

## Read

- `direct-http.md` / `direct-dotnet-sdk.md` for the POST itself
- https://app.queuey.ai/docs/how-to/ordering
- https://app.queuey.ai/docs/how-to/reliable-delivery
