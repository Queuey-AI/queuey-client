# Outcome: direct to cloud ingress over HTTP, any language

**Chosen when:** the network to Queuey is a given (or the durable step is
already handled by an outbox, bus or job queue the project owns), and the
codebase is not .NET — or is .NET but wants no SDK dependency.

**What the caller gets:** `202 Accepted` with an `eventId` the instant Queuey
has the event; delivery is asynchronous after that. What the caller does *not*
get is durability before the 202. If the process dies between "it happened" and
the POST, the event is gone — which is why this outcome is only chosen when
that window is acceptable or already covered.

## Shape

The request, from the published quickstart:

```
POST {INGRESS_BASE}/events/{tenantPublicId}/{queueName}
X-Api-Key: qak_…
Content-Type: application/json

{ "eventType": "customer.created", "payload": { … } }
```

`{INGRESS_BASE}` is `https://ingress.queuey.ai` for production. The tenant id
(the API's name for a workspace) and the queue name come from the console; the
queue must exist — declare it in `queuey.deploy.json` and apply with the CLI.

Set the ordering lane and, where the docs describe it, the idempotency key
through the `X-Queuey-*` request headers listed in the headers reference. Read
that page for the exact names rather than recalling them; the reference is the
contract.

## Around the POST

- Treat any status other than `202` as failure the caller sees. `4xx` is
  yours to fix (bad key, unknown queue, invalid body); do not retry it.
- Put a **bounded** retry with backoff around network errors and `5xx`, with
  an idempotency key derived from the event so a retry cannot become a
  duplicate. Bounded, because the honest contract here is "fail loudly", not
  "hope forever".
- Never log the API key. Read it from the environment or the project's secret
  store; never from a tracked file.

## Verify

Send one event; the response carries `eventId`; the console shows it moving
Received → InProgress → Delivered. Then send one with a deliberately wrong
queue name and confirm your code surfaces the `404`.

## Read

- https://app.queuey.ai/docs/quickstart (curl, TypeScript, C#)
- https://app.queuey.ai/docs/reference/headers (every ingress header and
  status code)
- https://app.queuey.ai/docs/how-to/signed-requests (HMAC instead of an API
  key — `queuey keys mint` issues the signing key, and needs a human)
- https://app.queuey.ai/docs/how-to/ordering
