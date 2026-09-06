using System;
using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>The outcome of applying one queue during <c>SyncQueues</c>.</summary>
public sealed class QueueApplyResult
{
    /// <summary>The declaring type's full name, or empty for a name-only queue.</summary>
    public string ModelType { get; init; } = string.Empty;

    /// <summary>The queue name (echoes <see cref="QueueDefinition.Name"/>).</summary>
    public string Name { get; init; } = default!;

    /// <summary>Whether the apply succeeded (always <c>true</c> for a dry run).</summary>
    public bool Succeeded { get; init; }

    /// <summary>The queue public id (<c>que_…</c>) from the response, when applied.</summary>
    public string? PublicId { get; init; }

    /// <summary>Whether this run created the queue, as opposed to finding it already there.</summary>
    public bool Created { get; init; }

    /// <summary>Whether a policy patch was sent (false when the queue inherits everything).</summary>
    public bool PolicyApplied { get; init; }

    /// <summary>Non-fatal observations about this queue — see <see cref="ISyncRunResult.Warnings"/>.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>The typed error when the apply failed; otherwise <c>null</c>.</summary>
    public QueueyException? Error { get; init; }

    /// <summary>Whether this result came from a dry run (no API call was made).</summary>
    public bool DryRun { get; init; }
}
