using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client;

/// <summary>Publishing operations against the Queuey ingress host.</summary>
public interface IQueueyIngress
{
    /// <summary>Publishes a raw byte payload to <paramref name="queueName"/>.</summary>
    Task<PublishResult> PublishAsync(string queueName, byte[] payload, PublishOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Publishes <paramref name="payload"/> serialized to JSON (camelCase) to <paramref name="queueName"/>.</summary>
    Task<PublishResult> PublishAsync<T>(string queueName, T payload, PublishOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Publishes a raw byte payload to the sandbox route for <paramref name="queueName"/>.</summary>
    Task<PublishResult> PublishSandboxAsync(string queueName, byte[] payload, SandboxPublishOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Publishes <paramref name="payload"/> serialized to JSON to the sandbox route for <paramref name="queueName"/>.</summary>
    Task<PublishResult> PublishSandboxAsync<T>(string queueName, T payload, SandboxPublishOptions? options = null, CancellationToken cancellationToken = default);
}
