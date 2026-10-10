namespace Queuey.Client.Cli;

internal static class Usage
{
    public const string Text = @"queuey — command-line tool for Queuey

USAGE
  queuey <command> [options]
  queuey --version

COMMANDS
  login          Log in with your Queuey account: open the link it prints and approve the code. No key to copy.
  logout         End the login with Queuey and remove it from this machine.
  advise         Read this repository and say how Queuey fits: Client, Edge or plain HTTP. Or turn a Desired
                 Flow into a deployment file and a code plan. Reads only.
  sync           Apply every [QueueyModel] stream found in an assembly (PUT /waas/streams).
  queue          Declare queues from [QueueyQueue] types: queue plan | queue sync. queue health reads a
                 queue's traffic, receivers and blocked lanes.
  apply          Converge Queuey from a declarative deployment file (queuey.deploy.json).
  plan           Ask Queuey what apply would change and refuse, as dry runs. Writes nothing.
  verify         Verify a queue's flow with Queuey, step by step from the ingress to the final state.
  schema         Print the JSON Schema for queuey.deploy.json, or a Desired Flow's. Reads nothing, needs no credentials.
  pull           Read a workspace back into a deployment file (the inverse of apply).
  credentials    Store delivery secrets a deployment file refers to: credentials set | generate | rotate | request
                 | list. generate makes a receiver's secret and writes it with --write, never shown.
  keys           Mint the key a producer publishes with, for the workspace or one queue (--queue), as
                 --type signing (default) or api-key, written with --write .env|user-secrets: keys mint | list | revoke.
  publish        Publish one event to a queue the way a producer does, and print its id for verify.
  events         Read events as Queuey serves them: events get (one, with its attempts) | events search.
  create-tenant  Create a tenant under the current license.
  create-queue   Create a queue under a tenant.
  metrics        Show a queue's traffic snapshot.
  issues         List a tenant's issues.
  listen         Receive webhooks locally over a secure push session (Stripe-listen style).
  replay         Replay one event to your connected listener (read-only DLQ debugging), or send events to
                 the queue's receiver again (--redeliver, --status dlq).
  diagnose       Say why a queue's deliveries fail: Queuey's incident report and what the failures share.
  resume         Verify a queue's receiver and resume delivery to it.
  unlock         Lift the lock Queuey put on a queue's delivery.
  edge           Operate a Queuey Edge spool: status | retry | discard | recover | reset.
  whoami         Show the resolved hosts / tenant / license, and the key (masked) or the login.

LOGIN
  queuey login [--profile <name>] [--scope operate|read] [--wait] [--no-browser] [--api-base <uri>]
               [--ingress-base <uri>] [--json]
                 Logs in the way `stripe login` does: it prints a link and a code, you open the
                 link, check that the page shows the same code, and approve it in the Queuey
                 console. In a terminal it opens the browser and waits.
                 Without a terminal, as when an agent runs it, it prints the link and exits 5
                 (waiting for a person); with --json as one line, { ""status"":
                 ""waiting_for_person"", ""link"", ""userCode"", ""expiresAt"", ""action"" }. The
                 code is kept for ten minutes, so `queuey login --wait` (or `queuey login` again)
                 finishes once it is approved. --wait waits in any case.
                 Already logged in? It says so and exits 0, so it is safe to run first.
                 The login is a connection for one person, one license and a scope: operate (the
                 default) acts as the person may, read only looks. It never approves anything:
                 what needs a person in prod still goes to the inbox. Every command that has no API
                 key uses it, for the API host it was made for.
                 --profile <name> writes the profile in ~/.queuey/config.json: the license, the
                 hosts and the workspace marked with the profile's environment (dev, test,
                 staging or prod), or --tenant's. Other profiles, and the profile's other fields,
                 stay as they were. A profile with an apiKey keeps it, the key wins, and login
                 never writes its hosts; it adds a license or workspace only where none is. A QUEUEY_ variable that disagrees with the
                 profile fails, as with any --profile; --api-base overrides the host.
                 The tokens are in ~/.queuey/credentials.json, next to config.json, readable only
                 by you and checked as config.json is. They renew themselves; two commands at
                 once take turns, so a refresh token is never spent twice.
                 Publishing to the ingress still needs a key: the ingress does not take a login.
                 The login keeps the ingress host Queuey gives with it; --ingress-base sets it
                 instead, and renewals keep that. Against a Queuey on this machine whose
                 ingress host is elsewhere, login warns. plan says where the ingress host came
                 from (the flag, a variable, queuey.json, the profile, or the login).
  queuey logout [--profile <name>] [--license <lic_…>] [--api-base <uri>] [--json]
                 Ends the login with Queuey (it disappears from Connected apps) and removes it
                 from this machine: every login for the API host, or only --license's. When Queuey
                 cannot be told, it is still removed here, and the command exits 1 and says so.

