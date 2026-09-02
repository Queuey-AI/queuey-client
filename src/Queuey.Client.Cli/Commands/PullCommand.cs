using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey pull</c> — reads a workspace back into a deployment file. Configure once in the console,
/// pull it, commit it, and every other environment converges from the same file.
/// </summary>
internal static class PullCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "force", "stdout", "json", "help", "h",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        ResolvedConfig config = CliHost.Resolve(map);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        DeploymentFile file = await service.PullDeploymentAsync(config.TenantPublicId);
        string json = file.ToJson();

        if (map.Has("stdout"))
        {
            Console.WriteLine(json);
            return ExitCodes.Success;
        }

        string path = map.Get("file") ?? DeploymentFile.DefaultFileName;
        if (File.Exists(path) && !map.Has("force"))
        {
            // Overwriting a committed declaration is how you lose an intentional edit that has not
            // been applied yet — that belongs behind an explicit flag, or under a diff.
            Console.Error.WriteLine($"'{path}' already exists. Re-run with --force to overwrite, "
                                    + "or --stdout to review the pull first (diff it before you replace anything).");
            return ExitCodes.Usage;
        }

        File.WriteAllText(path, json + Environment.NewLine);

        Console.WriteLine($"Wrote {path} — {file.Queues.Count} queue(s) from {file.Tenant}.");
        Console.WriteLine("No secrets are in it: credentials appear by name only. Safe to commit "
                          + "(keep it separate from queuey.json, which holds your API key).");
        return ExitCodes.Success;
    }
}
