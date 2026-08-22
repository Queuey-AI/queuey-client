namespace Queuey.Client.Cli;

internal static class Usage
{
    public const string Text = @"queuey — command-line tool for Queuey

USAGE
  queuey <command> [options]

COMMANDS
  sync           Apply every [QueueyModel] stream found in an assembly (PUT /waas/streams).
  publish        Publish an event to a stream.
  create-tenant  Create a tenant under the current license.
  create-queue   Create a queue under a tenant.
  metrics        Show a queue's traffic snapshot.
  issues         List a tenant's issues.
  listen         Receive webhooks locally over a secure push session (Stripe-listen style).
  replay         Replay one existing event to your connected listener (read-only DLQ debugging).
  edge           Operate a Queuey Edge spool: status | retry | discard | recover | reset.
  whoami         Show the resolved host / environment / tenant / license (masks the key).

SYNC
  queuey sync --assembly <path.dll> [--dry-run] [--only a,b] [--stop-on-error] [--json]

PUBLISH
  queuey publish <stream> --event <type> [--key <k>]
                 (--data <json> | --file <path> | --stdin)
                 [--idempotency-key <k>] [--source <s>] [--json]

CREATE-TENANT
  queuey create-tenant --name <display> [--as-producer] [--with-default-queue] [--json]

CREATE-QUEUE
  queuey create-queue --tenant <ten_...> --name <display> [--json]

METRICS
  queuey metrics <que_...> [--json]

ISSUES
  queuey issues <ten_...> [--status open|resolved] [--severity critical|warning|info]
                [--queue <que_...>] [--limit N] [--cursor <c>] [--json]

LISTEN
  queuey listen --forward-to <url> [--queue <que_...> | --tenant <ten_...>]
                [--forward-exact] [--tee] [--yes]
                 Receives delivered webhooks over an outbound push session (no inbound port
                 exposed) and replays each to --forward-to. By default it preserves fidelity —
                 same method, path, query, headers, and body (only the host is swapped, so a
                 tenant with a base URL + per-queue routes mirrors fully). --forward-exact posts
                 to --forward-to VERBATIM (ignoring the original path), for bridging deliveries
                 into a FIXED local endpoint such as a local ingress route. Scope defaults to the
                 configured tenant. Default (redirect) sends only to you and returns your local
                 response code to the delivery record; --tee also delivers to the real endpoint.
                 A tenant-wide redirect asks for confirmation (--yes to skip). Ctrl-C to stop.

REPLAY
  queuey replay <event-id> --queue <que_...> [--json]
                 Replays one existing event to your connected `queuey listen` session for local
                 debugging (Stripe-replay style). Read-only — the event isn't modified and the real
                 endpoint is never contacted; works on any event, including a DLQ'd one. Run
                 `queuey listen` first so there's a listener to receive it.

EDGE
  queuey edge status  --spool <path> [--json]
  queuey edge retry   --spool <path> (--id N | --all)
  queuey edge discard --spool <path> --id N
  queuey edge recover --spool <path>
  queuey edge reset   --spool <path> --accept-data-loss
                 Operates a Queuey Edge spool file directly (WAL allows this alongside a running
                 host). status shows pending/quarantined/oldest-age; retry returns a quarantined
                 event to the drain after remediation; discard drops ONE quarantined event (an
                 explicit, logged operator decision — pending events cannot be discarded); recover
                 salvages readable events from a faulted spool into a fresh one, reporting exactly
                 how many were unreadable; reset abandons the spool (requires --accept-data-loss,
                 the old file is preserved for support either way).

WHOAMI
  queuey whoami [--json]

GLOBAL OPTIONS (all commands)
  --api-base <uri>                Control-plane (API) host (default: https://api.queuey.ai).
                                  Set it to point at a locally-running instance, e.g. for testing.
  --ingress-base <uri>            Ingress (publish) host (default: https://ingress.queuey.ai).
  --api-key <qak_...>             License-wide API key
  --tenant <ten_...>              Producer tenant public id
  --license <lic id>              License public id (required for sync)
  --source <s>                    X-Queuey-Source trace value
  --config <path>                 Path to a queuey.json (default: ./queuey.json)

CONFIG PRECEDENCE
  flag  >  environment (QUEUEY_API_BASE / QUEUEY_INGRESS_BASE /
           QUEUEY_API_KEY / QUEUEY_TENANT / QUEUEY_LICENSE / QUEUEY_SOURCE)  >
           queuey.json  >  default

EXIT CODES
  0 success   1 runtime failure   2 usage   3 config   4 assembly load
";
}
