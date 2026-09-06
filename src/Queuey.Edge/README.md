# Queuey.Edge

Durable local publishing for [Queuey](https://queuey.ai). One call transfers
the operational delivery problem to Queuey:

```csharp
await queuey.PublishAsync("temperature.updated", payload);
```

When that call returns, the event is **committed to a local durable store on
this machine** (SQLite, fsync'd) under a permanent transfer identity — and
Queuey owns the delivery mechanics from there: transfer to Queuey Cloud,
retries, backoff, reconnect, lost-ACK resolution, idempotent resend, recovery
across process and machine restarts, and backlog draining. Kill the network,
kill Queuey, kill your own process — accepted events survive on disk and
drain, in order per lane, when the world comes back.

Your application never contains delivery code:

```csharp
// ❌ what Queuey.Edge exists to delete from your codebase
if (!await queuey.IsOnline()) { await localDb.Save(evt); ScheduleRetry(...); }

// ✅ the whole delivery story
await queuey.PublishAsync("temperature.updated", payload);
```

## Quick start

```csharp
// Program.cs (any .NET host — worker service, ASP.NET, console)
builder.Services.AddQueueyEdge(o =>
{
    o.ApiKey = "qak_...";          // a PUBLISH-ONLY, tenant-scoped Edge key
    o.TenantPublicId = "ten_...";
    // Optional: o.IngressBaseAddress = new Uri("http://localhost:5084");
    // Optional: o.Storage.Path = "/var/lib/myapp/queuey/spool.db";
});
```

```csharp
// Anywhere in your app
public sealed class SensorService(IQueueyPublisher queuey)
{
    public async Task OnReading(Reading reading) =>
        await queuey.PublishAsync("sensor-readings", reading, new PublishOptions
        {
            EventType = "temperature.updated",
            GroupKey = reading.DeviceId,          // ordering lane
            OccurredAtUtc = reading.ReadAtUtc     // honest history when a backlog drains
        });
}
```

That's the entire integration. A runnable end-to-end example (including the
kill-Queuey-and-watch-it-recover demo script) lives in
[`samples/Queuey.Edge.Demo`](https://github.com/Queuey-AI/queuey-client/tree/main/samples/Queuey.Edge.Demo).

## The contract, precisely

**On successful return:** the event is durably persisted locally (committed +
fsync'd before the call returns) with a stable transfer identity that Queuey
Cloud deduplicates on **permanently** — a retry, however late, can never
become a second logical event. Queuey retains the event and keeps
transferring until Cloud accepts it or an operator explicitly discards it.
**Age alone never deletes an accepted event.**

**Not claimed:** that Cloud has the event yet (transfer is asynchronous);
that the event survives destruction of this machine's storage; that it has
reached your final destination (that is Queuey Cloud's delivery lifecycle,
visible in the console).

**`PublishAsync` throws only for conditions that exist before Queuey takes
responsibility:**

| Exception | Meaning |
| --- | --- |
| `QueueyConfigurationException` | missing config, missing queue name |
| `QueueyPayloadRejectedException` | payload not serializable, or over the local `MaxPayloadBytes` |
| `QueueySpoolFullException` | the local store hit its limit — the honest backpressure of lossless retention |
| `QueueyStorageFaultedException` | the store is corrupt; halted until explicit operator recovery |

Network state, Queuey Cloud availability and HTTP errors **never** surface at
the call site — handling them is Queuey's job.

## Health: Queuey exposes, you monitor

Edge publishes an OpenTelemetry meter (`Queuey.Edge`) and an in-process
snapshot (`IQueueyEdgeHealth`). If you wire exactly one alert, use
**`queuey.edge.spool.oldest_age_seconds`** — it grows when *anything* has
been stopping transfer (network, Cloud, credentials, a paused queue), and
Edge resumes by itself when the cause clears.

| Signal | Meaning |
| --- | --- |
| `spool.pending` | accepted locally, not yet transferred |
| `spool.oldest_age_seconds` | age of the oldest pending event — **the** alert signal |
| `spool.quarantined` | events Cloud permanently rejected, awaiting operator retry/discard |
| `cloud.last_contact_seconds` | seconds since the last successful transfer |
| `state` | 0 Healthy · 1 Backlogged · 2 RequiresAction · 3 StorageFull · 4 StorageFaulted |
| `transfer.accepted{replayed}` / `transfer.failed{class,reason}` | throughput and diagnosis |

## No .NET app? Shell, Python, cron — the IoT path

The spool file is the local contract, and SQLite (WAL) lets multiple
processes share it safely. So on a Linux box / IoT gateway you can run Edge
as a **standalone daemon** and publish durably from *anything*:

```bash
# once, e.g. as a systemd service:
queuey edge run --spool /var/lib/queuey/spool.db \
  --tenant ten_... --api-key qak_...          # publish-only, workspace-scoped key

# from any program on the machine (bash, Python, cron, a C binary):
queuey edge publish sensor-readings \
  --spool /var/lib/queuey/spool.db --tenant ten_... \
  --data '{"temp":21.5}' \
  --event-type temperature.updated --group-key unit-7 \
  --occurred-at "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
```

`publish` returns after the **durable local commit** — same contract as the
in-process `PublishAsync`. No daemon running? The event still sits durably
and drains when one next starts. Microcontroller fleets that can't run Edge
publish over the LAN to one gateway that does.

### TypeScript, Java, Python — the same one-liner, now durable

Add `--listen 7311` to `edge run` and the daemon serves a **loopback**
endpoint with the *same wire shape as cloud ingress*. Any language's plain
HTTP publish becomes durable by swapping the base URL — the 202 answers
only after the fsync'd local commit, and the daemon owns retries, offline
buffering and reconnect from there. The thin client carries **zero**
reliability logic:

```ts
// TypeScript — identical to the cloud quickstart, base URL swapped:
await fetch(`http://localhost:7311/events/${tenant}/sensor-readings`, {
  method: "POST",
  headers: { "Content-Type": "application/json", "X-Queuey-Group-Key": "unit-7" },
  body: JSON.stringify(reading),
}); // 202 = Queuey has it durably, even with the internet cable pulled
```

```python
# Python
requests.post(f"http://localhost:7311/events/{tenant}/sensor-readings",
              json=reading, headers={"X-Queuey-Group-Key": "unit-7"})
```

Failure semantics stay honest: connection refused = the daemon is down and
Queuey did **not** take custody (run it under systemd with
`Restart=always`); `507` = spool full; `503` = storage faulted. A `GET
/health` on the same port serves the health snapshot for local probes.

## Operator verbs

The `queuey` CLI ([Queuey.Cli](https://www.nuget.org/packages/Queuey.Cli))
operates the spool file directly, alongside a running host:

```bash
queuey edge status  --spool <path> [--json]
queuey edge retry   --spool <path> (--id N | --all)   # after fixing a quarantine cause
queuey edge discard --spool <path> --id N             # explicit, logged operator decision
queuey edge recover --spool <path>                    # salvage a faulted spool, reports unreadable count
queuey edge reset   --spool <path> --accept-data-loss # start clean; the old file is preserved
```

## Storage rules (the short version)

- **Local durable disk only** — never a network share; in containers, a
  persistent volume (a temp/overlay path is detected and warned about).
- **Exclude `spool.db*` from antivirus and file-copy backups** — copying a
  WAL-mode SQLite database mid-write produces a corrupt copy.
- Size `Storage.MaxSpoolBytes` (default 512 MB) from your rate: at
  1 event/minute × 1 KB that is roughly **a year** of offline autonomy.
  A spool that hit the limit accepts again the moment its backlog has
  transferred — the limit counts live events, not the file's size.

## Semantics worth knowing

- **Exactly-once, logically:** at-least-once transfer + a permanent identity
  reservation at Cloud = one logical event, even for a lost-ACK resend weeks
  later.
- **Ordering:** strict FIFO per lane (`queue`, or `queue`+`GroupKey`) up
  to the point Queuey Cloud receives the event; at most one in-flight
  transfer per lane, and no ordering across lanes. Delivery order from
  Cloud to your destination follows the queue's own ordering policy
  (`ordering: fifo` keeps it; `besteffort` does not). A transient failure
  at the head *delays* its lane (that's FIFO); an event Cloud permanently
  rejects is **quarantined and steps aside** so one poisoned payload never
  freezes the stream — and `queuey edge retry` puts it back at the head of
  its lane, ahead of anything accepted after it.
- **Throttling:** none of Edge's own. Transfers run with bounded
  concurrency across lanes, back off with decorrelated jitter, probe with a
  single event after a failure, and wait at least Cloud's `Retry-After`
  (plus a little jitter, so a fleet that went dark together does not knock
  again in lockstep).
- **Honest history:** pass `PublishOptions.OccurredAtUtc` and a backlog
  drained on Wednesday still reads as Monday in the Queuey console.
- **Never silent loss:** events leave the spool only via Cloud custody,
  explicit operator discard, or an explicitly configured lossy policy.

## Requirements

- .NET 8.0+
- A Queuey queue and a **publish-only, tenant-scoped** API key (console →
  Developer → API keys). Keys on edge machines should never carry more.

MIT licensed. Docs: [queuey.ai docs](https://app.queuey.ai/docs) · issues:
[github.com/Queuey-AI/queuey-client](https://github.com/Queuey-AI/queuey-client).
