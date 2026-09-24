using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey apply</c> — converges Queuey from a declarative deployment file. The deploy-pipeline
/// verb: reviewable in a pull request, idempotent, and non-zero on anything short of convergence.
/// </summary>
internal static class ApplyCommand
{
    // --plan ble et eget verb (2026-09-24): et verb en eldre CLI ikke kjenner, feiler i alle versjoner,
    // mens `apply --plan` i en CLI fra før flagget var en ekte apply. Ordet får et hint i stedet.
    internal static readonly CommandOptions Options = new(
        "apply",
        flags: new[] { "dry-run", "check", "continue-on-error", "json" },
        values: new[] { "file" },
        hints: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "`apply --plan` is now `queuey plan`: it asks Queuey what apply would change, and writes nothing.",
        });

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        if (!TryReadDeploymentFile(map, out string path, out DeploymentFile file, out failure)) return failure;
        bool dryRun = map.Has("dry-run");

        if (dryRun)
        {
            // Samme regel for tenant som apply, så en dry-run feiler der applyen ville feilet.
            DeploymentTenant.EnsureNoConflict(map, CliHost.Env, file.ResolveTenant(), path);

            // Network-free: resolving validates names and policy, which is the failure worth catching
            // before a deploy window rather than during one.
            // Expanding first means an unset ${VAR} fails here, in the dry run, rather than during
            // the deploy it was meant to protect.
            IReadOnlyList<DeploymentQueuePlan> plans = file.Expand().Resolve();
            if (map.Has("json"))
                Console.WriteLine(JsonSerializer.Serialize(plans.Select(ToJsonPlan), CliHost.JsonOut));
            else
                WritePlan(path, file, plans);
            return ExitCodes.Success;

        }

        // Workspacet fila navngir, ellers det konfigurerte — og feil når --tenant eller QUEUEY_TENANT sier
        // noe annet enn fila. Samme regel som verify, så de treffer samme workspace.
        ResolvedConfig config = CliHost.ResolveForDeployment(map, file.ResolveTenant(), path);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        if (map.Has("check"))
            return await CheckAsync(service, file, path, map);

        QueueSyncResult result;
        try
        {
            result = await service.ApplyDeploymentAsync(file, new SyncOptions { ContinueOnError = map.Has("continue-on-error") });
        }
        catch (QueueySyncException ex)
        {
            result = ex.Queues!;
        }

        string? tenant = config.TenantPublicId;

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(ToJsonResult(result, path, config, tenant), CliHost.JsonOut));
        else
            WriteHuman(result, path, config, tenant);

        return result.AllSucceeded ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    /// <summary>
    /// The deployment file named by <c>--file</c>, or <c>queuey.deploy.json</c> here. A missing file is
    /// a usage error, written the way the caller asked for errors.
    /// </summary>
    internal static bool TryReadDeploymentFile(ArgMap map, out string path, out DeploymentFile file, out int failure)
    {
        path = map.Get("file") ?? DeploymentFile.DefaultFileName;
        file = null!;
        failure = ExitCodes.Success;

        if (!File.Exists(path))
        {
            failure = CliErrors.Usage(map, "missing_file", $"No deployment file at '{path}'.", "Create one, or pass --file <path>.");
            return false;
        }

        file = DeploymentFile.Parse(File.ReadAllText(path));
        return true;
    }

    /// <summary>
    /// The CI gate: report what applying would change, write nothing, and exit non-zero on drift so a
    /// divergence is noticed at review time rather than during an incident.
    /// </summary>
    private static async Task<int> CheckAsync(IQueueyService service, DeploymentFile file, string path, ArgMap map)
    {
        IReadOnlyList<DriftItem> drift = await service.CheckDeploymentAsync(file);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = path,
                inSync = drift.Count == 0,
                drift = drift.Select(d => new { d.Path, d.Declared, d.Actual }),
            }, CliHost.JsonOut));
        }
        else if (drift.Count == 0)
        {
            Console.WriteLine($"{path} matches the workspace — applying it would change nothing.");
        }
        else
        {
            Console.WriteLine($"{path} has drifted from the workspace ({drift.Count} difference(s)):");
            foreach (DriftItem d in drift)
                Console.WriteLine($"  ~ {d}");
            Console.WriteLine("Run `queuey apply` to converge, or `queuey pull` if the workspace is right.");
        }

        return drift.Count == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static void WritePlan(string path, DeploymentFile file, IReadOnlyList<DeploymentQueuePlan> plans)
    {
        Console.WriteLine($"Queuey apply (dry-run) — {path}");

        if (file.Workspace is { } w)
        {
            var parts = new List<string>();
            if (w.Ordering is not null) parts.Add($"ordering={w.Ordering}");
            if (w.RetentionDays is { } days) parts.Add($"retentionDays={days}");
            parts.AddRange(Retry(w.MaxAttempts, w.DlqAfterAttempts, w.Backoff));
            if (w.Ingress?.AuthMode is { } auth) parts.Add($"ingressAuth={auth}");
            if (w.Ingress?.EventType is { } et) parts.Add($"eventType={et.From}:{et.Name}");
            if (w.Ingress?.GroupKey is { } gk) parts.Add($"groupKey={gk.From}:{gk.Name}");
            if (w.Delivery?.BaseUrl is { } url) parts.Add($"baseUrl={url}");

            if (parts.Count > 0)
                Console.WriteLine($"  workspace\t{string.Join(" ", parts)}");
        }

        foreach (DeploymentQueuePlan p in plans)
        {
            string dest = p.Delivery is null
                ? "inherits the workspace"
                : p.Delivery.Inherit ? "back to inheriting" : $"url={p.Delivery.Url}";

            // Modus står først fordi den avgjør om noe leveres i det hele tatt.
            var parts = new List<string>
            {
                p.Mode is { } mode ? $"mode={mode.ToFileText()}" : "mode=(deliver when it has a destination, if new)",
                dest,
            };
            QueuePolicy policy = p.Definition.Policy;
            if (policy.Ordering is not null) parts.Add($"ordering={policy.Ordering}");
            parts.AddRange(Retry(policy.MaxAttempts, policy.DlqAfterAttempts, policy.Backoff));
            if (policy.Filter is { } filter) parts.Add($"filter=({filter})");

            Console.WriteLine($"  • {p.Definition.Name}\t{string.Join(" ", parts)}");
        }

        Console.WriteLine($"{plans.Count} queue(s) declared. Nothing was sent.");
    }

    private static IEnumerable<string> Retry(int? maxAttempts, int? dlqAfterAttempts, RetryBackoff? backoff)
    {
        if (maxAttempts is { } max) yield return $"maxAttempts={max}";
        if (dlqAfterAttempts is { } dlq) yield return $"dlqAfterAttempts={dlq}";
        if (backoff is { } b)
        {
            if (b.BaseDelayMs is { } baseMs) yield return $"backoff.baseDelayMs={baseMs}";
            if (b.MaxDelayMs is { } maxMs) yield return $"backoff.maxDelayMs={maxMs}";
            if (b.Jitter is { } jitter) yield return $"backoff.jitter={jitter}";
        }
    }

    private static void WriteHuman(QueueSyncResult result, string path, ResolvedConfig config, string? tenant)
    {
        Console.WriteLine($"Queuey apply — {path} → {config.ResolvedApiBase()}  (tenant {tenant ?? "?"})");

        foreach (QueueApplyResult r in result.Applied)
        {
            if (r.Succeeded)
                Console.WriteLine($"  ✓ {r.Name}\t{r.PublicId}\t{(r.Created ? "created" : "exists")}{(r.PolicyApplied ? ", policy" : "")}"
                                  + (r.Mode is { } mode ? $", {mode}" : ""));
            else if (r.Created)
                // Opprettet før feilen: køen finnes, og modusen den fikk, er det som avgjør om den leverer.
                Console.WriteLine($"  ✗ {r.Name}\t{r.PublicId}\tcreated, {r.Mode ?? "mode unknown"} — {FormatError(r.Error)}");
            else
                Console.WriteLine($"  ✗ {r.Name}\t{FormatError(r.Error)}");
        }

        foreach (string skipped in result.NotAttempted)
            Console.WriteLine($"  – {skipped}\tnot attempted (stopped at an earlier failure)");

        foreach (string warning in result.Warnings)
            Console.WriteLine($"  ! {warning}");

        Console.WriteLine($"{result.Succeeded} applied ({result.Created} created), {result.Failed} failed, "
                          + $"{result.NotAttempted.Count} not attempted"
                          + (result.NotAttempted.Count > 0 ? " — re-run to converge (applying is idempotent)" : ""));
    }

    private static string FormatError(QueueyException? e)
        => e is null ? "failed" : $"{(e.StatusCode?.ToString() ?? "error")} {e.ErrorCode} {e.Message}".Replace("  ", " ").Trim();

    private static object ToJsonPlan(DeploymentQueuePlan p) => new
    {
        p.Definition.Name,
        // null: leave it, and a queue this file creates delivers when it has a destination.
        mode = p.Mode?.ToFileText(),
        policy = new
        {
            p.Definition.Policy.Ordering,
            p.Definition.Policy.DlqEnabled,
            p.Definition.Policy.RetentionDays,
            p.Definition.Policy.Idempotent,
            p.Definition.Policy.MaxAttempts,
            p.Definition.Policy.DlqAfterAttempts,
            backoff = p.Definition.Policy.Backoff is { } b ? new { b.BaseDelayMs, b.MaxDelayMs, b.Jitter } : null,
            filter = p.Definition.Policy.Filter is { } f
                ? new { match = f.Match ?? "all", conditions = f.Conditions.Select(c => new { c.Field, c.Op, c.Value }) }
                : null,
        },
        delivery = p.Delivery is null ? null : new { p.Delivery.Url, p.Delivery.Inherit, p.Delivery.AuthMode, p.Delivery.CredentialRef },
        ingress = p.Ingress is null ? null : new { p.Ingress.AuthMode, eventType = p.Ingress.EventType?.Name, groupKey = p.Ingress.GroupKey?.Name },
    };

    private static object ToJsonResult(QueueSyncResult result, string path, ResolvedConfig config, string? tenant) => new
    {
        file = path,
        apiHost = config.ResolvedApiBase().ToString(),
        tenant,
        total = result.Total,
        succeeded = result.Succeeded,
        created = result.Created,
        failed = result.Failed,
        notAttempted = result.NotAttempted,
        warnings = result.Warnings,
        queues = result.Applied.Select(r => new { r.Name, r.Succeeded, r.PublicId, r.Created, r.PolicyApplied, r.Mode, error = r.Error?.Message, errorCode = r.Error?.ErrorCode }),
    };
}
