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
/// Queuey answered a dry run as a write: it did not say <c>dryRun: true</c>. Planning stops at once,
/// because every further call might write too.
/// </summary>
public sealed class DryRunIgnoredException : QueueyException
{
    internal DryRunIgnoredException()
        : base("Queuey answered a dry run as a write, so planning stopped before sending anything else.", errorCode: "dry_run_ignored")
    {
        SuggestedAction = "Compare the workspace with the file using `queuey apply --check` to see what changed.";
    }
}

// ── wire ──────────────────────────────────────────────────────────────────────

internal sealed class ConfigPlanResponse
{
    public bool DryRun { get; set; }
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

internal sealed class ApplyQueuePlanResponse
{
    public bool DryRun { get; set; }
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

    public async Task<DeploymentPlan> PlanAsync(
        DeploymentFile file, IReadOnlyList<DeploymentQueuePlan> plans, string tenant, CancellationToken ct)
    {
        await EnsureServerPlansAsync(tenant, ct).ConfigureAwait(false);

        var steps = new List<DeploymentPlanStep>();
        var credentials = new CredentialResolver(_management, tenant);

        if (file.Workspace is { } workspace)
        {
            if (workspace.Ingress is { } ingress && !ingress.IsEmpty)
                steps.Add(await StepAsync("workspace", "ingress", QueueyManagement.WireOf(ingress), ct, "tenants", tenant, "ingress").ConfigureAwait(false));

            if (workspace.HasPolicy)
                steps.Add(await StepAsync("workspace", "policy", QueueyManagement.WireOf(workspace), ct, "tenants", tenant, "policy").ConfigureAwait(false));

            if (workspace.Delivery is { } delivery && !delivery.IsEmpty)
                steps.Add(await GuardAsync("workspace", "delivery", async () =>
                {
                    WorkspaceDelivery resolved = await credentials.ResolveAsync(delivery, ct).ConfigureAwait(false);
                    return await StepAsync("workspace", "delivery", QueueyManagement.WireOf(resolved), ct, "tenants", tenant, "delivery").ConfigureAwait(false);
                }).ConfigureAwait(false));
        }

        var existing = new Dictionary<string, QueueListItem>(StringComparer.Ordinal);
        foreach (QueueListItem row in await _management.ListQueuesAsync(tenant, ct).ConfigureAwait(false))
        {
            if (row.DisplayName is { } name)
                existing[name] = row;
        }

        foreach (DeploymentQueuePlan plan in plans)
            steps.AddRange(await PlanQueueAsync(plan, tenant, credentials, existing, ct).ConfigureAwait(false));

        return new DeploymentPlan { Tenant = tenant, Steps = steps };
    }

    private async Task<IEnumerable<DeploymentPlanStep>> PlanQueueAsync(
        DeploymentQueuePlan plan, string tenant, CredentialResolver credentials,
        Dictionary<string, QueueListItem> existing, CancellationToken ct)
    {
        string name = plan.Definition.Name;
        string target = $"queues.{name}";
        var steps = new List<DeploymentPlanStep>();

        ApplyQueuePlanResponse applied;
        try
        {
            applied = await _controlPlane.DryRunAsync<ApplyQueuePlanResponse>(
                HttpMethod.Put, new QueueApplyRequest { TenantPublicId = tenant, DisplayName = name }, ct, "queues").ConfigureAwait(false);
            EnsurePlanned(applied.DryRun);
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
            steps.Add(await StepAsync(target, "ingress", QueueyManagement.WireOf(ingress), ct, "queues", queueId, "ingress").ConfigureAwait(false));

        if (!plan.Definition.Policy.IsEmpty)
            steps.Add(await StepAsync(target, "policy", QueueyService.ToPatch(plan.Definition.Policy), ct, "queues", queueId, "policy").ConfigureAwait(false));

        if (plan.Delivery is { } delivery)
            steps.Add(await GuardAsync(target, "delivery", async () =>
            {
                QueueDelivery resolved = await credentials.ResolveAsync(delivery, ct).ConfigureAwait(false);
                return await StepAsync(target, "delivery", QueueyManagement.WireOf(resolved), ct, "queues", queueId, "delivery").ConfigureAwait(false);
            }).ConfigureAwait(false));

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
                steps.Add(await StepAsync(target, "mode", new QueueModeChangeRequest { Mode = declared.ToWire() }, ct, "queues", queueId, "mode-change").ConfigureAwait(false));
        }

        return steps;
    }

    /// <summary>
    /// Proves the server answers dry runs before anything else is sent. A server from before them
    /// ignores <c>?dryRun=true</c> and performs the write, so the probe is one that changes nothing
    /// either way: an empty workspace policy patch.
    /// </summary>
    private async Task EnsureServerPlansAsync(string tenant, CancellationToken ct)
    {
        ConfigPlanResponse? probe;
        try
        {
            probe = await _controlPlane.DryRunAsync<ConfigPlanResponse>(
                Patch, new PatchTenantPolicyWireRequest(), ct, "tenants", tenant, "policy").ConfigureAwait(false);
        }
        catch (JsonException)
        {
            probe = null;   // 204 uten kropp: serveren utførte (den tomme) patchen i stedet for å planlegge
        }

        if (probe is null || !probe.DryRun)
            throw new QueueyException(
                "This Queuey API does not answer dry runs yet, so nothing was planned. Nothing was changed either: " +
                "the check was an empty policy patch, which changes nothing.",
                errorCode: "dry_run_unsupported")
            {
                SuggestedAction = "Use `queuey apply --check` to compare the file with the workspace, and `queuey apply --dry-run` to validate it locally.",
            };
    }

    private async Task<DeploymentPlanStep> StepAsync(
        string target, string aspect, object request, CancellationToken ct, params string[] segments)
    {
        try
        {
            ConfigPlanResponse plan = await _controlPlane.DryRunAsync<ConfigPlanResponse>(Patch, request, ct, segments).ConfigureAwait(false);
            EnsurePlanned(plan.DryRun);
            return new DeploymentPlanStep
            {
                Target = target,
                Aspect = aspect,
                Changes = (plan.Changes ?? new List<ConfigChangeResponse>())
                    .Select(c => new PlannedChange { Path = c.Path ?? string.Empty, From = Text(c.From), To = Text(c.To) })
                    .ToList(),
                Notes = plan.Notes ?? new List<string>(),
            };
        }
        catch (QueueyException ex) when (ex is not DryRunIgnoredException)
        {
            return new DeploymentPlanStep { Target = target, Aspect = aspect, Error = ex };
        }
    }

    private static async Task<DeploymentPlanStep> GuardAsync(string target, string aspect, Func<Task<DeploymentPlanStep>> step)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (QueueyException ex) when (ex is not DryRunIgnoredException)
        {
            // Et credential-navn som ikke finnes, er et avslag på lik linje med serverens.
            return new DeploymentPlanStep { Target = target, Aspect = aspect, Error = ex };
        }
    }

    // Svaret skal si dryRun: true. Gjør det ikke det, har serveren skrevet — stopp før flere kall.
    private static void EnsurePlanned(bool dryRun)
    {
        if (!dryRun)
            throw new DryRunIgnoredException();
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
