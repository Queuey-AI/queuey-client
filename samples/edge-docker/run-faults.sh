#!/usr/bin/env bash
# Three faults that run-scenario.sh does not make, on the running stack:
#
#   crash-with-backlog  Edge is killed (SIGKILL) during an outage, while events wait in its store,
#                       and started again before the uplink comes back.
#   recreate            Edge is stopped while the nodes publish, then its container is recreated
#                       with another hostname. Edge must resume its broker session.
#   broker-kill         Edge is stopped while the nodes publish, then the broker is killed (SIGKILL)
#                       with messages queued for Edge, and started again.
#
# Then it stops the nodes, waits until Edge holds nothing and the deliveries have been quiet, and
# analyses what arrived.
#
#   ./run-faults.sh                           # all three, about 15 minutes
#   TESTS="crash-with-backlog recreate" ./run-faults.sh
#
# The first two must lose nothing. broker-kill shows the broker's own durability, not Edge's: Edge is
# away, so the readings exist only in the broker, which has acknowledged them to the nodes. Mosquitto
# writes what it has queued to disk every autosave_interval (60 s in mosquitto.conf), so a hard kill
# loses what it queued since the last write; with autosave_on_changes it writes after every change and
# loses nothing (see the README). The analysis counts the readings missing from that window apart, and
# only a reading missing from anywhere else fails the run.
#
# Needs the stack up (docker compose up -d --build) and the deliveries reaching check/receiver.py,
# as for run-scenario.sh.
set -euo pipefail
cd "$(dirname "$0")"

TESTS=${TESTS:-"crash-with-backlog recreate broker-kill"}
OUTAGE=${OUTAGE:-240}             # the outage in crash-with-backlog
CRASH_AFTER=${CRASH_AFTER:-120}   # when in that outage Edge is killed
AWAY=${AWAY:-70}                  # how long Edge is away in recreate and broker-kill
SETTLE=${SETTLE:-60}
QUIET=${QUIET:-60}
PROJECT=${COMPOSE_PROJECT_NAME:-queuey-edge-mqtt}
RESULTS=results
SPOOL=/var/lib/queuey/spool.db

mkdir -p "$RESULTS"
: > "$RESULTS/phases.jsonl"

now_ms() { python3 -c 'import time; print(int(time.time() * 1000))'; }
say() { printf '%s  %s\n' "$(date -u +%H:%M:%SZ)" "$*"; }
record_phase() { printf '{"phase":"%s","startMs":%s,"endMs":%s}\n' "$1" "$2" "$3" >> "$RESULTS/phases.jsonl"; }
edge_status() { docker compose exec -T edge queuey edge status --spool "$SPOOL" --json 2>/dev/null || echo '{}'; }
pending() { edge_status | python3 -c 'import json, sys; print(json.load(sys.stdin).get("pending", -1))'; }
deliveries() { [ -f "$RESULTS/deliveries.jsonl" ] && wc -l < "$RESULTS/deliveries.jsonl" | tr -d ' ' || echo 0; }
session_line() { docker compose logs --no-log-prefix --since "$1" edge 2>/dev/null | grep -o 'connected to .*(broker session [a-z]*)' | sed -E 's/; [0-9]+ route\(s\) subscribed at QoS 1//' | tail -1; }
wants() { case " $TESTS " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

docker compose start simulator >/dev/null
say "nodes publishing; $SETTLE s before the first fault"
sleep "$SETTLE"

if wants crash-with-backlog; then
  say "crash-with-backlog: cutting Edge's uplink for $OUTAGE s, killing Edge after $CRASH_AFTER s"
  start=$(now_ms)
  docker network disconnect "${PROJECT}_uplink" "$(docker compose ps -q edge)"
  sleep "$CRASH_AFTER"
  say "crash-with-backlog: $(pending) event(s) waiting on Edge; SIGKILL, starting it again after 10 s"
  kill_at=$(now_ms)
  docker compose kill -s SIGKILL edge >/dev/null
  sleep 10
  docker compose start edge >/dev/null
  record_phase crash-in-outage "$kill_at" "$(now_ms)"
  sleep $(( OUTAGE - CRASH_AFTER - 10 ))
  say "crash-with-backlog: $(pending) event(s) waiting after the restart; uplink back"
  docker network connect "${PROJECT}_uplink" "$(docker compose ps -q edge)"
  record_phase outage-with-crash "$start" "$(now_ms)"
  sleep "$SETTLE"
fi

if wants recreate; then
  say "recreate: stopping Edge for $AWAY s while the nodes publish"
  start=$(now_ms)
  docker compose stop edge >/dev/null
  sleep "$AWAY"
  printf 'services:\n  edge:\n    hostname: recreated-%s\n' "$(date +%s)" > "$RESULTS/hostname-override.yml"
  COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}:$RESULTS/hostname-override.yml" \
    docker compose up -d --force-recreate --no-deps edge >/dev/null 2>&1
  record_phase recreate-away "$start" "$(now_ms)"
  sleep 10
  say "recreate: a new container, hostname $(docker inspect -f '{{.Config.Hostname}}' "$(docker compose ps -q edge)"): $(session_line 20s)"
  sleep "$SETTLE"
