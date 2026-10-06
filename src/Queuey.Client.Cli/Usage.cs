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
  plan           Ask Queuey what apply would change and refuse, as dry runs. Writes nothing.
  verify         Verify a queue's flow with Queuey, step by step from the ingress to the final state.
  schema         Print the JSON Schema for queuey.deploy.json. Reads nothing, needs no credentials.
  pull           Read a workspace back into a deployment file (the inverse of apply).
  credentials    Store delivery secrets a deployment file refers to: credentials set | list.
  keys           Mint an ingress signing key for a queue: keys mint.
  publish        Publish one event to a queue the way a producer does, and print its id for verify.
  events         Read one event's status and attempts as Queuey serves them: events get.
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
               [--repo <url|owner/repo>] [--repo-path <path>] [--commit <sha>] [--no-git]
               [--adopt <queue>|workspace[,…]]
                 Converges the workspace's delivery defaults, then each declared queue's
                 behaviour and destination. Idempotent; exits non-zero unless it fully
                 converged. --dry-run validates the file locally and sends nothing. To ask
                 Queuey what it would change first, run `queuey plan`.
                 --dry-run --json prints { ""schemaVersion"": 2, ""workspace"": …, ""queues"": […] }:
                 the workspace's declaration (null when the file has none) and each queue's,
                 with what the dry run notes about them. Each carries every field the file
                 can set on it, in the file's words, null when the file leaves it out.
                 Version 1, a bare array of queues, was what 0.1.0-preview.8 printed.
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
                 workspace.environment (dev, test, staging or prod; a workspace without one
                 counts as prod) is written first. A key may raise it towards prod, but only a
                 person lowers it: Queuey refuses a key that would, apply stops before it has
                 written anything else, and the error says what a person does instead.
                 Backoff (the wait between attempts: at most an hour at first and a day at
                 most, unless a longer wait is already in place) and a delivery filter are
                 declared per workspace or queue. The number of attempts is not a setting:
                 a file that still declares maxAttempts or dlqAfterAttempts is refused
                 before anything is sent. `queuey schema` lists every field and the values
                 it accepts.
                 A queue's delivery.kind is http, or localForward to send its events to a
                 connected `queuey listen` session (they wait while none is). The listener's
                 address belongs to the session, never to the file. A delivery URL on this
                 machine or a private network is refused before anything is sent: Queuey's
                 delivery never reaches it, and localForward is the way to your machine.
                 ingress.signedRequest { template, credentialRef } verifies a provider's
                 signature, such as Stripe's. A credential that is not stored yet is
                 accepted: the ingress refuses every event until it is stored and apply runs
                 again, and apply says so with the command that stores it.
                 The workspace is the file's ""tenant"" when it names one, else --tenant /
                 QUEUEY_TENANT / queuey.json. When --tenant or QUEUEY_TENANT names another
                 workspace than the file, apply fails and names both. plan and verify use the
                 same rule.
                 Queuey marks each queue apply writes, and the workspace's settings when the
                 file declares them, as managed by the file: the repository, the file's path
                 in it and the commit. They come from git (origin's URL without user info, the
                 path from the repository root, HEAD, left out when the file has uncommitted
                 changes or git ignores it) unless --repo, --repo-path or --commit gives them;
                 --no-git leaves git alone. git is the one in PATH, never one in the current
                 directory. A change to a managed queue's or workspace's configuration from
                 anywhere else is refused, or only warned about, with where the file is. A
                 person can detach one, with a reason: apply then skips it, and --check reports
                 it with who detached it, when and why. --adopt takes it back: apply shows what
                 the file changes on it, then writes it. A queue named workspace is taken back
                 with --adopt queue:workspace.
                 --json prints { ""schemaVersion"": 1, ""file"", ""source"", ""enforcement"",
                 ""skipped"": […], ""queues"": […], … }; --check --json { ""schemaVersion"": 1,
                 ""file"", ""inSync"", ""drift"": […], ""detached"": […] }.

