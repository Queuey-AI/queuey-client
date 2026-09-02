using System;

namespace Queuey.Client.Waas;

/// <summary>
/// The behaviour a queue declares. Every field is nullable and <c>null</c> means <b>inherit</b> — the
/// value resolves from the workspace, exactly as a null leaf does in the backend's own policy
/// cascade. Declaring a field makes the code its owner; leaving it null leaves it to the workspace.
/// </summary>
/// <remarks>
/// Deliberately six fields. The privacy and compliance levers a queue also has — payload visibility,
/// AI processing, payload lifetime, response capture — are not declarable here: they belong to
/// whoever owns the data-processing agreement, not to a developer editing an attribute in a feature
/// branch. They inherit from the workspace.
/// </remarks>
public sealed class QueuePolicy
{
    /// <summary>Delivery ordering: <c>fifo</c>, <c>bykey</c> or <c>besteffort</c>.</summary>
    public string? Ordering { get; set; }

    /// <summary>How many delivery attempts before the event is parked.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>Whether a dead-letter queue collects events that exhaust their attempts.</summary>
    public bool? DlqEnabled { get; set; }

    /// <summary>How many attempts before an event is dead-lettered.</summary>
    public int? DlqAfterAttempts { get; set; }

    /// <summary>How many days events are retained.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>Whether duplicate publishes are collapsed by idempotency key.</summary>
    public bool? Idempotent { get; set; }

    /// <summary>True when nothing is declared — the queue inherits its whole behaviour.</summary>
    public bool IsEmpty =>
        Ordering is null && MaxAttempts is null && DlqEnabled is null
        && DlqAfterAttempts is null && RetentionDays is null && Idempotent is null;

    /// <summary>The ordering values the backend accepts.</summary>
    internal static readonly string[] OrderingValues = { "fifo", "bykey", "besteffort" };

    /// <summary>A copy with <paramref name="overrides"/>' non-null fields laid over this one.</summary>
    internal QueuePolicy OverlaidWith(QueuePolicy? overrides) => overrides is null ? Copy() : new QueuePolicy
    {
        Ordering = overrides.Ordering ?? Ordering,
        MaxAttempts = overrides.MaxAttempts ?? MaxAttempts,
        DlqEnabled = overrides.DlqEnabled ?? DlqEnabled,
        DlqAfterAttempts = overrides.DlqAfterAttempts ?? DlqAfterAttempts,
        RetentionDays = overrides.RetentionDays ?? RetentionDays,
        Idempotent = overrides.Idempotent ?? Idempotent,
    };

    internal QueuePolicy Copy() => new()
    {
        Ordering = Ordering,
        MaxAttempts = MaxAttempts,
        DlqEnabled = DlqEnabled,
        DlqAfterAttempts = DlqAfterAttempts,
        RetentionDays = RetentionDays,
        Idempotent = Idempotent,
    };

    /// <summary>
    /// Validates the declared values locally, so a typo fails at registration rather than as a 400
    /// midway through a deploy. Returns null when valid, otherwise the reason.
    /// </summary>
    internal string? Validate()
    {
        if (Ordering != null && Array.IndexOf(OrderingValues, Ordering) < 0)
            return $"Ordering must be one of {string.Join(", ", OrderingValues)}; got '{Ordering}'.";
        if (MaxAttempts is { } attempts and < 1)
            return $"MaxAttempts must be at least 1; got {attempts}.";
        if (DlqAfterAttempts is { } dlq and < 1)
            return $"DlqAfterAttempts must be at least 1; got {dlq}.";
        if (RetentionDays is { } days and < 0)
            return $"RetentionDays cannot be negative; got {days}.";
        return null;
    }
}