ADVISE
  queuey advise [<path>] [--queue <name>] [--write-files [--force]] [--apply] [--json]
  queuey advise [<path>] --intent <flow.json> [--queue <name>] [--profile <name>] [--json]
                 Reads the repository (default: the current directory) and recommends how to
                 publish from it — and, when it finds an endpoint that takes webhooks, how to
                 receive safely. Every conclusion names the file it came from, and it lists
                 every file it read (filesRead in --json), so you can disagree with it.
                 It decides in three steps. Does this send, receive or both. Then, for sending:
                 is there ALREADY durability here (an outbox, a bus, a job queue) — if so,
                 publish from that consumer rather than rebuilding it. Otherwise, does local
                 disk survive a restart, because without that a spool is impossible no matter
                 how unreliable the network is. The network itself is not in the repository,
                 so it is asked rather than guessed.
                 With NO flags it changes nothing and needs no credentials — it prints the
                 advice and the files it WOULD write. Its only call is with a login (queuey
                 login), to the host the login is for, to read the license's plan; it never
                 sends an API key. That is the default on purpose: an agent
                 runs a command before it reads this text.
                 --write-files writes them: queuey.deploy.json (the committable one) and
                 .gitignore lines for .env and queuey.json, which hold keys and must not be
                 committed. The deployment file secures the workspace (signed requests with
                 Queuey's own template, which every queue inherits), keeps events no longer
                 than the license's plan allows (read with your login, else 7 days), and has
                 a dev profile: `queuey apply --profile dev` makes a dev workspace from it. An
                 existing deployment file is kept unless --force.
                 --apply converges the workspace down the same path `queuey apply` takes, and
                 needs a login (queuey login). It creates the queue. The app's signing key is
                 the workspace's: `queuey keys mint --write user-secrets` in a .NET project with
                 a UserSecretsId, else `queuey keys mint --write .env` (see KEYS; --queue limits it
                 to one queue). It needs a login that may manage keys and never shows the secret;
                 a license-wide API key is a person's, minted in the console.
                 The two flags are separate on purpose: a file lands in git diff and is undone
                 with git, while a workspace change is invisible from the repo and is undone in
                 the console. In a .NET project, adding the package stays a step you run
                 (dotnet add package) — editing your project file is a bigger liberty than this
                 command takes. Anywhere else there is no package: the advice is one HTTP call,
                 and where in the repository to make it.
                 --intent starts from what you want instead: a Desired Flow, a JSON file with
                 the source (stripe, supabase or app), the destination's route and what the
                 receiver needs, each field an object with its value and its provenance. Mark
                 what you say ""stated"". advise fills in the rest from the repository, marked
                 ""evidence"" with the file and line (the handler and its route, Stripe's
                 constructEvent or a secret compared in a header, the raw body, the framework,
                 the port), or ""assumed"". Then it proposes a deployment file and a code plan,
                 kept apart, with the reason for each setting and what it rests on: stated,
                 evidence, default or recommendation. What differs between environments goes
                 in a profile named after the flow's environment, so plan and apply take it
                 with that profile; a deployment file that is there without profiles keeps
                 fixed values. Where the repository contradicts the intent, points several
                 ways, or the intent asks for what Queuey cannot do, it lists conflicts,
                 proposes nothing and exits 1: answer them in the intent, and run it again.
                 Only stated fields are intent, so the flow it returns can go back in. It
                 writes nothing, so --write-files and --apply do not go with it.
                 Without an environment in the intent, a new file is for dev, and a
                 queuey.deploy.json that is there without workspace.environment is for prod,
                 as Queuey counts such a workspace: delivery over HTTP, credentials request,
                 and a real Stripe endpoint. A local listener and test mode need a new file or
                 one that gives dev: a stated dev against a file or profile that gives none,
                 or another, is a conflict, and the file is never rewritten to dev.
                 A file with profiles gets no new profile: the profile that gives the
                 environment takes the flow, chosen by that and never by its name. Without
                 one stated, one profile takes it, and several are a conflict. One profile
                 that gives none takes a stated environment, but never dev: Queuey treats its
                 workspace as prod. --profile <name> names the
                 profile instead, for profiles that share an environment; the file must have
                 it, unless advise proposes a new file. A queue the file forwards to a
                 listener outside dev is a conflict, and so is an environment apply refuses.
                 A file that takes its environment from a variable, without a profile value
                 or a stated environment that holds it, gets the delivery kind from one too,
                 without a default.
                 The schema command prints the Desired Flow's schema (see SCHEMA).
                 infrastructure.content is the whole deployment file, to write as it is: with a
                 queuey.deploy.json there, that file with the flow's queue and profile values
                 merged in. The file wins over what the intent does not state; a stated value
                 it contradicts, or a file apply cannot read, is a conflict. So is a root
                 queuey.deploy.json advise does not read: a link, a folder, a file over 512 KiB,
                 one it may not open, or no regular file, such as a pipe. It is read before the
                 scan, outside its limits. infrastructure.credentials names each secret the
                 file refers to, never its value, with the command that stores it:
                 credentials set in dev, for a value you hold, such as the Stripe CLI's test
                 secret, and credentials request elsewhere, where a person pastes it.
                 --json prints { ""schemaVersion"": 1, ""outcome"": ""proposed"" or ""conflicts"",
                 ""flow"", ""existing"", ""scanLimited"", ""infrastructure"", ""code"",
                 ""nextSteps"" }; check schemaVersion first. ""flow"" is the enriched Desired
                 Flow, to keep beside the code or in the pull request. It explains
                 queuey.deploy.json, and apply never reads it. ""existing"" is Queuey where the
                 repository has it already. ""scanLimited"" lists what the scan left out: it
                 follows no symbolic link, and stops at fixed limits of files, folders, bytes
                 and time. Without --intent, --json carries the same schemaVersion and lists
                 ""candidates"": the Stripe and Supabase flows the repository shows, each a
                 Desired Flow with its evidence. advise reads the names in a .env file and
                 never a value, and shows no payload from the repository.

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
  queuey queue health <queue> [--profile <name>] [--json]
                 The queue's traffic snapshot, its receivers' state and its blocked lanes, as
                 Queuey reads them. <queue> is the name or the id (que_…); a name is looked up in
                 the workspace (--tenant, the profile's, or queuey.deploy.json's). The lanes need
                 event.read; without it the rest is shown.

APPLY
  queuey apply [--file queuey.deploy.json] [--dry-run] [--check] [--continue-on-error] [--json]
               [--repo <url|owner/repo>] [--repo-path <path>] [--commit <sha>] [--no-git]
               [--adopt <queue>|workspace[,…]] [--profile <name>]
  queuey apply [--file queuey.deploy.json] --plan <plan_…> [--wait [--timeout <seconds>]] [--json]
                 Converges the workspace's delivery defaults, then each declared queue's
                 behaviour and destination. Idempotent; exits non-zero unless it fully
                 converged. --dry-run validates the file locally and sends nothing. To ask
                 Queuey what it would change first, run `queuey plan`.
                 When no workspace is named anywhere (the file's tenant, --tenant,
                 QUEUEY_TENANT, queuey.json or the profile) and the file says
                 workspace.environment dev or test, apply creates a workspace marked with it,
                 named queuey-<environment>, and applies to that. Never in CI (CI,
                 GITHUB_ACTIONS and the like are set), and never for a file whose delivery
                 names a credentialRef, which a new workspace does not have: those fail as
                 a missing workspace, with queuey create-tenant as the way out. Once
                 created, every output says it, errors too: ""createdWorkspace"" in --json,
                 with ""tenantOption"": ""--tenant ten_…"". Name it, or the next apply creates
                 another.
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
                 The CLI's own QUEUEY_ settings are never read (a file may use QUEUEY_TENANT,
                 QUEUEY_WORKSPACE_ENVIRONMENT and the QUEUEY_…_URL and QUEUEY_…_DELIVERY_KIND
                 names queuey pull writes), and a value that starts like a secret (qak_,
                 whsec_, sk_live_ …) is refused: what a file expands is stored in Queuey and
                 sent on. A receiver whose URL carries a token takes the whole URL from one
                 variable, such as ${ORDERS_HOOK_URL}; a secret sent in a header is a
                 credential, named in credentialRef with an authMode.
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
                 connected `queuey listen` session (they wait while none is), only in a
                 workspace marked dev: Queuey refuses it elsewhere, and plan says so first,
                 also for a queue it would create. The listener's
                 address belongs to the session, never to the file. A delivery URL on this
                 machine or a private network is refused before anything is sent to
                 Queuey's own hosts, whose delivery never reaches it; localForward is the
                 way to your machine. A Queuey on this machine or a private network decides
                 for itself, and a dry run, which does not connect, only warns.
                 ingress.signedRequest { template, credentialRef } verifies a provider's
                 signature, such as Stripe's. A credential that is not stored yet is
                 accepted: the ingress refuses every event until it is stored, and storing it
                 points the ingress at it at once (a Queuey from before credential requests
                 needs another apply). apply says so with the command that stores it:
                 credentials request, where a person pastes the value, unless the file's
                 workspace.environment is dev, where credentials set stores a value you hold.
                 Without --profile, the command names the file's tenant with --tenant.
                 A delivery's credentialRef is different: the delivery cannot be sent
                 without the credential, so a name the workspace has no credential under is
                 refused before anything is written (credential_not_found, exit 1), in plan
                 too, with the command that stores it.
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
                 Configuration plans: when Queuey applies to the workspace from an API key
                 only through a plan (plan_required, a production workspace), apply makes
                 the plan itself, as queuey plan does, and applies it at once when the
                 policy runs it. When a person approves it, apply prints where, writes
                 nothing and exits 5; --wait waits for the approval, at most --timeout
                 seconds (default 1800), then applies it. --plan plan_… applies a plan
                 Queuey stores once it may be: approved, or run by the policy; a plan sealed
                 for a person and not sent yet goes to the inbox first. Its source
                 and what it takes back are the plan's, so --adopt, --repo, --repo-path and
                 --commit do not go with it. Each write is then one of the plan's steps,
                 sent once, and Queuey's step_already_applied counts as written. A write
                 whose answer was lost is looked up in the plan after a short, growing wait,
                 and sent once more only when the plan does not show it. plan_stale (what the plan rests on
                 moved) and not_in_plan (the file no longer matches it) exit 1: plan again,
                 and the new plan shows what is left. Until Queuey enforces plans, an apply
                 without one goes through, and Queuey's would_require_approval warning is
                 written to stderr with what to do. Against a Queuey that stores no plans,
                 apply works as before.
                 --json prints { ""schemaVersion"": 1, ""file"", ""plan"", ""source"", ""enforcement"",
                 ""skipped"": […], ""serverWarnings"": […], ""queues"": […], … }, with ""plan"" null
                 unless the apply wrote a stored plan, and { ""schemaVersion"": 1, ""file"",
                 ""pendingApproval"": true, ""plan"": { … } } when the plan waits for a person;
                 --check --json { ""schemaVersion"": 1, ""file"", ""inSync"", ""drift"": […],
                 ""detached"": […] }.

