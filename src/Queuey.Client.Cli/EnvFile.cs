using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Queuey.Client;

namespace Queuey.Client.Cli;

// `queuey keys mint --write .env` (2026-10-09, Kenneth godkjente): hemmeligheten går rett i en fil, ikke gjennom terminalen
// eller agentens kontekst. Fila må være ignorert av git når den ligger i et repo, så hemmeligheten ikke havner i en commit.
// Linjene for nøkkelen erstattes eller legges til; resten av fila står som den sto.

/// <summary>A dotenv file the CLI writes a secret into: checked before anything is minted, and merged line by line.</summary>
internal static class EnvFile
{
    /// <summary>Runs git with arguments in a directory: its exit code and output, or null when git cannot be run. A seam.</summary>
    internal static Func<string, string[], (int Exit, string Output)?> Git { get; set; } = GitSource.RunGitWithExit;

    /// <summary>
    /// Throws when <paramref name="path"/> may not hold a secret: its folder is missing, it is a link or not a file, or it lies
    /// in a git repository and git does not ignore it. Returns the full path. Nothing is written.
    /// </summary>
    public static string Check(string path)
    {
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(folder))
            throw new CliUsageException("invalid_value", $"--write names a file in {folder}, which does not exist. Nothing was minted.", null);
        if (Directory.Exists(full))
            throw new CliUsageException("invalid_value", $"--write names {path}, which is a folder. Nothing was minted.", "Name a file, such as .env.");
        if (File.Exists(full) && new FileInfo(full).LinkTarget is not null)
            throw new CliUsageException("invalid_value", $"--write names {path}, which is a link. Nothing was minted.",
                "Name the file itself, so the secret lands where you can see it.");

        if (Git(folder, new[] { "rev-parse", "--is-inside-work-tree" }) is not { } inside)
        {
            if (InsideAGitFolder(folder))
                throw NotIgnored(path, "git could not be run, so whether git ignores it could not be checked");
            return full;
        }

        if (inside.Exit != 0 || inside.Output.Trim() != "true")
            return full; // Ikke i et repo.

        // check-ignore: 0 er ignorert, 1 er ikke. En fil git alt sporer, er ikke ignorert, selv om et mønster treffer den.
        (int Exit, string Output)? ignored = Git(folder, new[] { "check-ignore", "-q", "--", full });
        return ignored switch
        {
            { Exit: 0 } => full,
            { Exit: 1 } => throw NotIgnored(path, "it is in a git repository, and git does not ignore it"),
            _ => throw NotIgnored(path, "git could not say whether it ignores it"),
        };
    }

    /// <summary>
    /// Writes <paramref name="values"/> into the file at <paramref name="full"/>: each replaces the line that sets it (with or
    /// without <c>export</c>), and later lines that set it again are dropped, so an old secret does not linger; one that is not
    /// there is added at the end. Every other line stays. A new file is readable only by the user (0600); an existing one keeps
    /// its mode. Returns what the file held for each name before, or null.
    /// </summary>
    public static Dictionary<string, string?> Write(string full, IReadOnlyList<(string Name, string Value)> values)
    {
        string? existing = File.Exists(full) ? File.ReadAllText(full) : null;
        string merged = Merge(existing, values, out Dictionary<string, string?> previous);

        UnixFileMode? mode = existing is not null && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(full) : null;
        string temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = mode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write(merged);
            if (mode is { } kept)
                File.SetUnixFileMode(temp, kept); // umask kan ha tatt bort noe ved opprettelsen
            File.Move(temp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch { /* ble kanskje aldri laget */ }
            throw new CliFileException("file_unwritable", $"Could not write {full}: {ex.Message}", "Check that this user may write the file.", ex);
        }

        return previous;
    }

    /// <summary>
    /// The values <paramref name="names"/> are set to in the dotenv file at <paramref name="path"/>: the first line that sets
    /// each, with or without <c>export</c>, unquoted. Every other line is skipped, never read into anything. A file that
    /// cannot be read gives nothing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(string path, params string[] names)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        foreach (string line in lines)
        {
            Match match = Setter.Match(line);
            if (!match.Success || !names.Contains(match.Groups[2].Value, StringComparer.Ordinal) || found.ContainsKey(match.Groups[2].Value))
                continue;
            string value = Value(match.Groups[3].Value.Trim());
            if (value.Length > 0)
                found[match.Groups[2].Value] = value;
        }

        return found;
    }

    private static readonly Regex Setter = new(@"^\s*(export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$", RegexOptions.CultureInvariant);

    /// <summary>A dotenv value: single quotes as written, double quotes unescaped, otherwise up to a <c> #</c> comment.</summary>
    private static string Value(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            return raw[1..^1];
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return raw[1..^1].Replace("\\\"", "\"").Replace("\\$", "$").Replace("\\\\", "\\");
        int comment = raw.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? raw[..comment] : raw).Trim();
    }

    /// <summary>Whether others than the user can read the file at <paramref name="full"/>. Never on Windows.</summary>
    public static bool OthersCanRead(string full)
        => !OperatingSystem.IsWindows() && File.Exists(full)
           && (File.GetUnixFileMode(full) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0;

    /// <summary>The text of a dotenv file with <paramref name="values"/> merged in, and what each name held before.</summary>
    internal static string Merge(string? existing, IReadOnlyList<(string Name, string Value)> values, out Dictionary<string, string?> previous)
    {
        previous = values.ToDictionary(v => v.Name, _ => (string?)null, StringComparer.Ordinal);
        string newline = existing is not null && existing.Contains("\r\n") ? "\r\n" : "\n";
        var lines = new List<string>(string.IsNullOrEmpty(existing) ? Array.Empty<string>() : existing!.Split('\n').Select(l => l.TrimEnd('\r')));
        bool endedWithNewline = existing is null || existing.Length == 0 || existing.EndsWith("\n", StringComparison.Ordinal);
        if (endedWithNewline && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        foreach ((string name, string value) in values)
        {
            var setter = new Regex($@"^\s*(export\s+)?{Regex.Escape(name)}\s*=(.*)$");
            int first = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                Match match = setter.Match(lines[i]);
                if (!match.Success)
                    continue;
                if (first < 0)
                {
                    first = i;
                    previous[name] = Unquote(match.Groups[2].Value.Trim());
                    lines[i] = $"{match.Groups[1].Value}{name}={Quote(value)}";
                }
                else
                {
                    lines.RemoveAt(i--);
                }
            }

            if (first < 0)
                lines.Add($"{name}={Quote(value)}");
        }

        return string.Join(newline, lines) + newline;
    }

    /// <summary>A value as dotenv files take it: as it is when it has only safe characters, else in single quotes.</summary>
    internal static string Quote(string value)
    {
        if (value.All(c => char.IsAsciiLetterOrDigit(c) || "_-./+=:@".Contains(c)))
            return value;
        if (!value.Contains('\''))
            return $"'{value}'";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$") + "\"";
    }

    private static string Unquote(string value)
        => value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0] ? value[1..^1] : value;

    private static bool InsideAGitFolder(string folder)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            if (Directory.Exists(Path.Combine(at, ".git")) || File.Exists(Path.Combine(at, ".git")))
                return true;
        }

        return false;
    }

    private static QueueyConfigurationException NotIgnored(string path, string why)
        => new($"{path} would get the signing secret, and {why}. Nothing was minted.")
        {
            SuggestedAction = $"Add {Path.GetFileName(path)} to .gitignore (echo {Path.GetFileName(path)} >> .gitignore), or name a file outside the repository.",
        };
}
