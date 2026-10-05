using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Queuey's ceilings on backoff, checked before an apply writes anything: a write may make the first
/// wait at most an hour and the longest wait at most a day.
/// </summary>
/// <remarks>
/// A ceiling binds only a write that changes the wait a queue or the workspace runs with, so a longer
/// wait that was in place before the ceilings saves unchanged. Whether a declared wait changes anything
/// depends on what Queuey holds, so this reads it, and only for a file that declares a wait above a
/// ceiling: the usual apply makes no extra call. Where the read cannot tell, the server decides when the
/// queue is written, as it always does.
/// </remarks>
internal static class BackoffCeilings
{
    private const string BaseDelay = "baseDelayMs";
    private const string MaxDelay = "maxDelayMs";

    /// <summary>
    /// Throws <see cref="QueueyConfigurationException"/> naming every declared wait that is above a
    /// ceiling and would change the wait it replaces. Nothing has been written when it does.
    /// </summary>
    public static async Task EnsureAsync(
        QueueyControlPlaneClient controlPlane,
        string tenantPublicId,
        DeploymentFile file,
        IEnumerable<DeploymentQueuePlan> plans,
        IReadOnlyDictionary<string, QueueListItem> existing,
        CancellationToken cancellationToken)
    {
        // Backenden sjekker taket mot den effektive ventetiden før og etter skrivingen (Queuey#391, 2026-10-04), med
        // workspacet slik det er når køen skrives. Apply skriver workspacet først, så «før» for en kø som arver
        // ventetiden, er det workspacet får av fila. Samme regel her: ellers ville klienten avvist en fil serveren
        // godtar, eller latt en gjennom som serveren avviser midt i kjøringen (review 2026-10-05).
        var waits = new List<Wait>();
        Collect(waits, "workspace", file.Workspace?.Backoff, Scope.Workspace, queuePublicId: null);
        foreach (DeploymentQueuePlan plan in plans)
        {
            string? queuePublicId = existing.TryGetValue(plan.Definition.Name, out QueueListItem? row) ? row.PublicId : null;
            Collect(waits, $"queues.{plan.Definition.Name}", plan.Definition.Policy.Backoff,
                queuePublicId is null ? Scope.NewQueue : Scope.ExistingQueue, queuePublicId);
        }

        if (waits.Count == 0)
            return;

        TenantConfigResponse? workspace = null;
        bool workspaceRead = false;
        var queues = new Dictionary<string, QueueConfigResponse?>(StringComparer.Ordinal);
        var refusals = new List<string>();

        foreach (Wait wait in waits)
        {
            int? declaredForWorkspace = Field(file.Workspace?.Backoff, wait.Field);
            Before before;

            if (wait.Scope == Scope.ExistingQueue)
            {
                if (!queues.TryGetValue(wait.QueuePublicId!, out QueueConfigResponse? queue))
                {
                    queue = await ReadAsync(() => controlPlane.GetQueueConfigAsync(wait.QueuePublicId!, cancellationToken)).ConfigureAwait(false);
                    queues[wait.QueuePublicId!] = queue;
                }

                before = ExistingQueue(queue, declaredForWorkspace, wait.Field);
            }
            else if (wait.Scope == Scope.NewQueue && declaredForWorkspace is { } fromFile)
            {
                // En ny kø har ingen ventetid selv, så den får workspacets, og fila skriver workspacet først.
                before = new Before(fromFile, Inherited: true);
            }
            else
            {
                if (!workspaceRead)
                {
                    workspace = await ReadAsync(() => controlPlane.GetTenantConfigAsync(tenantPublicId, cancellationToken)).ConfigureAwait(false);
                    workspaceRead = true;
                }

                before = new Before(Field(workspace?.Policy?.Backoff, wait.Field), Inherited: wait.Scope == Scope.NewQueue);
            }

            // Ukjent (et API som ikke sender backoff, en nøkkel som ikke kan lese det, eller en kø vi ikke kan si om
            // arver ventetiden): serveren avgjør.
            if (before.Value is not { } now || now == wait.Declared)
                continue;

            refusals.Add($"{wait.Path}.backoff.{wait.Field} is {wait.Declared}, above the {wait.Ceiling} ({Limit(wait.Field)}) "
                         + $"{(wait.Field == BaseDelay ? "a first wait" : "the longest wait")} may be, and would change it from "
                         + (before.Inherited ? $"the {now} the queue inherits from the workspace" : $"{now}"));
        }

        if (refusals.Count > 0)
        {
            throw new QueueyConfigurationException(
                $"Queuey would refuse {(refusals.Count == 1 ? "this wait" : "these waits")}: {string.Join("; ", refusals)}. "
                + "A longer wait stays only where it is already in place. Nothing was changed.")
            {
                SuggestedAction = $"Declare at most {RetryBackoff.BaseDelayCeilingMs} for backoff.{BaseDelay} and "
                                  + $"{RetryBackoff.MaxDelayCeilingMs} for backoff.{MaxDelay}, or leave the field out.",
            };
        }
    }