PLAN
  queuey plan [--file queuey.deploy.json] [--adopt <queue>|workspace[,…]] [--profile <name>] [--json]
              [--local | --store | --submit] [--repo <url|owner/repo>] [--repo-path <path>] [--commit <sha>] [--no-git]
                 Asks Queuey itself what apply would do: every write apply would send goes as
                 a dry run (?dryRun=true), so it shows each value that would change and each
                 refusal Queuey would give that write — retention caps, queue limits, bad
                 values — with what to do about it. Each write is asked about on its own,
                 against what is stored now: a refusal that depends on a workspace change in
                 the same file shows only in apply. Changes no configuration; exits non-zero
                 if anything would be refused. Needs the key apply needs. A queue that does
                 not exist yet shows as one that would be created, with its settings checked
                 locally. With --local, the first dry run also proves that Queuey answers
                 dry runs; against an API that does not, planning stops there and says what
                 that one call may have changed — nothing, when a declared queue exists.
                 A verb and not an apply flag on purpose: a CLI too old to know it answers
                 ""Unknown command"" instead of running the apply you meant to plan.
                 By default the plan is made here and stored nowhere (--local says so):
                 its hash is sha256 over what apply would change and the server state it
                 rests on, so the same file against the same state gives the same hash,
                 whatever the order of its queues or its formatting. Fit for a pull request.
                 --store stores it in Queuey as a configuration plan (plan_…): its dry runs
                 are its steps, and Queuey seals it with a hash and the policy's decision.
                 execute: queuey apply applies it by its id (see APPLY). requires_approval:
                 apply sends it to Queuey's inbox, and --submit does it at once, prints
                 where a person approves it and exits 5. denied: nothing applies it, exit 1.
                 A plan with a refused write is not sealed. Where the file is (--repo,
                 --repo-path, --commit, git) goes with it, as for apply, and in GitHub
                 Actions also the branch, workflow and pull request. Against a Queuey that
                 stores no plans, it plans here, and says so.
                 It shows each queue's ingress URL, also for one that would be created.
                 --json prints { ""schemaVersion"": 2, ""file"", ""tenant"", ""planId"", ""planHash"",
                 ""stored"": true, ""version"", ""status"", ""decision"", ""rule"", ""class"",
                 ""approvalUrl"", ""expiresAt"", ""wouldSucceed"", ""changeCount"", ""queues"": […],
                 ""steps"": […], ""skipped"": […], ""warnings"": […] } with --store or --submit,
                 and { ""schemaVersion"": 1, … ""planId"": null, ""planHash"": ""sha256:…"" … } for a
                 plan made here; check schemaVersion first. A change's from and to are JSON
                 values, as Queuey's config reads them back.
                 A queue or workspace a person detached from the file gets no steps, as apply
                 skips it; it is listed under ""skipped"", with who detached it. --adopt plans
                 it as apply --adopt would write it.

