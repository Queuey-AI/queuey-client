using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Control-plane management: create tenants and queues. Targets the API host with
/// <c>X-Api-Key</c> + <c>X-License-PublicId</c> (needs <c>tenant.write</c>/<c>queue.write</c>).
/// </summary>
public interface IQueueyManagement
{
    /// <summary>
    /// Creates a tenant under the current license. <paramref name="asProducer"/> seeds a WaaS producer
    /// (ProducerSystem); <paramref name="withDefaultQueue"/> seeds a starter queue.
    /// </summary>
    Task<TenantResult> CreateTenantAsync(string displayName, bool asProducer = false, bool withDefaultQueue = false, CancellationToken cancellationToken = default);

    /// <summary>Creates a queue under a tenant.</summary>
    Task<QueueResult> CreateQueueAsync(string tenantPublicId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>Lists a workspace's queues — name, id, mode and whether each has anywhere to deliver.</summary>
    Task<IReadOnlyList<QueueListItem>> ListQueuesAsync(string tenantPublicId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Patches the workspace's default delivery endpoint — the layer every queue inherits from. Null
    /// properties are left alone, so this can never clear config it was not told about.
    /// </summary>
    Task SetWorkspaceDeliveryAsync(string tenantPublicId, WorkspaceDelivery delivery, CancellationToken cancellationToken = default);

    /// <summary>Patches one queue's destination. Null properties are left alone.</summary>
    Task SetQueueDeliveryAsync(string queuePublicId, QueueDelivery delivery, CancellationToken cancellationToken = default);

    /// <summary>
    /// Patches the workspace's behaviour — lane strategy, retention, DLQ, idempotency. Every queue
    /// that does not override a field inherits it.
    /// </summary>
    Task SetWorkspacePolicyAsync(string tenantPublicId, DeploymentWorkspace policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Patches how arriving events are read — ingress auth, and where the event type and group key
    /// come from. Set it on a workspace and every queue inherits; set it on a queue to override.
    /// </summary>
    /// <param name="publicId">A <c>ten_…</c> or <c>que_…</c> id, per <paramref name="isQueue"/>.</param>
    /// <param name="isQueue">Whether <paramref name="publicId"/> names a queue rather than a workspace.</param>
    /// <param name="ingress">The patch. Null properties are left alone.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task SetIngressAsync(string publicId, bool isQueue, DeploymentIngress ingress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a delivery credential under a workspace and returns its label. The secret is encrypted
    /// at rest and never readable again — a <c>credentialRef</c> points at it by name, which is what
    /// keeps a deployment file safe to commit. A name the workspace has keeps its credential: the value it
    /// holds stores as before, and another value replaces its secret only with <paramref name="replace"/>,
    /// since that changes every queue and ingress that uses it.
    /// </summary>
    /// <param name="replace">
    /// True to replace the secret of the credential that has the name when it holds another value, as a new
    /// version under the same id. Without it Queuey refuses another value (<c>credential_exists</c>).
    /// </param>
    /// <exception cref="QueueyConflictException">
    /// <c>credential_exists</c>: the name's credential holds another value and <paramref name="replace"/> is false.
    /// </exception>
    Task<CredentialResult> CreateCredentialAsync(
        string tenantPublicId, string name, string type, string secret,
        string? keyId = null, string? username = null, bool replace = false, CancellationToken cancellationToken = default);

    /// <summary>Lists a workspace's delivery credentials — labels and types only, never values.</summary>
    Task<IReadOnlyList<CredentialResult>> ListCredentialsAsync(string tenantPublicId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for the secret of a credential by name, so it never passes through the caller (<c>POST /tenants/{ten}/credential-requests</c>):
    /// Queuey opens a one-time request and answers with the console page where a signed-in person who may manage the
    /// workspace's credentials pastes the value. It is stored encrypted under <paramref name="name"/>, as a new credential or
    /// as a new version of the secret of the one that has the name (whose key id and username stay), and a deployment file's
    /// <c>credentialRef</c> that waits for the name verifies with it at once. Nothing here takes or returns a value, no API
    /// returns it, and Queuey uses it only where the workspace's configuration does. Asking again for the same name and type
    /// while a request is open answers with that request. A request expires a day after it was made.
    /// </summary>
    /// <param name="tenantPublicId">The workspace (<c>ten_…</c>).</param>
    /// <param name="name">The name the credential is stored under, in the shape a deployment file's <c>credentialRef</c> has.</param>
    /// <param name="type">
    /// The credential's type; Queuey asks for <c>HmacSigning</c>, a signing secret it never sends as it is, when null. A secret
    /// Queuey sends to a receiver as it is (<c>ApiKeyHeader</c>, <c>BearerToken</c>, <c>BasicPassword</c>,
    /// <c>OAuth2ClientSecret</c>) is asked for by its type. <c>OAuth2Certificate</c> is refused.
    /// </param>
    /// <param name="keyId">The key id of an <c>HmacSigning</c> credential; a new one gets the name when null.</param>
    /// <param name="username">The username of a <c>BasicPassword</c> credential; required for a new one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="QueueyConflictException">
    /// <c>credential_type_mismatch</c> when a credential of another type has the name, <c>credential_details_differ</c> when it
    /// has another key id or username than the request asks for (a request only replaces the secret), or
    /// <c>credential_request_conflict</c> when a request for it is open for something else.
    /// </exception>
    /// <exception cref="QueueyNotFoundException">With no error code: the Queuey instance predates credential requests.</exception>
    Task<CredentialRequestResult> RequestCredentialAsync(
        string tenantPublicId, string name, string? type = null, string? keyId = null, string? username = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints an ingress signing key for a queue, so producers can publish with HMAC instead of an API
    /// key. The secret is returned <b>once</b>.
    /// </summary>
    /// <remarks>
    /// Needs <c>ApiKeyManage</c>, which a deploy key deliberately does not carry: a key that could
    /// mint keys would turn pipeline access into account access. Run this from an admin credential,
    /// once, and put the result in your producer's secret store.
    /// </remarks>
    Task<IngressSigningKey> MintIngressKeyAsync(string queuePublicId, string name, CancellationToken cancellationToken = default);

    /// <summary>Reads a queue's traffic snapshot (<c>GET /queues/{q}/metrics/snapshot</c>).</summary>
    Task<QueueMetricsSnapshot> GetQueueMetricsSnapshotAsync(string queuePublicId, CancellationToken cancellationToken = default);

    /// <summary>Lists a tenant's issues (cursor-paged; optional status/severity/queue filter).</summary>
    Task<IssueListPage> ListIssuesAsync(string tenantPublicId, IssueQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Reads one issue's detail.</summary>
    Task<IssueDetails> GetIssueAsync(string tenantPublicId, string issuePublicId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays an existing event to a connected <c>queuey listen</c> session for local debugging
    /// (<c>POST /queues/{q}/replay-to-listener/{e}</c>). Read-only: the event is not modified and the real
    /// endpoint is never contacted. Requires a listener connected on the queue's / tenant's scope.
    /// Works only on a queue that forwards its deliveries to the listener and shares its payloads in full:
    /// any other queue throws <see cref="QueueyConflictException"/> with the error code
    /// <c>listener_replay_needs_local_forward</c> or <c>listener_replay_needs_full_payload_sharing</c>, and a
    /// message that says what to change.
    /// </summary>
    Task<ReplayResult> ReplayToListenerAsync(string queuePublicId, string eventPublicId, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a replay-to-listener: whether a listener was connected, and its local response.</summary>
public sealed class ReplayResult
{
    /// <summary>False when no <c>queuey listen</c> session is connected — nothing was forwarded.</summary>
    public bool ListenerConnected { get; init; }
    /// <summary>True when the local listener returned a 2xx.</summary>
    public bool Delivered { get; init; }
    /// <summary>The local listener's HTTP status, if it responded.</summary>
    public int? StatusCode { get; init; }
    /// <summary>Round-trip time to the listener.</summary>
    public long DurationMs { get; init; }
    /// <summary>Error/diagnostic, when not delivered.</summary>
    public string? Error { get; init; }
}

/// <summary>A created/summarized tenant.</summary>
public sealed class TenantResult
{
    /// <summary>Tenant public id (<c>ten_…</c>).</summary>
    public string? PublicId { get; init; }
    public string? DisplayName { get; init; }
    public string? Status { get; init; }
    /// <summary>Tenant kind: <c>Standard</c> | <c>ProducerSystem</c> | <c>Integration</c>.</summary>
    public string? Kind { get; init; }
}

/// <summary>A created queue.</summary>
public sealed class QueueResult
{
    /// <summary>Queue public id (<c>que_…</c>).</summary>
    public string? PublicId { get; init; }
    public string? TenantPublicId { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>One row of a workspace's queue listing.</summary>
public sealed class QueueListItem
{
    /// <summary>Queue public id (<c>que_…</c>).</summary>
    public string? PublicId { get; init; }

    /// <summary>The queue name — what you publish to.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Run mode: <c>Deliver</c> or <c>LogOnly</c>.</summary>
    public string? Mode { get; init; }

    /// <summary>Whether the queue resolves to somewhere to deliver, its own or the workspace's.</summary>
    public bool HasDeliveryTarget { get; init; }

    /// <summary>Ingress is closed — new events are rejected while the backlog drains.</summary>
    public bool IngressClosed { get; init; }

    /// <summary>Delivery is held — events accumulate without being delivered.</summary>
    public bool DeliveryHeld { get; init; }

    /// <summary>Platform-suspended.</summary>
    public bool Suspended { get; init; }

    /// <summary>
    /// Whether a deployment file manages the queue, and from where (Queuey F2.4). Null when none ever has, and from a
    /// Queuey that predates managed resources. A detached queue is skipped by apply until it is adopted.
    /// </summary>
    public DeploymentManagementInfo? Deployment { get; init; }
}
