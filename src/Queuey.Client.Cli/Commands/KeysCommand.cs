using System;
using System.Collections.Generic;
using System.IO;
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
    internal static readonly CommandOptions MintOptions = new("keys mint", flags: new[] { "json", "show-secret" }, values: new[] { "queue", "name", "write", "profile", "type" });
    internal static readonly CommandOptions ListOptions = new("keys list", flags: new[] { "json" }, values: new[] { "queue", "profile" });
    internal static readonly CommandOptions RevokeOptions = new("keys revoke", flags: new[] { "json" }, values: new[] { "reason", "profile" }, positionals: 1);

    /// <summary>The variables <c>--write</c> sets, the names the SDK reads (<see cref="QueueyOptions.UseEnvironmentVariables(System.Func{string, string?}?)"/>).</summary>
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

        bool json = map.Has("json");
        if (map.Has("write") && string.IsNullOrWhiteSpace(map.Get("write")))
            return CliErrors.Usage(map, "missing_value", "--write takes where the secret goes: .env (or another file), or user-secrets.");
        string type = (map.Get("type") ?? IngressKeyTypes.Signing).Trim().ToLowerInvariant();
        if (type is not (IngressKeyTypes.Signing or IngressKeyTypes.ApiKey))
            return CliErrors.Usage(map, "invalid_value", $"--type takes {IngressKeyTypes.Signing} (the default) or {IngressKeyTypes.ApiKey}.",
                "signing: the app signs each event with the key. api-key: the app sends a key that can only publish here, in X-Api-Key.");
        string[] variables = type == IngressKeyTypes.ApiKey ? new[] { QueueyEnvironmentVariables.ApiKey } : Variables;

        // Målet sjekkes før noe mintes: en hemmelighet som ikke kan skrives, er en nøkkel som må trekkes tilbake.
        SecretTarget? target = map.Get("write") is { } write ? SecretTarget.Parse(write) : null;

        (ResolvedConfig config, string? workspaceFrom) = DeploymentTenant.OrFromDeploymentFile(CliHost.Resolve(map, profiles: true), map);
        if (workspaceFrom is not null)
            Console.Error.WriteLine($"Workspace {config.TenantPublicId} from {workspaceFrom}.");
        string? queue = string.IsNullOrWhiteSpace(map.Get("queue")) ? null : await QueueIdAsync(map, config);
        string? tenant = config.TenantPublicId;
        if (queue is null && string.IsNullOrWhiteSpace(tenant))
            return CliErrors.Configuration(map, "config_error",
                "keys mint without --queue mints for the workspace, and none is named.",
                "Name it with --tenant, the profile's tenant, QUEUEY_TENANT, or tenant in queuey.deploy.json; or mint for one queue " +
                "with --queue <name|que_…>.");

        // Uten --write mintes ingenting (Kenneth 2026-10-09): en nøkkel ingen kan se, er til ingen nytte, og hemmeligheten skal ikke
        // stå i terminalen. Svaret sier hvilke variabler appen trenger, hvor --write legger dem, og hvor en person ser nøkkelen.
        if (target is null && !map.Has("show-secret"))
            return Guide(json, config, tenant, queue, type, variables);

        using ServiceProvider provider = CliHost.BuildProvider(config);
        if (provider.GetRequiredService<IQueueyService>().Management is not QueueyManagement management)
            return CliErrors.Write(json, "unsupported", "This build's Queuey client can't mint keys.", null, status: null, ExitCodes.RuntimeError);

        IngressSigningKey key;
        try
        {
            key = await management.MintKeyAsync(queue, tenant, map.Get("name") ?? "queuey-cli", type);
        }
        catch (IngressKeyPendingException pending)
        {
            return Pending(json, pending);
        }
        catch (QueueyForbiddenException refused) when (refused.ErrorCode == "approval_required")
        {
            // Queuey #513: en innlogging i et prod-workspace mynter ikke selv; en person gjør det, på siden lenken viser.
            return ApprovalRequired(json, refused);
        }

        bool apiKey = type == IngressKeyTypes.ApiKey;
        if ((!apiKey && string.IsNullOrWhiteSpace(key.KeyId)) || string.IsNullOrEmpty(key.Secret))
            return CliErrors.Write(json, "mint_answer_incomplete", "Queuey answered the mint without a key id and a secret.", null,
                status: null, ExitCodes.RuntimeError, "Queuey error");

        if (target is not null)
        {
            if ((!apiKey && !EnvFile.IsSafeValue(key.KeyId)) || !EnvFile.IsSafeValue(key.Secret))
                return CliErrors.Write(json, "mint_answer_invalid",
                    $"Queuey answered the mint with a key id or secret that has characters {target.Shown} cannot hold safely, so nothing was " +
                    "written. Neither is shown.",
                    "Revoke the new key in the Queuey console, and report this.", status: null, ExitCodes.RuntimeError, "Queuey error");
            return Written(map, key, target, apiKey ? new[] { (variables[0], key.Secret!) } : new[] { (variables[0], key.KeyId!), (variables[1], key.Secret!) },
                workspaceFrom);
        }

        // --show-secret: den eneste veien til hemmeligheten i terminalen, valgt med vilje.
        Console.Error.WriteLine("Warning: --show-secret prints the secret. Anything that reads this terminal or its log has it now.");
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                key.ClientPublicId, key.ClientName, key.KeyId, key.Secret, key.QueuePublicId, key.Type, key.Scope, key.TenantPublicId, key.Origin,
                variables,
            }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine($"Minted {Describe(key)}.");
        Console.WriteLine();
        if (apiKey)
            Console.WriteLine($"  {variables[0]} = {key.Secret}");
        else
        {
            Console.WriteLine($"  {variables[0]} = {key.KeyId}");
            Console.WriteLine($"  {variables[1]} = {key.Secret}");
        }
        Console.WriteLine();
        Console.WriteLine("The secret is shown once and cannot be retrieved again — put it in your secret store now.");
        return ExitCodes.Success;
    }

    /// <summary>What the key is, in words: its type, id and where it publishes.</summary>
    private static string Describe(IngressSigningKey key)
        => $"{(key.Type == IngressKeyTypes.ApiKey ? "a publishing API key" : "an ingress signing key")}" +
           $"{(key.KeyId is null ? "" : $" {TerminalText.Line(key.KeyId)}")} for " +
           (key.Scope == "workspace" || key.QueuePublicId is null
               ? $"every queue in workspace {TerminalText.Line(key.TenantPublicId ?? "?")}"
               : $"queue {TerminalText.Line(key.QueuePublicId)}") +
           (key.Origin is { } origin ? $", minted by {TerminalText.Line(origin)}" : "");

    /// <summary>
    /// keys mint without <c>--write</c>: nothing is minted. The variables the app needs, the targets <c>--write</c> takes, and
    /// the console page where a person makes or looks at the key.
    /// </summary>
    private static int Guide(bool json, ResolvedConfig config, string? tenant, string? queue, string type, string[] variables)
    {
        string suggested = SecretTarget.SuggestedFor(Directory.GetCurrentDirectory());
        string? console = ConsolePages.Security(config, tenant, queue);
        string command = $"queuey keys mint{(queue is null ? "" : $" --queue {queue}")}{(type == IngressKeyTypes.Signing ? "" : $" --type {type}")} --write {suggested}";
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                minted = false,
                type,
                variables,
                write = new[] { ".env", SecretTarget.UserSecretsWord },
                suggested = command,
                consoleUrl = console,
            }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine($"Nothing was minted. The app needs {string.Join(" and ", variables)}.");
        // Kommandoen har id-er, og lenken en vert fra innloggingen: begge går gjennom TerminalText (security-review av #69, K3).
        Console.WriteLine($"  To mint the key and put it where the app reads it, without showing it: {TerminalText.Line(command)}");
        Console.WriteLine("  --write takes .env (or another file git ignores) or user-secrets (the .NET project's user secrets).");
        if (console is not null)
            Console.WriteLine($"  Or a person makes or looks at the key in the console: {TerminalText.Line(console)}");
        Console.WriteLine("  --show-secret prints it here instead, once.");
        return ExitCodes.Success;
    }

    /// <summary>Queuey refused the mint until a person makes the key (403 <c>approval_required</c>): exit 5, with the page.</summary>
    private static int ApprovalRequired(bool json, QueueyForbiddenException refused)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "approval_required",
                message = refused.Message,
                action = refused.SuggestedAction,
            }, CliHost.JsonOut));
            return ExitCodes.PendingApproval;
        }

        Console.WriteLine("Nothing was minted: a person makes this key.");
        Console.WriteLine($"  {TerminalText.Line(refused.Message)}");
        if (refused.SuggestedAction is { } action)
            Console.WriteLine($"  → {TerminalText.Line(action)}");
        return ExitCodes.PendingApproval;
    }

    /// <summary>The key in the target, and what was written, without the secret.</summary>
    private static int Written(ArgMap map, IngressSigningKey key, SecretTarget target, IReadOnlyList<(string Name, string Value)> values,
        string? workspaceFrom = null)
    {
        IReadOnlyDictionary<string, string?> previous;
        string? tightenedFrom;
        try
        {
            (previous, tightenedFrom) = target.Write(values);
        }
        catch (CliFileException ex)
        {
            // Nøkkelen finnes, men hemmeligheten er tapt: den vises ikke her heller. Den trekkes tilbake, og en ny mintes.
            return CliErrors.Write(map.Has("json"), ex.Code,
                $"Minted key {key.KeyId}, but could not write it to {target.Shown}: {ex.Message} The secret is not shown.",
                key.KeyId is { } id && IsSigningKeyId(id)
                    ? $"Revoke it with queuey keys revoke {id}, fix where it goes, and mint again."
                    : "Revoke it in the Queuey console, fix where it goes, and mint again.",
                status: null, ExitCodes.Configuration, "Error");
        }

        string[] names = values.Select(v => v.Name).ToArray();
        // Bare en verdi med formen til en signerings-id vises (security-review av #69, R2-3): noe annet kan være en hemmelighet.
        string? replaced = key.Type != IngressKeyTypes.ApiKey && previous.TryGetValue(names[0], out string? old) && old is not null && old != key.KeyId
                           && IsSigningKeyId(old)
            ? old
            : null;
        // Security-review av #69 (K2): en API-nøkkel som sto der, publiserer fortsatt til den trekkes tilbake. Den vises maskert.
        string? replacedApiKey = key.Type == IngressKeyTypes.ApiKey && previous.TryGetValue(names[0], out string? oldKey) && oldKey is not null
                                 && oldKey != key.Secret
            ? CliHost.MaskKey(oldKey)
            : null;

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                key.ClientPublicId, key.ClientName, key.KeyId, key.QueuePublicId, key.Type, key.Scope, key.TenantPublicId, key.Origin,
                workspaceFrom,
                file = target.Kind == "file" ? map.Get("write") : null,
                target = target.Kind,
                written = target.Shown,
                variables = names,
                replacedKeyId = replaced,
                replacedApiKey,
                // Security-review av #67 (BØR A): en fil er alltid 0600 etterpå; her står modusen den hadde, når den ble strammet.
                mode = target.Kind == "file" ? "0600" : null,
                tightenedFrom,
            }, CliHost.JsonOut));
        }
        else
        {
            Console.WriteLine($"Minted {Describe(key)}.");
            Console.WriteLine($"Wrote {string.Join(" and ", names)} to {target.Shown}. The secret is not shown.");
            // Det SDK-en faktisk leser (security-review av #69): fra .env bare signeringsparet, i Development; QUEUEY_API_KEY fra miljøet.
            Console.WriteLine(target.Kind != "file"
                ? "The app reads them from its configuration, with QueueyOptions.UseSettings(key => configuration[key])."
                : key.Type == IngressKeyTypes.ApiKey
                    ? "The SDK reads QUEUEY_API_KEY from the environment, not from a file: load the file into the app's environment, "
                      + "or write to user-secrets instead."
                    : "A producer reads them with QueueyOptions.UseEnvironmentVariables(): from the environment, and from .env in Development.");
        }

        if (replaced is not null)
            Console.Error.WriteLine($"Note: {target.Shown} held key {TerminalText.Line(replaced)} before. It still verifies until it is revoked: " +
                                    $"queuey keys revoke {TerminalText.Line(replaced)}");
        if (replacedApiKey is not null)
            Console.Error.WriteLine($"Note: {target.Shown} held another QUEUEY_API_KEY ({TerminalText.Line(replacedApiKey)}) before. It may still " +
                                    "work, and it may reach the whole license, not one queue: revoke it in the Queuey console if nothing needs it.");
        if (tightenedFrom is not null)
            Console.Error.WriteLine($"Note: {target.Shown} had mode {tightenedFrom}; it is 0600 now, readable and writable only by you.");
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
        ResolvedConfig config = CliHost.Resolve(map, profiles: true);
        // Uten --queue: workspacets nøkler, de som publiserer til hver kø i det (Queuey #513).
        string? queue = string.IsNullOrWhiteSpace(map.Get("queue")) ? null : await QueueIdAsync(map, config);
        if (queue is null && string.IsNullOrWhiteSpace(config.TenantPublicId))
            return CliErrors.Configuration(map, "config_error", "keys list without --queue lists the workspace's keys, and none is named.",
                "Name it with --tenant, the profile's tenant, or QUEUEY_TENANT; or list one queue's with --queue <name|que_…>.");
        using ServiceProvider provider = CliHost.BuildProvider(config);
        if (provider.GetRequiredService<IQueueyService>().Management is not QueueyManagement management)
            return CliErrors.Write(map.Has("json"), "unsupported", "This build's Queuey client can't list keys.", null, status: null, ExitCodes.RuntimeError);

        IReadOnlyList<QueueHmacClientWireResponse> keys = queue is not null
            ? await management.ListIngressKeysAsync(queue)
            : await management.ListWorkspaceKeysAsync(config.TenantPublicId!);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                queuePublicId = queue,
                tenantPublicId = queue is null ? config.TenantPublicId : null,
                keys = keys.Select(k => new
                {
                    k.KeyId, k.ClientPublicId, k.ClientName, k.Scope, k.Origin, active = k.KeyIsActive && k.ClientIsActive,
                    k.LastUsedAtUtc, k.ExpiresAtUtc, k.RevokedAtUtc, k.RevokedReason,
                }),
            }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine($"{keys.Count} key(s) for {(queue is null ? $"workspace {config.TenantPublicId}" : queue)}:");
        foreach (QueueHmacClientWireResponse k in keys)
        {
            string state = k.RevokedAtUtc is { } revoked ? $"revoked {revoked.UtcDateTime:yyyy-MM-dd}"
                : k.KeyIsActive && k.ClientIsActive ? "active" : "inactive";
            string used = k.LastUsedAtUtc is { } last ? $"last used {last.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "never used";
            Console.WriteLine(TerminalText.Line($"  {k.KeyId}  {k.ClientName}  {(k.Scope is { } scope ? scope + ", " : "")}{state}, {used}"
                                                + (k.Origin is { } origin ? $", by {origin}" : "")));
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
        // Security-review av #67 (KAN D): id-en går i stien, og noe annet enn en nøkkel-id kan være en hemmelighet limt inn feil.
        if (!IsSigningKeyId(keyId))
            return CliErrors.Usage(map, "invalid_value", "keys revoke takes a signing key's id (hsk_…). The value is not shown, since it is not one.",
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
            Console.WriteLine($"Revoked signing key {keyId}: every producer that signs with it is refused at the ingress now.");
        return ExitCodes.Success;
    }

    /// <summary>Whether <paramref name="value"/> has the shape of a signing key id: <c>hsk_</c> and an id of letters and digits.</summary>
    internal static bool IsSigningKeyId(string? value)
        => value is { Length: > 4 and <= 64 } && value.StartsWith("hsk_", StringComparison.Ordinal)
           && value.Skip(4).All(char.IsAsciiLetterOrDigit);

    /// <summary>The queue's id: <c>--queue que_…</c> as given, or a name looked up in the configured workspace.</summary>
    private static async Task<string> QueueIdAsync(ArgMap map, ResolvedConfig config)
        => (await ListenCommand.ResolveTargetAsync(map, config)).Id;
}
