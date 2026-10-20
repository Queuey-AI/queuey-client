"""A receiving endpoint that records every delivery, for check/analyze.py.

Answers 200 to any POST and appends one JSON line per delivery to
results/deliveries.jsonl: when it arrived, Queuey's event id and idempotency
key, and the simulator's eventId, nodeId, seq and ts from the body. It does
not verify signatures; a real receiver must (see the Queuey docs).

    python3 check/receiver.py [--port 8099] [--out results/deliveries.jsonl]

Behind `queuey listen --forward-to http://127.0.0.1:8099`, or any HTTPS
front that forwards to it. Standard library only.
"""

import argparse
import json
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOCK = threading.Lock()


def make_handler(out_path):
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length) if length else b""
            try:
                payload = json.loads(body)
            except ValueError:
                payload = {}
            if not isinstance(payload, dict):
                payload = {}
            record = {
                "arrivedAtMs": int(time.time() * 1000),
                "queueyEventId": self.headers.get("X-Queuey-Event-Id"),
                "idempotencyKey": self.headers.get("Idempotency-Key"),
                "eventId": payload.get("eventId"),
                "nodeId": payload.get("nodeId"),
                "seq": payload.get("seq"),
                "ts": payload.get("ts"),
                "bytes": len(body),
            }
            with LOCK, open(out_path, "a", encoding="utf-8") as out:
                out.write(json.dumps(record) + "\n")
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"ok")

        def log_message(self, format, *args):
            pass

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--port", type=int, default=8099)
    parser.add_argument("--out", default="results/deliveries.jsonl")
    args = parser.parse_args()
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(args.out))
    print(f"Recording deliveries on http://127.0.0.1:{args.port} to {args.out}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
