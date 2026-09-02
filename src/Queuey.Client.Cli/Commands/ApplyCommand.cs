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
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "dry-run", "continue-on-error", "json", "help", "h",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string path = map.Get("file") ?? DeploymentFile.DefaultFileName;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No deployment file at '{path}'. Create one, or pass --file <path>.");
            return ExitCodes.Usage;
        }

        DeploymentFile file = DeploymentFile.Parse(File.ReadAllText(path));
        bool dryRun = map.Has("dry-run");

        if (dryRun)
        {
            // Network-free: resolving validates names and policy, which is the failure worth catching
            // before a deploy window rather than during one.
            IReadOnlyList<DeploymentQueuePlan> plans = file.Resolve();
            if (map.Has("json"))
                Console.WriteLine(JsonSerializer.Serialize(plans.Select(ToJsonPlan), CliHost.JsonOut));
            else
                WritePlan(path, file, plans);
            return ExitCodes.Success;
        }

        ResolvedConfig config = CliHost.Resolve(map);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        QueueSyncResult result;
        try
        {
            result = await service.ApplyDeploymentAsync(file, new SyncOptions { ContinueOnError = map.Has("continue-on-error") });
        }
        catch (QueueySyncException ex)
        {
            result = ex.Queues!;
        }

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(ToJsonResult(result, path, config), CliHost.JsonOut));
        else
            WriteHuman(result, path, config);

        return result.AllSucceeded ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static void WritePlan(string path, DeploymentFile file, IReadOnlyList<DeploymentQueuePlan> plans)
    {
        Console.WriteLine($"Queuey apply (dry-run) — {path}");

        if (file.Workspace is { } w && !string.IsNullOrWhiteSpace(w.BaseUrl))
            Console.WriteLine($"  workspace\tbaseUrl={w.BaseUrl}{(w.AuthMode is null ? "" : $" auth={w.AuthMode}")}");

        foreach (DeploymentQueuePlan p in plans)
        {
            string dest = p.Delivery is null
                ? "inherits the workspace"
                : p.Delivery.Inherit ? "back to inheriting" : $"url={p.Delivery.Url}";
            Console.WriteLine($"  • {p.Definition.Name}\t{dest}");
        }

        Console.WriteLine($"{plans.Count} queue(s) declared. Nothing was sent.");
    }

    private static void WriteHuman(QueueSyncResult result, string path, ResolvedConfig config)
    {
        Console.WriteLine($"Queuey apply — {path} → {config.ResolvedApiBase()}  (tenant {config.TenantPublicId ?? "?"})");

        foreach (QueueApplyResult r in result.Applied)
        {
            if (r.Succeeded)
                Console.WriteLine($"  ✓ {r.Name}\t{r.PublicId}\t{(r.Created ? "created" : "exists")}{(r.PolicyApplied ? ", policy" : "")}");
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
        policy = new
        {
            p.Definition.Policy.Ordering,
            p.Definition.Policy.MaxAttempts,
            p.Definition.Policy.DlqEnabled,
            p.Definition.Policy.DlqAfterAttempts,
            p.Definition.Policy.RetentionDays,
            p.Definition.Policy.Idempotent,
        },
        delivery = p.Delivery is null ? null : new { p.Delivery.Url, p.Delivery.Inherit, p.Delivery.AuthMode, p.Delivery.CredentialRef },
    };

    private static object ToJsonResult(QueueSyncResult result, string path, ResolvedConfig config) => new
    {
        file = path,
        apiHost = config.ResolvedApiBase().ToString(),
        total = result.Total,
        succeeded = result.Succeeded,
        created = result.Created,
        failed = result.Failed,
        notAttempted = result.NotAttempted,
        warnings = result.Warnings,
        queues = result.Applied.Select(r => new { r.Name, r.Succeeded, r.PublicId, r.Created, r.PolicyApplied, error = r.Error?.Message }),
    };
}
