using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
///
/// With <c>--intent</c> it starts from what the person wants instead: a Desired
/// Flow, which it enriches from the repository and turns into a deployment file
/// and a code plan, or stops at a conflict and asks. That mode only reads.
/// </summary>
internal static class AdviseCommand
{
    internal static readonly CommandOptions Options = new(
        "advise", flags: new[] { "json", "write-files", "apply", "force" }, values: new[] { "path", "queue", "intent" }, positionals: 1);

    /// <summary>The version of advise's --json output. Version 1 added it, with the candidate flows and --intent.</summary>
    internal const int SchemaVersion = 1;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        var root = map.Get("path") ?? map.FirstPositional ?? Directory.GetCurrentDirectory();

        if (map.Get("intent") is { } intentPath)
        {
            // F2.10: om --intent skal skrive fila, er en produktbeslutning som venter (rapportert 2026-10-06). Til den er tatt,
            // leser modusen bare, og et flagg som ville skrevet noe, avvises i stedet for å gjøre noe annet enn det sier.
            if (map.Has("write-files") || map.Has("apply") || map.Has("force"))
                return CliErrors.Usage(map, "intent_writes_nothing",
                    "advise --intent proposes a deployment file and a code plan, and writes nothing: --write-files, --force and " +
                    "--apply do not go with it.",
                    "Write infrastructure.content to queuey.deploy.json yourself, then run queuey plan and queuey apply.");
            return Intent(map, root, intentPath);
        }

        RepoFacts facts;
        FlowFacts flowFacts;
        try
        {
            facts = RepoScan.Scan(root);
            flowFacts = FlowScan.Scan(root);
        }
        catch (DirectoryNotFoundException ex)
        {
            return CliErrors.Usage(map, "missing_directory", ex.Message);
        }

        Advice advice = Recommendation.For(facts);
        var queueName = ScaffoldPlan.DefaultQueueName(root, map.Get("queue"));
        IReadOnlyList<PlannedFile> scaffold = ScaffoldPlan.For(root, queueName);
        IReadOnlyList<FlowCandidate> candidates = FlowAdvisor.Candidates(flowFacts, root);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    schemaVersion = SchemaVersion,
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
                    candidates = candidates.Select(c => new { summary = c.Summary, flow = c.Flow.ToJson(FlowSchema.Url) }),
                },
                CliHost.JsonOut));

            // --json still performs whatever was asked for; it just does not narrate.
            return await DoRequestedWorkAsync(map, root, queueName, scaffold, quiet: true);
        }

        WriteHuman(root, advice, queueName, scaffold, map, candidates);
        return await DoRequestedWorkAsync(map, root, queueName, scaffold, quiet: false);
    }

    /// <summary>
    /// advise --intent: reads the Desired Flow, enriches it from the repository, and proposes the design, or stops at the
    /// conflicts with exit 1. Writes nothing.
    /// </summary>
    private static int Intent(ArgMap map, string root, string intentPath)
    {
        DesiredFlow intent;
        try
        {
            intent = DesiredFlow.Parse(CliFiles.ReadAllText(intentPath));
        }
        catch (FlowFormatException ex)
        {
            return CliErrors.Usage(map, "invalid_intent", ex.Message,
                "Write the intent as queuey schema --flow describes: each field an object with value and provenance.");
        }

        FlowFacts facts;
        RepoFacts repo;
        try
        {
            facts = FlowScan.Scan(root);
            repo = RepoScan.Scan(root);
        }
        catch (DirectoryNotFoundException ex)
        {
            return CliErrors.Usage(map, "missing_directory", ex.Message);
        }

        string? queue = map.Get("queue") is { } requested ? ScaffoldPlan.DefaultQueueName(root, requested) : null;
        FlowAdvice advice = FlowAdvisor.Advise(intent, facts, root, queue, Recommendation.For(repo));

        if (map.Has("json"))
            Console.WriteLine(IntentJson(root, intentPath, advice).ToJsonString(CliHost.JsonOut));
        else
            AdviseIntentText.Write(root, advice);

        return advice.Design is null ? ExitCodes.RuntimeError : ExitCodes.Success;
    }

    internal const string ConflictStep =
        "Answer each conflict in flow.conflicts: write the answer into the intent, marked stated, and run advise --intent again.";

    private static JsonObject IntentJson(string root, string intentPath, FlowAdvice advice)
    {
        FlowDesign? design = advice.Design;
        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["path"] = Path.GetFullPath(root),
            ["intent"] = intentPath,
            ["outcome"] = design is null ? "conflicts" : "proposed",
            ["flow"] = advice.Flow.ToJson(FlowSchema.Url),
            ["existing"] = DesiredFlow.EvidenceJson(advice.Existing),
            ["infrastructure"] = design is null ? null : new JsonObject
            {
                ["file"] = design.File,
                ["exists"] = design.Exists,
                ["merge"] = design.Merge,
                ["content"] = design.Content.DeepClone(),
                ["variables"] = new JsonArray(design.Variables.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                ["credentials"] = new JsonArray(design.Credentials.Select(c => (JsonNode?)new JsonObject
                {
                    ["name"] = c.Name,
                    ["type"] = c.Type,
                    ["holds"] = c.Holds,
                    ["store"] = c.Store,
                }).ToArray()),
                ["settings"] = new JsonArray(design.Settings.Select(s => (JsonNode?)new JsonObject
                {
                    ["path"] = s.Path,
                    ["value"] = s.Value?.DeepClone(),
                    ["basis"] = s.Basis,
                    ["because"] = s.Because,
                    ["from"] = new JsonArray(s.From.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                }).ToArray()),
            },
            ["code"] = new JsonArray((design?.Code ?? Array.Empty<CodeStep>()).Select(c => (JsonNode?)new JsonObject
            {
                ["file"] = c.File,
                ["line"] = c.Line,
                ["action"] = c.Action,
                ["what"] = c.What,
                ["why"] = c.Why,
                ["basis"] = c.Basis,
            }).ToArray()),
            ["nextSteps"] = new JsonArray((design?.NextSteps ?? new[] { ConflictStep })
                .Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };
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
            Console.Error.WriteLine("Mint an API key in the console (the license menu → Manage license → API keys), then set QUEUEY_API_KEY and QUEUEY_TENANT.");
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
        string root, Advice advice, string queueName, IReadOnlyList<PlannedFile> scaffold, ArgMap map,
        IReadOnlyList<FlowCandidate> candidates)
    {
        Console.WriteLine($"Queuey — how this fits  ({Path.GetFullPath(root)})");
        Console.WriteLine();
        Console.WriteLine(advice.Headline);

        WriteSection("Why", advice.Reasons);
        WriteSection("Next", advice.NextSteps, numbered: true);
        WriteSection("Receiving", advice.ReceivingSteps, numbered: true);
        WriteSection("I could not tell from the code", advice.Questions);

        if (candidates.Count > 0)
        {
            WriteSection("Flows I can see (describe the one you want in a Desired Flow, and run advise with --intent)",
                candidates.Select(c => c.Summary).ToArray());
        }

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

    internal static void WriteSection(string title, IReadOnlyList<string> lines, bool numbered = false)
    {
        if (lines.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"{title}:");
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
            Console.WriteLine($"  {(numbered ? $"{i + 1}." : "-")} {Wrap(line)}");
    }

    /// <summary>Wraps at a terminal-friendly width, keeping the two-space hang of the bullet.</summary>
    internal static string Wrap(string text, int width = 88)
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
