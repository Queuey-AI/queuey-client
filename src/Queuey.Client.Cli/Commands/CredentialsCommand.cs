using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey credentials set|rotate|request|list</c> — the delivery secrets a deployment file refers to by name.
/// The value is written once and stored encrypted; it is never readable again, which is exactly what
/// lets <c>queuey.deploy.json</c> be committed. <c>request</c> asks a person to paste it in the console,
/// so whoever runs it — an agent, a script — never holds the value at all.
/// </summary>
internal static class CredentialsCommand
{
    // --replace (Queuey, besluttet av Kenneth 2026-10-06): en annen verdi for et navn workspacet har, bytter hemmeligheten bare
    // når den som kjører kommandoen, ber om det.
    internal static readonly CommandOptions SetOptions = new(
        "credentials set", flags: new[] { "json", "replace" }, values: new[] { "name", "from-env", "type", "key-id", "username", "profile" });

    internal static readonly CommandOptions ListOptions = new("credentials list", flags: new[] { "json" }, values: new[] { "profile" });

    internal static readonly CommandOptions GenerateOptions = new(
        "credentials generate", flags: new[] { "json", "replace" }, values: new[] { "write", "profile" }, positionals: 1);

    /// <summary>The variable <c>credentials generate</c> writes, the name the SDK reads for <c>QueueyDeliveryVerifier</c>.</summary>
    internal const string DeliverySecretVariable = QueueyEnvironmentVariables.DeliverySecret;

    /// <summary>How many random bytes a generated delivery secret has.</summary>
    internal const int GeneratedSecretBytes = 32;

