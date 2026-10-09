using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey events get</c> — reads one event's status and details over REST, as Queuey serves them: the envelope to a key
/// with <c>event.read</c>, and the content (payload, header values, the receiver's responses) only with <c>--content</c>,
/// when the key has <c>event.payload.read</c> and the queue's payload visibility lets values out.
/// </summary>
// F2.7 (2026-10-06): REST leser et event innenfor køen (GET /events/{kø}/{event}), så køen kreves. Innholdet hentes bare
// med --content: hver titt logges av Queuey før den svarer, så det skal være et valg, slik det er i konsollet.
internal static class EventsCommand
{
    internal static readonly CommandOptions GetOptions = new(
        "events get",
        flags: new[] { "content", "json" },
        values: new[] { "queue", "deployment", "profile" },
        positionals: 1);

    /// <summary>The version of <c>events get --json</c>'s shape; a script checks it first, as for the other commands.</summary>
    internal const int JsonSchemaVersion = 1;

    public static async Task<int> RunAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        return sub switch
        {
            "get" => await GetAsync(rest),
            "search" => await EventsSearchCommand.RunAsync(rest),
            "" or "-h" or "--help" or "help" => Help(),
            _ => CliErrors.Write(CliErrors.WantsJson(rest), "unknown_subcommand",
                $"Unknown events subcommand '{CliErrors.Shown(sub)}'. Expected 'get' or 'search'.", action: null, status: null, ExitCodes.Usage),
        };
    }

    private static int Help()
    {
        Console.WriteLine(Usage.Text);
        return ExitCodes.Success;
    }

    private static async Task<int> GetAsync(string[] args)
    {
        if (!GetOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();

        string? eventId = map.FirstPositional?.Trim();
        if (string.IsNullOrWhiteSpace(eventId))
            return CliErrors.Usage(map, "missing_argument", "events get requires <event>: the event's id (evt_…), as publish answered it.");
        if (!eventId!.StartsWith("evt_", StringComparison.Ordinal))
            return CliErrors.Usage(map, "invalid_value",
                "events get takes an event's id (evt_…), as publish answered it. The value is not shown, since it is not one.");

        string? queue = map.Get("queue")?.Trim();
        if (string.IsNullOrWhiteSpace(queue))
            return CliErrors.Usage(map, "missing_argument",
                "events get requires --queue: the queue's name or its id (que_…). Queuey reads an event within its queue (GET /events/<queue>/<event>).",
                "publish and verify print the queue's id beside the event's.");

        // Workspacet etter samme regel som apply, verify og publish, når køen er gitt ved navn.
        (string? fileTenant, string filePath) = DeploymentTenant.FromDeploymentOption(map, CliHost.Profile(map));
        ResolvedConfig config = CliHost.ResolveForDeployment(map, fileTenant, filePath);

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        EventRead read = await service.GetEventAsync(queue!, eventId, revealContent: map.Has("content"));

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(ToJson(read), CliHost.JsonOut));
        else
            WriteHuman(read);

        return ExitCodes.Success;
    }

    private static object ToJson(EventRead r) => new
    {
        schemaVersion = JsonSchemaVersion,
        queuePublicId = r.QueuePublicId,
        eventPublicId = r.EventPublicId,
        status = r.Status,
        payloadVisibility = r.PayloadVisibility,
        canRevealContent = r.CanRevealContent,
        @event = r.Envelope,
        content = r.Content,
    };

    private static void WriteHuman(EventRead r)
    {
        JsonElement e = r.Envelope;
        Console.WriteLine(TerminalText.Line($"{r.EventPublicId} in {r.QueuePublicId}: {r.Status ?? "unknown status"}"));

        Line("received", Text(e, "createdAtUtc"));
        Line("occurred", Text(e, "occurredAtUtc"));
        Line("completed", Text(e, "completedAtUtc"));
        Line("next retry", Text(e, "nextRetryAtUtc"));
        Line("held", Text(e, "holdReason"));
        Line("group key", Text(e, "groupKey"));
        Line("source", Text(e, "source"));

        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("attempts", out JsonElement attempts) && attempts.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"  attempts: {attempts.GetArrayLength()}");
            foreach (JsonElement a in attempts.EnumerateArray())
                Console.WriteLine("    " + TerminalText.Line(Attempt(a)));
        }

        Console.WriteLine("  " + TerminalText.Line(PayloadLine(r)));

        if (r.Content is { } content)
        {
            Console.WriteLine("  content (this look is recorded in the queue's payload access log):");
            Console.WriteLine(Indent(TerminalText.Block(JsonSerializer.Serialize(content, CliHost.JsonOut)), "    "));
        }
    }

    private static string PayloadLine(EventRead r)
    {
        if (r.Content is not null)
            return $"payload: shown below (visibility {r.PayloadVisibility ?? "unknown"}).";
        if (r.CanRevealContent)
            return $"payload: not shown. --content reveals it, and Queuey records the look (visibility {r.PayloadVisibility ?? "unknown"}).";
        return string.Equals(r.PayloadVisibility, "shapeOnly", StringComparison.Ordinal)
            ? "payload: never served, since the queue's payload visibility is shape only."
            : "payload: not shown. Revealing it needs event.payload.read, which this key does not have.";
    }

    private static string Attempt(JsonElement a)
    {
        var parts = new List<string>();
        if (Text(a, "attemptNumber") is { } number) parts.Add("#" + number);
        if (DeliveryStatus(a) is { } status) parts.Add(status);
        if (Text(a, "responseCode") is { } code) parts.Add("HTTP " + code);
        if (Text(a, "durationMs") is { } ms) parts.Add(ms + " ms");
        if (Text(a, "decisionKind") is { } kind) parts.Add(kind + (Text(a, "decisionReason") is { } reason ? $" ({reason})" : ""));
        if (Text(a, "errorMessage") is { } error) parts.Add(error);
        return string.Join("  ", parts);
    }

    // DeliveryStatus er et tall på ledningen; et navn virker også.
    private static string? DeliveryStatus(JsonElement a)
    {
        if (!a.TryGetProperty("status", out JsonElement status))
            return null;
        return status.ValueKind switch
        {
            JsonValueKind.Number when status.TryGetInt32(out int n) => n switch
            {
                0 => "Pending",
                1 => "Success",
                2 => "Failed",
                3 => "Sandbox",
                4 => "Logged",
                5 => "Held",
                6 => "Filtered",
                _ => "status " + n.ToString(CultureInfo.InvariantCulture),
            },
            JsonValueKind.String => status.GetString(),
            _ => null,
        };
    }

    // Verdiene kommer fra produsenten eller serveren (source, groupKey, holdReason …), så de skrives som én ren linje.
    private static void Line(string label, string? value)
    {
        if (value is not null)
            Console.WriteLine($"  {label}: {TerminalText.Line(value)}");
    }

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrEmpty(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string Indent(string text, string prefix)
        => string.Join(Environment.NewLine, text.Split('\n').Select(line => prefix + line.TrimEnd('\r')));
}
