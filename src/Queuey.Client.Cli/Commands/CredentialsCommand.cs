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
/// <c>queuey credentials set|list</c> — the delivery secrets a deployment file refers to by name.
/// The value is written once and stored encrypted; it is never readable again, which is exactly what
/// lets <c>queuey.deploy.json</c> be committed.
/// </summary>
internal static class CredentialsCommand
{
    internal static readonly CommandOptions SetOptions = new(
        "credentials set", flags: new[] { "json" }, values: new[] { "name", "from-env", "type", "key-id", "username" });

    internal static readonly CommandOptions ListOptions = new("credentials list", flags: new[] { "json" });

    /// <summary>The credential types Queuey stores. Mirrors the server's <c>CredentialType</c>.</summary>
    private static readonly string[] CredentialTypes =
    {
        "ApiKeyHeader", "BearerToken", "BasicPassword", "HmacSigning",
        "OAuth2ClientSecret", "OAuth2Certificate",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        return sub switch
        {
            "set" => await SetAsync(rest),
            "list" => await ListAsync(rest),
            "" or "-h" or "--help" or "help" => Help(),
            _ => Unknown(sub, rest),
        };
    }

    private static int Help() { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

    private static int Unknown(string sub, string[] rest)
        => CliErrors.Write(CliErrors.WantsJson(rest), "unknown_subcommand",
            $"Unknown credentials subcommand '{sub}'. Expected 'set' or 'list'.", action: null, status: null, ExitCodes.Usage);

    private static async Task<int> SetAsync(string[] args)
    {
        if (!SetOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();

        string? name = map.Get("name");
        if (string.IsNullOrWhiteSpace(name))
            return CliErrors.Usage(map, "missing_argument", "credentials set requires --name <name>.");

        // The secret comes from an environment variable, never an argument: a command line lands in
        // shell history and in CI logs, and a delivery secret in either is a leak.
        string? fromEnv = map.Get("from-env");
        if (string.IsNullOrWhiteSpace(fromEnv))
            return CliErrors.Usage(map, "missing_argument",
                "credentials set requires --from-env <ENV_VAR> — the secret is read from the environment, "
                + "never passed as an argument (arguments land in shell history and CI logs).");

        string? secret = Environment.GetEnvironmentVariable(fromEnv!);
        if (string.IsNullOrEmpty(secret))
            return CliErrors.Configuration(map, "config_error", $"Environment variable '{fromEnv}' is not set or is empty.");

        ResolvedConfig config = CliHost.Resolve(map);
        string? tenant = config.TenantPublicId;
        if (string.IsNullOrWhiteSpace(tenant))
            return CliErrors.Configuration(map, "config_error", "A tenant is required. Set --tenant, QUEUEY_TENANT, or tenant in queuey.json.");

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        // ApiKeyHeader by default: it is the type that pairs with `authMode: "ApiKey"`, which is what
        // a deployment file names most often. Checked here rather than at the server, because an
        // unknown type came back as a bare 400 with the useful half of the sentence stripped.
        string type = map.Get("type") ?? "ApiKeyHeader";
        if (Array.IndexOf(CredentialTypes, type) < 0)
            return CliErrors.Usage(map, "invalid_value", $"Unknown credential type '{type}'. Expected one of: {string.Join(", ", CredentialTypes)}.");

        CredentialResult created = await service.Management.CreateCredentialAsync(
            tenant!, name!, type, secret,
            keyId: map.Get("key-id"), username: map.Get("username"));

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(new { created.PublicId, created.Name, created.Type, created.KeyId }, CliHost.JsonOut));
        else
            Console.WriteLine($"Stored '{created.Name}' ({created.Type}). Refer to it as credentialRef \"{created.Name}\" — "
                              + "the value is encrypted and can't be read back.");

        return ExitCodes.Success;
    }

    private static async Task<int> ListAsync(string[] args)
    {
        if (!ListOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();

        ResolvedConfig config = CliHost.Resolve(map);
        string? tenant = config.TenantPublicId;
        if (string.IsNullOrWhiteSpace(tenant))
            return CliErrors.Configuration(map, "config_error", "A tenant is required. Set --tenant, QUEUEY_TENANT, or tenant in queuey.json.");

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        IReadOnlyList<CredentialResult> creds = await service.Management.ListCredentialsAsync(tenant!);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(creds.Select(c => new { c.PublicId, c.Name, c.Type, c.KeyId }), CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine($"{creds.Count} credential(s) in {tenant}");
        foreach (CredentialResult c in creds)
            Console.WriteLine($"  {c.Name}\t{c.Type}{(string.IsNullOrEmpty(c.KeyId) ? "" : $"\tkeyId={c.KeyId}")}");

        return ExitCodes.Success;
    }
}
