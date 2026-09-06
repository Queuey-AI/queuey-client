using System;

namespace Queuey.Client.Waas;

/// <summary>
/// Resolves a <see cref="QueueDefinition"/> from a type and/or inline options.
/// Precedence per field: <b>inline <see cref="QueueOptions"/> &gt; <see cref="QueueyQueueAttribute"/> &gt; convention</b>.
/// </summary>
internal static class QueueDefinitionFactory
{
    public static QueueDefinition FromType(Type type, QueueOptions? overrides)
    {
        if (type is null) throw new ArgumentNullException(nameof(type));

        var attr = (QueueyQueueAttribute?)Attribute.GetCustomAttribute(type, typeof(QueueyQueueAttribute), inherit: false);

        string name = ResolveName(type, FirstNonBlank(overrides?.Name, attr?.Name));
        QueuePolicy policy = (attr?.ToPolicy() ?? new QueuePolicy()).OverlaidWith(overrides?.Policy);
        EnsureValidPolicy(policy, name);

        return new QueueDefinition
        {
            ModelType = type,
            Name = name,
            Policy = policy,
        };
    }

    public static QueueDefinition FromName(string name, QueueOptions? overrides)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A queue name is required.", nameof(name));

        string resolved = (FirstNonBlank(overrides?.Name) ?? name).Trim();
        QueueyName.EnsureValid(resolved, "queue name");

        QueuePolicy policy = new QueuePolicy().OverlaidWith(overrides?.Policy);
        EnsureValidPolicy(policy, resolved);

        return new QueueDefinition
        {
            ModelType = null,
            Name = resolved,
            Policy = policy,
        };
    }

    /// <summary>
    /// Same contract as streams: a name the caller wrote is validated as written, and only the
    /// convention fallback is normalized (the CLR type name is a C# identifier, not a chosen name).
    /// </summary>
    private static string ResolveName(Type type, string? declared)
    {
        if (declared != null)
        {
            string explicitName = declared.Trim();
            QueueyName.EnsureValid(explicitName, "queue name");
            return explicitName;
        }

        string derived = QueueyName.Normalize(type.Name);
        if (QueueyName.Validate(derived) is { } reason)
        {
            throw new QueueyConfigurationException(
                $"Could not derive a valid Queuey queue name from type '{type.Name}'" +
                (derived.Length == 0 ? "" : $" (got '{derived}')") + $": {reason} " +
                $"Name it explicitly with [QueueyQueue(\"…\")] or AddQueue<{type.Name}>(o => o.Name = \"…\").");
        }

        return derived;
    }

    private static void EnsureValidPolicy(QueuePolicy policy, string queueName)
    {
        if (policy.Validate() is { } reason)
            throw new QueueyConfigurationException($"Queue '{queueName}' has an invalid policy: {reason}");
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (string? v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}
