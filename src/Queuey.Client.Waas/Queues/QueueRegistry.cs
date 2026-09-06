using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// The registered queues — the source of truth for <c>SyncQueues</c>. Built once at startup;
/// inspectable in a unit test. Duplicate names or types fail loudly.
/// </summary>
public sealed class QueueRegistry
{
    private readonly IReadOnlyList<QueueDefinition> _queues;
    private readonly Dictionary<Type, QueueDefinition> _byModel = new();
    private readonly Dictionary<string, QueueDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>Creates the registry, indexing by name and declaring type.</summary>
    public QueueRegistry(IEnumerable<QueueDefinition> queues)
    {
        _queues = (queues ?? throw new ArgumentNullException(nameof(queues))).ToArray();

        foreach (QueueDefinition q in _queues)
        {
            if (_byName.ContainsKey(q.Name))
                throw new QueueyConfigurationException($"Duplicate Queuey queue name '{q.Name}'. Each queue must have a unique name.");
            _byName.Add(q.Name, q);

            if (q.ModelType != null)
            {
                if (_byModel.ContainsKey(q.ModelType))
                    throw new QueueyConfigurationException($"Type '{q.ModelType.FullName}' is registered as more than one queue.");
                _byModel.Add(q.ModelType, q);
            }
        }
    }

    /// <summary>All registered queue definitions, in registration order.</summary>
    public IReadOnlyList<QueueDefinition> Queues => _queues;

    /// <summary>Looks up the queue registered for a type.</summary>
    public bool TryGetByModel(Type modelType, out QueueDefinition definition) => _byModel.TryGetValue(modelType, out definition!);

    /// <summary>Looks up the queue registered for a name.</summary>
    public bool TryGetByName(string name, out QueueDefinition definition) => _byName.TryGetValue(name, out definition!);
}
