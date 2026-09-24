using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// The behaviour a queue declares. Every field is nullable and <c>null</c> means <b>inherit</b> — the
/// value resolves from the workspace, exactly as a null leaf does in the backend's own policy
/// cascade. Declaring a field makes the code its owner; leaving it null leaves it to the workspace.
/// </summary>
/// <remarks>
/// The privacy and compliance levers a queue also has — payload visibility, AI processing, payload
/// lifetime, response capture — are not declarable here: they belong to whoever owns the
/// data-processing agreement, not to a developer editing an attribute in a feature branch. They
/// inherit from the workspace.
/// </remarks>
public sealed class QueuePolicy
{
    /// <summary>Delivery ordering: <c>fifo</c>, <c>bykey</c> or <c>besteffort</c>.</summary>
    public string? Ordering { get; set; }

    /// <summary>Whether a dead-letter queue collects events the receiver rejected.</summary>
    public bool? DlqEnabled { get; set; }

    /// <summary>How many days events are retained.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>Whether duplicate publishes are collapsed by idempotency key.</summary>
    public bool? Idempotent { get; set; }

    /// <summary>How many times an event is attempted before it gives up.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>After how many attempts an event goes to the dead-letter queue. Must be below <see cref="MaxAttempts"/>.</summary>
    public int? DlqAfterAttempts { get; set; }

    /// <summary>How long to wait between attempts.</summary>
    public RetryBackoff? Backoff { get; set; }

    /// <summary>
    /// Which events this queue delivers. Events that do not match are kept as <c>Filtered</c> and
    /// never delivered. An empty condition list delivers everything, which is how a filter is removed.
    /// </summary>
    public DeliveryFilter? Filter { get; set; }

    /// <summary>True when nothing is declared — the queue inherits its whole behaviour.</summary>
    public bool IsEmpty =>
        Ordering is null && DlqEnabled is null && RetentionDays is null && Idempotent is null
        && MaxAttempts is null && DlqAfterAttempts is null && (Backoff is null || Backoff.IsEmpty)
        && Filter is null;

    /// <summary>The ordering values the backend accepts.</summary>
    internal static readonly string[] OrderingValues = { "fifo", "bykey", "besteffort" };

    /// <summary>A copy with <paramref name="overrides"/>' non-null fields laid over this one.</summary>
    internal QueuePolicy OverlaidWith(QueuePolicy? overrides) => overrides is null ? Copy() : new QueuePolicy
    {
        Ordering = overrides.Ordering ?? Ordering,
        DlqEnabled = overrides.DlqEnabled ?? DlqEnabled,
        RetentionDays = overrides.RetentionDays ?? RetentionDays,
        Idempotent = overrides.Idempotent ?? Idempotent,
        MaxAttempts = overrides.MaxAttempts ?? MaxAttempts,
        DlqAfterAttempts = overrides.DlqAfterAttempts ?? DlqAfterAttempts,
        Backoff = overrides.Backoff ?? Backoff,
        Filter = overrides.Filter ?? Filter,
    };

    internal QueuePolicy Copy() => new()
    {
        Ordering = Ordering,
        DlqEnabled = DlqEnabled,
        RetentionDays = RetentionDays,
        Idempotent = Idempotent,
        MaxAttempts = MaxAttempts,
        DlqAfterAttempts = DlqAfterAttempts,
        Backoff = Backoff,
        Filter = Filter,
    };

    /// <summary>
    /// A local check that mirrors the backend's, so a typo fails before anything is written. Null
    /// when the policy is valid, otherwise what is wrong and the values that work.
    /// </summary>
    internal string? Validate()
    {
        if (Ordering != null && Array.IndexOf(OrderingValues, Ordering) < 0)
            return $"Ordering must be one of {string.Join(", ", OrderingValues)}; got '{Ordering}'.";

        if (RetentionDays is { } days and < 0)
            return $"RetentionDays cannot be negative; got {days}.";

        if (MaxAttempts is { } max and < 1)
            return $"MaxAttempts must be at least 1; got {max}.";

        if (DlqAfterAttempts is { } after and < 1)
            return $"DlqAfterAttempts must be at least 1; got {after}.";

        if (MaxAttempts is { } m && DlqAfterAttempts is { } a && a >= m)
            return $"DlqAfterAttempts ({a}) must be below MaxAttempts ({m}), so some retries happen before an event goes to the DLQ.";

        return Backoff?.Validate() ?? Filter?.Validate();
    }
}

/// <summary>How long a queue waits between attempts. Null fields inherit.</summary>
public sealed class RetryBackoff
{
    /// <summary>The first wait, in milliseconds. Each later wait doubles, up to <see cref="MaxDelayMs"/>.</summary>
    public int? BaseDelayMs { get; set; }

