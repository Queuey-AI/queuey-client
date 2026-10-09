# Queuey.Cli

The `queuey` command-line tool for [Queuey](https://queuey.ai) — the deploy-time
and debugging half of the SDK. Everything the client libraries do from inside
your app, this does from a pipeline or a terminal.

```bash
dotnet tool install -g Queuey.Cli --prerelease
queuey advise          # reads the repository and says how Queuey belongs in it; changes nothing
queuey advise --intent flow.json --json   # from what you want to a deployment file and a code plan
```

No .NET on the machine? Every release also ships the tool as one self-contained
file per platform — `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64` and
`win-x64` — on the [releases page](https://github.com/Queuey-AI/queuey-client/releases/latest).
It needs no runtime:

```bash
curl -fsSL https://github.com/Queuey-AI/queuey-client/releases/latest/download/queuey-linux-x64.tar.gz | tar -xz
./queuey --version
```

## What it is for

Three jobs, in the order most people meet them:

```bash
# 1. Converge Queuey from your code or a committed file — the deploy step
queuey sync   --assembly bin/Release/net8.0/App.dll
queuey apply  --file queuey.deploy.json --check     # the CI drift gate
queuey publish orders --data '{"orderId":"A-1"}' --idempotency-key order-A-1   # prints evt_…
queuey verify orders --event evt_…                  # did the event you published arrive?

# 2. Receive real webhooks on your laptop, without exposing a port
queuey listen --queue <name> --forward-to http://localhost:5000

# 3. Publish, inspect, operate
queuey publish order-events --event order.created --data '{"orderId":"A-1"}'
queuey events get evt_… --queue orders              # its status and attempts, as Queuey serves them
queuey metrics que_… ; queuey issues ten_… ; queuey edge status
```

`queuey --help` lists every command. Each one that reports takes `--json` —
errors included, as JSON on stdout — so the tool composes into scripts rather
than only into human eyes. An option a command does not take fails it with
exit 2 and the list of options it does take, instead of being ignored.

## Log in

```bash
queuey login --profile dev
```

It prints a link and a code: open the link, check the code, and approve it in
the Queuey console. No key to copy. In a terminal it opens the browser and
waits; without one (an agent) it prints the link and exits `5`, and
`queuey login --wait` finishes once the link is approved. Every command without
an API key then uses the login. `queuey logout` ends it. Publishing to the
ingress still needs a key.

## Configuration

Connection settings resolve with the precedence **CLI flag > environment
variable > `queuey.json` > default**, and without an API key the login is used:

| Setting | Flag | Environment |
| --- | --- | --- |
| API key | `--api-key` | `QUEUEY_API_KEY` |
| Tenant | `--tenant` | `QUEUEY_TENANT` |
| License | `--license` | `QUEUEY_LICENSE` |
| Environment | `--env` | `QUEUEY_ENV` |

`queuey whoami` prints what actually resolved, with the key masked — the first
thing to run when a command reaches the wrong workspace.

Keep `queuey.json` (which holds your API key) out of version control. The
deployment file `queuey.deploy.json` is the opposite: it carries no secrets,
naming credentials by reference, and is meant to be committed.

## Exit codes

A run that did not fully converge exits non-zero, so a pipeline fails loudly
rather than reporting a green deploy over a half-applied workspace. Applying is
idempotent, so a fixed re-run converges. Exit `5` means a person is needed:
a configuration plan waits for approval in Queuey's inbox and nothing was
applied (`queuey apply --plan plan_…` applies it once approved, and `--wait`
waits), or a login link waits to be approved (`queuey login --wait`).

## More

Full documentation, including the deployment-file format and the `listen`
workflow, is in the [repository README](https://github.com/Queuey-AI/queuey-client#readme).

MIT licensed.
