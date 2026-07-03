using System;

namespace Queuey.Client.Waas;

/// <summary>
/// Excludes a property from the generated <c>payloadSchema</c> of a <see cref="QueueyModelAttribute"/>
/// type. It does not affect the published event body — use <c>[JsonIgnore]</c> to drop a property from
/// the wire payload (the schema generator honors that too).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
public sealed class QueueyIgnoreAttribute : Attribute
{
}
