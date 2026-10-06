using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// What applying a deployment file would do, asked of Queuey itself: every write <c>apply</c> would
/// send runs as a dry run on the server — the same validation, the same refusals — and nothing is
/// stored. The <c>terraform plan</c> to <see cref="IQueueyService.ApplyDeploymentAsync"/>.
/// </summary>
public sealed class DeploymentPlan
{
    /// <summary>The workspace the plan was made against.</summary>
    public string Tenant { get; init; } = default!;

    /// <summary>
    /// The plan's id: <c>plan_</c> and the start of <see cref="PlanHash"/>, so the same file against the same state gives
    /// the same id. Opaque: compare it, never parse it.
    /// </summary>
    public string PlanId { get; init; } = default!;

    /// <summary>
    /// <c>sha256:…</c> over what apply would change and the server state it rests on, normalized so that the order of the
    /// queues and the file's formatting do not count (<see cref="DeploymentPlanHash"/>). A plan made again after the state
    /// moved, or after the file changed what apply does, has another hash.
    /// </summary>
    public string PlanHash { get; init; } = default!;

    /// <summary>One entry per queue the file declares: its ingress URL, and whether it exists yet.</summary>
    public IReadOnlyList<DeploymentPlanQueue> Queues { get; init; } = Array.Empty<DeploymentPlanQueue>();

    /// <summary>One entry per write apply would send, in the order it would send them.</summary>
    public IReadOnlyList<DeploymentPlanStep> Steps { get; init; } = Array.Empty<DeploymentPlanStep>();

    /// <summary>
    /// The queues, and the workspace, apply would leave alone because a person detached them (Queuey F2.4). No step is
    /// planned for them. <c>--adopt</c> plans them again.
    /// </summary>
    public IReadOnlyList<SkippedResource> Skipped { get; init; } = Array.Empty<SkippedResource>();

    /// <summary>
    /// Whether Queuey started an apply for the plan's dry runs (Queuey F2.4). False against a Queuey that predates managed
    /// resources; then a dry run against a resource a file manages is answered as it would be outside an apply.
    /// </summary>
    public bool ApplyStarted { get; init; }

    /// <summary>True when Queuey would accept every write.</summary>
    public bool WouldSucceed => Steps.All(s => s.Error is null);

    /// <summary>How many values would change, counting a queue that would be created as one.</summary>
    public int ChangeCount => Steps.Sum(s => s.Changes.Count + (s.Creates ? 1 : 0));
}

/// <summary>A queue the file declares, as the plan sees it.</summary>
public sealed class DeploymentPlanQueue
{
    /// <summary>The queue's name.</summary>
    public string Name { get; init; } = default!;

    /// <summary>
    /// Where producers publish to it: <c>{ingress}/events/{tenant}/{name}</c>. Known before the queue exists, since it is
    /// built from the name, so a provider can be pointed at it in the same change.
    /// </summary>
    public string IngressUrl { get; init; } = default!;

    /// <summary>The queue's id, or null when apply would create it.</summary>
    public string? PublicId { get; init; }
}

/// <summary>One write in a <see cref="DeploymentPlan"/>.</summary>
public sealed class DeploymentPlanStep
{
    /// <summary><c>workspace</c>, or <c>queues.&lt;name&gt;</c>.</summary>
    public string Target { get; init; } = default!;

    /// <summary>
    /// What the write touches: <c>environment</c>, <c>queue</c>, <c>ingress</c>, <c>policy</c>, <c>delivery</c> or <c>mode</c>.
    /// </summary>
    public string Aspect { get; init; } = default!;

    /// <summary>True when the write would create the queue.</summary>
    public bool Creates { get; init; }

    /// <summary>The values that would change. Empty when the write would change nothing.</summary>
    public IReadOnlyList<PlannedChange> Changes { get; init; } = Array.Empty<PlannedChange>();

    /// <summary>What else the write reaches, or what could not be checked yet.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>The refusal the write would get — code, message and what to do — or null.</summary>
    public QueueyException? Error { get; init; }

    /// <summary>
    /// What the step rests on, as the plan's hash covers it: the server's <c>stateHash</c> of the config its dry run
    /// started from, or the mode a queue has when its declared mode needs no write. Null for a refused step and a queue
    /// apply would create.
    /// </summary>
    public string? State { get; init; }

    /// <summary>For a queue apply would create: what it would send once the queue exists, which no dry run can answer for.</summary>
    public JsonElement? Desired { get; init; }
}

