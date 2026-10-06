using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Advise;

/// <summary>
/// One setting in the proposed deployment file and why it is there. <see cref="Basis"/> says what it rests on:
/// <c>stated</c> (the intent), <c>evidence</c> (the repository), <c>default</c> (Queuey's or the provider's usual value,
/// which the person can change) or <c>recommendation</c> (advise's judgement). <see cref="From"/> names the flow fields
/// it follows from.
/// </summary>
public sealed record DesignSetting(string Path, JsonNode? Value, string Basis, string Because, IReadOnlyList<string> From);

/// <summary>A credential the deployment file names, what it holds, and the command that stores it. Never the value.</summary>
public sealed record CredentialNeed(string Name, string Type, string Holds, string Store);

/// <summary>
/// One step in the application's code or configuration: <c>keep</c> what works as it is, <c>change</c> or <c>add</c>
/// code, or <c>configure</c> the app. Kept apart from the deployment file, which is Queuey's half.
/// </summary>
public sealed record CodeStep(string? File, int? Line, string Action, string What, string Why, string Basis);

/// <summary>What advise proposes for a flow without conflicts: the deployment file and the code plan, kept apart.</summary>
public sealed class FlowDesign
{
    public string File { get; init; } = DeploymentFile.DefaultFileName;

    /// <summary>Whether the repository has the file already.</summary>
    public bool Exists { get; init; }

    /// <summary>How the proposal goes into the file that is there, or null when there is none.</summary>
    public string? Merge { get; init; }

    /// <summary>The deployment file for this flow, as JSON. It carries no secrets: credentials are named, never given.</summary>
    public JsonObject Content { get; init; } = new();

    /// <summary>The <c>${VAR}</c>s the file needs when it is applied.</summary>
    public IReadOnlyList<string> Variables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<CredentialNeed> Credentials { get; init; } = Array.Empty<CredentialNeed>();

    public IReadOnlyList<DesignSetting> Settings { get; init; } = Array.Empty<DesignSetting>();

    public IReadOnlyList<CodeStep> Code { get; init; } = Array.Empty<CodeStep>();

