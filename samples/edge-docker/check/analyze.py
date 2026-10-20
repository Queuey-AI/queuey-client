"""What arrived, against what the simulator published.

Reads the deliveries check/receiver.py recorded and reports, per node and in
total: messages missing, duplicates (and whether a duplicate is the same
Queuey event delivered again or a second event), order inversions, and
latency from reading to arrival. With the phase log run-scenario.sh writes,
it also reports the latency of the readings taken during the baseline, before
any outage, and per outage, how long after the link came back the readings
taken during the outage had arrived. A reading's time has whole seconds, so a
latency can read up to a second high.

    python3 check/analyze.py [--results results]

Standard library only. Exit code 1 when anything is missing or out of order.
"""

import argparse
import json
import os
import statistics
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone


def load_jsonl(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def load_published(path):
    published = {}
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                parts = line.split()
                if len(parts) == 2 and parts[1].isdigit():
                    published[parts[0]] = int(parts[1])
    return published


def epoch_ms(ts):
    return int(datetime.strptime(ts, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc).timestamp() * 1000)


def percentile(values, p):
    if not values:
        return None
    ordered = sorted(values)
    k = min(len(ordered) - 1, max(0, round(p / 100 * (len(ordered) - 1))))
    return ordered[k]


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--results", default="results")
    args = parser.parse_args()

    deliveries = [d for d in load_jsonl(os.path.join(args.results, "deliveries.jsonl")) if d.get("eventId")]
    published = load_published(os.path.join(args.results, "published.txt"))
    phases = load_jsonl(os.path.join(args.results, "phases.jsonl"))
    if not deliveries:
        print("No deliveries recorded.")
        return 1

    deliveries.sort(key=lambda d: d["arrivedAtMs"])

    # Duplicates: the same reading arriving more than once.
    copies = defaultdict(list)
    for d in deliveries:
        copies[d["eventId"]].append(d.get("queueyEventId"))
    redelivered = sum(len(ids) - 1 for ids in copies.values() if len(ids) > 1 and len(set(ids)) == 1)
    second_events = sum(len(set(ids)) - 1 for ids in copies.values() if len(set(ids)) > 1)

    # Per node: what is missing, and whether first arrivals came in order.
    seen = defaultdict(list)
    first_seen = set()
    for d in deliveries:
        if d["eventId"] in first_seen:
            continue
        first_seen.add(d["eventId"])
        seen[d["nodeId"]].append(d["seq"])
    nodes = sorted(set(seen) | set(published))
    missing = {}
    inversions = Counter()
    for node in nodes:
        arrived = seen.get(node, [])
        expected = published.get(node, max(arrived, default=0))
        gaps = sorted(set(range(1, expected + 1)) - set(arrived))
        if gaps:
            missing[node] = gaps
        highest = 0
        for seq in arrived:
            if seq < highest:
                inversions[node] += 1
            highest = max(highest, seq)

    latencies = [(d["arrivedAtMs"] - epoch_ms(d["ts"])) / 1000 for d in deliveries if d.get("ts")]

    # Normal operation: the readings taken during the baseline. The overall
    # figures include every reading an outage held back.
    baseline = next((p for p in phases if p.get("phase") == "baseline" and "endMs" in p), None)
    baseline_latencies = [
        (d["arrivedAtMs"] - epoch_ms(d["ts"])) / 1000 for d in deliveries
        if baseline and d.get("ts") and baseline["startMs"] <= epoch_ms(d["ts"]) <= baseline["endMs"]
    ]

    outages = []
    for phase in phases:
        if not phase.get("phase", "").startswith("outage") or "endMs" not in phase:
            continue
        inside = [d for d in deliveries if d.get("ts") and phase["startMs"] <= epoch_ms(d["ts"]) <= phase["endMs"]]
        after = [d["arrivedAtMs"] for d in deliveries if d["arrivedAtMs"] >= phase["endMs"]]
        outages.append({
            "phase": phase["phase"],
            "minutes": round((phase["endMs"] - phase["startMs"]) / 60000, 1),
            "readingsDuringOutage": len({d["eventId"] for d in inside}),
            "firstArrivalAfterLinkBackSeconds": round((min(after) - phase["endMs"]) / 1000, 1) if after else None,
            "allOutageReadingsArrivedAfterSeconds": round((max(d["arrivedAtMs"] for d in inside) - phase["endMs"]) / 1000, 1) if inside else None,
        })

    summary = {
        "deliveries": len(deliveries),
        "uniqueReadings": len(copies),
        "published": sum(published.values()) if published else None,
        "missing": sum(len(g) for g in missing.values()),
        "duplicatesSameQueueyEvent": redelivered,
        "duplicatesAsSecondEvent": second_events,
        "orderInversions": sum(inversions.values()),
        "latencySeconds": {
            "p50": percentile(latencies, 50),
            "p95": percentile(latencies, 95),
            "max": max(latencies) if latencies else None,
            "mean": round(statistics.mean(latencies), 1) if latencies else None,
        },
        "latencyBaselineSeconds": {
            "readings": len(baseline_latencies),
            "p50": percentile(baseline_latencies, 50),
            "p95": percentile(baseline_latencies, 95),
            "max": max(baseline_latencies) if baseline_latencies else None,
        },
        "outages": outages,
        "missingByNode": missing,
        "inversionsByNode": dict(inversions),
    }
    with open(os.path.join(args.results, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2)

    print(f"Deliveries:            {summary['deliveries']}")
    print(f"Unique readings:       {summary['uniqueReadings']}" + (f" of {summary['published']} published" if published else ""))
    print(f"Missing:               {summary['missing']}")
    print(f"Duplicates, same event:{redelivered:>6}   (one Queuey event delivered again)")
    print(f"Duplicates, new event: {second_events:>6}   (the reading reached Queuey twice)")
    print(f"Order inversions:      {summary['orderInversions']}")
    lat = summary["latencySeconds"]
    print(f"Latency, all (s):      p50 {lat['p50']}  p95 {lat['p95']}  max {lat['max']}")
    base = summary["latencyBaselineSeconds"]
    if base["readings"]:
        print(f"Latency, baseline (s): p50 {base['p50']}  p95 {base['p95']}  max {base['max']}   ({base['readings']} readings taken before any outage)")
    for o in outages:
        print(f"{o['phase']}: {o['minutes']} min, {o['readingsDuringOutage']} readings taken during it; "
              f"first arrival {o['firstArrivalAfterLinkBackSeconds']} s after the link came back, "
              f"all of them {o['allOutageReadingsArrivedAfterSeconds']} s after")
    print(f"Written: {os.path.join(args.results, 'summary.json')}")
    return 1 if summary["missing"] or summary["orderInversions"] else 0


if __name__ == "__main__":
    sys.exit(main())
