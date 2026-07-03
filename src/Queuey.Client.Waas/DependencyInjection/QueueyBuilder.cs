using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Queuey.Client.Waas;

/// <summary>Collects stream registrations and materializes a <see cref="StreamRegistry"/>.</summary>
internal sealed class QueueyBuilder : IQueueyBuilder
{
    private readonly List<StreamDefinition> _streams = new();

    public QueueyBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    public IQueueyBuilder AddStream<T>(Action<StreamOptions>? configure = null)
    {
        _streams.Add(StreamDefinitionFactory.FromType(typeof(T), BuildOptions(configure)));
        return this;
    }

    public IQueueyBuilder AddStream(string name, Action<StreamOptions>? configure = null)
    {
        _streams.Add(StreamDefinitionFactory.FromName(name, BuildOptions(configure)));
        return this;
    }

    public IQueueyBuilder AddStreamsFromAssembly(params Assembly[] assemblies)
    {
        if (assemblies is null) throw new ArgumentNullException(nameof(assemblies));
        foreach (Assembly assembly in assemblies)
            foreach (Type type in StreamDiscovery.TypesWithQueueyModel(assembly))
                _streams.Add(StreamDefinitionFactory.FromType(type, null));
        return this;
    }

    public IQueueyBuilder AddStreamsFromAssemblyContaining<TMarker>()
        => AddStreamsFromAssembly(typeof(TMarker).Assembly);

    /// <summary>Builds the registry (throws on duplicate stream names / model types).</summary>
    public StreamRegistry BuildRegistry() => new(_streams);

    private static StreamOptions? BuildOptions(Action<StreamOptions>? configure)
    {
        if (configure is null) return null;
        var options = new StreamOptions();
        configure(options);
        return options;
    }
}
