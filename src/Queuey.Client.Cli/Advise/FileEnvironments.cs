using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Advise;

// Før tag (etter #59, 2026-10-06): konflikten for flere profiler ba om miljøet som et profilnavn («local, production»), men
// environment tar bare dev, test, staging og prod. Og med prod oppgitt laget advise profiles.prod ved siden av production,
// med --profile prod. En profil velges derfor etter miljøet den gir, aldri etter navnet, og FlowAdvisor og designet spør
// her, så de velger likt. Som plan og apply leser en profil sin verdi for variabelen, ellers ${VAR:-standardverdien}.

/// <summary>
/// The workspace environment a deployment file that is there gives, and the profile a flow goes into: chosen by the
/// environment each profile gives, never by its name. A profile gives what it sets the variable the file's
/// <c>workspace.environment</c> comes from, else that variable's <c>${VAR:-default}</c>; a file with a fixed environment
/// gives that one with every profile.
/// </summary>
internal static class FileEnvironments
{
    /// <summary>The variable <c>workspace.environment</c> comes from, and its <c>${VAR:-default}</c>; null for a fixed one or none.</summary>
    internal static (string Name, string? Default)? Variable(ExistingDeployFile file) => ParseVariable(file.Environment);

    /// <summary>
    /// The variable <paramref name="text"/> is, and its <c>${VAR:-default}</c>, as plan and apply read it: only a text that
    /// is exactly one <c>${…}</c>. Null otherwise, also for <c>${A}x</c> or <c>${A}${B}</c>, which give no single variable.
    /// </summary>
    internal static (string Name, string? Default)? ParseVariable(string? text)
    {
        string? whole = text?.Trim();
        if (whole is null || !whole.StartsWith("${", StringComparison.Ordinal) || !whole.EndsWith("}", StringComparison.Ordinal)
            || whole.IndexOf('}') != whole.Length - 1 || whole.IndexOf("${", 2, StringComparison.Ordinal) >= 0)
            return null;

        string token = whole.Substring(2, whole.Length - 3);
        int separator = token.IndexOf(":-", StringComparison.Ordinal);
        string name = (separator < 0 ? token : token.Substring(0, separator)).Trim();
        return name.Length == 0 ? null : (name, separator < 0 ? null : token.Substring(separator + 2));
    }

    /// <summary>
    /// Whether <c>workspace.environment</c> reads variables in a form advise cannot follow, such as <c>${A}x</c> or
    /// <c>${A}${B}</c>: it is neither a fixed environment nor exactly one <c>${VAR}</c>.
    /// </summary>
    internal static bool Unreadable(ExistingDeployFile file)
        => file.Environment is { } environment && environment.IndexOf("${", StringComparison.Ordinal) >= 0 && Variable(file) is null;

    /// <summary>
    /// A value the file gives the environment that apply would refuse: where it is, and the value. A profile's value counts
    /// when it is not empty, as plan and apply read it; the <c>${VAR:-default}</c> only otherwise. Null when all are valid.
    /// </summary>
    // Review av #60: en ugyldig profilverdi falt tilbake på standardverdien, mens plan og apply bruker profilverdien og nekter.
    internal static (string Where, string Value)? Refused(ExistingDeployFile file)
    {
        if (Fixed(file) is not null || Variable(file) is not { } variable)
            return null;

        foreach (string profile in file.Profiles)
        {
            if (ProfileValue(file, profile, variable.Name) is { Length: > 0 } value && Valid(value) is null)
                return ($"profiles.{profile}.variables.{variable.Name}", value);
        }

        return variable.Default is { Length: > 0 } fallback && Valid(fallback) is null
            ? ($"the default in workspace.environment (${{{variable.Name}:-…}})", fallback)
            : null;
    }

    /// <summary>The fixed environment the file names, lower case; null for one from a variable, none, or one apply refuses.</summary>
    internal static string? Fixed(ExistingDeployFile file)
        => file.Environment is { } environment && environment.IndexOf("${", StringComparison.Ordinal) < 0 ? Valid(environment) : null;

    /// <summary>
    /// The environment the file gives with <paramref name="profile"/>: fixed, the profile's value when it is not empty, or
    /// else the default, as plan and apply read it; null when that is none, or one apply refuses (<see cref="Refused"/>).
    /// </summary>
    internal static string? GivenBy(ExistingDeployFile file, string profile)
    {
        if (Fixed(file) is { } fixedEnvironment)
            return fixedEnvironment;
        if (Variable(file) is not { } variable)
            return null;
        return ProfileValue(file, profile, variable.Name) is { Length: > 0 } value ? Valid(value) : Valid(variable.Default);
    }

    /// <summary>The environment the file gives without a profile: fixed, or the variable's default; else null.</summary>
    internal static string? WithoutProfile(ExistingDeployFile file)
        => Fixed(file) ?? (Variable(file) is { } variable ? Valid(variable.Default) : null);

    /// <summary>Where the environment <paramref name="profile"/> gives comes from, in words for a flow's evidence.</summary>
    internal static string Source(ExistingDeployFile file, string? profile)
    {
        if (Fixed(file) is { } fixedEnvironment)
            return $"the deployment file's workspace is {fixedEnvironment}";
        if (Variable(file) is not { } variable)
            return "the deployment file names no environment";
        return profile is not null && ProfileValue(file, profile, variable.Name) is { Length: > 0 } value && Valid(value) is { } given
            ? $"profiles.{profile}.variables.{variable.Name} is {given}"
            : $"${{{variable.Name}}} defaults to {Valid(variable.Default)}";
    }

    /// <summary>
    /// The profile a flow for <paramref name="environment"/> goes into: the one profile that gives it, or the file's only
    /// profile; null when there is none, or several give it and the flow has a conflict for it.
    /// </summary>
    internal static string? Choose(ExistingDeployFile file, string environment)
    {
        string[] giving = file.Profiles.Where(p => GivenBy(file, p) == environment).ToArray();
        return giving.Length == 1 ? giving[0] : file.Profiles.Count == 1 ? file.Profiles[0] : null;
    }

    /// <summary>The profiles and what each gives, for a conflict: <c>local (dev), production (prod), staging (no environment)</c>.</summary>
    internal static string Describe(ExistingDeployFile file)
        => string.Join(", ", file.Profiles.Select(p => $"{p} ({GivenBy(file, p) ?? "no environment"})"));

    /// <summary>The profiles and what each gives, as a conflict's found value.</summary>
    internal static JsonObject Found(ExistingDeployFile file)
    {
        var found = new JsonObject();
        foreach (string profile in file.Profiles)
            found[profile] = GivenBy(file, profile) is { } given ? JsonValue.Create(given) : null;
        return found;
    }

    private static string? Valid(string? value)
        => value?.Trim().ToLowerInvariant() is { } environment && DeploymentWorkspace.EnvironmentValues.Contains(environment, StringComparer.Ordinal)
            ? environment
            : null;

    /// <summary>A profile's value for a variable, read as the deployment file's reader does: property names in any casing.</summary>
    private static string? ProfileValue(ExistingDeployFile file, string profile, string variable)
        => Property(Property(Property(file.Json, "profiles") as JsonObject, profile, exact: true) as JsonObject, "variables") is JsonObject variables
           && variables[variable] is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : null;

    private static JsonNode? Property(JsonObject? obj, string name, bool exact = false)
    {
        if (obj is null)
            return null;
        if (obj.TryGetPropertyValue(name, out JsonNode? found) || exact)
            return found;
        return obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