    // Queuey F3.7 (besluttet 2026-10-07): rotasjonen er en operasjon (rotate_credential). Navnet og verdien som for `set`, og
    // et vindu bare når --grace er gitt. Typen er credentialens egen, så --type, --key-id og --username hører til `set`.
    internal static readonly CommandOptions RotateOptions = new(
        "credentials rotate", flags: new[] { "json" }, values: new[] { "name", "from-env", "grace", "expect-version", "profile" },
        hints: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["replace"] = "credentials rotate always replaces the secret: it needs no --replace.",
            ["type"] = "credentials rotate keeps the credential's type. To store a credential of another type, use credentials set under another name.",
            ["key-id"] = "credentials rotate replaces only the secret. To change the key id, use credentials set --key-id.",
            ["username"] = "credentials rotate replaces only the secret. To change the username, use credentials set --username.",
        });

    /// <summary>The longest grace window <c>credentials rotate --grace</c> takes, in minutes: a day, as Queuey's.</summary>
    internal const int MaxGraceMinutes = 24 * 60;

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
    /// The same default as Queuey's, and as the commands advise, plan and apply suggest (<see cref="CredentialStoring"/>).
    /// </summary>
    internal const string RequestDefaultType = CredentialStoring.RequestDefaultType;

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
            "generate" => await GenerateAsync(rest),
            "request" => await RequestAsync(rest),
            "rotate" => await RotateAsync(rest),
            "list" => await ListAsync(rest),
            "" or "-h" or "--help" or "help" => Help(),
            _ => Unknown(sub, rest),
        };
    }

    private static int Help() { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

    private static int Unknown(string sub, string[] rest)
        => CliErrors.Write(CliErrors.WantsJson(rest), "unknown_subcommand",
            $"Unknown credentials subcommand '{CliErrors.Shown(sub)}'. Expected 'set', 'generate', 'rotate', 'request' or 'list'.", action: null, status: null, ExitCodes.Usage);

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

        // Queuey (besluttet av Kenneth 2026-10-06, review av queuey-client#60): `set` byttet hemmeligheten til et navn workspacet
        // hadde, uten et ord. En agent som tok feil av miljøet, kunne kjøre `credentials set --profile prod --name stripe-whsec`
        // med Stripe CLI-ens testhemmelighet, og prods ingress ville avvist hvert ekte Stripe-event. Nå nekter Queuey en annen
        // verdi uten --replace, og feilen sier hva et bytte gjør. Samme verdi lagres som før, så en idempotent set i CI virker.
        CredentialResult created;
        try
        {
            created = await service.Management.CreateCredentialAsync(
                tenant!, name!, type, secret,
                keyId: map.Get("key-id"), username: map.Get("username"), replace: map.Has("replace"));
        }
        catch (CredentialRotationPendingException pending)
        {
            // Security-review av #69 (K5): 202 er ingen lagret verdi.
            return GeneratePending(map.Has("json"), pending);
        }
        catch (QueueyConflictException ex) when (ex.ErrorCode == "credential_exists")
        {
            // Navnet er sjekket over (FitsShape, ikke en hemmelighet), og går likevel gjennom Showable og TerminalText (CliErrors).
            // Et bytte er en persons beslutning (review av queuey-client#60): teksten sier det, så en agent ikke bare legger til
            // --replace og prøver igjen.
            string holder = CredentialNameRules.Showable(name) is { } shown ? $"'{shown}'" : "The name";
            return CliErrors.Write(map.Has("json"), "credential_exists",
                $"{holder} already holds a different secret, used by every queue and ingress that names it.",
                "Replacing it is a decision for a person: if that is intended, run again with --replace; to keep it, store the new "
                + "value under another name.",
                status: 409, ExitCodes.RuntimeError, "Queuey error");
        }

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

    /// <summary>
    /// The target already holds a delivery secret, and no <c>--replace</c>: nothing is stored or written. The answer says what
    /// <c>--replace</c> would hit, from whether Queuey holds a credential of that name. The list gives only names, which is enough.
    /// </summary>
    // Security-review av #69 (R2-1): uten dette sendte nektelsen agenten rett til --replace, uten å si hva det rammer i Queuey.
    private static async Task<int> LocalSecretExists(bool json, IQueueyService service, string tenant, string name, string holder, SecretTarget target)
    {
        bool? inQueuey;
        try
        {
            IReadOnlyList<CredentialResult> credentials = await service.Management.ListCredentialsAsync(tenant);
            inQueuey = credentials.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            inQueuey = null;
        }

        const string Another = "the value here may belong to another credential whose deliveries then fail.";
        string replace = inQueuey switch
        {
            true => $"Queuey holds {holder}. --replace also replaces it in Queuey, for every queue and ingress that names it, and " + Another,
            false => $"Queuey holds no credential named {holder}, so --replace replaces only the value here, and stores the new one in "
                     + "Queuey under that name; " + Another,
            null => $"Could not check whether Queuey holds {holder}. If it does, --replace also replaces it in Queuey, for every queue "
                    + "and ingress that names it; either way, " + Another,
        };
        return CliErrors.Write(json, "secret_exists_locally",
            $"{target.Shown} already holds {DeliverySecretVariable}, the secret a receiver here verifies deliveries with. Nothing was " +
            "stored or written.",
            replace + " Replacing it is a decision for a person; to keep it, write to another target.",
            status: null, ExitCodes.Configuration, "Error",
            new Dictionary<string, object?> { ["inQueuey"] = inQueuey });
    }

    /// <summary>
    /// <c>credentials generate &lt;name&gt; --write &lt;target&gt;</c>: the receiver's secret for <c>QueueyDeliveryVerifier</c>, made here,
    /// stored in Queuey as the credential Queuey signs deliveries with, and written where the receiver reads it as
    /// <c>QUEUEY_DELIVERY_SECRET</c>. The same value in both places, never shown.
    /// </summary>
    // Kenneth 2026-10-09: den lokale verdien skal alltid være den samme som i Queuey, for mottakeren som for avsenderen.
    private static async Task<int> GenerateAsync(string[] args)
    {
        if (!GenerateOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();
        bool json = map.Has("json");

        string? name = map.FirstPositional?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return CliErrors.Usage(map, "missing_argument", "credentials generate requires a name: queuey credentials generate <name> --write <target>.",
                "Give the name the queue's delivery signing will refer to, such as orders-signing.");
        if (DeploymentCredentialNames.LooksLikeASecret(name!))
            return CliErrors.Usage(map, "invalid_value", "The name looks like a secret, not the name of a credential. Its value is not shown.",
                "Name the credential with letters, digits and . _ : @ / -, such as orders-signing.");
        if (!DeploymentCredentialNames.FitsShape(name))
            return CliErrors.Usage(map, "invalid_value",
                $"'{CliErrors.Shown(name!)}' can't name a credential: letters, digits and . _ : @ / -, starting with a letter or digit, " +
                $"at most {DeploymentCredentialNames.MaxLength} characters.", "Choose a name of that shape, such as orders-signing.");

        // Uten et sted å skrive den, lages ingen hemmelighet: verdien vises aldri, så en som ikke er lagret lokalt, er tapt.
        if (string.IsNullOrWhiteSpace(map.Get("write")))
            return CliErrors.Usage(map, "missing_argument",
                "credentials generate requires --write <target>: where the receiver reads the secret. It is never shown.",
                $"--write {SecretTarget.SuggestedFor(Directory.GetCurrentDirectory())} fits this folder; --write takes .env (or another file git ignores) or user-secrets.");
        SecretTarget target = SecretTarget.Parse(map.Get("write")!);

        // Security-review av #69 (B1): målet leses før noe lagres i Queuey. En annen verdi der er hemmeligheten en mottaker her
        // verifiserer med; å bytte den er et valg, som --replace gjør uttrykkelig, for Queuey og for målet.
        bool localExists = target.Current(DeliverySecretVariable).ContainsKey(DeliverySecretVariable);

        (ResolvedConfig config, string? workspaceFrom) = DeploymentTenant.OrFromDeploymentFile(ListenCommand.Connection(map), map);
        if (workspaceFrom is not null)
            Console.Error.WriteLine($"Workspace {config.TenantPublicId} from {workspaceFrom}.");
        string? tenant = config.TenantPublicId;
        if (string.IsNullOrWhiteSpace(tenant))
            return CliErrors.Configuration(map, "config_error", "A workspace is required. Set --tenant, QUEUEY_TENANT, the profile's tenant, " +
                "tenant in queuey.json, or tenant in queuey.deploy.json.");

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();
        string holder = CredentialNameRules.Showable(name) is { } shownName ? $"'{shownName}'" : "The credential";

        if (localExists && !map.Has("replace"))
            return await LocalSecretExists(json, service, tenant!, name!, holder, target);

        string secret = NewSecret();

        // Som `credentials set` med HmacSigning, og navnet som key id: det Queuey signerer leveringene med.
        CredentialResult stored;
        try
        {
            stored = await service.Management.CreateCredentialAsync(tenant!, name!, "HmacSigning", secret, keyId: name, username: null,
                replace: map.Has("replace"));
        }
        catch (CredentialRotationPendingException pending)
        {
            // Security-review av #69 (K5): en person lagrer den. Ingenting er lagret, og ingenting skrives her.
            return GeneratePending(json, pending);
        }
        catch (QueueyConflictException ex) when (ex.ErrorCode == "credential_exists")
        {
            // Samme ordlyd som `credentials set` (security-review av #69, B3): et bytte rammer alt som navngir den.
            return CliErrors.Write(json, "credential_exists",
                $"{holder} already holds a different secret, used by every queue and ingress that names it. Nothing was written here.",
                "Replacing it is a decision for a person: if that is intended, run again with --replace, which makes a new value for "
                + "Queuey and for the receiver; to keep it, choose another name.",
                status: 409, ExitCodes.RuntimeError, "Queuey error");
        }

        // Security-review av #69 (R2-4): uten en id i svaret har Queuey ikke bekreftet at den er lagret, og da skrives
        // ingenting her. Ellers kunne mottakeren fått en verdi Queuey aldri signerer med.
        if (string.IsNullOrWhiteSpace(stored.PublicId))
            return CliErrors.Write(json, "credential_unconfirmed",
                $"Queuey's answer did not confirm that it stored {holder}: it carried no id. Nothing was written to {target.Shown}, " +
                "and the secret is not shown.",
                $"Check with queuey credentials list. If {holder} is there, run queuey credentials generate {name} --replace --write " +
                $"{map.Get("write")}, which makes a new secret for both.",
                status: null, ExitCodes.RuntimeError, "Queuey error");

        bool replacedInQueuey = stored.Created == false && stored.SecretReplaced != false;
        string? tightenedFrom;
        try
        {
            (_, tightenedFrom) = target.Write(new[] { (DeliverySecretVariable, secret) });
        }
        catch (CliFileException ex)
        {
            // Lagret i Queuey, men ikke her: verdien er tapt, siden den aldri vises. En ny generate med --replace lager en ny for begge.
            return CliErrors.Write(json, ex.Code,
                (replacedInQueuey || localExists
                    ? $"Queuey now has the new secret for {holder}; the receiver still has the old one and will reject deliveries until it " +
                      $"has the new one. Could not write it to {target.Shown}: {ex.Message}"
                    : $"Stored {holder} in Queuey, but could not write it to {target.Shown}: {ex.Message}") + " The secret is not shown.",
                $"Fix where it goes, and run queuey credentials generate {name} --replace --write {map.Get("write")}, which makes a new " +
                "secret for both.", status: null, ExitCodes.Configuration, "Error");
        }

        string reference = $"\"delivery\": {{ \"signing\": {{ \"enabled\": true, \"credentialRef\": \"{stored.Name}\" }} }}";
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                stored.PublicId, stored.Name, stored.Type, stored.KeyId, stored.Version, stored.Created, stored.SecretReplaced,
                stored.BoundWorkspace, stored.BoundQueues,
                target = target.Kind,
                written = target.Shown,
                variable = DeliverySecretVariable,
                replacedLocally = localExists,
                tightenedFrom,
                workspaceFrom,
                deliverySigning = new { enabled = true, credentialRef = stored.Name },
            }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine(TerminalText.Line(replacedInQueuey
            ? $"Generated a new secret for '{stored.Name}' (HmacSigning): it replaced the one in Queuey, version {stored.Version} now, used by "
              + "every queue and ingress that names it, and the previous one stopped verifying."
            : $"Generated the delivery secret '{stored.Name}' and stored it in Queuey (HmacSigning)."));
        Console.WriteLine(TerminalText.Line($"Wrote it to {target.Shown} as {DeliverySecretVariable}{(localExists ? ", in place of the value there" : "")}. "
                                            + "The value is not shown."));
        WriteBinding(stored.BoundWorkspace == true, stored.BoundQueues);
        if (tightenedFrom is not null)
            Console.Error.WriteLine($"Note: {target.Shown} had mode {tightenedFrom}; it is 0600 now, readable and writable only by you.");
        Console.WriteLine(TerminalText.Line($"Point the queue's (or the workspace's) delivery at it in queuey.deploy.json, then apply: {reference}"));
        Console.WriteLine("The receiver verifies each delivery with QueueyDeliveryVerifier.FromEnvironment() in .NET.");
        return ExitCodes.Success;
    }

    /// <summary>A generate Queuey gave to a person (202): nothing stored, nothing written, and exit 5 with the link.</summary>
    private static int GeneratePending(bool json, CredentialRotationPendingException pending)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = CredentialRotationPendingException.PendingApproval,
                approvalUrl = pending.ApprovalUrl,
                credentialRequest = pending.CredentialRequest,
                expiresAt = pending.ExpiresAt,
                message = pending.Message,
            }, CliHost.JsonOut));
            return ExitCodes.PendingApproval;
        }

        Console.WriteLine("Nothing was stored or written: a person stores this secret.");
        if (pending.ApprovalUrl is { } url)
            Console.WriteLine($"  Give this link to a person who may manage the workspace's credentials: {TerminalText.Line(url)}");
        Console.Error.WriteLine(TerminalText.Line(pending.Message));
        return ExitCodes.PendingApproval;
    }

    /// <summary>A new delivery secret: <see cref="GeneratedSecretBytes"/> random bytes, base64url without padding.</summary>
    internal static string NewSecret()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(GeneratedSecretBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<int> RotateAsync(string[] args)
    {
        if (!RotateOptions.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) return Help();

        // Navnet sjekkes som for `set` og deploy-fila, før noe annet: et navn som ser ut som en hemmelighet, vises ikke.
        string? name = map.Get("name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return CliErrors.Usage(map, "missing_argument", "credentials rotate requires --name <name>.",
                "Give the name the workspace has the credential under, as a deployment file's credentialRef names it.");
        if (DeploymentCredentialNames.LooksLikeASecret(name!))
            return CliErrors.Usage(map, "invalid_value",
                "--name looks like a secret, not the name of a credential. Its value is not shown.",
                "Give the name the credential is stored under, such as stripe-whsec, and keep the new secret in the environment "
                + "variable --from-env names.");
        if (!DeploymentCredentialNames.FitsShape(name))
            return CliErrors.Usage(map, "invalid_value",
                $"--name '{CliErrors.Shown(name!)}' can't name a credential: a name may only use letters, digits and . _ : @ / -, "
                + $"starting with a letter or digit, at most {DeploymentCredentialNames.MaxLength} characters.",
                "Give the name the workspace has the credential under.");

        // Vinduet åpnes bare uttrykkelig, og høyst et døgn (Queuey F3.7). Sjekket før miljøet og workspacet, som andre bruksfeil.
        int? grace = null;
        if (map.Get("grace") is { } graceText)
        {
            if (!int.TryParse(graceText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minutes)
                || minutes < 1 || minutes > MaxGraceMinutes)
                return CliErrors.Usage(map, "invalid_value",
                    $"--grace takes whole minutes, 1 to {MaxGraceMinutes}: a grace window lasts at most a day.",
                    "Give the minutes the old secret should still verify, such as --grace 60, or leave --grace out to stop it at once.");
            grace = minutes;
        }

        // Versjonen kalleren vet den bytter (review av #63, K1): en rotasjon fra CI skriver da aldri over en rotasjon en person
        // gjorde imellom. Queuey nekter en annen versjon med credential_changed_meanwhile, og ingenting lagres.
        int? expectVersion = null;
        if (map.Get("expect-version") is { } versionText)
        {
            if (!int.TryParse(versionText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int version)
                || version < 1)
                return CliErrors.Usage(map, "invalid_value",
                    "--expect-version takes the version of the secret the credential holds now, a whole number from 1.",
                    "queuey credentials list shows each credential's version. Leave --expect-version out to rotate whatever version it holds.");
            expectVersion = version;
        }

        // Verdien kommer fra en miljøvariabel, aldri et argument, som for `set`.
        string? fromEnv = map.Get("from-env");
        if (string.IsNullOrWhiteSpace(fromEnv))
            return CliErrors.Usage(map, "missing_argument",
                "credentials rotate requires --from-env <ENV_VAR> — the new secret is read from the environment, "
                + "never passed as an argument (arguments land in shell history and CI logs).");
        string? secret = Environment.GetEnvironmentVariable(fromEnv!);
        if (string.IsNullOrEmpty(secret))
            return CliErrors.Configuration(map, "config_error",
                LooksLikeAVariableName(fromEnv!)
                    ? $"Environment variable '{fromEnv}' is not set or is empty."
                    : "The environment variable --from-env names is not set or is empty.",
                "--from-env takes the name of an environment variable that holds the secret, such as PARTNER_KEY, never the secret itself.");

        ResolvedConfig config = ListenCommand.Connection(map);
        string? tenant = config.TenantPublicId;
        if (string.IsNullOrWhiteSpace(tenant))
            return CliErrors.Configuration(map, "config_error", "A tenant is required. Set --tenant, QUEUEY_TENANT, or tenant in queuey.json.");

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();
        if (service.Management is not QueueyManagement management)
            return CliErrors.Write(map.Has("json"), "unsupported", "This build's Queuey client can't rotate credentials.", null,
                status: null, ExitCodes.RuntimeError, "Queuey error");

        string holder = CredentialNameRules.Showable(name) is { } shown ? $"'{shown}'" : "The credential";
        CredentialResult rotated;
        try
        {
            rotated = await management.RotateCredentialAsync(tenant!, name!, secret!, grace, expectVersion);
        }
        catch (QueueyNotFoundException ex) when (ex.ErrorCode is null)
        {
            // En Queuey uten ruten svarer 404 uten feilkonvolutt. En credential som ikke finnes, har koden sin.
            return CliErrors.Write(map.Has("json"), "credential_rotation_unsupported",
                "This Queuey can't rotate a credential (it predates rotations).",
                $"Replace the secret with queuey credentials set --name {name} --from-env {fromEnv} --replace, which opens no grace window.",
                status: 404, ExitCodes.RuntimeError, "Queuey error");
        }
        catch (CredentialRotationPendingException pending)
        {
            return RotationPending(map.Has("json"), pending);
        }
        catch (QueueyConflictException ex) when (ex.ErrorCode == "credential_changed_meanwhile")
        {
            return CliErrors.Write(map.Has("json"), "credential_changed_meanwhile",
                expectVersion is { } expected
                    ? $"{holder} does not hold version {expected} of its secret now, which --expect-version confirmed, and nothing was stored."
                    : $"{holder} was changed by someone else while it was rotated, and nothing was stored.",
                "Look at what changed with queuey credentials list, and rotate again if it is still intended.",
                status: 409, ExitCodes.RuntimeError, "Queuey error");
        }

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                rotated.PublicId, rotated.Name, rotated.Type, rotated.KeyId, rotated.Version, rotated.SecretReplaced,
                rotated.PreviousVersionValidUntil, rotated.GraceWindowClosed, rotated.BoundWorkspace, rotated.BoundQueues,
            }, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        // Navn og typer kommer fra serveren, så linjene går gjennom TerminalText (F2.7-regelen).
        // Samme verdi (review av #63, M1): linjen sier alltid hvordan det står med vinduet, så ingen tror en lekket gammel
        // hemmelighet er kuttet når den ikke er det. Queuey lukker et åpent vindu når --grace mangler (Queuey #462). En eldre
        // Queuey lot det stå, og da står tiden i svaret.
        if (rotated.SecretReplaced == false)
        {
            Console.WriteLine(TerminalText.Line(
                $"'{rotated.Name}' ({rotated.Type}) already holds this value: nothing was rotated, and its secret stays version "
                + $"{rotated.Version}."));
            Console.WriteLine(rotated.GraceWindowClosed == true
                ? "The grace window is closed: the previous secret stopped verifying at once."
                : rotated.PreviousVersionValidUntil is { } stillUntil
                    ? $"The grace window is still open: the previous secret verifies at the ingress until {stillUntil.UtcDateTime:yyyy-MM-dd HH:mm} "
                      + "UTC. " + (grace is null
                          ? "This Queuey keeps it open for the same value; to stop it now, rotate to a new value without --grace, or revoke the credential."
                          : "Run the same rotation without --grace to close it now.")
                    : "No grace window is open: only this value verifies.");
            return ExitCodes.Success;
        }

        Console.WriteLine(TerminalText.Line(
            $"Rotated the secret of '{rotated.Name}' ({rotated.Type}): it holds version {rotated.Version} now, under the same id, so "
            + "everything that refers to it uses the new value. The value is encrypted and can't be read back."));
        Console.WriteLine(rotated.PreviousVersionValidUntil is { } until
            ? $"The previous secret still verifies at the ingress until {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC, so senders can switch "
              + "over; each event records which version verified it. To stop it sooner, run the same rotation again without --grace: "
              + "the same value closes the window."
            : "The previous secret stopped verifying at once.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// A rotation Queuey gave to a person (202, Queuey #511): the link where they paste the value, and exit 5. The secret that
    /// was sent is never printed; Queuey did not keep it.
    /// </summary>
    private static int RotationPending(bool json, CredentialRotationPendingException pending)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = CredentialRotationPendingException.PendingApproval,
                approvalUrl = pending.ApprovalUrl,
                credentialRequest = pending.CredentialRequest,
                expiresAt = pending.ExpiresAt,
                policyRule = pending.PolicyRule,
                message = pending.Message,
            }, CliHost.JsonOut));
            return ExitCodes.PendingApproval;
        }

        // Teksten og lenken kommer fra serveren, så de går gjennom TerminalText (F2.7-regelen).
        Console.WriteLine("Nothing was rotated: a person has to paste the new value, and the value you sent was not kept.");
        Console.WriteLine(pending.ApprovalUrl is { } url
            ? $"  Give this link to a person who may manage the workspace's credentials: {TerminalText.Line(url)}"
            : $"  A person pastes it on credential request {TerminalText.Line(pending.CredentialRequest ?? "?")} in the Queuey console.");
        if (pending.ExpiresAt is { } expires)
            Console.WriteLine($"  The request expires at {expires.UtcDateTime:yyyy-MM-dd HH:mm} UTC. The rotation runs when they paste the value.");
        Console.Error.WriteLine(TerminalText.Line(pending.Message));
        return ExitCodes.PendingApproval;
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
            Console.WriteLine(JsonSerializer.Serialize(
                creds.Select(c => new { c.PublicId, c.Name, c.Type, c.KeyId, c.Version, c.PreviousVersionValidUntil }), CliHost.JsonOut));
            return ExitCodes.Success;
        }

        // Navn, typer og key id-er kommer fra serveren: hver verdi går gjennom TerminalText, og tabulatorene mellom dem står.
        // Versjonen er det `rotate --expect-version` tar, og et åpent vindu vises, så ingen tror en gammel hemmelighet er kuttet
        // (review av #63, Queuey F3.7).
        Console.WriteLine(TerminalText.Line($"{creds.Count} credential(s) in {tenant}"));
        foreach (CredentialResult c in creds)
            Console.WriteLine($"  {TerminalText.Line(c.Name)}\t{TerminalText.Line(c.Type)}"
                              + (string.IsNullOrEmpty(c.KeyId) ? "" : $"\tkeyId={TerminalText.Line(c.KeyId)}")
                              + (c.Version is { } version ? $"\tversion={version}" : "")
                              + (c.PreviousVersionValidUntil is { } until
                                  ? $"\tprevious version verifies until {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
                                  : ""));

        return ExitCodes.Success;
    }

    /// <summary>An environment variable's name as it is usually written: capitals, digits and underscores.</summary>
    private static bool LooksLikeAVariableName(string name)
        => name.Length is > 0 and <= 64 && (name[0] is >= 'A' and <= 'Z' or '_')
           && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}
