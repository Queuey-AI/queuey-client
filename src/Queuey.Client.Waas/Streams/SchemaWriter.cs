using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Queuey.Client.Waas;

/// <summary>
/// Generates a small, advisory JSON-Schema string from a CLR type (System.Text.Json only, no deps).
/// The backend stores it verbatim and never validates it, so this is a best-effort description:
/// honors <see cref="QueueyIgnoreAttribute"/>, <c>[JsonIgnore]</c>, <c>[JsonPropertyName]</c>, and
/// camelCases property names to match the published payload.
/// </summary>
internal static class SchemaWriter
{
    private static readonly JsonSerializerOptions Options =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string ForType(Type type)
        => JsonSerializer.Serialize(Build(type, new HashSet<Type>()), Options);

    private static object Build(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string) || type == typeof(Guid))
            return new Dictionary<string, object?> { ["type"] = "string" };
        if (type == typeof(bool))
            return new Dictionary<string, object?> { ["type"] = "boolean" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date-time" };
        if (type.IsEnum)
            return new Dictionary<string, object?> { ["type"] = "string", ["enum"] = Enum.GetNames(type) };
        if (IsInteger(type))
            return new Dictionary<string, object?> { ["type"] = "integer" };
        if (IsNumber(type))
            return new Dictionary<string, object?> { ["type"] = "number" };

        Type? element = ElementType(type);
        if (element != null)
            return new Dictionary<string, object?> { ["type"] = "array", ["items"] = Build(element, visited) };

        // Complex object.
        if (!visited.Add(type))
            return new Dictionary<string, object?> { ["type"] = "object" }; // cycle guard

        var properties = new Dictionary<string, object?>();
        var required = new List<string>();
        foreach (PropertyInfo p in ReadableProperties(type))
        {
            string name = PropertyName(p);
            properties[name] = Build(p.PropertyType, visited);
            if (IsRequired(p.PropertyType))
                required.Add(name);
        }
        visited.Remove(type);

        var schema = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private static IEnumerable<PropertyInfo> ReadableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead
                        && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<QueueyIgnoreAttribute>() is null
                        && p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    private static string PropertyName(PropertyInfo p)
        => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? CamelCase(p.Name);

    private static string CamelCase(string s)
        => string.IsNullOrEmpty(s) || char.IsLower(s[0]) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

    // Best-effort: non-nullable value types are required; reference types are optional.
    private static bool IsRequired(Type t)
        => t.IsValueType && Nullable.GetUnderlyingType(t) is null;

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            Type? ie = type.GetInterfaces().Concat(new[] { type })
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return ie?.GetGenericArguments()[0];
        }
        return null;
    }

    private static bool IsInteger(Type t)
        => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    private static bool IsNumber(Type t)
        => t == typeof(float) || t == typeof(double) || t == typeof(decimal);
}
