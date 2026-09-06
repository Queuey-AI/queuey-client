using System;

namespace Queuey.Client.Waas;

/// <summary>
/// Marks a class or struct as a Queuey <b>queue</b> — a delivery pipeline you publish into. When the
/// type is registered (<c>AddQueue&lt;T&gt;()</c>) and <c>SyncQueues</c> runs, the SDK ensures a queue
/// by this name exists and applies the behaviour declared here.
/// </summary>
/// <remarks>
/// <para>
/// A queue is not a <see cref="QueueyModelAttribute"/> stream. A stream is a catalog entry that
/// publishes events to integration partners; a queue is the pipeline that receives and delivers them.
/// A stream <i>has</i> a queue — so queues are the lower layer, and a full sync applies them first.
/// Use this attribute when you just want durable delivery to your own endpoint; use
/// <c>[QueueyModel]</c> when partners subscribe.
/// </para>
/// <para>
/// This attribute declares <b>identity and behaviour</b> only. The destination — endpoint URL,
/// outbound auth, signing — is deliberately not here: it differs per environment and carries secrets,
/// so it belongs in configuration, not in a type that ships in your assembly.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class QueueyQueueAttribute : Attribute
{
    /// <summary>Creates the attribute, optionally setting the queue <see cref="Name"/>.</summary>
    public QueueyQueueAttribute(string? name = null) => Name = name;

    /// <summary>
    /// Queue name — the string you publish to, and the segment that appears in the ingress URL. Must
    /// satisfy <see cref="QueueyName"/>: lowercase, starting with a letter or digit, then letters,
    /// digits, <c>.</c>, <c>-</c> or <c>_</c>. A value set here is validated as written; null/empty
    /// falls back to the CLR type name normalized (<c>OrderCreated</c> → <c>order-created</c>).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Delivery ordering: <c>fifo</c>, <c>bykey</c> or <c>besteffort</c>. Null inherits the workspace.
    /// </summary>
    public string? Ordering { get; set; }

    /// <summary>Whether a dead-letter queue collects events that exhaust their attempts. Null inherits.</summary>
    public bool DlqEnabled { get => _dlqEnabled ?? false; set => _dlqEnabled = value; }

    /// <summary>How many days events are retained. Null inherits the workspace.</summary>
    public int RetentionDays { get => _retentionDays ?? 0; set => _retentionDays = value; }

    /// <summary>Whether duplicate publishes are collapsed by idempotency key. Null inherits.</summary>
    public bool Idempotent { get => _idempotent ?? false; set => _idempotent = value; }

    private bool? _dlqEnabled;
    private int? _retentionDays;
    private bool? _idempotent;

    /// <summary>The declared policy, with every field the caller never set left as null (= inherit).</summary>
    internal QueuePolicy ToPolicy() => new()
    {
        Ordering = Ordering,
        DlqEnabled = _dlqEnabled,
        RetentionDays = _retentionDays,
        Idempotent = _idempotent,
    };
}
