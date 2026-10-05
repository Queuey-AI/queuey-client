using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Queuey's ceilings on backoff, checked before an apply writes anything: a write may make the first
/// wait at most an hour and the longest wait at most a day.
/// </summary>
/// <remarks>
/// A ceiling binds only a write that changes the wait a queue or the workspace runs with. A longer wait
/// that was in place before the ceilings saves unchanged, and a pulled file carries it, so applying that
/// file must not fail here. Whether a declared wait changes anything depends on what the workspace holds,
/// so this reads it, and only for a file that declares a wait above a ceiling: the usual apply makes no
/// extra call.
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
        // Backenden sjekker taket mot den effektive ventetiden før og etter skrivingen (Queuey#391, 2026-10-04),
        // så en uendret apply av en arvet ventetid fra før taket går gjennom. Samme regel her, ellers ville
        // klienten avvist en fil serveren godtar. «Før» er det køen har nå; for en ny kø det workspacet vil ha,
        // som er fila sin verdi når den deklarerer en, fordi workspacet skrives først.
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

        RetryBackoffWire? workspaceNow = null;
        bool workspaceRead = false;
        var queueNow = new Dictionary<string, RetryBackoffWire?>(StringComparer.Ordinal);
        var refusals = new List<string>();

        foreach (Wait wait in waits)
        {
            int? now;
            if (wait.Scope == Scope.ExistingQueue)
            {
                if (!queueNow.TryGetValue(wait.QueuePublicId!, out RetryBackoffWire? queue))
                {
                    queue = await ReadAsync(() => controlPlane.GetQueueConfigAsync(wait.QueuePublicId!, cancellationToken), c => c.Policy?.Backoff)
                        .ConfigureAwait(false);
                    queueNow[wait.QueuePublicId!] = queue;
                }

                now = Field(queue, wait.Field);
            }
            else
            {
                now = wait.Scope == Scope.NewQueue ? Field(file.Workspace?.Backoff, wait.Field) : null;
                if (now is null)
                {
                    if (!workspaceRead)
                    {
                        workspaceNow = await ReadAsync(() => controlPlane.GetTenantConfigAsync(tenantPublicId, cancellationToken), c => c.Policy?.Backoff)
                            .ConfigureAwait(false);
                        workspaceRead = true;
                    }

                    now = Field(workspaceNow, wait.Field);
                }
            }

            // Ukjent (et API som ikke sender backoff, eller en nøkkel som ikke kan lese det): serveren avgjør.
            if (now is null || now == wait.Declared)
                continue;

            refusals.Add($"{wait.Path}.backoff.{wait.Field} is {wait.Declared}, above the {wait.Ceiling} ({Limit(wait.Field)}) "
                         + $"{(wait.Field == BaseDelay ? "a first wait" : "the longest wait")} may be, and would change it from {now}");
        }

        if (refusals.Count > 0)
        {
            throw new QueueyConfigurationException(
                $"Queuey would refuse {(refusals.Count == 1 ? "this wait" : "these waits")}: {string.Join("; ", refusals)}. "
                + "A longer wait stays only where it is already in place. Nothing was changed.")
            {
                SuggestedAction = $"Declare at most {RetryBackoff.MaxBaseDelayMs} for backoff.{BaseDelay} and "
                                  + $"{RetryBackoff.MaxMaxDelayMs} for backoff.{MaxDelay}, or leave the field out.",
            };
        }
    }

    private static void Collect(List<Wait> waits, string path, RetryBackoff? backoff, Scope scope, string? queuePublicId)
    {
        if (backoff?.BaseDelayMs is { } baseDelay && baseDelay > RetryBackoff.MaxBaseDelayMs)
            waits.Add(new Wait(path, BaseDelay, baseDelay, RetryBackoff.MaxBaseDelayMs, scope, queuePublicId));
        if (backoff?.MaxDelayMs is { } maxDelay && maxDelay > RetryBackoff.MaxMaxDelayMs)
            waits.Add(new Wait(path, MaxDelay, maxDelay, RetryBackoff.MaxMaxDelayMs, scope, queuePublicId));
    }

    /// <summary>
    /// The backoff a config read reports, or null when it cannot be read: a key that may not read
    /// config, or a queue gone since it was listed. The server then decides, as it always does.
    /// </summary>
    private static async Task<RetryBackoffWire?> ReadAsync<T>(Func<Task<T>> read, Func<T, RetryBackoffWire?> backoff)
    {
        try
        {
            return backoff(await read().ConfigureAwait(false));
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

    private sealed record Wait(string Path, string Field, int Declared, int Ceiling, Scope Scope, string? QueuePublicId);
}
