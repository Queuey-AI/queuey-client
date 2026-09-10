# Outcome: Edge as a daemon, any language on the same machine

**Chosen when:** the machine has a durable local disk and an unreliable network,
but the publishing process is not a .NET 8+ host — Node, Python, Java, Go,
shell, cron, a C binary, or a fleet of devices publishing to one gateway.

**What the caller gets:** the same contract as in-process Edge. The daemon owns
the spool; the application's publish becomes durable by pointing at a loopback
endpoint instead of the cloud. The thin client carries zero reliability logic.

## Shape

Run the daemon once per machine, under a supervisor that restarts it:

```bash
queuey edge run --spool /var/lib/queuey/spool.db \
  --tenant ten_… --api-key "$QUEUEY_API_KEY" \
  --listen 7311                      # loopback ingress, same wire shape as cloud
```

Then publish from anything on the box with the cloud quickstart's request and
the base URL swapped:

```ts
// TypeScript — identical to the cloud request, base URL swapped
await fetch(`http://localhost:7311/events/${tenant}/sensor-readings`, {
  method: "POST",
  headers: { "Content-Type": "application/json", "X-Queuey-Group-Key": "unit-7" },
  body: JSON.stringify(reading),
}); // 202 = durably accepted locally, even with the cable pulled
```

```python
requests.post(f"http://localhost:7311/events/{tenant}/sensor-readings",
              json=reading, headers={"X-Queuey-Group-Key": "unit-7"})
```

Or from a shell / cron, without HTTP:

```bash
queuey edge publish sensor-readings --spool /var/lib/queuey/spool.db --tenant ten_… \
  --data '{"temp":21.5}' --event-type temperature.updated --group-key unit-7
```

`publish` returns after the durable local commit. With no daemon running the
event still sits durably in the spool and drains when one next starts.

## Failure semantics the caller must honour

- **Connection refused** — the daemon is down and Queuey did **not** take
  custody. Run it under systemd with `Restart=always`; a client that sees
  refused must fail its own operation, not pretend.
- **507** — spool full. Backpressure; treat as an operational alert.
- **503** — storage faulted; operator recovery required.
- `GET /health` on the same port serves the health snapshot for local probes.

## Installing on a box without .NET

Self-contained single-file binaries per architecture are attached to each
GitHub Release of queuey-client (`linux-x64`, `linux-arm64`, `osx-arm64`,
`win-x64`). No runtime needed — the install path for Pi-class gateways and
industrial PCs. Where the SDK is present, `dotnet tool install -g Queuey.Cli
--prerelease` also works.

## Already have an MQTT broker on the gateway?

Do not rewrite the devices. Add routes so the daemon subscribes and hands every
message to the spool; the broker is acked only after the fsync'd commit:

```bash
queuey edge run … --mqtt localhost:1883 \
  --mqtt-routes "plant/+/alarms=alarms@1;plant/+/state=machine-state@1"
```

`@1` makes topic level 1 the ordering lane. Intake only: no transformation,
no fan-out.

## Verify

`GET http://localhost:7311/health` answers; one publish shows as transferred
in `queuey edge status --spool …` and in the console; a publish with egress
blocked shows as pending and drains when egress returns.

## Read

- https://app.queuey.ai/docs/how-to/edge
- `src/Queuey.Edge/README.md` (sections "No .NET app?" and "TypeScript, Java,
  Python") and `docs/edge-operations.md` in
  https://github.com/Queuey-AI/queuey-client
