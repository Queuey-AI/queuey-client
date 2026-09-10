# Outcome: Edge, in-process in a .NET host

**Chosen when:** the publishing process is a .NET 8+ host (worker service,
ASP.NET, console under a supervisor) on a machine with a durable local disk,
and the network to Queuey is not a given.

**What the caller gets:** `PublishAsync` returns once the event is committed
and fsync'd to a local SQLite spool under a permanent transfer identity. From
there Queuey owns transfer, retries, reconnect, lost-ACK resolution, idempotent
resend and backlog draining. Network state and Queuey availability never
surface at the call site.

## Shape

```csharp
// Program.cs
builder.Services.AddQueueyEdge(o =>
{
    o.ApiKey = cfg["Queuey:ApiKey"];        // publish-only, workspace-scoped key
    o.TenantPublicId = cfg["Queuey:Tenant"];
    // Local durable disk. Never a network share (SQLite locking over SMB/NFS is
    // unreliable) and never container-ephemeral storage.
    o.Storage.Path = "/var/lib/<app>/queuey/spool.db";
});
```

```csharp
// Wherever the event happens
public sealed class SensorService(IQueueyPublisher queuey)
{
    public Task OnReading(Reading r) =>
        queuey.PublishAsync("sensor-readings", r, new PublishOptions
        {
            EventType      = "temperature.updated",
            GroupKey       = r.DeviceId,     // the ordering lane
            IdempotencyKey = r.SampleId,     // derived from the event, not a GUID
            OccurredAtUtc  = r.ReadAtUtc     // honest history when a backlog drains
        });
}
```

Package: `Queuey.Edge` (net8.0 only; pulls `Microsoft.Data.Sqlite`). Add with an
explicit preview version.

## Configuration and secrets

- The API key comes from configuration the project already secures (user
  secrets, environment, Key Vault). It must be a key that can only publish to
  its own workspace — an edge machine should never hold anything broader.
- The spool path lives under a directory the service owns, with the service
  account as owner. Exclude `spool.db*` from antivirus and file-copy backups;
  copying a WAL-mode SQLite database mid-write produces a corrupt copy.
- `Storage.MaxSpoolBytes` (default 512 MB) is the offline budget. At one 1 KB
  event per minute that is roughly a year.

## What `PublishAsync` can throw

Only conditions that exist before Queuey takes responsibility:
`QueueyConfigurationException`, `QueueyPayloadRejectedException`,
`QueueySpoolFullException` (the honest backpressure of lossless retention) and
`QueueyStorageFaultedException` (corrupt store, operator recovery required).
Handle the last two as operational alerts, not as retry-and-hope.

## Operating it

Wire one alert: `queuey.edge.spool.oldest_age_seconds` from the `Queuey.Edge`
OpenTelemetry meter. It grows when anything stops transfer and falls by itself
when the cause clears. Optionally `o.Health.ReportToCloud = true` so the node
appears under **Edge nodes** in the console.

The CLI operates the spool alongside the running host:

```bash
queuey edge status  --spool /var/lib/<app>/queuey/spool.db
queuey edge retry   --spool … --all        # after fixing a quarantine cause
```

## Verify

1. Publish one event with the network up; `queuey edge status` shows it
   transferred, and it appears in the console.
2. Block egress, publish a few, confirm they are pending and the process is
   healthy; restore egress, confirm they drain in order per group key.

The runnable version of this is `samples/Queuey.Edge.Demo` in the
queuey-client repository.

## Read

- https://app.queuey.ai/docs/how-to/edge
- `src/Queuey.Edge/README.md` in https://github.com/Queuey-AI/queuey-client
  (the precise contract, storage rules, semantics)
- `docs/edge-operations.md` in the same repository (spool sizing, runbooks)
