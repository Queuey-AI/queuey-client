using System;

namespace Queuey.Client;

/// <summary>
/// A named Queuey deployment. Selecting one provides default base addresses for both the
/// control-plane (API) host and the publish (ingress) host; either can be overridden on
/// <see cref="QueueyOptions"/> to point at a locally-running instance (e.g. for testing).
/// </summary>
public enum QueueyEnvironment
{
    /// <summary>Production: <c>https://api.queuey.ai</c> + <c>https://ingress.queuey.ai</c>.</summary>
    Production = 0,
}

/// <summary>Default host addresses for each <see cref="QueueyEnvironment"/>. A locally-running instance
/// is reached by overriding <c>ApiBaseAddress</c>/<c>IngressBaseAddress</c>.</summary>
internal static class QueueyHosts
{
    /// <summary>The control-plane (management / WaaS / SyncModels) host.</summary>
    public static Uri Api(QueueyEnvironment environment) => new Uri("https://api.queuey.ai");

    /// <summary>The ingress (publish) host.</summary>
    public static Uri Ingress(QueueyEnvironment environment) => new Uri("https://ingress.queuey.ai");
}
