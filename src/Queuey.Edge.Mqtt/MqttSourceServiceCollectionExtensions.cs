using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
        // Health lets a source that stepped back resume the moment the spool
        // has room; the registry puts its side in the node's health; the spool
        // and the node's identity keep the client id, and so the broker
        // session, the same across a new hostname. All come from
        // AddQueueyEdge, and the source still runs without them.
        services.AddHostedService(sp => new MqttSource(
            sp.GetRequiredService<MqttSourceOptions>(),
            sp.GetRequiredService<IQueueyPublisher>(),
            sp.GetRequiredService<ILogger<MqttSource>>(),
            sp.GetService<IQueueyEdgeHealth>(),
            sp.GetService<EdgeIntakeRegistry>(),
            sp.GetService<IEventSpool>(),
            sp.GetService<EdgeNodeIdentity>()));
        return services;
    }
}
