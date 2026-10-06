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
        "credentials set", flags: new[] { "json" }, values: new[] { "name", "from-env", "type", "key-id", "username", "profile" });

    internal static readonly CommandOptions ListOptions = new("credentials list", flags: new[] { "json" }, values: new[] { "profile" });

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
            $"Unknown credentials subcommand '{CliErrors.Shown(sub)}'. Expected 'set' or 'list'.", action: null, status: null, ExitCodes.Usage);

    private static async Task<int> SetAsync(string[] args)
    {
        if (!SetOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();

        string? name = map.Get("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return CliErrors.Usage(map, "missing_argument", "credentials set requires --name <name>.");

        // Queuey F2.3-review (2026-10-06): CLI-en laget navn dens egen deploy-fil avviser (ingress.signedRequest.credentialRef).
        // Samme regler som fila: et navn som ser ut som en hemmelighet, og formen. Sjekket før typen, miljøet og workspacet,
        // som andre bruksfeil. Navnet vises med høyst tre tegn, og ikke i det hele tatt når det ser ut som en hemmelighet.
        if (DeploymentCredentialNames.LooksLikeASecret(name!))
            return CliErrors.Usage(map, "invalid_value",
                "--name looks like a secret, not the name of a credential. Its value is not shown.",
                "Name the credential with letters, digits and . _ : @ / -, such as partner-key, and keep the secret in the "
                + "environment variable --from-env names.");
        if (!DeploymentCredentialNames.FitsShape(name))
            return CliErrors.Usage(map, "invalid_value",
                $"--name '{CliErrors.Shown(name!)}' can't name a credential: a name may only use letters, digits and . _ : @ / -, "
                + $"starting with a letter or digit, at most {DeploymentCredentialNames.MaxLength} characters.",
                "Choose a name of that shape, such as partner-key or stripe.whsec. A deployment file refers to the credential by it "
                + "(ingress.signedRequest.credentialRef).");

        // ApiKeyHeader by default: it is the type that pairs with `authMode: "ApiKey"`, which is what
        // a deployment file names most often. Checked here rather than at the server, because an
        // unknown type came back as a bare 400 with the useful half of the sentence stripped.
        // Typen vises bare når den er et typenavn med feil store og små bokstaver: `--type sk_live_…` skrev hemmeligheten
        // tilbake, i den ene kommandoen der brukeren håndterer en (re-review 2026-10-05). Sjekket før miljøet og workspacet,
        // som andre bruksfeil.
        string type = map.Get("type") ?? "ApiKeyHeader";
        if (Array.IndexOf(CredentialTypes, type) < 0)
        {
            string? spelled = CredentialTypes.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
            return CliErrors.Usage(map, "invalid_value",
                spelled is not null
                    ? $"Unknown credential type '{type}'. Did you mean {spelled}?"
                    : "--type is not a credential type. Its value is not shown, since it may be a secret.",
                $"Expected one of: {string.Join(", ", CredentialTypes)}. The secret itself is read from the environment " +
                "variable --from-env names.");
        }

        // The secret comes from an environment variable, never an argument: a command line lands in
        // shell history and in CI logs, and a delivery secret in either is a leak.
        string? fromEnv = map.Get("from-env");
        if (string.IsNullOrWhiteSpace(fromEnv))
            return CliErrors.Usage(map, "missing_argument",
                "credentials set requires --from-env <ENV_VAR> — the secret is read from the environment, "
                + "never passed as an argument (arguments land in shell history and CI logs).");

        // Navnet vises bare når det ser ut som et miljøvariabelnavn. `--from-env sk_test_…` ble skrevet tilbake som et navn
        // som ikke var satt, og hemmeligheten sto i feilen (review 2026-10-05).
        string? secret = Environment.GetEnvironmentVariable(fromEnv!);
        if (string.IsNullOrEmpty(secret))
            return CliErrors.Configuration(map, "config_error",
                LooksLikeAVariableName(fromEnv!)
                    ? $"Environment variable '{fromEnv}' is not set or is empty."
                    : "The environment variable --from-env names is not set or is empty.",
                "--from-env takes the name of an environment variable that holds the secret, such as PARTNER_KEY, never the secret itself.");

        // Med en profil (F2.7) lagres credentialen i workspacet profilen og deploy-fila navngir, det apply skriver til.
        ResolvedConfig config = ListenCommand.Connection(map);
        string? tenant = config.TenantPublicId;
        if (string.IsNullOrWhiteSpace(tenant))
            return CliErrors.Configuration(map, "config_error", "A tenant is required. Set --tenant, QUEUEY_TENANT, or tenant in queuey.json.");

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

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

        // Med en profil (F2.7) lagres credentialen i workspacet profilen og deploy-fila navngir, det apply skriver til.
        ResolvedConfig config = ListenCommand.Connection(map);
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

    /// <summary>An environment variable's name as it is usually written: capitals, digits and underscores.</summary>
    private static bool LooksLikeAVariableName(string name)
        => name.Length is > 0 and <= 64 && (name[0] is >= 'A' and <= 'Z' or '_')
           && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}
