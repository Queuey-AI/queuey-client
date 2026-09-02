using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Talks to the Queuey control-plane (API host) with <c>X-Api-Key</c> + <c>X-License-PublicId</c>, reusing
/// the frozen core transport. Phase 1 implements the stream-apply call (<c>PUT /waas/streams</c>).
/// </summary>
internal sealed class QueueyControlPlaneClient
{
    private const string JsonContentType = "application/json";

    private readonly QueueyHttpConnection _connection;
    private readonly QueueyOptions _options;

    public QueueyControlPlaneClient(HttpClient httpClient, QueueyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connection = new QueueyHttpConnection(httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
    }

    /// <summary>Applies a stream via <c>PUT /waas/streams</c> (declarative, idempotent by producer + name).</summary>
    public async Task<StreamApplyResponse> ApplyStreamAsync(StreamApplyRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // Credentials are validated when the call is actually made (never at construction), so a
        // publish-only consumer that resolves IQueueyService never trips a control-plane requirement.
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "streams");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        return await _connection.SendForJsonAsync<StreamApplyResponse>(
            HttpMethod.Put,
            uri,
            body,
            JsonContentType,
            authenticator,
            headers => QueueyHttpHeaders.Set(headers, QueueyHeaders.LicensePublicId, license),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Idempotently upserts a package via <c>PUT /waas/packages</c> (by producer + name).</summary>
    public async Task<PackageApplyResponse> ApplyPackageAsync(PackageApplyRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "packages");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        return await _connection.SendForJsonAsync<PackageApplyResponse>(
            HttpMethod.Put,
            uri,
            body,
            JsonContentType,
            authenticator,
            headers => QueueyHttpHeaders.Set(headers, QueueyHeaders.LicensePublicId, license),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Assigns a stream (catalog entry) to a package (additive, idempotent). Returns 204.</summary>
    public async Task AssignStreamAsync(string packagePublicId, string catalogEntryPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "streams");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new AssignStreamRequest { CatalogEntryPublicId = catalogEntryPublicId }, QueueyJson.Options);

        await _connection.SendAsync(
            HttpMethod.Post,
            uri,
            body,
            JsonContentType,
            authenticator,
            headers => QueueyHttpHeaders.Set(headers, QueueyHeaders.LicensePublicId, license),
            cancellationToken).ConfigureAwait(false);
    }

    // ── Integration partner enablement (grant/activate → routing) ──────────────

    /// <summary>Invites an integration partner by email (<c>POST /waas/integrations</c>).</summary>
    public async Task<IntegrationResponse> InviteIntegrationAsync(string email, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "integrations");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new InviteIntegrationRequest { ProducerTenantPublicId = RequireTenant(), Email = email }, QueueyJson.Options);