/// <summary>One value that would change: a path into the config read-back, and the JSON on each side.</summary>
public sealed class PlannedChange
{
    /// <summary>Where, e.g. <c>policy.retentionDays</c>.</summary>
    public string Path { get; init; } = default!;

    /// <summary>
    /// The value now, as Queuey's config reads it back: a number, a boolean, a string or an object. Null when there is none.
    /// </summary>
    // Typet som serverens ConfigChangeDto (review 2026-10-05). Før var verdiene tekst, så plan --json skrev 7 og true
    // som strenger, og en streng "7" kunne ikke skilles fra tallet.
    public JsonElement? From { get; init; }

    /// <summary>The value after, as Queuey's config would read it back. Null when there would be none.</summary>
    public JsonElement? To { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Path}: {Text(From) ?? "(none)"} → {Text(To) ?? "(none)"}";

    /// <summary>A value as one line of text: a string without its quotes, anything else as JSON.</summary>
    internal static string? Text(JsonElement? value) => value switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { } v => v.GetRawText(),
    };

    /// <summary>The value as it came, detached from the answer it was read from, or null for none.</summary>
    internal static JsonElement? Raw(JsonElement? value)
        => value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } v ? v.Clone() : null;
}

/// <summary>
/// Queuey answered a dry run with something that is not a plan — an empty body, a 204, text that is
/// not JSON, or JSON without <c>dryRun: true</c> — so it may have carried the write out. Planning
/// stops at once, because every further call might write too.
/// </summary>
public sealed class DryRunIgnoredException : QueueyException
{
    internal DryRunIgnoredException(string target, string aspect)
        : base($"Queuey answered the dry run of {target} · {aspect} with something that is not a plan, so it may have " +
               "carried that write out. Planning stopped before sending anything else.", errorCode: "dry_run_ignored")
    {
        Target = target;
        Aspect = aspect;
        SuggestedAction = $"Compare the workspace with the file using `queuey apply --check` to see whether {target} · {aspect} changed.";
    }

    /// <summary>What the write touched: <c>workspace</c>, or <c>queues.&lt;name&gt;</c>.</summary>
    public string Target { get; }

    /// <summary>Which part of it: <c>queue</c>, <c>ingress</c>, <c>policy</c>, <c>delivery</c> or <c>mode</c>.</summary>
    public string Aspect { get; }
}

// ── wire ──────────────────────────────────────────────────────────────────────

/// <summary>What every dry-run answer carries: that it is one.</summary>
internal abstract class DryRunAnswer
{
    public bool DryRun { get; set; }
}

internal sealed class ConfigPlanResponse : DryRunAnswer
{
    public string? Target { get; set; }
    public List<ConfigChangeResponse>? Changes { get; set; }
    public List<string>? Notes { get; set; }

    // sha256 over den kanoniske lesingen skrivingen startet fra (Queuey F2.3). Null fra et API som er eldre enn feltet.
    public string? StateHash { get; set; }
}

internal sealed class ConfigChangeResponse
{
    public string? Path { get; set; }
    public JsonElement? From { get; set; }
    public JsonElement? To { get; set; }
}

internal sealed class ApplyQueuePlanResponse : DryRunAnswer
{
    public string? PublicId { get; set; }
    public string? DisplayName { get; set; }
    public bool Created { get; set; }
    public bool HasDeliveryTarget { get; set; }
}

/// <summary>
/// Walks a deployment file the way <c>ApplyDeploymentAsync</c> does, sending every write as a dry run.
/// </summary>
internal sealed class DeploymentPlanner
{
    private static readonly HttpMethod Patch = new("PATCH");

    private readonly QueueyControlPlaneClient _controlPlane;
    private readonly IQueueyManagement _management;
    private readonly Uri _ingressBase;

    public DeploymentPlanner(QueueyControlPlaneClient controlPlane, IQueueyManagement management, Uri ingressBase)
    {
        _controlPlane = controlPlane;
        _management = management;
        _ingressBase = ingressBase;
    }

    /// <summary>One write the plan sends: where it goes, what it carries, and whether it changes anything as a real write.</summary>
    private sealed record PlannedWrite(string Target, string Aspect, HttpMethod Method, object Body, string[] Segments, bool ChangesNothing = false)
    {
        public string Key => $"{Target} · {Aspect}";
    }

