using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Queuey.Client.Cli.Advise;

/// <summary>One file the scaffold would create or change.</summary>
/// <param name="Path">Repo-relative path.</param>
/// <param name="Content">The full contents to write.</param>
/// <param name="Action">What writing it would do, in the reader's words.</param>
/// <param name="Exists">True when the file is already there.</param>
public sealed record PlannedFile(string Path, string Content, string Action, bool Exists);

/// <summary>
/// What <c>--write-files</c> would put in the repository.
///
/// Computing it separately from writing it is what lets the bare command show
/// the plan and change nothing. The two cannot disagree, because they are the
/// same plan.
///
/// The scaffold stays deliberately small: a deployment file, and a gitignore
/// line that keeps the API key out of git. Adding the package is left to
/// <c>dotnet add package</c>, printed as a step — editing someone's project
/// file is a bigger liberty than this command should take, and the command to
/// do it is one line they can read.
/// </summary>
public static class ScaffoldPlan
{
    public const string DeployFileName = "queuey.deploy.json";
    private const string ConfigFileName = "queuey.json";

    /// <summary>
    /// The queue name to scaffold: what the caller asked for, otherwise one
    /// derived from the repository's own name, because "events" in every
    /// workspace helps nobody find anything later.
    /// </summary>
    public static string DefaultQueueName(string root, string? requested = null)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return Slug(requested!);

        var folder = new DirectoryInfo(Path.GetFullPath(root)).Name;
        var slug = Slug(folder);
        return string.IsNullOrEmpty(slug) ? "events" : slug;
    }

    public static IReadOnlyList<PlannedFile> For(string root, string queueName)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A repository path is required.", nameof(root));
        if (string.IsNullOrWhiteSpace(queueName)) throw new ArgumentException("A queue name is required.", nameof(queueName));

        var planned = new List<PlannedFile>();

        var deployPath = Path.Combine(root, DeployFileName);
        var deployExists = File.Exists(deployPath);
        planned.Add(new PlannedFile(
            DeployFileName,
            DeploymentFileContent(queueName),
            deployExists ? "already exists — left alone" : "create",
            deployExists));

        var gitignore = GitignorePlan(root);
        if (gitignore is not null) planned.Add(gitignore);

        return planned;
    }

    /// <summary>
    /// A deployment file with one queue and nothing else. Every field left out
    /// means "leave alone", so a scaffold that says little changes little — and
    /// it carries no secrets by construction.
    ///
    /// No "$schema" line: the deployment parser rejects properties it does not
    /// know, so adding one would make the file we just wrote unreadable to
    /// `queuey apply`. A test parses this content to keep that honest.
    /// </summary>
    private static string DeploymentFileContent(string queueName) =>
        $$"""
        {
          "queues": {
            "{{queueName}}": {
              "dlqEnabled": true,
              "retentionDays": 30
            }
          }
        }

        """;

    /// <summary>
    /// Keeps <c>queuey.json</c> out of git. It holds the API key; the
    /// deployment file beside it is meant to be committed, and the whole point
    /// of them being two files is that one of them can be.
    /// </summary>
    private static PlannedFile? GitignorePlan(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        var exists = File.Exists(path);
        var current = exists ? File.ReadAllText(path) : string.Empty;

        var alreadyIgnored = current
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line == ConfigFileName || line == "/" + ConfigFileName);

        if (alreadyIgnored) return null;

        var addition = $"{Environment.NewLine}# Queuey connection config — holds the API key, never commit it{Environment.NewLine}{ConfigFileName}{Environment.NewLine}";
        var content = exists ? current.TrimEnd('\n', '\r') + addition : addition.TrimStart('\r', '\n');

        return new PlannedFile(
            ".gitignore",
            content,
            exists ? $"add {ConfigFileName}" : $"create, ignoring {ConfigFileName}",
            exists);
    }

    /// <summary>A queue name Queuey will accept: lowercase, dots and dashes, nothing exotic.</summary>
    private static string Slug(string value)
    {
        var cleaned = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-')
            .ToArray());

        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        return cleaned.Trim('-', '.');
    }
}
