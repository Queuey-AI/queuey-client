# Queuey Edge demo

A "sensor" publishing one temperature reading per interval — and the entire
delivery story in application code is **one call**:

```csharp
await queuey.PublishAsync("sensor-readings", reading, options);
```

No retries, no buffering, no connectivity checks, no restart recovery.
Kill Queuey, pull the network, kill this process: accepted events survive on
disk and drain, in order, when the world comes back. That is the product.

## Run it

Create a queue (e.g. `sensor-readings`) and a **publish-only, tenant-scoped**
API key for the tenant, then:

```bash
export QUEUEY_API_KEY="qak_..."          # publish-only Edge key
export QUEUEY_TENANT="ten_..."
export QUEUEY_INGRESS_BASE="http://localhost:5084"   # omit for production
# optional: QUEUEY_DEMO_QUEUE (default sensor-readings), QUEUEY_DEMO_INTERVAL (default 5s)

dotnet run --project samples/Queuey.Edge.Demo
```

The dashboard shows the same `IQueueyEdgeHealth` snapshot any monitoring
system would read:

```text
[Healthy] pending=0 oldest=- quarantined=0 cloud-contact=3s ago last-failure=-
```

## The demo script

1. **Start the demo.** Readings flow; `pending` stays at 0.
2. **Kill Queuey** (stop the ingress host) or disconnect the network.
   The state flips to `Backlogged`; `pending` climbs; the application code
   neither notices nor cares. Events are on disk (`queuey-edge/spool.db`
   next to the binary — inspect with `queuey edge status --spool <path>`).
3. **Kill the demo process too**, if you like. Restart it. The backlog is
   still there; publishing continues from where it left off.
4. **Restart Queuey.** Watch the backlog drain to 0 — oldest first, in
   order, each event carrying its original `occurred_at`, so the history in
   the console is *honest*: Monday's readings delivered Wednesday still say
   Monday.
5. In the Queuey console, note the drained events' receive time vs occurred
   time — and that events published during the outage appear exactly once.

## Chaos variations

- **Lost ACK:** hard-kill the demo mid-transfer (`kill -9`); on restart the
  event is re-sent with the same transfer identity and Cloud answers
  `replayed: true` — one logical event, never two.
- **Wrong credentials:** set a bad `QUEUEY_API_KEY`; the state becomes
  `RequiresAction (AuthenticationRejected)` and events are *retained*,
  probing slowly. Fix the key, restart, and everything drains. Nothing was
  lost, nothing hot-looped.
- **Storage pressure:** set a tiny spool in code (`Storage.MaxSpoolBytes`)
  and watch publishes refuse honestly with `QueueySpoolFullException` when
  the disk budget is spent — the backpressure of lossless retention.

## Operator verbs

```bash
queuey edge status  --spool <path> [--json]
queuey edge retry   --spool <path> (--id N | --all)
queuey edge discard --spool <path> --id N
queuey edge recover --spool <path>
queuey edge reset   --spool <path> --accept-data-loss
```
