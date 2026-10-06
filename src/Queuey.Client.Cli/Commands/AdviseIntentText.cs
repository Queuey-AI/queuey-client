using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Queuey.Client.Cli.Advise;

namespace Queuey.Client.Cli;

/// <summary>
/// advise --intent for a person: the flow with where each field came from, then either the conflicts to answer, or the
/// deployment file with the reason for each setting, the code steps and what to run. The same as --json gives, in words.
/// </summary>
internal static class AdviseIntentText
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(string root, FlowAdvice advice)
    {
        DesiredFlow flow = advice.Flow;
        Console.WriteLine($"Queuey — the flow you described  ({Path.GetFullPath(root)})");
        Console.WriteLine();
        Console.WriteLine("Flow (stated: the intent says so; evidence: found here; assumed: advise's default):");

        (FlowFieldSpec Spec, FlowValue Value)[] fields = flow.Fields.ToArray();
        int pathWidth = fields.Select(f => f.Spec.Path.Length).DefaultIfEmpty(10).Max();
        int valueWidth = Math.Min(28, fields.Select(f => Shown(f.Value.Value).Length).DefaultIfEmpty(10).Max());
        foreach ((FlowFieldSpec spec, FlowValue value) in fields)
        {
            string where = value.Evidence.Count > 0
                ? "  " + string.Join(", ", value.Evidence.Take(2).Select(e => e.ToString())) + (value.Evidence.Count > 2 ? ", …" : "")
                : "";
            Console.WriteLine($"  {spec.Path.PadRight(pathWidth)}  {Shown(value.Value).PadRight(valueWidth)}  " +
                              $"{DesiredFlow.ProvenanceText(value.Provenance)}{where}");
        }

        AdviseCommand.WriteSection("Assumptions", flow.Assumptions);
        AdviseCommand.WriteSection("Queuey is here already", advice.Existing.Select(e => $"{e.What} ({e})").ToArray());

        if (advice.Design is not { } design)
        {
            Console.WriteLine();
            Console.WriteLine("Stopped: nothing is proposed until these are answered.");
            int n = 1;
            foreach (FlowConflict conflict in flow.Conflicts)
            {
                Console.WriteLine($"  {n++}. {conflict.Field} ({conflict.Kind}): {AdviseCommand.Wrap(conflict.Message)}");
                if (conflict.Evidence.Count > 0)
                    Console.WriteLine("     Found: " + AdviseCommand.Wrap(string.Join(", ",
                        conflict.Evidence.Take(4).Select(e => $"{e.What} ({e})"))));
                Console.WriteLine($"     → {AdviseCommand.Wrap(conflict.Question)}");
            }
            Console.WriteLine();
            Console.WriteLine(AdviseCommand.ConflictStep);
            return;
        }

        Console.WriteLine();
        Console.WriteLine(design.Exists
            ? $"Infrastructure — {design.File}, which is there. {design.Merge}"
            : $"Infrastructure — {design.File}, to create:");
        foreach (string line in design.Content.ToJsonString(Indented).Split('\n'))
            Console.WriteLine("  " + line);

        AdviseCommand.WriteSection("Why each setting",
            design.Settings.Select(s => $"{s.Path} = {Shown(s.Value)} [{s.Basis}] {s.Because}").ToArray());
        AdviseCommand.WriteSection("Credentials the file names (store each once; the file never holds them)",
            design.Credentials.Select(c => $"{c.Name} ({c.Type}): {c.Holds} Store it with: {c.Store}").ToArray());
        AdviseCommand.WriteSection("Variables it needs when it is applied", design.Variables);
        AdviseCommand.WriteSection("Code (the application's half; the file above is Queuey's)",
            design.Code.Select(c => $"{c.Action}: {Where(c)}{c.What} {c.Why}").ToArray(), numbered: true);
        AdviseCommand.WriteSection("Next", design.NextSteps, numbered: true);

        Console.WriteLine();
        Console.WriteLine("Nothing was written. --json gives the same as data, with the flow to keep beside the code.");
    }

    private static string Where(CodeStep step)
        => step.File is null ? "" : step.Line is { } line ? $"{step.File}:{line} — " : $"{step.File} — ";

    private static string Shown(JsonNode? value) => value?.ToJsonString(Compact) ?? "null";
}
