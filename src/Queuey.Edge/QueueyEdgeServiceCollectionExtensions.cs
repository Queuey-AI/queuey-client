using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Queuey.Edge;

/// <summary>
/// Registers Queuey Edge. After this, application code is one call:
/// <code>
/// services.AddQueueyEdge(o =>
/// {
///     o.ApiKey = "...";            // publish-only, tenant-scoped Edge key
///     o.TenantPublicId = "ten_...";
/// });
/// ...
/// await queuey.PublishAsync("temperature.updated", payload);
/// </code>
/// </summary>
public static class QueueyEdgeServiceCollectionExtensions
{
    public static IServiceCollection AddQueueyEdge(
        this IServiceCollection services,
        Action<QueueyEdgeOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        services.TryAddSingleton<IEdgeClock>(SystemEdgeClock.Instance);
        services.TryAddSingleton<EdgeWake>();

        services.TryAddSingleton(sp =>
        {
            var options = new QueueyEdgeOptions();
            configure(options);
            // Fail at composition, not at the first publish: a misconfigured
            // node should die on startup where the operator is looking.
            options.Validate();
            return options;
        });

        services.TryAddSingleton<IEventSpool>(sp => new SqliteEventSpool(
            sp.GetRequiredService<QueueyEdgeOptions>().Storage,
            sp.GetRequiredService<IEdgeClock>()));

        services.TryAddSingleton<IQueueyPublisher>(sp => new QueueyEdgePublisher(
            sp.GetRequiredService<QueueyEdgeOptions>(),
            sp.GetRequiredService<IEventSpool>(),
            sp.GetRequiredService<IEdgeClock>(),
            sp.GetRequiredService<EdgeWake>()));

        // Transfer machinery. ITransferChannel is TryAdd so tests (or a
        // future transport) can pre-register their own before this call.
        services.TryAddSingleton<ITransferOutcomeClassifier, TransferOutcomeClassifier>();
        services.TryAddSingleton<EdgeRuntimeState>();
        services.TryAddSingleton(sp => new BackoffPolicy(
            sp.GetRequiredService<QueueyEdgeOptions>().Transfer));
        services.TryAddSingleton<ITransferChannel>(sp => new HttpTransferChannel(
            new System.Net.Http.HttpClient(),
            sp.GetRequiredService<QueueyEdgeOptions>(),
            sp.GetRequiredService<ITransferOutcomeClassifier>(),
            sp.GetRequiredService<IEdgeClock>()));
        services.AddHostedService<EdgeTransferLoop>();

        // Health: the in-process snapshot and the OpenTelemetry meter
        // "Queuey.Edge". Hosted so the meter publishes eagerly and the
        // ephemeral-path warning lands at startup.
        services.TryAddSingleton<EdgeHealthService>();
        services.TryAddSingleton<IQueueyEdgeHealth>(sp => sp.GetRequiredService<EdgeHealthService>());
        services.AddHostedService(sp => sp.GetRequiredService<EdgeHealthService>());

        return services;
    }
}