VERIFY
  queuey verify <queue> --event <evt_…>
  queuey verify <queue> --event-type <type> [--ingress-auth <template>]
  queuey verify <queue> --send (--data <json> | --file <path> | --stdin) [--event-type <type>]
                [--timeout <seconds>] [--background] [--deployment queuey.deploy.json] [--profile <name>] [--json]
  queuey verify <queue> --wait <ver_…> [--json]
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
                 --background starts the verification and returns at once with its id and
                 the command that reads the outcome: queuey verify <que_…> --wait <ver_…>,
                 which reads it until Queuey has settled it. Queuey follows the event either
                 way, so trigger it once --background has returned.
                 Exits 0 only when the verification passed. failed, timed_out and not_tried
                 exit 1 with Queuey's summary, and a refusal exits 1 with Queuey's message.
                 --background is the exception: it exits 0 once the verification is started,
                 whatever comes of it, so it is no gate. The gate is --wait <ver_…>.
                 --json prints one JSON object per line (NDJSON), each with ""schemaVersion"": 3
                 and a ""status"". The first, once Queuey follows the event and before verify
                 waits, is { ""status"": ""waiting"", ""verificationId"", ""queuePublicId"", ""mode"",
                 ""eventType"", ""observeUntil"", ""message"", ""next"" }: trigger the event then.
                 None comes when Queuey settled it at once. The last is { ""status"": ""done"",
                 ""tenant"", ""queue"", ""queuePublicId"", ""verification"": { … } }: the verification
                 in Queuey's own shape, with its own schemaVersion; check schemaVersion first.
                 --background --json prints only the waiting line, with ""next"" set. Neither output shows a payload
                 value or a secret: the evidence is ids, statuses, times and header names.
                 <queue> is the queue's name or its id (que_…). --queue <queue> is another
                 name for it, as in queuey verify --queue orders --event <evt_…>: give one
                 or the other. A name is looked up in the workspace by apply's rule: the
                 deployment file's ""tenant"" (--deployment, default ./queuey.deploy.json)
                 when it names one, else --tenant / QUEUEY_TENANT / queuey.json; when
                 --tenant or QUEUEY_TENANT names another workspace than the file, verify
                 fails and names both. Needs a key that may read the queue and its events
                 (queue.read, event.read). Against a Queuey without flow verification,
                 verify fails with flow_verification_unavailable and sends nothing. --file
                 is the test event; a deployment file there is refused (name that one with
                 --deployment).

