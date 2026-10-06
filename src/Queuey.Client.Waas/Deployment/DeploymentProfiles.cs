using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Queuey.Client.Waas;

// F2.7 (2026-10-06, Kenneth: «Gjør din anbefaling på --profile»): verdiene en deploy-fil tar per miljø, står i fila, i en
// profil per miljø, så en promotering er en PR. En profil gir verdier til fila sine ${VAR}-er: den enkleste formen som
// passer med utvidelsen som finnes (DeploymentVariables), og med `pull --as`, som skriver de verdiene som ikke reiser mellom
// workspacene, om til ${VAR}. Tilkoblingen (nøkkelen) står aldri her, men hos brukeren (~/.queuey/config.json i CLI-en).

/// <summary>
/// The values a deployment file takes in one environment: what its <c>${VAR}</c> references expand to with
/// <c>--profile &lt;name&gt;</c>. Never a secret: the file is committed, and the key that applies it lives with the user.
/// </summary>
public sealed class DeploymentProfile
{
    /// <summary>
    /// The value of each <c>${VAR}</c> in this environment, by variable name, such as <c>QUEUEY_TENANT</c>: a workspace id,
    /// a base URL, a delivery kind. A value is taken as it is written, never expanded again, and one that looks like a
    /// secret is refused. A variable the profile leaves out comes from the environment, as without a profile; one set in
    /// both, to different values, is an error.
    /// </summary>
    public Dictionary<string, string>? Variables { get; set; }
}

/// <summary>The rules for a deployment file's <c>profiles</c>, and the lookup a profile expands the file with.</summary>
internal static class DeploymentProfiles
{
    /// <summary>The shape of a profile name: what <c>--profile</c> takes, in the deployment file and in the user's connections.</summary>
    internal const string NamePattern = "^[a-z0-9][a-z0-9._-]{0,63}$";

    /// <summary>The shape of a variable name, as <c>${VAR}</c> takes it.</summary>
    internal const string VariableNamePattern = "^[A-Za-z_][A-Za-z0-9_]*$";

    // Queuey sine public id-er er tilfeldige av natur, men ikke hemmelige: en profil gir et workspace (ten_…) eller en lisens.
    // Anslaget for tilfeldige verdier ville nektet dem, så det gjelder ikke for dem. Kjente prefikser for hemmeligheter gjør det.
    private static readonly string[] PublicIdPrefixes = { "ten_", "lic_", "que_", "cred_" };

