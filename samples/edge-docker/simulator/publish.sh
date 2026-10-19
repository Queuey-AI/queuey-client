#!/bin/sh
# NODES simulated lighting nodes, each publishing one reading of about 850
# bytes every INTERVAL seconds at QoS 1, on lighting/<node>/telemetry.
#
# Every message carries a stable eventId (node and sequence number) and the
# time the reading was taken. A node keeps a message until the broker has
# acknowledged it, as a device's MQTT client would, and resends the same
# bytes until then. A gap in the sequence numbers at the receiver is
# therefore never the simulator's.
set -u

NODES=${NODES:-20}
INTERVAL=${INTERVAL:-30}
BROKER=${BROKER:-mosquitto}
START_DELAY=${START_DELAY:-20}
DUPLICATE_EVERY=${DUPLICATE_EVERY:-0}
PAD=$(printf '%0700d' 0)

# A broker queues messages only for a subscriber whose session exists: what is
# published before Edge has subscribed for the first time is gone. Give Edge
# time to connect before the first reading.
sleep "$START_DELAY"

while :; do
  started=$(date +%s)
  i=1
  while [ "$i" -le "$NODES" ]; do
    node=$(printf 'node-%02d' "$i")
    state="/state/$node.seq"
    seq=$(( $(cat "$state" 2>/dev/null || echo 0) + 1 ))
    message=$(printf '{"eventId":"%s-%06d","nodeId":"%s","seq":%d,"ts":"%s","lux":%d,"powerW":%d,"pad":"%s"}' \
      "$node" "$seq" "$node" "$seq" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
      $(( (seq * 37 + i * 11) % 1000 )) $(( 40 + (seq + i) % 25 )) "$PAD")
    until mosquitto_pub -h "$BROKER" -q 1 -i "sim-$node" -t "lighting/$node/telemetry" -m "$message"; do
      sleep 1
    done
    echo "$seq" > "$state"
    # Every DUPLICATE_EVERY-th reading goes out twice, the same bytes, as a
    # device does when it re-sends a QoS 1 message whose PUBACK it lost.
    if [ "$DUPLICATE_EVERY" -gt 0 ] && [ $(( seq % DUPLICATE_EVERY )) -eq 0 ]; then
      until mosquitto_pub -h "$BROKER" -q 1 -i "sim-$node" -t "lighting/$node/telemetry" -m "$message"; do
        sleep 1
      done
    fi
    i=$((i + 1))
  done
  elapsed=$(( $(date +%s) - started ))
  if [ "$elapsed" -lt "$INTERVAL" ]; then
    sleep $(( INTERVAL - elapsed ))
  fi
done