    /// <summary>
    /// The wait an existing queue has when Queuey checks its write, from its config read: the workspace's
    /// after this apply when the queue inherits it, its own when it owns it. No value when the read cannot
    /// tell which, and the answer depends on it.
    /// </summary>
    private static Before ExistingQueue(QueueConfigResponse? config, int? declaredForWorkspace, string field)
    {
        int? queue = Field(config?.Policy?.Backoff, field);
        int? workspace = Field(config?.TenantBaseline?.Policy?.Backoff, field);

        // Køen eier ingen policy, så den arver ventetiden og har workspacets etter at fila har skrevet det.
        if (config?.Inherited?.Behavior == true)
            return new Before(declaredForWorkspace ?? workspace, Inherited: true);

        if (queue is null)
            return default;

        // Inherited.Behavior sier bare om køen eier noe av policyen, ikke hva. En verdi ulik workspacets er køens egen,
        // for en arvet verdi er workspacets.
        if (workspace is not null && queue != workspace)
            return new Before(queue, Inherited: false);

        // Lik workspacets kan være arvet eller eid. Endrer ikke fila workspacets ventetid, har køen den samme etterpå
        // uansett.
        if (declaredForWorkspace is null || declaredForWorkspace == workspace)
            return new Before(queue, Inherited: false);

        // Fila endrer workspacets ventetid, og køen kan ha sin egen eller arve den: serveren avgjør.
        return default;
    }

    private static void Collect(List<Wait> waits, string path, RetryBackoff? backoff, Scope scope, string? queuePublicId)
    {
        if (backoff?.BaseDelayMs is { } baseDelay && baseDelay > RetryBackoff.BaseDelayCeilingMs)
            waits.Add(new Wait(path, BaseDelay, baseDelay, RetryBackoff.BaseDelayCeilingMs, scope, queuePublicId));
        if (backoff?.MaxDelayMs is { } maxDelay && maxDelay > RetryBackoff.MaxDelayCeilingMs)
            waits.Add(new Wait(path, MaxDelay, maxDelay, RetryBackoff.MaxDelayCeilingMs, scope, queuePublicId));
    }

    /// <summary>
    /// A config read, or null when it cannot be had: a key that may not read config, or a queue gone
    /// since it was listed. The server then decides, as it always does.
    /// </summary>
    private static async Task<T?> ReadAsync<T>(Func<Task<T>> read) where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (QueueyException ex) when (ex is QueueyForbiddenException or QueueyNotFoundException)
        {
            return null;
        }
    }

    private static int? Field(RetryBackoffWire? backoff, string field)
        => field == BaseDelay ? backoff?.BaseDelayMs : backoff?.MaxDelayMs;

    private static int? Field(RetryBackoff? backoff, string field)
        => field == BaseDelay ? backoff?.BaseDelayMs : backoff?.MaxDelayMs;

    private static string Limit(string field) => field == BaseDelay ? "one hour" : "24 hours";

    private enum Scope
    {
        Workspace,
        NewQueue,
        ExistingQueue,
    }

    /// <summary>The wait a declared one would replace, and whether the queue has it from the workspace.</summary>
    private readonly record struct Before(int? Value, bool Inherited);

    private sealed record Wait(string Path, string Field, int Declared, int Ceiling, Scope Scope, string? QueuePublicId);
}
