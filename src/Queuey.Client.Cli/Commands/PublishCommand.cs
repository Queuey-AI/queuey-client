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

internal static class PublishCommand
{
    internal static readonly CommandOptions Options = new(
        "publish",
        flags: new[] { "stdin", "json" },
        values: new[] { "event", "key", "data", "file", "idempotency-key", "stream" },
        positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? stream = map.FirstPositional ?? map.Get("stream");
        string? eventType = map.Get("event");
        if (string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(eventType))
            return CliErrors.Usage(map, "missing_argument", "publish requires <stream> and --event <type>.");

        byte[]? body = ReadBody(map, out string? bodyCode, out string? bodyError);
        if (bodyError != null)
            return CliErrors.Usage(map, bodyCode!, bodyError);

        ResolvedConfig config = CliHost.Resolve(map);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        PublishResult result = await service.PushEventAsync(
            stream!,
            eventType!,
            map.Get("key"),
            body ?? Array.Empty<byte>(),
            new PublishOptions
            {
                IdempotencyKey = map.Get("idempotency-key"),
                Source = map.Get("source"),
                ContentType = "application/json",
            });

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(result, CliHost.JsonOut));
        else
            Console.WriteLine($"Published {result.EventId} → {result.QueuePublicId} (mode={result.Mode}, replayed={result.Replayed})");

        return ExitCodes.Success;
    }

    private static byte[]? ReadBody(ArgMap map, out string? code, out string? error)
    {
        code = error = null;

        if (map.Has("stdin"))
            return Encoding.UTF8.GetBytes(Console.In.ReadToEnd());

        string? file = map.Get("file");
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file)) { code = "missing_file"; error = $"File not found: {file}"; return null; }
            return File.ReadAllBytes(file);
        }

        string? data = map.Get("data");
        if (data != null)
            return Encoding.UTF8.GetBytes(data);

        code = "missing_body";
        error = "publish requires a body: --data <json>, --file <path>, or --stdin.";
        return null;
    }
}
