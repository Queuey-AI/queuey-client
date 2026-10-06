using System;
using System.Linq;

namespace Queuey.Client.Waas;

// Én regel for hvordan en credential lagres, for advise i CLI-en og for plan, apply og verify her (før tag, 2026-10-06). F2.9:
// en verdi agenten ikke skal holde, limer en person inn på siden `credentials request` åpner, og playbookene fra F2.11 sier det
// samme. Før foreslo plan og apply `credentials set` også i prod, og advise hadde regelen for seg selv. Teksten bor her, så de
// ikke kan gli fra hverandre.
//
// Navnet står i en kommando en agent kan lime inn i et skall (review av #58, B1). Navn på leveranse-credentials sjekkes ikke
// mot noen form i fila, så `x;curl … | sh;#`, `$(…)`, `$PARTNER_KEY` eller `-h` kom rett inn i forslaget. Derfor går hvert navn
// gjennom CredentialNameRules.Showable her, der kommandoen bygges, som hos søsknene (planens merknad, applys advarsel, drift).

/// <summary>
/// How a credential a deployment file names is stored, as advise, plan, apply and verify suggest it. <c>queuey credentials
/// set</c> reads a value the caller holds from an environment variable. <c>queuey credentials request</c> prints a link where
/// a person pastes a value the caller never holds. A dev workspace stores what the caller holds, such as a provider CLI's test
/// secret. Every other environment asks a person, and so does a workspace whose environment is not known, which Queuey counts
/// as prod. A name goes into a command only in the shape a shell reads as it is (<see cref="CredentialNameRules.Showable"/>);
/// any other shows as <see cref="Placeholder"/>.
/// </summary>
internal sealed class CredentialStoring
{
    /// <summary>The type <c>credentials request</c> asks for without <c>--type</c>, as Queuey does.</summary>
    internal const string RequestDefaultType = "HmacSigning";

    /// <summary>What a command shows for a name it cannot carry, and what a caller passes for a name it does not know.</summary>
    internal const string Placeholder = "<NAME>";

    /// <summary>The sentence after a command that shows <see cref="Placeholder"/> for a name the file gives.</summary>
    // Re-review av #58: hintet skal ikke friste noen til å legge et passord i navnet. credentialRef navngir, og verdien bor i
    // credentialen.
    internal const string NotShown =
        "The name is not shown: a command carries only letters, digits and . _ : @ / -, starting with a letter or digit, and " +
        "nothing that looks like a secret. Rename the credential that way, or name a stored one by its cred_… id. " +
        "credentialRef names a credential, never its value; a variable that holds the name is written ${NAME}.";

    private const string VariablePlaceholder = "<VARIABLE>";

    private readonly string _profileFlag;

    /// <param name="environment">The workspace's environment (dev, test, staging or prod), or null when it is not known.</param>
    /// <param name="profile">The profile the commands take with <c>--profile</c>, or null for none.</param>
    /// <param name="tenant">
    /// The workspace the commands name with <c>--tenant</c> when there is no profile: without one, credentials goes to the
    /// configured workspace, not the deployment file's (review of #60). Taken only when it is a workspace id.
    /// </param>
    public CredentialStoring(string? environment, string? profile, string? tenant = null)
    {
        AsksAPerson = !string.Equals(environment?.Trim(), "dev", StringComparison.OrdinalIgnoreCase);
        _profileFlag = profile is not null ? $" --profile {profile}"
            : tenant is not null && WorkspaceIds.IsOne(tenant.Trim()) ? $" --tenant {tenant.Trim()}"
            : "";
    }

    /// <summary>What the commands add to reach the workspace: <c> --profile &lt;name&gt;</c>, <c> --tenant &lt;ten_…&gt;</c>, or nothing.</summary>
    internal string Connection => _profileFlag;

