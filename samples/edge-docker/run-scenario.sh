#!/usr/bin/env bash
# Puts the running stack through the scenario: a baseline, two outages of
# Edge's uplink, an Edge crash and a broker restart. Then it stops the nodes,
# waits until Edge has handed everything to Queuey Cloud and the deliveries
# have stopped coming, and analyses what arrived.
#
#   ./run-scenario.sh                                            # 30- and 60-minute outages, about 2 h 20 min
#   BASELINE=120 OUTAGE_1=120 OUTAGE_2=180 SETTLE=120 ./run-scenario.sh   # a quick run, about 15 min
#
# Needs the stack up (docker compose up -d --build) and the deliveries reaching
# check/receiver.py, which records them in results/deliveries.jsonl.
set -euo pipefail
cd "$(dirname "$0")"

BASELINE=${BASELINE:-600}
OUTAGE_1=${OUTAGE_1:-1800}
OUTAGE_2=${OUTAGE_2:-3600}
SETTLE=${SETTLE:-600}
QUIET=${QUIET:-90}
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

# Edge's spool, every 10 seconds, for the timeline.
( while :; do
    printf '{"tMs":%s,"status":%s}\n' "$(now_ms)" "$(edge_status | tr -d '\n')" >> "$RESULTS/edge-status.jsonl"
    sleep 10
  done ) &
POLLER=$!
trap 'kill $POLLER 2>/dev/null || true' EXIT

outage() { # name seconds
  local edge start
  edge=$(docker compose ps -q edge)
  say "$1: cutting Edge's uplink for $2 s"
  start=$(now_ms)
  docker network disconnect "${PROJECT}_uplink" "$edge"
  sleep "$2"
  docker network connect "${PROJECT}_uplink" "$edge"
  record_phase "$1" "$start" "$(now_ms)"
  say "$1: uplink back, $(pending) event(s) pending on Edge"
}

say "baseline: $BASELINE s"
start=$(now_ms); sleep "$BASELINE"; record_phase baseline "$start" "$(now_ms)"

outage outage-1 "$OUTAGE_1"
sleep "$SETTLE"
outage outage-2 "$OUTAGE_2"
sleep "$SETTLE"

say "crash: killing Edge (SIGKILL), starting it again after 10 s"
start=$(now_ms)
docker compose kill -s SIGKILL edge >/dev/null
sleep 10
docker compose start edge >/dev/null
record_phase crash "$start" "$(now_ms)"
sleep 120

say "broker restart"
start=$(now_ms)
docker compose restart mosquitto >/dev/null
record_phase broker-restart "$start" "$(now_ms)"
sleep "$SETTLE"

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
# Edge's own side: what its MQTT intake did with every message (accepted,
# duplicates, deferred while it stood back, …), from the loopback endpoint.
docker compose exec -T edge bash -c 'exec 3<>/dev/tcp/127.0.0.1/7311 && printf "GET /health HTTP/1.0\r\nHost: 127.0.0.1:7311\r\n\r\n" >&3 && cat <&3' \
  | python3 -c 'import sys; sys.stdout.buffer.write(sys.stdin.buffer.read().split(b"\r\n\r\n", 1)[-1])' > "$RESULTS/edge-health.json" || true
python3 -c 'import json, sys
try:
    h = json.load(open(sys.argv[1]))
    for i in h.get("intakes") or []:
        print("Edge intake", i["name"], json.dumps(i["counters"]))
except Exception as e:
    print("Edge health not read:", e)' "$RESULTS/edge-health.json"
python3 check/analyze.py --results "$RESULTS"