    public async Task<DeploymentPlan> PlanAsync(
        DeploymentFile file, IReadOnlyList<DeploymentQueuePlan> plans, string tenant, CancellationToken ct,
        IReadOnlyList<SkippedResource>? skipped = null)
    {
        // Det en person har løsrevet (Queuey F2.4), planlegges ikke: apply hopper over det. Køene står likevel i lista med
        // ingress-URL-en, så planen sier hva fila nevner.
        skipped ??= Array.Empty<SkippedResource>();
        var skippedQueues = new HashSet<string>(skipped.Where(s => s.QueueName is not null).Select(s => s.QueueName!), StringComparer.Ordinal);
        bool skipsWorkspace = skipped.Any(s => s.QueueName is null);

        // Lesingene først, før noe sendes: køene slik de er, og hvert credential-navn fila bruker. Et navn
        // som mangler, feiler planen her, før første skriving, slik det feiler apply (review 2026-09-24).
        // Før ble det et avslag på ett steg, oppdaget midt i planen.
        var existing = new Dictionary<string, QueueListItem>(StringComparer.Ordinal);
        foreach (QueueListItem row in await _management.ListQueuesAsync(tenant, ct).ConfigureAwait(false))
        {
            if (row.DisplayName is { } name)
                existing[name] = row;
        }

        CredentialStoring storing = CredentialStoring.For(file);
        ResolvedDeliveries deliveries = await new CredentialResolver(_management, tenant, storing)
            .ResolveAllAsync(file.Workspace?.Delivery, plans, ct).ConfigureAwait(false);

        List<PlannedWrite> workspaceWrites = skipsWorkspace ? new List<PlannedWrite>() : WorkspaceWrites(file.Workspace, deliveries, tenant);
        IReadOnlyList<DeploymentQueuePlan> planned = plans.Where(p => !skippedQueues.Contains(p.Definition.Name)).ToList();

        // Beviset på at serveren planlegger, før noe annet sendes. Svaret gjelder også som svaret på
        // den skrivingen, så den sendes ikke to ganger.
        var answered = new Dictionary<string, DryRunAnswer>(StringComparer.Ordinal);
        if (ChooseProbe(workspaceWrites, planned, existing, tenant) is { } probe)
            answered[probe.Key] = await ProbeAsync(probe, ct).ConfigureAwait(false);

        var steps = new List<DeploymentPlanStep>();
        foreach (PlannedWrite write in workspaceWrites)
            steps.Add(await StepAsync(write, answered, ct).ConfigureAwait(false));

        // Queuey (vedtatt av Kenneth 2026-10-06): bare et workspace merket dev videresender til en lytter. En kø som finnes,
        // spørres med dry run av leveringstypen, og svarer med Queueys avslag. En ny kø kan ikke tørrkjøres felt for felt, så
        // planen leser miljøet workspacet får: det fila setter, ellers det workspacet har. Bare når en ny kø vil videresende.
        ListenerEnvironment? listenerEnvironment = null;
        if (planned.Any(p => p.Kind == DeploymentDeliveryKind.LocalForward && !existing.ContainsKey(p.Definition.Name)))
        {
            listenerEnvironment = !skipsWorkspace && file.Workspace?.EnvironmentToSend is { } declared
                ? new ListenerEnvironment(declared, FromFile: true)
                : new ListenerEnvironment((await _controlPlane.GetTenantConfigAsync(tenant, ct).ConfigureAwait(false)).Environment, FromFile: false);
        }

        // Apply sender workspacets levering før køene, så en base-URL i fila er et mål for hver kø uten egen absolutt URL,
        // også når workspacet ikke har noen ennå. Planen ser bare det som er lagret, og avviste derfor en gyldig første
        // fil med deliver_without_destination (review 2026-10-05).
        bool workspaceBase = !string.IsNullOrWhiteSpace(file.Workspace?.Delivery?.BaseUrl);

        foreach (DeploymentQueuePlan plan in planned)
            steps.AddRange(await PlanQueueAsync(plan, tenant, deliveries, storing, existing, answered, workspaceBase, listenerEnvironment, ct).ConfigureAwait(false));

        List<DeploymentPlanQueue> queues = plans.Select(p => new DeploymentPlanQueue
        {
            Name = p.Definition.Name,
            IngressUrl = QueueyUri.Build(_ingressBase, null, "events", tenant, p.Definition.Name).ToString(),
            PublicId = existing.TryGetValue(p.Definition.Name, out QueueListItem? row) ? row.PublicId : null,
        }).ToList();

        string hash = DeploymentPlanHash.Of(tenant, queues, steps);
        return new DeploymentPlan
        {
            Tenant = tenant,
            PlanId = DeploymentPlanHash.IdOf(hash),
            PlanHash = hash,
            Queues = queues,
            Steps = steps,
            Skipped = skipped,
        };
    }

