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

    /// <summary>One entry per write apply would send, in the order it would send them.</summary>
    public IReadOnlyList<DeploymentPlanStep> Steps { get; init; } = Array.Empty<DeploymentPlanStep>();

    /// <summary>True when Queuey would accept every write.</summary>
    public bool WouldSucceed => Steps.All(s => s.Error is null);

    /// <summary>How many values would change, counting a queue that would be created as one.</summary>
    public int ChangeCount => Steps.Sum(s => s.Changes.Count + (s.Creates ? 1 : 0));
}

/// <summary>One write in a <see cref="DeploymentPlan"/>.</summary>
public sealed class DeploymentPlanStep
{
    /// <summary><c>workspace</c>, or <c>queues.&lt;name&gt;</c>.</summary>
    public string Target { get; init; } = default!;

    /// <summary>What the write touches: <c>queue</c>, <c>ingress</c>, <c>policy</c>, <c>delivery</c> or <c>mode</c>.</summary>
    public string Aspect { get; init; } = default!;

    /// <summary>True when the write would create the queue.</summary>
    public bool Creates { get; init; }

    /// <summary>The values that would change. Empty when the write would change nothing.</summary>
    public IReadOnlyList<PlannedChange> Changes { get; init; } = Array.Empty<PlannedChange>();

    /// <summary>What else the write reaches, or what could not be checked yet.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>The refusal the write would get — code, message and what to do — or null.</summary>
    public QueueyException? Error { get; init; }
}

/// <summary>One value that would change: a path into the config read-back, and the JSON on each side.</summary>
public sealed class PlannedChange
{
    /// <summary>Where, e.g. <c>policy.maxAttempts</c>.</summary>
    public string Path { get; init; } = default!;

    /// <summary>The value now, as JSON text (a string without quotes), or null.</summary>
    public string? From { get; init; }

    /// <summary>The value after, as JSON text (a string without quotes), or null.</summary>
    public string? To { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Path}: {From ?? "(none)"} → {To ?? "(none)"}";
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

    public DeploymentPlanner(QueueyControlPlaneClient controlPlane, IQueueyManagement management)
    {
        _controlPlane = controlPlane;
        _management = management;
    }

    /// <summary>One write the plan sends: where it goes, what it carries, and whether it changes anything as a real write.</summary>
    private sealed record PlannedWrite(string Target, string Aspect, HttpMethod Method, object Body, string[] Segments, bool ChangesNothing = false)
    {
        public string Key => $"{Target} · {Aspect}";
    }

    public async Task<DeploymentPlan> PlanAsync(
        DeploymentFile file, IReadOnlyList<DeploymentQueuePlan> plans, string tenant, CancellationToken ct)
    {
        // Lesingene først, før noe sendes: køene slik de er, og hvert credential-navn fila bruker. Et navn
        // som mangler, feiler planen her, før første skriving, slik det feiler apply (review 2026-09-24).
        // Før ble det et avslag på ett steg, oppdaget midt i planen.
        var existing = new Dictionary<string, QueueListItem>(StringComparer.Ordinal);
        foreach (QueueListItem row in await _management.ListQueuesAsync(tenant, ct).ConfigureAwait(false))
        {
            if (row.DisplayName is { } name)
                existing[name] = row;
        }

        ResolvedDeliveries deliveries = await new CredentialResolver(_management, tenant)
            .ResolveAllAsync(file.Workspace?.Delivery, plans, ct).ConfigureAwait(false);

        List<PlannedWrite> workspaceWrites = WorkspaceWrites(file.Workspace, deliveries, tenant);

        // Beviset på at serveren planlegger, før noe annet sendes. Svaret gjelder også som svaret på
        // den skrivingen, så den sendes ikke to ganger.
        var answered = new Dictionary<string, DryRunAnswer>(StringComparer.Ordinal);
        if (ChooseProbe(workspaceWrites, plans, existing, tenant) is { } probe)
            answered[probe.Key] = await ProbeAsync(probe, ct).ConfigureAwait(false);

        var steps = new List<DeploymentPlanStep>();
        foreach (PlannedWrite write in workspaceWrites)
            steps.Add(await StepAsync(write, answered, ct).ConfigureAwait(false));

        foreach (DeploymentQueuePlan plan in plans)
            steps.AddRange(await PlanQueueAsync(plan, tenant, deliveries, existing, answered, ct).ConfigureAwait(false));

        return new DeploymentPlan { Tenant = tenant, Steps = steps };
    }

