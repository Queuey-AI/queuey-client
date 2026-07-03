using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton(registry);

        services.AddSingleton<IQueueyService>(sp =>
        {
            var options = sp.GetRequiredService<QueueyOptions>();
            var client = sp.GetRequiredService<QueueyClient>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient http = factory.CreateClient(QueueyClientDefaults.HttpClientName);
            var controlPlane = new QueueyControlPlaneClient(http, options);
            return new QueueyService(client, controlPlane, registry, options);
        });

        return services;
    }
}