    /// <summary>
    /// The note for a queue apply would create, whose ingress names a credential that is not stored yet: its ingress would
    /// refuse every event, and how to store the credential where the file goes.
    /// </summary>
    internal static string AwaitedCredentialNote(string awaited, CredentialStoring storing)
    {
        // Navnet er sjekket mot formen (DeploymentSignedRequest.Validate); Showable holder det ute av kommandoen like fullt.
        // Utenfor dev limer en person inn verdien (CredentialStoring, F2.9); før sto credentials set her også i prod. Med set
        // må en Queuey fra før credential-forespørsler ha en apply til før ingressen bruker den.
        string note = CredentialNameRules.Showable(awaited) is { } shown
            ? $"No credential named '{shown}' is stored in this workspace yet, so its ingress would refuse every event until it " +
              $"is. {storing.HowToStore(shown, CredentialStoring.RequestDefaultType)}"
            : "The credential its ingress names is not stored in this workspace yet, so its ingress would refuse every event until " +
              $"it is. {storing.HowToStore(CredentialStoring.Placeholder, CredentialStoring.RequestDefaultType)}";
        return storing.AsksAPerson ? note : note + " Then apply again.";
    }

    /// <summary>The workspace's writes, in the order apply sends them: environment, ingress, policy, delivery.</summary>
    private static List<PlannedWrite> WorkspaceWrites(DeploymentWorkspace? workspace, ResolvedDeliveries deliveries, string tenant)
    {
        var writes = new List<PlannedWrite>();
        if (workspace is null)
            return writes;

        // Planen spør om miljø-merket som apply sender det, så en senking en nøkkel ikke får gjøre, står som avslag her.
        if (workspace.EnvironmentToSend is { } environment)
            writes.Add(new PlannedWrite("workspace", "environment", Patch, QueueyManagement.WireOfEnvironment(environment), new[] { "tenants", tenant }));

        if (workspace.Ingress is { IsEmpty: false } ingress)
            writes.Add(new PlannedWrite("workspace", "ingress", Patch, QueueyManagement.WireOf(ingress), new[] { "tenants", tenant, "ingress" }));

        if (workspace.HasPolicy)
            writes.Add(new PlannedWrite("workspace", "policy", Patch, QueueyManagement.WireOf(workspace), new[] { "tenants", tenant, "policy" }));

        if (workspace.Delivery is { IsEmpty: false } && deliveries.Workspace is { } delivery)
            writes.Add(new PlannedWrite("workspace", "delivery", Patch, QueueyManagement.WireOf(delivery), new[] { "tenants", tenant, "delivery" }));

        return writes;
    }

    private static PlannedWrite QueuePut(string name, string tenant, bool exists)
        => new($"queues.{name}", "queue", HttpMethod.Put, new QueueApplyRequest { TenantPublicId = tenant, DisplayName = name },
            new[] { "queues" }, ChangesNothing: exists);

    /// <summary>
    /// What the probe sends: a call that changes nothing on any server, also one that ignores dryRun.
    /// First choice is PUT /queues for a declared queue that exists, which is a read everywhere and needs
    /// only what apply needs. Otherwise an empty policy patch on the workspace, which needs tenant.write.
    /// </summary>
    private static PlannedWrite? ChooseProbe(
        List<PlannedWrite> workspaceWrites, IReadOnlyList<DeploymentQueuePlan> plans,
        Dictionary<string, QueueListItem> existing, string tenant)
    {
        // Proben må ikke kunne skrive noe (review 2026-09-24). Planens første skriving ville opprettet en
        // kø eller skrevet workspacet på en server fra før dry-run, og planen er sikkerhetsnettet mot
        // nettopp det. Den tomme patchen krever tenant.write, men bare når fila ikke har en kø som finnes;
        // uten tenant.write stopper planen da uten å ha endret noe.
        if (plans.FirstOrDefault(p => existing.ContainsKey(p.Definition.Name)) is { } known)
            return QueuePut(known.Definition.Name, tenant, exists: true);

        return new PlannedWrite("workspace", EmptyPolicyCheck, Patch, new PatchTenantPolicyWireRequest(),
            new[] { "tenants", tenant, "policy" }, ChangesNothing: true);
    }