SCHEMA
  queuey schema [--flow] [--json]
                 Prints the JSON Schema for queuey.deploy.json — every field, the values it
                 accepts and what it does. Save it, or point ""$schema"" at its ""$id"": the copy
                 published at this version's release tag,
                 https://raw.githubusercontent.com/Queuey-AI/queuey-client/v<version>/schema/queuey.deploy.schema.json
                 --flow prints the Desired Flow's schema instead: the intent advise reads.
                 Each release publishes it beside the other, as schema/queuey.flow.schema.json.

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
  queuey keys mint [--queue <name|que_...>] [--type signing|api-key] [--write <target>] [--show-secret]
                   [--name <label>] [--profile <name>] [--json]
                 Mints the key a producer publishes with: for one queue with --queue, else for
                 every queue in the workspace (--tenant or the profile's), also queues made
                 later. --type signing (the default) gives QUEUEY_SIGNING_KEY_ID and
                 QUEUEY_SIGNING_SECRET, which the app signs each event with; api-key gives
                 QUEUEY_API_KEY, a key that can only publish there. Needs a login (queuey login)
                 for a person who may manage keys, or a credential with key-management rights.
                 In a prod workspace a login gets approval_required: it exits 5 with the link
                 where a person makes the key, and nothing is minted or written.
                 --write puts the values where the app reads them and never prints the secret,
                 in --json neither:
                   .env (or another file): the lines that set them are replaced, every other
                   line stays, and the file is 0600 afterwards (one others could read is
                   tightened, and the answer says from what). In a git repository the file must
                   be one git ignores, and it may not be a link, or nothing is minted.
                   user-secrets: the user secrets of the .NET project in this folder, set with
                   `dotnet user-secrets set`, the values on its stdin, never as arguments. The
                   project needs a UserSecretsId (`dotnet user-secrets init` adds one).
                   `dotnet user-secrets list` runs first, before anything is minted or stored,
                   and the answer names the id it uses. Both evaluate the project with MSBuild,
                   so they run its build logic: trust the project as you would for dotnet build.
                 Without --write nothing is minted: it says which variables the app needs, the
                 --write that fits this folder, and the console page where a person makes or
                 looks at the key. --show-secret mints and prints the secret instead, once, and
                 warns.
                 The SDK reads the values with QueueyOptions.UseEnvironmentVariables() (the
                 environment, and .env in Development) or UseSettings(key => configuration[key])
                 (.NET configuration, user secrets among it).
  queuey keys list [--queue <name|que_...>] [--profile <name>] [--json]
                 The queue's keys, or without --queue the workspace's: id, name, scope, who
                 minted it, active or revoked, last use. Never a secret.
  queuey keys revoke <keyId> [--reason <text>] [--profile <name>] [--json]
                 Every producer that signs with the key is refused at the ingress at once. In
                 a prod workspace an API key gets 403 approval_required, and a person revokes.

CREDENTIALS
  queuey credentials generate <name> --write <target> [--replace] [--profile <name>] [--json]
                 The receiver's secret: a random value (32 bytes, base64url) made here, stored
                 in Queuey as the HmacSigning credential <name> that deliveries are signed with,
                 and written where the receiver reads it as QUEUEY_DELIVERY_SECRET. The same
                 value in both places, never shown. --write takes .env (or another file git
                 ignores) or user-secrets, as for keys mint. The answer says how the queue's
                 delivery points at it: ""delivery"": { ""signing"": { ""enabled"": true,
                 ""credentialRef"": ""<name>"", ""templateKey"": ""queuey"" } } in queuey.deploy.json,
                 then apply. templateKey ""queuey"" is Queuey's own signature, which the verifier
                 checks. In .NET the
                 receiver verifies with QueueyDeliveryVerifier.FromEnvironment(). A name Queuey
                 already holds is refused unless --replace, which makes a new value for both, used
                 by every queue and ingress that names it. So is a target that already holds a
                 QUEUEY_DELIVERY_SECRET: it is read first, and only --replace overwrites it.
                 When Queuey gives the store to a person, it exits 5 with the link, and nothing
                 is written.
  queuey credentials set --name <name> --from-env <ENV_VAR> [--type <type>]
                [--key-id <id>] [--username <u>] [--replace] [--profile <name>] [--json]
                 Stores a delivery secret under the workspace and names it, so a deployment
                 file can refer to it as credentialRef. The value is read from the
                 environment — never an argument, which would land in shell history and CI
                 logs — is encrypted at rest, and is never readable again. An ingress that
                 waits for the name verifies with it at once. For a name the workspace has,
                 the value it already holds is stored as before and keeps its version, so
                 running the same set again changes nothing. A different value is refused
                 (credential_exists, exit 1): replacing it changes every queue and ingress
                 that uses the credential at once, and is a decision for a person.
                 --replace replaces it, as a new version of its secret under the same id.
                 The commands advise, plan and apply suggest never carry --replace.
  queuey credentials rotate --name <name> --from-env <ENV_VAR> [--grace <minutes>]
                [--expect-version <n>] [--profile <name>] [--json]
                 Rotates the secret of a credential the workspace has: the value, read from
                 the environment, becomes a new version under the same id, and everything
                 that uses the credential switches to it at once. Queuey runs it as the
                 operation rotate_credential. Without --grace the previous secret stops
                 verifying at once. --grace <minutes> (1 to 1440, HmacSigning only) keeps
                 it verifying at the ingress that long, so senders can switch over; each
                 event records which version verified it. A Stripe ingress verifies only
                 the new secret: Stripe's own roll signs with both while it lasts. Rotating
                 again while a window is open keeps only the secret it replaces, and without
                 --grace closes it, so a leaked old secret stops verifying at once. That
                 holds for the value the credential already holds too: it rotates nothing,
                 but without --grace it closes an open window, and the output says how the
                 window stands. --expect-version <n> rotates only while the credential holds
                 version n of its secret (credentials list shows it); another is refused
                 (credential_changed_meanwhile, exit 1) and nothing is stored. A name the
                 workspace has no credential under is refused (credential_not_found, exit 1).
                 Where Queuey's policy gives the rotation to a person, as an API key's
                 without a window in a prod workspace, nothing is stored and the value is
                 not kept: a person pastes the new value on the link rotate prints, and the
                 rotation runs then. Exit 5; --json prints { ""status"": ""pending_approval"",
                 ""approvalUrl"", ""credentialRequest"", ""expiresAt"", ""policyRule"", ""message"" }.
  queuey credentials request <name> [--type <type>] [--key-id <id>] [--username <u>]
                [--profile <name>] [--json]
                 Asks a person for a secret, so it never passes through this terminal or a
                 conversation: Queuey opens a one-time request and prints the console link
                 where a person who can manage the workspace's credentials signs in and
                 pastes the value. No API returns it, and Queuey uses it only where the
                 workspace's configuration does. Without --type it asks for an HmacSigning
                 secret, which Queuey never sends as it is; a key or token Queuey sends to
                 a receiver is asked for with its --type. The link works once, for a day,
                 and asking again for the same name while it is open prints the same link.
                 A new HmacSigning credential gets the name as its key id unless --key-id
                 is given, and a new BasicPassword needs --username. For a name the
                 workspace has, only the secret is replaced: another key id or username is
                 refused, and credentials set changes them. OAuth2Certificate is refused: a
                 certificate is uploaded in the Queuey console. --json prints
                 { ""schemaVersion"": 1, ""requestId"", ""workspaceId"", ""workspaceName"",
                 ""organizationName"", ""name"", ""type"", ""keyId"", ""username"", ""status"",
                 ""url"", ""expiresAt"", ""replacesCredentialId"" }; replacesCredentialId names
                 the credential whose secret the value replaces, when the name has one.
  queuey credentials list [--profile <name>] [--json]
                 Lists the workspace's credentials by name and type, with the version of
                 each secret and, while a rotation's grace window is open, until when the
                 previous version still verifies. Never a value.

PUBLISH
  queuey publish <queue> (--data <json> | --file <path> | --stdin)
                 [--idempotency-key <k>] [--event <type>] [--key <group-key>]
                 [--content-type <ct>] [--source <s>] [--deployment queuey.deploy.json]
                 [--profile <name>] [--json]
                 Publishes ONE event to the queue the way a producer does: with the
                 configured key, to the queue's ingress URL. A fixed --idempotency-key
                 makes the event recognizable: publishing it again answers with the same
                 event (replayed), and nothing new is stored. --event and --key set the
                 event type and the group key for a queue that reads them, as a stream does.
                 The answer has the event's id: follow it with
                 `queuey verify <queue> --event <evt_…>`.
                 Without an API key (as with a login, which the ingress does not take), it
                 signs with QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET from the
                 environment, else from ./.env, where `queuey keys mint` writes them (see
                 KEYS). Only those two are read from .env, only from a plain file of your own,
                 and a key that is set wins. --json says where (signingKeyFrom).
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
  queuey events search <queue> [--status dlq,failed] [--idempotency-key <k>] [--event-type <t>]
                [--group-key <k>] [--source <s>] [--from <time>] [--to <time>] [--has-failures]
                [--response-code <n>] [--page <n>] [--page-size <n>] [--profile <name>] [--json]
                 A queue's events, newest first (GET /events/{queue}): by status (a comma list),
                 the Idempotency-Key the producer sent, the event type, group, source, the time
                 received, failures or a receiver's response code. 20 per page, at most 200.
                 Never the payload text: searching it takes event.payload.read, which a key or a
                 login does not have.
  queuey events get <evt_…> --queue <queue> [--content] [--deployment queuey.deploy.json]
                 [--profile <name>] [--json]
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
  queuey create-tenant --name <display> [--environment dev|test|staging|prod] [--profile <name>]
                       [--as-producer] [--with-default-queue] [--json]
                 With --profile it uses the profile's API host and its login, as queuey login
                 --profile wrote them. Without a key or a login for the host it would reach,
                 it sends nothing and names the hosts you are logged in to.
                 --environment marks the new workspace. An API key sets it only here, when
                 it creates the workspace: lowering it later takes a person. Without it, the
                 workspace has none, which Queuey counts as prod. A Queuey that does not take
                 an environment when it creates a workspace fails it (environment_not_set),
                 with the id of the workspace it made.

CREATE-QUEUE
  queuey create-queue --tenant <ten_...> --name <display> [--json]

METRICS
  queuey metrics <que_...> [--json]

ISSUES
  queuey issues <ten_...> [--status open|resolved] [--severity critical|warning|info]
                [--queue <que_...>] [--limit N] [--cursor <c>] [--json]

LISTEN
  queuey listen --forward-to <origin> [--queue <name|que_...> | --tenant <ten_...>]
                [--take-over] [--forward-exact] [--profile <name>] [--json]
                 Receives the deliveries of a queue set to forward to a local listener (Local
                 forward) over an outbound push session (no inbound port exposed) and sends each
                 to --forward-to. Give the origin only, e.g. http://localhost:5000: a delivery
                 keeps the path and query of the queue's endpoint (/api/stripe), its method,
                 headers and body; only the host is swapped. --forward-exact posts to
                 --forward-to as given instead, for a FIXED local endpoint such as a local
                 ingress route. --queue takes the queue's name (its workspace from --tenant or
                 QUEUEY_TENANT) or its id; without --queue the session listens on the workspace.
                 Your local response is the delivery's outcome; the app gets 18 s, then it is a
                 504. The same event can come more than once, as after a listener went away:
                 deduplicate on the event id (X-Queuey-Event-Id) or the Idempotency-Key, which
                 signature v2 (X-Queuey-Signatures: v2=…) covers, so verify v2 first.
                 One session listens on a queue at a time: the first one. Another is refused
                 (listener_already_connected) until it stops, or takes the queue over with
                 --take-over, and the session it took over from stops. A queue under a workspace
                 another session listens on is that session's: listening on the queue takes
                 --take-over too, and the workspace session is told it lost the queue.
                 --json prints one JSON object per line on stdout, each with ""schemaVersion"": 1
                 and a ""type"": listening, then delivery per forward (eventId, path, localUrl,
                 status, durationMs, signatureHeaders; path and localUrl without the query or a
                 part that may be a secret), lost when a workspace session loses a queue,
                 reconnecting and reconnected (scopeKey) when the connection drops and comes
                 back, and one last line: refused (also for an error before the session), superseded, or
                 closed. Output nobody reads any more (a closed pipe) stops the session too.
                 Exit 0 after Ctrl-C, 1 when refused, taken over, the connection is lost for good
                 or the output is gone, 2 on a usage error, 3 on a key error, 143 after SIGTERM.

