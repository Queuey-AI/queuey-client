using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Queuey.Client.Waas;

/// <summary>Fluent registration of Queuey streams. Streams are registered explicitly; there is no implicit scan.</summary>
public interface IQueueyBuilder
{
    /// <summary>The underlying service collection, for advanced composition.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Registers a stream from a model type. Uses its <see cref="QueueyModelAttribute"/> when present;
    /// <paramref name="configure"/> overrides per field (name, description, event types, visibility).
    /// </summary>
    IQueueyBuilder AddStream<T>(Action<StreamOptions>? configure = null);

    /// <summary>Registers a name-only stream (no CLR model type).</summary>
    IQueueyBuilder AddStream(string name, Action<StreamOptions>? configure = null);

    /// <summary>Opt-in: registers every <see cref="QueueyModelAttribute"/>-decorated type in the given assemblies.</summary>
    IQueueyBuilder AddStreamsFromAssembly(params Assembly[] assemblies);

    /// <summary>Opt-in: registers every decorated type in the assembly containing <typeparamref name="TMarker"/>.</summary>
    IQueueyBuilder AddStreamsFromAssemblyContaining<TMarker>();
}