    /// <summary>The workspace's writes, in the order apply sends them: ingress, policy, delivery.</summary>
    private static List<PlannedWrite> WorkspaceWrites(DeploymentWorkspace? workspace, ResolvedDeliveries deliveries, string tenant)
    {
        var writes = new List<PlannedWrite>();
        if (workspace is null)
            return writes;

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
        DeploymentQueuePlan plan, string tenant, ResolvedDeliveries deliveries,
        Dictionary<string, QueueListItem> existing, Dictionary<string, DryRunAnswer> answered, CancellationToken ct)
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

        bool hasDestination = IsAbsoluteUrl(plan.Delivery?.Url) || applied.HasDeliveryTarget;

        if (applied.Created || applied.PublicId is not { } queueId)
        {
            // En kø som ikke finnes ennå, kan ikke tørrkjøres felt for felt: det finnes ingen kø å
            // sende PATCH-ene til. Fila er validert lokalt; serveren sjekker resten når køen finnes.
            var notes = new List<string>
            {
                plan.Mode == DeploymentQueueMode.LogOnly
                    ? "It would log events without delivering them (logOnly)."
                    : hasDestination
                        ? "It would deliver: it has a destination."
                        : "It would log events until it has a destination: give it a delivery.url, or set workspace.delivery.baseUrl.",
            };
            if (!plan.Definition.Policy.IsEmpty || plan.Ingress is not null || plan.Delivery is not null)
                notes.Add("Its policy, ingress and delivery passed local validation; Queuey checks them against the queue once it exists.");

            DeploymentPlanStep create = new() { Target = target, Aspect = "queue", Creates = true, Notes = notes };
            if (plan.Mode == DeploymentQueueMode.Deliver && !hasDestination)
                create = new DeploymentPlanStep
                {
                    Target = target, Aspect = "queue", Creates = true, Notes = notes,
                    Error = DeliverWithoutDestination(name),
                };
            steps.Add(create);
            return steps;
        }

        if (plan.Ingress is { } ingress)
            steps.Add(await StepAsync(new PlannedWrite(target, "ingress", Patch, QueueyManagement.WireOf(ingress), new[] { "queues", queueId, "ingress" }), answered, ct).ConfigureAwait(false));

        if (!plan.Definition.Policy.IsEmpty)
            steps.Add(await StepAsync(new PlannedWrite(target, "policy", Patch, QueueyService.ToPatch(plan.Definition.Policy), new[] { "queues", queueId, "policy" }), answered, ct).ConfigureAwait(false));

        if (deliveries.Queues.TryGetValue(name, out QueueDelivery? delivery))
            steps.Add(await StepAsync(new PlannedWrite(target, "delivery", Patch, QueueyManagement.WireOf(delivery), new[] { "queues", queueId, "delivery" }), answered, ct).ConfigureAwait(false));

        // Modus som apply ville satt den: bare en deklarert modus endres på en kø som finnes, og
        // den gamle Paused-modusen røres aldri.
        existing.TryGetValue(name, out QueueListItem? row);
        DeploymentQueueMode? current = DeploymentQueueModes.FromBackend(row?.Mode);
        if (plan.Mode is { } declared)
        {
            if (string.Equals(row?.Mode, "Paused", StringComparison.OrdinalIgnoreCase))
                steps.Add(new DeploymentPlanStep
                {
                    Target = target, Aspect = "mode",
                    Notes = new[] { "It has the old Paused mode, which a deploy does not change: resume it in the Queuey console first." },
                });
            else if (declared == DeploymentQueueMode.Deliver && !(IsAbsoluteUrl(plan.Delivery?.Url) || (row?.HasDeliveryTarget ?? false)))
                steps.Add(new DeploymentPlanStep { Target = target, Aspect = "mode", Error = DeliverWithoutDestination(name) });
            else if (declared != current)
                steps.Add(await StepAsync(new PlannedWrite(target, "mode", Patch, new QueueModeChangeRequest { Mode = declared.ToWire() }, new[] { "queues", queueId, "mode-change" }), answered, ct).ConfigureAwait(false));
        }

        return steps;
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
                    .Select(c => new PlannedChange { Path = c.Path ?? string.Empty, From = Text(c.From), To = Text(c.To) })
                    .ToList(),
                Notes = plan.Notes ?? new List<string>(),
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

    private static string? Text(JsonElement? value) => value switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { } v => v.GetRawText(),
    };

    private static bool IsAbsoluteUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
           && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
