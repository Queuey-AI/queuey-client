using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>
    /// Whether the receiver handles the same event twice safely. Then Queuey sends an event again after a timeout or a
    /// conflict (409, 412, 423 or 428). Otherwise a timeout holds the queue until a person resumes it, and a conflict
    /// stops the event. It does not deduplicate publishes.
    /// </summary>
    public bool? Idempotent { get; set; }

    /// <summary>
    /// How long to wait between attempts. The number of attempts is not a setting: Queuey decides what
    /// each failure needs.
    /// </summary>
    public RetryBackoff? Backoff { get; set; }

    /// <summary>
    /// Which events this queue delivers. Events that do not match are kept as <c>Filtered</c> and
    /// never delivered. An empty condition list delivers everything, which is how a filter is removed.
    /// </summary>
    public DeliveryFilter? Filter { get; set; }

    /// <summary>True when nothing is declared — the queue inherits its whole behaviour.</summary>
    public bool IsEmpty =>
        Ordering is null && DlqEnabled is null && RetentionDays is null && Idempotent is null
        && (Backoff is null || Backoff.IsEmpty) && Filter is null;

    /// <summary>The ordering values the backend accepts.</summary>
    internal static readonly string[] OrderingValues = { "fifo", "bykey", "besteffort" };

    /// <summary>A copy with <paramref name="overrides"/>' non-null fields laid over this one.</summary>
    internal QueuePolicy OverlaidWith(QueuePolicy? overrides) => overrides is null ? Copy() : new QueuePolicy
    {
        Ordering = overrides.Ordering ?? Ordering,
        DlqEnabled = overrides.DlqEnabled ?? DlqEnabled,
        RetentionDays = overrides.RetentionDays ?? RetentionDays,
        Idempotent = overrides.Idempotent ?? Idempotent,
        Backoff = overrides.Backoff ?? Backoff,
        Filter = overrides.Filter ?? Filter,
    };

    internal QueuePolicy Copy() => new()
    {
        Ordering = Ordering,
        DlqEnabled = DlqEnabled,
        RetentionDays = RetentionDays,
        Idempotent = Idempotent,
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
            return FormattableString.Invariant($"RetentionDays cannot be negative; got {days}.");

        return Backoff?.Validate() ?? Filter?.Validate();
    }
}

/// <summary>How long a queue waits between attempts. Null fields inherit.</summary>
public sealed class RetryBackoff
{
    /// <summary>
    /// The first wait, in milliseconds: above 0, and at most 3600000 (one hour) unless a longer wait is
    /// already in place. Each later wait doubles, up to <see cref="MaxDelayMs"/>.
    /// </summary>
    public int? BaseDelayMs { get; set; }

    /// <summary>
    /// The longest wait between two attempts, in milliseconds: above 0, not below the first wait, and at
    /// most 86400000 (24 hours) unless a longer wait is already in place.
    /// </summary>
    public int? MaxDelayMs { get; set; }

    /// <summary><c>full</c> spreads the waits randomly so retries do not arrive in step; <c>none</c> does not.</summary>
    public string? Jitter { get; set; }

    internal bool IsEmpty => BaseDelayMs is null && MaxDelayMs is null && Jitter is null;

    internal static readonly string[] JitterValues = { "none", "full" };

    // Samme tak som backenden (PolicyValidation, Queuey#391, 2026-10-04). De gjelder bare en skriving som
    // endrer ventetiden, så de sjekkes mot det workspacet har før apply skriver (BackoffCeilings), ikke her:
    // en lengre ventetid fra før taket skal kunne stå uendret i fila. static readonly, ikke const: en const
    // bakes inn i den som bruker den, og et tak backenden endrer, skal følge med en ny SDK.

    /// <summary>
    /// The longest first wait a write may set: 3600000 ms, one hour. A longer wait that is already in
    /// place stays; Queuey refuses only a write that changes it.
    /// </summary>
    public static readonly int BaseDelayCeilingMs = 3_600_000;

    /// <summary>
    /// The longest wait a write may set: 86400000 ms, 24 hours. A longer wait that is already in place
    /// stays; Queuey refuses only a write that changes it.
    /// </summary>
    public static readonly int MaxDelayCeilingMs = 86_400_000;

