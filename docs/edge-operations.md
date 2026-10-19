# Queuey Edge — operations notes

## Linux IoT runbook — from zero to durable telemetry

What it takes on a Pi-class device or industrial gateway, end to end:

**1. Queuey side (once):** create a workspace + queue, then make the node's
signing key for that queue on your own machine — the key will live on a
machine you don't control, and reaches nothing but its queue:

```bash
queuey keys mint --queue sensor-readings --write edge.env   # the secret is never shown
```

Put it on the device as `/etc/queuey/edge.env`, owned by root and readable by
nobody else (systemd reads it as root before it starts the service as `queuey`,
so the service user never needs to read the file). Stream it straight into
place — it never lands in `/tmp` on the device — then delete your copy:

```bash
ssh device 'sudo install -d -m 700 -o root -g root /etc/queuey'
ssh device 'sudo install -m 600 -o root -g root /dev/stdin /etc/queuey/edge.env' < edge.env && rm edge.env
```

(A publish-only API key, *Limit to ingress* and scoped to the workspace, is the
alternative to the signing key, as `QUEUEY_API_KEY` in the same file.)

**2. Get the binary onto the device.** One self-contained file, no .NET
runtime needed on the device:

```bash
# on your build machine (linux-arm64 for Pi-class, linux-x64 for industrial PCs)
dotnet publish src/Queuey.Client.Cli -c Release -r linux-arm64 \
  --self-contained -p:PublishSingleFile=true
scp src/Queuey.Client.Cli/bin/Release/net8.0/linux-arm64/publish/Queuey.Client.Cli \
  device:/opt/queuey/queuey
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
After=network-online.target time-sync.target
Wants=network-online.target time-sync.target

[Service]
ExecStart=/opt/queuey/queuey edge run --spool /var/lib/queuey/spool.db --listen 7311
EnvironmentFile=/etc/queuey/edge.env
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/cache/queuey
Restart=always
RestartSec=2
User=queuey
StateDirectory=queuey
CacheDirectory=queuey

[Install]
WantedBy=multi-user.target
```

```bash
# /etc/queuey/edge.env  (root:root, 0600 — installed as above)
QUEUEY_TENANT=ten_...
QUEUEY_SIGNING_KEY_ID=hsk_...       # from queuey keys mint --write edge.env
QUEUEY_SIGNING_SECRET=...
# QUEUEY_EDGE_HEALTH_API_KEY=qak_...  # a publish-only key, only for --report-health (the check-in takes an API key today)
# QUEUEY_API_KEY=qak_...            # alternative to the pair; with the pair set it is not read
# QUEUEY_INGRESS_BASE=http://localhost:5084   # only for testing against a local Queuey
```

`sudo systemctl enable --now queuey-edge` — done. `StateDirectory` gives
the spool a durable home at `/var/lib/queuey` with the right ownership.

`CacheDirectory` and `DOTNET_BUNDLE_EXTRACT_BASE_DIR` give the binary
somewhere to unpack SQLite's native library. The binary carries the library
inside itself and writes it out at every start, before any command runs, to
`/var/cache/queuey/queuey/<build id>/`. Without the variable it unpacks under
the user's home (`~/.net`), and a system user's home usually does not exist:
the binary then exits before it runs anything, with `Failure processing
application bundle` and exit code 159. Wherever the variable points, the
directory must be writable by the service user and nobody else, and on a
filesystem that allows running code — not a `noexec` `/tmp`. Hardening the
unit does not change this: `ProtectSystem=strict` leaves `CacheDirectory`
writable, and unpacking needs no home, so `ProtectHome=yes` is fine. Each
version unpacks into a folder of its own; `sudo systemctl clean --what=cache
queuey-edge` removes the old ones while the service is stopped. Any other
account that runs the binary on the device, such as the cron job in step 4,
needs the same: a home it can write to, or `DOTNET_BUNDLE_EXTRACT_BASE_DIR`
set for it.

**The clock matters.** Edge signs each transfer when it sends it, with this
machine's clock, and Queuey refuses a signature more than 5 minutes off its
own (`timestamp_out_of_range`). `time-sync.target` makes the service start
after the clock is synchronised — but only when a wait service is enabled;
without one the target is reached at once. Enable the one for the time daemon
the device runs:

```bash
sudo systemctl enable systemd-time-wait-sync.service   # with systemd-timesyncd
sudo systemctl enable chrony-wait.service              # with chrony
```

