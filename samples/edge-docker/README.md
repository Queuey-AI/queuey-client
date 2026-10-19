# Queuey Edge with MQTT, in Docker

A gateway in three containers: Mosquitto, Queuey Edge subscribed to it, and
simulated lighting nodes publishing readings through it. Edge holds every
message on disk before it acknowledges it to the broker, and hands it to
Queuey Cloud when the uplink is there; Cloud delivers to your endpoint.

```
nodes ──MQTT QoS 1──▶ Mosquitto ──▶ Queuey Edge ══ uplink ══▶ Queuey Cloud ──HTTPS──▶ your endpoint
└──────────────── gateway network (no route out) ───────────┘
```

No .NET is needed: Edge is the self-contained `queuey` binary from a
[GitHub release](https://github.com/Queuey-AI/queuey-client/releases), on
Debian slim (`edge/Dockerfile`). The Compose file builds `0.1.0-preview.12`
(`QUEUEY_VERSION` picks another). Use at least `0.1.0-preview.10`: older
binaries cannot open the spool.

## What you need

- Docker with Compose and BuildKit (Docker Desktop, or Docker Engine 23+).
- A Queuey workspace with a queue, here `lighting-telemetry`, delivering to
  an endpoint you can read. Without a public HTTPS endpoint, set the queue to
  forward to a local listener (a dev workspace) and receive with
  `queuey listen`, as in step 3.
- Python 3 for the checker in `check/` (standard library only).
- The `queuey` CLI on your own machine for the keys and `queuey listen`
  (`dotnet tool install -g Queuey.Cli --prerelease`, or a release binary).

## 1. The queue

`queuey.deploy.json` declares it: delivering, in order per node (the group
key Edge sends as `X-Queuey-Group-Key`), signed requests only, dead-lettering
on, 7 days of retention, and delivery to a local listener.

```bash
queuey create-queue --tenant ten_... --name lighting-telemetry
QUEUEY_TENANT=ten_... queuey plan --file queuey.deploy.json      # what would change
QUEUEY_TENANT=ten_... queuey apply --file queuey.deploy.json
```

Forwarding to a local listener needs a workspace marked dev; a person sets
that in the console, or `queuey create-tenant --environment dev` makes one.
With your own HTTPS endpoint instead, change `delivery` to
`{ "kind": "http", "url": "https://…" }` and drop the workspace section.

## 2. Keys

```bash
cp edge.env.example edge.env
queuey keys mint --queue lighting-telemetry --tenant ten_... --write edge.env
```

Set `QUEUEY_TENANT`, and put a publish-only key for the health check-in in
`QUEUEY_EDGE_HEALTH_API_KEY` (see the comments in the file). `edge.env` holds
secrets: git ignores it, and `keys mint --write` makes it readable by you
only. Docker passes it to the container as environment variables, which
`docker inspect` shows to anyone who may use Docker on the machine.

## 3. Start

```bash
docker compose up -d --build
docker compose logs -f edge      # "MQTT source connected to mosquitto:1883 …"
```

The node appears under **Edge nodes** in the console as
`lighting-gateway-1`. Twenty nodes publish one reading of about 850 bytes
every 30 seconds (`NODES` and `INTERVAL` change that), each with a stable
`eventId` that Edge deduplicates on (`QUEUEY_MQTT_ID_FIELD`).
`DUPLICATE_EVERY=25` makes every 25th reading go out twice, as from a node
that re-sends after a lost acknowledgement. They wait
`START_DELAY` seconds (20) before the first one: a broker queues messages only
for a subscriber whose session exists, so whatever is published before Edge
has subscribed for the first time is gone. That is true of any MQTT setup,
not just this one.

## 4. Receive and check

```bash
python3 check/receiver.py                       # records deliveries in results/deliveries.jsonl
queuey listen --queue lighting-telemetry --tenant ten_... --forward-to http://127.0.0.1:8099
```

With your own endpoint instead, record the same fields there:
`X-Queuey-Event-Id`, `Idempotency-Key`, and `eventId`, `nodeId`, `seq` and
`ts` from the body.

## 5. Break things

```bash
./run-scenario.sh
```

It runs a 10-minute baseline, cuts Edge's uplink for 30 and then 60 minutes,
kills Edge with SIGKILL and starts it again, restarts the broker, then stops
the nodes, waits until Edge holds nothing, and runs `check/analyze.py`. That
takes about 2 h 20 min. At the end it also saves Edge's own counters
(`results/edge-health.json`: accepted, duplicates, deferred while it stood
back, …). A quick run takes about 15:

```bash
BASELINE=120 OUTAGE_1=120 OUTAGE_2=180 SETTLE=120 ./run-scenario.sh
```

By hand:

```bash
docker network disconnect queuey-edge-mqtt_uplink $(docker compose ps -q edge)   # outage
docker compose exec edge queuey edge status --spool /var/lib/queuey/spool.db     # pending grows
docker network connect queuey-edge-mqtt_uplink $(docker compose ps -q edge)      # back
docker compose kill -s SIGKILL edge && docker compose start edge                 # crash
docker compose restart mosquitto                                                  # broker restart
```

## What to expect

- **Nothing missing.** Every reading the broker acknowledged to a node
  arrives. A reading Edge took survives outages, crashes and restarts on its
  volume.
- **Order per node.** The route makes topic level 1 (the node) the lane, and
  Edge hands a lane to Cloud one event at a time. Deliver in order with a
  queue that keeps order: FIFO, or by group key for parallel nodes.
- **No duplicates from the gateway.** A node re-sending at QoS 1, or the
  broker re-sending after Edge crashed between its disk write and its
  acknowledgement, brings the same message to Edge twice. Edge keys it on the
  `eventId` and a hash of the payload, so the repeat is one event, counted as
  a duplicate in Edge's health. Queuey Cloud still delivers at-least-once:
  a redelivery of one event carries the same `X-Queuey-Event-Id`.
- **Recovery after an outage.** Edge does not watch the network; it keeps
  trying to deliver. While the uplink is down it sends one test event at a
  time, and the wait between attempts grows at random from 1 second up to a
  cap of 5 minutes, about one attempt a minute in a long outage. The
  randomness keeps a fleet that lost its link together from knocking again in
  lockstep. When an attempt gets through, every node resumes at once
  (0.1.0-preview.12 and later) and drains in order at full speed. With 20
  nodes some node is due within seconds of the link coming back; with a
  single node the median is about a minute, and the cap is 5 minutes either
  way. On a link that drops packets instead of refusing connections, add up to
  30 seconds for the attempt in progress to time out.
  `docker compose exec edge queuey edge kick --spool /var/lib/queuey/spool.db`
  starts the drain at once.

## Backpressure

`docker-compose.backpressure.yml` gives Edge a 1 MB spool, which fills
within a few minutes of an outage. Edge then stops taking messages, steps
back from the broker, and lets the broker hold the overflow until it has room:

```bash
docker compose -f docker-compose.yml -f docker-compose.backpressure.yml up -d
```

Use 0.1.0-preview.13 or later for this. Before it, a transferred event with a
small payload kept its spool space for 24 hours, so a full spool of small
events stayed full after the backlog had gone.

## Limits worth knowing

- There is no official container image yet; this Dockerfile is the sample.
- The spool and the extraction directory must be volumes. The binary
  unpacks SQLite's native library at start and must be able to execute it
  there, which a Docker tmpfs (noexec) does not allow.
- The broker's durability is not Edge's: Mosquitto writes its queue to disk
  every `autosave_interval` (60 s here). See `mosquitto/mosquitto.conf` and the
  [operations notes](https://github.com/Queuey-AI/queuey-client/blob/main/docs/edge-operations.md#the-mqtt-broker-in-front-of-edge).
- MQTT 3.1.1 only, and nodes must publish at QoS 1 for the guarantee.

## Clean up

`docker compose down` stops everything and keeps the volumes. `docker compose
down -v` also deletes Edge's spool, and with it every event Edge has not yet
handed to Queuey Cloud.
