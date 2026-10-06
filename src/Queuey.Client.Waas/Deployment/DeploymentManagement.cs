using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Queuey.Client.Waas;

// Styrte ressurser (Queuey F2.4, 2026-10-06). En apply merker hver kø og hvert workspace den skriver, med fila den kom fra
// (repo, sti og commit), og Queuey avviser endringer fra andre steder. En person kan løsrive en ressurs; da hopper apply over
// den til --adopt tar den tilbake. Serveren kjenner en apply på et token den utsteder når applyen starter
// (POST /tenants/{t}/deployment/applies), som følger hver skriving i headeren X-Queuey-Apply.
//
// Kilden renses her før den sendes, og på serveren før den lagres: en repo-URL kan bære et token
// (https://token@github.com/…), og git remote get-url origin gir den som den står i .git/config.

/// <summary>Where a deployment file lives: its repository, its path from the repository root, and the commit.</summary>
public sealed class DeploymentFileSource
{
    /// <summary>The repository, without user info, query or fragment. A URL, <c>owner/repo</c>, or an SSH remote.</summary>
    public string? Repo { get; init; }

    /// <summary>The file's path from the repository root, with <c>/</c> as separator.</summary>
    public string? Path { get; init; }

    /// <summary>The commit the file is applied from.</summary>
    public string? Commit { get; init; }

    /// <summary>True when none of the three is known.</summary>
    public bool IsEmpty => Repo is null && Path is null && Commit is null;

    /// <summary>
    /// The source with every value cleaned the way Queuey stores it: user info, query and fragment stripped from the
    /// repository, the path relative with <c>/</c>, the commit in lower case. A value that cannot be one is dropped.
    /// </summary>
    public static DeploymentFileSource Clean(string? repo, string? path, string? commit) => new()
    {
        Repo = CleanRepo(repo),
        Path = CleanPath(path),
        Commit = CleanCommit(commit),
    };

    /// <summary>
    /// The repository without anything that could carry a secret: a URL keeps its scheme, host, port and path; the
    /// scp-style <c>user@host:path</c> loses <c>user@</c>; a trailing <c>.git</c> and <c>/</c> go. Null when nothing is left.
    /// </summary>
    public static string? CleanRepo(string? repo)
    {
        string? trimmed = repo?.Trim();
        if (trimmed is null || trimmed.Length == 0 || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return null;

        string cleaned;
        if (trimmed.Contains("://") && Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            if (string.IsNullOrEmpty(uri!.Host)) return null;
            string port = uri.IsDefaultPort || uri.Port < 0 ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            cleaned = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{uri.AbsolutePath}";
        }
        else
        {
            cleaned = trimmed;
            int cut = cleaned.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) cleaned = cleaned.Substring(0, cut);
            int slash = cleaned.IndexOf('/');
            int at = slash < 0 ? cleaned.LastIndexOf('@') : cleaned.LastIndexOf('@', slash);
            if (at >= 0) cleaned = cleaned.Substring(at + 1);
        }

        cleaned = cleaned.TrimEnd('/');
        if (cleaned.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring(0, cleaned.Length - 4);
        cleaned = cleaned.TrimEnd('/');

        return cleaned.Length == 0 || cleaned.EndsWith("://", StringComparison.Ordinal) || cleaned.Length > 512 ? null : cleaned;
    }

    /// <summary>The path relative to the repository root with <c>/</c>, or null for an absolute path or one that leaves it.</summary>
    public static string? CleanPath(string? path)
    {
        string? trimmed = path?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(trimmed) || trimmed!.Length > 512 || trimmed.Any(char.IsControl))
            return null;
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
            trimmed = trimmed.Substring(2);
        if (trimmed.StartsWith("/", StringComparison.Ordinal) || (trimmed.Length > 2 && trimmed[1] == ':' && trimmed[2] == '/')
            || trimmed.Split('/').Any(segment => segment == ".."))
            return null;
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>The commit in lower case when it is 7 to 64 hexadecimal characters, otherwise null.</summary>
    public static string? CleanCommit(string? commit)
    {
        string? trimmed = commit?.Trim().ToLowerInvariant();
        return trimmed is { Length: >= 7 and <= 64 } && trimmed.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? trimmed : null;
    }
}

/// <summary>Who applied, detached or changed a managed queue or workspace.</summary>
public sealed class DeploymentActorInfo
{
    /// <summary><c>user</c>, <c>api_client</c> or <c>system</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>A person's name as it is now, or the API client's name when it acted.</summary>
    public string? Name { get; init; }

    /// <summary>The API client (<c>cli_…</c>) when an API key acted.</summary>
    public string? ApiClientPublicId { get; init; }

