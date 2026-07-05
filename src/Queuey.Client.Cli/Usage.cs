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
  queuey listen --forward-to <url> [--queue <que_...> | --tenant <ten_...>] [--tee]
                 Receives delivered webhooks over an outbound push session (no inbound port
                 exposed) and replays each — same method, path, query, headers, and body — to
                 --forward-to (only the host is swapped). Scope defaults to the configured tenant.
                 --tee also lets the real endpoint fire; default (redirect) sends only to you.
                 Ctrl-C to stop.

WHOAMI
  queuey whoami [--json]

GLOBAL OPTIONS (all commands)
  --env production|development     Target environment (default: production)
  --api-base <uri>                Override the control-plane (API) host
  --ingress-base <uri>            Override the ingress (publish) host
  --api-key <qak_...>             License-wide API key
  --tenant <ten_...>              Producer tenant public id
  --license <lic id>              License public id (required for sync)
  --source <s>                    X-Queuey-Source trace value
  --config <path>                 Path to a queuey.json (default: ./queuey.json)

CONFIG PRECEDENCE
  flag  >  environment (QUEUEY_ENV / QUEUEY_API_BASE / QUEUEY_INGRESS_BASE /
           QUEUEY_API_KEY / QUEUEY_TENANT / QUEUEY_LICENSE / QUEUEY_SOURCE)  >
           queuey.json  >  default

EXIT CODES
  0 success   1 runtime failure   2 usage   3 config   4 assembly load
";
}