    public IReadOnlyList<string> NextSteps { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Turns a flow without conflicts into a Queuey design: a deployment file where every setting has a reason, the code steps
/// that go with it, and what to run, in order. The design follows the provider: a Stripe queue verifies Stripe's signature
/// at the ingress and signs each delivery again in Stripe's format, so the handler keeps constructEvent; a Supabase queue
/// takes the webhook's key. In dev the queue delivers to a queuey listen session; elsewhere over HTTP.
/// </summary>
public static class FlowDesigner
{
    public const string StripeCredential = "stripe-whsec";

    public static FlowDesign Design(DesiredFlow flow, FlowFacts facts, Advice? sending = null)
    {
        if (flow.Conflicts.Count > 0)
            throw new InvalidOperationException("A flow with conflicts gets no design.");
        return new Builder(flow, facts, sending).Build();
    }

    private sealed class Builder
    {
        private readonly DesiredFlow _flow;
        private readonly FlowFacts _facts;
        private readonly Advice? _sending;
        private readonly List<DesignSetting> _settings = new();
        private readonly List<CredentialNeed> _credentials = new();
        private readonly List<CodeStep> _code = new();
        private readonly List<string> _next = new();
        private readonly SortedSet<string> _variables = new(StringComparer.Ordinal);

        private readonly string _kind;
        private readonly string _env;
        private readonly string _queue;
        private readonly string _route;
        private readonly bool _local;
        private readonly string _prefix;

        // Med profiler (F2.7, #53) står verdiene som skiller miljøene, i profilen med miljøets navn, og fila tar dem som
        // ${VAR}: `queuey plan --profile dev` virker da, og prod er en PR som legger til en profil. En fil som finnes uten
        // profiler, holder seg til faste verdier, som resten av den.
        private readonly bool _portable;
        private readonly SortedDictionary<string, string> _profile = new(StringComparer.Ordinal);
        private readonly string _profileFlag;

        public Builder(DesiredFlow flow, FlowFacts facts, Advice? sending)
        {
            _flow = flow;
            _facts = facts;
            _sending = sending;
            _kind = flow.String("source.kind") ?? throw new InvalidOperationException("A design needs the source.");
            _env = flow.String("environment") ?? "dev";
            _queue = flow.String("queue") ?? throw new InvalidOperationException("A design needs the queue.");
            _route = flow.String("destination.route") ?? throw new InvalidOperationException("A design needs the route.");
            _local = _env == "dev";
            _prefix = $"queues.{_queue}";
            _portable = facts.DeployFile is null || facts.DeployFile.Profiles.Count > 0;
            _profileFlag = _portable ? $" --profile {_env}" : "";
        }

        public FlowDesign Build()
        {
            var workspace = new JsonObject();
            var queue = new JsonObject();
            var ingress = new JsonObject();
            var delivery = new JsonObject();

            WorkspaceEnvironment(workspace);

            switch (_kind)
            {
                case "stripe": StripeIngress(ingress); break;
                case "supabase": SupabaseIngress(ingress); break;
                default: AppIngress(ingress); break;
            }
            queue["ingress"] = ingress;
            Ordering(queue, ingress);
            Idempotency(queue);
            Put(queue, "dlqEnabled", true, $"{_prefix}.dlqEnabled", "recommendation",
                "An event the receiver keeps refusing waits in the dead-letter queue for a replay, instead of locking the queue " +
                "until a person acts.");

            Destination(delivery);
            if (_kind == "stripe") StripeSigning(delivery);
            if (_kind != "stripe") SharedSecret(delivery);
            queue["delivery"] = delivery;

            var content = new JsonObject
            {
                ["$schema"] = DeploymentFile.SchemaUrl,
                ["workspace"] = workspace,
                ["queues"] = new JsonObject { [_queue] = queue },
            };
            if (_profile.Count > 0)
                content["profiles"] = Profile();
            Check(content);

            CodePlan();
            NextSteps();

            ExistingDeployFile? existing = _facts.DeployFile;
            return new FlowDesign
            {
                Exists = existing is not null,
                Merge = existing is null ? null : MergeText(existing),
                Content = content,
                Variables = _variables.ToArray(),
                Credentials = _credentials.ToArray(),
                Settings = _settings.ToArray(),
                Code = _code.ToArray(),
                NextSteps = _next.ToArray(),
            };
        }

        // ── ingressen ────────────────────────────────────────────────────

        private void StripeIngress(JsonObject ingress)
        {
            Put(ingress, "authMode", "SignedRequest", $"{_prefix}.ingress.authMode", Basis("source.authentication"),
                "Stripe signs every webhook, so the ingress takes only what Stripe signed and refuses the rest.",
                "source.kind", "source.authentication");
            Put(ingress, "signedRequest", new JsonObject { ["template"] = "stripe", ["credentialRef"] = StripeCredential },
                $"{_prefix}.ingress.signedRequest", Basis("source.kind"),
                $"Verify Stripe's scheme, the Stripe-Signature header, with the secret stored as {StripeCredential}. The file names " +
                "the credential and never holds the secret; until it is stored, the ingress refuses every event.",
                "source.kind");
            Put(ingress, "eventType", new JsonObject { ["from"] = "body", ["name"] = "type" }, $"{_prefix}.ingress.eventType", "default",
                "Stripe puts the event type in the body's type field. Queuey labels each event with it, which is what verify " +
                "--event-type and the console filter on.",
                "source.kind");

            _credentials.Add(new CredentialNeed(StripeCredential, "HmacSigning",
                "Stripe's webhook signing secret (whsec_…). Testing locally, it is the one stripe listen prints; for an endpoint " +
                "in Stripe's dashboard, that endpoint's own.",
                $"queuey credentials set{_profileFlag} --name {StripeCredential} --type HmacSigning --key-id {StripeCredential} --from-env STRIPE_WHSEC"));
        }

        private void SupabaseIngress(JsonObject ingress)
        {
            string auth = _flow.String("source.authentication") ?? "api-key";
            if (auth == "none")
            {
                Put(ingress, "authMode", "None", $"{_prefix}.ingress.authMode", Basis("source.authentication"),
                    "The intent has the webhook send no key, so the ingress takes every request to its URL. Anyone who learns " +
                    "the URL can add events.",
                    "source.authentication");
            }
            else
            {
                Put(ingress, "authMode", "ApiKey", $"{_prefix}.ingress.authMode", Basis("source.authentication"),
                    "A Supabase Database Webhook cannot sign what it sends, but it can send a header: a key that can publish " +
                    "to this queue only, in X-Api-Key. The ingress refuses a request without it.",
                    "source.kind", "source.authentication");
            }

            Put(ingress, "eventType", new JsonObject { ["from"] = "body", ["name"] = "type" }, $"{_prefix}.ingress.eventType", "default",
                "Supabase puts the operation (INSERT, UPDATE or DELETE) in the body's type field. Queuey labels each event with " +
                "it, which is what verify --event-type and the console filter on.",
                "source.kind");
        }

        private void AppIngress(JsonObject ingress)
        {
            string auth = _flow.String("source.authentication") ?? "api-key";
            (string mode, string because) = auth switch
            {
                "queuey-signature" => ("SignedRequest",
                    "The app signs what it publishes with Queuey's scheme, and the ingress verifies it with the sending key's " +
                    "signing key."),
                "none" => ("None", "The intent has the app publish without a key, so the ingress takes every request to its URL."),
                _ => ("ApiKey", "The app publishes with an API key, as Queuey.Client and the plain HTTP call do. The ingress refuses " +
                                "a request without one."),
            };
            Put(ingress, "authMode", mode, $"{_prefix}.ingress.authMode", Basis("source.authentication"), because, "source.authentication");
            if (mode == "SignedRequest")
                Put(ingress, "signedRequest", new JsonObject { ["template"] = "queuey" }, $"{_prefix}.ingress.signedRequest",
                    Basis("source.authentication"), "Queuey's own scheme takes no credential: it verifies with the sending API " +
                    "client's signing key.", "source.authentication");
        }

        // ── atferden ─────────────────────────────────────────────────────

        private void Ordering(JsonObject queue, JsonObject ingress)
        {
            string ordering = _flow.String("requirements.ordering") ?? "none";
            switch (ordering)
            {
                case "per-key":
                    string key = _flow.String("requirements.orderingKey")!;
                    bool header = key.StartsWith("header:", StringComparison.OrdinalIgnoreCase);
                    string name = header ? key["header:".Length..].Trim() : key;
                    Put(ingress, "groupKey", new JsonObject { ["from"] = header ? "header" : "body", ["name"] = name },
                        $"{_prefix}.ingress.groupKey", Basis("requirements.orderingKey"),
                        $"The key the receiver needs events in order by: {(header ? $"the {name} header" : $"the body's {name} field")}.",
                        "requirements.orderingKey");
                    Put(queue, "ordering", "bykey", $"{_prefix}.ordering", Basis("requirements.ordering"),
                        "In order per key, one at a time for each, while different keys move side by side. An event the receiver " +
                        "refuses holds only its own key.",
                        "requirements.ordering");
                    break;

                case "fifo":
                    Put(queue, "ordering", "fifo", $"{_prefix}.ordering", Basis("requirements.ordering"),
                        "The whole queue in order, one event at a time: the receiver needs it so. One slow or refused event holds " +
                        "every event after it.",
                        "requirements.ordering");
                    break;

                default:
                    Put(queue, "ordering", "besteffort", $"{_prefix}.ordering", Basis("requirements.ordering"),
                        _kind == "stripe"
                            ? "Stripe sends its events in no set order, so the handler cannot rely on one. Deliveries go side by " +
                              "side, and a slow one does not hold the rest."
                            : "The receiver does not need the events in order, so deliveries go side by side, and a slow one does " +
                              "not hold the rest.",
                        "requirements.ordering");
                    break;
            }
        }

        private void Idempotency(JsonObject queue)
        {
            bool idempotent = _flow.Bool("requirements.idempotent") ?? false;
            Put(queue, "idempotent", idempotent, $"{_prefix}.idempotent",
                _flow["requirements.idempotent"] is null ? "default" : Basis("requirements.idempotent"),
                idempotent
                    ? "The receiver handles the same event twice safely, so Queuey sends an event again after a timeout instead " +
                      "of holding the queue for a person."
                    : "Nothing shows that the receiver handles the same event twice safely, so a timeout holds the queue for a " +
                      "person rather than risk a second effect. Set it to true once the receiver deduplicates.",
                "requirements.idempotent");
        }

        // ── leveransen ───────────────────────────────────────────────────

        private void Destination(JsonObject delivery)
        {
            FlowValue? baseUrl = _flow["destination.baseUrl"];
            string url;
            string basis;
            string because;

            if (baseUrl is { Provenance: Provenance.Stated } && _portable)
            {
                url = "${" + DeploymentTemplate.BaseUrlVariable + "}" + _route;
                ProfileValue(DeploymentTemplate.BaseUrlVariable, baseUrl.AsString!.TrimEnd('/'), "stated",
                    $"Where the receiver is reachable over HTTP in {_env}, as the intent gives it.", "destination.baseUrl");
                basis = "stated";
                because = $"The handler's route, {_route}, on {DeploymentTemplate.BaseUrlVariable}, which the {_env} profile sets to the " +
                          "base the intent gives. On the queue rather than as the workspace's base, so the workspace and its other " +
                          "queues are left alone.";
            }
            else if (baseUrl is { Provenance: Provenance.Stated })
            {
                url = baseUrl.AsString!.TrimEnd('/') + _route;
                basis = "stated";
                because = $"The handler's route, {_route}, on the base the intent gives.";
            }
            else if (_facts.DeployFile?.BaseUrl is not null)
            {
                url = _route;
                basis = "evidence";
                because = $"The handler's route. It appends to the workspace's base URL, which {_facts.DeployFile.File} declares.";
            }
            else
            {
                url = "${" + DeploymentTemplate.BaseUrlVariable + "}" + _route;
                _variables.Add(DeploymentTemplate.BaseUrlVariable);
                basis = "recommendation";
                because = $"The handler's route, {_route}, on {DeploymentTemplate.BaseUrlVariable}: where the receiver is reachable " +
                          "over HTTP. On the queue rather than as the workspace's base, so the workspace and its other queues are " +
                          "left alone.";
            }

            if (_local)
                because += " A local listener forwards each delivery to this path on your machine, and Queuey signs a delivery for " +
                           "a listener only when its URL is absolute.";

            Put(delivery, "url", url, $"{_prefix}.delivery.url", basis, because, "destination.route", "destination.baseUrl");

            string kind = _local ? "localForward" : "http";
            string kindBecause = _local
                ? "A dev workspace: each delivery goes to the queuey listen session on your machine, which forwards it to the " +
                  "receiver. While no session is connected, the events wait."
                : "Queuey delivers to the URL over HTTP.";
            if (_portable)
            {
                string variable = DeploymentTemplate.QueueKindVariable(_queue);
                Put(delivery, "kind", "${" + variable + "}", $"{_prefix}.delivery.kind", "recommendation",
                    $"Where the queue delivers depends on the environment, so each profile sets it: localForward in dev, http elsewhere.",
                    "environment");
                ProfileValue(variable, kind, Basis("environment"), kindBecause, "environment");
            }
            else
            {
                Put(delivery, "kind", kind, $"{_prefix}.delivery.kind", Basis("environment"), kindBecause, "environment");
            }
        }

        private void WorkspaceEnvironment(JsonObject workspace)
        {
            string? existing = _facts.DeployFile?.Environment;
            string? variable = existing is not null && existing.IndexOf("${", StringComparison.Ordinal) >= 0
                ? DeploymentVariables.Referenced(existing).FirstOrDefault()
                : null;

            if (variable is not null)
            {
                Put(workspace, "environment", "${" + variable + "}", "workspace.environment", "evidence",
                    $"The deployment file takes the workspace's environment from {variable}.", "environment");
                if (_portable)
                    ProfileValue(variable, _env, Basis("environment"), EnvironmentBecause(), "environment");
                return;
            }

            if (existing is null && _portable)
            {
                variable = DeploymentTemplate.EnvironmentVariable;
                Put(workspace, "environment", "${" + variable + "}", "workspace.environment", "recommendation",
                    "The workspace's environment comes from the profile, so the same file goes to another environment with a " +
                    "profile of its own.", "environment");
                ProfileValue(variable, _env, Basis("environment"), EnvironmentBecause(), "environment");
                return;
            }

            Put(workspace, "environment", _env, "workspace.environment", Basis("environment"), EnvironmentBecause(), "environment");
        }

        /// <summary>A value for the flow's environment, under profiles.&lt;environment&gt;.variables.</summary>
        private void ProfileValue(string variable, string value, string basis, string because, params string[] from)
        {
            _profile[variable] = value;
            _settings.Add(new DesignSetting($"profiles.{_env}.variables.{variable}", JsonValue.Create(value), basis, because, from));
        }

        private JsonObject Profile()
        {
            var variables = new JsonObject();
            foreach ((string variable, string value) in _profile)
                variables[variable] = value;
            return new JsonObject { [_env] = new JsonObject { ["variables"] = variables } };
        }

        private void StripeSigning(JsonObject delivery)
        {
            if (_flow["requirements.verification"] is not { AsString: "stripe-signature" } verification)
                return;

            string where = verification.Evidence.FirstOrDefault() is { } e ? $" ({e})" : "";
            Put(delivery, "signing", new JsonObject { ["enabled"] = true, ["templateKey"] = "stripe" }, $"{_prefix}.delivery.signing",
                Basis("requirements.verification"),
                $"The handler verifies Stripe's signature{where}. Queuey checks each event against the signature Stripe sent, and " +
                "signs the delivery again in Stripe's format with the secret its ingress verifies with, so the handler works " +
                "unchanged, also for a retry or a replay days later.",
                "requirements.verification");
        }

        private void SharedSecret(JsonObject delivery)
        {
            if (SecretCheck() is not { } check)
                return;

            string credential = $"{_queue}-webhook-secret";
            string type = check.IsBearer ? "BearerToken" : "ApiKeyHeader";
            if (check.IsBearer)
            {
                Put(delivery, "authMode", "Bearer", $"{_prefix}.delivery.authMode", "evidence",
                    $"The handler checks the Authorization header ({check.File}:{check.Line}). Queuey sends a bearer token with " +
                    "every delivery over HTTP.", "requirements.verification");
            }
            else
            {
                Put(delivery, "authMode", "ApiKey", $"{_prefix}.delivery.authMode", "evidence",
                    $"The handler checks the {check.Header} header ({check.File}:{check.Line}). Queuey sends it with every " +
                    "delivery over HTTP.", "requirements.verification");
                Put(delivery, "authHeaderName", check.Header, $"{_prefix}.delivery.authHeaderName", "evidence",
                    "The header the handler reads the secret from.", "requirements.verification");
            }
            Put(delivery, "credentialRef", credential, $"{_prefix}.delivery.credentialRef", "default",
                "The name of the credential holding the secret the handler compares with. The file names it and never holds it.",
                "requirements.verification");

            string variable = check.SecretName is { } name && !name.Contains(':') && !name.Contains('.') ? name : "WEBHOOK_SECRET";
            _credentials.Add(new CredentialNeed(credential, type,
                $"The secret the handler compares the {check.Header} header with" +
                (check.SecretName is { } secretName ? $" ({secretName})." + EnvNote(secretName) : "."),
                $"queuey credentials set{_profileFlag} --name {credential} --type {type} --from-env {variable}"));
        }

        /// <summary>The check behind a shared-secret verification: the header, and where the secret comes from.</summary>
        private SecretCheckFinding? SecretCheck()
        {
            if (_flow["requirements.verification"] is not { AsString: "shared-secret" } verification)
                return null;
            return verification.Evidence
                .Select(e => _facts.SecretChecks.FirstOrDefault(c => c.File == e.File && c.Line == e.Line))
                .FirstOrDefault(c => c is not null);
        }

        // ── koden ────────────────────────────────────────────────────────

        private void CodePlan()
        {
            switch (_kind)
            {
                case "stripe": StripeCode(); break;
                case "supabase": SupabaseCode(); break;
                default: AppCode(); break;
            }

            if (_flow.String("destination.framework") == "supabase-edge")
                SupabaseJwt();
        }

        private void StripeCode()
        {
            if (_flow["requirements.verification"] is { AsString: "stripe-signature" } verification)
            {
                StripeVerificationFinding? call = verification.Evidence
                    .Select(e => _facts.StripeVerifications.FirstOrDefault(v => v.File == e.File && v.Line == e.Line))
                    .FirstOrDefault(v => v is not null);

                if (call is not null)
                {
                    _code.Add(new CodeStep(call.File, call.Line, "keep", $"Keep {call.Api}: it verifies the signature Queuey sends.",
                        $"Queuey signs each delivery again in Stripe's format, with the secret stored as {StripeCredential}.", "evidence"));

                    _code.Add(call.SecretName is { } secret
                        ? new CodeStep(call.File, call.SecretLine, "configure",
                            $"Give {secret} the same secret Queuey stores as {StripeCredential}.",
                            "Queuey signs deliveries with the secret its ingress verifies Stripe's events with. Testing locally, " +
                            "that is the one stripe listen prints." + EnvNote(secret), "evidence")
                        : new CodeStep(call.File, call.Line, "configure",
                            $"Give the handler's signing secret the same value Queuey stores as {StripeCredential}.",
                            "Queuey signs deliveries with the secret its ingress verifies Stripe's events with.", "evidence"));
                }

                RawBodyCode();
            }

            if (_flow.Bool("requirements.idempotent") != true)
            {
                FlowEvidence? at = _flow["requirements.verification"]?.Evidence.FirstOrDefault();
                _code.Add(new CodeStep(at?.File ?? HandlerFile(), at?.Line, "add",
                    "Skip an event the handler has handled before: keep Stripe's event id (event.id) when it handles one, and " +
                    "check for it first.",
                    "Queuey delivers at least once, and so does Stripe: a delivery that timed out comes again with the same event.",
                    "recommendation"));
            }
        }

        /// <summary>An Express app that parses JSON before the webhook route breaks constructEvent; say where.</summary>
        private void RawBodyCode()
        {
            if (_flow.String("destination.framework") is not ("express" or "node"))
                return;
            if (_flow["destination.expectsRawBody"]?.Evidence.Any(e => e.What == "reads the raw request body") == true)
                return;
            if (_facts.RawBodyReads.Count > 0)
                return;

            Finding? parser = _facts.JsonBodyParsers.FirstOrDefault();
            if (parser is null)
                return;

            _code.Add(new CodeStep(parser.File, parser.Line, "change",
                "Give the webhook route the raw body: mount express.raw({ type: 'application/json' }) on it, ahead of " +
                "express.json().",
                "constructEvent checks the exact bytes Stripe signed, and a body express.json() has parsed fails it, whoever " +
                "delivers.", "evidence"));
        }

        private void SupabaseCode()
        {
            Finding? payload = _facts.SupabasePayloadReads.FirstOrDefault(r => r.File == HandlerFile());
            if (payload is not null)
                _code.Add(new CodeStep(payload.File, payload.Line, "keep", "Keep reading Supabase's payload as it is.",
                    "Queuey delivers the body Supabase sent, unchanged: type, table, record and old_record.", "evidence"));

            if (SecretCheck() is { } check)
            {
                _code.Add(new CodeStep(check.File, check.Line, "keep", $"Keep the {check.Header} check.",
                    $"Queuey sends that header with the secret stored as {_queue}-webhook-secret on every delivery over HTTP.",
                    "evidence"));
                if (_local)
                    _code.Add(new CodeStep(check.File, check.Line, "configure",
                        $"While you develop, expect the {check.Header} check to refuse local deliveries: queuey listen does not pass " +
                        "the header on. Verify Queuey's signature instead, as https://queuey.ai/docs/how-to/verify-deliveries " +
                        "shows, or test the check against the deployed function.",
                        "Queuey never sends a credential to a developer's machine. A local delivery carries Queuey's signature " +
                        "instead.", "evidence"));
            }
            else if (_flow.String("requirements.verification") == "none")
            {
                _code.Add(new CodeStep(HandlerFile(), payload?.Line, "add",
                    "Check what reaches the handler: compare a header with a secret, or verify Queuey's signature as " +
                    "https://queuey.ai/docs/how-to/verify-deliveries shows.",
                    "The handler's URL is public, and without a check anyone who finds it can post a row change.", "recommendation"));
            }

            if (_flow.Bool("requirements.idempotent") != true)
                _code.Add(new CodeStep(HandlerFile(), payload?.Line, "add",
                    "Apply a change so that applying it twice does no harm: an upsert keyed on the row's id, or a check of what " +
                    "was applied.",
                    "Queuey delivers at least once, and Supabase can send one change twice.", "recommendation"));

            foreach (SupabaseTriggerFinding trigger in _facts.SupabaseTriggers.Where(t => !t.TargetsQueuey
                         && (t.TargetPath is not null && FlowScan.SameRoute(t.TargetPath, _route)
                             || (_flow.String("source.table") is { } table && t.Table is not null
                                 && t.Table.Split('.').Last().Equals(table.Split('.').Last(), StringComparison.OrdinalIgnoreCase)))))
            {
                _code.Add(new CodeStep(trigger.File, trigger.Line, "change",
                    "Point this Database Webhook at the queue's ingress URL, which queuey plan shows, with X-Api-Key holding a key " +
                    "that can publish to this queue only. Create the webhook in the Supabase dashboard rather than in a " +
                    "migration, so the key is not committed.",
                    "Supabase sends a change once and does not retry it. Queuey takes it durably and delivers it to the handler " +
                    "with retries.", "evidence"));
            }
        }

        private void AppCode()
        {
            Finding? registration = _facts.Queuey.FirstOrDefault(f => f.What.StartsWith("registers Queuey", StringComparison.Ordinal));
            if (registration is not null)
            {
                _code.Add(new CodeStep(registration.File, registration.Line, "keep",
                    "Publish through the Queuey registration that is here.",
                    "Reuse what is registered: a second client is a second configuration to keep right.", "evidence"));
                return;
            }

            if (_sending is { Send: not SendPath.None } sending)
                _code.Add(new CodeStep(null, null, "add", sending.Headline,
                    sending.Reasons.FirstOrDefault() ?? Recommendation.DoNotRebuild, "recommendation"));
        }

        private void SupabaseJwt()
        {
            string? function = _route.StartsWith("/functions/v1/", StringComparison.Ordinal) ? _route["/functions/v1/".Length..] : null;
            if (function is null)
                return;

            NameFinding? off = _facts.SupabaseFunctionsWithoutJwt.FirstOrDefault(f => f.Name == function);
            _code.Add(off is not null
                ? new CodeStep(off.File, off.Line, "keep", $"Keep verify_jwt off for {function}.",
                    "Queuey's deliveries carry no Supabase JWT.", "evidence")
                : new CodeStep("supabase/config.toml", null, "configure",
                    $"Turn off JWT verification for {function}: verify_jwt = false under [functions.{function}] in " +
                    "supabase/config.toml, or deploy it with --no-verify-jwt.",
                    "Queuey's deliveries carry no Supabase JWT, and the function's gateway refuses a request without one.",
                    "recommendation"));
        }

        private string? HandlerFile() => _flow["destination.route"]?.Evidence.FirstOrDefault()?.File;

        /// <summary>Where a .env file sets <paramref name="name"/>, by name: advise reads the names there, never a value.</summary>
        private string EnvNote(string name)
        {
            string variable = name.Replace(':', '_').Replace('.', '_');
            NameFinding[] set = _facts.EnvNames
                .Where(e => e.Name.Equals(name, StringComparison.Ordinal) || e.Name.Equals(variable, StringComparison.OrdinalIgnoreCase)
                            || e.Name.Equals(name.Replace(":", "__"), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return set.Length == 0
                ? ""
                : $" {set[0].Name} is set in {string.Join(", ", set.Select(e => e.File).Distinct())}; advise reads the name there, " +
                  "never the value.";
        }

        // ── neste steg ───────────────────────────────────────────────────

        private void NextSteps()
        {
            ExistingDeployFile? existing = _facts.DeployFile;
            _next.Add(existing is null
                ? "Write infrastructure.content to queuey.deploy.json, and keep the flow beside it, as queuey.flow.json or in the " +
                  "pull request."
                : "Merge infrastructure.content into queuey.deploy.json as infrastructure.merge says, and keep the flow beside it, " +
                  "as queuey.flow.json or in the pull request.");

            if (_variables.Contains(DeploymentTemplate.BaseUrlVariable))
                _next.Add((_local
                              ? $"Set {DeploymentTemplate.BaseUrlVariable} to where the receiver is reachable over HTTP, such as " +
                                "production's URL. While the queue forwards to a listener, Queuey sends nothing there; it takes the path."
                              : $"Set {DeploymentTemplate.BaseUrlVariable} to where the receiver is reachable over HTTP.")
                          + (_portable ? $" Once you know it, it can go under profiles.{_env}.variables instead." : ""));

            if (_portable)
                _next.Add($"--profile {_env} takes your connection from ~/.queuey/config.json, never from the repository: " +
                          $"profiles.{_env} there, with apiKey, license and tenant, readable only by you (chmod 600). The key is " +
                          "minted in the Queuey console.");

            if (_local)
                _next.Add("Apply it to a workspace set to dev. Only a person lowers a workspace's environment, in the Queuey " +
                          "console, and apply refuses this file against a higher one.");

            _next.Add($"queuey apply --dry-run{_profileFlag} checks the file and sends nothing. queuey plan{_profileFlag} asks Queuey " +
                      "what would change, and shows the queue's ingress URL.");

            string type = _flow.Strings("source.eventTypes").FirstOrDefault()
                          ?? (_kind == "stripe" ? "checkout.session.completed" : _kind == "supabase" ? "INSERT" : "order.created");
            string port = _flow.Int("destination.port") is { } p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<port>";

            switch (_kind)
            {
                case "stripe":
                    _next.Add($"queuey apply{_profileFlag}. Until {StripeCredential} is stored, the ingress refuses every event.");
                    if (_local)
                    {
                        _next.Add("stripe listen --forward-to <ingress URL>. It prints this session's signing secret (whsec_…). Put it " +
                                  $"in STRIPE_WHSEC and store it: {_credentials[0].Store}. Then run queuey apply{_profileFlag} again, " +
                                  "which points the ingress at it.");
                        _next.Add(SecretName() is { } secret
                            ? $"Run the app with {secret} set to that same secret."
                            : "Run the app with its signing secret set to that same secret.");
                        _next.Add($"queuey listen{_profileFlag} --queue {_queue} --forward-to http://localhost:{port} --json, in the " +
                                  "background.");
                        _next.Add($"queuey verify {_queue}{_profileFlag} --event-type {type} --ingress-auth stripe --json, and while it " +
                                  $"waits: stripe trigger {type}.");
                    }
                    else
                    {
                        _next.Add("Point the Stripe webhook endpoint at the queue's ingress URL, in the dashboard or with Stripe's API. " +
                                  "Changing the URL of an endpoint that exists keeps its secret, which the handler has. Put that secret " +
                                  $"in STRIPE_WHSEC and store it: {_credentials[0].Store}. Then run queuey apply{_profileFlag} again.");
                        _next.Add($"queuey verify {_queue}{_profileFlag} --event-type {type} --ingress-auth stripe --json, and send that " +
                                  "event from Stripe while it waits.");
                    }
                    break;

                case "supabase":
                    _next.Add($"queuey apply{_profileFlag}.");
                    _next.Add("Mint a key that can publish to this queue only, in the Queuey console. Point the Database Webhook at " +
                              "the queue's ingress URL with the key in X-Api-Key: Supabase dashboard → Integrations → Database " +
                              "Webhooks.");
                    foreach (CredentialNeed credential in _credentials)
                        _next.Add($"Store the handler's secret for Queuey: {credential.Store}.");
                    if (_local)
                        _next.Add((_flow.String("destination.framework") == "supabase-edge" ? "supabase functions serve, then " : "") +
                                  $"queuey listen{_profileFlag} --queue {_queue} --forward-to http://localhost:{port} --json, in the background.");
                    _next.Add($"queuey verify {_queue}{_profileFlag} --event-type {type} --json, and change a row" +
                              (_flow.String("source.table") is { } table ? $" in {table}" : "") + " while it waits.");
                    break;

                default:
                    _next.Add($"queuey apply{_profileFlag}.");
                    if (_sending is not null)
                        _next.AddRange(_sending.NextSteps.Where(s => !s.StartsWith("Create the workspace", StringComparison.Ordinal)));
                    if (_local)
                        _next.Add($"queuey listen{_profileFlag} --queue {_queue} --forward-to http://localhost:{port} --json, where the " +
                                  "receiver runs.");
                    _next.Add($"queuey publish {_queue}{_profileFlag} --data '{{\"test\":true}}' prints the event's id; queuey verify " +
                              $"{_queue}{_profileFlag} --event <evt_…> --json follows it to the receiver.");
                    break;
            }
        }

        private string? SecretName()
            => _flow["requirements.verification"]?.Evidence
                .Select(e => _facts.StripeVerifications.FirstOrDefault(v => v.File == e.File && v.Line == e.Line)?.SecretName)
                .FirstOrDefault(n => n is not null);

        private string MergeText(ExistingDeployFile existing)
        {
            if (existing.Problem is not null)
                return $"{existing.File} is there, but apply cannot read it as it is: {existing.Problem} Fix that first, then add " +
                       $"queues.{_queue} from the proposal.";

            var parts = new List<string>
            {
                existing.Queues.ContainsKey(_queue)
                    ? $"{existing.File} declares queues.{_queue} already; the proposal's declaration replaces it."
                    : $"Add queues.{_queue} from the proposal to {existing.File}, and keep what is there.",
            };

            if (_profile.Count > 0)
                parts.Add(existing.Profiles.Contains(_env)
                    ? $"Add the variables under profiles.{_env} to the profile it has."
                    : $"Add profiles.{_env} from the proposal: the file has no profile for {_env} yet.");

            string? environment = existing.Environment?.Trim().ToLowerInvariant();
            if (environment is null)
                parts.Add("Add workspace.environment from the proposal.");
            else if (environment.IndexOf("${", StringComparison.Ordinal) >= 0 && !_portable)
                parts.Add($"Its workspace.environment is a variable; set it to {_env} where this file is applied.");
            return string.Join(" ", parts);
        }

        // ── hjelpere ─────────────────────────────────────────────────────

        private void Put(JsonObject target, string key, JsonNode value, string path, string basis, string because, params string[] from)
        {
            target[key] = value;
            _settings.Add(new DesignSetting(path, value.DeepClone(), basis, because, from));
        }

        private void Put(JsonObject target, string key, string value, string path, string basis, string because, params string[] from)
            => Put(target, key, JsonValue.Create(value)!, path, basis, because, from);

        private void Put(JsonObject target, string key, bool value, string path, string basis, string because, params string[] from)
            => Put(target, key, JsonValue.Create(value), path, basis, because, from);

        /// <summary>What a setting that follows from a flow field rests on: the intent, the repository, or a default.</summary>
        private string Basis(string field) => _flow[field]?.Provenance switch
        {
            Provenance.Stated => "stated",
            Provenance.Evidence => "evidence",
            _ => "default",
        };

        private string EnvironmentBecause() => _env switch
        {
            "dev" => "A dev workspace, where a queue may deliver to a listener on a developer's machine. apply refuses a file that " +
                     "names a lower environment than the workspace has, so this file cannot move a production queue to a laptop.",
            "prod" => "The production workspace: the queue delivers over HTTP.",
            _ => $"A {_env} workspace: the queue delivers over HTTP.",
        };

        /// <summary>The proposal must be a file apply reads, also with its profile: anything else is a bug here, not advice.</summary>
        private void Check(JsonObject content)
        {
            try
            {
                DeploymentFile file = DeploymentFile.Parse(content.ToJsonString());
                file.Resolve();
                // Det profilen lar stå åpent, kommer fra miljøet når fila tas i bruk: her en stedfortreder for basen.
                if (_profile.Count > 0)
                    file.ForProfile(_env, name => name == DeploymentTemplate.BaseUrlVariable && !_profile.ContainsKey(name)
                        ? "https://api.example.com"
                        : null).Resolve();
            }
            catch (QueueyConfigurationException ex)
            {
                throw new InvalidOperationException($"advise proposed a deployment file apply would refuse: {ex.Message}", ex);
            }
        }
    }
}
