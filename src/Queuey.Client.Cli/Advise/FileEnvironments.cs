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

    /// <summary>The first <c>${VAR}</c> in <paramref name="text"/>, and its <c>${VAR:-default}</c>, as plan and apply read it; else null.</summary>
    internal static (string Name, string? Default)? ParseVariable(string? text)
    {
        int start = text?.IndexOf("${", StringComparison.Ordinal) ?? -1;
        int end = start < 0 ? -1 : text!.IndexOf('}', start + 2);
        if (end < 0)
            return null;

        string token = text!.Substring(start + 2, end - start - 2);
        int separator = token.IndexOf(":-", StringComparison.Ordinal);
        string name = (separator < 0 ? token : token.Substring(0, separator)).Trim();
        return name.Length == 0 ? null : (name, separator < 0 ? null : token.Substring(separator + 2));
    }

    /// <summary>The fixed environment the file names, lower case; null for one from a variable, none, or one apply refuses.</summary>
    internal static string? Fixed(ExistingDeployFile file)
        => file.Environment is { } environment && environment.IndexOf("${", StringComparison.Ordinal) < 0 ? Valid(environment) : null;

    /// <summary>The environment the file gives with <paramref name="profile"/>: fixed, the profile's value, or the default; else null.</summary>
    internal static string? GivenBy(ExistingDeployFile file, string profile)
        => Fixed(file) ?? (Variable(file) is { } variable ? Valid(ProfileValue(file, profile, variable.Name)) ?? Valid(variable.Default) : null);

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
        return profile is not null && Valid(ProfileValue(file, profile, variable.Name)) is { } given
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