    /// <inheritdoc />
    public override string ToString() => Kind == "api_client" && ApiClientPublicId is { } client
        ? $"{Name ?? client} ({client})"
        : Name ?? Kind ?? "unknown";
}

/// <summary>
/// Whether a deployment file manages a queue or workspace, and from where, as Queuey reads it back. A later Queuey may add
/// states; a state this SDK does not know counts as managed.
/// </summary>
public sealed class DeploymentManagementInfo
{
    /// <summary>The state a resource has once an apply wrote it: its configuration is the deployment file's.</summary>
    public const string Managed = "managed";

    /// <summary>The state a resource has once a person detached it: apply skips it until it is adopted.</summary>
    public const string Detached = "detached";

    /// <summary><see cref="Managed"/>, <see cref="Detached"/>, or a later state.</summary>
    public string? State { get; init; }

    /// <summary>The repository the file is in, without user info.</summary>
    public string? Repo { get; init; }

    /// <summary>The file's path from the repository root.</summary>
    public string? Path { get; init; }

    /// <summary>The commit the file was applied from.</summary>
    public string? Commit { get; init; }

    /// <summary>The file as a person reads it, <c>repo/path</c>.</summary>
    public string? File { get; init; }

    /// <summary>An <c>https</c> link to the repository, or null.</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>When the last apply that touched the resource started.</summary>
    public DateTimeOffset? AppliedAtUtc { get; init; }

    /// <summary>Who ran it.</summary>
    public DeploymentActorInfo? AppliedBy { get; init; }

    /// <summary>When a person detached the resource.</summary>
    public DateTimeOffset? DetachedAtUtc { get; init; }

    /// <summary>Who detached it.</summary>
    public DeploymentActorInfo? DetachedBy { get; init; }

    /// <summary>Why, in their words.</summary>
    public string? DetachReason { get; init; }

    /// <summary>The last change made outside the file while Queuey only warned about such changes.</summary>
    public DateTimeOffset? ChangedOutsideAtUtc { get; init; }

    /// <summary>Who made it.</summary>
    public DeploymentActorInfo? ChangedOutsideBy { get; init; }

    /// <summary>True when a person detached the resource, so apply skips it.</summary>
    public bool IsDetached => string.Equals(State, Detached, StringComparison.Ordinal);

    /// <summary>
    /// What a person reads about a detached resource: who detached it, when and why. Empty for one that is not detached.
    /// </summary>
    public string DetachedText()
    {
        if (!IsDetached) return string.Empty;
        var parts = new List<string> { "detached" };
        if (DetachedBy is { } by) parts.Add("by " + by);
        if (DetachedAtUtc is { } at) parts.Add("at " + at.ToString("u", CultureInfo.InvariantCulture));
        string text = string.Join(" ", parts);
        return DetachReason is { Length: > 0 } reason ? $"{text}: {reason}" : text;
    }
}

/// <summary>A queue or the workspace an apply, plan or check left alone because a person detached it.</summary>
public sealed class SkippedResource
{
    /// <summary><c>workspace</c>, or <c>queues.&lt;name&gt;</c>.</summary>
    public string Target { get; init; } = default!;

    /// <summary>The queue's name, or null for the workspace.</summary>
    public string? QueueName { get; init; }

    /// <summary>The management Queuey read, with who detached it, when and why.</summary>
    public DeploymentManagementInfo Management { get; init; } = default!;

    /// <summary>How to take it back: the <c>--adopt</c> value.</summary>
    public string AdoptAs => QueueName ?? "workspace";

    /// <inheritdoc />
    public override string ToString() => $"{Target}: {Management.DetachedText()}";
}

/// <summary>
/// What a deployment check found: the differences applying would change, and the resources it leaves alone because a
/// person detached them. A detached resource is not drift: apply skips it.
/// </summary>
public sealed class DeploymentCheck
{
    /// <summary>What applying the file would change.</summary>
    public IReadOnlyList<DriftItem> Drift { get; init; } = Array.Empty<DriftItem>();

    /// <summary>The queues, and the workspace, apply would skip, with who detached them, when and why.</summary>
    public IReadOnlyList<SkippedResource> Detached { get; init; } = Array.Empty<SkippedResource>();

    /// <summary>True when applying the file would change nothing.</summary>
    public bool InSync => Drift.Count == 0;
}

/// <summary>What an apply or a plan takes back from a detach (<c>queuey apply --adopt</c>).</summary>
public static class DeploymentAdopt
{
    /// <summary>The value that adopts the workspace's own settings.</summary>
    public const string Workspace = "workspace";

    private const string QueuePrefix = "queue:";