    internal string? Validate()
    {
        // Over 0, som backenden krever av den effektive ventetiden (PolicyValidation.ValidateBackoff). 0 gikk gjennom
        // her og ble avvist der (review 2026-10-05). Tall skrives invariant, som i backenden: med nb-NO ble -1 til
        // «−1» med U+2212.
        if (BaseDelayMs is { } b and <= 0)
            return FormattableString.Invariant($"Backoff.BaseDelayMs must be above 0; got {b}.");
        if (MaxDelayMs is { } m and <= 0)
            return FormattableString.Invariant($"Backoff.MaxDelayMs must be above 0; got {m}.");
        if (BaseDelayMs is { } bb && MaxDelayMs is { } mm && mm < bb)
            return FormattableString.Invariant($"Backoff.MaxDelayMs ({mm}) cannot be below BaseDelayMs ({bb}).");
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

    /// <summary>
    /// The conditions, required whenever a filter is declared. An empty list delivers every event, which
    /// is how a filter is removed, so a filter without the list is refused rather than read as empty.
    /// </summary>
    public List<DeliveryFilterCondition>? Conditions { get; set; }

    internal static readonly string[] MatchValues = { "all", "any" };

    // Samme grenser som backenden (DeliveryFilterPolicy): filteret kjøres for hver levering.
    internal const int MaxConditions = 32;
    internal const int MaxFieldLength = 256;
    internal const int MaxValueLength = 4096;

    internal string? Validate()
    {
        if (WholeFilterProblem() is { } whole)
            return whole;

        foreach (DeliveryFilterCondition? c in Conditions!)
        {
            if (ConditionProblem(c) is { } problem)
                return problem;
        }

        return null;
    }

    /// <summary>What Queuey would refuse in the filter as a whole, before its conditions one by one.</summary>
    internal string? WholeFilterProblem()
    {
        if (Match != null && Array.IndexOf(MatchValues, Match) < 0)
            return $"Filter.Match must be one of {string.Join(", ", MatchValues)}; got '{Match}'.";

        // En manglende liste ble lest som tom, og en tom liste leverer alt: "filter": {"match": "any"} fjernet det
        // lagrede filteret, og køen leverte hvert event (review 2026-10-05). Backenden avviser det samme (Queuey#391).
        if (Conditions is null)
            return "A filter needs its conditions. To remove the filter, write \"conditions\": [].";

        if (Conditions.Count > MaxConditions)
            return FormattableString.Invariant($"A filter supports at most {MaxConditions} conditions; got {Conditions.Count}.");

        return null;
    }

    /// <summary>What Queuey would refuse in one condition, or null when it would take it as written.</summary>
    internal static string? ConditionProblem(DeliveryFilterCondition? c)
    {
        // "conditions": [null] krasjet CLI-en med NullReferenceException og stack trace (review 2026-10-05).
        if (c is null)
            return "A filter condition cannot be null.";

        if (string.IsNullOrWhiteSpace(c.Field))
            return "Every filter condition needs a field.";
        if (c.Field.Length > MaxFieldLength)
            return FormattableString.Invariant($"Filter field '{c.Field.Substring(0, 40)}…' is longer than {MaxFieldLength} characters.");

        // Workeren slår opp feltet nøyaktig slik det står, så "amount " treffer aldri. Backenden avviser det i
        // stedet for å trimme (Queuey#391, 2026-10-04); her avvises det før noe er sendt.
        if (c.Field != c.Field.Trim())
            return $"Filter field '{c.Field}' has whitespace around it, so it never matches: Queuey looks a field up exactly as written. Write it as '{c.Field.Trim()}'.";

        if (Array.IndexOf(DeliveryFilterCondition.OpValues, c.Op) < 0)
            return $"Filter op on field '{c.Field}' must be one of {string.Join(", ", DeliveryFilterCondition.OpValues)}; got '{c.Op}'.";

        // exists leser aldri verdien, så "exists" med "false", ment som «feltet skal mangle», leverte bare events
        // som har feltet. Backenden avviser en verdi på exists (Queuey#391, 2026-10-04).
        if (c.Op == "exists" && c.Value is not null)
            return $"Filter condition '{c.Field} exists' takes no value: it matches every event that has the field, whatever the value. Leave the value out.";
        if (c.Op != "exists" && c.Value is null)
            return $"Filter condition '{c.Field} {c.Op}' needs a value.";
        if (c.Value is { Length: > MaxValueLength })
            return FormattableString.Invariant($"Filter value for field '{c.Field}' is longer than {MaxValueLength} characters.");

        if (Array.IndexOf(NumberOps, c.Op) >= 0 && NumberRefusal(c.Value!) is { } refusal)
            return $"Filter condition '{c.Field} {c.Op}' compares numbers, and {refusal}";

        return null;
    }

    /// <summary>The ops that compare numbers; their value must be one.</summary>
    internal static readonly string[] NumberOps = { "gt", "gte", "lt", "lte" };

    // Lest som backendens deploy-kontrakt leser tallet (Queuey#391, 2026-10-04): NumberStyles.Float og invariant
    // kultur. Workeren leser med NumberStyles.Any, der komma er tusenskilletegn, så "1,5" ble lagret og sammenlignet
    // som 15; "(5)", "5-" og "¤5" gikk også gjennom. Alt Float godtar, leser workeren som det samme tallet.
    private static string? NumberRefusal(string value)
    {
        bool parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number);

        // Et tall som renner over, blir uendelig; "Infinity" og "NaN" er ord, ikke tall. På .NET Framework gir
        // overløp false fra TryParse, så der står det «not a number» i stedet for «out of range». Avvist blir det likt.
        if (parsed && double.IsInfinity(number) && value.Any(ch => ch >= '0' && ch <= '9'))
            return $"'{Quote(value)}' is out of range.";

        return parsed && !double.IsInfinity(number) && !double.IsNaN(number)
            ? null
            : $"'{Quote(value)}' is not a number. Write it like 1.5: a point for decimals, and no thousands separators, currency or parentheses.";
    }

