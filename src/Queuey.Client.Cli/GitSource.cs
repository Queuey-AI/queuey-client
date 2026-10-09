using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// Hvor deploy-fila ligger, for merkingen av det apply styrer (Queuey F2.4, 2026-10-06). Fra flaggene --repo, --repo-path
// og --commit når de er gitt, ellers fra git: remoten origin, stien fra roten av repoet og HEAD. --no-git lar git være.
// Flagget heter --repo-path og ikke --path, fordi --file alt er stien til fila på maskinen.
//
// Remoten står som den står i .git/config, og kan bære et token (https://token@github.com/…). Den renses her før den
// sendes (DeploymentFileSource.Clean), og Queuey renser den igjen før den lagres. Finnes ikke git, eller ligger fila
// utenfor et repo, sendes det som er kjent, og applyen går som før.
//
// Sikkerhetsreviewen 2026-10-06:
// - .NET slår opp et program uten sti i katalogen programmet ligger i og i arbeidskatalogen før PATH. Et repo med en
//   kjørbar fil som heter git, ville da fått den kjørt av queuey apply. git slås derfor opp her, bare i de absolutte
//   oppføringene i PATH, og startes med full sti.
// - Barneprosessen får ingen QUEUEY_*-variabler: git trenger ikke API-nøkkelen, og en hook eller en hjelper skal ikke se den.
// - Commit-en sendes bare når fila er som i HEAD (git status --porcelain er tom for den), ellers sier merket noe fila ikke er.
//   Med --ignored, så en fil git ignorerer, og som derfor ikke er i noen commit, heller ikke får en (re-reviewen 2026-10-06).

/// <summary>Where a deployment file lives, from the command's flags and, unless told not to, from git.</summary>
internal static class GitSource
{
    /// <summary>Runs git with arguments in a directory, and returns what it printed, or null when it failed.</summary>
    internal delegate string? GitRunner(string directory, params string[] arguments);

    /// <summary>The runner the CLI uses: the <c>git</c> found in <see cref="PathVariable"/>, a few seconds at most per call.</summary>
    internal static GitRunner Git { get; set; } = RunGit;

    /// <summary>What git is always run with, before the command: no fsmonitor, and no hooks a repository could set.</summary>
    internal static readonly string[] SafeArguments =
        { "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null") };

    /// <summary>The search path git is looked up in. The process's <c>PATH</c>; tests give their own.</summary>
    internal static Func<string?> PathVariable { get; set; } = () => Environment.GetEnvironmentVariable("PATH");

    /// <summary>
    /// The source for <paramref name="deploymentFile"/>: each of <paramref name="repo"/>, <paramref name="path"/> and
    /// <paramref name="commit"/> when given, the rest from git unless <paramref name="noGit"/>. Every value is cleaned the
    /// way Queuey stores it; null when nothing is known. A commit from git is left out when the file differs from it.
    /// </summary>
    public static DeploymentFileSource? Resolve(string deploymentFile, string? repo, string? path, string? commit, bool noGit)
    {
        if (!noGit && (repo is null || path is null || commit is null))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(deploymentFile)) ?? Directory.GetCurrentDirectory();
            string fileName = Path.GetFileName(deploymentFile);
            if (Git(directory, "rev-parse", "--is-inside-work-tree")?.Trim() == "true")
            {
                repo ??= Git(directory, "remote", "get-url", "origin")?.Trim();
                // Stien fra roten slik git ser den, så en symlenke i stien (/tmp → /private/tmp) ikke gir en sti utenfor.
                path ??= Git(directory, "rev-parse", "--show-prefix")?.Trim() is { } prefix
                    ? prefix + fileName
                    : null;
                if (commit is null && Git(directory, "rev-parse", "HEAD")?.Trim() is { Length: > 0 } head
                    && Git(directory, "status", "--porcelain", "--ignored", "--", fileName) is { } status && status.Trim().Length == 0)
                    commit = head;
            }
        }

        DeploymentFileSource source = DeploymentFileSource.Clean(repo, path, commit);
        return source.IsEmpty ? null : source;
    }

    /// <summary>
    /// The full path of <paramref name="name"/> in the first absolute entry of <paramref name="pathVariable"/> that has it,
    /// or null. Empty and relative entries (<c>.</c>, <c>bin</c>) are skipped: they name the working directory, which a
    /// repository the command runs in, controls.
    /// </summary>
    internal static string? FindExecutable(string name, string? pathVariable)
    {
        if (string.IsNullOrEmpty(pathVariable)) return null;
        string fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string entry in pathVariable.Split(Path.PathSeparator))
        {
            string trimmed = entry.Trim().Trim('"');
            if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed))
                continue;
            string candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? RunGit(string directory, params string[] arguments)
        => RunGitWithExit(directory, arguments) is { Exit: 0 } run ? run.Output : null;

    /// <summary>
    /// Runs git as <see cref="Git"/> does, and returns its exit code with what it printed, for a command whose exit code is the
    /// answer (<c>check-ignore</c>). Null when git is not found, does not start, or takes too long.
    /// </summary>
    internal static (int Exit, string Output)? RunGitWithExit(string directory, params string[] arguments)
    {
        try
        {
            if (FindExecutable("git", PathVariable()) is not { } git)
                return null;

            var start = new ProcessStartInfo(git)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Ingen fsmonitor og ingen hooks (security-review av #68, K3): et repo kan sette en kommando i core.fsmonitor, som git
            // kjører ved status og andre lesinger.
            foreach (string argument in SafeArguments.Concat(arguments))
                start.ArgumentList.Add(argument);
            // Ingen pager og ingen spørsmål om passord: en remote som vil ha innlogging, skal ikke stoppe en apply.
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_PAGER"] = "cat";
            // Ingen av Queueys hemmeligheter følger med til git, en hook eller en hjelper den starter.
            foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("QUEUEY_", StringComparison.OrdinalIgnoreCase)).ToList())
                start.Environment.Remove(key);

            using Process? process = Process.Start(start);
            if (process is null) return null;
            // Begge strømmene leses mens git kjører, så en full pipe ikke holder den igjen, og ventetiden har et tak.
            System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            return output.Wait(1000) ? (process.ExitCode, output.Result) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