REPLAY
  queuey replay <event-id> --queue <que_...> [--json]
                 Replays one existing event to your connected `queuey listen` session for local
                 debugging (Stripe-replay style). Read-only — the event isn't modified and the real
                 endpoint is never contacted. Works on any event of a queue that forwards to the
                 listener (Local forward) and shares its payloads in full, a DLQ'd one included; on
                 any other queue the server refuses and says what to change. Run `queuey listen`
                 first so there's a listener to receive it.
  queuey replay <evt_…> --queue <queue> --redeliver [--profile <name>] [--json]
  queuey replay --queue <queue> --status dlq[,failed] [--max <n>] [--dry-run] [--profile <name>] [--json]
                 Sends events to the queue's receiver again: one with --redeliver, or those with a
                 status, at most --max (default 100, up to 500). --dry-run says how many match and
                 sends nothing. In a prod workspace a key or a login sends up to 100 at once; more
                 waits for a person: exit 5 with the link where they approve it.

DIAGNOSE
  queuey diagnose <queue> [--take <n>] [--profile <name>] [--json]
                 Why the queue's deliveries fail: Queuey's incident report (locked, stalled,
                 degraded or healthy, and what to do), and the response codes, endpoints and
                 errors the last --take failed events (default 10, up to 25) have in common.
                 Reads only.