    /// <summary>
    /// True when <paramref name="name"/> has the shape of a profile name, and does not look like a key: a name is shown in
    /// errors and lists, and a key pasted where a name goes can have the shape (<c>qak_kid.secret</c>).
    /// </summary>
    internal static bool IsName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > 64 || !(IsLower(name[0]) || IsDigit(name[0])))
            return false;

        foreach (char c in name)
        {
            if (!(IsLower(c) || IsDigit(c) || c == '.' || c == '-' || c == '_'))
                return false;
        }

        return !LooksLikeASecret(name);
    }

    /// <summary>True when <paramref name="name"/> has the shape of a variable name, and does not look like a key.</summary>
    internal static bool IsVariableName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !(IsLetter(name![0]) || name[0] == '_'))
            return false;

        foreach (char c in name)
        {
            if (!(IsLetter(c) || IsDigit(c) || c == '_'))
                return false;
        }

        return !LooksLikeASecret(name);
    }

    private static bool LooksLikeASecret(string value) => CredentialNameRules.HasSecretPrefix(value) || CredentialNameRules.LooksRandom(value);

    /// <summary>
    /// Throws <see cref="QueueyConfigurationException"/> for a profile name or variable name out of shape, a value that
    /// refers to another variable, an empty one, or one that looks like a secret. A value is never shown; a name is shown
    /// only when it has its shape.
    /// </summary>
    internal static void Validate(Dictionary<string, DeploymentProfile>? profiles)
    {
        if (profiles is null)
            return;

        foreach (KeyValuePair<string, DeploymentProfile> entry in profiles)
        {
            if (!IsName(entry.Key))
                throw new QueueyConfigurationException(
                    "A profile in the deployment file has a name that is not a profile name: lowercase letters, digits, '.', '-' " +
                    "and '_', starting with a letter or digit, at most 64 characters, and not shaped like a key. The name is not shown.");

            string where = $"profiles.{entry.Key}";
            if (entry.Value?.Variables is not { } variables)
                continue;

            foreach (KeyValuePair<string, string> variable in variables)
            {
                if (!IsVariableName(variable.Key))
                    throw new QueueyConfigurationException(
                        $"{where}.variables has a name that is not a variable name: letters, digits and '_', not starting with a " +
                        "digit, as ${VAR} takes it, and not shaped like a key. The name is not shown.");

                // Samme regel som utvidelsen (DeploymentVariables): en variabel fila ikke får lese, kan ikke en profil gi heller.
                if (DeploymentVariables.RefusalOf(variable.Key) is { } refused)
                    throw new QueueyConfigurationException(
                        $"{where}.variables.{variable.Key} is a variable a deployment file may not read: {refused}. A profile cannot " +
                        "give it a value either.")
                    {
                        SuggestedAction = DeploymentVariables.RefusalAction,
                    };

                if (ValueRefusal(variable.Value) is { } why)
                    throw new QueueyConfigurationException($"{where}.variables.{variable.Key} {why} Its value is not shown.")
                    {
                        SuggestedAction = "A profile holds what varies between environments and is safe to commit: a workspace id, a " +
                                          "base URL, a delivery kind. Keep a secret in a credential (queuey credentials set) and name it.",
                    };
            }
        }
    }

    /// <summary>Why a profile may not hold <paramref name="value"/>, as the end of a sentence, or null when it may.</summary>
    private static string? ValueRefusal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "is empty: give it a value, or leave the variable out so it comes from the environment.";
        if (value!.IndexOf("${", StringComparison.Ordinal) >= 0)
            return "refers to a variable: a profile's value is taken as it is written, and is never expanded again.";

        string trimmed = value.Trim();
        if (CredentialNameRules.HasSecretPrefix(trimmed))
            return "starts like a secret.";
        if (!PublicIdPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal)) && CredentialNameRules.LooksRandom(trimmed))
            return "looks like a secret: it has a long stretch of hex or random text.";
        return null;
    }

    /// <summary>
    /// The profile <paramref name="name"/> in <paramref name="profiles"/>, after checking them all. Throws
    /// <see cref="QueueyConfigurationException"/> naming the profiles the file has, when it has no such one.
    /// </summary>
    internal static DeploymentProfile Find(Dictionary<string, DeploymentProfile>? profiles, string name)
    {
        Validate(profiles);
        if (profiles is not null && profiles.TryGetValue(name, out DeploymentProfile? profile))
            return profile ?? new DeploymentProfile();

        string has = profiles is { Count: > 0 }
            ? "Its profiles are " + string.Join(", ", profiles.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "."
            : "It has no profiles.";
        throw new QueueyConfigurationException(
            $"The deployment file has no profile '{name}', so --profile {name} has no values for it. {has}")
        {
            SuggestedAction = $"Add \"profiles\": {{ \"{name}\": {{ \"variables\": {{ … }} }} }} to the deployment file, with the values " +
                              "its ${VAR} references take in that environment.",
        };
    }

    /// <summary>
    /// The variable lookup for expanding a file with <paramref name="profile"/>: the profile's value, else the environment's.
    /// A variable both set, to different values, throws: neither is picked, as with a workspace two places name.
    /// </summary>
    internal static Func<string, string?> Lookup(string name, DeploymentProfile profile, Func<string, string?> environment)
        => variable =>
        {
            string? fromProfile = profile.Variables is not null && profile.Variables.TryGetValue(variable, out string? v) ? v : null;
            string? fromEnvironment = environment(variable);

            if (!string.IsNullOrEmpty(fromProfile) && !string.IsNullOrEmpty(fromEnvironment)
                && !string.Equals(fromProfile, fromEnvironment, StringComparison.Ordinal))
                throw new QueueyConfigurationException(
                    $"${{{variable}}} has a value in profile {name} and another in the environment, so neither is picked. " +
                    "The values are not shown.")
                {
                    SuggestedAction = $"Unset {variable} in the environment to use the profile's value, or run without --profile.",
                };

            return string.IsNullOrEmpty(fromProfile) ? fromEnvironment : fromProfile;
        };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// The <c>profiles</c> of a deployment file read as a JSON document, without the rest of it: for a command that reads
    /// only the workspace from the file, as <c>verify</c> does. Null when it has none.
    /// </summary>
    internal static Dictionary<string, DeploymentProfile>? ReadFrom(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        JsonProperty[] found = root.EnumerateObject()
            .Where(p => string.Equals(p.Name, "profiles", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (found.Length > 1)
            throw new QueueyConfigurationException("The deployment file names profiles more than once, and only the last would count. Keep one.");
        if (found.Length == 0 || found[0].Value.ValueKind == JsonValueKind.Null)
            return null;

        try
        {
            return found[0].Value.Deserialize<Dictionary<string, DeploymentProfile>>(ReadOptions);
        }
        catch (JsonException ex)
        {
            // Stien er relativ til profiles: $.<profil>.variables.<VAR>. Et navn uten den formen vises ikke (JsonErrorPaths).
            string? path = JsonErrorPaths.Mask(ex.Path, IsName, JsonErrorPaths.Fields("variables"), IsVariableName);
            throw new QueueyConfigurationException(
                $"Could not read the deployment file's profiles{(path is null ? "" : $" ({path})")}: each profile is " +
                "{ \"variables\": { \"NAME\": \"value\" } }, with text values. Nothing of it is shown.");
        }
    }

    /// <summary>What the error for a variable that is set nowhere adds, with a profile.</summary>
    internal static string NotSetHint(string name)
        => $"Profile {name} gives no value for it either: add it to profiles.{name}.variables in the deployment file.";

    private static bool IsLower(char c) => c >= 'a' && c <= 'z';
    private static bool IsLetter(char c) => IsLower(c) || (c >= 'A' && c <= 'Z');
    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
