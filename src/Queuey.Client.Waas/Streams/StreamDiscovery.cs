using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Queuey.Client.Waas;

/// <summary>Finds <see cref="QueueyModelAttribute"/>-decorated types in an assembly (used by the CLI and assembly-scan registration).</summary>
public static class StreamDiscovery
{
    /// <summary>Returns the public/non-public concrete types in <paramref name="assembly"/> marked with <see cref="QueueyModelAttribute"/>.</summary>
    public static IReadOnlyList<Type> TypesWithQueueyModel(Assembly assembly)
    {
        if (assembly is null) throw new ArgumentNullException(nameof(assembly));

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types; // keep the types that did load
        }

        return types
            .Where(t => t != null
                        && !t.IsAbstract
                        && t.GetCustomAttribute<QueueyModelAttribute>(inherit: false) != null)
            .Select(t => t!)
            .ToArray();
    }

    /// <summary>Resolves the <see cref="StreamDefinition"/>s for every decorated type in the assembly (used by the CLI's dry-run, which needs no credentials).</summary>
    public static IReadOnlyList<StreamDefinition> DefinitionsFromAssembly(Assembly assembly)
        => TypesWithQueueyModel(assembly)
            .Select(t => StreamDefinitionFactory.FromType(t, null))
            .ToArray();
}