    private const string EmptyPolicyCheck = "empty policy patch";

    /// <summary>
    /// Sends the probe and proves the server plans: its answer has to say <c>dryRun: true</c>. When it
    /// does not, or the probe is refused, planning stops here and says what that means.
    /// </summary>
    private async Task<DryRunAnswer> ProbeAsync(PlannedWrite probe, CancellationToken ct)
    {
        try
        {
            return await SendAsync(probe, ct).ConfigureAwait(false);
        }
        catch (DryRunIgnoredException)
        {
            throw new QueueyException(
                "This Queuey API does not answer dry runs yet, so nothing was planned and nothing else was sent. " +
                $"Nothing was changed either: the check was the dry run of {probe.Key}, which changes nothing even when a server carries it out.",
                errorCode: "dry_run_unsupported")
            {
                SuggestedAction = "Use `queuey apply --check` to compare the file with the workspace, and `queuey apply --dry-run` to validate it locally.",
            };
        }
        catch (QueueyException ex)
        {
            // Avvist: da er det ikke bevist at serveren planlegger, så ingenting mer sendes. En avvist
            // skriving endrer ingenting, på en gammel server som på en ny.
            throw new QueueyException(
                $"Planning stopped at its first dry run, {probe.Key}, which also checks that Queuey answers dry runs: " +
                $"it was refused ({Describe(ex)}). Nothing else was sent and nothing was changed.",
                ex.StatusCode, "dry_run_probe_failed", ex)
            {
                SuggestedAction = ex.SuggestedAction ?? (probe.Aspect == EmptyPolicyCheck
                    ? "No queue in the file exists yet, so the check is an empty policy patch on the workspace, which needs tenant.write. " +
                      "Plan with a key that has it (Build), or once a declared queue exists."
                    : "Fix what the refusal names, often the key's permissions, and plan again."),
            };
        }
    }


    private static string Describe(QueueyException ex)
        => $"{string.Join(" ", new[] { ex.StatusCode?.ToString(), ex.ErrorCode }.Where(p => !string.IsNullOrWhiteSpace(p)))}: {ex.Message}".TrimStart(':', ' ');

    private async Task<DryRunAnswer> SendAsync(PlannedWrite write, CancellationToken ct) => write.Aspect == "queue"
        ? await _controlPlane.DryRunAsync<ApplyQueuePlanResponse>(write.Target, write.Aspect, write.Method, write.Body, ct, write.Segments).ConfigureAwait(false)
        : await _controlPlane.DryRunAsync<ConfigPlanResponse>(write.Target, write.Aspect, write.Method, write.Body, ct, write.Segments).ConfigureAwait(false);

    private async Task<DryRunAnswer> AnswerAsync(PlannedWrite write, Dictionary<string, DryRunAnswer> answered, CancellationToken ct)
        => answered.TryGetValue(write.Key, out DryRunAnswer? known) ? known : await SendAsync(write, ct).ConfigureAwait(false);

