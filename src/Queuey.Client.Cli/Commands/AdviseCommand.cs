using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        "advise", flags: new[] { "json", "write-files", "apply", "force" }, values: new[] { "path", "queue", "intent", "profile" }, positionals: 1);

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

        // --profile velger deploy-filas profil for flyten (review av #60), og hører bare til --intent.
        if (map.Has("profile"))
            return CliErrors.Usage(map, "profile_needs_intent",
                "advise --profile chooses the deployment file's profile for a flow, so it goes with --intent.",
                "Run advise --intent <flow.json> --profile <name>, or leave --profile out.");

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

        Advice advice = Recommendation.For(facts, SecretTarget.SuggestedFor(root));
        var queueName = ScaffoldPlan.DefaultQueueName(root, map.Get("queue"));
        (int retentionDays, string retentionFrom) = await PlanRetention.ForAsync(Connection(map));
        IReadOnlyList<PlannedFile> scaffold = ScaffoldPlan.For(root, queueName, retentionDays);
        IReadOnlyList<string> filesRead = RepoWalk.FilesUnion(facts.FilesRead, flowFacts.FilesRead);
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
                    filesNote = ScaffoldPlan.IngressNote,
                    retentionDays,
                    retentionFrom,
                    filesRead,
                    candidates = candidates.Select(c => new { summary = c.Summary, flow = c.Flow.ToJson(FlowSchema.Url) }),
                    scanLimited = RepoWalk.Union(facts.ScanLimits, flowFacts.Limits),
                },
                CliHost.JsonOut));

            // --json still performs whatever was asked for; it just does not narrate.
            return await DoRequestedWorkAsync(map, root, queueName, scaffold, retentionDays, quiet: true);
        }

        WriteHuman(root, advice, queueName, scaffold, map, candidates, RepoWalk.Union(facts.ScanLimits, flowFacts.Limits), filesRead,
            retentionFrom);
        return await DoRequestedWorkAsync(map, root, queueName, scaffold, retentionDays, quiet: false);
    }

    /// <summary>
    /// advise --intent: reads the Desired Flow, enriches it from the repository, and proposes the design, or stops at the
    /// conflicts with exit 1. Writes nothing.
    /// </summary>
    /// <summary>The connection advise may read the license's plan with, or null when there is none or it cannot be resolved.</summary>
    private static ResolvedConfig? Connection(ArgMap map)
    {
        try
        {
            return CliHost.Resolve(map);
        }
        catch (Exception ex) when (ex is QueueyConfigurationException or CliUsageException or CliFileException)
        {
            return null;
        }
    }

    private static int Intent(ArgMap map, string root, string intentPath)
    {
        DesiredFlow intent;
        try
        {
            string? text = ReadIntent(intentPath);
            if (text is null)
                return CliErrors.Usage(map, "intent_too_large",
                    $"The intent is larger than {MaxIntentBytes / 1024} KiB, the most advise reads: a Desired Flow is a few fields.",
                    "Write the intent as queuey schema --flow describes it, with the stated fields only.");
            intent = DesiredFlow.Parse(text);
        }
        catch (FlowFormatException ex)
        {
            return CliErrors.Usage(map, "invalid_intent", ex.Message,
                "Write the intent as queuey schema --flow describes: each field an object with value and provenance.");
        }

        // Navnet havner i kommandoer advise foreslår (--profile <navn>), så det må ha formen til et profilnavn.
        string? profile = map.Get("profile");
        if (map.Has("profile") && !DeploymentProfiles.IsName(profile))
            return CliErrors.Usage(map, "invalid_value",
                "--profile is not a profile name: lowercase letters, digits, '.', '-' and '_', starting with a letter or digit. " +
                "The value is not shown.",
                "Name one of the deployment file's profiles.");

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
        FlowAdvice advice = FlowAdvisor.Advise(intent, facts, root, queue, Recommendation.For(repo), profile);

        IReadOnlyList<string> limits = RepoWalk.Union(facts.Limits, repo.ScanLimits);
        if (map.Has("json"))
        {
            JsonObject json = IntentJson(root, intentPath, advice, limits);
            json["filesRead"] = new JsonArray(RepoWalk.FilesUnion(facts.FilesRead, repo.FilesRead).Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
            Console.WriteLine(json.ToJsonString(CliHost.JsonOut));
        }
        else
            AdviseIntentText.Write(root, advice, limits);

        return advice.Design is null ? ExitCodes.RuntimeError : ExitCodes.Success;
    }

    /// <summary>The most of an intent advise reads: a Desired Flow is a few fields, never a file this size.</summary>
    internal const int MaxIntentBytes = 256 * 1024;

    /// <summary>
    /// The intent's text, read to <see cref="MaxIntentBytes"/> and no further whatever the file's length says, or null when it
    /// is larger. A file that cannot be read is a <see cref="CliFileException"/>, as for every file the command line names.
    /// </summary>
    private static string? ReadIntent(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buffer = new byte[MaxIntentBytes + 1];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
            return read > MaxIntentBytes ? null : new UTF8Encoding(false).GetString(buffer, 0, read).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliFileException("file_unreadable", ex.Message, "Check that the path names a file this user can read.", ex);
        }
    }

    internal const string ConflictStep =
        "Answer each conflict in flow.conflicts: write the answer into the intent, marked stated, and run advise --intent again.";

    private static JsonObject IntentJson(string root, string intentPath, FlowAdvice advice, IReadOnlyList<string> limits)
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
            ["scanLimited"] = new JsonArray(limits.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
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
                    ["for"] = c.For,
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
        ArgMap map, string root, string queueName, IReadOnlyList<PlannedFile> scaffold, int retentionDays, bool quiet)
    {
        if (map.Has("write-files"))
        {
            int written = WriteFiles(root, scaffold, map.Has("force"), quiet);
            if (written < 0) return ExitCodes.RuntimeError;
        }

        if (map.Has("apply"))
            return await ApplyAsync(map, queueName, retentionDays, quiet);

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
    private static async Task<int> ApplyAsync(ArgMap map, string queueName, int retentionDays, bool quiet)
    {
        var file = new DeploymentFile
        {
            Queues = new Dictionary<string, DeploymentQueue>(StringComparer.Ordinal)
            {
                [queueName] = new DeploymentQueue { DlqEnabled = true, RetentionDays = retentionDays },
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
            Console.Error.WriteLine($"--apply needs a connection: {ex.Message}");
            Console.Error.WriteLine("Log in with queuey login (it prints a link to approve), and name the workspace with --tenant or QUEUEY_TENANT. " +
                                    "The app's own key is the workspace's signing key: queuey keys mint --write user-secrets in a .NET " +
                                    "project with a UserSecretsId, else --write .env; --queue limits it to one queue. It needs a login " +
                                    "that may manage keys.");
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
        IReadOnlyList<FlowCandidate> candidates, IReadOnlyList<string> limits, IReadOnlyList<string> filesRead, string retentionFrom)
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
            // Sammendraget har stier og ruter fra repoet, og går derfor gjennom TerminalText som alt annet fra repoet.
            WriteSection("Flows I can see (describe the one you want in a Desired Flow, and run advise with --intent)",
                candidates.Select(c => TerminalText.Line(c.Summary)).ToArray());
        }

        WriteSection("The scan was limited", limits);

        // Blindtesten 2026-10-09 (funn 8): hjelpeteksten lovet at hver konklusjon navngir fila den kom fra, og advise sa ikke
        // hvilke filer den leste.
        Console.WriteLine();
        Console.WriteLine(filesRead.Count == 0
            ? "Read: no files (nothing the scan looks at)."
            : $"Read {filesRead.Count} file(s): " + Wrap(TerminalText.Line(string.Join(", ", filesRead.Take(30))
                + (filesRead.Count > 30 ? $", and {filesRead.Count - 30} more (all in --json, filesRead)" : ""))));

        var willWrite = map.Has("write-files");
        var willApply = map.Has("apply");

        Console.WriteLine();
        Console.WriteLine(willWrite
            ? $"Files (queue \"{queueName}\"):"
            : $"Files --write-files would create (queue \"{queueName}\"):");
        foreach (var file in scaffold)
            Console.WriteLine($"  - {file.Path}: {file.Action}");
        Console.WriteLine($"  The queue keeps events for {TerminalText.Line(retentionFrom)}.");
        Console.WriteLine($"  {Wrap(ScaffoldPlan.IngressNote)}");

        if (!willApply)
        {
            Console.WriteLine();
            Console.WriteLine($"--apply would create the queue \"{queueName}\" in your workspace, with your login. The app's signing key " +
                              $"is the workspace's: queuey keys mint --write {SecretTarget.SuggestedFor(root)} (add --queue {queueName} only to " +
                              "limit it to one queue), which needs a login that may manage keys.");
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
        // Linjene har stier, ruter og navn fra repoet. TerminalText tar bort det som kunne styrt terminalen (F2.10-review).
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
            Console.WriteLine($"  {(numbered ? $"{i + 1}." : "-")} {Wrap(TerminalText.Line(line))}");
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
