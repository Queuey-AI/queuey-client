using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey keys</c>: mint an ingress signing key for a queue, so a producer can publish with HMAC instead of an API key; list a
/// queue's keys; revoke one.
/// </summary>
/// <remarks>
/// Minting needs a credential carrying <c>ApiKeyManage</c>, or a login (<c>queuey login</c>) for a person who may manage keys.
/// A deploy key deliberately does not carry it: a key that could mint keys would turn pipeline access into account access.
/// With <c>--write .env</c> the secret goes straight into a git-ignored file and is never printed.
/// </remarks>
internal static class KeysCommand
{
    internal static readonly CommandOptions MintOptions = new("keys mint", flags: new[] { "json" }, values: new[] { "queue", "name", "write", "profile" });
    internal static readonly CommandOptions ListOptions = new("keys list", flags: new[] { "json" }, values: new[] { "queue", "profile" });
    internal static readonly CommandOptions RevokeOptions = new("keys revoke", flags: new[] { "json" }, values: new[] { "reason", "profile" }, positionals: 1);

    /// <summary>The variables <c>--write</c> sets, the names the SDK reads (<see cref="QueueyOptions.UseEnvironmentVariables"/>).</summary>
    internal static readonly string[] Variables = { QueueyEnvironmentVariables.SigningKeyId, QueueyEnvironmentVariables.SigningSecret };

