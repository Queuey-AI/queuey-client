using System;

namespace Queuey.Client.Waas;

// Én regel for hvordan en credential lagres, for advise i CLI-en og for plan og apply her (før tag, 2026-10-06). F2.9: en
// verdi agenten ikke skal holde, limer en person inn på siden `credentials request` åpner, og playbookene fra F2.11 sier det
// samme. Før foreslo plan og apply `credentials set` også i prod, og advise hadde regelen for seg selv. Teksten bor her, så
// de tre ikke kan gli fra hverandre.

/// <summary>
/// How a credential a deployment file names is stored, as advise, plan and apply suggest it. <c>queuey credentials set</c>
/// reads a value the caller holds from an environment variable. <c>queuey credentials request</c> prints a link where a
/// person pastes a value the caller never holds. A dev workspace stores what the caller holds, such as a provider CLI's test
/// secret. Every other environment asks a person, and so does a workspace whose environment is not known, which Queuey
/// counts as prod.
/// </summary>
internal sealed class CredentialStoring
{
    /// <summary>The type <c>credentials request</c> asks for without <c>--type</c>, as Queuey does.</summary>
    internal const string RequestDefaultType = "HmacSigning";

    private readonly string _profileFlag;

    /// <param name="environment">The workspace's environment (dev, test, staging or prod), or null when it is not known.</param>
    /// <param name="profile">The profile the commands take with <c>--profile</c>, or null for none.</param>
    public CredentialStoring(string? environment, string? profile)
    {
        AsksAPerson = !string.Equals(environment?.Trim(), "dev", StringComparison.OrdinalIgnoreCase);
        _profileFlag = profile is null ? "" : $" --profile {profile}";
    }

    /// <summary>For a file as plan and apply read it: its workspace's environment, and the profile it was expanded for.</summary>
    public static CredentialStoring For(DeploymentFile file) => new(file.Workspace?.Environment, file.ProfileName);

    /// <summary>Whether a person pastes the value: everywhere but dev.</summary>
    public bool AsksAPerson { get; }

    /// <summary>
    /// <c>queuey credentials set</c>, which reads the value from <paramref name="variable"/>. An <c>HmacSigning</c> credential
    /// gets its name as its key id, as a request gives it; a type that is not known shows as <c>&lt;type&gt;</c>.
    /// </summary>
    public string Set(string name, string? type, string variable = "<VARIABLE>")
        => $"queuey credentials set{_profileFlag} --name {name} --type {type ?? "<type>"}"
           + (type == RequestDefaultType ? $" --key-id {name}" : "") + Username(type) + $" --from-env {variable}";

    /// <summary><c>queuey credentials request</c>, with <c>--type</c> only when it is not <see cref="RequestDefaultType"/>.</summary>
    public string Request(string name, string? type)
        => $"queuey credentials request {name}" + (type == RequestDefaultType ? "" : $" --type {type ?? "<type>"}") + Username(type)
           + _profileFlag;

    /// <summary>The command that stores it here: a request where a person pastes the value, set where the caller holds it.</summary>
    public string Store(string name, string? type, string variable = "<VARIABLE>")
        => AsksAPerson ? Request(name, type) : Set(name, type, variable);

    /// <summary>
    /// How to store it here, and the other way: sentences that each end with a period. Outside dev a person pastes the value;
    /// in dev the caller sets it from a variable that holds it.
    /// </summary>
    public string HowToStore(string name, string? type, string variable = "<VARIABLE>")
        => AsksAPerson
            ? $"A person pastes the value, so it never passes through you: {Request(name, type)} prints a link for them, and " +
              $"queuey credentials list{_profileFlag} --json lists {name} once it is stored. A value that is yours to hold goes " +
              $"in with {Set(name, type, variable)} instead."
            : $"Run {Set(name, type, variable)} from a shell where {variable} holds the value, and never print it. When the " +
              $"value is not yours to hold, a person pastes it instead: {Request(name, type)}.";

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
}
