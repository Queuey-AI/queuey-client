using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        _connection = new QueueyHttpConnection(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), Observe);
    }

    /// <summary>Applies a stream via <c>PUT /waas/streams</c> (declarative, idempotent by producer + name).</summary>
    public async Task<StreamApplyResponse> ApplyStreamAsync(StreamApplyRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // Credentials are validated when the call is actually made (never at construction), so a
        // publish-only consumer that resolves IQueueyService never trips a control-plane requirement.
        IQueueyAuthenticator authenticator = Authenticator();
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

        IQueueyAuthenticator authenticator = Authenticator();
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
        IQueueyAuthenticator authenticator = Authenticator();
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
        IQueueyAuthenticator authenticator = Authenticator();
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
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "grants");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new GrantPackageRequest { IntegrationPublicId = integrationPublicId }, QueueyJson.Options);

        await _connection.SendAsync(HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes a package grant (<c>DELETE …/packages/{pkg}/grants/{integration}</c>). Idempotent. Returns 204.</summary>
    public async Task RevokePackageAsync(string integrationPublicId, string packagePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "grants", integrationPublicId);
        await _connection.SendAsync(HttpMethod.Delete, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Activates a group key for an integration (<c>POST /waas/activations</c>) — the second routing gate.</summary>
    public async Task<ActivationResponse> ActivateAsync(string integrationPublicId, string groupKey, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
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
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "activations");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new ActivationRequest { ProducerTenantPublicId = RequireTenant(), GroupKey = groupKey, IntegrationPublicId = integrationPublicId }, QueueyJson.Options);

        // The backend's deactivate takes the request in the DELETE body (proxy hazard, but supported by HttpClient).
        await _connection.SendAsync(HttpMethod.Delete, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    // ── Management: tenants + queues (root routes on the API host) ─────────────

    /// <summary>
    /// Creates a tenant (<c>POST /tenants</c>). LicenseId 0 → the backend uses the license header. <paramref name="environment"/>
    /// is left out when null.
    /// </summary>
    public async Task<TenantSummaryResponse> CreateTenantAsync(
        string displayName, bool asProducer, bool withDefaultQueue, CancellationToken cancellationToken, string? environment = null)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new CreateTenantWireRequest
            {
                LicenseId = 0, DisplayName = displayName, CreateAsProducer = asProducer, CreateDefaultQueue = withDefaultQueue, Environment = environment,
            },
            QueueyJson.Options);

        return await _connection.SendForJsonAsync<TenantSummaryResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a queue under a tenant (<c>POST /queues</c>).</summary>
    public async Task<QueueReadResponse> CreateQueueAsync(string tenantPublicId, string displayName, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
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

        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        // I en apply bundet til en plan kan svaret på et nytt forsøk være at steget alt er skrevet. Da finnes ingen kropp å
        // lese, og den som kalte, slår køen opp selv (StepAlreadyWrittenException).
        return await SendWriteAsync(
            () => _connection.SendForJsonAsync<QueueApplyResponse>(
                HttpMethod.Put, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken),
            () => throw new StepAlreadyWrittenException(request.DisplayName),
            cancellationToken).ConfigureAwait(false);
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

        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "policy");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        await SendWriteAsync(() => _connection.SendAsync(
            new HttpMethod("PATCH"), uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // ── Delivery + credentials (the destination, and the secrets it uses) ─────

    /// <summary>Patches the workspace's default endpoint (<c>PATCH /tenants/{ten}/delivery</c>). Returns 204.</summary>
    public async Task PatchTenantDeliveryAsync(string tenantPublicId, PatchTenantDeliveryWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "delivery");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        await SendWriteAsync(() => _connection.SendAsync(
            new HttpMethod("PATCH"), uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Patches one queue's destination (<c>PATCH /queues/{que}/delivery</c>). Returns 204.</summary>
    public async Task PatchQueueDeliveryAsync(string queuePublicId, PatchQueueDeliveryWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "delivery");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        await SendWriteAsync(() => _connection.SendAsync(
            new HttpMethod("PATCH"), uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a delivery credential (<c>POST /tenants/{ten}/credentials</c>). The value is never readable again.</summary>
    public async Task<CredentialWireResponse> CreateCredentialAsync(string tenantPublicId, CreateCredentialWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "credentials");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        return await _connection.SendForJsonAsync<CredentialWireResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rotates the secret of a credential the workspace has (<c>POST /tenants/{ten}/credentials/rotate</c>, Queuey F3.7): the
    /// value becomes a new version under the same id, as the operation <c>rotate_credential</c>. The value is never readable again.
    /// </summary>
    public async Task<CredentialWireResponse> RotateCredentialAsync(string tenantPublicId, RotateCredentialWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "credentials", "rotate");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        return await _connection.SendForJsonAsync<CredentialWireResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks for the secret of a credential that a signed-in person pastes in the console
    /// (<c>POST /tenants/{ten}/credential-requests</c>). 201 for a new request, 200 for the one open for the same name.
    /// </summary>
    public async Task<CredentialRequestWireResponse> RequestCredentialAsync(
        string tenantPublicId, CreateCredentialRequestWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "credential-requests");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        return await _connection.SendForJsonAsync<CredentialRequestWireResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists a workspace's delivery credentials — labels only (<c>GET /tenants/{ten}/credentials</c>).</summary>
    public async Task<List<CredentialWireResponse>> ListCredentialsAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "credentials");
        return await _connection.SendForJsonAsync<List<CredentialWireResponse>>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists a tenant's queues (<c>GET /tenants/{ten}/queues</c>).</summary>
    public async Task<List<QueueListItemResponse>> ListQueuesAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "queues");
        return await _connection.SendForJsonAsync<List<QueueListItemResponse>>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Patches the workspace itself, today its environment (<c>PATCH /tenants/{ten}</c>). Returns 204.</summary>
    public Task PatchTenantAsync(string tenantPublicId, PatchWorkspaceWireRequest request, CancellationToken cancellationToken)
        => PatchAsync(request, cancellationToken, "tenants", tenantPublicId);

    /// <summary>Patches the workspace's behaviour (<c>PATCH /tenants/{ten}/policy</c>). Returns 204.</summary>
    public Task PatchTenantPolicyAsync(string tenantPublicId, PatchTenantPolicyWireRequest request, CancellationToken cancellationToken)
        => PatchAsync(request, cancellationToken, "tenants", tenantPublicId, "policy");

    /// <summary>Patches how a workspace reads arriving events (<c>PATCH /tenants/{ten}/ingress</c>). Returns 204.</summary>
    public Task PatchTenantIngressAsync(string tenantPublicId, PatchIngressWireRequest request, CancellationToken cancellationToken)
        => PatchAsync(request, cancellationToken, "tenants", tenantPublicId, "ingress");

    /// <summary>Patches how one queue reads arriving events (<c>PATCH /queues/{que}/ingress</c>). Returns 204.</summary>
    public Task PatchQueueIngressAsync(string queuePublicId, PatchIngressWireRequest request, CancellationToken cancellationToken)
        => PatchAsync(request, cancellationToken, "queues", queuePublicId, "ingress");

    /// <summary>
    /// Sets whether a queue delivers or only logs (<c>PATCH /queues/{que}/mode-change</c>, the mode as its
    /// number). Returns 204. Never used for pausing: that is a separate lever a deploy does not touch.
    /// </summary>
    public Task SetQueueModeAsync(string queuePublicId, int mode, CancellationToken cancellationToken)
        => PatchAsync(new QueueModeChangeRequest { Mode = mode }, cancellationToken, "queues", queuePublicId, "mode-change");

    /// <summary>
    /// Sends a queue's events to a connected <c>queuey listen</c> session, or back to its HTTP destination
    /// (<c>PATCH /queues/{que}/local-forward</c>). Returns 204. Turning it on needs <c>queue.listen</c>.
    /// </summary>
    public Task SetQueueLocalForwardAsync(string queuePublicId, bool enabled, CancellationToken cancellationToken)
        => PatchAsync(new LocalForwardRequest { Enabled = enabled }, cancellationToken, "queues", queuePublicId, "local-forward");

    /// <summary>
    /// The oldest event on a queue that is still failing (<c>GET /events/{que}?status=Failed</c>,
    /// oldest first), or none. What holds the events behind it on an ordered queue.
    /// </summary>
    public async Task<EventListItemResponse?> GetOldestFailingEventAsync(string queuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), "status=Failed&pageSize=1&sortDirection=asc", "events", queuePublicId);
        EventListPageResponse page = await _connection.SendForJsonAsync<EventListPageResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
        return page.Items?.FirstOrDefault();
    }

    /// <summary>
    /// Reads one page of one event from a queue (<c>GET /events/{que}?pageSize=1</c>): the cheapest read that needs
    /// <c>event.read</c>, so a caller can learn it may read events before it publishes one.
    /// </summary>
    public async Task ReadOneEventAsync(string queuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), "pageSize=1", "events", queuePublicId);
        await _connection.SendForJsonAsync<EventListPageResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one event's status and delivery attempts (<c>GET /events/{que}/{evt}</c>). Envelope only —
    /// the payload is a separate, recorded read this SDK does not make.
    /// </summary>
    public async Task<EventDetailsResponse> GetEventAsync(string queuePublicId, string eventPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "events", queuePublicId, eventPublicId);
        return await _connection.SendForJsonAsync<EventDetailsResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a flow verification of a queue (<c>POST /queues/{que}/verifications</c>): 201 with a new one, or 200 with the
    /// open one that already follows the same event. Either way the body is the verification as it stands.
    /// </summary>
    public async Task<FlowVerification> StartVerificationAsync(string queuePublicId, FlowVerificationWireRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "verifications");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);
        return await _connection.SendForJsonAsync<FlowVerification>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a flow verification (<c>GET /queues/{que}/verifications/{ver}?waitSeconds=N</c>). Queuey waits up to
    /// <paramref name="waitSeconds"/> (at most 20) for it to settle before it answers.
    /// </summary>
    public async Task<FlowVerification> GetVerificationAsync(string queuePublicId, string verificationId, int waitSeconds, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), "waitSeconds=" + waitSeconds.ToString(CultureInfo.InvariantCulture),
            "queues", queuePublicId, "verifications", verificationId);
        return await _connection.SendForJsonAsync<FlowVerification>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One event's envelope as Queuey's REST API answers it (<c>GET /events/{que}/{evt}</c>): status, timings, attempts
    /// and their decisions, and whether its content may be revealed. Never content: Queuey leaves it out for everyone.
    /// Needs <c>event.read</c>.
    /// </summary>
    public async Task<JsonElement> GetEventEnvelopeJsonAsync(string queuePublicId, string eventPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "events", queuePublicId, eventPublicId);
        return await _connection.SendForJsonAsync<JsonElement>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One event's content (<c>GET /events/{que}/{evt}/content</c>): the payload, the resolved header values and the
    /// receiver's responses. Queuey serves it only with <c>event.payload.read</c> and a payload visibility that lets values
    /// out, and records every look before it answers.
    /// </summary>
    public async Task<JsonElement> GetEventContentJsonAsync(string queuePublicId, string eventPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "events", queuePublicId, eventPublicId, "content");
        return await _connection.SendForJsonAsync<JsonElement>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A write sent as a dry run (<c>?dryRun=true</c>): the server runs it the same way, stops before it
    /// stores anything, and answers with what it would have done. Refusals come back as they would.
    /// </summary>
    /// <remarks>
    /// Every 2xx must be a plan that says <c>dryRun: true</c>. Anything else — an empty body, a 204,
    /// text that is not JSON, JSON without the flag — means the server may have done the write, and
    /// throws <see cref="DryRunIgnoredException"/> naming <paramref name="target"/> and
    /// <paramref name="aspect"/>.
    /// </remarks>
    public async Task<TPlan> DryRunAsync<TPlan>(
        string target, string aspect, HttpMethod method, object? request, CancellationToken cancellationToken, params string[] segments)
        where TPlan : DryRunAnswer
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), "dryRun=true", segments);
        byte[]? body = request is null ? null : JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), QueueyJson.Options);

        // En dry run i en plan som bygges på serveren (Queuey F3.11), har planens id i X-Queuey-Plan og aldri apply-tokenet:
        // serveren nekter begge sammen. Svaret sier i X-Queuey-Plan-Step hvilket steg dry run ble.
        string? plan = CurrentPlan.Value;
        var step = new StepBox();
        CurrentStep.Value = step;
        Action<HttpRequestHeaders> headers = plan is null
            ? LicenseHeader(license)
            : h =>
            {
                QueueyHttpHeaders.Set(h, QueueyHeaders.LicensePublicId, license);
                QueueyHttpHeaders.Set(h, PlanHeader, plan);
            };

        // Svaret leses først som JSON av hvilken som helst form, og så som en plan. Før 2026-09-24 ble det
        // lest rett som en plan, så en tom 2xx eller en 204 kastet JsonException ut av CLI-en med stacktrace.
        JsonElement answer;
        try
        {
            answer = await _connection.SendForJsonAsync<JsonElement>(
                method, uri, body, body is null ? null : JsonContentType, authenticator, headers, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new DryRunIgnoredException(target, aspect);
        }
        finally
        {
            CurrentStep.Value = null;
        }

        TPlan? parsed = null;
        if (answer.ValueKind == JsonValueKind.Object)
        {
            try
            {
                parsed = answer.Deserialize<TPlan>(QueueyJson.Options);
            }
            catch (JsonException)
            {
                // Et objekt som ikke er en plan, er det samme som ingen plan.
            }
        }

        if (parsed is { DryRun: true })
        {
            parsed.PlanStep = step.Index;
            return parsed;
        }

        throw new DryRunIgnoredException(target, aspect);
    }

    /// <summary>One PATCH shape for the control plane: JSON body, API key + license header, 204 back.</summary>
    private async Task PatchAsync(object request, CancellationToken cancellationToken, params string[] segments)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, segments);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), QueueyJson.Options);

        await SendWriteAsync(() => _connection.SendAsync(
            new HttpMethod("PATCH"), uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the workspace's delivery + policy config (<c>GET /tenants/{ten}/config</c>).</summary>
    public async Task<TenantConfigResponse> GetTenantConfigAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "config");
        return await _connection.SendForJsonAsync<TenantConfigResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one queue's config with its per-section inherit flags (<c>GET /queues/{que}/config</c>).</summary>
    public async Task<QueueConfigResponse> GetQueueConfigAsync(string queuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "config");
        return await _connection.SendForJsonAsync<QueueConfigResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads what one queue stores itself (<c>GET /queues/{que}</c>): its raw overrides, without what it
    /// inherits. Needs only <c>queue.read</c>.
    /// </summary>
    public async Task<QueueStoredResponse> GetQueueStoredAsync(string queuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId);
        return await _connection.SendForJsonAsync<QueueStoredResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Mints an ingress signing key for a queue (<c>POST /hmacclients/queues/{que}</c>).</summary>
    public async Task<CreateQueueHmacClientWireResponse> MintIngressKeyAsync(
        string queuePublicId, CreateQueueHmacClientWireRequest request, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "hmacclients", "queues", queuePublicId);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);

        return await _connection.SendForJsonAsync<CreateQueueHmacClientWireResponse>(
            HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The signing keys of a queue (<c>GET /hmacclients/queues/{que}</c>): metadata, never a secret.</summary>
    public async Task<List<QueueHmacClientWireResponse>> ListIngressKeysAsync(string queuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "hmacclients", "queues", queuePublicId);
        return await _connection.SendForJsonAsync<List<QueueHmacClientWireResponse>>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes a signing key (<c>POST /hmacclients/hmac-signing-keys/{keyId}/revoke</c>, 204).</summary>
    public async Task RevokeIngressKeyAsync(string keyId, string? reason, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "hmacclients", "hmac-signing-keys", keyId, "revoke");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new RevokeSigningKeyWireRequest { Reason = reason }, QueueyJson.Options);
        await _connection.SendAsync(HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Package lifecycle (update / archive / remove stream) ───────────────────

    /// <summary>Updates a package's name/description (<c>PUT …/packages/{pkg}</c>).</summary>
    public async Task<PackageApplyResponse> UpdatePackageAsync(string packagePublicId, string? name, string? description, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
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
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "archive");
        await _connection.SendAsync(HttpMethod.Post, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a stream from a package (<c>DELETE …/packages/{pkg}/streams/{cat}</c>). Idempotent. Returns 204.</summary>
    public async Task RemoveStreamAsync(string packagePublicId, string catalogEntryPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        string tenant = RequireTenant();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "waas", "producer", tenant, "packages", packagePublicId, "streams", catalogEntryPublicId);
        await _connection.SendAsync(HttpMethod.Delete, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    // ── Observability reads (metrics + issues) ─────────────────────────────────

    /// <summary>Reads a queue traffic snapshot (<c>GET /queues/{q}/metrics/snapshot</c>).</summary>
    public async Task<QueueMetricsSnapshot> GetQueueMetricsSnapshotAsync(string queuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "metrics", "snapshot");
        return await _connection.SendForJsonAsync<QueueMetricsSnapshot>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replays an event to a connected listener (<c>POST /queues/{q}/replay-to-listener/{e}</c>).</summary>
    public async Task<ReplayResult> ReplayToListenerAsync(string queuePublicId, string eventPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "queues", queuePublicId, "replay-to-listener", eventPublicId);
        return await _connection.SendForJsonAsync<ReplayResult>(
            HttpMethod.Post, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists a tenant's issues (<c>GET /issues/{tenant}?status&amp;severity&amp;queuePublicId&amp;limit&amp;cursor</c>).</summary>
    public async Task<IssueListPage> ListIssuesAsync(string tenantPublicId, IssueQuery? query, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), BuildIssueQuery(query), "issues", tenantPublicId);
        return await _connection.SendForJsonAsync<IssueListPage>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one issue's detail (<c>GET /issues/{tenant}/{issue}</c>).</summary>
    public async Task<IssueDetails> GetIssueAsync(string tenantPublicId, string issuePublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
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

    // Apply-tokenet (Queuey F2.4) følger hvert kall i applyen, også lesingene og dry runs: serveren leser det bare på
    // skrivingene apply sender. AsyncLocal, så to applyer i samme prosess, eller en apply og andre kall, ikke deler det.
    private static readonly AsyncLocal<string?> CurrentApply = new();

    /// <summary>The header that carries the apply token on each write of an apply.</summary>
    internal const string ApplyHeader = "X-Queuey-Apply";

    /// <summary>
    /// Makes every call in the current async flow part of the apply <paramref name="token"/> names, until the returned
    /// scope is disposed. A null or blank token changes nothing. <paramref name="plan"/> is the stored plan the apply writes
    /// (Queuey F3.11): then a write Queuey says is already written counts as written, and a write whose answer was lost is
    /// looked up in the plan before it is sent again (<see cref="SendWriteAsync{T}"/>).
    /// </summary>
    internal static IDisposable InApply(string? token, PlanApplyProgress? plan = null)
    {
        string? before = CurrentApply.Value;
        PlanApplyProgress? planBefore = CurrentPlanApply.Value;
        if (!string.IsNullOrWhiteSpace(token))
        {
            CurrentApply.Value = token;
            CurrentPlanApply.Value = plan;
        }

        return new Scope(() =>
        {
            CurrentApply.Value = before;
            CurrentPlanApply.Value = planBefore;
        });
    }

    /// <summary>
    /// Makes every dry run in the current async flow a step of the stored plan <paramref name="planId"/> (Queuey F3.11,
    /// <c>X-Queuey-Plan</c>), until the returned scope is disposed. Its dry runs carry no apply token: Queuey refuses the two
    /// together, and the plan stands for the token on what a deployment file manages.
    /// </summary>
    internal static IDisposable InPlan(string planId)
    {
        string? before = CurrentPlan.Value;
        string? applyBefore = CurrentApply.Value;
        CurrentPlan.Value = planId;
        CurrentApply.Value = null;
        return new Scope(() =>
        {
            CurrentPlan.Value = before;
            CurrentApply.Value = applyBefore;
        });
    }

    /// <summary>
    /// Collects every <c>X-Queuey-Warning</c> Queuey answers in the current async flow into <paramref name="collector"/>, and
    /// passes each on to the collector around it, until the returned scope is disposed.
    /// </summary>
    internal static IDisposable CollectWarnings(ServerWarnings collector)
    {
        if (collector is null) throw new ArgumentNullException(nameof(collector));
        ServerWarnings? before = CurrentWarnings.Value;
        collector.Outer = before;
        CurrentWarnings.Value = collector;
        return new Scope(() => CurrentWarnings.Value = before);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _restore;
        public Scope(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }

    // Planen dry runs bygger (X-Queuey-Plan), og om applyen er bundet til en plan. AsyncLocal, som tokenet.
    private static readonly AsyncLocal<string?> CurrentPlan = new();
    private static readonly AsyncLocal<PlanApplyProgress?> CurrentPlanApply = new();
    private static readonly AsyncLocal<ServerWarnings?> CurrentWarnings = new();
    private static readonly AsyncLocal<StepBox?> CurrentStep = new();

    /// <summary>The header that makes a dry run a step of a stored plan (Queuey F3.11).</summary>
    internal const string PlanHeader = "X-Queuey-Plan";

    /// <summary>The header that says which step of the plan a dry run became.</summary>
    internal const string PlanStepHeader = "X-Queuey-Plan-Step";

    /// <summary>The header Queuey warns in, one per warning: <c>code: message</c>.</summary>
    internal const string WarningHeader = "X-Queuey-Warning";

    private sealed class StepBox
    {
        public int? Index { get; set; }
    }

    // Hvert svar, før det leses: advarslene til den som samler dem, og steget en dry run i en plan ble.
    private static void Observe(HttpResponseMessage response)
    {
        if (CurrentWarnings.Value is { } warnings && response.Headers.TryGetValues(WarningHeader, out IEnumerable<string>? values))
            foreach (string value in values)
                warnings.Add(value);

        if (CurrentStep.Value is { } step && response.Headers.TryGetValues(PlanStepHeader, out IEnumerable<string>? indexes)
            && int.TryParse(indexes.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            step.Index = index;
    }

    // En skriving i en apply bundet til en plan (Queuey F3.11): hvert steg skrives én gang, og serveren svarer 409
    // step_already_applied på det samme steget igjen, som regnes som skrevet. Et svar som gikk tapt (nettet eller en timeout),
    // sendes ikke på nytt med en gang (BØR 1 fra reviewen av #64): etter en timeout kan den første forespørselen fortsatt kjøre,
    // og to samtidige skrivinger på samme steg gjør planen plan_stale for godt. Klienten venter først en kort, økende tid og
    // leser planens appliedSteps. Skrivingene går én om gangen, så et steg mer enn applyen har fått svar på, er denne
    // skrivingen: den regnes som skrevet. Står planen ikke lenger som executing, stopper applyen med statusen. Er steget fortsatt
    // ikke skrevet, sendes det én gang til. Utenfor en slik apply sendes skrivingen én gang, som før.
    private Task SendWriteAsync(Func<Task> send, CancellationToken cancellationToken)
        => SendWriteAsync(async () =>
        {
            await send().ConfigureAwait(false);
            return true;
        }, () => true, cancellationToken);

    private async Task<T> SendWriteAsync<T>(Func<Task<T>> send, Func<T> alreadyWritten, CancellationToken cancellationToken)
    {
        if (CurrentPlanApply.Value is not { } plan)
            return await send().ConfigureAwait(false);

        try
        {
            T answer = await send().ConfigureAwait(false);
            plan.Confirmed++;
            return answer;
        }
        catch (QueueyException ex) when (ex.ErrorCode == StepAlreadyAppliedCode)
        {
            plan.Confirmed++;
            return alreadyWritten();
        }
        catch (Exception ex) when (AnswerLost(ex, cancellationToken))
        {
            if (await WrittenMeanwhileAsync(plan, cancellationToken).ConfigureAwait(false))
                return alreadyWritten();

            try
            {
                T answer = await send().ConfigureAwait(false);
                plan.Confirmed++;
                return answer;
            }
            catch (QueueyException again) when (again.ErrorCode == StepAlreadyAppliedCode)
            {
                plan.Confirmed++;
                return alreadyWritten();
            }
        }
    }

    /// <summary>
    /// How long to wait before each look at the plan after a lost answer, growing. The first request may still be running on
    /// Queuey meanwhile. A setting for the tests.
    /// </summary>
    internal static TimeSpan[] LostAnswerWaits { get; set; } = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

    // Om skrivingen som mistet svaret, står i planens appliedSteps. Kaster når planen ikke kjører lenger.
    private async Task<bool> WrittenMeanwhileAsync(PlanApplyProgress plan, CancellationToken cancellationToken)
    {
        foreach (TimeSpan wait in LostAnswerWaits)
        {
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            PlanWireResponse now;
            try
            {
                now = await GetPlanAsync(plan.Tenant, plan.PlanId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (AnswerLost(ex, cancellationToken))
            {
                continue;
            }

            int applied = now.AppliedSteps?.Count ?? 0;
            if (applied > plan.Confirmed)
            {
                plan.Confirmed = applied;
                return true;
            }

            if (!string.Equals(now.Status, StoredPlan.Statuses.Executing, StringComparison.Ordinal))
                throw new QueueyException(
                    $"An answer from Queuey was lost during the apply of plan {plan.PlanId}, and the plan is {now.Status ?? "gone"} now, "
                    + "so nothing more is sent. What this apply wrote before stands.",
                    409, now.Status == StoredPlan.Statuses.PlanStale ? StoredPlan.Statuses.PlanStale : "plan_not_executing")
                {
                    SuggestedAction = "Plan again: a new plan shows what is left to change.",
                };
        }

        return false;
    }

    /// <summary>The code Queuey answers a step of a plan that is already written with.</summary>
    internal const string StepAlreadyAppliedCode = "step_already_applied";

    private static bool AnswerLost(Exception ex, CancellationToken cancellationToken)
        => ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static Action<HttpRequestHeaders> LicenseHeader(string license)
        => headers =>
        {
            QueueyHttpHeaders.Set(headers, QueueyHeaders.LicensePublicId, license);
            if (CurrentApply.Value is { } token)
                QueueyHttpHeaders.Set(headers, ApplyHeader, token);
        };

    /// <summary>
    /// Starts an apply of a deployment file (<c>POST /tenants/{ten}/deployment/applies</c>): the token for its writes, and the
    /// workspace's management as it is. Null from a Queuey that predates managed resources (404), which has nothing to mark.
    /// </summary>
    public async Task<StartApplyWireResponse?> StartApplyAsync(string tenantPublicId, StartApplyWireRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "deployment", "applies");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);
        try
        {
            return await _connection.SendForJsonAsync<StartApplyWireResponse>(
                HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
        }
        catch (QueueyException ex) when ((ex.StatusCode == 404 || ex.StatusCode == 405) && request.PlanId is null)
        {
            return null;
        }
        catch (JsonException) when (request.PlanId is null)
        {
            // Et svar uten en apply er det samme som ingen apply: skrivingene sendes uten token, og en styrt ressurs nekter
            // dem med Queuey sin egen nektelse.
            return null;
        }
    }

    // ── Lagrede planer (Queuey F3.11) ──────────────────────────────────────────

    /// <summary>
    /// Makes an empty plan for the workspace (<c>POST /tenants/{ten}/deployment/plans</c>), with where the file lives and what
    /// it takes back from a detach. Null from a Queuey that stores no plans (404 or 405 without one of Queuey's codes).
    /// </summary>
    public async Task<PlanWireResponse?> CreatePlanAsync(string tenantPublicId, StartPlanWireRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "deployment", "plans");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, QueueyJson.Options);
        try
        {
            return await _connection.SendForJsonAsync<PlanWireResponse>(
                HttpMethod.Post, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
        }
        catch (QueueyException ex) when ((ex.StatusCode == 404 || ex.StatusCode == 405) && ex.ErrorCode is null)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets what apply sends to the queue step <paramref name="step"/> of the plan creates, once it exists
    /// (<c>PUT …/plans/{plan}/steps/{step}/desired</c>).
    /// </summary>
    public async Task SetPlanDesiredAsync(string tenantPublicId, string planPublicId, int step, JsonElement desired, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "deployment", "plans", planPublicId,
            "steps", step.ToString(CultureInfo.InvariantCulture), "desired");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(desired, QueueyJson.Options);
        await _connection.SendForJsonAsync<JsonElement>(
            HttpMethod.Put, uri, body, JsonContentType, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Seals the plan (<c>POST …/plans/{plan}/seal</c>): its hash, and the policy's decision with the rule.</summary>
    public async Task<SealedPlanWireResponse> SealPlanAsync(string tenantPublicId, string planPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "deployment", "plans", planPublicId, "seal");
        return await _connection.SendForJsonAsync<SealedPlanWireResponse>(
            HttpMethod.Post, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a sealed plan the policy gave to a person to the approval inbox (<c>POST …/plans/{plan}/submit</c>, 202). A plan
    /// already waiting answers the same again.
    /// </summary>
    public async Task<PlanPendingWireResponse> SubmitPlanAsync(string tenantPublicId, string planPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "deployment", "plans", planPublicId, "submit");
        return await _connection.SendForJsonAsync<PlanPendingWireResponse>(
            HttpMethod.Post, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a plan with its steps (<c>GET /tenants/{ten}/deployment/plans/{plan}</c>).</summary>
    public async Task<PlanWireResponse> GetPlanAsync(string tenantPublicId, string planPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();
        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId, "deployment", "plans", planPublicId);
        return await _connection.SendForJsonAsync<PlanWireResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads how the workspace's own settings are managed (<c>GET /tenants/{ten}</c>, the <c>deployment</c> field).</summary>
    public async Task<TenantDeploymentResponse> GetTenantDeploymentAsync(string tenantPublicId, CancellationToken cancellationToken)
    {
        IQueueyAuthenticator authenticator = Authenticator();
        string license = RequireLicense();

        Uri uri = QueueyUri.Build(_options.ResolveApiBaseAddress(), null, "tenants", tenantPublicId);
        return await _connection.SendForJsonAsync<TenantDeploymentResponse>(
            HttpMethod.Get, uri, null, null, authenticator, LicenseHeader(license), cancellationToken).ConfigureAwait(false);
    }

    // «(SyncStreams)» sto i meldingene for alle kall, også apply og verify, og ingen sa hvor verdien settes (review
    // 2026-10-05). Handlingen nevner både SDK-en og CLI-en, fordi begge ender her.
    private string RequireTenant()
        => string.IsNullOrWhiteSpace(_options.TenantPublicId) ? throw MissingSetting.Tenant() : _options.TenantPublicId!;

    // Nøkkelen først, så innloggingen (queuey login, 2026-10-09): en nøkkel som er satt, er et uttrykkelig valg og vinner.
    private IQueueyAuthenticator Authenticator()
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            return new ApiKeyAuthenticator(_options.ApiKey!);
        if (_options.AccessTokenProvider is { } token)
            return new BearerTokenAuthenticator(token);
        throw MissingSetting.ApiKey();
    }

    private string RequireLicense()
        => string.IsNullOrWhiteSpace(_options.LicensePublicId) ? throw MissingSetting.License() : _options.LicensePublicId!;
}
