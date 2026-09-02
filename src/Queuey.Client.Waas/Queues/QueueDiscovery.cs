using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Queuey.Client.Waas;

/// <summary>Finds <see cref="QueueyQueueAttribute"/>-decorated types in an assembly (used by the CLI and assembly-scan registration).</summary>
public static class QueueDiscovery
{
    /// <summary>Returns the concrete types in <paramref name="assembly"/> marked with <see cref="QueueyQueueAttribute"/>.</summary>
    public static IReadOnlyList<Type> TypesWithQueueyQueue(Assembly assembly)
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
                        && t.GetCustomAttribute<QueueyQueueAttribute>(inherit: false) != null)
            .Select(t => t!)
            .ToArray();
    }

    /// <summary>Resolves the <see cref="QueueDefinition"/>s for every decorated type (used by the CLI's credential-free dry run).</summary>
    public static IReadOnlyList<QueueDefinition> DefinitionsFromAssembly(Assembly assembly)
        => TypesWithQueueyQueue(assembly)
            .Select(t => QueueDefinitionFactory.FromType(t, null))
            .ToArray();
}
