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
    internal static readonly CommandOptions Options = new(
        "pull",
        flags: new[] { "force", "stdout", "json" },
        values: new[] { "file", "as", "emit-code", "namespace" });

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        ResolvedConfig config = CliHost.Resolve(map);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        DeploymentFile file = await service.PullDeploymentAsync(config.TenantPublicId);

        // --as rewrites the handful of values that do not travel between environments into ${VAR}
        // references, so one committed file converges them all.
        string? asEnvironment = map.Get("as");
        if (asEnvironment is not null)
            file = DeploymentTemplate.ToTemplate(file, asEnvironment);

        if (map.Get("emit-code") is { } codePath)
        {
            string code = QueueCodeWriter.ForFile(file, map.Get("namespace") ?? "Queues");
            CliFiles.WriteAllText(codePath, code);
            Console.WriteLine($"Wrote {codePath} — {file.Queues.Count} [QueueyQueue] declaration(s). "
                              + "Behaviour only; destinations stay in the deployment file.");
        }

        string json = file.ToJson();

        if (map.Has("stdout"))
        {
            Console.WriteLine(json);
            WarnAboutRefusedFilters(file);
            return ExitCodes.Success;
        }

        string path = map.Get("file") ?? DeploymentFile.DefaultFileName;
        if (File.Exists(path) && !map.Has("force"))
        {
            // Overwriting a committed declaration is how you lose an intentional edit that has not
            // been applied yet — that belongs behind an explicit flag, or under a diff.
            return CliErrors.Usage(map, "file_exists", $"'{path}' already exists.",
                "Re-run with --force to overwrite, or --stdout to review the pull first (diff it before you replace anything).");
        }

        CliFiles.WriteAllText(path, json + Environment.NewLine);

        Console.WriteLine($"Wrote {path} — {file.Queues.Count} queue(s)"
                          + (file.Tenant is null ? "" : $" from {file.Tenant}") + ".");
        Console.WriteLine("No secrets are in it: credentials appear by name only. Safe to commit "
                          + "(keep it separate from queuey.json, which holds your API key).");

        IReadOnlyList<string> variables = file.ReferencedVariables();
        if (variables.Count > 0)
        {
            Console.WriteLine($"Set these before applying it: {string.Join(", ", variables)}");
            Console.WriteLine("Everything else travels as-is — a queue that owns only a path says the "
                              + "same thing in every environment.");
        }

        WarnAboutRefusedFilters(file);
        return ExitCodes.Success;
    }

    /// <summary>
    /// One warning per filter problem apply would refuse, on stderr so <c>--stdout</c> stays a clean file. Pull
    /// writes the filter as Queuey stored it; the warning says what has to be fixed before the file applies.
    /// </summary>
    private static void WarnAboutRefusedFilters(DeploymentFile file)
    {
        // Rå overrides eller en deploy fra før Queuey#391 kan ha lagret "gt": "1,000" eller exists med en verdi. Pull
        // skriver det som det står, og da avviste apply, plan og --check fila uten at pull hadde sagt noe (review
        // 2026-10-05). Fila skrives fortsatt: den viser hva Queuey har.
        IReadOnlyList<string> problems = file.FilterProblems();
        foreach (string problem in problems)
            Console.Error.WriteLine($"Warning: {problem}");
        if (problems.Count > 0)
            Console.Error.WriteLine("apply, plan and apply --check refuse the file until "
                                    + (problems.Count == 1 ? "this is" : "these are") + " fixed in it.");
    }
}
