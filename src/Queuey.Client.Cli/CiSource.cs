using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// Ref, workflow og PR i kilden til en plan Queuey lagrer (Queuey F3.11, BØR 2 fra reviewen av #64): den som godkjenner, skal se
// om planen kommer fra en PR-branch eller fra main. Verdiene er oppgitt, ikke bevist (PlanSource.Claimed); bare OIDC (C2) beviser
// en kilde. De leses fra GitHub Actions' miljø, aldri fra git, så ingenting nytt kjøres.
//
// I en PR-hendelse er GITHUB_HEAD_REF branchen endringen kommer fra. Den er ref-en, også for pull_request_target, der GITHUB_REF
// er base-branchen og ellers ville sett ut som main. PR-nummeret står i GITHUB_REF (refs/pull/N/merge), ellers i eventen
// (GITHUB_EVENT_PATH, pull_request.number).

/// <summary>Where a plan comes from in CI, beyond the repository, path and commit: the ref, the workflow and the pull request.</summary>
internal static class CiSource
{
    private static readonly Regex PullRef = new(@"^refs/pull/(\d{1,10})/", RegexOptions.CultureInvariant);

    /// <summary><paramref name="source"/> with the ref, workflow and pull request GitHub Actions gives, when it gives them.</summary>
    public static DeploymentFileSource? With(DeploymentFileSource? source, Func<string, string?> env)
    {
        string? githubRef = Value(env("GITHUB_REF"));
        string? headRef = Value(env("GITHUB_HEAD_REF"));
        string? workflow = Value(env("GITHUB_WORKFLOW_REF"));
        string? @ref = headRef is not null ? "refs/heads/" + headRef : githubRef;
        string? pullRequest = githubRef is not null && PullRef.Match(githubRef) is { Success: true } match
            ? match.Groups[1].Value
            : headRef is not null ? EventPullRequest(env("GITHUB_EVENT_PATH")) : null;

        if (@ref is null && workflow is null && pullRequest is null)
            return source;

        return new DeploymentFileSource
        {
            Repo = source?.Repo,
            Path = source?.Path,
            Commit = source?.Commit,
            Ref = @ref,
            Workflow = workflow,
            PullRequest = pullRequest,
        };
    }

    private static string? Value(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    // pull_request.number i eventen, eller null. En fil som ikke kan leses, gir ingen PR: kilden er en opplysning, ikke et krav.
    private static string? EventPullRequest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("pull_request", out JsonElement pr) && pr.ValueKind == JsonValueKind.Object
                   && pr.TryGetProperty("number", out JsonElement number) && number.TryGetInt64(out long n) && n > 0
                ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
