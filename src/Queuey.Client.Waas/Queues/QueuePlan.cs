namespace Queuey.Client.Waas;

/// <summary>
/// A network-free preview of what <c>SyncQueues</c> would apply for one queue (used by
/// <c>--dry-run</c> and tests).
/// </summary>
public sealed class QueuePlan
{
    /// <summary>The declaring type's full name, or <c>null</c> for a name-only queue.</summary>
    public string? ModelType { get; init; }

    /// <summary>The queue name that would be applied.</summary>
    public string Name { get; init; } = default!;

    /// <summary>The description that would be applied.</summary>
    public string? Description { get; init; }

    /// <summary>The behaviour that would be declared. Null fields inherit from the workspace.</summary>
    public QueuePolicy Policy { get; init; } = new();

    /// <summary>Whether anything is declared at all, or the queue inherits its whole behaviour.</summary>
    public bool InheritsEverything => Policy.IsEmpty;
}
