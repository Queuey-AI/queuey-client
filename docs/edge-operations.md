# Queuey Edge — operations notes

## Linux IoT runbook — from zero to durable telemetry

What it takes on a Pi-class device or industrial gateway, end to end:

**1. Queuey side (once, in the console):** create a workspace + queue, then
mint an API key with *Limit to ingress* + scoped to that one workspace —
the key will live on a machine you don't control, and must not be able to
do anything else.

**2. Get the binary onto the device.** One self-contained file, no .NET
runtime needed on the device:

```bash
# on your build machine (linux-arm64 for Pi-class, linux-x64 for industrial PCs)
dotnet publish src/Queuey.Client.Cli -c Release -r linux-arm64 \
  --self-contained -p:PublishSingleFile=true
scp bin/Release/net8.0/linux-arm64/publish/queuey device:/opt/queuey/queuey
```

(On devices that have the SDK, `dotnet tool install -g Queuey.Cli --prerelease`
works instead; `--prerelease` is required while every version is a preview.)

**3. Run it under systemd** so the daemon survives reboots and restarts
itself — which is what makes "connection refused = daemon down" a
non-event:

```ini
# /etc/systemd/system/queuey-edge.service
[Unit]
Description=Queuey Edge
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/opt/queuey/queuey edge run --spool /var/lib/queuey/spool.db --listen 7311
EnvironmentFile=/etc/queuey/edge.env
Restart=always
RestartSec=2
User=queuey
StateDirectory=queuey

[Install]
WantedBy=multi-user.target
```

```bash
# /etc/queuey/edge.env  (chmod 600, owned by the queuey user)
QUEUEY_TENANT=ten_...
QUEUEY_API_KEY=qak_...
# QUEUEY_INGRESS_BASE=http://localhost:5084   # only for testing against a local Queuey
```

`sudo systemctl enable --now queuey-edge` — done. `StateDirectory` gives
the spool a durable home at `/var/lib/queuey` with the right ownership.

**4. Publish from whatever the device runs** — all three are the same
durable accept boundary:

```bash
# shell / cron / C binary
queuey edge publish sensor-readings --spool /var/lib/queuey/spool.db \
  --tenant ten_... --data "$(read_sensor)" --group-key unit-7
```

```python
# Python / TypeScript / Java — plain HTTP to the loopback endpoint
requests.post(f"http://localhost:7311/events/ten_.../sensor-readings", json=reading)
```

```csharp
// or a .NET app embeds Queuey.Edge directly (no daemon needed)
await queuey.PublishAsync("sensor-readings", reading);
```

**5. Wire one alert** — `queuey.edge.spool.oldest_age_seconds` (or poll
`GET localhost:7311/health` / `queuey edge status --json` from your
existing agent). Then pull the network cable and watch nothing break.

Queuey Edge makes `PublishAsync` mean: *the event is durably accepted on
this machine, and Queuey owns the delivery mechanics from here.* This page
is what an operator needs to know about the machine's side of that bargain.

## The boundary, in one line

**Queuey owns delivery behaviour. You own the environment Queuey runs in** —
the machine, the disk, the network, the credentials, and the decision to
watch the health signals Edge exposes.

## Sizing the spool (days of autonomy)

The spool limit (`Storage.MaxSpoolBytes`, default 512 MB) is the only
backpressure: age never deletes an accepted event, so when the spool is
full, `PublishAsync` refuses new events (draining continues). Size it from
your event rate:

| Events/day | Payload | Spool/day | Days of autonomy in 512 MB |
|---:|---:|---:|---:|
| 1 440 (1/min) | 1 KB | ~1.4 MB | ~1 year |
| 1 440 | 10 KB | ~14 MB | ~36 days |
| 17 280 (12/min) | 1 KB | ~17 MB | ~30 days |
| 86 400 (1/s) | 1 KB | ~85 MB | ~6 days |

(Plus modest per-row overhead. Round down; leave the 4 MB headroom alone —
it is what lets Edge keep *recording* successes at the limit.)

The limit counts **live** events. A transfer settles by blanking its payload
on the device, which frees that space for the next accept at once, so a spool
that filled up during an outage accepts again as soon as its backlog has
transferred — no retention window to wait out. The file itself shrinks in the
background (`queuey edge status` reports live bytes, not file size).

## Storage rules

- **Local durable disk only.** Never a network share (SQLite locking over
  SMB/NFS is unreliable). In containers, mount a persistent volume — a
  temp/overlay path is detected and warned about at startup and on the
  health flag `StorageDurabilityWarning`, but Edge will run: durability is
  your choice to make.
- **Exclude `spool.db*` from antivirus scanning and file-copy backups.**
  Copying a WAL-mode SQLite database mid-write produces a corrupt copy, and
  AV locks can stall accepts. The spool is not a backup target — Cloud is
  the system of record once events transfer.

## Data at rest on the device

