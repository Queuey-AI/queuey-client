using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Queuey.Client.Cli.Advise;

namespace Queuey.Client.Cli;

/// <summary>
/// Reads the repository it is pointed at and says how Queuey belongs in it.
///
/// It only ever reads. There is no flag here that changes a file or a
/// workspace, and that is deliberate: an agent runs a command before it reads
/// the help, so the bare command has to be the safe one. Writing anything is a
/// separate command with a name that says so.
///
/// The output is written for two readers at once. A person gets the reasoning
/// with the file each conclusion came from, so they can disagree with it. An
/// agent gets --json, and the same fields.
/// </summary>
internal static class AdviseCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "json", "help", "h" };

    public static int Run(string[] args)
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
                    docs = "https://app.queuey.ai/llms-full.txt",
                },
                CliHost.JsonOut));
            return ExitCodes.Success;
        }

        WriteHuman(root, advice);
        return ExitCodes.Success;
    }

    private static void WriteHuman(string root, Advice advice)
    {
        Console.WriteLine($"Queuey — how this fits  ({Path.GetFullPath(root)})");
        Console.WriteLine();
        Console.WriteLine(advice.Headline);

        WriteSection("Why", advice.Reasons);
        WriteSection("Next", advice.NextSteps, numbered: true);
        WriteSection("Receiving", advice.ReceivingSteps, numbered: true);
        WriteSection("I could not tell from the code", advice.Questions);

        Console.WriteLine();
        Console.WriteLine($"Rule: {Recommendation.DoNotRebuild}");
        Console.WriteLine();
        Console.WriteLine("Nothing was changed. The whole documentation, as text: https://app.queuey.ai/llms-full.txt");
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