    /// <summary>
    /// The adopt values in <paramref name="text"/>, comma-separated: <c>workspace</c>, a queue name, or
    /// <c>queue:&lt;name&gt;</c>. Blank entries go. A queue is its bare name, except a queue named <c>workspace</c>, which
    /// keeps its <c>queue:</c> so it is not read as the workspace.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
        => (text ?? string.Empty)
            .Split(',')
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Select(Normalize)
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The target a plan and an apply give an adopt value: <c>workspace</c>, or <c>queues.&lt;name&gt;</c>.</summary>
    public static string TargetOf(string value)
        => IsWorkspace(value) ? Workspace : "queues." + QueueNameOf(value);

    // Et kønavn har aldri kolon (QueueyName), så queue:-formen er entydig.
    private static string Normalize(string value)
    {
        if (IsWorkspace(value)) return Workspace;
        if (!value.StartsWith(QueuePrefix, StringComparison.OrdinalIgnoreCase)) return value;
        string name = value.Substring(QueuePrefix.Length).Trim();
        return string.Equals(name, Workspace, StringComparison.OrdinalIgnoreCase) ? QueuePrefix + name : name;
    }

    private static bool IsWorkspace(string value) => string.Equals(value, Workspace, StringComparison.OrdinalIgnoreCase);

    private static string QueueNameOf(string value)
        => value.StartsWith(QueuePrefix, StringComparison.OrdinalIgnoreCase) ? value.Substring(QueuePrefix.Length).Trim() : value;

    internal static bool AdoptsWorkspace(IReadOnlyList<string>? adopt) => adopt?.Any(IsWorkspace) == true;

    internal static IReadOnlyList<string> Queues(IReadOnlyList<string>? adopt)
        => adopt?.Where(a => !IsWorkspace(a)).Select(QueueNameOf).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
}

// ── Wire ──────────────────────────────────────────────────────────────────────

internal sealed class DeploymentManagementResponse
{
    public string? State { get; set; }
    public string? Repo { get; set; }
    public string? Path { get; set; }
    public string? Commit { get; set; }
    public string? File { get; set; }
    public string? RepositoryUrl { get; set; }
    public DateTimeOffset? AppliedAtUtc { get; set; }
    public DeploymentActorResponse? AppliedBy { get; set; }
    public DateTimeOffset? DetachedAtUtc { get; set; }
    public DeploymentActorResponse? DetachedBy { get; set; }
    public string? DetachReason { get; set; }
    public DateTimeOffset? ChangedOutsideAtUtc { get; set; }
    public DeploymentActorResponse? ChangedOutsideBy { get; set; }

    public DeploymentManagementInfo ToInfo() => new()
    {
        State = State,
        Repo = Repo,
        Path = Path,
        Commit = Commit,
        File = File,
        RepositoryUrl = RepositoryUrl,
        AppliedAtUtc = AppliedAtUtc,
        AppliedBy = AppliedBy?.ToInfo(),
        DetachedAtUtc = DetachedAtUtc,
        DetachedBy = DetachedBy?.ToInfo(),
        DetachReason = DetachReason,
        ChangedOutsideAtUtc = ChangedOutsideAtUtc,
        ChangedOutsideBy = ChangedOutsideBy?.ToInfo(),
    };
}

internal sealed class DeploymentActorResponse
{
    public string? Kind { get; set; }
    public string? Name { get; set; }
    public string? ApiClientPublicId { get; set; }

    public DeploymentActorInfo ToInfo() => new() { Kind = Kind, Name = Name, ApiClientPublicId = ApiClientPublicId };
}

/// <summary>Wire request for <c>POST /tenants/{t}/deployment/applies</c>.</summary>
internal sealed class StartApplyWireRequest
{
    public StartApplySourceWire? Source { get; set; }
    public StartApplyAdoptWire? Adopt { get; set; }
}

internal sealed class StartApplySourceWire
{
    public string? Repo { get; set; }
    public string? Path { get; set; }
    public string? Commit { get; set; }
}

internal sealed class StartApplyAdoptWire
{
    public bool Workspace { get; set; }
    public List<string>? Queues { get; set; }
}

/// <summary>Wire response for <c>POST /tenants/{t}/deployment/applies</c>.</summary>
internal sealed class StartApplyWireResponse
{
    public string? Token { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string? Enforcement { get; set; }
    public DeploymentManagementResponse? Workspace { get; set; }
}

/// <summary>Wire response for <c>GET /tenants/{t}</c>, the part a deployment check reads.</summary>
internal sealed class TenantDeploymentResponse
{
    public DeploymentManagementResponse? Deployment { get; set; }
}
