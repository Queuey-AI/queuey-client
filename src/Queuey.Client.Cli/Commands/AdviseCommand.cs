using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client.Cli.Advise;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// Reads the repository it is pointed at and says how Queuey belongs in it.
///
/// The bare command only reads. That is deliberate: an agent runs a command
/// before it reads the help, so the default has to be the safe one. It prints
/// the files it WOULD write and changes nothing.
///
/// Two flags opt into doing something, and they are separate because they are
/// different kinds of trust. <c>--write-files</c> touches the repository, where
/// a mistake shows up in git diff and is undone with git. <c>--apply</c>
/// changes the workspace, which is invisible from the repository and is undone
/// in the console. Nothing should make those one flag.
///
/// The output is written for two readers at once. A person gets the reasoning
/// with the file each conclusion came from, so they can disagree with it. An
/// agent gets --json, and the same fields.
/// </summary>
internal static class AdviseCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "json", "help", "h", "write-files", "apply", "force",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        var root = map.Get("path") ?? map.FirstPositional ?? Directory.GetCurrentDirectory();

        RepoFacts facts;
        try
        {
            facts = RepoScan.Scan(root);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Usage;
        }

        Advice advice = Recommendation.For(facts);
        var queueName = ScaffoldPlan.DefaultQueueName(root, map.Get("queue"));
        IReadOnlyList<PlannedFile> scaffold = ScaffoldPlan.For(root, queueName);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    path = Path.GetFullPath(root),
                    sends = advice.Sends,
                    receives = advice.Receives,
                    send = advice.Send.ToString(),
                    headline = advice.Headline,
                    reasons = advice.Reasons,
                    nextSteps = advice.NextSteps,
                    questions = advice.Questions,
                    receivingSteps = advice.ReceivingSteps,
                    rule = Recommendation.DoNotRebuild,
                    docs = "https://queuey.ai/llms-full.txt",
                    queue = queueName,
                    files = scaffold.Select(f => new { path = f.Path, action = f.Action, exists = f.Exists }),
                },
                CliHost.JsonOut));

            // --json still performs whatever was asked for; it just does not narrate.
            return await DoRequestedWorkAsync(map, root, queueName, scaffold, quiet: true);
        }

        WriteHuman(root, advice, queueName, scaffold, map);
        return await DoRequestedWorkAsync(map, root, queueName, scaffold, quiet: false);
    }

    /// <summary>
    /// Carries out whatever the flags asked for, in the order a person would:
    /// files first, because the deployment file is what apply converges from.
    /// </summary>
    private static async Task<int> DoRequestedWorkAsync(
        ArgMap map, string root, string queueName, IReadOnlyList<PlannedFile> scaffold, bool quiet)
    {
        if (map.Has("write-files"))
        {
            int written = WriteFiles(root, scaffold, map.Has("force"), quiet);
            if (written < 0) return ExitCodes.RuntimeError;
        }

        if (map.Has("apply"))
            return await ApplyAsync(map, queueName, quiet);

        return ExitCodes.Success;
    }

    /// <summary>Writes the scaffold. An existing file is left alone unless --force says otherwise.</summary>
    private static int WriteFiles(string root, IReadOnlyList<PlannedFile> scaffold, bool force, bool quiet)
    {
        var written = 0;

        foreach (var file in scaffold)
        {
            var full = Path.Combine(root, file.Path);
            var isDeployFile = file.Path == ScaffoldPlan.DeployFileName;

            // The gitignore plan is additive — it carries the merged contents —
            // so only the deployment file can actually clobber something.
            if (file.Exists && isDeployFile && !force)
            {
                if (!quiet) Console.WriteLine($"  kept   {file.Path} (already exists; --force to replace)");
                continue;
            }

            try
            {
                File.WriteAllText(full, file.Content);
                written++;
                if (!quiet) Console.WriteLine($"  wrote  {file.Path}");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Could not write {file.Path}: {ex.Message}");
                return -1;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Could not write {file.Path}: {ex.Message}");
                return -1;
            }
        }

        if (!quiet && written > 0)
            Console.WriteLine($"  review with: git diff");

        return written;
    }

    /// <summary>
    /// Converges the workspace down the same path <c>queuey apply</c> takes, so
    /// there is one implementation of what applying means.
    ///
    /// The key is the real boundary here: a flag is a courtesy an eager agent
    /// can add, while a credential's scope is something it cannot argue its way
    /// past. When the key cannot reach far enough, say which step it was and
    /// where a human does it, rather than leaving a 403 in the output.
    /// </summary>
    private static async Task<int> ApplyAsync(ArgMap map, string queueName, bool quiet)
    {
        var file = new DeploymentFile
        {
            Queues = new Dictionary<string, DeploymentQueue>(StringComparer.Ordinal)
            {
                [queueName] = new DeploymentQueue { DlqEnabled = true, RetentionDays = 30 },
            },
        };

        QueueSyncResult result;
        try
        {
            // Configuration is only known to be missing once the client is built,
            // so the friendly form of that error has to wrap the whole thing.
            ResolvedConfig config = CliHost.Resolve(map);
            using ServiceProvider provider = CliHost.BuildProvider(config);
            var service = provider.GetRequiredService<IQueueyService>();

            result = await service.ApplyDeploymentAsync(file, new SyncOptions());
        }
        catch (QueueySyncException ex)
        {
            result = ex.Queues!;
        }
        catch (QueueyConfigurationException ex)
        {
            Console.Error.WriteLine($"--apply needs credentials: {ex.Message}");
            Console.Error.WriteLine("Mint an API key in the console under Developer → API keys, then set QUEUEY_API_KEY and QUEUEY_TENANT.");
            Console.Error.WriteLine("Everything above this line still holds — the advice and any files written needed no credentials.");
            return ExitCodes.Configuration;
        }

        if (!quiet)
        {
            Console.WriteLine();
            foreach (var queue in result.Applied)
            {
                Console.WriteLine(queue.Succeeded
                    ? $"  applied {queue.Name}"
                    : $"  failed  {queue.Name}: {Explain(queue.Error?.Message)}");
            }
        }

        return result.AllSucceeded ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    /// <summary>Turns a permission failure into the thing to do about it.</summary>
    private static string Explain(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "unknown error";

        if (error.Contains("403", StringComparison.Ordinal) || error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return error + " — this key's scope does not reach that. A deploy key deliberately cannot "
                 + "create workspaces or mint credentials; do those in the console, or use a key that can.";
        }

        if (error.Contains("401", StringComparison.Ordinal) || error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return error + " — Queuey did not accept the key. Check QUEUEY_API_KEY.";

        return error;
    }

    private static void WriteHuman(
        string root, Advice advice, string queueName, IReadOnlyList<PlannedFile> scaffold, ArgMap map)
    {
        Console.WriteLine($"Queuey — how this fits  ({Path.GetFullPath(root)})");
        Console.WriteLine();
        Console.WriteLine(advice.Headline);

        WriteSection("Why", advice.Reasons);
        WriteSection("Next", advice.NextSteps, numbered: true);
        WriteSection("Receiving", advice.ReceivingSteps, numbered: true);
        WriteSection("I could not tell from the code", advice.Questions);

        var willWrite = map.Has("write-files");
        var willApply = map.Has("apply");

        Console.WriteLine();
        Console.WriteLine(willWrite
            ? $"Files (queue \"{queueName}\"):"
            : $"Files --write-files would create (queue \"{queueName}\"):");
        foreach (var file in scaffold)
            Console.WriteLine($"  - {file.Path}: {file.Action}");

        if (!willApply)
        {
            Console.WriteLine();
            Console.WriteLine($"--apply would create the queue \"{queueName}\" in your workspace. The API key itself is always minted in the console.");
        }

        Console.WriteLine();
        Console.WriteLine($"Rule: {Recommendation.DoNotRebuild}");
        Console.WriteLine();
        Console.WriteLine(willWrite || willApply
            ? "The whole documentation, as text: https://queuey.ai/llms-full.txt"
            : "Nothing was changed. The whole documentation, as text: https://queuey.ai/llms-full.txt");

        if (willWrite || willApply) Console.WriteLine();
    }

    private static void WriteSection(string title, IReadOnlyList<string> lines, bool numbered = false)
    {
        if (lines.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"{title}:");
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
            Console.WriteLine($"  {(numbered ? $"{i + 1}." : "-")} {Wrap(line)}");
    }

    /// <summary>Wraps at a terminal-friendly width, keeping the two-space hang of the bullet.</summary>
    private static string Wrap(string text, int width = 88)
    {
        var words = text.Split(' ');
        var lines = new List<string>();
        var current = "";

        foreach (var word in words)
        {
            if (current.Length == 0) current = word;
            else if (current.Length + 1 + word.Length <= width) current += " " + word;
            else { lines.Add(current); current = word; }
        }
        if (current.Length > 0) lines.Add(current);

        return string.Join("\n     ", lines);
    }
}
