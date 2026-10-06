using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey replay &lt;event-id&gt; --queue &lt;que_…&gt;</c> — replays one existing event to your connected
/// <c>queuey listen</c> session for local debugging. Read-only: the event is not modified and the real
/// endpoint is never contacted. Works on any event of a queue that forwards its deliveries to the listener
/// (Local forward) and shares its payloads in full, a DLQ'd one included. On any other queue the server
/// refuses, and the command shows the server's reason, which says what to change.
/// </summary>
internal static class ReplayCommand
{
    internal static readonly CommandOptions Options = new("replay", flags: new[] { "json" }, values: new[] { "queue" }, positionals: 1);

    /// <summary>
    /// The codes the server refuses a replay to a listener with (409): the queue doesn't forward its deliveries
    /// to a listener, or it shares only its payloads' shape. The server's message says what to change.
    /// </summary>
    internal static readonly IReadOnlySet<string> RefusalCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "listener_replay_needs_local_forward",
        "listener_replay_needs_full_payload_sharing",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h"))
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        string? eventId = map.FirstPositional;
        string? queue = map.Get("queue");
        if (string.IsNullOrWhiteSpace(eventId))
            return CliErrors.Usage(map, "missing_argument", "replay requires an event id: queuey replay <event-id> --queue <que_...>");
        if (string.IsNullOrWhiteSpace(queue))
            return CliErrors.Usage(map, "missing_argument", "replay requires --queue <que_...> (the queue the event belongs to).");

        using ServiceProvider sp = CliHost.BuildProvider(CliHost.Resolve(map));
        var svc = sp.GetRequiredService<IQueueyService>();

        ReplayResult r;
        try
        {
            r = await svc.Management.ReplayToListenerAsync(queue!, eventId!);
        }
        catch (QueueyConflictException ex) when (IsRefusal(ex))
        {
            WriteRefusal(ex, map.Has("json"), Console.Out, Console.Error);
            return ExitCodes.RuntimeError;
        }

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(r, CliHost.JsonOut));
            return r.Delivered ? ExitCodes.Success : ExitCodes.RuntimeError;
        }

        if (!r.ListenerConnected)
        {
            Console.Error.WriteLine($"No listener connected. Run  queuey listen --forward-to <origin> --queue {queue}  first, then replay.");
            return ExitCodes.RuntimeError;
        }

        string code = r.StatusCode?.ToString() ?? "no response";
        string tail = string.IsNullOrWhiteSpace(r.Error) ? "" : $"  — {r.Error}";
        Console.WriteLine($"Replayed {eventId}  →  {code} ({r.DurationMs}ms){tail}");
        return r.Delivered ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    /// <summary>Whether <paramref name="ex"/> is the server refusing the replay to a listener, not a failure.</summary>
    internal static bool IsRefusal(QueueyException ex)
        => ex.StatusCode == 409 && ex.ErrorCode is { } code && RefusalCodes.Contains(code);

    /// <summary>
    /// Shows a refused replay: the server's message, which is the hint, on stderr. With --json it is the API's
    /// error envelope on stdout instead, so a script can read the code.
    /// </summary>
    internal static void WriteRefusal(QueueyException ex, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (json)
            stdout.WriteLine(JsonSerializer.Serialize(new { error = new { code = ex.ErrorCode, message = ex.Message } }, CliHost.JsonOut));
        else
            stderr.WriteLine($"Replay refused: {ex.Message}");
    }
}
