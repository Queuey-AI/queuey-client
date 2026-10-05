using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// The injected Queuey facade for producers. Two everyday verbs — <see cref="SyncStreamsAsync(SyncOptions,CancellationToken)"/>
/// on deploy and <c>PushEventAsync</c> on save — plus the <see cref="Registry"/> and the underlying
/// <see cref="Client"/> as an escape hatch to the frozen SDK.
/// </summary>
public interface IQueueyService
{
    /// <summary>Publishes an event to a stream by name. <paramref name="key"/> maps to the group key (nullable).</summary>
    Task<PublishResult> PushEventAsync<TData>(
        string stream, string eventType, string? key, TData data,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes to a stream with extra publish options (idempotency key, source, content type).</summary>
    Task<PublishResult> PushEventAsync<TData>(
        string stream, string eventType, string? key, TData data, PublishOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes to the stream registered for <typeparamref name="TModel"/> (no magic string).</summary>
    Task<PublishResult> PushEventAsync<TModel>(
        string eventType, string? key, TModel data,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes pre-serialized bytes (e.g. a JSON string from the CLI). Sent verbatim; defaults to
    /// <c>application/octet-stream</c> unless <see cref="PublishOptions.ContentType"/> is set.
    /// </summary>
    Task<PublishResult> PushEventAsync(
        string stream, string eventType, string? key, byte[] data, PublishOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Applies every registered stream via <c>PUT /waas/streams</c>. Safe to run on every deploy.</summary>
    Task<SyncResult> SyncStreamsAsync(SyncOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Applies the given model types (resolved via <see cref="QueueyModelAttribute"/>/convention), ignoring the registry.</summary>
    Task<SyncResult> SyncStreamsAsync(IEnumerable<Type> modelTypes, SyncOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Applies a single explicit stream definition.</summary>
    Task<StreamApplyResult> ApplyStreamAsync(StreamDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently creates (or updates) a package by name — <c>PUT /waas/packages</c>. Use this to
    /// create a package directly (e.g. with a description) outside the model-driven <c>SyncStreams</c> flow.
    /// </summary>
    Task<PackageApplyResult> ApplyPackageAsync(string name, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>Updates a package's display name and/or description (by <c>pkg_…</c> id).</summary>
    Task<PackageApplyResult> UpdatePackageAsync(string packagePublicId, string? name = null, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>Archives a package — its streams stop being visible to partners.</summary>
    Task ArchivePackageAsync(string packagePublicId, CancellationToken cancellationToken = default);

    /// <summary>Assigns a stream (catalog entry <c>cat_…</c>) to a package (additive, idempotent).</summary>
    Task AssignStreamToPackageAsync(string packagePublicId, string catalogEntryPublicId, CancellationToken cancellationToken = default);

    /// <summary>Removes a stream from a package (idempotent). Its other package memberships are unaffected.</summary>
    Task RemoveStreamFromPackageAsync(string packagePublicId, string catalogEntryPublicId, CancellationToken cancellationToken = default);

    /// <summary>A network-free preview of what <see cref="SyncStreamsAsync(SyncOptions,CancellationToken)"/> would apply.</summary>
    IReadOnlyList<StreamPlan> Plan();

    /// <summary>
    /// Applies every registered queue via <c>PUT /queues</c>, patching the policy of those that
    /// declare one. Safe to run on every deploy — applying is idempotent.
    /// </summary>
    Task<QueueSyncResult> SyncQueuesAsync(SyncOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Applies a single explicit queue definition (ensure it exists, then patch its policy).</summary>
    Task<QueueApplyResult> ApplyQueueAsync(QueueDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>A network-free preview of what <see cref="SyncQueuesAsync"/> would apply.</summary>
    IReadOnlyList<QueuePlan> PlanQueues();

    /// <summary>
    /// Converges Queuey from a declarative deployment file: the workspace's delivery defaults first,
    /// then each queue's behaviour and destination. Workspace before queues, because a queue that
    /// means to inherit needs something to inherit.
    /// </summary>
    Task<QueueSyncResult> ApplyDeploymentAsync(DeploymentFile file, SyncOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a workspace back into a deployment file — the inverse of
    /// <see cref="ApplyDeploymentAsync"/>. Inherit-aware (a queue that inherits a section writes
    /// nothing for it) and secret-free by construction, so the result is safe to commit.
    /// </summary>
    Task<DeploymentFile> PullDeploymentAsync(string? tenantPublicId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports what applying <paramref name="file"/> would change — the CI gate. Only what the file
    /// declares is compared: a workspace holding settings the file says nothing about is inheritance
    /// working as designed, not drift.
    /// </summary>
    Task<IReadOnlyList<DriftItem>> CheckDeploymentAsync(DeploymentFile file, string? tenantPublicId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Proves a queue delivers: publishes <paramref name="payload"/> to <paramref name="queueName"/>
    /// and follows that event until it is delivered, logged, filtered or failed, or the timeout
    /// passes. A failure is reported on its first attempt, with the receiver's answer and what to
    /// change — the retries can take hours, and the first answer is what says what to fix.
    /// </summary>
    /// <remarks>
    /// The event is a real one: the receiver gets it like any other, so send data it treats as
    /// harmless. Needs a key that may publish and read events — a deploy key can.
    /// </remarks>
    Task<DeliveryVerification> VerifyDeliveryAsync(string queueName, byte[] payload, VerifyDeliveryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies queues, then streams. Queues go first because a stream is published on top of one, so a
    /// queue failure stops the run before any stream is touched.
    /// </summary>
    Task<(QueueSyncResult Queues, SyncResult Streams)> SyncAsync(SyncOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>The registered queues (inspectable in tests).</summary>
    QueueRegistry Queues { get; }

    /// <summary>Producer-side integration-partner management (invite, grant packages, activate group keys).</summary>
    IQueueyIntegrations Integrations { get; }

    /// <summary>Control-plane management (create tenants and queues).</summary>
    IQueueyManagement Management { get; }

    /// <summary>The registered streams (inspectable in tests).</summary>
    StreamRegistry Registry { get; }

    /// <summary>The underlying frozen SDK client (sandbox publish, HMAC, raw-byte publish, etc.).</summary>
    QueueyClient Client { get; }
}
