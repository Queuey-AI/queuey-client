using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Advise;

/// <summary>
/// One setting advise writes into the deployment file and why. <see cref="Basis"/> says what it rests on: <c>stated</c>
/// (the intent), <c>evidence</c> (the repository, or the deployment file that is there), <c>default</c> (Queuey's or the
/// provider's usual value, which the person can change) or <c>recommendation</c> (advise's judgement). <see cref="From"/>
/// names the flow fields it follows from.
/// </summary>
public sealed record DesignSetting(string Path, JsonNode? Value, string Basis, string Because, IReadOnlyList<string> From);

/// <summary>
/// A credential the deployment file names, what it holds, and the command that stores it. Never the value.
/// <see cref="For"/> is <c>ingress</c> (the ingress waits for it, refusing events until it is stored) or <c>delivery</c>
/// (plan and apply look it up, so it is stored first).
/// </summary>
public sealed record CredentialNeed(string Name, string Type, string Holds, string Store, string For);

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

    /// <summary>What the content keeps of the file that is there and what it adds, in words; null when there is none.</summary>
    public string? Merge { get; init; }

    /// <summary>
    /// The whole deployment file, to write as it is: the one that is there with the flow's queue and profile values in it,
    /// or a new one. It carries no secrets: credentials are named, never given.
    /// </summary>
    public JsonObject Content { get; init; } = new();

    /// <summary>The <c>${VAR}</c>s the file reads from the environment when it is applied: those no profile gives.</summary>
    public IReadOnlyList<string> Variables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<CredentialNeed> Credentials { get; init; } = Array.Empty<CredentialNeed>();

    public IReadOnlyList<DesignSetting> Settings { get; init; } = Array.Empty<DesignSetting>();

    public IReadOnlyList<CodeStep> Code { get; init; } = Array.Empty<CodeStep>();

    public IReadOnlyList<string> NextSteps { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Turns a flow without conflicts into a Queuey design: the whole deployment file, where every setting advise writes has a
/// reason, the code steps that go with it, and what to run, in order. The design follows the provider: a Stripe queue
/// verifies Stripe's signature at the ingress and signs each delivery again in Stripe's format, so the handler keeps
/// constructEvent; a Supabase queue takes the webhook's key. In dev the queue delivers to a queuey listen session;
/// elsewhere over HTTP.
/// </summary>
/// <remarks>
/// When the repository has a deployment file, the design is that file with the flow in it, and the agent writes it as it
/// is (F2.10-review, 2026-10-06: advise writes nothing itself). The file wins over everything the intent does not state;
/// a stated value the file contradicts is a conflict, never an overwrite. What the file cannot hold, a profile value the
/// rules refuse or a combination apply would refuse, is a conflict too, with the way out.
/// </remarks>
public static class FlowDesigner
{
    public const string StripeCredential = "stripe-whsec";

    /// <summary>The design, or null when it gives the flow a conflict: the flow then says what stands in the way.</summary>
    public static FlowDesign? Design(DesiredFlow flow, FlowFacts facts, Advice? sending = null, string? profile = null)
    {
        if (flow.Conflicts.Count > 0)
            throw new InvalidOperationException("A flow with conflicts gets no design.");
        return new Builder(flow, facts, sending, profile).Build();
    }

    private sealed class Builder
    {
        private readonly DesiredFlow _flow;
        private readonly FlowFacts _facts;
        private readonly Advice? _sending;
        private readonly List<DesignSetting> _settings = new();
        private readonly List<CredentialNeed> _credentials = new();

        // To måter å lagre en credential på (F2.9, playbookene fra F2.11, 2026-10-06): set leser en verdi den som kjører,
        // holder, fra miljøet; request skriver ut en lenke der en person limer inn en verdi agenten aldri skal se. Regelen og
        // kommandoene er de plan og apply foreslår (CredentialStoring i Waas), så de ikke glir fra hverandre.
        private readonly CredentialStoring _storing;
        private readonly Dictionary<string, (string Type, string Variable)> _stored = new(StringComparer.Ordinal);
        private readonly List<CodeStep> _code = new();
        private readonly List<string> _next = new();
        private readonly List<FlowConflict> _conflicts = new();

        // Det fila som er der, får beholde, og det advise legger til: til forklaringen i merge.
        private readonly List<string> _added = new();
        private readonly Dictionary<string, JsonNode?> _kept = new(StringComparer.Ordinal);
        private readonly List<string> _notes = new();

        private readonly string _kind;
        private readonly string _env;
        private readonly string _queue;
        private readonly string _route;
        private readonly bool _local;
        private readonly string _prefix;
        private readonly ExistingDeployFile? _file;

        // Et umerket workspace er prod, og et antatt miljø skrives bare inn i en ny fil (Kenneths beslutninger, review av #58,
        // B2). FlowAdvisor antar derfor prod for en fil som finnes uten miljø, og hele designet følger miljøet i praksis: et
        // miljø intensjonen oppgir, fila sier, eller advise skriver inn, i en ny fil eller i profilen til en fil med en
        // miljøvariabel. Gir profilen for miljøet alt variabelen en verdi, er det fila sin som gjelder (re-review av #58).
        // Bare dev i praksis leverer til en lytter og gir Stripe sin testmodus; ellers HTTP, credentials request og et ekte
        // endepunkt, som plan og apply sier.
        private readonly bool _devInPractice;

        // Miljøet var antatt for en fil som finnes uten et, så prod-veien sier hvordan man får dev.
        private readonly bool _assumedForUnmarkedFile;

        // Med profiler (F2.7, #53) står verdiene som skiller miljøene, i profilen med miljøets navn, og fila tar dem som
        // ${VAR}: `queuey plan --profile dev` virker da, og prod er en PR som legger til en profil. En fil som finnes uten
        // profiler, holder seg til faste verdier, som resten av den.
        private readonly bool _portable;
        private readonly SortedDictionary<string, (string Value, DesignSetting Setting)> _profile = new(StringComparer.Ordinal);
        private readonly string _profileFlag;

        // Profilen verdiene går i, og som kommandoene tar med --profile. En ny fil får profilen med miljøets navn. En fil som
        // finnes med profiler, får aldri en ny (re-review av #59, og før tag): profilen som gir miljøet, eller den eneste fila har
        // (FileEnvironments.Choose, som FlowAdvisor velger med; ellers har flyten en konflikt). Profilens navn er da ikke
        // miljøet: en fil med bare profilen dev og uten miljø gjelder et workspace Queuey regner som prod.
        private readonly string _profileName;

        // Miljøet kommer fra en variabel som ingen verdi i fila holder fast for denne flyten: en fil uten profiler (review av #60,
        // B1), eller en profil som ikke setter variabelen selv, når intensjonen ikke oppgir miljøet (runde 6). Leveringstypen tar
        // da også en variabel, uten profilverdi og uten standardverdi, så den følger miljøet der plan og apply kjører.
        private readonly bool _kindFromVariable;

        public Builder(DesiredFlow flow, FlowFacts facts, Advice? sending, string? profile)
        {
            _flow = flow;
            _facts = facts;
            _sending = sending;
            _kind = flow.String("source.kind") ?? throw new InvalidOperationException("A design needs the source.");
            _env = flow.String("environment") ?? "dev";
            _queue = flow.String("queue") ?? throw new InvalidOperationException("A design needs the queue.");
            _route = flow.String("destination.route") ?? throw new InvalidOperationException("A design needs the route.");
            _prefix = $"queues.{_queue}";
            _file = facts.DeployFile;
            _portable = _file is null || _file.Profiles.Count > 0;
            // advise --profile velger profilen selv (review av #60): en profil fila har, eller navnet på den første i en ny fil.
            _profileName = profile ?? (_file is { Profiles.Count: > 0 } && FileEnvironments.Choose(_file, _env) is { } chosen ? chosen : _env);
            // Re-review av #60, runde 6: med ${QUEUEY_WORKSPACE_ENVIRONMENT:-dev} og en profil main som ikke satte variabelen, ga
            // standardverdien dev, og profiles.main fikk KIND=localForward. CI kjørte apply --profile main med prod, og køen ble
            // opprettet i prod med en lytter. Et oppgitt miljø skrives inn i profilen (WorkspaceEnvironment), og da holder den det.
            _kindFromVariable = _file is not null && FileEnvironments.Variable(_file) is { } environmentVariable
                                && (!_portable || (FileProfileValue(environmentVariable.Name) is null && !flow.IsStated("environment")));
            _profileFlag = _portable ? $" --profile {_profileName}" : "";

            // Miljøet er det FlowAdvisor kom fram til: oppgitt, i fila, i den ene profilen, eller antatt (dev for en ny fil, prod
            // for en som finnes). Bare dev i praksis leverer til en lytter og gir Stripe sin testmodus.
            _devInPractice = _env == "dev";
            _local = _devInPractice;
            _assumedForUnmarkedFile = flow["environment"] is { Provenance: Provenance.Assumed } && _file is not null;
            // Uten profil går credentials til det konfigurerte workspacet, ikke deploy-filas (review av #60), mens apply bruker
            // filas tenant. En fast tenant i fila står derfor i kommandoene.
            _storing = new CredentialStoring(_env, _portable ? _profileName : null,
                tenant: _portable ? null : Property(_file?.Json, "tenant") is JsonValue fileTenant && fileTenant.TryGetValue(out string? id)
                                           && id.IndexOf("${", StringComparison.Ordinal) < 0 ? id : null);
        }

        /// <summary>The value the file's profile gives <paramref name="variable"/>, or null when it gives none.</summary>
        private string? FileProfileValue(string variable)
            => Property((Property(_file?.Json, "profiles") as JsonObject)?[_profileName] as JsonObject, "variables") is JsonObject given
               && given[variable] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;

        /// <summary>
        /// The queue's delivery kind in the file, lower case, and where it is set: the kind itself, the profile's value for its
        /// ${VAR}, or that variable's ${VAR:-default}, as plan and apply read it. Null when the file gives the queue none.
        /// </summary>
        private (string Kind, string Where)? FileQueueKind()
        {
            if ((Property(_file?.Json, "queues") as JsonObject)?[_queue] is not JsonObject queue
                || Property(Property(queue, "delivery") as JsonObject, "kind") is not JsonValue value
                || !value.TryGetValue(out string? kind) || string.IsNullOrWhiteSpace(kind))
                return null;

            string leaf = $"queues.{_queue}.delivery.kind";
            if (FileEnvironments.ParseVariable(kind) is not { } variable)
                return (kind.Trim().ToLowerInvariant(), leaf);
            if (_portable && FileProfileValue(variable.Name) is { } given)
                return (given.Trim().ToLowerInvariant(), $"profiles.{_profileName}.variables.{variable.Name}");
            return variable.Default is { Length: > 0 } fallback ? (fallback.Trim().ToLowerInvariant(), $"the default in {leaf}") : null;
        }

        /// <summary>The variable the queue's delivery kind comes from: the file's, or the one advise writes; null for a fixed kind.</summary>
        private string? KindVariable()
        {
            if ((Property(_file?.Json, "queues") as JsonObject)?[_queue] is JsonObject queue
                && Property(Property(queue, "delivery") as JsonObject, "kind") is JsonValue value && value.TryGetValue(out string? kind))
                return FileEnvironments.ParseVariable(kind)?.Name;
            return _portable || _kindFromVariable ? DeploymentTemplate.QueueKindVariable(_queue) : null;
        }

        /// <summary>
        /// A queue the file already sends to a local listener, outside dev: a conflict, wherever the environment comes from. The
        /// design for it would point a real endpoint at a queue that delivers to a laptop, and nobody starts the listener.
        /// </summary>
        // Re-review av #59: en fil skrevet av en eldre advise kan ha kind localForward og ikke noe miljø. Fila vinner over det
        // advise antar, så designet beholdt localForward, mens stegene var for prod. Før tag gjelder det uansett hvor miljøet
        // kommer fra: oppgitt, fast i fila eller fra profilen, som en profil prod med KIND=localForward kopiert fra dev.
        private void ForwardingOutsideDev()
        {
            if (FileQueueKind() is not { Kind: "localforward" } forwarding)
                return;

            // Re-review av #60: en fast localForward i en fil som også kan kjøre utenfor dev, fordi miljøet kommer fra en
            // variabel (CI setter prod) eller en profil gir noe annet enn dev, sender køen til en lytter også der. Det gjelder
            // selv om flyten nå er for dev.
            if (_devInPractice)
            {
                // Runde 6: en profil som ikke setter miljøvariabelen, men har kind localForward, sender køen til en lytter også der
                // CI setter prod. advise skriver ikke lenger kind inn i en slik profil, men en fil kan ha det fra før.
                if (_portable && _kindFromVariable && forwarding.Where.StartsWith("profiles.", StringComparison.Ordinal))
                {
                    string environmentVariable = FileEnvironments.Variable(_file!)!.Value.Name;
                    string kindVariable = forwarding.Where[(forwarding.Where.LastIndexOf('.') + 1)..];
                    _conflicts.Add(new FlowConflict("ambiguous", "environment", null, JsonValue.Create("localForward"),
                        new[] { new FlowEvidence(_file!.File, null, $"{forwarding.Where} is localForward") },
                        $"{_file.File} forwards queues.{_queue} to a local listener ({forwarding.Where}), but the profile " +
                        $"{_profileName} does not set {environmentVariable}: where CI sets prod, the queue forwards there too.",
                        $"Take the kind out of the profile: remove {kindVariable} from profiles.{_profileName}.variables, and set it " +
                        $"where plan and apply run, localForward where {environmentVariable} is dev and http elsewhere. Or give the " +
                        $"profile {environmentVariable}=dev. Then run advise --intent again."));
                    return;
                }

                if (forwarding.Where != $"queues.{_queue}.delivery.kind" || !FileEnvironments.CanRunOutsideDev(_file!))
                    return;

                string variable = DeploymentTemplate.QueueKindVariable(_queue);
                _conflicts.Add(new FlowConflict("ambiguous", "environment", null, JsonValue.Create("localForward"),
                    new[] { new FlowEvidence(_file!.File, null, $"{forwarding.Where} is localForward") },
                    $"{_file.File} forwards queues.{_queue} to a local listener with a fixed kind, but the file can run outside dev: " +
                    (FileEnvironments.Variable(_file) is { } environment
                        ? $"its environment comes from ${{{environment.Name}}}."
                        : $"its profiles give {FileEnvironments.Describe(_file)}."),
                    $"Take the kind from a variable, as advise writes it: set {forwarding.Where} to ${{{variable}}}, which is " +
                    $"localForward in dev and http elsewhere. Then run advise --intent again."));
                return;
            }

            bool stated = _flow.IsStated("environment");
            string where = _flow["environment"]?.Provenance == Provenance.Assumed
                ? "but it names no workspace environment, and Queuey counts such a workspace as prod"
                : stated ? $"and the intent states {_env}" : $"and its workspace is {_env}";
            _conflicts.Add(new FlowConflict(stated ? "contradiction" : "ambiguous", "environment",
                stated ? JsonValue.Create(_env) : null, JsonValue.Create("localForward"),
                new[] { new FlowEvidence(_file!.File, null, $"{forwarding.Where} is localForward") },
                $"{_file.File} forwards queues.{_queue} to a local listener ({forwarding.Where}), {where}.",
                "Is this flow for dev? " + (WhatTheFileNeedsForDev() is { } needs ? $"If it is, {needs}." : "State the environment dev in the intent.") +
                $" For {_env}, set {forwarding.Where} to http in {_file.File}. Then run advise --intent again."));
        }

        /// <summary>
        /// What the file needs before a flow for dev can go into it, when it does not give dev itself: the profile gives no
        /// environment, or the file without profiles gives none or another by its variable's default. Null when stating dev in
        /// the intent is enough, or the file's environment is fixed.
        /// </summary>
        // Re-review av #60, runde 4 og 5: et oppgitt dev tar ikke en profil eller fil som ikke gir dev, så «state dev in the
        // intent» ledet rett inn i en ny konflikt. Veien ut sier i stedet hva fila må få.
        private string? WhatTheFileNeedsForDev()
        {
            if (_file is null)
                return null;

            string? variable = FileEnvironments.Variable(_file)?.Name;
            if (_portable)
                return FileEnvironments.GivenBy(_file, _profileName) is null
                    ? $"give the profile {_profileName} {variable ?? DeploymentTemplate.EnvironmentVariable}=dev in {_file.File}" +
                      (variable is null ? $", with workspace.environment ${{{DeploymentTemplate.EnvironmentVariable}}}" : "")
                    : null;

            if (FileEnvironments.Fixed(_file) is not null || FileEnvironments.WithoutProfile(_file) == "dev")
                return null;
            return $"set workspace.environment to dev in {_file.File}" +
                   (variable is null ? "" : $", or give ${{{variable}}} the default dev: ${{{variable}:-dev}}");
        }

        public FlowDesign? Build()
        {
            ForwardingOutsideDev();

            // 1. Forslaget for flyten: miljøet, køen og profilverdiene, hver innstilling med en grunn.
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
            else SharedSecret(delivery);
            queue["delivery"] = delivery;

            // 2. Hele fila: den som er der med flyten flettet inn, eller en ny.
            JsonObject content = Compose(workspace, queue);

            // 3. Det fila ikke kan holde, blir en konflikt med veien ut, ikke en fil apply avviser. Har flettingen gitt en
            // konflikt alt, foreslås ingenting, og selvsjekken ville bare gjentatt følgen av den.
            if (_conflicts.Count == 0)
                Check(content);
            if (_conflicts.Count > 0)
            {
                foreach (FlowConflict conflict in _conflicts)
                {
                    if (!_flow.Conflicts.Any(c => c.Field == conflict.Field))
                        _flow.Conflicts.Add(conflict);
                }
                return null;
            }

            IReadOnlyList<string> variables = FromEnvironment(content);
            CodePlan();
            NextSteps(variables);

            return new FlowDesign
            {
                Exists = _file is not null,
                Merge = _file is null ? null : MergeText(),
                Content = content,
                Variables = variables,
                Credentials = _credentials.ToArray(),
                Settings = FinalSettings(content),
                Code = _code.ToArray(),
                NextSteps = _next.ToArray(),
            };
        }

        // ── ingressen ────────────────────────────────────────────────────

        private void StripeIngress(JsonObject ingress)
        {
            // Stripe som oppgitt kilde gir Stripes signatur: det er ikke et valg advise tar, så en fil som sier noe annet, motsier
            // intensjonen.
            string basis = _flow.IsStated("source.kind") || _flow.IsStated("source.authentication") ? "stated" : Basis("source.authentication");
            Put(ingress, "authMode", "SignedRequest", $"{_prefix}.ingress.authMode", basis,
                "Stripe signs every webhook, so the ingress takes only what Stripe signed and refuses the rest.",
                "source.kind", "source.authentication");
            Put(ingress, "signedRequest", new JsonObject { ["template"] = "stripe", ["credentialRef"] = StripeCredential },
                $"{_prefix}.ingress.signedRequest", basis,
                $"Verify Stripe's scheme, the Stripe-Signature header, with the secret stored as {StripeCredential}. The file names " +
                "the credential and never holds the secret; until it is stored, the ingress refuses every event.",
                "source.kind");
            Put(ingress, "eventType", new JsonObject { ["from"] = "body", ["name"] = "type" }, $"{_prefix}.ingress.eventType", "default",
                "Stripe puts the event type in the body's type field. Queuey labels each event with it, which is what verify " +
                "--event-type and the console filter on.",
                "source.kind");

            _credentials.Add(Credential(StripeCredential, CredentialStoring.RequestDefaultType,
                "Stripe's webhook signing secret (whsec_…). Testing locally, it is the Stripe CLI's own, which stripe listen " +
                "--print-secret prints; for an endpoint in Stripe's dashboard, that endpoint's own, which a person pastes.",
                "STRIPE_WHSEC", "ingress"));
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

        // ── miljøet og leveransen ────────────────────────────────────────

        private void WorkspaceEnvironment(JsonObject workspace)
        {
            string? existing = _file?.Environment;
            string? variable = existing is not null && existing.IndexOf("${", StringComparison.Ordinal) >= 0
                ? DeploymentVariables.Referenced(existing).FirstOrDefault()
                : null;

            if (variable is not null)
            {
                // Fila tar miljøet fra en variabel, og apply nekter en den ikke får lese, som ${QUEUEY_STAGE} (#55).
                if (DeploymentVariables.RefusalOf(variable) is { } refused)
                {
                    _conflicts.Add(new FlowConflict("unsupported", "environment", null, null,
                        new[] { new FlowEvidence(_file!.File, null, $"the deployment file reads ${{{variable}}}") },
                        $"{_file.File} takes the workspace's environment from ${{{variable}}}, which a deployment file may not read: {refused}.",
                        $"Rename ${{{variable}}} in {_file.File} to a variable a deployment file may read, such as " +
                        $"{DeploymentTemplate.EnvironmentVariable} or a name without QUEUEY_, then run advise --intent again."));
                    return;
                }

                // Et oppgitt miljø går inn i profilen. Et antatt skrives ikke inn i en fil som finnes (re-review av #59): den ene
                // profilen gir det, eller miljøet der apply kjører.
                if (_portable && _flow.IsStated("environment"))
                    ProfileValue(variable, _env, Basis("environment"), EnvironmentBecause(), "environment");
                return;
            }

            if (existing is not null)
                return;   // fila har et fast miljø; motsier det intensjonen, er det en konflikt fra før

            if (_file is not null && !_flow.IsStated("environment"))
            {
                // Et miljø advise bare antar, skrives ikke inn i en fil som ikke har noe: det ville gjelde hele workspacet.
                _notes.Add("It leaves workspace.environment out: the file has none, and the intent does not state one.");
                return;
            }

            if (_portable)
            {
                variable = DeploymentTemplate.EnvironmentVariable;
                Put(workspace, "environment", "${" + variable + "}", "workspace.environment", "recommendation",
                    "The workspace's environment comes from the profile, so the same file goes to another environment with a " +
                    "profile of its own.", "environment");
                ProfileValue(variable, _env, Basis("environment"), EnvironmentBecause(), "environment");
                return;
            }

            // Hit kommer bare en fil som finnes uten profiler og uten miljø, med et oppgitt miljø. Et oppgitt dev er en konflikt
            // der (FlowAdvisor, re-review av #60, runde 5), så dev skrives aldri inn i en fil Queuey regner som prod.
            Put(workspace, "environment", _env, "workspace.environment", Basis("environment"), EnvironmentBecause(), "environment");
        }

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
                because = $"The handler's route, {_route}, on {DeploymentTemplate.BaseUrlVariable}, which the {_profileName} profile sets to the " +
                          "base the intent gives. On the queue rather than as the workspace's base, so the workspace and its other " +
                          "queues are left alone.";
            }
            else if (baseUrl is { Provenance: Provenance.Stated })
            {
                url = baseUrl.AsString!.TrimEnd('/') + _route;
                basis = "stated";
                because = $"The handler's route, {_route}, on the base the intent gives.";
            }
            else if (_file?.BaseUrl is not null)
            {
                url = _route;
                basis = "evidence";
                because = $"The handler's route. It appends to the workspace's base URL, which {_file.File} declares.";
            }
            else
            {
                url = "${" + DeploymentTemplate.BaseUrlVariable + "}" + _route;
                basis = "recommendation";
                because = $"The handler's route, {_route}, on {DeploymentTemplate.BaseUrlVariable}: where the receiver is reachable " +
                          "over HTTP. On the queue rather than as the workspace's base, so the workspace and its other queues are " +
                          "left alone.";
            }

            if (_local)
                because += " A local listener forwards each delivery to this path on your machine" +
                           (SignsDeliveries ? ", and Queuey signs a delivery for a listener only when its URL is absolute." : ".");

            Put(delivery, "url", url, $"{_prefix}.delivery.url", basis, because, "destination.route", "destination.baseUrl");

            string kind = _local ? "localForward" : "http";
            string kindBecause = _local
                ? "A dev workspace: each delivery goes to the queuey listen session on your machine, which forwards it to the " +
                  "receiver. While no session is connected, the events wait."
                : "Queuey delivers to the URL over HTTP.";
            if (_kindFromVariable)
            {
                // Review av #60, B1: en fil uten profiler som tar miljøet fra en variabel, som ${QUEUEY_WORKSPACE_ENVIRONMENT:-dev}
                // så CI kan sette prod, fikk kind localForward som fast verdi, og køen ble opprettet i prod med localForward. Nå
                // tar kind også en variabel, uten standardverdi, som pull --as skriver den: glemmer CI den, stopper apply. Runde 6:
                // det samme for en profil som ikke setter miljøvariabelen, og da uten profilverdi for kind.
                string variable = DeploymentTemplate.QueueKindVariable(_queue);
                string from = _portable
                    ? $"The profile {_profileName} does not set {FileEnvironments.Variable(_file!)!.Value.Name}, so the workspace's " +
                      "environment comes from where plan and apply run, and so does where the queue delivers"
                    : "The file takes the workspace's environment from a variable, so where the queue delivers does too";
                Put(delivery, "kind", "${" + variable + "}", $"{_prefix}.delivery.kind", "recommendation",
                    $"{from}: {variable} is localForward in dev and http elsewhere, set where plan and apply run, without a " +
                    "default, so apply stops rather than guess.",
                    "environment");
            }
            else if (_portable)
            {
                string variable = DeploymentTemplate.QueueKindVariable(_queue);
                Put(delivery, "kind", "${" + variable + "}", $"{_prefix}.delivery.kind", "recommendation",
                    "Where the queue delivers depends on the environment, so each profile sets it: localForward in dev, http elsewhere.",
                    "environment");
                // Leveringstypen følger miljøet som en standard, ikke som noe intensjonen sier: en fil som har en, beholder den.
                ProfileValue(variable, kind, "default", kindBecause, "environment");
            }
            else
            {
                Put(delivery, "kind", kind, $"{_prefix}.delivery.kind", Basis("environment"), kindBecause, "environment");
            }
        }

        /// <summary>A value for the flow's environment, under profiles.&lt;environment&gt;.variables.</summary>
        private void ProfileValue(string variable, string value, string basis, string because, params string[] from)
            => _profile[variable] = (value, new DesignSetting($"profiles.{_profileName}.variables.{variable}", JsonValue.Create(value), basis, because, from));

        /// <summary>Whether the design signs the queue's deliveries: only a Stripe queue does, in Stripe's format.</summary>
        private bool SignsDeliveries => _kind == "stripe" && _flow["requirements.verification"] is { AsString: "stripe-signature" };

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
            _credentials.Add(Credential(credential, type,
                $"The secret the handler compares the {check.Header} header with" +
                (check.SecretName is { } secretName ? $" ({secretName})." + EnvNote(secretName) : "."),
                variable, "delivery"));
        }

        /// <summary>
        /// A credential the file names, stored the way its environment calls for (<see cref="CredentialStoring"/>): in dev with
        /// credentials set from <paramref name="variable"/>, such as the Stripe CLI's test secret; elsewhere with credentials
        /// request, where a person pastes a value the caller never holds (F2.9).
        /// </summary>
        private CredentialNeed Credential(string name, string type, string holds, string variable, string @for)
        {
            _stored[name] = (type, variable);
            return new CredentialNeed(name, type, holds, _storing.Store(name, type, variable), @for);
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

        // ── hele fila ────────────────────────────────────────────────────

        /// <summary>
        /// The whole file: the one that is there, with the proposal merged in, or a new one. The file wins over every value
        /// the intent does not state; a stated value it contradicts is a conflict.
        /// </summary>
        private JsonObject Compose(JsonObject workspace, JsonObject queue)
        {
            JsonObject content = _file?.Json is { } json
                ? (JsonObject)json.DeepClone()
                : new JsonObject { ["$schema"] = DeploymentFile.SchemaUrl };

            if (workspace.Count > 0)
                MergeInto(Child(content, "workspace"), workspace, "workspace");

            JsonObject queues = Child(content, "queues");
            if (queues[_queue] is JsonObject there)
            {
                MergeUrl(there, queue);
                MergeKind(there, queue);
                MergeInto(there, queue, _prefix);
            }
            else
            {
                queues[_queue] = queue.DeepClone();
                if (_file is not null)
                    _added.Add(_prefix);
            }

            // Bare verdiene fila faktisk leser, går inn i profilen.
            HashSet<string> referenced = Referenced(content);
            var values = _profile.Where(p => referenced.Contains(p.Key)).ToArray();
            if (values.Length > 0)
            {
                JsonObject variables = Child(Entry(Child(content, "profiles"), _profileName), "variables");
                foreach ((string name, (string value, DesignSetting setting)) in values)
                {
                    if (ProfileProblem(name, value) is { } problem)
                    {
                        ProfileConflict(name, setting, problem);
                        continue;
                    }
                    MergeLeaf(variables, name, JsonValue.Create(value)!, setting.Path, setting, exact: true);
                }
            }

            return content;
        }

        /// <summary>Merges <paramref name="proposed"/> into <paramref name="target"/>, leaf by leaf.</summary>
        private void MergeInto(JsonObject target, JsonObject proposed, string path)
        {
            foreach ((string key, JsonNode? value) in proposed.ToList())
            {
                if (value is null)
                    continue;
                string leaf = $"{path}.{key}";
                if (Property(target, key) is JsonObject inner && value is JsonObject innerProposed)
                {
                    MergeInto(inner, innerProposed, leaf);
                    continue;
                }
                MergeLeaf(target, key, value, leaf, SettingFor(leaf), exact: false);
            }
        }

        /// <param name="exact">
        /// True for a name the file matches exactly (a variable's), false for a property, which it matches in any casing.
        /// </param>
        private void MergeLeaf(JsonObject target, string key, JsonNode proposed, string leaf, DesignSetting? setting, bool exact)
        {
            string? actual = exact ? (target.ContainsKey(key) ? key : null) : KeyOf(target, key);
            JsonNode? there = actual is null ? null : target[actual];
            if (there is null)
            {
                target[actual ?? key] = proposed.DeepClone();
                if (_file is not null && !_added.Any(a => leaf.StartsWith(a + ".", StringComparison.Ordinal)))
                    _added.Add(leaf);
                return;
            }

            if (Same(key, there, proposed))
                return;

            if (setting is { Basis: "stated" })
            {
                string field = setting.From.FirstOrDefault(f => _flow.IsStated(f)) ?? setting.From.FirstOrDefault() ?? leaf;
                _conflicts.Add(new FlowConflict("contradiction", field, proposed.DeepClone(), there.DeepClone(),
                    new[] { new FlowEvidence(_file!.File, null, $"{leaf} in the deployment file") },
                    $"{_file.File} has {leaf} as {Show(there)}, and the intent's {field} makes it {Show(proposed)}.",
                    $"Which holds for {leaf}: the file's value, or the intent's? Change the one that is wrong, then run advise " +
                    "--intent again."));
                return;
            }

            _kept[leaf] = proposed.DeepClone();
        }

        /// <summary>
        /// The queue's URL when the file has one. A stated route or base the file's URL contradicts is a conflict; otherwise
        /// the file's URL stays, whatever form it has.
        /// </summary>
        private void MergeUrl(JsonObject there, JsonObject queue)
        {
            if (Property(Property(there, "delivery") as JsonObject, "url") is not JsonValue thereValue || !thereValue.TryGetValue(out string? thereUrl)
                || queue["delivery"] is not JsonObject delivery || delivery["url"] is not JsonValue oursValue
                || !oursValue.TryGetValue(out string? ours))
                return;

            delivery.Remove("url");
            if (thereUrl == ours)
                return;

            string leaf = $"{_prefix}.delivery.url";
            string? path = PathOf(thereUrl);
            if (_flow.IsStated("destination.route") && path is not null && !FlowScan.SameRoute(path, _route))
            {
                _conflicts.Add(new FlowConflict("contradiction", "destination.route", JsonValue.Create(_route), JsonValue.Create(path),
                    new[] { new FlowEvidence(_file!.File, null, $"{leaf} in the deployment file") },
                    $"{_file.File} delivers queues.{_queue} to {path}, and the intent's route is {_route}.",
                    $"Which route is the queue's: {path}, as the file has it, or {_route}? Change the one that is wrong, then run advise " +
                    "--intent again."));
                return;
            }

            if (_flow.IsStated("destination.baseUrl") && BaseOf(thereUrl) is { } thereBase
                && !string.Equals(thereBase, _flow.String("destination.baseUrl")!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                _conflicts.Add(new FlowConflict("contradiction", "destination.baseUrl", JsonValue.Create(_flow.String("destination.baseUrl")),
                    JsonValue.Create(thereBase), new[] { new FlowEvidence(_file!.File, null, $"{leaf} in the deployment file") },
                    $"{_file.File} delivers queues.{_queue} to {thereBase}, and the intent's base URL is {_flow.String("destination.baseUrl")}.",
                    "Which base is the receiver's? Change the one that is wrong, then run advise --intent again."));
                return;
            }

            _kept[leaf] = JsonValue.Create(ours);
        }

        /// <summary>The queue's delivery kind when the file has one: it stays, since the kind follows no stated field directly.</summary>
        private void MergeKind(JsonObject there, JsonObject queue)
        {
            if (Property(Property(there, "delivery") as JsonObject, "kind") is not { } thereKind || queue["delivery"] is not JsonObject delivery
                || delivery["kind"] is not { } ours)
                return;

            delivery.Remove("kind");
            if (!Same("kind", thereKind, ours))
                _kept[$"{_prefix}.delivery.kind"] = ours.DeepClone();
        }

        /// <summary>The path of a delivery URL: after a leading ${VAR}, the path of an absolute URL, or a relative path as it is.</summary>
        private static string? PathOf(string url)
        {
            string u = url.Trim();
            if (u.StartsWith("${", StringComparison.Ordinal))
            {
                int end = u.IndexOf('}');
                return end < 0 ? null : (u[(end + 1)..] is { Length: > 0 } rest ? rest : "/");
            }
            if (u.StartsWith("/", StringComparison.Ordinal))
                return u;
            return Uri.TryCreate(u, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" ? uri.AbsolutePath : null;
        }

        /// <summary>The scheme and host of an absolute delivery URL; null for a relative one or one from a variable.</summary>
        private static string? BaseOf(string url)
            => !url.Contains("${", StringComparison.Ordinal) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
               && uri.Scheme is "http" or "https"
                ? $"{uri.Scheme}://{uri.Authority}"
                : null;

        /// <summary>Why a profile may not hold the value, in the deployment file's own words, or null when it may.</summary>
        private string? ProfileProblem(string name, string value)
        {
            if (DeploymentVariables.RefusalOf(name) is { } refused)
                return $"a deployment file may not read ${{{name}}}: {refused}.";
            try
            {
                DeploymentProfiles.Validate(new Dictionary<string, DeploymentProfile>(StringComparer.Ordinal)
                {
                    [_profileName] = new DeploymentProfile { Variables = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value } },
                });
                return null;
            }
            catch (QueueyConfigurationException ex)
            {
                return ex.Message;
            }
        }

        private void ProfileConflict(string name, DesignSetting setting, string problem)
        {
            string field = setting.From.FirstOrDefault() ?? setting.Path;
            string question = field switch
            {
                "destination.baseUrl" =>
                    $"Leave destination.baseUrl out of the intent and set {name} where plan and apply run: a value from the " +
                    "environment is not held to a profile's rule. Or give a base URL without a random-looking part.",
                "environment" when name != DeploymentTemplate.EnvironmentVariable =>
                    $"Rename ${{{name}}} in {(_file?.File ?? DeploymentFile.DefaultFileName)} to a variable a deployment file may read, such as " +
                    $"{DeploymentTemplate.EnvironmentVariable} or a name without QUEUEY_, then run advise --intent again.",
                _ => $"Give {name} its value from the environment where plan and apply run, rather than from the {_profileName} profile.",
            };
            _conflicts.Add(new FlowConflict("unsupported", field, null, null,
                _file is null ? Array.Empty<FlowEvidence>() : new[] { new FlowEvidence(_file.File, null, $"the deployment file reads ${{{name}}}") },
                $"The {_profileName} profile cannot hold {name}: {problem}", question));
        }

        /// <summary>Every ${VAR} the file reads outside its profiles.</summary>
        private static HashSet<string> Referenced(JsonObject content)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string key, JsonNode? value) in content)
            {
                if (!string.Equals(key, "profiles", StringComparison.OrdinalIgnoreCase))
                    Collect(value, names);
            }
            return names;

            static void Collect(JsonNode? node, HashSet<string> into)
            {
                switch (node)
                {
                    case JsonObject obj:
                        foreach ((string _, JsonNode? child) in obj)
                            Collect(child, into);
                        break;
                    case JsonArray array:
                        foreach (JsonNode? child in array)
                            Collect(child, into);
                        break;
                    case JsonValue v when v.TryGetValue(out string? text):
                        foreach (string name in DeploymentVariables.Referenced(text))
                            into.Add(name);
                        break;
                }
            }
        }

        /// <summary>The variables the file reads from the environment: those the flow's profile does not give.</summary>
        private IReadOnlyList<string> FromEnvironment(JsonObject content)
        {
            HashSet<string> referenced = Referenced(content);
            if (Property((Property(content, "profiles") as JsonObject)?[_profileName] as JsonObject, "variables") is JsonObject given)
                referenced.ExceptWith(given.Select(v => v.Key));
            return referenced.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// The settings as the file holds them: each one advise wrote, at its value there, and the ones the file kept with its
        /// own value and that reason. A setting the file does not hold (a profile value nothing reads) is left out.
        /// </summary>
        private IReadOnlyList<DesignSetting> FinalSettings(JsonObject content)
        {
            var settings = new List<DesignSetting>();
            foreach (DesignSetting setting in _settings.Concat(_profile.Values.Select(p => p.Setting)))
            {
                if (At(content, setting.Path) is not { } value)
                    continue;

                string[] kept = _kept.Keys.Where(k => k == setting.Path || k.StartsWith(setting.Path + ".", StringComparison.Ordinal)).ToArray();
                settings.Add(kept.Length == 0
                    ? setting with { Value = value.DeepClone() }
                    : setting with
                    {
                        Value = value.DeepClone(),
                        Basis = "evidence",
                        Because = $"Kept as {_file!.File} has it. advise would write {Show(_kept[kept[0]])} here ({setting.Basis}), and " +
                                  "the file wins over what the intent does not state.",
                    });
            }
            return settings;
        }

        private JsonNode? At(JsonObject content, string path)
        {
            JsonNode? node = content;
            string? previous = null;
            foreach (string part in Split(path))
            {
                // Navnet på en kø, en profil eller en variabel er en nøkkel leseren tar som den står; resten er egenskaper.
                bool name = previous is not null && (previous.Equals("queues", StringComparison.OrdinalIgnoreCase)
                                                     || previous.Equals("profiles", StringComparison.OrdinalIgnoreCase)
                                                     || previous.Equals("variables", StringComparison.OrdinalIgnoreCase));
                node = node is JsonObject obj ? (name ? obj[part] : Property(obj, part)) : null;
                if (node is null)
                    return null;
                previous = part;
            }
            return node;
        }

        // Stien har køens navn og variabelnavn i seg; et kønavn kan ha punktum, så det splittes med det for øye.
        private IEnumerable<string> Split(string path)
        {
            string queuesPrefix = $"queues.{_queue}";
            if (path == queuesPrefix || path.StartsWith(queuesPrefix + ".", StringComparison.Ordinal))
            {
                yield return "queues";
                yield return _queue;
                foreach (string part in path[queuesPrefix.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries))
                    yield return part;
                yield break;
            }
            foreach (string part in path.Split('.'))
                yield return part;
        }

        private DesignSetting? SettingFor(string leaf)
            => _settings.Where(s => leaf == s.Path || leaf.StartsWith(s.Path + ".", StringComparison.Ordinal))
                .OrderByDescending(s => s.Path.Length)
                .FirstOrDefault();

        /// <summary>
        /// The key the file gives a property: the deployment file's reader matches property names in any casing, so a file's
        /// "Queues" is its queues, and a second "queues" beside it would be a file it refuses (review of #56, K-3).
        /// </summary>
        private static string? KeyOf(JsonObject obj, string property)
        {
            if (obj.ContainsKey(property))
                return property;
            foreach ((string key, JsonNode? _) in obj)
            {
                if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
            return null;
        }

        private static JsonNode? Property(JsonObject? obj, string property)
            => obj is not null && KeyOf(obj, property) is { } key ? obj[key] : null;

        /// <summary>A property's object, made when the file has none: matched in any casing, as the reader does.</summary>
        private static JsonObject Child(JsonObject parent, string property)
        {
            string key = KeyOf(parent, property) ?? property;
            if (parent[key] is JsonObject child)
                return child;
            var created = new JsonObject();
            parent[key] = created;
            return created;
        }

        /// <summary>A named entry's object (a queue, a profile), made when the file has none: matched as it is written.</summary>
        private static JsonObject Entry(JsonObject parent, string name)
        {
            if (parent[name] is JsonObject child)
                return child;
            var created = new JsonObject();
            parent[name] = created;
            return created;
        }

        private static readonly HashSet<string> CaseFree = new(StringComparer.Ordinal)
        {
            "authMode", "kind", "ordering", "mode", "template", "templateKey", "from", "method",
        };

        /// <summary>Whether two values say the same, with the casing the deployment file ignores ignored.</summary>
        private static bool Same(string key, JsonNode? a, JsonNode? b)
        {
            if (a is JsonValue va && b is JsonValue vb && va.TryGetValue(out string? sa) && vb.TryGetValue(out string? sb))
                return string.Equals(sa, sb, CaseFree.Contains(key) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (a is JsonObject oa && b is JsonObject ob)
                return oa.Count == ob.Count && oa.All(p => KeyOf(ob, p.Key) is { } k && Same(p.Key, p.Value, ob[k]));
            return JsonNode.DeepEquals(a, b);
        }

        private static string Show(JsonNode? value) => value?.ToJsonString() ?? "null";

        /// <summary>The proposal must be a file apply reads: what it cannot read is a conflict with the reason, not advice.</summary>
        private void Check(JsonObject content)
        {
            try
            {
                DeploymentFile file = DeploymentFile.Parse(content.ToJsonString());
                file.Resolve();

                // En ny fil har bare advise sine variabler, så den utvides med profilen, slik apply --profile gjør. Det profilen lar
                // stå åpent, kommer fra miljøet: her en stedfortreder for basen. En fil som var der, kan lese variabler bare
                // brukerens miljø kjenner, så den sjekkes uten å utvides.
                if (_file is null && file.Profiles is { } profiles && profiles.ContainsKey(_profileName))
                {
                    HashSet<string> given = profiles[_profileName].Variables?.Keys.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
                    file.ForProfile(_profileName, name => name == DeploymentTemplate.BaseUrlVariable && !given.Contains(name)
                        ? "https://api.example.com"
                        : null).Resolve();
                }
            }
            catch (QueueyConfigurationException ex)
            {
                _conflicts.Add(new FlowConflict("unsupported", DeploymentFile.DefaultFileName, null, null, Array.Empty<FlowEvidence>(),
                    $"The deployment file advise would propose is one apply refuses: {ex.Message}",
                    "Change what the message names, in the intent or in queuey.deploy.json, then run advise --intent again."));
            }
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
                            "that is the Stripe CLI's own, which stripe listen --print-secret prints." + EnvNote(secret), "evidence")
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

            SecretCheckCode();
            if (SecretCheck() is null && _flow.String("requirements.verification") == "none")
            {
                _code.Add(new CodeStep(HandlerFile(), payload?.Line, "add",
                    "Check what reaches the handler: compare a header with a secret Queuey sends, or sign the queue's deliveries " +
                    "(delivery.signing) and verify Queuey's signature, as https://queuey.ai/docs/how-to/verify-deliveries shows.",
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

        /// <summary>The handler's own header check: kept, and in dev a word about what a listener does not pass on.</summary>
        private void SecretCheckCode()
        {
            if (SecretCheck() is not { } check)
                return;

            _code.Add(new CodeStep(check.File, check.Line, "keep", $"Keep the {check.Header} check.",
                $"Queuey sends that header with the secret stored as {_queue}-webhook-secret on every delivery over HTTP.",
                "evidence"));
            // advise slår ikke på signering for en hodesjekk, så en lokal leveranse har verken hodet eller, uten signering fra
            // fila, en signatur. Før 2026-10-06 sto det at den bar Queuey sin signatur. Playbooken for Supabase sier det samme:
            // bevis flyten mot den deployede mottakeren, eller godta signaturen for en kø som signerer.
            if (_local)
                _code.Add(new CodeStep(check.File, check.Line, "configure",
                    $"While you develop, expect the {check.Header} check to refuse local deliveries: queuey listen does not pass " +
                    "the header on, so the receiver refuses each one and the event goes to the dead-letter queue. Prove the flow " +
                    "against the deployed receiver instead, or let the handler accept Queuey's signature once the queue signs " +
                    "its deliveries (delivery.signing), as https://queuey.ai/docs/how-to/verify-deliveries shows.",
                    "Queuey never sends a credential to a developer's machine. A local delivery carries Queuey's signature only " +
                    "for a queue that signs its deliveries, and advise does not turn that on for a header check.", "evidence"));
        }

        private void AppCode()
        {
            SecretCheckCode();

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

        /// <summary>Where a .env file sets <paramref name="name"/>, by name: advise reads the names there, never the value.</summary>
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

        private void NextSteps(IReadOnlyList<string> variables)
        {
            _next.Add(_file is null
                ? "Write infrastructure.content to queuey.deploy.json, and keep the flow beside it, as queuey.flow.json or in the " +
                  "pull request."
                : "Write infrastructure.content to queuey.deploy.json as it is: it is the whole file, with what is there kept. Keep " +
                  "the flow beside it, as queuey.flow.json or in the pull request.");

            if (variables.Count > 0)
            {
                // Leveringstypen fra en variabel (som pull --as skriver den) får verdien den skal ha her (før tag): uten profil
                // sier ingenting ellers at den er http i prod.
                // Re-review av #60: begge verdiene, knyttet til miljøvariabelen, så en agent ikke setter localForward der prod kjører.
                string? kindVariable = KindVariable();
                string kindValues = kindVariable is null || !variables.Contains(kindVariable) ? ""
                    : (_file is not null && FileEnvironments.Variable(_file) is { } environment
                          ? $" Where {environment.Name} is dev, set {kindVariable}=localForward"
                          : $" In a dev workspace, set {kindVariable}=localForward") +
                      $"; everywhere else (CI, prod), set {kindVariable}=http.";
                bool baseUrl = variables.Contains(DeploymentTemplate.BaseUrlVariable);
                _next.Add($"Set {string.Join(", ", variables)} where plan and apply run: the file reads {(variables.Count == 1 ? "it" : "them")} " +
                          "from the environment." + kindValues +
                          (baseUrl
                              ? $" {DeploymentTemplate.BaseUrlVariable} is where the receiver is reachable over HTTP, such as production's URL" +
                                (_local ? "; while the queue forwards to a listener, Queuey sends nothing there and takes only the path." : ".")
                              : "") +
                          (_portable
                              ? $" A value that is the same every time can go under profiles.{_profileName}.variables instead" +
                                (kindValues.Length > 0 ? $", but not {kindVariable}, which follows the environment." : ".")
                              : ""));
            }

            if (_portable)
                _next.Add($"--profile {_profileName} takes your connection from ~/.queuey/config.json, never from the repository: " +
                          $"profiles.{_profileName} there, with apiKey, license and tenant, readable only by you (chmod 600). The key is " +
                          "minted in the Queuey console.");

            // Bare der fila sier dev: en fil uten miljø senker ingenting, og apply har ingenting å nekte (B2).
            if (_devInPractice)
                _next.Add("Apply it to a workspace set to dev. Only a person lowers a workspace's environment, in the Queuey " +
                          "console, and apply refuses this file against a higher one.");

            // En leveranse-credential slås opp av plan og apply, som stopper uten den. Stripe sin på ingressen kan vente (F2.3).
            foreach (CredentialNeed credential in _credentials.Where(c => c.For == "delivery"))
            {
                (string credentialType, string variable) = _stored[credential.Name];
                _next.Add($"Store the secret the handler checks before plan and apply, which look {credential.Name} up. " +
                          _storing.HowToStore(credential.Name, credentialType, variable));
            }

            _next.Add($"queuey apply --dry-run{_profileFlag} checks the file and sends nothing. queuey plan{_profileFlag} asks Queuey " +
                      "what would change, and shows the queue's ingress URL.");

            string type = _flow.Strings("source.eventTypes").FirstOrDefault()
                          ?? (_kind == "stripe" ? "checkout.session.completed" : _kind == "supabase" ? "INSERT" : "order.created");
            string port = _flow.Int("destination.port") is { } p ? p.ToString(CultureInfo.InvariantCulture) : "<port>";

            switch (_kind)
            {
                case "stripe":
                    string set = _storing.Set(StripeCredential, CredentialStoring.RequestDefaultType, "STRIPE_WHSEC");
                    string request = _storing.Request(StripeCredential, CredentialStoring.RequestDefaultType);
                    _next.Add($"queuey apply{_profileFlag}. Until {StripeCredential} is stored, the ingress refuses every event.");
                    if (_devInPractice)
                    {
                        // Testmodus: Stripe CLI-ens egen hemmelighet er en testhemmelighet agenten kan holde, aldri i utdata. Et
                        // ekte endepunkts hemmelighet limer en person inn (F2.9, playbooken fra F2.11). Hentingen og lagringen står
                        // på én linje (K1 i runde 2 av #58): i et agentverktøy er hvert kall et nytt skall, og en variabel fra et
                        // kall er borte i det neste. --replace står aldri i et forslag, heller ikke i testmodus (besluttet av Kenneth
                        // 2026-10-06, review av #60): et bytte er en persons beslutning. Teksten sier bare at set nekter en annen
                        // testhemmelighet under samme navn.
                        // Re-review av #60: bare en apply som gikk gjennom, viser at workspacet er dev. Ble fila nektet, er det ikke
                        // det, og testnøkkelen skal ikke lagres der.
                        _next.Add("Test mode, with Stripe's own CLI, once that apply has gone through: if it refused the file, the " +
                                  "workspace is not dev, so stop, and store no test secret. The Stripe CLI's signing secret is a test " +
                                  "secret you may hold. Take it and " +
                                  "store it in one command, so it stays out of the output and needs no variable from an earlier shell. " +
                                  $"In a POSIX shell: STRIPE_WHSEC=\"$(stripe listen --print-secret)\" {set}. In PowerShell: " +
                                  $"$env:STRIPE_WHSEC = stripe listen --print-secret; {set}; Remove-Item Env:STRIPE_WHSEC, since $env: lasts " +
                                  $"for the session. Then run queuey apply{_profileFlag} again, " +
                                  "which points the ingress at it, and stripe listen --forward-to <ingress URL> in the background. " +
                                  $"The same secret stored again changes nothing. If {StripeCredential} holds another test secret, from " +
                                  "another machine or Stripe account, set refuses it, and replacing it is a decision for a person. A real " +
                                  $"endpoint's secret is never yours to hold: a person pastes it on the page {request} opens.");
                        _next.Add((SecretName() is { } secret
                                      ? $"Run the app with {secret} set to that same secret."
                                      : "Run the app with its signing secret set to that same secret.") +
                                  " stripe listen --print-secret prints the same one each time, so take it from there again rather " +
                                  "than from any output.");
                        _next.Add($"queuey listen{_profileFlag} --queue {_queue} --forward-to http://localhost:{port} --json, in the " +
                                  "background.");
                        _next.Add($"queuey verify {_queue}{_profileFlag} --event-type {type} --ingress-auth stripe --json, and while it " +
                                  $"waits: stripe trigger {type}.");
                    }
                    else
                    {
                        // Et ekte endepunkt: hemmeligheten går aldri gjennom agenten (F2.9). Den lagres før endepunktet pekes hit,
                        // ellers avviser ingressen Stripe sine eventer til den er der.
                        _next.Add("A real endpoint's signing secret never passes through you or this conversation: " +
                                  $"{request} prints a link where a person pastes it, and queuey credentials list{_storing.Connection} " +
                                  $"--json lists {StripeCredential} once it is stored. credentials set --from-env is only for the " +
                                  "test secret stripe listen --print-secret gives, in dev.");
                        _next.Add("Then point the Stripe webhook endpoint at the queue's ingress URL, in the dashboard or with Stripe's " +
                                  "API. Changing the URL of an endpoint that exists keeps its secret, which the handler has. A new " +
                                  "endpoint has a secret of its own: the person pastes that one, and gives the handler the same.");
                        _next.Add($"queuey verify {_queue}{_profileFlag} --event-type {type} --ingress-auth stripe --json, and send that " +
                                  "event from Stripe while it waits.");
                        // Re-review av #60, runde 5: et oppgitt dev tar ikke lenger en fil som ikke gir noe miljø, så steget sier
                        // hva fila må få.
                        if (_assumedForUnmarkedFile)
                            _next.Add("For test mode with stripe listen instead, " +
                                      (WhatTheFileNeedsForDev() ?? "state the environment dev in the intent") +
                                      ", if its workspace is dev, and run advise --intent again. Only a person lowers a workspace to " +
                                      "dev, in the Queuey console.");
                    }
                    break;

                case "supabase":
                    _next.Add($"queuey apply{_profileFlag}.");
                    _next.Add("Mint a key that can publish to this queue only, in the Queuey console. Point the Database Webhook at " +
                              "the queue's ingress URL with the key in X-Api-Key: Supabase dashboard → Integrations → Database " +
                              "Webhooks.");
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

        /// <summary>What the content keeps of the file that is there, what it adds, and what it does not carry over.</summary>
        private string MergeText()
        {
            var parts = new List<string>
            {
                $"The whole of {_file!.File}, with everything that is there kept" +
                (_added.Count > 0 ? $"; it adds {string.Join(", ", _added)}." : "; it adds nothing."),
            };
            parts.AddRange(_notes);
            foreach ((string leaf, JsonNode? proposed) in _kept)
                parts.Add($"{leaf} stays as the file has it; advise would write {Show(proposed)}, which the intent does not state.");
            if (_file.HasComments)
                parts.Add("The file has comments or trailing commas, which this content does not have: put them back after writing " +
                          "it, or make the same change by hand.");
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
    }
}