    /// <summary>
    /// For a file as plan and apply read it: its workspace's environment, the profile it was expanded for, and without one,
    /// the workspace it names, which apply writes to while credentials goes to the configured one.
    /// </summary>
    // Re-review av #60, runde 5: uten profil foreslo plan og apply credentials request uten --tenant. Den gikk da til det
    // konfigurerte workspacet, som kan være dev, og en person limte prod-hemmeligheten inn der. Profilen vinner i konstruktøren,
    // og bare en workspace-id blir et flagg, så en uutvidet ${VAR} gir ingenting.
    public static CredentialStoring For(DeploymentFile file) => new(file.Workspace?.Environment, file.ProfileName, file.Tenant);

    /// <summary>Whether a person pastes the value: everywhere but dev.</summary>
    public bool AsksAPerson { get; }

    /// <summary>
    /// <c>queuey credentials set</c>, which reads the value from <paramref name="variable"/>. An <c>HmacSigning</c> credential
    /// gets its name as its key id, as a request gives it; a type that is not known shows as <c>&lt;type&gt;</c>.
    /// </summary>
    public string Set(string name, string? type, string variable = VariablePlaceholder)
        => $"queuey credentials set{_profileFlag} --name {Shown(name)} --type {type ?? "<type>"}"
           + (type == RequestDefaultType ? $" --key-id {Shown(name)}" : "") + Username(type) + $" --from-env {Variable(variable)}";

    /// <summary><c>queuey credentials request</c>, with <c>--type</c> only when it is not <see cref="RequestDefaultType"/>.</summary>
    public string Request(string name, string? type)
        => $"queuey credentials request {Shown(name)}" + (type == RequestDefaultType ? "" : $" --type {type ?? "<type>"}")
           + Username(type) + _profileFlag;

    /// <summary>The command that stores it here: a request where a person pastes the value, set where the caller holds it.</summary>
    public string Store(string name, string? type, string variable = VariablePlaceholder)
        => AsksAPerson ? Request(name, type) : Set(name, type, variable);

    /// <summary>
    /// How to store it here, and the other way: sentences that each end with a period. Outside dev a person pastes the value;
    /// in dev the caller sets it from a variable that holds it. A name the commands cannot carry adds <see cref="NotShown"/>.
    /// </summary>
    public string HowToStore(string name, string? type, string variable = VariablePlaceholder)
    {
        string? shown = CredentialNameRules.Showable(name);
        string how = AsksAPerson
            ? $"A person pastes the value, so it never passes through you: {Request(name, type)} prints a link for them, and " +
              $"queuey credentials list{_profileFlag} --json lists {shown ?? "it"} once it is stored. A value that is yours to hold " +
              $"goes in with {Set(name, type, variable)} instead."
            : $"Run {Set(name, type, variable)} from a shell where {Variable(variable)} holds the value, and never print it. When " +
              $"the value is not yours to hold, a person pastes it instead: {Request(name, type)}.";
        return shown is null && name != Placeholder ? how + " " + NotShown : how;
    }

    /// <summary>The name as a command or a text may carry it (<see cref="CredentialNameRules.Showable"/>), or <see cref="Placeholder"/>.</summary>
    internal static string Shown(string? name) => CredentialNameRules.Showable(name) ?? Placeholder;

    /// <summary>The credential type a delivery's <c>authMode</c> sends, or null for one that sends none or is not known.</summary>
    public static string? TypeForAuthMode(string? authMode) => authMode?.Trim().ToLowerInvariant() switch
    {
        "apikey" => "ApiKeyHeader",
        "bearer" => "BearerToken",
        "basic" => "BasicPassword",
        "oauth2clientcredentials" => "OAuth2ClientSecret",
        _ => null,
    };

    // En ny BasicPassword trenger et brukernavn, med set som med request.
    private static string Username(string? type) => type == "BasicPassword" ? " --username <username>" : "";

    // --from-env tar navnet på en variabel, som et skall leser som det er: bokstaver, sifre og _, ikke et siffer først.
    private static string Variable(string variable)
        => variable.Length is > 0 and <= 128 && !char.IsDigit(variable[0]) && variable.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_'))
            ? variable
            : VariablePlaceholder;
}
