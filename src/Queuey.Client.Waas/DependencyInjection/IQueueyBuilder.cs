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

    /// <summary>
    /// Registers a queue from a type. Uses its <see cref="QueueyQueueAttribute"/> when present;
    /// <paramref name="configure"/> overrides per field. A queue is the pipeline you publish into —
    /// use this when you deliver to your own endpoint, and <c>AddStream</c> when partners subscribe.
    /// </summary>
    IQueueyBuilder AddQueue<T>(Action<QueueOptions>? configure = null);

    /// <summary>Registers a name-only queue (no CLR type).</summary>
    IQueueyBuilder AddQueue(string name, Action<QueueOptions>? configure = null);

    /// <summary>Opt-in: registers every <see cref="QueueyQueueAttribute"/>-decorated type in the given assemblies.</summary>
    IQueueyBuilder AddQueuesFromAssembly(params Assembly[] assemblies);

    /// <summary>Opt-in: registers every decorated type in the assembly containing <typeparamref name="TMarker"/>.</summary>
    IQueueyBuilder AddQueuesFromAssemblyContaining<TMarker>();

    /// <summary>Turns on advisory <c>payloadSchema</c> generation for streams registered after this call.</summary>
    IQueueyBuilder GenerateSchemas(bool enabled = true);

    /// <summary>
    /// Applies every registered stream while the host starts, and <b>aborts startup</b> if the run does
    /// not fully converge — the deploy fails instead of the first customer request.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately so: app instances often hold a publish-only key, and a rolling
    /// deploy would have every replica applying the same streams at once. A deploy step
    /// (<c>queuey sync --assembly</c>) is the better default home for this; turn it on for local
    /// development, or for a single-instance service that owns its own schema.
    /// <para>
    /// Stream names are validated earlier regardless — <c>AddQueuey</c> builds the registry as it
    /// registers, so a bad name throws before a host exists at all.
    /// </para>
    /// </remarks>
    /// <param name="configure">Optional tuning of the startup run (e.g. <see cref="SyncOptions.ContinueOnError"/>).</param>
    IQueueyBuilder SyncOnStartup(Action<SyncOptions>? configure = null);
}
