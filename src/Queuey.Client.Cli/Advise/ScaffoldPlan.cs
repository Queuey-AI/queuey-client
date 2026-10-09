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
/// The scaffold stays deliberately small: a deployment file, and gitignore
/// lines that keep the API key's queuey.json and the signing key's .env out of git. Adding the package is left to
/// <c>dotnet add package</c>, printed as a step — editing someone's project
/// file is a bigger liberty than this command should take, and the command to
/// do it is one line they can read.
/// </summary>
public static class ScaffoldPlan
{
    public const string DeployFileName = "queuey.deploy.json";

    /// <summary>
    /// What a person must know before applying the scaffold to a workspace that already exists: it takes signed requests
    /// only, which loosens a workspace that demands an API key as well.
    /// </summary>
    // Security-review av #68 (K4): scaffolden kunne nedgradere ApiKeyAndSignedRequest til SignedRequest uten et ord.
    // ApiKeyAndSignedRequest som standard ville krevd en lisensbred nøkkel i hver app, så scaffolden sier fra i stedet.
    public const string IngressNote =
        "queuey.deploy.json sets the workspace to take signed requests only (authMode SignedRequest). If your workspace already " +
        "demands an API key and a signature (ApiKeyAndSignedRequest), set authMode to that in the file before you apply, or " +
        "apply loosens it. queuey plan --profile dev shows the change before anything is written.";
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

    public static IReadOnlyList<PlannedFile> For(string root, string queueName, int retentionDays = PlanRetention.FreeDays)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A repository path is required.", nameof(root));
        if (string.IsNullOrWhiteSpace(queueName)) throw new ArgumentException("A queue name is required.", nameof(queueName));

        var planned = new List<PlannedFile>();

        var deployPath = Path.Combine(root, DeployFileName);
        var deployExists = File.Exists(deployPath);
        planned.Add(new PlannedFile(
            DeployFileName,
            DeploymentFileContent(queueName, retentionDays),
            deployExists ? "already exists — left alone" : "create",
            deployExists));

        var gitignore = GitignorePlan(root);
        if (gitignore is not null) planned.Add(gitignore);

        return planned;
    }

    /// <summary>
    /// A deployment file with one queue, the workspace's ingress security, and a dev profile. Every field left out means
    /// "leave alone", so a scaffold that says little changes little — and it carries no secrets by construction.
    ///
    /// The workspace takes signed requests with Queuey's own template, so every queue in it inherits that: a producer signs
    /// with the key <c>queuey keys mint --queue … --write .env</c> writes, and nothing else gets in. The template is named,
    /// since the file needs one. The environment comes from the profile, so <c>queuey apply --profile dev</c> creates a dev
    /// workspace secured that way, and production is one more profile (docs agent, 2026-10-09).
    ///
    /// No "$schema" line: the deployment parser rejects properties it does not
    /// know, so adding one would make the file we just wrote unreadable to
    /// `queuey apply`. A test parses this content to keep that honest.
    ///
    /// The retention is one the license's plan allows (<see cref="PlanRetention"/>): 30 was refused on Free, whose limit is 7
    /// (blind test 2026-10-09).
    /// </summary>
    private static string DeploymentFileContent(string queueName, int retentionDays) =>
        $$"""
        {
          "workspace": {
            "environment": "${QUEUEY_WORKSPACE_ENVIRONMENT}",
            "ingress": {
              "authMode": "SignedRequest",
              "signedRequest": { "template": "queuey" }
            }
          },
          "queues": {
            "{{queueName}}": {
              "dlqEnabled": true,
              "retentionDays": {{retentionDays}}
            }
          },
          "profiles": {
            "dev": { "variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "dev" } }
          }
        }

        """;

    /// <summary>The files a repository must keep out of git: the API key's <c>queuey.json</c>, and the signing key's <c>.env</c>.</summary>
    private static readonly (string File, string Why)[] Ignored =
    {
        (ConfigFileName, "a connection with an API key, if you use one"),
        (".env", "the signing key queuey keys mint --write .env writes"),
    };

    /// <summary>
    /// Keeps <c>queuey.json</c> and <c>.env</c> out of git. <c>queuey keys mint --write .env</c> refuses a file git would commit,
    /// so <c>.env</c> has to be ignored before the key is made.
    /// </summary>
    private static PlannedFile? GitignorePlan(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        var exists = File.Exists(path);
        var current = exists ? File.ReadAllText(path) : string.Empty;

        var lines = current.Split('\n').Select(line => line.Trim()).ToArray();
        var missing = Ignored.Where(i => !lines.Any(line => line == i.File || line == "/" + i.File)).ToArray();
        if (missing.Length == 0) return null;

        var addition = string.Concat(missing.Select(m =>
            $"{Environment.NewLine}# Queuey: {m.Why} — never commit it{Environment.NewLine}{m.File}{Environment.NewLine}"));
        var content = exists ? current.TrimEnd('\n', '\r') + addition : addition.TrimStart('\r', '\n');
        var names = string.Join(" and ", missing.Select(m => m.File));

        return new PlannedFile(
            ".gitignore",
            content,
            exists ? $"add {names}" : $"create, ignoring {names}",
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