PLAN
  queuey plan [--file queuey.deploy.json] [--adopt <queue>|workspace[,…]] [--json]
                 Asks Queuey itself what apply would do: every write apply would send goes as
                 a dry run (?dryRun=true), so it shows each value that would change and each
                 refusal Queuey would give that write — retention caps, queue limits, bad
                 values — with what to do about it. Each write is asked about on its own,
                 against what is stored now: a refusal that depends on a workspace change in
                 the same file shows only in apply. Writes nothing; exits non-zero if anything
                 would be refused. Needs the key apply needs. A queue that does not exist yet
                 shows as one that would be created, with its settings checked locally. The first dry run also proves that Queuey answers
                 dry runs; against an API that does not, planning stops there and says what
                 that one call may have changed — nothing, when a declared queue exists.
                 A verb and not an apply flag on purpose: a CLI too old to know it answers
                 ""Unknown command"" instead of running the apply you meant to plan.
                 It shows each queue's ingress URL, also for one that would be created, and
                 the plan's id and hash: sha256 over what apply would change and the server
                 state it rests on, so the same file against the same state gives the same
                 hash, whatever the order of its queues or its formatting.
                 --json prints { ""schemaVersion"": 1, ""file"", ""tenant"", ""planId"", ""planHash"",
                 ""wouldSucceed"", ""changeCount"", ""queues"": […], ""steps"": […] }; check
                 schemaVersion first. A change's from and to are JSON values, as Queuey's
                 config reads them back.
                 A queue or workspace a person detached from the file gets no steps, as apply
                 skips it; it is listed under ""skipped"", with who detached it. --adopt plans
                 it as apply --adopt would write it.

VERIFY
  queuey verify <queue> --event <evt_…>
  queuey verify <queue> --event-type <type> [--ingress-auth <template>]
  queuey verify <queue> --send (--data <json> | --file <path> | --stdin) [--event-type <type>]
                [--timeout <seconds>] [--deployment queuey.deploy.json] [--json]
                 Verifies the queue's flow with Queuey's flow verification, and reads it
                 until Queuey has settled it: each step from the ingress to the final state
                 (ingress_reached, ingress_auth, persisted, routed, delivery_attempted,
                 delivery_auth, receiver_response, final_state), with its evidence.
                 Verify the producer's own events; that works everywhere:
                 --event follows an event already in the queue: publish one the way the
                 producer does, with its key, and pass the event id the ingress answered.
                 --event-type waits for the next event the ingress takes with that type,
                 from the start on: trigger it once verify says it is waiting, such as with
                 stripe trigger. --ingress-auth adds that the ingress verified it with that
                 signed-request template, such as stripe.
                 --send has Queuey send a test event through the queue's ingress instead,
                 with --event-type as its type. It works only where all three hold: Queuey
                 has active verification switched on (production Queuey does not today),
                 the key has event.publish in a workspace tagged dev, test or staging (no
                 tag counts as production), and the queue's ingress takes events without a
                 key or a signature. Otherwise Queuey refuses (active_verification_disabled,
                 production_workspace) or answers not_tried. The test event is real and
                 reaches the receiver like any other event, so send data it treats as
                 harmless: one JSON value of at most 64 KB. Queuey never signs it as a
                 provider.
                 --timeout (or --wait) is how long Queuey follows the event, in seconds: a
                 minute when left out, at most 900.
                 Exits 0 only when the verification passed. failed, timed_out and not_tried
                 exit 1 with Queuey's summary, and a refusal exits 1 with Queuey's message.
                 --json prints { ""schemaVersion"": 2, ""tenant"", ""queue"", ""queuePublicId"",
                 ""verification"": { … } }: the verification in Queuey's own shape, with its
                 own schemaVersion; check schemaVersion first. Neither output shows a payload
                 value or a secret: the evidence is ids, statuses, times and header names.
                 <queue> is the queue's name or its id (que_…). A name is looked up in the
                 workspace by apply's rule: the deployment file's ""tenant"" (--deployment,
                 default ./queuey.deploy.json) when it names one, else --tenant /
                 QUEUEY_TENANT / queuey.json; when --tenant or QUEUEY_TENANT names another
                 workspace than the file, verify fails and names both. Needs a key that may
                 read the queue and its events (queue.read, event.read). Against a Queuey
                 without flow verification, verify fails with flow_verification_unavailable
                 and sends nothing. --file is the test event; a deployment file there is
                 refused (name that one with --deployment).

SCHEMA
  queuey schema [--json]
                 Prints the JSON Schema for queuey.deploy.json — every field, the values it
                 accepts and what it does. Save it, or point ""$schema"" at its ""$id"": the copy
                 published at this version's release tag,
                 https://raw.githubusercontent.com/Queuey-AI/queuey-client/v<version>/schema/queuey.deploy.schema.json

