using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// Resolves a <see cref="StreamDefinition"/> from a model type and/or inline options.
/// Precedence per field: <b>inline <see cref="StreamOptions"/> &gt; <see cref="QueueyModelAttribute"/> &gt; convention</b>.
/// </summary>
internal static class StreamDefinitionFactory
{
    public static StreamDefinition FromType(Type type, StreamOptions? overrides, bool generateSchemasDefault = false)
    {
        if (type is null) throw new ArgumentNullException(nameof(type));

        var attr = (QueueyModelAttribute?)Attribute.GetCustomAttribute(type, typeof(QueueyModelAttribute), inherit: false);

        string name = ResolveName(type, FirstNonBlank(overrides?.Name, attr?.Name));

        // Precedence: explicit pre-rendered schema > (inline > attribute > builder-default) generation.
        bool generate = overrides?.GenerateSchema ?? (attr?.GenerateSchema == true ? true : generateSchemasDefault);
        string? payloadSchema = overrides?.PayloadSchema
                                ?? (generate ? SchemaWriter.ForType(type) : null);

        return new StreamDefinition
        {
            ModelType = type,
            Name = name,
            Description = overrides?.Description ?? attr?.Description,
            EventTypes = ResolveEventTypes(overrides, attr?.EventTypes),
            IsPublic = overrides?.IsPublic ?? attr?.IsPublic ?? true,
            Packages = ResolvePackages(overrides, attr?.Packages),
            PayloadSchema = payloadSchema,
        };
    }

    public static StreamDefinition FromName(string name, StreamOptions? overrides)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A stream name is required.", nameof(name));

        string resolved = (FirstNonBlank(overrides?.Name) ?? name).Trim();
        QueueyName.EnsureValid(resolved, "stream name");

        return new StreamDefinition
        {
            ModelType = null,
            Name = resolved,
            Description = overrides?.Description,
            EventTypes = ResolveEventTypes(overrides, null),
            IsPublic = overrides?.IsPublic ?? true,
            Packages = ResolvePackages(overrides, null),
            PayloadSchema = overrides?.PayloadSchema,
        };
    }

    /// <summary>
    /// Resolves the stream name and holds it to Queuey's naming contract. A name the caller wrote —
    /// on the attribute or inline — is validated, never rewritten: silently lowercasing it would
    /// leave <c>PushEvent("Order-Events", …)</c> publishing to a stream that no longer exists under
    /// that name. Only the convention fallback is normalized, because there the CLR type name is a
    /// C# identifier, not a name anyone chose (<c>OrderCreated</c> → <c>order-created</c>).
    /// </summary>
    private static string ResolveName(Type type, string? declared)
    {
        if (declared != null)
        {
            string explicitName = declared.Trim();
            QueueyName.EnsureValid(explicitName, "stream name");
            return explicitName;
        }

        string derived = QueueyName.Normalize(type.Name);
        if (QueueyName.Validate(derived) is { } reason)
        {
            throw new QueueyConfigurationException(
                $"Could not derive a valid Queuey stream name from type '{type.Name}'" +
                (derived.Length == 0 ? "" : $" (got '{derived}')") + $": {reason} " +
                $"Name it explicitly with [QueueyModel(\"…\")] or AddStream<{type.Name}>(o => o.Name = \"…\").");
        }

        return derived;
    }

    private static IReadOnlyList<string> ResolveEventTypes(StreamOptions? overrides, string[]? attributeEventTypes)
    {
        if (overrides != null && overrides.EventTypes.Count > 0)
            return overrides.EventTypes.ToArray();
        if (attributeEventTypes != null && attributeEventTypes.Length > 0)
            return attributeEventTypes.ToArray();
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ResolvePackages(StreamOptions? overrides, string[]? attributePackages)
    {
        if (overrides != null && overrides.Packages.Count > 0)
            return overrides.Packages.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        if (attributePackages != null && attributePackages.Length > 0)
            return attributePackages.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        return Array.Empty<string>();
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (string? v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}
