using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Queuey.Client.Cli;

// Kenneth 2026-10-09: «en smidig løsning hvor en VibeCoder og en ingeniør begge får det». En hemmelighet når appen ved at
// agenten setter den selv (--write), sier hvor den ligger, eller viser en lenke i konsollet, og verdien er aldri i terminalen.
// --write tar en fil (.env, med sjekkene EnvFile har) eller user-secrets, .NET sin hemmelighetslagring for et prosjekt.

/// <summary>Where <c>--write</c> puts a secret: a dotenv file, or the project's .NET user secrets.</summary>
internal abstract class SecretTarget
{
    /// <summary>The word <c>--write</c> takes for the project's .NET user secrets.</summary>
    internal const string UserSecretsWord = "user-secrets";

    /// <summary>What the target is, as output names it, such as <c>.env</c> or <c>user-secrets of Shop.csproj</c>.</summary>
    public abstract string Shown { get; }

    /// <summary><c>file</c> or <c>user-secrets</c>, for <c>--json</c>.</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// Writes <paramref name="values"/>. What each name held before, when the target can tell, and the mode a file had when
    /// it was tightened. Throws <see cref="CliFileException"/> when it cannot write; never shows a value.
    /// </summary>
    public abstract (IReadOnlyDictionary<string, string?> Previous, string? TightenedFrom) Write(IReadOnlyList<(string Name, string Value)> values);

    /// <summary>
    /// The target <paramref name="write"/> names, checked before anything is minted or stored: a value that cannot be written
    /// is a key that has to be revoked. Throws a usage or configuration error when it cannot take a secret.
    /// </summary>
    public static SecretTarget Parse(string write)
        => string.Equals(write.Trim(), UserSecretsWord, StringComparison.OrdinalIgnoreCase)
            ? UserSecretsTarget.ForWorkingFolder()
            : new FileTarget(write, EnvFile.Check(write));

    /// <summary>
    /// The target that fits the project in <paramref name="folder"/>: user-secrets for a .NET project with a
    /// <c>UserSecretsId</c>, else <c>.env</c>. What advise names.
    /// </summary>
    internal static string SuggestedFor(string folder)
        => UserSecretsTarget.ProjectWithUserSecrets(folder) is not null ? UserSecretsWord : ".env";
}

/// <summary>A dotenv file, written by <see cref="EnvFile"/>: merged, 0600, and only when git ignores it.</summary>
internal sealed class FileTarget : SecretTarget
{
    private readonly string _shown;
    private readonly string _full;

    public FileTarget(string shown, string full)
    {
        _shown = shown;
        _full = full;
    }

    public override string Shown => _shown;
    public override string Kind => "file";

    public override (IReadOnlyDictionary<string, string?> Previous, string? TightenedFrom) Write(IReadOnlyList<(string Name, string Value)> values)
    {
        EnvFile.Written written = EnvFile.Write(_full, values);
        return (written.Previous, written.TightenedFrom is { } mode ? EnvFile.Octal(mode) : null);
    }
}

/// <summary>
/// The .NET user secrets of the project in the working folder, set with <c>dotnet user-secrets set</c>. The values go in on
/// stdin as JSON, never as arguments, which other users on the machine can read in the process list.
/// </summary>
internal sealed class UserSecretsTarget : SecretTarget
{
    /// <summary>The search path <c>dotnet</c> is looked up in. A seam for tests.</summary>
    internal static Func<string?> PathVariable { get; set; } = () => Environment.GetEnvironmentVariable("PATH");

    /// <summary>The working folder the project is looked for in. A seam for tests.</summary>
    internal static Func<string> WorkingFolder { get; set; } = Directory.GetCurrentDirectory;

    private static readonly Regex UserSecretsId = new(@"<UserSecretsId>\s*([^<\s]+)\s*</UserSecretsId>", RegexOptions.CultureInvariant);

    private readonly string _project;
    private readonly string _dotnet;

    private UserSecretsTarget(string project, string dotnet)
    {
        _project = project;
        _dotnet = dotnet;
    }

    public override string Shown => $"user-secrets of {Path.GetFileName(_project)}";
    public override string Kind => UserSecretsWord;

    /// <summary>The project in the working folder, with a <c>UserSecretsId</c> and a <c>dotnet</c> to set them with.</summary>
    internal static UserSecretsTarget ForWorkingFolder()
    {
        string folder = WorkingFolder();
        string[] projects = Projects(folder);
        if (projects.Length == 0)
            throw new CliUsageException("no_project",
                "--write user-secrets sets the secrets of the .NET project in this folder, and there is none (no .csproj). Nothing was minted.",
                "Run the command in the project's folder, or write to a file instead: --write .env.");
        if (projects.Length > 1)
            throw new CliUsageException("several_projects",
                $"--write user-secrets needs one .NET project in this folder, and there are {projects.Length}: " +
                $"{string.Join(", ", projects.Select(p => TerminalText.Line(Path.GetFileName(p))))}. Nothing was minted.",
                "Run the command in the folder of the project that publishes or receives.");

        if (IdOf(projects[0]) is null)
            throw new QueueyConfigurationException(
                $"{Path.GetFileName(projects[0])} has no UserSecretsId, so it has no user secrets to set. Nothing was minted.")
            {
                SuggestedAction = "Add one with `dotnet user-secrets init` in the project's folder, then run the command again. Or write " +
                                  "to a file: --write .env.",
            };

        string dotnet = GitSource.FindExecutable("dotnet", PathVariable())
                        ?? throw new QueueyConfigurationException("--write user-secrets runs `dotnet user-secrets`, and no dotnet was found on PATH. Nothing was minted.")
                        {
                            SuggestedAction = "Install the .NET SDK, or write to a file: --write .env.",
                        };
        return new UserSecretsTarget(projects[0], dotnet);
    }

    /// <summary>The one project in <paramref name="folder"/> that has a <c>UserSecretsId</c>, or null.</summary>
    internal static string? ProjectWithUserSecrets(string folder)
        => Projects(folder) is { Length: 1 } one && IdOf(one[0]) is not null ? one[0] : null;

    private static string[] Projects(string folder)
    {
        try
        {
            return Directory.GetFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? IdOf(string project)
    {
        try
        {
            return UserSecretsId.Match(File.ReadAllText(project)) is { Success: true } match ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public override (IReadOnlyDictionary<string, string?> Previous, string? TightenedFrom) Write(IReadOnlyList<(string Name, string Value)> values)
    {
        var start = new ProcessStartInfo(_dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(_project)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Bare navnene står i argumentene; verdiene går på stdin (dotnet user-secrets set leser JSON derfra).
        foreach (string argument in new[] { "user-secrets", "set", "--project", _project })
            start.ArgumentList.Add(argument);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("QUEUEY_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(key);

        string json = JsonSerializer.Serialize(values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));
        try
        {
            using Process process = Process.Start(start) ?? throw new IOException("dotnet did not start.");
            process.StandardInput.Write(json);
            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new IOException("dotnet user-secrets set did not finish within two minutes.");
            }

            // Utdataene vises ikke: en feil fra den kan sitere det den fikk på stdin.
            if (process.ExitCode != 0)
                throw new IOException($"dotnet user-secrets set exited with {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CliFileException("secret_unwritable", $"Could not set the user secrets of {Path.GetFileName(_project)}: {ex.Message}",
                "Run `dotnet user-secrets list --project <project>` to see that the project's user secrets work.", ex);
        }

        return (values.ToDictionary(v => v.Name, _ => (string?)null, StringComparer.Ordinal), null);
    }
}