    /// <summary>A value as an error message quotes it: at most 40 characters, as the backend quotes it.</summary>
    private static string Quote(string value) => value.Length <= 40 ? value : value.Substring(0, 40) + "…";

    /// <summary>The filter on one line — <c>any: type eq order.created; priority exists</c>.</summary>
    public override string ToString()
        => Conditions is null ? "(no conditions)" : Conditions.Count == 0 ? "(delivers every event)" : Describe();

    /// <summary>A stable text form, for drift reports: <c>any: type eq order.created; priority exists</c>.</summary>
    internal string Describe()
        => $"{(Match ?? "all").ToLowerInvariant()}: "
           + string.Join("; ", (Conditions ?? new List<DeliveryFilterCondition>()).Select(Describe));

    /// <summary>One condition as a filter line writes it: <c>priority exists</c>, <c>amount gt 5</c>.</summary>
    internal static string Describe(DeliveryFilterCondition? c)
        => c is null ? "(null)" : c.Value is null ? $"{c.Field} {c.Op}" : $"{c.Field} {c.Op} {c.Value}";
}

/// <summary>One condition on a top-level JSON body field.</summary>
public sealed class DeliveryFilterCondition
{
    /// <summary>The top-level field of the JSON body, exactly as it is written there: no whitespace around it.</summary>
    public string Field { get; set; } = default!;

    /// <summary><c>eq</c>, <c>ne</c>, <c>gt</c>, <c>gte</c>, <c>lt</c>, <c>lte</c>, <c>contains</c> or <c>exists</c>.</summary>
    public string Op { get; set; } = default!;

    /// <summary>
    /// The value to compare with. <c>gt</c>, <c>gte</c>, <c>lt</c> and <c>lte</c> take a plain number written
    /// like <c>1.5</c>. <c>exists</c> takes no value.
    /// </summary>
    public string? Value { get; set; }

    internal static readonly string[] OpValues = { "eq", "ne", "gt", "gte", "lt", "lte", "contains", "exists" };
}
