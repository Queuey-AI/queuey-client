using System;

namespace Queuey.Client.Waas;

/// <summary>
/// The resolved, immutable declaration of a Queuey queue — the desired state <c>SyncQueues</c>
/// applies. Produced from a <see cref="QueueyQueueAttribute"/>, inline registration options, or
/// convention.
/// </summary>
public sealed class QueueDefinition
{
    /// <summary>The CLR type this queue was declared on, or <c>null</c> for a name-only queue.</summary>
    public Type? ModelType { get; init; }

    /// <summary>The queue name — the string you publish to, and the ingress URL segment.</summary>
    public string Name { get; init; } = default!;

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>The declared behaviour. Null fields inherit from the workspace.</summary>
    public QueuePolicy Policy { get; init; } = new();
}
