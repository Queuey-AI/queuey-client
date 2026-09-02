namespace Queuey.Client.Waas;

/// <summary>
/// Inline overrides supplied when registering a queue (<c>AddQueue&lt;T&gt;(cfg =&gt; …)</c>). Any value
/// left unset inherits from the type's <see cref="QueueyQueueAttribute"/>, then convention.
/// </summary>
public sealed class QueueOptions
{
    /// <summary>
    /// Overrides the queue name (else attribute name, else the CLR type name normalized). A name set
    /// here is validated, never rewritten.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Policy overrides, laid over the attribute's per field — a value set here wins, a null here
    /// keeps whatever the attribute declared, and null in both means inherit from the workspace.
    /// </summary>
    public QueuePolicy Policy { get; } = new();
}