RESUME
  queuey resume <queue> [--target <id>] [--replay none|failed|dlq] [--dry-run] [--profile <name>] [--json]
                 Verifies the receiver and resumes delivery (verify-and-resume): Queuey probes
                 it, sends the event at the head again, and lifts the lock once it is delivered.
                 --replay also sends the failed or DLQ events again after. --target picks the
                 receiver when the queue has several (queuey queue health lists them). --dry-run
                 says what would happen. In a prod workspace a key or a login resumes only with
                 a person's approval: exit 5 with the link.

UNLOCK
  queuey unlock <queue> [--profile <name>] [--json]
                 Lifts the lock Queuey put on the queue's delivery. In a prod workspace a key or
                 a login unlocks only with a person's approval: exit 5 with the link.

EDGE
  queuey edge run     --spool <path> --tenant <ten_...>
                [--listen <port>] [--report-health] [--node-name <name>] [--ingress-base <uri>] [--source <s>]
                [--mqtt <host[:port]> --mqtt-routes ""filter=queue[@segment];…"" [--mqtt-user <u>] [--mqtt-tls]]
                 Signs every transfer with the queue's signing key, read from the environment
                 as QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET (queuey keys mint writes
                 them into the service's environment file). Without the pair it publishes with
                 QUEUEY_API_KEY, a publish-only key; with the pair, QUEUEY_API_KEY is not read.
                 Keys come from the environment, not flags: argv is visible to every user on
                 the machine.
                 Hosts the Edge transfer loop as a standalone daemon (systemd-friendly) — the
                 complete Edge for machines with no .NET app of their own: run this, and anything
                 on the box publishes durably with 'queuey edge publish'. --listen additionally
                 serves a LOOPBACK publish endpoint with the same wire shape as cloud ingress
                 (POST http://localhost:<port>/events/{tenant}/{queue}) — any language's plain
                 HTTP one-liner becomes durable by swapping the base URL; 202 = committed to the
                 local spool. Ctrl-C/SIGTERM to stop; accepted events survive restarts.
                 --report-health (or QUEUEY_REPORT_HEALTH=1) makes the node check in to the
                 console under Edge nodes (outbound only; reports are not events, never billed).
                 Queuey's check-in takes an API key today: a node that signs checks in with
                 QUEUEY_EDGE_HEALTH_API_KEY, a publish-only key in the same environment file,
                 which nothing else uses;
                 --node-name (QUEUEY_NODE_NAME) is the label shown there, default: machine name.
                 --mqtt subscribes to a (usually local) broker and spools every message durably
                 BEFORE acking it (QoS 1); a route's @segment makes that topic level the lane
                 (FIFO per machine). Env: QUEUEY_MQTT, QUEUEY_MQTT_ROUTES, QUEUEY_MQTT_USER; the
                 broker's password only as QUEUEY_MQTT_PASSWORD, never in argv.
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
  queuey whoami [--profile <name>] [--json]
                 The connection the CLI resolves, with the key masked, or the login it uses
                 (""login"" in --json, never a token); with a profile, that profile's and the file
                 it came from. It does not connect. Hosts (""hosts"" in --json) is Production for Queuey's
                 own hosts, Local when both hosts are on this machine, and Custom otherwise.

PROFILES
  --profile <name> (or QUEUEY_PROFILE) picks one environment, such as dev or prod, and
  takes both halves of it. Each half has to be there, or the command fails and says
  which is missing:
                 1. The connection, which lives with you and never in the repository:
                    ~/.queuey/config.json (or the file QUEUEY_USER_CONFIG names), as
                    { ""profiles"": { ""dev"": { ""license"": ""lic_…"", ""tenant"": ""ten_…"",
                    ""apiBase"": ""https://…"", ""ingressBase"": ""https://…"" } } }. `queuey login
                    --profile dev` writes it. A profile without ""apiKey"" uses the login; one with
                    ""apiKey"": ""qak_…"" uses the key, as before.
                    It holds keys, so it is read only when nobody else can reach it, as
                    ssh checks ~/.ssh: it belongs to you, chmod 600; a link is followed to
                    the file it points to; and each folder above it, up to your home folder,
                    belongs to you or root and is not writable by others (unless sticky,
                    like /tmp). ACLs are not read, so put none on them. On Windows your
                    profile's ACL protects it. In CI, write it from the pipeline's secrets,
                    with an apiKey.
                 2. The deployment file's values for that environment, committed with it:
                    ""profiles"": { ""dev"": { ""variables"": { ""QUEUEY_BASE_URL"": ""https://…"",
                    ""QUEUEY_STRIPE_DELIVERY_KIND"": ""localForward"" } } }. They fill the file's
                    ${VAR} references, so promoting a change is a pull request. A value is
                    taken as written, and one that looks like a secret is refused.
                 apply, plan, verify, publish, listen, events get and credentials take both
                 (listen and credentials read ./queuey.deploy.json for it); whoami shows the
                 connection; apply --dry-run needs only the file's half, as it never connects,
                 and says on stderr when the connection is missing or cannot be read.
                 With a profile, a flag still wins for its value, queuey.json is not read, and
                 a QUEUEY_ variable for the key, license, workspace or a host that disagrees
                 with the profile is an error, as is a ${VAR} the profile and the environment
                 set to different values: a value left in the shell from another environment
                 never mixes in. The workspace is the connection's; a file that names one
                 too must name the same. A command that takes no profile refuses to run
                 while QUEUEY_PROFILE is set.

GLOBAL OPTIONS (all commands)
  An option a command does not take fails it (exit 2) and lists the ones it does.
  --api-base <uri>                Control-plane (API) host (default: https://api.queuey.ai).
                                  Set it to point at a locally-running instance, e.g. for testing.
  --ingress-base <uri>            Ingress (publish) host (default: https://ingress.queuey.ai).
  --api-key <qak_...>             License-wide API key. Without one, the login (queuey login) is used
  --tenant <ten_...>              Producer tenant public id
  --license <lic id>              License public id (required for sync)
  --source <s>                    X-Queuey-Source trace value
  --config <path>                 Path to a queuey.json (default: ./queuey.json)

CONFIG PRECEDENCE
  flag  >  environment (QUEUEY_API_BASE / QUEUEY_INGRESS_BASE /
           QUEUEY_API_KEY / QUEUEY_TENANT / QUEUEY_LICENSE / QUEUEY_SOURCE)  >
           queuey.json  >  default
  With --profile:  flag  >  the profile in ~/.queuey/config.json  >  default
           (see PROFILES: queuey.json is not read, and a disagreeing QUEUEY_ variable fails)
  Without an API key from any of them, the login for the API host (queuey login) is used, with
  its license unless one is named.

EXIT CODES
  0 success   1 runtime failure   2 usage   3 config   4 assembly load
  5 waits for a person: a configuration plan's approval in Queuey's inbox (plan --submit, apply),
    or a login code to approve (login)
";
}
