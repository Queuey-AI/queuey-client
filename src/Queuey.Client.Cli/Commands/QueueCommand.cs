using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey queue plan|sync</c> — declares queues from an assembly's <c>[QueueyQueue]</c> types.
/// <c>plan</c> is network-free; <c>sync</c> applies and exits non-zero unless it fully converged.
/// </summary>
internal static class QueueCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "continue-on-error", "json", "help", "h",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        return sub switch
        {
            "plan" => Plan(rest),
            "sync" => await SyncAsync(rest),
            "" or "-h" or "--help" or "help" => Help(),
            _ => Unknown(sub),
        };
    }

    private static int Help()
    {
        Console.WriteLine(Usage.Text);
        return ExitCodes.Success;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown queue subcommand '{sub}'. Expected 'plan' or 'sync'.");
        return ExitCodes.Usage;
    }

    private static int Plan(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) return Help();

        if (!TryLoadAssembly(map, out Assembly? assembly, out int failure)) return failure;

        // Network-free: discovery + local validation only, so this needs no credentials.
        IReadOnlyList<QueueDefinition> defs = QueueDiscovery.DefinitionsFromAssembly(assembly!);
        _ = new QueueRegistry(defs); // duplicate names/types fail loudly

        var filter = BuildFilter(map.Get("only"));
        var selected = (filter is null ? defs : defs.Where(filter)).ToList();

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(selected.Select(ToJsonPlan), CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine($"Queuey queue plan — {selected.Count} queue(s) declared in {Path.GetFileName(assembly!.Location)}");
        foreach (QueueDefinition d in selected)
            Console.WriteLine($"  • {d.Name}\t{DescribePolicy(d.Policy)}");

        return ExitCodes.Success;
    }

    private static async Task<int> SyncAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) return Help();

        if (!TryLoadAssembly(map, out Assembly? assembly, out int failure)) return failure;

        ResolvedConfig config = CliHost.Resolve(map);
        var filter = BuildFilter(map.Get("only"));

        using ServiceProvider provider = CliHost.BuildProvider(config, b => b.AddQueuesFromAssembly(assembly!));
        var service = provider.GetRequiredService<IQueueyService>();

        QueueSyncResult result;
        try
        {
            result = await service.SyncQueuesAsync(new SyncOptions
            {
                ContinueOnError = map.Has("continue-on-error"),
                QueueFilter = filter,
            });
        }
        catch (QueueySyncException ex)
        {
            result = ex.Queues!;
        }

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(ToJsonResult(result, config), CliHost.JsonOut));
        else
            WriteHuman(result, config);

        return result.AllSucceeded ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static void WriteHuman(QueueSyncResult result, ResolvedConfig config)
    {
        Console.WriteLine($"Queuey queue sync → {config.ResolvedApiBase()}  (tenant {config.TenantPublicId ?? "?"})");

        foreach (QueueApplyResult r in result.Applied)
        {
            if (r.Succeeded)
                Console.WriteLine($"  ✓ {r.Name}\t{r.PublicId}\t{(r.Created ? "created" : "exists")}{(r.PolicyApplied ? ", policy applied" : "")}");
            else
                Console.WriteLine($"  ✗ {r.Name}\t{FormatError(r.Error)}");
        }

        foreach (string skipped in result.NotAttempted)
            Console.WriteLine($"  – {skipped}\tnot attempted (stopped at an earlier failure)");

        // Warnings are not failures — the declared state landed, the workspace just isn't wired yet.
        foreach (string warning in result.Warnings)
            Console.WriteLine($"  ! {warning}");

        Console.WriteLine($"{result.Succeeded} applied ({result.Created} created), {result.Failed} failed, "
                          + $"{result.NotAttempted.Count} not attempted"
                          + (result.NotAttempted.Count > 0 ? " — re-run to converge (applying is idempotent)" : ""));
    }

    private static bool TryLoadAssembly(ArgMap map, out Assembly? assembly, out int failure)
    {
        assembly = null;
        failure = ExitCodes.Success;

        string? path = map.Get("assembly");
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("queue requires --assembly <path.dll>.");
            failure = ExitCodes.Usage;
            return false;
        }

        try
        {
            assembly = Assembly.LoadFrom(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
        {
            Console.Error.WriteLine($"Could not load assembly '{path}': {ex.Message}");
            failure = ExitCodes.AssemblyLoad;
            return false;
        }
    }

    private static Func<QueueDefinition, bool>? BuildFilter(string? only)
    {
        if (string.IsNullOrWhiteSpace(only)) return null;
        var names = new HashSet<string>(
            only!.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
            StringComparer.Ordinal);
        return def => names.Contains(def.Name);
    }

    /// <summary>Renders only what the queue actually declares — everything else says "inherits".</summary>
    private static string DescribePolicy(QueuePolicy p)
    {
        if (p.IsEmpty) return "inherits everything from the workspace";

        var parts = new List<string>();
        if (p.Ordering != null) parts.Add($"ordering={p.Ordering}");
        if (p.DlqEnabled is { } d) parts.Add($"dlq={(d ? "on" : "off")}");
        if (p.RetentionDays is { } rd) parts.Add($"retentionDays={rd}");
        if (p.Idempotent is { } i) parts.Add($"idempotent={(i ? "on" : "off")}");
        return string.Join(" ", parts);
    }

    private static string FormatError(QueueyException? e)
        => e is null ? "failed" : $"{(e.StatusCode?.ToString() ?? "error")} {e.ErrorCode} {e.Message}".Replace("  ", " ").Trim();

    private static object ToJsonPlan(QueueDefinition d) => new
    {
        d.Name,
        modelType = d.ModelType?.FullName,
        policy = new
        {
            d.Policy.Ordering,
            d.Policy.DlqEnabled,
            d.Policy.RetentionDays,
            d.Policy.Idempotent,
        },
        inheritsEverything = d.Policy.IsEmpty,
    };

    private static object ToJsonResult(QueueSyncResult result, ResolvedConfig config) => new
    {
        apiHost = config.ResolvedApiBase().ToString(),
        total = result.Total,
        succeeded = result.Succeeded,
        created = result.Created,
        failed = result.Failed,
        notAttempted = result.NotAttempted,
        warnings = result.Warnings,
        queues = result.Applied.Select(r => new
        {
            r.Name,
            r.ModelType,
            r.Succeeded,
            r.PublicId,
            r.Created,
            r.PolicyApplied,
            r.Warnings,
            error = r.Error?.Message,
        }),
    };
}
