using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Queuey.Client;

// Blindtesten 2026-10-09 (funn 9): `queuey keys mint --write .env` skrev signeringsnøkkelen i .env, og en .NET-app leste den
// ikke, for .NET leser ikke .env selv. UseEnvironmentVariables() leser nå QUEUEY_*-navnene derfra, med reglene `queuey publish`
// har: bare en vanlig fil eid av brukeren, aldri en git sporer, og miljøvariablene vinner.
//
// Bare i Development (DOTNET_ENVIRONMENT eller ASPNETCORE_ENVIRONMENT). Valgt fordi .env er en utviklerkonvensjon: der keys
// mint skriver den, på en utviklers maskin. I produksjon kommer hemmeligheter fra plattformens miljø eller hemmelighetslager,
// og en fil i arbeidsmappa som stille ble lest der, ville vært en overraskelse og en ny vei inn. Sjekken av git starter
// dessuten en prosess, som et bibliotek ikke bør gjøre ved oppstart i produksjon.

/// <summary>
/// The <c>QUEUEY_*</c> values in a <c>.env</c> file in the working folder, for <see cref="QueueyOptions.UseEnvironmentVariables(Func{string, string?}?)"/>
/// in Development.
/// </summary>
internal static class DotEnvFile
{
    internal const string FileName = ".env";

    /// <summary>Runs git with arguments in a folder: its exit code, or null when git cannot be run. A seam for tests.</summary>
    internal static Func<string, string[], int?> Git { get; set; } = RunGit;

    /// <summary>Who owns a file, or null when it cannot be told. A seam for tests.</summary>
    internal static Func<string, uint?> OwnerOf { get; set; } = NativeOwner;

    /// <summary>The user the process runs as, or null when it cannot be told. A seam for tests.</summary>
    internal static Func<uint?> CurrentUser { get; set; } = NativeCurrentUser;

    /// <summary>Whether the environment <paramref name="read"/> gives is Development, the only one <c>.env</c> is read in.</summary>
    internal static bool IsDevelopment(Func<string, string?> read)
        => new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT" }
            .Any(name => string.Equals(read(name)?.Trim(), "Development", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The <c>QUEUEY_*</c> values in <c>.env</c> in <paramref name="folder"/>: empty when there is none, or when it is not a
    /// regular file of the user's own, or git tracks it (or cannot say while it is inside a repository folder).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Read(string folder)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(folder, FileName);
        if (!IsOwnPlainFile(path) || TrackedByGit(folder))
            return values;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return values;
        }

        foreach (string line in lines)
        {
            if (Parse(line) is not { } setting || !setting.Name.StartsWith("QUEUEY_", StringComparison.Ordinal) || values.ContainsKey(setting.Name))
                continue;
            if (setting.Value.Length > 0)
                values[setting.Name] = setting.Value;
        }

        return values;
    }

    /// <summary>One <c>NAME=value</c> line (with or without <c>export</c>), unquoted, or null for anything else.</summary>
    internal static (string Name, string Value)? Parse(string line)
    {
        string text = line.Trim();
        if (text.StartsWith("export ", StringComparison.Ordinal))
            text = text.Substring("export ".Length).TrimStart();
        int equals = text.IndexOf('=');
        if (equals <= 0)
            return null;
        string name = text.Substring(0, equals).Trim();
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return null;

        string raw = text.Substring(equals + 1).Trim();
        string value;
        if (raw.Length >= 2 && raw[0] == '\'' && raw[raw.Length - 1] == '\'')
            value = raw.Substring(1, raw.Length - 2);
        else if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
            value = raw.Substring(1, raw.Length - 2).Replace("\\\"", "\"").Replace("\\$", "$").Replace("\\\\", "\\");
        else
        {
            int comment = raw.IndexOf(" #", StringComparison.Ordinal);
            value = (comment >= 0 ? raw.Substring(0, comment) : raw).Trim();
        }

        return (name, value);
    }

    /// <summary>A regular file, not a link, owned by the user running the process (on Windows, the profile's ACL protects it).</summary>
    private static bool IsOwnPlainFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            return false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;
        uint? owner = OwnerOf(path), me = CurrentUser();
        return owner is not null && me is not null && owner == me;
    }

    /// <summary>Whether git tracks <c>.env</c> in <paramref name="folder"/>, or cannot say while the folder is inside a repository.</summary>
    private static bool TrackedByGit(string folder)
    {
        int? exit = Git(folder, new[] { "ls-files", "--error-unmatch", "--", FileName });
        return exit switch
        {
            0 => true,
            1 => false,
            _ => InsideAGitFolder(folder),
        };
    }

    private static bool InsideAGitFolder(string folder)
    {
        for (string? at = Path.GetFullPath(folder); !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            if (Directory.Exists(Path.Combine(at, ".git")) || File.Exists(Path.Combine(at, ".git")))
                return true;
        }

        return false;
    }

    // git slås opp bare i de absolutte oppføringene i PATH, som i CLI-en (GitSource): et repo med en kjørbar fil som heter git,
    // skal ikke få den kjørt. Barneprosessen får ingen QUEUEY_*-variabler.
    private static int? RunGit(string folder, string[] arguments)
    {
        try
        {
            string? git = FindGit();
            if (git is null)
                return null;
            var start = new ProcessStartInfo(git)
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = string.Join(" ", arguments.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)),
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            foreach (string key in start.EnvironmentVariables.Keys.Cast<string>()
                         .Where(k => k.StartsWith("QUEUEY_", StringComparison.OrdinalIgnoreCase)).ToList())
                start.EnvironmentVariables.Remove(key);

            using Process? process = Process.Start(start);
            if (process is null)
                return null;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }

            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string? FindGit()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        string name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "git.exe" : "git";
        foreach (string entry in path!.Split(Path.PathSeparator))
        {
            string folder = entry.Trim().Trim('"');
            if (folder.Length == 0 || !Path.IsPathRooted(folder) || folder.StartsWith(".", StringComparison.Ordinal))
                continue;
            string candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

#if NET8_0_OR_GREATER
    // Eieren av en fil fra runtimens egen shim (libSystem.Native), som CLI-en leser den (UserProfiles). Finnes den ikke, er
    // eieren ukjent, og .env leses ikke.
    [StructLayout(LayoutKind.Sequential, Size = 512)]
    private struct FileStatus
    {
        public int Flags;
        public int Mode;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true)]
    private static extern int Stat(string path, out FileStatus status);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_GetEUid")]
    private static extern uint GetEUid();

    private static uint? NativeOwner(string path)
    {
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return Stat(path, out FileStatus status) == 0 && (status.Mode & 0xFFF) == ((int)mode & 0xFFF) ? status.Uid : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static uint? NativeCurrentUser()
    {
        try
        {
            return GetEUid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
#else
    // netstandard2.0 utenfor Windows: eieren kan ikke leses uten en avhengighet, så .env leses ikke.
    private static uint? NativeOwner(string path) => null;

    private static uint? NativeCurrentUser() => null;
#endif
}
