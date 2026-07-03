using System;

namespace Queuey.Client;

/// <summary>
/// A named Queuey deployment. Selecting one provides default base addresses for both the
/// control-plane (API) host and the publish (ingress) host; either can be overridden on
/// <see cref="QueueyOptions"/>.
/// </summary>
public enum QueueyEnvironment
{
    /// <summary>Production: <c>https://api.queuey.ai</c> + <c>https://ingress.queuey.ai</c>.</summary>
    Production = 0,

    /// <summary>Development: <c>https://devapi.queuey.ai</c> + <c>https://devingress.queuey.ai</c>.</summary>
    Development = 1,
}

/// <summary>Default host addresses for each <see cref="QueueyEnvironment"/>.</summary>
internal static class QueueyHosts
{
    /// <summary>The control-plane (management / WaaS / SyncModels) host.</summary>
    public static Uri Api(QueueyEnvironment environment) => environment switch
    {
        QueueyEnvironment.Development => new Uri("https://devapi.queuey.ai"),
        _ => new Uri("https://api.queuey.ai"),
    };

    /// <summary>The ingress (publish) host.</summary>
    public static Uri Ingress(QueueyEnvironment environment) => environment switch
    {
        QueueyEnvironment.Development => new Uri("https://devingress.queuey.ai"),
        _ => new Uri("https://ingress.queuey.ai"),
    };
}
