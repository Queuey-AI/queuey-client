using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey keys mint</c> — mints an ingress signing key for a queue, so a producer can publish with
/// HMAC instead of an API key.
/// </summary>
/// <remarks>
/// Needs a credential carrying <c>ApiKeyManage</c>. A deploy key deliberately does not carry it: a
/// key that could mint keys would turn pipeline access into account access. This is a one-off run
/// from an admin credential, and the secret is shown once.
/// </remarks>
internal static class KeysCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "json", "help", "h" };

    public static async Task<int> RunAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        if (sub is "" or "-h" or "--help" or "help") { Console.WriteLine(Usage.Text); return ExitCodes.Success; }
        if (sub != "mint")
        {
            Console.Error.WriteLine($"Unknown keys subcommand '{sub}'. Expected 'mint'.");
            return ExitCodes.Usage;
        }

        ArgMap map = ArgMap.Parse(rest, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? queue = map.Get("queue");
        if (string.IsNullOrWhiteSpace(queue))
        {
            Console.Error.WriteLine("keys mint requires --queue <que_...>.");
            return ExitCodes.Usage;
        }

        ResolvedConfig config = CliHost.Resolve(map);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        IngressSigningKey key = await service.Management.MintIngressKeyAsync(queue!, map.Get("name") ?? "queuey-cli");

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { key.ClientPublicId, key.ClientName, key.KeyId, key.Secret, key.QueuePublicId }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine($"Minted an ingress signing key for {key.QueuePublicId}.");
        Console.WriteLine();
        Console.WriteLine($"  SigningKeyId  = {key.KeyId}");
        Console.WriteLine($"  SigningSecret = {key.Secret}");
        Console.WriteLine();
        Console.WriteLine("The secret is shown once and cannot be retrieved again — put it in your secret store now.");
        Console.WriteLine("Then set QueueyOptions.SigningKeyId and .SigningSecret, and drop the API key from your producer.");
        return ExitCodes.Success;
    }
}
