using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// Hvor deploy-fila ligger, for merkingen av det apply styrer (Queuey F2.4, 2026-10-06). Fra flaggene --repo, --repo-path
// og --commit når de er gitt, ellers fra git: remoten origin, stien fra roten av repoet og HEAD. --no-git lar git være.
// Flagget heter --repo-path og ikke --path, fordi --file alt er stien til fila på maskinen.
//
// Remoten står som den står i .git/config, og kan bære et token (https://token@github.com/…). Den renses her før den
// sendes (DeploymentFileSource.Clean), og Queuey renser den igjen før den lagres. Finnes ikke git, eller ligger fila
// utenfor et repo, sendes det som er kjent, og applyen går som før.

/// <summary>Where a deployment file lives, from the command's flags and, unless told not to, from git.</summary>
internal static class GitSource
{
    /// <summary>Runs git with arguments in a directory, and returns what it printed, or null when it failed.</summary>
    internal delegate string? GitRunner(string directory, params string[] arguments);

    /// <summary>The runner the CLI uses: the <c>git</c> on the PATH, a few seconds at most per call.</summary>
    internal static GitRunner Git { get; set; } = RunGit;

    /// <summary>
    /// The source for <paramref name="deploymentFile"/>: each of <paramref name="repo"/>, <paramref name="path"/> and
    /// <paramref name="commit"/> when given, the rest from git unless <paramref name="noGit"/>. Every value is cleaned the
    /// way Queuey stores it; null when nothing is known.
    /// </summary>
    public static DeploymentFileSource? Resolve(string deploymentFile, string? repo, string? path, string? commit, bool noGit)
    {
        if (!noGit && (repo is null || path is null || commit is null))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(deploymentFile)) ?? Directory.GetCurrentDirectory();
            if (Git(directory, "rev-parse", "--is-inside-work-tree")?.Trim() == "true")
            {
                repo ??= Git(directory, "remote", "get-url", "origin")?.Trim();
                // Stien fra roten slik git ser den, så en symlenke i stien (/tmp → /private/tmp) ikke gir en stien utenfor.
                path ??= Git(directory, "rev-parse", "--show-prefix")?.Trim() is { } prefix
                    ? prefix + Path.GetFileName(deploymentFile)
                    : null;
                commit ??= Git(directory, "rev-parse", "HEAD")?.Trim();
            }
        }

        DeploymentFileSource source = DeploymentFileSource.Clean(repo, path, commit);
        return source.IsEmpty ? null : source;
    }

    private static string? RunGit(string directory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            // Ingen pager og ingen spørsmål om passord: en remote som vil ha innlogging, skal ikke stoppe en apply.
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_PAGER"] = "cat";

            using Process? git = Process.Start(start);
            if (git is null) return null;
            // Begge strømmene leses mens git kjører, så en full pipe ikke holder den igjen, og ventetiden har et tak.
            System.Threading.Tasks.Task<string> output = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(5000))
            {
                try { git.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            return git.ExitCode == 0 && output.Wait(1000) ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
