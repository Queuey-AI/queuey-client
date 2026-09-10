# Queuey.Cli

The `queuey` command-line tool for [Queuey](https://queuey.ai) — the deploy-time
and debugging half of the SDK. Everything the client libraries do from inside
your app, this does from a pipeline or a terminal.

```bash
dotnet tool install -g Queuey.Cli --prerelease
queuey whoami
```

## What it is for

Three jobs, in the order most people meet them:

```bash
# 1. Converge Queuey from your code or a committed file — the deploy step
queuey sync   --assembly bin/Release/net8.0/App.dll
queuey apply  --file queuey.deploy.json --check     # the CI drift gate

# 2. Receive real webhooks on your laptop, without exposing a port
queuey listen --forward-to http://localhost:5000/hooks

# 3. Publish, inspect, operate
queuey publish order-events --event order.created --data '{"orderId":"A-1"}'
queuey metrics que_… ; queuey issues ten_… ; queuey edge status
```

`queuey --help` lists every command. Each one takes `--json`, so the tool
composes into scripts rather than only into human eyes.

## Configuration

Connection settings resolve with the precedence **CLI flag > environment
variable > `queuey.json` > default**:

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
idempotent, so a fixed re-run converges.

## More

Full documentation, including the deployment-file format and the `listen`
workflow, is in the [repository README](https://github.com/Queuey-AI/queuey-client#readme).

MIT licensed.
