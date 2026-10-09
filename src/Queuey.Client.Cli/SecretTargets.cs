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

    /// <summary>
    /// What the target is, as output names it, such as <c>.env</c> or <c>user-secrets of Shop.csproj (id shop-1)</c>. Made safe
    /// for a terminal: it holds a path and a project's id, which the repository chose.
    /// </summary>
    public abstract string Shown { get; }

    /// <summary><c>file</c> or <c>user-secrets</c>, for <c>--json</c>.</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// What <paramref name="names"/> are set to there now, read before anything is minted or stored, so a value that would be
    /// overwritten is known first. Never shown.
    /// </summary>
    public abstract IReadOnlyDictionary<string, string> Current(params string[] names);

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
    /// <c>UserSecretsId</c>, else <c>.env</c>. What advise names; it runs nothing.
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
        _shown = TerminalText.Line(shown);
        _full = full;
    }

    public override string Shown => _shown;
    public override string Kind => "file";

    public override IReadOnlyDictionary<string, string> Current(params string[] names)
        => File.Exists(_full) ? EnvFile.Read(_full, names) : new Dictionary<string, string>();

    public override (IReadOnlyDictionary<string, string?> Previous, string? TightenedFrom) Write(IReadOnlyList<(string Name, string Value)> values)
    {
        EnvFile.Written written = EnvFile.Write(_full, values);
        return (written.Previous, written.TightenedFrom is { } mode ? EnvFile.Octal(mode) : null);
    }
}