        return await _connection.SendForJsonAsync<IntegrationResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Grants a package to an integration (<c>POST …/packages/{pkg}/grants</c>). Idempotent. Returns 204.</summary>
    public async Task GrantPackageAsync(string integrationPublicId, string packagePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "grants");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new GrantPackageRequest { IntegrationPublicId = integrationPublicId }, QueueyJson.Options);

        await _connection.SendAsync(HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes a package grant (<c>DELETE …/packages/{pkg}/grants/{integration}</c>). Idempotent. Returns 204.</summary>
    public async Task RevokePackageAsync(string integrationPublicId, string packagePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "grants", integrationPublicId);
        await _connection.SendAsync(HttpMethod.Delete, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Activates a group key for an integration (<c>POST /waas/activations</c>) — the second routing gate.</summary>
    public async Task<ActivationResponse> ActivateAsync(string integrationPublicId, string groupKey, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "activations");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new ActivationRequest { ProducerTenantPublicId = RequireTenant(), GroupKey = groupKey, IntegrationPublicId = integrationPublicId }, QueueyJson.Options);

        return await _connection.SendForJsonAsync<ActivationResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deactivates a group key (<c>DELETE /waas/activations</c> — with a JSON body). Returns 204.</summary>
    public async Task DeactivateAsync(string integrationPublicId, string groupKey, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "activations");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new ActivationRequest { ProducerTenantPublicId = RequireTenant(), GroupKey = groupKey, IntegrationPublicId = integrationPublicId }, QueueyJson.Options);

        // The backend's deactivate takes the request in the DELETE body (proxy hazard, but supported by HttpClient).
        await _connection.SendAsync(HttpMethod.Delete, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    // ── Management: tenants + queues (root routes on the API host) ─────────────

    /// <summary>Creates a tenant (<c>POST /tenants</c>). LicenseId 0 → the backend uses the license header.</summary>
    public async Task<TenantSummaryResponse> CreateTenantAsync(string displayName, bool asProducer, bool withDefaultQueue, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new CreateTenantWireRequest { LicenseId = 0, DisplayName = displayName, CreateAsProducer = asProducer, CreateDefaultQueue = withDefaultQueue },
            QueueyJson.Options);

        return await _connection.SendForJsonAsync<TenantSummaryResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a queue under a tenant (<c>POST /queues</c>).</summary>
    public async Task<QueueReadResponse> CreateQueueAsync(string tenantPublicId, string displayName, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new CreateQueueWireRequest { TenantPublicId = tenantPublicId, DisplayName = displayName }, QueueyJson.Options);

        return await _connection.SendForJsonAsync<QueueReadResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    // ── Queues: declarative apply + policy patch ──────────────────────────────

    /// <summary>Applies a queue via <c>PUT /queues</c> (idempotent by tenant + name; existence only).</summary>
    public async Task<QueueApplyResponse> ApplyQueueAsync(QueueApplyRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        return await _connection.SendForJsonAsync<QueueApplyResponse>(
            HttpMethod.Put, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Patches a queue's policy (<c>PATCH /queues/{que}/policy</c>). Null fields are left alone — this
    /// is deliberately NOT the full-state <c>PUT /queues/{que}/config</c>, which would also rewrite
    /// (and on an empty base URL, clear) the queue's delivery config.
    /// </summary>
    public async Task PatchQueuePolicyAsync(string queuePublicId, QueuePolicyPatchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queuePublicId)) throw new ArgumentException("A queue public id is required.", nameof(queuePublicId));
        if (request is null) throw new ArgumentNullException(nameof(request));

        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "policy");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        await _connection.SendAsync(
            new HttpMethod("PATCH"), uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    // ── Delivery + credentials (the destination, and the secrets it uses) ─────

    /// <summary>Patches the workspace's default endpoint (<c>PATCH /tenants/{ten}/delivery</c>). Returns 204.</summary>
    public async Task PatchTenantDeliveryAsync(string tenantPublicId, PatchTenantDeliveryWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "delivery");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        await _connection.SendAsync(
            new HttpMethod("PATCH"), uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Patches one queue's destination (<c>PATCH /queues/{que}/delivery</c>). Returns 204.</summary>
    public async Task PatchQueueDeliveryAsync(string queuePublicId, PatchQueueDeliveryWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "delivery");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        await _connection.SendAsync(
            new HttpMethod("PATCH"), uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a delivery credential (<c>POST /tenants/{ten}/credentials</c>). The value is never readable again.</summary>
    public async Task<CredentialWireResponse> CreateCredentialAsync(string tenantPublicId, CreateCredentialWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "credentials");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        return await _connection.SendForJsonAsync<CredentialWireResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists a workspace's delivery credentials — labels only (<c>GET /tenants/{ten}/credentials</c>).</summary>
    public async Task<List<CredentialWireResponse>> ListCredentialsAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "credentials");
        return await _connection.SendForJsonAsync<List<CredentialWireResponse>>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists a tenant's queues (<c>GET /tenants/{ten}/queues</c>).</summary>
    public async Task<List<QueueListItemResponse>> ListQueuesAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "queues");
        return await _connection.SendForJsonAsync<List<QueueListItemResponse>>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the workspace's delivery + policy config (<c>GET /tenants/{ten}/config</c>).</summary>
    public async Task<TenantConfigResponse> GetTenantConfigAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "config");
        return await _connection.SendForJsonAsync<TenantConfigResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one queue's config with its per-section inherit flags (<c>GET /queues/{que}/config</c>).</summary>
    public async Task<QueueConfigResponse> GetQueueConfigAsync(string queuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "config");
        return await _connection.SendForJsonAsync<QueueConfigResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    // ── Package lifecycle (update / archive / remove stream) ───────────────────

    /// <summary>Updates a package's name/description (<c>PUT …/packages/{pkg}</c>).</summary>
    public async Task<PackageApplyResponse> UpdatePackageAsync(string packagePublicId, string? name, string? description, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new UpdatePackageWireRequest { Name = name, Description = description }, QueueyJson.Options);

        return await _connection.SendForJsonAsync<PackageApplyResponse>(
            HttpMethod.Put, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Archives a package (<c>POST …/packages/{pkg}/archive</c>). Returns 204.</summary>
    public async Task ArchivePackageAsync(string packagePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "archive");
        await _connection.SendAsync(HttpMethod.Post, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a stream from a package (<c>DELETE …/packages/{pkg}/streams/{cat}</c>). Idempotent. Returns 204.</summary>
    public async Task RemoveStreamAsync(string packagePublicId, string catalogEntryPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "streams", catalogEntryPublicId);
        await _connection.SendAsync(HttpMethod.Delete, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    // ── Observability reads (metrics + issues) ─────────────────────────────────

    /// <summary>Reads a queue traffic snapshot (<c>GET /queues/{q}/metrics/snapshot</c>).</summary>
    public async Task<QueueMetricsSnapshot> GetQueueMetricsSnapshotAsync(string queuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "metrics", "snapshot");
        return await _connection.SendForJsonAsync<QueueMetricsSnapshot>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replays an event to a connected listener (<c>POST /queues/{q}/replay-to-listener/{e}</c>).</summary>
    public async Task<ReplayResult> ReplayToListenerAsync(string queuePublicId, string eventPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "replay-to-listener", eventPublicId);
        return await _connection.SendForJsonAsync<ReplayResult>(
            HttpMethod.Post, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists a tenant's issues (<c>GET /issues/{tenant}?status&amp;severity&amp;queuePublicId&amp;limit&amp;cursor</c>).</summary>
    public async Task<IssueListPage> ListIssuesAsync(string tenantPublicId, IssueQuery? query, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), BuildIssueQuery(query), "issues", tenantPublicId);
        return await _connection.SendForJsonAsync<IssueListPage>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one issue's detail (<c>GET /issues/{tenant}/{issue}</c>).</summary>
    public async Task<IssueDetails> GetIssueAsync(string tenantPublicId, string issuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = new ApiKeyAuthenticator(RequireApiKey());
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "issues", tenantPublicId, issuePublicId);
        return await _connection.SendForJsonAsync<IssueDetails>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    private static string? BuildIssueQuery(IssueQuery? q)
    {
        if (q is null) return null;
        var parts = new List<string>();
        if (q.Status is { } s) parts.Add("status=" + s);           // enum name — ASP.NET binds it
        if (q.Severity is { } sv) parts.Add("severity=" + sv);
        if (!string.IsNullOrWhiteSpace(q.QueuePublicId)) parts.Add("queuePublicId=" + Uri.EscapeDataString(q.QueuePublicId!));
        if (q.Limit is { } l) parts.Add("limit=" + l.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(q.Cursor)) parts.Add("cursor=" + Uri.EscapeDataString(q.Cursor!));
        return parts.Count == 0 ? null : string.Join("&", parts);
    }

    private static Action<HttpRequestHeaders> LicenseHeader(string license)
        => headers => QueueyHttpHeaders.Set(headers, QueueyHeaders.LicensePublicId, license);

    private string RequireTenant()
    {
        if (string.IsNullOrWhiteSpace(_options.TenantPublicId))
            throw new QueueyConfigurationException(
                "TenantPublicId is required for control-plane operations. Set QueueyOptions.TenantPublicId.");
        return _options.TenantPublicId!;
    }

    private string RequireApiKey()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new QueueyConfigurationException(
                "An API key is required for control-plane operations (SyncStreams). Set QueueyOptions.ApiKey.");
        return _options.ApiKey!;
    }

    private string RequireLicense()
    {
        if (string.IsNullOrWhiteSpace(_options.LicensePublicId))
            throw new QueueyConfigurationException(
                "LicensePublicId is required for control-plane operations (SyncStreams). Set QueueyOptions.LicensePublicId.");
        return _options.LicensePublicId!;
    }
}
