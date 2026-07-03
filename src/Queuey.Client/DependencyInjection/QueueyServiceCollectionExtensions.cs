using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Queuey.Client;

/// <summary>DI registration for <see cref="QueueyClient"/>.</summary>
public static class QueueyServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="QueueyClient"/> (and its <see cref="QueueyOptions"/>) using
    /// <see cref="IHttpClientFactory"/> for connection pooling. Resolve <see cref="QueueyClient"/> from DI.
    /// </summary>
    public static IServiceCollection AddQueueyClient(this IServiceCollection services, Action<QueueyOptions> configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        services.AddSingleton(_ =>
        {
            var options = new QueueyOptions();
            configure(options);
            return options;
        });

        services
            .AddHttpClient(QueueyClientDefaults.HttpClientName, (sp, http) =>
            {
                var options = sp.GetRequiredService<QueueyOptions>();
                http.Timeout = options.Timeout;
                try { http.DefaultRequestHeaders.UserAgent.ParseAdd(QueueyUserAgent.Value); }
                catch { /* never fail registration on a malformed UA */ }
            });

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<QueueyOptions>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient http = factory.CreateClient(QueueyClientDefaults.HttpClientName);
            return new QueueyClient(options, http);
        });

        return services;
    }
}