/// <summary>
/// The .NET user secrets of the project in the working folder, through <c>dotnet user-secrets</c>. The values go in on stdin as
/// JSON, never as arguments, which other users on the machine can read in the process list. <c>dotnet user-secrets</c>
/// evaluates the project with MSBuild, so it runs the project's build logic, with the trust <c>dotnet build</c> needs.
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
    private readonly string _id;
    private readonly Dictionary<string, string> _current;

    private UserSecretsTarget(string project, string dotnet, string id, Dictionary<string, string> current)
    {
        _project = project;
        _dotnet = dotnet;
        _id = id;
        _current = current;
    }

    public override string Shown => TerminalText.Line($"user-secrets of {Path.GetFileName(_project)} (id {_id})");
    public override string Kind => UserSecretsWord;

    /// <summary>The id <c>dotnet user-secrets</c> uses for the project, as it said itself.</summary>
    public string Id => _id;

    /// <summary>
    /// The project in the working folder, checked the way the write will run: <c>dotnet user-secrets list</c> must work for it,
    /// which also says the id it uses and what the secrets hold now. Throws before anything is minted or stored.
    /// </summary>
    // Security-review av #69 (B2): en regex på csproj var hele forsjekken, så en feil i user-secrets ble først oppdaget etter
    // at Queuey var endret.
    internal static UserSecretsTarget ForWorkingFolder()
    {
        string folder = WorkingFolder();
        string[] projects = Projects(folder);
        if (projects.Length == 0)
            throw new CliUsageException("no_project",
                "--write user-secrets sets the secrets of the .NET project in this folder, and there is none (no .csproj). Nothing was minted or stored.",
                "Run the command in the project's folder, or write to a file instead: --write .env.");
        if (projects.Length > 1)
            throw new CliUsageException("several_projects",
                $"--write user-secrets needs one .NET project in this folder, and there are {projects.Length}: " +
                $"{string.Join(", ", projects.Select(p => TerminalText.Line(Path.GetFileName(p))))}. Nothing was minted or stored.",
                "Run the command in the folder of the project that publishes or receives.");

        string dotnet = GitSource.FindExecutable("dotnet", PathVariable())
                        ?? throw new QueueyConfigurationException("--write user-secrets runs `dotnet user-secrets`, and no dotnet was found on PATH. Nothing was minted or stored.")
                        {
                            SuggestedAction = "Install the .NET SDK, or write to a file: --write .env.",
                        };

        string project = projects[0];
        int exit;
        string output;
        try
        {
            (exit, output) = Run(dotnet, project, new[] { "user-secrets", "list", "--project", project, "--verbose" }, stdin: null);
        }
        catch (IOException ex)
        {
            throw new QueueyConfigurationException($"`dotnet user-secrets list` could not run: {ex.Message} Nothing was minted or stored.")
            {
                SuggestedAction = "Check the .NET SDK, or write to a file: --write .env.",
            };
        }
        if (exit != 0)
        {
            // Utdataene vises ikke: de kan sitere hemmeligheter. Mangler id-en, sier dotnet det med navnet på egenskapen.
            bool noId = output.Contains("UserSecretsId", StringComparison.Ordinal);
            throw new QueueyConfigurationException(noId
                ? $"{TerminalText.Line(Path.GetFileName(project))} has no UserSecretsId, so it has no user secrets to set. Nothing was minted or stored."
                : $"`dotnet user-secrets list` failed for {TerminalText.Line(Path.GetFileName(project))} (exit {exit}), so its user secrets cannot be set. Nothing was minted or stored.")
            {
                SuggestedAction = noId
                    ? "Add one with `dotnet user-secrets init` in the project's folder, then run the command again. Or write to a file: --write .env."
                    : "Run `dotnet user-secrets list` in the project's folder to see why. Or write to a file: --write .env.",
            };
        }

        return new UserSecretsTarget(project, dotnet, IdFrom(output) ?? "?", Secrets(output));
    }

    /// <summary>The one project in <paramref name="folder"/> whose file names a <c>UserSecretsId</c>, or null. Runs nothing.</summary>
    internal static string? ProjectWithUserSecrets(string folder)
        => Projects(folder) is { Length: 1 } one && RegexId(one[0]) is not null ? one[0] : null;

    public override IReadOnlyDictionary<string, string> Current(params string[] names)
        => names.Where(_current.ContainsKey).ToDictionary(n => n, n => _current[n], StringComparer.Ordinal);

    public override (IReadOnlyDictionary<string, string?> Previous, string? TightenedFrom) Write(IReadOnlyList<(string Name, string Value)> values)
    {
        string json = JsonSerializer.Serialize(values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));
        int exit;
        try
        {
            // Bare navnene står i argumentene; verdiene går på stdin (dotnet user-secrets set leser JSON derfra).
            (exit, _) = Run(_dotnet, _project, new[] { "user-secrets", "set", "--project", _project }, json);
        }
        catch (IOException ex)
        {
            throw new CliFileException("secret_unwritable", $"Could not set the user secrets of {TerminalText.Line(Path.GetFileName(_project))}: {ex.Message}",
                "Run `dotnet user-secrets list` in the project's folder to see that its user secrets work.", ex);
        }

        // Utdataene vises ikke: en feil fra den kan sitere det den fikk på stdin.
        if (exit != 0)
            throw new CliFileException("secret_unwritable",
                $"Could not set the user secrets of {TerminalText.Line(Path.GetFileName(_project))}: dotnet user-secrets set exited with {exit}.",
                "Run `dotnet user-secrets list` in the project's folder to see that its user secrets work.", new IOException($"exit {exit}"));

        return (values.ToDictionary(v => v.Name, v => _current.TryGetValue(v.Name, out string? had) ? had : null, StringComparer.Ordinal), null);
    }

    /// <summary>The id from the secrets file path <c>--verbose</c> prints: the folder the file is in.</summary>
    private static string? IdFrom(string output)
    {
        const string Marker = "Secrets file path ";
        string? line = output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null)
            return null;
        string path = line.Substring(Marker.Length).TrimEnd('.');
        return Path.GetFileName(Path.GetDirectoryName(path));
    }

    /// <summary>The secrets <c>dotnet user-secrets list</c> printed, as <c>name = value</c> lines. Never shown.</summary>
    private static Dictionary<string, string> Secrets(string output)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int at = line.IndexOf(" = ", StringComparison.Ordinal);
            if (at > 0 && !line.StartsWith("Project file path ", StringComparison.Ordinal) && !line.StartsWith("Secrets file path ", StringComparison.Ordinal))
                secrets[line.Substring(0, at)] = line.Substring(at + 3);
        }

        return secrets;
    }

    private static (int Exit, string Output) Run(string dotnet, string project, string[] arguments, string? stdin)
    {
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            UseShellExecute = false,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("QUEUEY_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(key);

        try
        {
            using Process process = Process.Start(start) ?? throw new IOException("dotnet did not start.");
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
            System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new IOException("dotnet user-secrets did not finish within two minutes.");
            }

            return (process.ExitCode, output.Result + error.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException($"dotnet could not be run ({ex.GetType().Name}).", ex);
        }
    }

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

    private static string? RegexId(string project)
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
}