    /// <summary>The longest wait between two attempts, in milliseconds.</summary>
    public int? MaxDelayMs { get; set; }

    /// <summary><c>full</c> spreads the waits randomly so retries do not arrive in step; <c>none</c> does not.</summary>
    public string? Jitter { get; set; }

    internal bool IsEmpty => BaseDelayMs is null && MaxDelayMs is null && Jitter is null;

    internal static readonly string[] JitterValues = { "none", "full" };

    internal string? Validate()
    {
        if (BaseDelayMs is { } b and < 0)
            return $"Backoff.BaseDelayMs cannot be negative; got {b}.";
        if (MaxDelayMs is { } m and < 0)
            return $"Backoff.MaxDelayMs cannot be negative; got {m}.";
        if (BaseDelayMs is { } bb && MaxDelayMs is { } mm && mm < bb)
            return $"Backoff.MaxDelayMs ({mm}) cannot be below BaseDelayMs ({bb}).";
        if (Jitter != null && Array.IndexOf(JitterValues, Jitter) < 0)
            return $"Backoff.Jitter must be one of {string.Join(", ", JitterValues)}; got '{Jitter}'.";
        return null;
    }
}

/// <summary>
/// Which events a queue delivers: every condition (<c>match: all</c>, the default) or any of them
/// (<c>any</c>). A condition compares a top-level field of the JSON body.
/// </summary>
public sealed class DeliveryFilter
{
    /// <summary><c>all</c> or <c>any</c>.</summary>
    public string? Match { get; set; }

    /// <summary>The conditions. Empty delivers everything.</summary>
    public List<DeliveryFilterCondition> Conditions { get; set; } = new();

    internal static readonly string[] MatchValues = { "all", "any" };

    // Samme grenser som backenden (DeliveryFilterPolicy): filteret kjøres for hver levering.
    internal const int MaxConditions = 32;
    internal const int MaxFieldLength = 256;
    internal const int MaxValueLength = 4096;

    internal string? Validate()
    {
        if (Match != null && Array.IndexOf(MatchValues, Match) < 0)
            return $"Filter.Match must be one of {string.Join(", ", MatchValues)}; got '{Match}'.";

        if (Conditions.Count > MaxConditions)
            return $"A filter supports at most {MaxConditions} conditions; got {Conditions.Count}.";

        foreach (DeliveryFilterCondition c in Conditions)
        {
            if (string.IsNullOrWhiteSpace(c.Field))
                return "Every filter condition needs a field.";
            if (c.Field.Length > MaxFieldLength)
                return $"Filter field '{c.Field.Substring(0, 40)}…' is longer than {MaxFieldLength} characters.";
            if (Array.IndexOf(DeliveryFilterCondition.OpValues, c.Op) < 0)
                return $"Filter op on field '{c.Field}' must be one of {string.Join(", ", DeliveryFilterCondition.OpValues)}; got '{c.Op}'.";
            if (c.Op != "exists" && c.Value is null)
                return $"Filter condition '{c.Field} {c.Op}' needs a value.";
            if (c.Value is { Length: > MaxValueLength })
                return $"Filter value for field '{c.Field}' is longer than {MaxValueLength} characters.";
        }

        return null;
    }

    /// <summary>The filter on one line — <c>any: type eq order.created; priority exists</c>.</summary>
    public override string ToString() => Conditions.Count == 0 ? "(delivers every event)" : Describe();

    /// <summary>A stable text form, for drift reports: <c>any: type eq order.created; priority exists</c>.</summary>
    internal string Describe()
        => $"{(Match ?? "all").ToLowerInvariant()}: "
           + string.Join("; ", Conditions.Select(c => c.Value is null ? $"{c.Field} {c.Op}" : $"{c.Field} {c.Op} {c.Value}"));
}

/// <summary>One condition on a top-level JSON body field.</summary>
public sealed class DeliveryFilterCondition
{
    /// <summary>The top-level field of the JSON body.</summary>
    public string Field { get; set; } = default!;

    /// <summary><c>eq</c>, <c>ne</c>, <c>gt</c>, <c>gte</c>, <c>lt</c>, <c>lte</c>, <c>contains</c> or <c>exists</c>.</summary>
    public string Op { get; set; } = default!;

    /// <summary>The value to compare with. Not used by <c>exists</c>.</summary>
    public string? Value { get; set; }

    internal static readonly string[] OpValues = { "eq", "ne", "gt", "gte", "lt", "lte", "contains", "exists" };
}