The spool holds event payloads until Cloud has acknowledged them — on the
customer's own equipment, which is the most exposed copy of a payload Queuey
ever keeps. What Edge does about it, and what remains yours:

- **The payload leaves the spool at settle.** The moment Cloud acknowledges an
  event, its payload is blanked; the row stays for `SettledRetention` (24 h)
  with the transfer id and Cloud event id only, then the sweep removes it.
- **Freed pages are overwritten** (`PRAGMA secure_delete`), so a settled
  payload does not linger in unused pages of the file.
- **`spool.db` is created `0600` in a `0700` directory** on Linux/macOS; the
  WAL/SHM sidecars inherit the mode. Windows uses the directory's ACL.
- **Encryption at rest is opt-in**: set `QUEUEY_SPOOL_KEY` to a base64
  32-byte key (`openssl rand -base64 32`) and payloads are stored AES-256-GCM
  encrypted (`EdgeStorageOptions.PayloadKey` from code). Keep the key in the
  environment file or a secret store the device already has — a systemd
  credential, a TPM-backed keychain — never next to the spool. Rows written
  before the key was set keep draining; a row that cannot be opened with the
  configured key is quarantined (`PayloadUnreadable`), never sent, never
  dropped. Rotating the key means draining first.
- **What Edge cannot do for you**: a process that holds the key can read the
  spool, and a device without a secret store has nowhere safe to put one.
  Full-disk encryption (LUKS, BitLocker, the device vendor's option) is the
  baseline; the spool measures are defence in depth on top of it.
- **`spool.db.faulted-*` and `.salvage` files hold payloads.** `recover` and
  `reset` keep the previous file on purpose (it may contain events nothing
  else has). Once you have confirmed the recovery, delete them yourself —
  Edge never removes a file that might be the only copy of an event.

## The one metric worth alerting on

`queuey.edge.spool.oldest_age_seconds` (OpenTelemetry meter `Queuey.Edge`).
If it grows past your tolerance, something has been stopping transfer for
that long — network, Queuey Cloud, credentials, or a paused queue; the
`queuey.edge.state` gauge and `LastTransferFailure` say which. Edge resumes
by itself when the cause clears; the alert exists because *no one else can
page you about a machine only you can see*.

If the node reports to Cloud (`--report-health` / `Health.ReportToCloud`),
Cloud can page you about two things:

- **Silence**, for nodes you tick as *Expected* in the console's Edge nodes
  tab: three missed reports (never under 15 minutes) mark the node Silent
  and send one alert to the license's notification destinations, with one
  more when the node is back. *Alert after* on the node, or on many nodes
  at once, makes Cloud wait longer — up to 24 hours without a report — for
  a link that deserves patience. A link that flaps earns one pair per
  wait, and several nodes changing in the same minute arrive as one message.
- **What the node says about itself**, for every node that reports: cannot
  transfer (a paused queue, a bad key, an unknown route, billing, TLS), local
  store full or faulted, or a backlog whose oldest event is over 15 minutes
  old while the node is online. One alert when it starts, one when it clears.

Everything a node knows about itself reaches Cloud only while it can talk,
so the local metric above still matters for a machine you cannot reach at
all. *Forget* in the tab removes a node you took out of service.

A report is one small request every five minutes plus one on each state
change, never billed and never an event. Cloud caps a node at one report
per ten seconds and a license at a fleet's worth; beyond that it answers
429 with `Retry-After`, and the node waits as told, backing off from ten
seconds up to its report interval. A refused report is loss-free — the next
one carries the newer truth.

## StorageFaulted runbook

Symptoms: `EdgeState.StorageFaulted`, publishes throw
`QueueyStorageFaultedException`, transfer halted, critical log
`queuey.edge.storage_faulted`. Edge has detected corruption and preserved
the file untouched — it never silently starts a fresh spool.

1. `queuey edge status --spool <path>` — confirms the fault.
2. `queuey edge recover --spool <path>` — salvages every readable
   still-owned event into a fresh spool and reports **exactly how many were
   unreadable**. The faulted file is kept beside it (`*.faulted-<ts>`).
3. If the file is beyond reading:
   `queuey edge reset --accept-data-loss --spool <path>` — starts clean.
   The flag is the point: data loss is a human decision, taken in your
   name, never Queuey's default.
4. Either way, keep the preserved file for support.

Likely root causes: failing disk, AV/backup interference (see above), or a
copied-while-hot spool file.

## Quarantine

An event Cloud permanently rejects (payload too large, malformed, 415) is
**quarantined**: kept durably, taken out of the retry path, and its lane
continues — one poisoned reading never freezes a unit's stream. It exits
only by your hand: `queuey edge retry --id N` after fixing the cause, or
`queuey edge discard --id N` (logged, explicit). `RequiresAction` states
(401/403/404/402/paused) are different: those retain and probe
automatically, and recover on their own once you fix the cause.
