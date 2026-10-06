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
/// <c>queuey credentials set|request|list</c> — the delivery secrets a deployment file refers to by name.
/// The value is written once and stored encrypted; it is never readable again, which is exactly what
/// lets <c>queuey.deploy.json</c> be committed. <c>request</c> asks a person to paste it in the console,
/// so whoever runs it — an agent, a script — never holds the value at all.
/// </summary>
internal static class CredentialsCommand
{
    internal static readonly CommandOptions SetOptions = new(
        "credentials set", flags: new[] { "json" }, values: new[] { "name", "from-env", "type", "key-id", "username", "profile" });

    internal static readonly CommandOptions ListOptions = new("credentials list", flags: new[] { "json" }, values: new[] { "profile" });

    // Queuey F2.9 (2026-10-06): navnet er argumentet. Et valg som hører til `set`, sier hva som gjelder i stedet.
    internal static readonly CommandOptions RequestOptions = new(
        "credentials request", flags: new[] { "json" }, values: new[] { "type", "key-id", "username", "profile" }, positionals: 1,
        hints: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "credentials request takes the name as its argument: queuey credentials request <name>.",
            ["from-env"] = "credentials request takes no value: a person pastes it in the Queuey console. To store a value you hold, "
                           + "use queuey credentials set --from-env.",
        });

    /// <summary>The JSON <c>credentials request --json</c> prints. Raised only for a change a reader must know about.</summary>
    internal const int RequestJsonSchemaVersion = 1;

    /// <summary>
    /// The type <c>credentials request</c> asks for without <c>--type</c>: a signing secret, which Queuey never sends as it is.
    /// The same default as Queuey's.
    /// </summary>
    internal const string RequestDefaultType = "HmacSigning";

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
            "request" => await RequestAsync(rest),
            "list" => await ListAsync(rest),
            "" or "-h" or "--help" or "help" => Help(),
            _ => Unknown(sub, rest),
        };
    }

    private static int Help() { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

    private static int Unknown(string sub, string[] rest)
        => CliErrors.Write(CliErrors.WantsJson(rest), "unknown_subcommand",
            $"Unknown credentials subcommand '{CliErrors.Shown(sub)}'. Expected 'set', 'request' or 'list'.", action: null, status: null, ExitCodes.Usage);

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
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                created.PublicId, created.Name, created.Type, created.KeyId,
                created.Version, created.Created, created.SecretReplaced, created.BoundWorkspace, created.BoundQueues,
            }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        // Queuey F2.9: et navn som finnes, får hemmeligheten som en ny versjon under samme id. En eldre Queuey sier ingenting
        // om det (Created er null), og da står meldingen som før. Verdien credentialen alt har, er ingen ny versjon (Queuey #446,
        // M2), og gjør en utløpt credential brukbar igjen (L2). Et utløp som ikke har passert, står (runde 3, L1), så meldingen
        // lover ikke «no expiry». Navn og typer kommer fra serveren, så linjene går gjennom
        // TerminalText (F2.7-regelen, review av queuey-client#54, L3).
        Console.WriteLine(TerminalText.Line(created.Created == false
            ? created.SecretReplaced == false
                ? $"'{created.Name}' ({created.Type}) already holds this value: its secret stays version {created.Version}, under the "
                  + "same id, and the credential is usable. An expiry that has not passed stays. The value is encrypted and can't be "
                  + "read back."
                : $"Replaced the secret of '{created.Name}' ({created.Type}): it holds version {created.Version} now, under the same "
                  + "id, so everything that refers to it uses the new value. The value is encrypted and can't be read back."
            : $"Stored '{created.Name}' ({created.Type}). Refer to it as credentialRef \"{created.Name}\" — "
              + "the value is encrypted and can't be read back."));
        WriteBinding(created.BoundWorkspace == true, created.BoundQueues);

        return ExitCodes.Success;
    }

    private static async Task<int> RequestAsync(string[] args)
    {
        if (!RequestOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();

        string? name = map.FirstPositional?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return CliErrors.Usage(map, "missing_argument", "credentials request requires a name: queuey credentials request <name>.",
                "Give the name a deployment file's credentialRef uses, such as stripe-whsec.");

        // Samme regler som `set` og deploy-fila (Queuey F2.3): et navn som ser ut som en hemmelighet, vises ikke, og et navn
        // utenfor formen vises med høyst tre tegn. Sjekket før noe sendes.
        if (DeploymentCredentialNames.LooksLikeASecret(name!))
            return CliErrors.Usage(map, "invalid_value",
                "The name looks like a secret, not the name of a credential. Its value is not shown.",
                "Name the credential with letters, digits and . _ : @ / -, such as partner-key. The secret itself is pasted by a "
                + "person on the page the request opens, and never passes through this command.");
        if (!DeploymentCredentialNames.FitsShape(name))
            return CliErrors.Usage(map, "invalid_value",
                $"'{CliErrors.Shown(name!)}' can't name a credential: a name may only use letters, digits and . _ : @ / -, "
                + $"starting with a letter or digit, at most {DeploymentCredentialNames.MaxLength} characters.",
                "Choose a name of that shape, such as stripe-whsec. A deployment file refers to the credential by it "
                + "(ingress.signedRequest.credentialRef).");

        // Typen vises bare når den er et typenavn med feil store og små bokstaver, som for `set`. Uten --type ber kommandoen
        // om en signeringshemmelighet (Queuey F2.9-review, M5): den sendes aldri som den er, så en nøkkel som spør, kan ikke
        // peke en køs auth mot en mottaker den selv har. En hemmelighet Queuey sender som den er, bes om med --type.
        string type = map.Get("type") ?? RequestDefaultType;
        if (Array.IndexOf(CredentialTypes, type) < 0)
        {
            string? spelled = CredentialTypes.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
            return CliErrors.Usage(map, "invalid_value",
                spelled is not null
                    ? $"Unknown credential type '{type}'. Did you mean {spelled}?"
                    : "--type is not a credential type. Its value is not shown, since it may be a secret.",
                $"Expected one of: {string.Join(", ", CredentialTypes.Where(t => t != "OAuth2Certificate"))}.");
        }
        // Et sertifikat er en fil og passfrasen dens, som konsollet lagrer sammen ved opplasting; siden for en forespørsel tar én
        // innlimt verdi (Queuey F2.9-review, L6).
        if (type == "OAuth2Certificate")
            return CliErrors.Usage(map, "invalid_value",
                "An OAuth2Certificate credential is a certificate file and its passphrase, which the Queuey console stores from an "
                + "upload, and a request's page takes one pasted value.",
                "Upload the certificate where the queue's delivery auth is set in the Queuey console, or ask for another type.");

        ResolvedConfig config = ListenCommand.Connection(map);
        string? tenant = config.TenantPublicId;
        if (string.IsNullOrWhiteSpace(tenant))
            return CliErrors.Configuration(map, "config_error", "A tenant is required. Set --tenant, QUEUEY_TENANT, or tenant in queuey.json.");

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        CredentialRequestResult request;
        try
        {
            request = await service.Management.RequestCredentialAsync(
                tenant!, name!, type, keyId: map.Get("key-id"), username: map.Get("username"));
        }
        catch (QueueyNotFoundException ex) when (ex.ErrorCode is null)
        {
            // En Queuey uten ruten svarer 404 uten feilkonvolutt. Et workspace som ikke finnes, har koden sin.
            return CliErrors.Write(map.Has("json"), "credential_requests_unsupported",
                "This Queuey takes no credential requests (it predates them).",
                $"Store the secret with queuey credentials set --name {name} --from-env <ENV_VAR>, from a shell that holds it.",
                status: 404, ExitCodes.RuntimeError, "Queuey error");
        }

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = RequestJsonSchemaVersion,
                request.RequestId,
                request.WorkspaceId,
                request.WorkspaceName,
                request.OrganizationName,
                request.Name,
                request.Type,
                request.KeyId,
                request.Username,
                request.Status,
                request.Url,
                request.ExpiresAt,
                request.ReplacesCredentialId,
            }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        // Review av queuey-client#54, L3: workspacets navn, og alt annet her som kommer fra serveren, går gjennom TerminalText
        // (F2.7-regelen). Serveren sjekker ikke navnet for kontrolltegn, og en ANSI-sekvens der kunne skjult eller endret linjen
        // med lenken.
        string workspace = request.WorkspaceName is { Length: > 0 } wsName ? $"{wsName} ({request.WorkspaceId ?? tenant})" : request.WorkspaceId ?? tenant!;
        Console.WriteLine(TerminalText.Line($"Asked for the secret of '{request.Name}' ({request.Type}) in workspace {workspace}."));
        string until = request.ExpiresAt is { } expires ? $", until {expires.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "";
        if (request.Url is { Length: > 0 } url)
        {
            Console.WriteLine($"Hand this link to a person who can manage the workspace's credentials. They sign in to Queuey and "
                              + $"paste the value there, once{until}:");
            Console.WriteLine();
            Console.WriteLine($"  {TerminalText.Line(url)}");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine(TerminalText.Line($"This Queuey has no console address to link to. A person who can manage the workspace's "
                                                + $"credentials opens request {request.RequestId} in the Queuey console and pastes the value "
                                                + $"there, once{until}."));
        }

        if (request.ReplacesCredentialId is { Length: > 0 } replaces)
            Console.WriteLine(TerminalText.Line($"A credential is stored under this name ({replaces}): the value replaces its secret as a "
                                                + "new version, under the same id and with the key id and username it has."));
        // Queuey F2.9-review, M5: det som holder. En nøkkel med queue.write kunne peke en køs auth mot en mottaker den har, så
        // teksten lover ikke at den som spurte, aldri kan få verdien.
        Console.WriteLine("The value is stored encrypted. No API returns it, it never passes through this terminal or a conversation, "
                          + "and Queuey uses it only where the workspace's configuration does.");
        return ExitCodes.Success;
    }

    // Queuey F2.9: et navn ingressen ventet på (F2.3), bindes når credentialen lagres. En eldre Queuey sier ingenting, og da
    // gjør neste apply det.
    private static void WriteBinding(bool workspace, IReadOnlyList<string>? queues)
    {
        if (workspace)
            Console.WriteLine("The workspace's ingress waited for this name, and verifies with it now.");
        if (queues is { Count: > 0 })
            Console.WriteLine(TerminalText.Line($"The ingress of {string.Join(", ", queues)} waited for this name, and verifies with it now."));
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

        // Navn, typer og key id-er kommer fra serveren: hver verdi går gjennom TerminalText, og tabulatorene mellom dem står.
        Console.WriteLine(TerminalText.Line($"{creds.Count} credential(s) in {tenant}"));
        foreach (CredentialResult c in creds)
            Console.WriteLine($"  {TerminalText.Line(c.Name)}\t{TerminalText.Line(c.Type)}"
                              + (string.IsNullOrEmpty(c.KeyId) ? "" : $"\tkeyId={TerminalText.Line(c.KeyId)}"));

        return ExitCodes.Success;
    }

    /// <summary>An environment variable's name as it is usually written: capitals, digits and underscores.</summary>
    private static bool LooksLikeAVariableName(string name)
        => name.Length is > 0 and <= 64 && (name[0] is >= 'A' and <= 'Z' or '_')
           && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}
