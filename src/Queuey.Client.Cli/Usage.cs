namespace Queuey.Client.Cli;

internal static class Usage
{
    public const string Text = @"queuey — command-line tool for Queuey

USAGE
  queuey <command> [options]
  queuey --version

COMMANDS
  advise         Read this repository and say how Queuey fits: Client, Edge or plain HTTP. Reads only.
  sync           Apply every [QueueyModel] stream found in an assembly (PUT /waas/streams).
  queue          Declare queues from [QueueyQueue] types: queue plan | queue sync.
  apply          Converge Queuey from a declarative deployment file (queuey.deploy.json).
  verify         Publish one event to a queue and follow it: delivered, or why not and what to change.
  schema         Print the JSON Schema for queuey.deploy.json. Reads nothing, needs no credentials.
  pull           Read a workspace back into a deployment file (the inverse of apply).
  credentials    Store delivery secrets a deployment file refers to: credentials set | list.
  keys           Mint an ingress signing key for a queue: keys mint.
  publish        Publish an event to a stream.
  create-tenant  Create a tenant under the current license.
  create-queue   Create a queue under a tenant.
  metrics        Show a queue's traffic snapshot.
  issues         List a tenant's issues.
  listen         Receive webhooks locally over a secure push session (Stripe-listen style).
  replay         Replay one existing event to your connected listener (read-only DLQ debugging).
  edge           Operate a Queuey Edge spool: status | retry | discard | recover | reset.
  whoami         Show the resolved host / environment / tenant / license (masks the key).

ADVISE
  queuey advise [<path>] [--queue <name>] [--write-files [--force]] [--apply] [--json]
                 Reads the repository (default: the current directory) and recommends how to
                 publish from it — and, when it finds an endpoint that takes webhooks, how to
                 receive safely. Every conclusion names the file it came from, so you can
                 disagree with it.
                 It decides in three steps. Does this send, receive or both. Then, for sending:
                 is there ALREADY durability here (an outbox, a bus, a job queue) — if so,
                 publish from that consumer rather than rebuilding it. Otherwise, does local
                 disk survive a restart, because without that a spool is impossible no matter
                 how unreliable the network is. The network itself is not in the repository,
                 so it is asked rather than guessed.
                 With NO flags it changes nothing and needs no credentials — it prints the
                 advice and the files it WOULD write. That is the default on purpose: an agent
                 runs a command before it reads this text.
                 --write-files writes them: queuey.deploy.json (the committable one) and a
                 .gitignore line for queuey.json (the one holding the API key, which must not
                 be committed). An existing deployment file is kept unless --force.
                 --apply converges the workspace down the same path `queuey apply` takes, and
                 needs credentials. It creates the queue; the API key itself is always minted
                 in the console, because a key that can mint keys turns repo access into
                 account access.
                 The two flags are separate on purpose: a file lands in git diff and is undone
                 with git, while a workspace change is invisible from the repo and is undone in
                 the console. In a .NET project, adding the package stays a step you run
                 (dotnet add package) — editing your project file is a bigger liberty than this
                 command takes. Anywhere else there is no package: the advice is one HTTP call,
                 and where in the repository to make it.

SYNC
  queuey sync --assembly <path.dll> [--dry-run] [--only a,b] [--continue-on-error] [--json]
                 Stops at the first failure by default and reports what it did not attempt;
                 --continue-on-error applies the rest first to collect every failure. Either
                 way a run that did not fully converge exits non-zero. Applying is idempotent,
                 so a fixed re-run converges.

QUEUE
  queuey queue plan --assembly <path.dll> [--only a,b] [--json]
                 Network-free preview of the [QueueyQueue] declarations — no credentials needed.
  queuey queue sync --assembly <path.dll> [--only a,b] [--continue-on-error] [--json]
                 Ensures each queue exists and patches the policy of those that declare one.
                 A queue with no delivery target is reported as a warning, not a failure: it
                 accepts events and logs them without delivering until an endpoint is set.

APPLY
  queuey apply [--file queuey.deploy.json] [--dry-run] [--check] [--continue-on-error] [--json]
                 Converges the workspace's delivery defaults, then each declared queue's
                 behaviour and destination. Idempotent; exits non-zero unless it fully
                 converged. --dry-run validates the file locally and sends nothing.
                 The file carries NO secrets: auth and signing name a credentialRef, so it is
                 meant to be committed. Keep it separate from queuey.json, which holds your
                 API key and must not be.
                 --check writes nothing and exits non-zero when the file and the workspace
                 have diverged — the CI gate. Only what the file declares is compared, so a
                 workspace holding settings the file is silent about is not drift.
                 ${VAR} in a value is expanded from the environment; an unset one is an
                 error, never an empty string. Use ${VAR:-default} when a default is meant.
                 A queue this file creates delivers when it has a destination (its own
                 delivery.url or workspace.delivery.baseUrl) and logs events until it has one.
                 Declare ""mode"": ""deliver"" or ""logOnly"" to own it; an existing queue keeps its
                 mode otherwise. Pausing is an operator's lever: a deploy never resumes a queue.
                 Retry (maxAttempts, dlqAfterAttempts, backoff) and a delivery filter are
                 declared per workspace or queue; `queuey schema` lists every field and the
                 values it accepts.
                 The workspace is the file's ""tenant"" when it names one, else --tenant /
                 QUEUEY_TENANT / queuey.json. When --tenant or QUEUEY_TENANT names another
                 workspace than the file, apply fails and names both. verify uses the same rule.

VERIFY
  queuey verify <queue> (--data <json> | --file <path> | --stdin)
                [--timeout <seconds>] [--event-type <t>] [--content-type <ct>]
                [--deployment queuey.deploy.json] [--json]
                 Publishes ONE event to <queue> and follows it until it is delivered, logged,
                 filtered or failed, or --timeout passes (default 30). Exits 0 only when the
                 receiver got it. Otherwise the verdict — logged_not_delivered, filtered,
                 failed or timeout — names what to change: the mode, the filter, the
                 credential the receiver rejected, held delivery, or the earlier event
                 holding a fifo queue. A failure is reported on its first attempt, not after
                 every retry.
                 The event is real and reaches the receiver like any other: send data it
                 treats as harmless. The workspace follows apply's rule: the deployment file's
                 ""tenant"" (--deployment, default ./queuey.deploy.json) when it names one, else
                 --tenant / QUEUEY_TENANT / queuey.json; when --tenant or QUEUEY_TENANT names
                 another workspace than the file, verify fails and names both. The output names
                 the workspace. Needs a key that may publish and read events; a deploy key can.

SCHEMA
  queuey schema
                 Prints the JSON Schema for queuey.deploy.json — every field, the values it
                 accepts and what it does. Save it, or point ""$schema"" at
                 https://raw.githubusercontent.com/Queuey-AI/queuey-client/main/schema/queuey.deploy.schema.json

PULL
  queuey pull [--file queuey.deploy.json] [--force] [--stdout]
              [--as <environment>] [--emit-code <path.cs>] [--namespace <ns>]
                 Reads the workspace's delivery defaults and every queue back into a
                 deployment file. Inherit-aware — a queue that inherits a section writes
                 nothing for it, so the file says what is actually owned rather than freezing
                 today's defaults as permanent overrides. No secrets: credentials appear by
                 name. Refuses to overwrite an existing file without --force; use --stdout to
                 diff first.
                 --as <env> rewrites the values that do not travel between workspaces (the
                 workspace binding, the base URL, absolute queue URLs) into ${VAR} references,
                 so one file converges every environment. Paths and credential names travel
                 as they are.
                 --emit-code writes [QueueyQueue] declarations for the pulled queues —
                 behaviour only; destinations stay in the deployment file.

KEYS
  queuey keys mint --queue <que_...> [--name <label>] [--json]
                 Mints an ingress signing key so producers can publish with HMAC instead of
                 an API key. The secret is shown ONCE. Needs a credential with key-management
                 rights — a deploy key deliberately has none, since a key that can mint keys
                 turns pipeline access into account access.

CREDENTIALS
  queuey credentials set --name <name> --from-env <ENV_VAR> [--type <type>]
                [--key-id <id>] [--username <u>] [--json]
                 Stores a delivery secret under the workspace and names it, so a deployment
                 file can refer to it as credentialRef. The value is read from the
                 environment — never an argument, which would land in shell history and CI
                 logs — is encrypted at rest, and is never readable again.
  queuey credentials list [--json]

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
  queuey edge run     --spool <path> --tenant <ten_...> --api-key <qak_...>
                [--listen <port>] [--report-health] [--node-name <name>] [--ingress-base <uri>] [--source <s>]
                [--mqtt <host[:port]> --mqtt-routes ""filter=queue[@segment];…"" [--mqtt-user <u> --mqtt-password <p>] [--mqtt-tls]]
                 Hosts the Edge transfer loop as a standalone daemon (systemd-friendly) — the
                 complete Edge for machines with no .NET app of their own: run this, and anything
                 on the box publishes durably with 'queuey edge publish'. --listen additionally
                 serves a LOOPBACK publish endpoint with the same wire shape as cloud ingress
                 (POST http://localhost:<port>/events/{tenant}/{queue}) — any language's plain
                 HTTP one-liner becomes durable by swapping the base URL; 202 = committed to the
                 local spool. Ctrl-C/SIGTERM to stop; accepted events survive restarts.
                 --report-health (or QUEUEY_REPORT_HEALTH=1) makes the node check in to the
                 console under Edge nodes (outbound only; reports are not events, never billed);
                 --node-name (QUEUEY_NODE_NAME) is the label shown there, default: machine name.
                 --mqtt subscribes to a (usually local) broker and spools every message durably
                 BEFORE acking it (QoS 1); a route's @segment makes that topic level the lane
                 (FIFO per machine). Env: QUEUEY_MQTT, QUEUEY_MQTT_ROUTES, QUEUEY_MQTT_USER/PASSWORD.
  queuey edge publish <queue> --spool <path> --tenant <ten_...>
                (--data '<json>' | --file <path>) [--content-type <ct>]
                [--idempotency-key <k>] [--event-type <t>] [--group-key <k>]
                [--occurred-at <iso8601>] [--source <s>] [--json]
  queuey edge status  --spool <path> [--json]
  queuey edge kick    --spool <path>
  queuey edge drain   --spool <path> [--timeout <seconds>]
  queuey edge retry   --spool <path> (--id N | --all)
  queuey edge discard --spool <path> --id N
  queuey edge recover --spool <path>
  queuey edge reset   --spool <path> --accept-data-loss
                 Operates a Queuey Edge spool file directly (WAL allows this alongside a running
                 host). publish DURABLY enqueues one event into the local spool — the shell/IoT
                 path: any program on the machine (bash, Python, cron) hands events to the
                 co-resident Edge host, which transfers them with full retry/offline handling;
                 no host running means the event waits durably for the next one. status shows
                 pending/quarantined/oldest-age; drain waits until a running host has emptied
                 the backlog (the uninstall gate — never delete a spool with pending events);
                 retry returns a quarantined event to the drain after remediation; discard drops
                 ONE quarantined event (an explicit, logged operator decision — pending events
                 cannot be discarded); recover salvages readable events from a faulted spool,
                 reporting exactly how many were unreadable; reset abandons the spool (requires
                 --accept-data-loss, the old file is preserved for support either way).

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
