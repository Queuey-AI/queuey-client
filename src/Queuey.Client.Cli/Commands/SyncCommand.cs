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

internal static class SyncCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "dry-run", "stop-on-error", "json", "help", "h",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? assemblyPath = map.Get("assembly");
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            Console.Error.WriteLine("sync requires --assembly <path.dll>.");
            return ExitCodes.Usage;
        }

        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
        {
            Console.Error.WriteLine($"Could not load assembly '{assemblyPath}': {ex.Message}");
            return ExitCodes.AssemblyLoad;
        }

        bool dryRun = map.Has("dry-run");
        ResolvedConfig config = CliHost.Resolve(map);
        Func<StreamDefinition, bool>? filter = BuildFilter(map.Get("only"));

        SyncResult result;
        if (dryRun)
        {
            // Dry-run is purely local: discover + preview, no client, no credentials.
            IEnumerable<StreamDefinition> defs = StreamDiscovery.DefinitionsFromAssembly(assembly);
            _ = new StreamRegistry(defs); // still validate: duplicate names/types fail loudly
            if (filter != null) defs = defs.Where(filter);
            var applied = defs
                .Select(d => new StreamApplyResult
                {
                    ModelType = d.ModelType?.FullName ?? string.Empty,
                    Name = d.Name,
                    Succeeded = true,
                    DryRun = true,
                })
                .ToList();
            result = new SyncResult(applied);
        }
        else
        {
            using ServiceProvider provider = CliHost.BuildProvider(config, b => b.AddStreamsFromAssembly(assembly));
            var service = provider.GetRequiredService<IQueueyService>();
            var options = new SyncOptions { StopOnFirstError = map.Has("stop-on-error"), Filter = filter };
            try
            {
                result = await service.SyncModelsAsync(options);
            }
            catch (QueueySyncException ex)
            {
                result = new SyncResult(ex.Results); // --stop-on-error threw; still render what we have
            }
        }

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(ToJson(result, config, dryRun), CliHost.JsonOut));
        else
            WriteHuman(result, config, dryRun);

        return result.AllSucceeded ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static void WriteHuman(SyncResult result, ResolvedConfig config, bool dryRun)
    {
        string suffix = dryRun ? " (dry-run)" : "";
        Console.WriteLine($"Queuey sync{suffix} → {config.ResolvedApiBase()}  " +
                          $"(tenant {config.TenantPublicId ?? "?"}, license {config.LicensePublicId ?? "?"})");

        foreach (StreamApplyResult r in result.Applied)
        {
            if (r.Succeeded)
                Console.WriteLine($"  {(r.DryRun ? "•" : "✓")} {r.Name}\t{r.PublicId ?? "(dry-run)"}\t{r.Status}".TrimEnd());
            else
                Console.WriteLine($"  ✗ {r.Name}\t{FormatError(r.Error)}");
        }

        Console.WriteLine($"{result.Succeeded} applied, {result.Failed} failed");
    }

    private static Func<StreamDefinition, bool>? BuildFilter(string? only)
    {
        if (string.IsNullOrWhiteSpace(only)) return null;
        var names = new HashSet<string>(
            only.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
            StringComparer.Ordinal);
        return def => names.Contains(def.Name);
    }

    private static string FormatError(QueueyException? e)
        => e is null ? "failed" : $"{(e.StatusCode?.ToString() ?? "error")} {e.ErrorCode} {e.Message}".Replace("  ", " ").Trim();

    private static object ToJson(SyncResult result, ResolvedConfig config, bool dryRun) => new
    {
        apiHost = config.ResolvedApiBase().ToString(),
        dryRun,
        total = result.Total,
        succeeded = result.Succeeded,
        failed = result.Failed,
        streams = result.Applied.Select(r => new
        {
            r.Name,
            r.ModelType,
            r.Succeeded,
            r.PublicId,
            r.Status,
            error = r.Error?.Message,
            r.DryRun,
        }),
    };
}