    private async Task<IEnumerable<DeploymentPlanStep>> PlanQueueAsync(
        DeploymentQueuePlan plan, string tenant, ResolvedDeliveries deliveries, CredentialStoring storing,
        Dictionary<string, QueueListItem> existing, Dictionary<string, DryRunAnswer> answered, bool workspaceBase,
        ListenerEnvironment? listenerEnvironment, CancellationToken ct)
    {
        string name = plan.Definition.Name;
        string target = $"queues.{name}";
        var steps = new List<DeploymentPlanStep>();

        ApplyQueuePlanResponse applied;
        try
        {
            applied = (ApplyQueuePlanResponse)await AnswerAsync(QueuePut(name, tenant, existing.ContainsKey(name)), answered, ct).ConfigureAwait(false);
        }
        catch (QueueyException ex) when (ex is not DryRunIgnoredException)
        {
            steps.Add(new DeploymentPlanStep { Target = target, Aspect = "queue", Error = ex });
            return steps;
        }

        // En lokal lytter er et sted å levere (Queuey F2.3): eventene venter på økten.
        bool forwards = plan.Kind == DeploymentDeliveryKind.LocalForward;
        bool hasDestination = forwards || IsAbsoluteUrl(plan.Delivery?.Url) || applied.HasDeliveryTarget || workspaceBase;

        if (applied.Created || applied.PublicId is not { } queueId)
        {
            // En kø som ikke finnes ennå, kan ikke tørrkjøres felt for felt: det finnes ingen kø å
            // sende PATCH-ene til. Fila er validert lokalt; serveren sjekker resten når køen finnes.
            var notes = new List<string>
            {
                plan.Mode == DeploymentQueueMode.LogOnly
                    ? "It would log events without delivering them (logOnly)."
                    : forwards
                        ? "It would deliver to a local listener: its events wait until queuey listen connects."
                        : hasDestination
                            ? "It would deliver: it has a destination."
                            : "It would log events until it has a destination: give it a delivery.url, or set workspace.delivery.baseUrl.",
            };
            if (!plan.Definition.Policy.IsEmpty || plan.Ingress is not null || plan.Delivery is not null || plan.Kind is not null)
                notes.Add("Its policy, ingress and delivery passed the checks the CLI makes; Queuey checks the rest, such as a signing template, once the queue exists.");
            if (plan.Ingress?.SignedRequest?.CredentialRef is { } awaited && !awaited.StartsWith("cred_", StringComparison.Ordinal)
                && !deliveries.KnownCredential(awaited))
                notes.Add(AwaitedCredentialNote(awaited, storing));

            steps.Add(new DeploymentPlanStep
            {
                Target = target, Aspect = "queue", Creates = true, Notes = notes,
                Desired = DesiredForNewQueue(plan, deliveries),
                Error = forwards && listenerEnvironment is { AllowsLocalForward: false } environment
                    ? LocalForwardNeedsDevWorkspace(name, tenant, environment)
                    : plan.Mode == DeploymentQueueMode.Deliver && !hasDestination ? DeliverWithoutDestination(name) : null,
            });
            return steps;
        }

        if (plan.Ingress is { } ingress)
            steps.Add(await StepAsync(new PlannedWrite(target, "ingress", Patch, QueueyManagement.WireOf(ingress), new[] { "queues", queueId, "ingress" }), answered, ct).ConfigureAwait(false));

        if (!plan.Definition.Policy.IsEmpty)
            steps.Add(await StepAsync(new PlannedWrite(target, "policy", Patch, QueueyService.ToPatch(plan.Definition.Policy), new[] { "queues", queueId, "policy" }), answered, ct).ConfigureAwait(false));

        if (deliveries.Queues.TryGetValue(name, out QueueDelivery? delivery))
            steps.Add(await StepAsync(new PlannedWrite(target, "delivery", Patch, QueueyManagement.WireOf(delivery), new[] { "queues", queueId, "delivery" }), answered, ct).ConfigureAwait(false));

        // Leveringstypen som apply setter den (Queuey F2.3), alltid som dry run: den har ingen kolonne i lista over køer.
        if (plan.Kind is { } kind)
            steps.Add(await StepAsync(new PlannedWrite(target, "kind", Patch, new LocalForwardRequest { Enabled = kind == DeploymentDeliveryKind.LocalForward },
                new[] { "queues", queueId, "local-forward" }), answered, ct).ConfigureAwait(false));

        // Modus som apply ville satt den: bare en deklarert modus endres på en kø som finnes, og
        // den gamle Paused-modusen røres aldri.
        existing.TryGetValue(name, out QueueListItem? row);
        DeploymentQueueMode? current = DeploymentQueueModes.FromBackend(row?.Mode);
        if (plan.Mode is { } declared)
        {
            if (string.Equals(row?.Mode, "Paused", StringComparison.OrdinalIgnoreCase))
                steps.Add(new DeploymentPlanStep
                {
                    Target = target, Aspect = "mode", State = "mode:Paused",
                    Notes = new[] { "It has the old Paused mode, which a deploy does not change: resume it in the Queuey console first." },
                });
            else if (declared == DeploymentQueueMode.Deliver && !(forwards || IsAbsoluteUrl(plan.Delivery?.Url) || (row?.HasDeliveryTarget ?? false) || workspaceBase))
                steps.Add(new DeploymentPlanStep { Target = target, Aspect = "mode", Error = DeliverWithoutDestination(name) });
            else if (declared != current)
                steps.Add(await StepAsync(new PlannedWrite(target, "mode", Patch, new QueueModeChangeRequest { Mode = declared.ToWire() }, new[] { "queues", queueId, "mode-change" }), answered, ct).ConfigureAwait(false));
            else
                // Ingen skriving trengs, men planen hviler på modusen køen har: endres den før apply, setter apply den tilbake.
                steps.Add(new DeploymentPlanStep { Target = target, Aspect = "mode", State = "mode:" + row?.Mode });
        }

        return steps;
    }