    public static async Task<int> RunAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        return sub switch
        {
            "" or "-h" or "--help" or "help" => Help(),
            "mint" => await MintAsync(rest),
            "list" => await ListAsync(rest),
            "revoke" => await RevokeAsync(rest),
            _ => CliErrors.Write(CliErrors.WantsJson(rest), "unknown_subcommand",
                $"Unknown keys subcommand '{CliErrors.Shown(sub)}'. Expected 'mint', 'list' or 'revoke'.", action: null, status: null, ExitCodes.Usage),
        };
    }

    private static int Help()
    {
        Console.WriteLine(Usage.Text);
        return ExitCodes.Success;
    }

    private static async Task<int> MintAsync(string[] args)
    {
        if (!MintOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();

        if (string.IsNullOrWhiteSpace(map.Get("queue")))
            return CliErrors.Usage(map, "missing_argument", "keys mint requires --queue <name|que_…>.");
        if (map.Has("write") && string.IsNullOrWhiteSpace(map.Get("write")))
            return CliErrors.Usage(map, "missing_value", "--write takes a file, such as .env.");

        // Fila sjekkes før noe mintes: en hemmelighet som ikke kan skrives, er en nøkkel som må trekkes tilbake.
        string? file = map.Get("write") is { } write ? EnvFile.Check(write) : null;

        ResolvedConfig config = CliHost.Resolve(map, profiles: true);
        string queue = await QueueIdAsync(map, config);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        IngressSigningKey key;
        try
        {
            key = await service.Management.MintIngressKeyAsync(queue, map.Get("name") ?? "queuey-cli");
        }
        catch (IngressKeyPendingException pending)
        {
            return Pending(map.Has("json"), pending);
        }

        if (string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrEmpty(key.Secret))
            return CliErrors.Write(map.Has("json"), "mint_answer_incomplete", "Queuey answered the mint without a key id and a secret.", null,
                status: null, ExitCodes.RuntimeError, "Queuey error");

        if (file is not null)
            return Written(map, key, file, map.Get("write")!);

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
        Console.Error.WriteLine("Tip: --write .env puts them in a git-ignored file instead, and never prints the secret.");
        return ExitCodes.Success;
    }

    /// <summary>The key in the file, and what was written, without the secret.</summary>
    private static int Written(ArgMap map, IngressSigningKey key, string file, string shown)
    {
        Dictionary<string, string?> previous;
        try
        {
            previous = EnvFile.Write(file, new[] { (Variables[0], key.KeyId!), (Variables[1], key.Secret!) });
        }
        catch (CliFileException ex)
        {
            // Nøkkelen finnes, men hemmeligheten er tapt: den vises ikke her heller. Den trekkes tilbake, og en ny mintes.
            return CliErrors.Write(map.Has("json"), ex.Code,
                $"Minted key {key.KeyId}, but could not write it to {shown}: {ex.Message} The secret is not shown.",
                $"Revoke it with queuey keys revoke {key.KeyId}, fix the file, and mint again.", status: null, ExitCodes.Configuration, "Error");
        }

        string? replaced = previous[Variables[0]] is { } old && old != key.KeyId ? old : null;
        bool readable = EnvFile.OthersCanRead(file);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                key.ClientPublicId, key.ClientName, key.KeyId, key.QueuePublicId,
                file = shown,
                variables = Variables,
                replacedKeyId = replaced,
            }, CliHost.JsonOut));
        }
        else
        {
            Console.WriteLine($"Minted ingress signing key {key.KeyId} for {key.QueuePublicId}.");
            Console.WriteLine($"Wrote {Variables[0]} and {Variables[1]} to {shown}. The secret is not shown.");
            Console.WriteLine("A producer reads them from the environment, with QueueyOptions.UseEnvironmentVariables() in .NET.");
        }

        if (replaced is not null)
            Console.Error.WriteLine($"Note: {shown} held key {TerminalText.Line(replaced)} before. It still verifies until it is revoked: " +
                                    $"queuey keys revoke {TerminalText.Line(replaced)}");
        if (readable)
            Console.Error.WriteLine($"Note: other users can read {shown}. Let only you read it: chmod 600 {shown}");
        return ExitCodes.Success;
    }

    /// <summary>
    /// A mint Queuey gave to a person (202): the link where they decide, and exit 5. Nothing was minted, so there is no secret.
    /// </summary>
    private static int Pending(bool json, IngressKeyPendingException pending)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = CredentialRotationPendingException.PendingApproval,
                approvalUrl = pending.ApprovalUrl,
                expiresAt = pending.ExpiresAt,
                policyRule = pending.PolicyRule,
                message = pending.Message,
            }, CliHost.JsonOut));
            return ExitCodes.PendingApproval;
        }

        // Teksten og lenken kommer fra serveren, så de går gjennom TerminalText (F2.7-regelen).
        Console.WriteLine("Nothing was minted: a person decides on this key in Queuey's inbox.");
        if (pending.ApprovalUrl is { } url)
            Console.WriteLine($"  Give this link to a person who may manage the workspace's keys: {TerminalText.Line(url)}");
        if (pending.ExpiresAt is { } expires)
            Console.WriteLine($"  The request expires at {expires.UtcDateTime:yyyy-MM-dd HH:mm} UTC.");
        Console.Error.WriteLine(TerminalText.Line(pending.Message));
        return ExitCodes.PendingApproval;
    }

    private static async Task<int> ListAsync(string[] args)
    {
        if (!ListOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();
        if (string.IsNullOrWhiteSpace(map.Get("queue")))
            return CliErrors.Usage(map, "missing_argument", "keys list requires --queue <name|que_…>.");

        ResolvedConfig config = CliHost.Resolve(map, profiles: true);
        string queue = await QueueIdAsync(map, config);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        if (provider.GetRequiredService<IQueueyService>().Management is not QueueyManagement management)
            return CliErrors.Write(map.Has("json"), "unsupported", "This build's Queuey client can't list keys.", null, status: null, ExitCodes.RuntimeError);

        IReadOnlyList<QueueHmacClientWireResponse> keys = await management.ListIngressKeysAsync(queue);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                queuePublicId = queue,
                keys = keys.Select(k => new
                {
                    k.KeyId, k.ClientPublicId, k.ClientName, active = k.KeyIsActive && k.ClientIsActive,
                    k.LastUsedAtUtc, k.ExpiresAtUtc, k.RevokedAtUtc, k.RevokedReason,
                }),
            }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine($"{keys.Count} signing key(s) for {queue}:");
        foreach (QueueHmacClientWireResponse k in keys)
        {
            string state = k.RevokedAtUtc is { } revoked ? $"revoked {revoked.UtcDateTime:yyyy-MM-dd}"
                : k.KeyIsActive && k.ClientIsActive ? "active" : "inactive";
            string used = k.LastUsedAtUtc is { } last ? $"last used {last.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "never used";
            Console.WriteLine(TerminalText.Line($"  {k.KeyId}  {k.ClientName}  {state}, {used}"));
        }

        return ExitCodes.Success;
    }

    private static async Task<int> RevokeAsync(string[] args)
    {
        if (!RevokeOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();
        string? keyId = map.FirstPositional?.Trim();
        if (string.IsNullOrWhiteSpace(keyId))
            return CliErrors.Usage(map, "missing_argument", "keys revoke requires the key's id: queuey keys revoke <keyId>.",
                "queuey keys list --queue <queue> lists them.");

        ResolvedConfig config = CliHost.Resolve(map, profiles: true);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        if (provider.GetRequiredService<IQueueyService>().Management is not QueueyManagement management)
            return CliErrors.Write(map.Has("json"), "unsupported", "This build's Queuey client can't revoke keys.", null, status: null, ExitCodes.RuntimeError);

        // Queuey svarer 404 både for en ukjent nøkkel og en annens, og 403 approval_required for en nøkkel i prod (som da fortsatt
        // verifiserer). Begge skrives av CliEntry med Queueys egen melding.
        await management.RevokeIngressKeyAsync(keyId!, map.Get("reason"));

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(new { keyId, revoked = true }, CliHost.JsonOut));
        else
            Console.WriteLine($"Revoked signing key {TerminalText.Line(keyId)}: every producer that signs with it is refused at the ingress now.");
        return ExitCodes.Success;
    }

    /// <summary>The queue's id: <c>--queue que_…</c> as given, or a name looked up in the configured workspace.</summary>
    private static async Task<string> QueueIdAsync(ArgMap map, ResolvedConfig config)
        => (await ListenCommand.ResolveTargetAsync(map, config)).Id;
}