A device without NTP, or with a dead RTC battery, holds its events — Edge logs
that the clock is off; the key is checked once the clock is right — until the
clock is right, then drains by itself.

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
it is what lets Edge keep *recording* successes at the limit. A spool smaller
than 16 MB keeps a quarter instead, see below.)

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

## The MQTT broker in front of Edge

With an MQTT intake (`--mqtt`), the broker is part of the chain, and only the
spool is Edge's. Edge acknowledges a message only after the spool has it on
disk. When the spool is full, the intake steps back: it leaves the message
unacknowledged, disconnects, and reconnects once there is room, so the broker
re-sends it first. Until then the backlog sits on the broker, under the
broker's rules. Three things follow:

- **Size the spool for the outage; the broker queue is only the overflow.**
  The broker holds messages only after the spool is full.
- **Devices publish at QoS 1.** A QoS 0 message has no redelivery: if Edge
  cannot take it at that moment, it is gone (counted as `lost`). Mosquitto
  also does not queue QoS 0 for a client that is away (`queue_qos0_messages`
  is off by default).
- **The broker's own durability is not Edge's.** Mosquitto, for one, writes
  its persistence file every `autosave_interval` seconds (default 1800). A
  power cut loses what it queued since then.
- **A message can reach Edge twice.** A device re-sends at QoS 1 when its
  acknowledgement is lost, and the broker re-sends what Edge had not yet
  acknowledged when it stopped. With a stable event id in the payload,
  `--mqtt-id-field eventId` (`QUEUEY_MQTT_ID_FIELD`) makes such a copy one event:
  Edge acknowledges it to the broker and counts it as `duplicate`. The key is
  the id plus a hash of the payload, so a device that reuses an id with new
  content still gets a new event. A message without the id is taken as its own
  event and counted in `queuey.edge.intake.without_id`; watch that counter after
  a firmware change.

A Mosquitto configuration for a gateway where Edge is the only subscriber:

```conf
# /etc/mosquitto/conf.d/queuey-edge.conf
listener 1883 127.0.0.1
allow_anonymous true              # loopback only; use password_file once the broker leaves the machine

persistence true
persistence_location /var/lib/mosquitto/
autosave_interval 60              # seconds; how much a power cut can cost the broker's queue

persistent_client_expiration 30d  # keep Edge's session across long Edge downtime, not forever
max_queued_messages 100000        # default 1000, then the broker drops; size for the overflow you accept
max_queued_bytes 0                # no byte cap; set one if the gateway's disk is small
```

Watch `queuey.edge.intake.paused` (the intake stands back because the spool is
full) and `queuey.edge.intake.connected` (0 when the broker is unreachable),
next to `queuey.edge.spool.oldest_age_seconds`. A broker that went away leaves
an empty spool, which reads exactly like a quiet, healthy node.

## Spool limit and backoff cap for the daemon

Two settings for `queuey edge run`, as a flag or in the service's environment
file:

| Setting | Default | What it does |
| --- | --- | --- |
| `--spool-max-mb <n>` / `QUEUEY_SPOOL_MAX_MB` | 512 | The spool's limit in whole megabytes, 1 or more. |
| `--backoff-cap-seconds <n>` / `QUEUEY_BACKOFF_CAP_SECONDS` | 300 | The longest wait between transfer attempts while Queuey cannot be reached, 1 to 3600. |

**The spool limit.** Up to 4 MB of the limit is kept for bookkeeping, so that
Edge can still record a transfer when the spool is full. A spool smaller than
16 MB keeps a quarter instead: at `--spool-max-mb 1` the spool takes 768 KB of
events, and then refuses. The health snapshot's `storageLimitBytes` (786432 in
that case) is what the spool accepts up to, and the node reports `StorageFull`
once it is reached. A small limit is how a test shows backpressure within a
short outage: with an MQTT intake, the intake steps back from the broker, and
the broker holds the rest. `queuey edge publish` writes the same spool and must
have the same limit: give it the same `--spool-max-mb`, or keep
`QUEUEY_SPOOL_MAX_MB` in the environment both run in.

**The backoff cap.** While Queuey cannot be reached, the wait between attempts
grows to the cap. After a long outage, the backlog starts to drain at most one
cap after the network is back: five minutes by default. A lower cap starts the
drain sooner, for one more attempt per cap while the network is down. It does
not change the probes after Queuey refuses the credentials (five minutes, then
up to an hour) or a paused queue (every minute). `queuey edge kick` starts the
drain at once.

An invalid value stops `edge run` and `edge publish` at startup with a message
and exit code 2, as an invalid `--listen` does.

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