    /// <summary>
    /// What apply sends to a queue it creates, once it exists: the ingress, policy, delivery (with credential names
    /// resolved to ids), kind and mode the file declares. No dry run can answer for a queue that does not exist, so the
    /// plan's hash covers this instead.
    /// </summary>
    private static JsonElement? DesiredForNewQueue(DeploymentQueuePlan plan, ResolvedDeliveries deliveries)
    {
        var desired = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ingress"] = plan.Ingress is { } ingress ? QueueyManagement.WireOf(ingress) : null,
            ["policy"] = plan.Definition.Policy.IsEmpty ? null : QueueyService.ToPatch(plan.Definition.Policy),
            ["delivery"] = deliveries.Queues.TryGetValue(plan.Definition.Name, out QueueDelivery? delivery) ? QueueyManagement.WireOf(delivery) : null,
            ["kind"] = plan.Kind?.ToFileText(),
            ["mode"] = plan.Mode?.ToFileText(),
        };

        if (desired.Values.All(v => v is null))
            return null;

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(desired, QueueyJson.Options));
        return doc.RootElement.Clone();
    }

    private async Task<DeploymentPlanStep> StepAsync(PlannedWrite write, Dictionary<string, DryRunAnswer> answered, CancellationToken ct)
    {
        try
        {
            var plan = (ConfigPlanResponse)await AnswerAsync(write, answered, ct).ConfigureAwait(false);
            return new DeploymentPlanStep
            {
                Target = write.Target,
                Aspect = write.Aspect,
                Changes = (plan.Changes ?? new List<ConfigChangeResponse>())
                    .Select(c => new PlannedChange { Path = c.Path ?? string.Empty, From = PlannedChange.Raw(c.From), To = PlannedChange.Raw(c.To) })
                    .ToList(),
                Notes = plan.Notes ?? new List<string>(),
                State = plan.StateHash,
            };
        }
        catch (QueueyException ex) when (ex is not DryRunIgnoredException)
        {
            return new DeploymentPlanStep { Target = write.Target, Aspect = write.Aspect, Error = ex };
        }
    }

    private static QueueyException DeliverWithoutDestination(string name) => new(
        $"Queue '{name}' declares \"mode\": \"deliver\" but has nowhere to deliver.", errorCode: "deliver_without_destination")
    {
        SuggestedAction = "Give it a delivery.url, or set workspace.delivery.baseUrl.",
    };

    /// <summary>The environment the workspace has after the apply, for a new queue that would forward: the file's, or the stored one.</summary>
    private sealed record ListenerEnvironment(string? Value, bool FromFile)
    {
        /// <summary>Only a workspace marked dev forwards to a listener; one without an environment counts as prod.</summary>
        public bool AllowsLocalForward => string.Equals(Value?.Trim(), "dev", StringComparison.OrdinalIgnoreCase);
    }

    // Samme kode og samme vei ut som Queuey svarer med (local_forward_needs_dev_workspace), så planen sier det før apply lager
    // køen og får avslaget på leveringstypen.
    private static QueueyException LocalForwardNeedsDevWorkspace(string name, string tenant, ListenerEnvironment environment) => new QueueyConflictException(
        $"Queue '{name}' would be created with \"kind\": \"localForward\", and only a workspace marked dev forwards to a local listener "
        + $"(queuey listen): workspace {tenant} "
        + (environment.Value is { } value
            ? $"{(environment.FromFile ? "would be marked" : "is marked")} {value.Trim().ToLowerInvariant()}{(environment.FromFile ? " by this file" : "")}"
            : "has no environment, which counts as prod")
        + ". apply would create the queue, and Queuey would refuse its kind.", errorCode: "local_forward_needs_dev_workspace")
    {
        SuggestedAction = "Give the queue \"kind\": \"http\" where the workspace is not dev, or take the kind from a variable, such as "
                          + $"${{{DeploymentTemplate.QueueKindVariable(name)}}}, set to http there. If the workspace is for development, a "
                          + "person marks it dev with Set environment… on its page in the Queuey console.",
    };

    private static bool IsAbsoluteUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
           && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