PULL
  queuey pull [--file queuey.deploy.json] [--force] [--stdout]
              [--as <environment>] [--emit-code <path.cs>] [--namespace <ns>]
                 Reads the workspace's delivery defaults and every queue back into a
                 deployment file. Inherit-aware — a queue that inherits a section writes
                 nothing for it, so the file says what is actually owned rather than freezing
                 today's defaults as permanent overrides. No secrets: credentials appear by
                 name. Refuses to overwrite an existing file without --force; use --stdout to
                 diff first. A filter condition Queuey stored before it checked it is written
                 as it is, with a warning on stderr: apply refuses the file until it is fixed.
                 --as <env> rewrites the values that do not travel between workspaces (the
                 workspace binding, its environment, the base URL, absolute queue URLs) into
                 ${VAR} references, so one file converges every environment. Paths and
                 credential names travel as they are.
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
  queuey publish <queue> (--data <json> | --file <path> | --stdin)
                 [--idempotency-key <k>] [--event <type>] [--key <group-key>]
                 [--content-type <ct>] [--source <s>] [--deployment queuey.deploy.json] [--json]
                 Publishes ONE event to the queue the way a producer does: with the
                 configured key, to the queue's ingress URL. A fixed --idempotency-key
                 makes the event recognizable: publishing it again answers with the same
                 event (replayed), and nothing new is stored. --event and --key set the
                 event type and the group key for a queue that reads them, as a stream does.
                 The answer has the event's id: follow it with
                 `queuey verify <queue> --event <evt_…>`.
                 When the key may read the queue, what its ingress requires is read first.
                 An ingress that verifies a provider's signature (such as Stripe's) or
                 Queuey's own, or wants a key and a signature together, is refused before
                 anything is sent, saying what it requires: such an event comes from the
                 provider or from the producer that signs it. A key that may only publish
                 skips that read, and the ingress decides; a queue name that starts like a
                 secret (qak_, whsec_, sk_, rk_) is then refused before anything is sent.
                 --json prints { ""schemaVersion"": 1, ""tenant"", ""tenantFrom"", ""queue"",
                 ""queuePublicId"", ""eventPublicId"", ""receivedAtUtc"", ""mode"", ""replayed"",
                 ""verify"" }, never the payload or a key. eventPublicId and verify are null
                 when the ingress answered without a receipt, as a queue whose ingress
                 answers 204 does; any other answer that is not Queuey's receipt is an error.
                 0.1.0-preview.8 printed the bare receipt, with eventId, and needed --event.
                 <queue> is the queue's name, or its id (que_…) when the key may read the
                 workspace's queues. The workspace follows apply's rule: the deployment
                 file's ""tenant"" (--deployment, default ./queuey.deploy.json) when it names
                 one, else --tenant / QUEUEY_TENANT / queuey.json, so verify finds the queue.
                 When the deployment file decides it, publish says so before it sends, and
                 tenantFrom names the file.

EVENTS
  queuey events get <evt_…> --queue <queue> [--content] [--deployment queuey.deploy.json] [--json]
                 Reads one event as Queuey's REST API serves it (GET /events/{queue}/{event}):
                 its status, its times and each attempt with what Queuey decided after it.
                 --queue is the queue's name or its id (que_…): Queuey reads an event within
                 its queue, and publish and verify print the queue's id beside the event's.
                 The payload, the header values and the receiver's responses are the event's
                 content. --content reveals it, only when the key has event.payload.read and
                 the queue's payload visibility lets values out, and Queuey records every
                 look before it answers. Without --content, none of it is read.
                 --json prints { ""schemaVersion"": 1, ""queuePublicId"", ""eventPublicId"", ""status"",
                 ""payloadVisibility"", ""canRevealContent"", ""event"", ""content"" }: event is the
                 envelope as REST returns it, and content is null unless revealed. Needs a key
                 that may read the queue's events (event.read). A queue named by name is found
                 in the workspace by apply's rule, as for publish.

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
                 endpoint is never contacted. Works on any event of a queue that forwards to the
                 listener (Local forward) and shares its payloads in full, a DLQ'd one included; on
                 any other queue the server refuses and says what to change. Run `queuey listen`
                 first so there's a listener to receive it.

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
  An option a command does not take fails it (exit 2) and lists the ones it does.
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
