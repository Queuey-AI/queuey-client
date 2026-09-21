using System.Reflection;

namespace Queuey.Client.Cli;

/// <summary>
/// The version <c>queuey --version</c> prints. It is the first thing an agent
/// runs after installing a tool, to see that the install worked — so it has to
/// exist and exit 0, or a good install reads as a failed one.
/// </summary>
internal static class CliVersion
{
    public static string Current => Format(
        typeof(CliVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// The package version without build metadata. SourceLink appends
    /// <c>+&lt;commit sha&gt;</c>, which is noise next to a version people
    /// compare against NuGet and the release page.
    /// </summary>
    internal static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "unknown";

        var plus = informationalVersion.IndexOf('+');
        return plus > 0 ? informationalVersion[..plus] : informationalVersion.Trim();
    }
}
