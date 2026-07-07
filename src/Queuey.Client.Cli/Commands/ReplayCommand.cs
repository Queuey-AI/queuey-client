using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey replay &lt;event-id&gt; --queue &lt;que_…&gt;</c> — replays one existing event to your connected
/// <c>queuey listen</c> session for local debugging. Read-only: the event is not modified and the real
/// endpoint is never contacted. Works on any event, including a DLQ'd one.
/// </summary>
internal static class ReplayCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "help", "h", "json" };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h"))
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        string? eventId = map.FirstPositional;
        string? queue = map.Get("queue");
        if (string.IsNullOrWhiteSpace(eventId))
        {
            Console.Error.WriteLine("replay requires an event id: queuey replay <event-id> --queue <que_...>");
            return ExitCodes.Usage;
        }
        if (string.IsNullOrWhiteSpace(queue))
        {
            Console.Error.WriteLine("replay requires --queue <que_...> (the queue the event belongs to).");
            return ExitCodes.Usage;
        }

        using ServiceProvider sp = CliHost.BuildProvider(CliHost.Resolve(map));
        var svc = sp.GetRequiredService<IQueueyService>();

        ReplayResult r = await svc.Management.ReplayToListenerAsync(queue!, eventId!);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(r, CliHost.JsonOut));
            return r.Delivered ? ExitCodes.Success : ExitCodes.RuntimeError;
        }

        if (!r.ListenerConnected)
        {
            Console.Error.WriteLine($"No listener connected. Run  queuey listen --forward-to <url> --queue {queue}  first, then replay.");
            return ExitCodes.RuntimeError;
        }

        string code = r.StatusCode?.ToString() ?? "no response";
        string tail = string.IsNullOrWhiteSpace(r.Error) ? "" : $"  — {r.Error}";
        Console.WriteLine($"Replayed {eventId}  →  {code} ({r.DurationMs}ms){tail}");
        return r.Delivered ? ExitCodes.Success : ExitCodes.RuntimeError;
    }
}
