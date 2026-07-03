using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// The registered streams — the single source of truth for both <c>SyncModels</c> and typed publish.
/// Built once at startup; inspectable in a unit test. Duplicate names or model types fail loudly.
/// </summary>
public sealed class StreamRegistry
{
    private readonly IReadOnlyList<StreamDefinition> _streams;
    private readonly Dictionary<Type, StreamDefinition> _byModel = new();
    private readonly Dictionary<string, StreamDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>Creates the registry, indexing by name and model type.</summary>
    public StreamRegistry(IEnumerable<StreamDefinition> streams)
    {
        _streams = (streams ?? throw new ArgumentNullException(nameof(streams))).ToArray();

        foreach (StreamDefinition s in _streams)
        {
            if (_byName.ContainsKey(s.Name))
                throw new QueueyConfigurationException($"Duplicate Queuey stream name '{s.Name}'. Each stream must have a unique name.");
            _byName.Add(s.Name, s);

            if (s.ModelType != null)
            {
                if (_byModel.ContainsKey(s.ModelType))
                    throw new QueueyConfigurationException($"Model type '{s.ModelType.FullName}' is registered as more than one stream.");
                _byModel.Add(s.ModelType, s);
            }
        }
    }

    /// <summary>All registered stream definitions, in registration order.</summary>
    public IReadOnlyList<StreamDefinition> Streams => _streams;

    /// <summary>Looks up the stream registered for a model type.</summary>
    public bool TryGetByModel(Type modelType, out StreamDefinition definition) => _byModel.TryGetValue(modelType, out definition!);

    /// <summary>Looks up the stream registered for a name.</summary>
    public bool TryGetByName(string name, out StreamDefinition definition) => _byName.TryGetValue(name, out definition!);
}