fi

if wants broker-kill; then
  say "broker-kill: stopping Edge for $AWAY s while the broker queues for it, then killing the broker (SIGKILL)"
  start=$(now_ms)
  docker compose stop edge >/dev/null
  sleep "$AWAY"
  kill_at=$(now_ms)
  docker compose kill -s SIGKILL mosquitto >/dev/null
  record_phase broker-kill "$start" "$kill_at"
  docker compose start mosquitto >/dev/null
  sleep 3
  docker compose start edge >/dev/null
  sleep 10
  say "broker-kill: broker and Edge started again: $(session_line 15s)"
  sleep "$SETTLE"
fi

say "stopping the nodes"
docker compose stop simulator >/dev/null
docker run --rm -v "${PROJECT}_simulator-state:/state:ro" eclipse-mosquitto:2 \
  sh -c 'for f in /state/*.seq; do echo "$(basename "$f" .seq) $(cat "$f")"; done' > "$RESULTS/published.txt"

say "waiting until Edge holds nothing and deliveries have been quiet for $QUIET s"
last=$(deliveries); quiet_since=$(date +%s)
while :; do
  sleep 10
  now=$(deliveries)
  if [ "$now" != "$last" ]; then last=$now; quiet_since=$(date +%s); fi
  if [ "$(pending)" = "0" ] && [ $(( $(date +%s) - quiet_since )) -ge "$QUIET" ]; then break; fi
done

say "done; analysing"
python3 check/analyze.py --results "$RESULTS" || true
# A reading missing from the broker-kill window went down with the broker's unsaved queue; any other
# missing reading, or an order inversion, is a failure.
python3 - "$RESULTS" <<'EOF'
import json, os, sys
from datetime import datetime, timezone
results = sys.argv[1]
summary = json.load(open(os.path.join(results, "summary.json")))
phases = [json.loads(l) for l in open(os.path.join(results, "phases.jsonl")) if l.strip()]
window = next((p for p in phases if p["phase"] == "broker-kill"), None)
ts = {}
for line in open(os.path.join(results, "deliveries.jsonl")):
    d = json.loads(line)
    if d.get("eventId") and d.get("ts"):
        ts[(d["nodeId"], d["seq"])] = datetime.strptime(d["ts"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc).timestamp() * 1000
in_window, elsewhere = 0, 0
for node, seqs in summary["missingByNode"].items():
    delivered = sorted(s for (n, s) in ts if n == node)
    for seq in seqs:
        before = max((s for s in delivered if s < seq), default=None)
        after = min((s for s in delivered if s > seq), default=None)
        lo = ts[(node, before)] if before is not None else 0
        hi = ts[(node, after)] if after is not None else float("inf")
        if window and lo <= window["endMs"] and hi >= window["startMs"]:
            in_window += 1
        else:
            elsewhere += 1
print(f"Missing from the broker-kill window: {in_window}   (the broker's queue since its last autosave)")
print(f"Missing from anywhere else:          {elsewhere}")
print(f"Order inversions:                    {summary['orderInversions']}")
sys.exit(1 if elsewhere or summary["orderInversions"] else 0)
EOF
