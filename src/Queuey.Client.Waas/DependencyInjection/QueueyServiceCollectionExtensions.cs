using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Queuey.Client.Waas;

/// <summary>DI registration for the Queuey WaaS producer surface (<see cref="IQueueyService"/>).</summary>
public static class QueueyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IQueueyService"/> (and the underlying <see cref="QueueyClient"/> + a
    /// <see cref="StreamRegistry"/>). Set credentials on <paramref name="configureOptions"/> (ApiKey,
    /// optional TenantPublicId/LicensePublicId) and declare streams via <paramref name="build"/>.
    /// </summary>
    public static IServiceCollection AddQueuey(
        this IServiceCollection services,
        Action<QueueyOptions> configureOptions,
        Action<IQueueyBuilder>? build = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configureOptions is null) throw new ArgumentNullException(nameof(configureOptions));

        // Reuse the core registration: QueueyOptions (singleton) + named HttpClient + QueueyClient (singleton).
        services.AddQueueyClient(configureOptions);

        var builder = new QueueyBuilder(services);
        build?.Invoke(builder);
        StreamRegistry registry = builder.BuildRegistry(); // fails loudly on duplicate names/types
        QueueRegistry queues = builder.BuildQueueRegistry();
        services.AddSingleton(registry);
        services.AddSingleton(queues);

        services.AddSingleton<IQueueyService>(sp =>
        {
            var options = sp.GetRequiredService<QueueyOptions>();
            var client = sp.GetRequiredService<QueueyClient>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient http = factory.CreateClient(QueueyClientDefaults.HttpClientName);
            var controlPlane = new QueueyControlPlaneClient(http, options);
            // En logger fra vertens DI når den finnes (BØR 3 fra reviewen av #64): en endring synken fra kode lar ligge fordi
            // Queuey vil ha en plan, skal synes i appens logg også.
            return new QueueyService(client, controlPlane, registry, options, queues, sp.GetService<ILogger<QueueyService>>());
        });

        // Planene Queuey lagrer (F3.11), som et eget grensesnitt: IQueueyService får ikke nye medlemmer.
        services.AddSingleton<IQueueyPlans>(sp => sp.GetRequiredService<IQueueyService>() as IQueueyPlans
            ?? throw new InvalidOperationException("IQueueyService is not Queuey's own, so it has no plans: register IQueueyPlans too."));

        return services;
    }
}
