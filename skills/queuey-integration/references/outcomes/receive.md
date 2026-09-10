# Outcome: receive Queuey deliveries

**Chosen when:** the codebase has, or needs, an HTTP endpoint that Queuey
delivers events to. None of the publishing decision applies; the questions
are authenticity, idempotency and local development.

## Shape

1. **Verify every delivery** when the queue signs its requests:
   - Read the signature headers (`X-Queuey-Signature`, `X-Queuey-Timestamp`,
     `X-Queuey-Nonce`, `X-Queuey-Key-Id`); reject if any are missing.
   - Reject stale timestamps (a few minutes of clock skew).
   - Hash the **raw body bytes** — never a re-serialized object. Frameworks
     that parse JSON before the handler runs break verification; capture the
     raw body (a `rawBody` hook, `express.raw`, reading the stream yourself).
   - Rebuild the canonical string and HMAC-SHA256 it with the shared secret;
     compare in constant time.
2. **Handle idempotently.** Queuey delivers at-least-once; key the handler on
   `X-Queuey-Event-Id` and make a repeat a no-op.
3. **Respond fast with a 2xx** and do the work after acknowledging, or keep the
   work inside the timeout the queue's policy allows.
4. **If this service also publishes to Queuey**, read the loop-prevention page
   and forward `X-Queuey-Path` as described, so a Queuey-to-Queuey chain cannot
   cycle.

## Local development

Receive real deliveries on a laptop without exposing a port:

```bash
queuey listen --forward-to http://localhost:5000/webhooks/queuey --queue que_…
```

It opens an outbound push session and replays each delivery to the local URL
with the original method, path, headers and body. `--tee` also delivers to the
real endpoint; a tenant-wide redirect asks for confirmation.

## Declaring the destination

The queue's endpoint, auth and signing live in `queuey.deploy.json`, with the
secret stored by name via `queuey credentials set --from-env` (a human runs
that; it reads the value from the environment so it never lands in shell
history). The file is committed; the secret is not.

## Verify

`queuey listen` shows a delivery arriving; the handler accepts it, rejects a
tampered copy, and treats a replayed copy as a no-op. `queuey replay <event-id>
--queue …` replays one existing event for that test.

## Read

- https://app.queuey.ai/docs/how-to/verify-deliveries
- https://app.queuey.ai/docs/how-to/signed-requests
- https://app.queuey.ai/docs/reference/headers (delivery headers, names that
  can never be forwarded)
- https://app.queuey.ai/docs/how-to/loop-prevention
- https://app.queuey.ai/docs/how-to/reliable-delivery (what retries and the
  DLQ look like from the receiver's side)
