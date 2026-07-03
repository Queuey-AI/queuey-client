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

        string name = FirstNonBlank(overrides?.Name, attr?.Name) ?? type.Name;

        // Precedence: explicit pre-rendered schema > (inline > attribute > builder-default) generation.
        bool generate = overrides?.GenerateSchema ?? (attr?.GenerateSchema == true ? true : generateSchemasDefault);
        string? payloadSchema = overrides?.PayloadSchema
                                ?? (generate ? SchemaWriter.ForType(type) : null);

        return new StreamDefinition
        {
            ModelType = type,
            Name = name.Trim(),
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

        return new StreamDefinition
        {
            ModelType = null,
            Name = (FirstNonBlank(overrides?.Name) ?? name).Trim(),
            Description = overrides?.Description,
            EventTypes = ResolveEventTypes(overrides, null),
            IsPublic = overrides?.IsPublic ?? true,
            Packages = ResolvePackages(overrides, null),
            PayloadSchema = overrides?.PayloadSchema,
        };
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
