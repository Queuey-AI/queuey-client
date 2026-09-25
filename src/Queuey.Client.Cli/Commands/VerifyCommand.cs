using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey verify</c> — proves a queue delivers: publishes one event and follows it until it is
/// delivered, logged, filtered or failed. The step after <c>apply</c>, because an apply that exits 0
/// says the configuration landed, not that events arrive.
/// </summary>
internal static class VerifyCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "stdin", "json", "help", "h" };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? queue = map.FirstPositional ?? map.Get("queue");
        if (string.IsNullOrWhiteSpace(queue))
        {
            Console.Error.WriteLine("verify requires <queue>: the queue name you publish to.");
            return ExitCodes.Usage;
        }

        byte[]? body = ReadBody(map, out string? bodyError);
        if (bodyError != null)
        {
            Console.Error.WriteLine(bodyError);
            return ExitCodes.Usage;
        }

        int timeoutSeconds = 30;
        if (map.Get("timeout") is { } rawTimeout && (!int.TryParse(rawTimeout, out timeoutSeconds) || timeoutSeconds < 1))
        {
            Console.Error.WriteLine($"--timeout takes whole seconds, at least 1; got '{rawTimeout}'.");
            return ExitCodes.Usage;
        }

        // Workspacet apply skrev til, etter samme regel som apply: fila sin tenant, ellers den
        // konfigurerte, og feil når --tenant eller QUEUEY_TENANT navngir et annet enn fila.
        (string? fileTenant, string filePath) = DeploymentFileTenant(map);
        ResolvedConfig config = CliHost.ResolveForDeployment(map, fileTenant, filePath);

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        DeliveryVerification result = await service.VerifyDeliveryAsync(queue!, body!, new VerifyDeliveryOptions
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            ContentType = map.Get("content-type") ?? "application/json",
            EventType = map.Get("event-type"),
        });

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(ToJson(result), CliHost.JsonOut));
        else
            WriteHuman(result);

        return result.Delivered ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    /// <summary>The deployment file's tenant, when there is a file — named by --deployment, or the default one here.</summary>
    private static (string? Tenant, string Path) DeploymentFileTenant(ArgMap map)
    {
        string? named = map.Get("deployment");
        string path = named ?? DeploymentFile.DefaultFileName;
        if (!File.Exists(path))
        {
            if (named is not null)
                throw new QueueyConfigurationException($"No deployment file at '{path}'.");
            return (null, path);
        }

        return (DeploymentFile.Parse(File.ReadAllText(path)).ResolveTenant(), path);
    }

    private static void WriteHuman(DeliveryVerification r)
    {
        string mark = r.Delivered ? "✓" : "✗";
        string verdict = r.Verdict switch
        {
            DeliveryVerdict.Delivered => "Delivered",
            DeliveryVerdict.LoggedNotDelivered => "Logged, not delivered",
            DeliveryVerdict.Filtered => "Filtered, not delivered",
            DeliveryVerdict.Failed => "Delivery failed",
            _ => "No outcome yet",
        };

        Console.WriteLine($"{mark} {verdict} — {r.Queue} ({r.QueuePublicId}) in {r.Tenant ?? "?"}, event {r.EventId}");
        Console.WriteLine($"  {r.Summary}");
        if (r.SuggestedAction is { } action)
            Console.WriteLine($"  → {action}");
    }

    private static object ToJson(DeliveryVerification r) => new
    {
        tenant = r.Tenant,
        queue = r.Queue,
        queuePublicId = r.QueuePublicId,
        eventId = r.EventId,
        verdict = r.Verdict.ToText(),
        delivered = r.Delivered,
        status = r.Status,
        attempts = r.Attempts,
        target = r.Target,
        responseCode = r.ResponseCode,
        durationMs = r.DurationMs,
        failureClass = r.FailureClass,
        error = r.Error,
        summary = r.Summary,
        suggestedAction = r.SuggestedAction,
    };

    private static byte[]? ReadBody(ArgMap map, out string? error)
    {
        error = null;

        if (map.Has("stdin"))
            return Encoding.UTF8.GetBytes(Console.In.ReadToEnd());

        string? file = map.Get("file");
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file)) { error = $"File not found: {file}"; return null; }
            return File.ReadAllBytes(file);
        }

        string? data = map.Get("data");
        if (data != null)
            return Encoding.UTF8.GetBytes(data);

        // Ingen standard-payload: eventen går til den ekte mottakeren, så hva den får, skal være et valg.
        error = "verify requires the event to send: --data <json>, --file <path>, or --stdin. " +
                "It is delivered to the real receiver like any other event, so send data it treats as harmless.";
        return null;
    }
}
