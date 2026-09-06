using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Queuey.Edge.Mqtt;

public static class MqttSourceServiceCollectionExtensions
{
    /// <summary>
    /// Adds an MQTT intake to a host that already called <c>AddQueueyEdge</c>:
    /// <code>
    /// services.AddQueueyEdgeMqttSource(m =>
    /// {
    ///     m.Server = "localhost";
    ///     m.Routes.Add(new MqttRoute { TopicFilter = "plant/+/alarms", Queue = "alarms", GroupKeySegment = 1 });
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddQueueyEdgeMqttSource(this IServiceCollection services, Action<MqttSourceOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        services.TryAddSingleton(sp =>
        {
            var o = new MqttSourceOptions();
            configure(o);
            o.Validate(); // die at composition, where the operator is looking
            return o;
        });
        services.AddHostedService<MqttSource>();
        return services;
    }
}
