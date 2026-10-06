using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>Default <see cref="IQueueyService"/> — composes publish (over <see cref="QueueyClient"/>) and sync (over the control-plane).</summary>
public sealed class QueueyService : IQueueyService
{
    private readonly QueueyControlPlaneClient _controlPlane;
    private readonly QueueyOptions _options;

    internal QueueyService(
        QueueyClient client,
        QueueyControlPlaneClient controlPlane,
        StreamRegistry registry,
        QueueyOptions options,
        QueueRegistry? queues = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Queues = queues ?? new QueueRegistry(Array.Empty<QueueDefinition>());
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Integrations = new QueueyIntegrations(controlPlane);
        Management = new QueueyManagement(controlPlane);
    }

    /// <inheritdoc />
    public IQueueyIntegrations Integrations { get; }

    /// <inheritdoc />
    public IQueueyManagement Management { get; }

    /// <inheritdoc />
    public StreamRegistry Registry { get; }

    /// <inheritdoc />
    public QueueRegistry Queues { get; }

    /// <inheritdoc />
    public QueueyClient Client { get; }

    // ---- publish ----

    /// <inheritdoc />
    public Task<PublishResult> PushEventAsync<TData>(string stream, string eventType, string? key, TData data, CancellationToken cancellationToken = default)
        => PushEventAsync(stream, eventType, key, data, new PublishOptions(), cancellationToken);

    /// <inheritdoc />
    public Task<PublishResult> PushEventAsync<TData>(string stream, string eventType, string? key, TData data, PublishOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stream)) throw new ArgumentException("A stream name is required.", nameof(stream));
        if (string.IsNullOrWhiteSpace(eventType)) throw new ArgumentException("An event type is required.", nameof(eventType));
        if (options is null) throw new ArgumentNullException(nameof(options));

        // eventType/key are the first-class params; carry over any extra options from the caller.
        var publishOptions = new PublishOptions
        {
            EventType = eventType,
            GroupKey = key,
            IdempotencyKey = options.IdempotencyKey,
            Source = options.Source,
            ContentType = options.ContentType,
        };

        return Client.Ingress.PublishAsync(stream, data, publishOptions, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PublishResult> PushEventAsync<TModel>(string eventType, string? key, TModel data, CancellationToken cancellationToken = default)
    {
        if (!Registry.TryGetByModel(typeof(TModel), out StreamDefinition def))
        {
            throw new QueueyConfigurationException(
                $"No stream is registered for model '{typeof(TModel).FullName}'. " +
                $"Register it with AddStream<{typeof(TModel).Name}>() or use the overload that takes an explicit stream name.");
        }

        return PushEventAsync(def.Name, eventType, key, data, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PublishResult> PushEventAsync(string stream, string eventType, string? key, byte[] data, PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stream)) throw new ArgumentException("A stream name is required.", nameof(stream));
        if (string.IsNullOrWhiteSpace(eventType)) throw new ArgumentException("An event type is required.", nameof(eventType));

        var publishOptions = new PublishOptions
        {
            EventType = eventType,
            GroupKey = key,
            IdempotencyKey = options?.IdempotencyKey,
            Source = options?.Source,
            ContentType = options?.ContentType,
        };

        return Client.Ingress.PublishAsync(stream, data ?? Array.Empty<byte>(), publishOptions, cancellationToken);
    }

    // ---- sync ----

    /// <inheritdoc />
    public Task<SyncResult> SyncStreamsAsync(SyncOptions? options = null, CancellationToken cancellationToken = default)
        => SyncDefinitionsAsync(Registry.Streams, options, cancellationToken);

    /// <inheritdoc />
    public Task<SyncResult> SyncStreamsAsync(IEnumerable<Type> modelTypes, SyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (modelTypes is null) throw new ArgumentNullException(nameof(modelTypes));
        var definitions = modelTypes.Select(t => StreamDefinitionFactory.FromType(t, null)).ToArray();
        _ = new StreamRegistry(definitions); // validate: throws on duplicate stream names / model types
        return SyncDefinitionsAsync(definitions, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StreamApplyResult> ApplyStreamAsync(StreamDefinition definition, CancellationToken cancellationToken = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        // Last gate before the wire: definitions from the factory are already validated, but a
        // hand-constructed one reaches this method directly. The server rejects a bad name with an
        // unhelpful error, so fail here with the rule instead.
        QueueyName.EnsureValid(definition.Name, "stream name");

        var request = new StreamApplyRequest
        {
            ProducerTenantPublicId = RequireTenant(),
            Name = definition.Name,
            Description = definition.Description,
            EventTypes = definition.EventTypes.Count > 0 ? definition.EventTypes : null, // empty → null → backend []
            PayloadSchema = definition.PayloadSchema,
            IsPublic = definition.IsPublic,
        };

        StreamApplyResponse response = await _controlPlane.ApplyStreamAsync(request, cancellationToken).ConfigureAwait(false);

        return new StreamApplyResult
        {
            ModelType = definition.ModelType?.FullName ?? string.Empty,
            Name = definition.Name,
            Succeeded = true,
            PublicId = response.PublicId,
            Status = response.Status,
            Packages = definition.Packages,
        };
    }

    /// <inheritdoc />
    public async Task<PackageApplyResult> ApplyPackageAsync(string name, string? description = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A package name is required.", nameof(name));

        PackageApplyResponse resp = await _controlPlane
            .ApplyPackageAsync(new PackageApplyRequest { ProducerTenantPublicId = RequireTenant(), Name = name.Trim(), Description = description }, cancellationToken)
            .ConfigureAwait(false);

        return new PackageApplyResult { Name = name.Trim(), PublicId = resp.PublicId, Succeeded = true };
    }

    /// <inheritdoc />
    public async Task<PackageApplyResult> UpdatePackageAsync(string packagePublicId, string? name = null, string? description = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePublicId)) throw new ArgumentException("A package public id is required.", nameof(packagePublicId));

        PackageApplyResponse resp = await _controlPlane
            .UpdatePackageAsync(packagePublicId, name, description, cancellationToken)
            .ConfigureAwait(false);

        return new PackageApplyResult { Name = resp.Name ?? name ?? string.Empty, PublicId = resp.PublicId, Succeeded = true };
    }

    /// <inheritdoc />
    public Task ArchivePackageAsync(string packagePublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePublicId)) throw new ArgumentException("A package public id is required.", nameof(packagePublicId));
        return _controlPlane.ArchivePackageAsync(packagePublicId, cancellationToken);
    }

    /// <inheritdoc />
    public Task AssignStreamToPackageAsync(string packagePublicId, string catalogEntryPublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePublicId)) throw new ArgumentException("A package public id is required.", nameof(packagePublicId));
        if (string.IsNullOrWhiteSpace(catalogEntryPublicId)) throw new ArgumentException("A catalog entry public id is required.", nameof(catalogEntryPublicId));
        return _controlPlane.AssignStreamAsync(packagePublicId, catalogEntryPublicId, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveStreamFromPackageAsync(string packagePublicId, string catalogEntryPublicId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePublicId)) throw new ArgumentException("A package public id is required.", nameof(packagePublicId));
        if (string.IsNullOrWhiteSpace(catalogEntryPublicId)) throw new ArgumentException("A catalog entry public id is required.", nameof(catalogEntryPublicId));
        return _controlPlane.RemoveStreamAsync(packagePublicId, catalogEntryPublicId, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<StreamPlan> Plan()
        => Registry.Streams.Select(ToPlan).ToArray();

    private async Task<SyncResult> SyncDefinitionsAsync(IReadOnlyList<StreamDefinition> definitions, SyncOptions? options, CancellationToken cancellationToken)
    {
        options ??= new SyncOptions();

        var selected = (options.Filter is null ? definitions : definitions.Where(options.Filter)).ToList();

        // ── Preflight: everything checkable without the network, before the first write ──
        // A sync is not a transaction, so the cheapest way to avoid a half-converged workspace is to
        // fail on the whole plan before any of it is applied. Names and duplicates are already
        // enforced at registration; re-checking here also covers hand-built definitions and the
        // by-type overload. Credentials are checked once, not per stream (dry runs need none).
        foreach (StreamDefinition def in selected)
            QueueyName.EnsureValid(def.Name, "stream name");

        if (!options.DryRun)
            RequireForSync();

        var streamResults = new List<StreamApplyResult>();
        var applied = new List<(StreamDefinition Def, string CatalogId)>(); // succeeded streams + their cat_ ids
        var notAttempted = new List<string>();
        bool stopped = false;

        for (int i = 0; i < selected.Count; i++)
        {
            StreamDefinition def = selected[i];

            if (options.DryRun)
            {
                streamResults.Add(new StreamApplyResult
                {
                    ModelType = def.ModelType?.FullName ?? string.Empty,
                    Name = def.Name,
                    Succeeded = true,
                    DryRun = true,
                    Packages = def.Packages,
                });
                continue;
            }

            QueueyException? failure = null;
            try
            {
                StreamApplyResult r = await ApplyStreamAsync(def, cancellationToken).ConfigureAwait(false);

                if (r.PublicId is { } catId)
                {
                    streamResults.Add(r);
                    applied.Add((def, catId));
                }
                else
                {
                    // A 2xx with no catalog id used to count as success while silently dropping the
                    // stream from the package phase — a stream that reports "applied" but reaches no
                    // partner. Treat the missing id as the failure it is.
                    failure = new QueueyException(
                        $"Queuey accepted stream '{def.Name}' but returned no catalog id, so it cannot be " +
                        "assigned to its packages.");
                }
            }
            catch (QueueyException ex)
            {
                failure = ex;
            }

            if (failure is null)
                continue;

            streamResults.Add(new StreamApplyResult
            {
                ModelType = def.ModelType?.FullName ?? string.Empty,
                Name = def.Name,
                Succeeded = false,
                Error = failure,
                Packages = def.Packages,
            });

            if (!options.ContinueOnError)
            {
                // Name what we are NOT going to do, so a stopped run can never read as a clean one.
                for (int rest = i + 1; rest < selected.Count; rest++)
                    notAttempted.Add(selected[rest].Name);

                stopped = true;
                break;
            }
        }

        // Skip the package phase entirely on a stopped run: assigning packages for a partially applied
        // set of streams deepens the divergence the stop was meant to contain.
        IReadOnlyList<PackageApplyResult> packageResults = stopped
            ? Array.Empty<PackageApplyResult>()
            : await ApplyPackagesAsync(selected, applied, options, cancellationToken).ConfigureAwait(false);

        var result = new SyncResult(streamResults, packageResults, notAttempted);

        // All-or-nothing in every mode: a run that did not fully converge throws, so partial success can
        // never be mistaken for success. ContinueOnError only decides how much of the picture is
        // gathered before that happens.
        result.ThrowIfAnyFailed();

        return result;
    }

    // ---- queues ----

    /// <inheritdoc />
    public Task<QueueSyncResult> SyncQueuesAsync(SyncOptions? options = null, CancellationToken cancellationToken = default)
        => SyncQueueDefinitionsAsync(Queues.Queues, options, tenantPublicId: null, hooks: null, cancellationToken);

    /// <inheritdoc />
    public async Task<QueueApplyResult> ApplyQueueAsync(QueueDefinition definition, CancellationToken cancellationToken = default)
    {
        QueueApplyResult result = await ApplyQueueAsync(definition, RequireTenant(), hooks: null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result : throw result.Error!;
    }

    /// <summary>What the deployment path hangs on one queue's apply.</summary>
    private sealed class QueueApplyHooks
    {
        /// <summary>After the queue exists, before its policy — ingress, which <c>bykey</c> needs first.</summary>
        public Func<QueueDefinition, string, CancellationToken, Task>? BeforePolicy { get; init; }

        /// <summary>After the policy — destination, then mode. Returns the queue's warnings and mode.</summary>
        public Func<QueueDefinition, string, QueueApplyResponse, CancellationToken, Task<QueueApplyOutcome>>? AfterApply { get; init; }

        /// <summary>
        /// What to say about a queue this run created before its apply failed, or null when nothing
        /// needs saying. It is in logOnly either way; what makes it deliver depends on the path.
        /// </summary>
        public Func<QueueDefinition, string?>? CreatedButFailed { get; init; }
    }

    private sealed class QueueApplyOutcome
    {
        public QueueApplyOutcome(IReadOnlyList<string> warnings, string? mode)
        {
            Warnings = warnings;
            Mode = mode;
        }

        public IReadOnlyList<string> Warnings { get; }
        public string? Mode { get; }
    }

    private async Task<QueueApplyResult> ApplyQueueAsync(
        QueueDefinition definition,
        string tenantPublicId,
        QueueApplyHooks? hooks,
        CancellationToken cancellationToken = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        QueueyName.EnsureValid(definition.Name, "queue name");

        // Policyen også, før køen finnes (review 2026-10-05). En QueueDefinition kan bygges uten fabrikken som
        // validerer den, og da kom et filter uten conditions eller med null først etter PUT /queues: som en patch
        // serveren avviste, eller en NullReferenceException.
        if (definition.Policy.Validate() is { } reason)
            throw new QueueyConfigurationException($"Queue '{definition.Name}' has an invalid policy: {reason}");

        // The tenant is passed in, never re-read from the options here: a deployment file that names
        // its workspace must put its queues in that workspace too. Before 2026-09-23 the workspace
        // went to the file's tenant and the queues to the configured one.
        QueueApplyResponse response = await _controlPlane.ApplyQueueAsync(
            new QueueApplyRequest
            {
                TenantPublicId = tenantPublicId,
                DisplayName = definition.Name,
            },
            cancellationToken).ConfigureAwait(false);

        if (response.PublicId is not { } queueId)
        {
            throw new QueueyException(
                $"Queuey accepted queue '{definition.Name}' but returned no queue id, so its policy cannot be applied.");
        }

        bool policyApplied = false;
        QueueApplyOutcome outcome;
        try
        {
            // Ingress before policy, for the same reason as at workspace level: bykey needs its key
            // source to exist already.
            if (hooks?.BeforePolicy is not null)
                await hooks.BeforePolicy(definition, queueId, cancellationToken).ConfigureAwait(false);

            // Policy is a separate PATCH, and only when something is actually declared — an all-inherit
            // queue must not send a patch that could pin values it meant to keep inheriting.
            if (!definition.Policy.IsEmpty)
            {
                await _controlPlane.PatchQueuePolicyAsync(queueId, ToPatch(definition.Policy), cancellationToken).ConfigureAwait(false);
                policyApplied = true;
            }

            // Destination and mode, inside the same apply as the rest: a destination that fails to land,
            // or a mode that cannot be set, fails the queue rather than leaving it "applied" while it
            // delivers nowhere.
            outcome = hooks?.AfterApply is not null
                ? await hooks.AfterApply(definition, queueId, response, cancellationToken).ConfigureAwait(false)
                : await ConvergeCreatedQueueModeAsync(definition, queueId, response, cancellationToken).ConfigureAwait(false);
        }
        catch (QueueyException ex)
        {
            // Køen finnes selv om resten feilet. Før 2026-09-24 mistet feilresultatet både id og at den
            // var opprettet, så ingen fikk vite at en ny kø lå igjen i logOnly — og neste apply lar en
            // eksisterende kø beholde modusen sin og ga exit 0. Modussteget er sist, så en kø som ble
            // opprettet her og feilet, har fortsatt modusen en ny kø starter med.
            return new QueueApplyResult
            {
                ModelType = definition.ModelType?.FullName ?? string.Empty,
                Name = definition.Name,
                Succeeded = false,
                Error = ex,
                PublicId = queueId,
                Created = response.Created,
                PolicyApplied = policyApplied,
                Mode = response.Created ? DeploymentQueueMode.LogOnly.ToFileText() : null,
                Warnings = response.Created && (hooks?.CreatedButFailed ?? CreatedButFailedInCode)(definition) is { } warning
                    ? new[] { warning }
                    : Array.Empty<string>(),
            };
        }

        return new QueueApplyResult
        {
            ModelType = definition.ModelType?.FullName ?? string.Empty,
            Name = definition.Name,
            Succeeded = true,
            PublicId = queueId,
            Created = response.Created,
            PolicyApplied = policyApplied,
            Mode = outcome.Mode,
            Warnings = outcome.Warnings,
        };
    }

    /// <summary>A queue declared in code cannot declare a mode, so a later sync never sets one.</summary>
    private static string CreatedButFailedInCode(QueueDefinition definition)
        => $"Queue '{definition.Name}' was created before its apply failed, so it is in logOnly mode: it accepts events and " +
           "logs them without delivering. A later sync leaves an existing queue's mode alone — set it to deliver in the " +
           "Queuey console, or declare \"mode\": \"deliver\" for it in queuey.deploy.json.";

    /// <summary>
    /// The mode step for a queue declared in code, which carries no destination and no mode: a queue
    /// this sync created delivers when it already has somewhere to deliver — the workspace's base
    /// URL. Before 2026-09-23 it stayed in LogOnly and consumed every event to Logged without a word,
    /// because the readiness warning only spoke up when there was no destination at all. A queue
    /// that already existed keeps the mode it has.
    /// </summary>
    private async Task<QueueApplyOutcome> ConvergeCreatedQueueModeAsync(
        QueueDefinition definition, string queueId, QueueApplyResponse response, CancellationToken cancellationToken)
    {
        if (response.Created && response.HasDeliveryTarget)
        {
            await _controlPlane.SetQueueModeAsync(queueId, DeploymentQueueMode.Deliver.ToWire(), cancellationToken).ConfigureAwait(false);
            return new QueueApplyOutcome(Array.Empty<string>(), DeploymentQueueMode.Deliver.ToFileText());
        }

        return new QueueApplyOutcome(
            ReadinessWarnings(definition, response),
            response.Created ? DeploymentQueueMode.LogOnly.ToFileText() : null);
    }

    /// <summary>
    /// The mode step of a deployment, last because it depends on everything before it: the queue's
    /// destination decides whether it can deliver at all.
    /// <list type="bullet">
    /// <item>A declared mode is converged — and <c>deliver</c> with nowhere to deliver fails the queue.</item>
    /// <item>An undeclared mode is left alone on a queue that exists, and a queue this file created
    /// delivers when it has a destination.</item>
    /// <item>The old <c>Paused</c> mode is never changed: changing it also resumes the queue.</item>
    /// </list>
    /// Pausing itself (delivery held, ingress closed) is an operator's lever and never touched.
    /// </summary>
    private async Task<QueueApplyOutcome> ConvergeModeAsync(
        string name,
        string queueId,
        string tenantPublicId,
        DeploymentQueuePlan? plan,
        QueueApplyResponse response,
        QueueListItem? existing,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        bool hasDestination = await HasDestinationAsync(queueId, tenantPublicId, plan, response, cancellationToken).ConfigureAwait(false);

        string? current = response.Created ? "LogOnly" : existing?.Mode;
        DeploymentQueueMode? mode = DeploymentQueueModes.FromBackend(current);
        DeploymentQueueMode? declared = plan?.Mode;

        if (!response.Created && string.Equals(current, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"Queue '{name}' still has the old Paused mode. A deploy does not change it, because changing it " +
                "would also resume the queue: resume it in the Queuey console, then apply again.");
            return new QueueApplyOutcome(warnings, "paused");
        }

        if (declared == DeploymentQueueMode.Deliver && !hasDestination)
        {
            throw new QueueyException(
                $"Queue '{name}' declares \"mode\": \"deliver\" but has nowhere to deliver. Give it a delivery.url, " +
                $"or set workspace.delivery.baseUrl, and apply again. Its mode was left as {current ?? "it was"}.",
                errorCode: "deliver_without_destination");
        }

        DeploymentQueueMode? desired = declared
            ?? (response.Created ? (hasDestination ? DeploymentQueueMode.Deliver : DeploymentQueueMode.LogOnly) : null);

        if (desired is { } target && target != mode)
        {
            await _controlPlane.SetQueueModeAsync(queueId, target.ToWire(), cancellationToken).ConfigureAwait(false);
            mode = target;
        }

        if (mode == DeploymentQueueMode.LogOnly && declared != DeploymentQueueMode.LogOnly)
        {
            warnings.Add(hasDestination
                ? $"Queue '{name}' has a destination but is in logOnly mode, so its events are logged, not delivered. " +
                  "Declare \"mode\": \"deliver\" for it to deliver."
                : $"Queue '{name}' has no delivery target — neither its own nor one inherited from the workspace. " +
                  "It accepts events and logs them without delivering. Give it a delivery.url, or set " +
                  "workspace.delivery.baseUrl, to start delivering.");
        }
        else if (mode == DeploymentQueueMode.Deliver && !hasDestination)
        {
            warnings.Add(
                $"Queue '{name}' is set to deliver but has nowhere to deliver, so its deliveries fail. Give it a " +
                "delivery.url, or set workspace.delivery.baseUrl.");
        }

        if (existing?.DeliveryHeld == true)
            warnings.Add($"Delivery is held on queue '{name}': its events wait until someone resumes it in the Queuey console. A deploy never resumes it.");
        if (existing?.IngressClosed == true)
            warnings.Add($"Queue '{name}' does not accept new events: its ingress was closed in the Queuey console. A deploy never reopens it.");

        return new QueueApplyOutcome(warnings, mode?.ToFileText());
    }

    /// <summary>
    /// Whether the queue has somewhere to deliver after this apply. An absolute URL of its own settles
    /// it. Without one, the apply's answer counts — it already includes the workspace's base URL,
    /// which was patched before any queue — unless this run just changed the queue's destination to
    /// a path or back to inheriting, in which case the answer predates the change and is read again.
    /// </summary>
    private async Task<bool> HasDestinationAsync(
        string queueId, string tenantPublicId, DeploymentQueuePlan? plan, QueueApplyResponse response, CancellationToken cancellationToken)
    {
        // En lokal lytter er et sted å levere (Queuey F2.3): eventene venter på den, de logges ikke bort.
        if (IsAbsoluteUrl(plan?.Delivery?.Url) || plan?.Kind == DeploymentDeliveryKind.LocalForward)
            return true;

        if (plan?.Delivery is null || response.Created)
            return response.HasDeliveryTarget;

        IReadOnlyList<QueueListItem> rows = await Management.ListQueuesAsync(tenantPublicId, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(r => r.PublicId == queueId)?.HasDeliveryTarget ?? response.HasDeliveryTarget;
    }

    private static bool IsAbsoluteUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
           && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    /// <inheritdoc />
    public IReadOnlyList<QueuePlan> PlanQueues()
        => Queues.Queues.Select(d => new QueuePlan
        {
            ModelType = d.ModelType?.FullName,
            Name = d.Name,
            Policy = d.Policy,
        }).ToArray();

    /// <inheritdoc />
    public async Task<(QueueSyncResult Queues, SyncResult Streams)> SyncAsync(SyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Queues first: a stream is published on top of a queue, so converging queues first means a
        // stream apply never races the queue it needs. A queue failure throws before any stream is
        // touched, which is the same all-or-nothing contract one level up.
        QueueSyncResult queues = await SyncQueuesAsync(options, cancellationToken).ConfigureAwait(false);
        SyncResult streams = await SyncStreamsAsync(options, cancellationToken).ConfigureAwait(false);
        return (queues, streams);
    }

    /// <inheritdoc />
    public Task<DeploymentFile> PullDeploymentAsync(string? tenantPublicId = null, CancellationToken cancellationToken = default)
        => new DeploymentPuller(_controlPlane, Management)
            .PullAsync(tenantPublicId ?? RequireTenant(), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DriftItem>> CheckDeploymentAsync(
        DeploymentFile file, string? tenantPublicId = null, CancellationToken cancellationToken = default)
        => (await InspectDeploymentAsync(file, tenantPublicId, cancellationToken).ConfigureAwait(false)).Drift;

    /// <inheritdoc />
    public async Task<DeploymentCheck> InspectDeploymentAsync(
        DeploymentFile file, string? tenantPublicId = null, CancellationToken cancellationToken = default)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));

        DeploymentFile declared = file.Expand();
        _ = declared.Resolve();   // a file that cannot be applied is a failure, not "no drift"

        string tenant = declared.Tenant ?? tenantPublicId ?? RequireTenant();

        // Effective values, not the inherit-aware file a pull writes: that one leaves out a queue's
        // value when it equals the workspace's, so a file declaring it reported drift right after a
        // clean apply (2026-09-23).
        DeploymentFile actual = await new DeploymentPuller(_controlPlane, Management)
            .PullAsync(tenant, cancellationToken, effective: true).ConfigureAwait(false);

        IReadOnlyList<DriftItem> drift = DeploymentDrift.Compare(declared, actual);

        // Det en person har løsrevet (Queuey F2.4), hopper apply over: det er ikke drift, men det meldes, med hvem og når.
        var detached = new List<SkippedResource>();
        if (declared.Workspace is not null && await WorkspaceManagementAsync(tenant, cancellationToken).ConfigureAwait(false) is { IsDetached: true } workspace)
            detached.Add(new SkippedResource { Target = "workspace", Management = workspace });
        if (declared.Queues.Count > 0)
        {
            foreach (QueueListItem row in await Management.ListQueuesAsync(tenant, cancellationToken).ConfigureAwait(false))
            {
                if (row.DisplayName is { } name && declared.Queues.ContainsKey(name) && row.Deployment is { IsDetached: true } management)
                    detached.Add(new SkippedResource { Target = "queues." + name, QueueName = name, Management = management });
            }
        }

        return new DeploymentCheck
        {
            Drift = drift.Where(d => !detached.Any(s => Covers(s, d.Path))).ToList(),
            Detached = detached,
        };
    }

    private static bool Covers(SkippedResource skipped, string path)
        => path == skipped.Target || path.StartsWith(skipped.Target + ".", StringComparison.Ordinal);

    // Workspacets styring, når kalleren kan lese den. Den er et tillegg i sjekken, så en lesing som ikke går, gjør ikke
    // sjekken feilet: driften er riktig uansett.
    private async Task<DeploymentManagementInfo?> WorkspaceManagementAsync(string tenant, CancellationToken cancellationToken)
    {
        try
        {
            return (await _controlPlane.GetTenantDeploymentAsync(tenant, cancellationToken).ConfigureAwait(false)).Deployment?.ToInfo();
        }
        catch (QueueyException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<DeploymentPlan> PlanDeploymentAsync(DeploymentFile file, CancellationToken cancellationToken = default)
        => PlanDeploymentAsync(file, null, cancellationToken);

    /// <inheritdoc />
    public async Task<DeploymentPlan> PlanDeploymentAsync(DeploymentFile file, SyncOptions? options, CancellationToken cancellationToken = default)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));

        file = file.Expand();
        IReadOnlyList<DeploymentQueuePlan> plans = file.Resolve();   // lokal validering først, som apply
        EnsureReachableDestinations(file);
        string tenant = RequireForSync(file.Tenant);

        // Planen starter en apply som apply gjør (Queuey F2.4): dry runs mot det fila styrer slippes gjennom bare inne i en, og
        // planen hopper over det samme som applyen ville hoppet over. En dry run merker ingenting.
        var existing = new Dictionary<string, QueueListItem>(StringComparer.Ordinal);
        foreach (QueueListItem row in await Management.ListQueuesAsync(tenant, cancellationToken).ConfigureAwait(false))
            if (row.DisplayName is { } name)
                existing[name] = row;
        ManagedApply managed = await StartManagedApplyAsync(
                tenant, options?.Source, options?.Adopt, existing, plans, file.Workspace is not null, cancellationToken)
            .ConfigureAwait(false);
        using IDisposable inApply = QueueyControlPlaneClient.InApply(managed.Token);

        DeploymentPlan plan = await new DeploymentPlanner(_controlPlane, Management, _options.ResolveIngressBaseAddress())
            .PlanAsync(file, plans, tenant, cancellationToken, managed.Skipped).ConfigureAwait(false);
        return new DeploymentPlan
        {
            Tenant = plan.Tenant,
            PlanId = plan.PlanId,
            PlanHash = plan.PlanHash,
            Queues = plan.Queues,
            Steps = plan.Steps,
            Skipped = plan.Skipped,
            ApplyStarted = managed.Started,
        };
    }

    private void EnsureReachableDestinations(DeploymentFile expanded)
        => DeploymentDestinations.EnsureReachable(expanded, _options.ResolveApiBaseAddress());

    /// <inheritdoc />
    public Task<DeliveryVerification> VerifyDeliveryAsync(
        string queueName, byte[] payload, VerifyDeliveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueName)) throw new ArgumentException("A queue name is required.", nameof(queueName));
        if (payload is null) throw new ArgumentNullException(nameof(payload));

        return DeliveryVerifier.RunAsync(Client, _controlPlane, Management, _options.TenantPublicId, queueName, payload,
            options ?? new VerifyDeliveryOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<FlowVerification> VerifyFlowAsync(
        string queue, FlowVerificationRequest request, IProgress<FlowVerification>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queue)) throw new ArgumentException("A queue, its id or its name, is required.", nameof(queue));
        if (request is null) throw new ArgumentNullException(nameof(request));

        return FlowVerifier.RunAsync(_controlPlane, Management, _options.TenantPublicId, queue, request, progress, cancellationToken);
    }

    /// <inheritdoc />
    public Task<QueuePublishResult> PublishToQueueAsync(
        string queue, byte[] payload, QueuePublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queue)) throw new ArgumentException("A queue, its name or its id, is required.", nameof(queue));
        if (payload is null) throw new ArgumentNullException(nameof(payload));

        return QueuePublisher.RunAsync(Client, _controlPlane, Management, _options, queue.Trim(), payload, options ?? new QueuePublishOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<EventRead> GetEventAsync(string queue, string eventPublicId, bool revealContent = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queue)) throw new ArgumentException("A queue, its name or its id, is required.", nameof(queue));
        if (string.IsNullOrWhiteSpace(eventPublicId)) throw new ArgumentException("An event id (evt_…) is required.", nameof(eventPublicId));

        return EventReader.RunAsync(_controlPlane, Management, _options.TenantPublicId, queue.Trim(), eventPublicId.Trim(), revealContent, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<QueueSyncResult> ApplyDeploymentAsync(
        DeploymentFile file, SyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        options ??= new SyncOptions();

        // Expand ${VAR} first: a file that names an unset variable must fail before a single write,
        // not halfway through one.
        file = file.Expand();

        IReadOnlyList<DeploymentQueuePlan> plans = file.Resolve();   // validates names + policy locally
        // Også i en dry run (Queuey F2.3-review, 2026-10-06): den skal feile der applyen ville feilet, for en som kaller
        // biblioteket som for CLI-en.
        EnsureReachableDestinations(file);
        var byName = plans.ToDictionary(p => p.Definition.Name, StringComparer.Ordinal);

        string tenant = file.Tenant ?? (options.DryRun ? _options.TenantPublicId ?? string.Empty : RequireTenant());
        CredentialStoring storing = CredentialStoring.For(file);
        var existing = new Dictionary<string, QueueListItem>(StringComparer.Ordinal);
        ResolvedDeliveries? deliveries = null;
        var workspaceWarnings = new List<string>();

        // The queues as they are before this run touches anything: an existing queue's mode and flow
        // decide what the mode step may change and what it has to say. One read for the whole file,
        // and first, so a key that cannot read the workspace fails before it has written to it.
        if (!options.DryRun)
        {
            foreach (QueueListItem row in await Management.ListQueuesAsync(tenant, cancellationToken).ConfigureAwait(false))
            {
                if (row.DisplayName is { } name)
                    existing[name] = row;
            }

            // Every credential name, before the first write: a name that is missing fails the run
            // here, with nothing sent, instead of halfway through it.
            deliveries = await new CredentialResolver(Management, tenant, storing)
                .ResolveAllAsync(file.Workspace?.Delivery, plans, cancellationToken).ConfigureAwait(false);

            // Backoff-takene også før første skriving (2026-10-05). Før feilet en ventetid Queuey avviser, først på
            // køen den sto på, etter at workspacet og køene foran allerede var skrevet.
            await BackoffCeilings.EnsureAsync(_controlPlane, tenant, file,
                plans.Where(p => options.QueueFilter?.Invoke(p.Definition) ?? true), existing, cancellationToken).ConfigureAwait(false);
        }

        // Applyen starter før første skriving (Queuey F2.4): serveren utsteder tokenet som merker det applyen skriver som
        // styrt fra fila, og slipper skrivingene gjennom på det fila alt styrer. Et løsrevet workspace og løsrevne køer hoppes
        // over, med mindre --adopt tar dem tilbake.
        ManagedApply managed = options.DryRun
            ? ManagedApply.None
            : await StartManagedApplyAsync(tenant, options.Source, options.Adopt, existing, plans, file.Workspace is not null, cancellationToken)
                .ConfigureAwait(false);
        using IDisposable inApply = QueueyControlPlaneClient.InApply(managed.Token);
        var skippedQueues = new HashSet<string>(managed.Skipped.Where(s => s.QueueName is not null).Select(s => s.QueueName!), StringComparer.Ordinal);

        // Workspace first: queues inherit from it, so converging it first means a queue that means to
        // inherit already has something to inherit. A failure here throws before any queue is
        // touched — the same all-or-nothing contract, one level up.
        if (!options.DryRun && !managed.SkipsWorkspace && file.Workspace is { } workspace)
        {
            // Miljø-merket før alt annet (Queuey F2.2): bare en person senker det, så en nøkkel som ville senket det, nektes
            // med 403, og da er ingenting annet skrevet. Feilen går ut som Queuey sa den, med hva en person gjør.
            if (workspace.EnvironmentToSend is { } environment)
                await _controlPlane.PatchTenantAsync(tenant, QueueyManagement.WireOfEnvironment(environment), cancellationToken)
                    .ConfigureAwait(false);

            // Ingress FIRST, and not for tidiness: "ordering: bykey" is rejected unless a group-key
            // source already exists, so a policy patch that arrives before the ingress one fails
            // validation on a file that is perfectly correct.
            if (workspace.Ingress is { } ingress && !ingress.IsEmpty)
            {
                await Management.SetIngressAsync(tenant, isQueue: false, ingress, cancellationToken).ConfigureAwait(false);

                // En credential som ikke er lagret ennå, godtas (Queuey F2.3), og da avviser ingressen alt. Det sies her, én
                // gang for workspacet, og ikke for hver kø som arver det.
                if (ingress.SignedRequest is not null)
                {
                    TenantConfigResponse stored = await _controlPlane.GetTenantConfigAsync(tenant, cancellationToken).ConfigureAwait(false);
                    if (AwaitedCredentialWarning("The workspace's ingress", "every queue that inherits it refuses every event", stored.Ingress, storing) is { } waiting)
                        workspaceWarnings.Add(waiting);
                }
            }

            if (workspace.HasPolicy)
                await Management.SetWorkspacePolicyAsync(tenant, workspace, cancellationToken).ConfigureAwait(false);

            if (workspace.Delivery is { IsEmpty: false } && deliveries?.Workspace is { } resolved)
                await Management.SetWorkspaceDeliveryAsync(tenant, resolved, cancellationToken).ConfigureAwait(false);
        }

        QueueSyncResult result = await SyncQueueDefinitionsAsync(
            plans.Select(p => p.Definition).Where(d => !skippedQueues.Contains(d.Name)).ToArray(),
            options,
            tenant,
            new QueueApplyHooks
            {
                BeforePolicy = async (definition, queuePublicId, ct) =>
                {
                    if (byName.TryGetValue(definition.Name, out DeploymentQueuePlan? plan) && plan.Ingress is { } queueIngress)
                        await Management.SetIngressAsync(queuePublicId, isQueue: true, queueIngress, ct).ConfigureAwait(false);
                },
                AfterApply = async (definition, queuePublicId, response, ct) =>
                {
                    byName.TryGetValue(definition.Name, out DeploymentQueuePlan? plan);
                    if (deliveries is not null && deliveries.Queues.TryGetValue(definition.Name, out QueueDelivery? resolved))
                        await Management.SetQueueDeliveryAsync(queuePublicId, resolved, ct).ConfigureAwait(false);

                    // Leveringstypen etter målet og før modusen (Queuey F2.3): modusen spør om køen har et sted å levere, og en
                    // lokal lytter er et.
                    if (plan?.Kind is { } kind)
                        await _controlPlane.SetQueueLocalForwardAsync(queuePublicId, kind == DeploymentDeliveryKind.LocalForward, ct).ConfigureAwait(false);

                    existing.TryGetValue(definition.Name, out QueueListItem? row);
                    QueueApplyOutcome outcome = await ConvergeModeAsync(definition.Name, queuePublicId, tenant, plan, response,
                        response.Created ? null : row, ct).ConfigureAwait(false);

                    IReadOnlyList<string> readiness = await ReadinessOfDeclarationsAsync(definition.Name, queuePublicId, plan, storing, ct).ConfigureAwait(false);
                    return readiness.Count == 0 ? outcome : new QueueApplyOutcome(outcome.Warnings.Concat(readiness).ToArray(), outcome.Mode);
                },
                CreatedButFailed = definition =>
                {
                    byName.TryGetValue(definition.Name, out DeploymentQueuePlan? plan);
                    return plan?.Mode switch
                    {
                        // Der fila vil ha den: ingenting å si.
                        DeploymentQueueMode.LogOnly => null,
                        DeploymentQueueMode.Deliver =>
                            $"Queue '{definition.Name}' was created before its apply failed, so it is in logOnly mode for now. " +
                            "The file declares \"mode\": \"deliver\", so the next apply sets it once the error is fixed.",
                        _ =>
                            $"Queue '{definition.Name}' was created before its apply failed, so it is in logOnly mode: it accepts " +
                            "events and logs them without delivering. A later apply leaves an existing queue's mode alone, so it " +
                            "will not start delivering by itself — declare \"mode\": \"deliver\" for it, then apply again.",
                    };
                },
            },
            cancellationToken,
            workspaceWarnings,
            managed.Skipped,
            managed.Enforcement,
            options.DryRun ? null : managed.Started).ConfigureAwait(false);

        return result;
    }

    /// <summary>What an apply learned when it started: its token, what it skips, and what Queuey does outside an apply.</summary>
    private sealed record ManagedApply(string? Token, IReadOnlyList<SkippedResource> Skipped, string? Enforcement)
    {
        public static readonly ManagedApply None = new(null, Array.Empty<SkippedResource>(), null);

        public bool SkipsWorkspace => Skipped.Any(s => s.QueueName is null);

        /// <summary>True when Queuey started an apply, so the writes carry its token and mark what they write.</summary>
        public bool Started => Token is not null;
    }

    /// <summary>
    /// Starts the apply on the server (Queuey F2.4), and works out what it skips: the workspace and the declared queues a
    /// person detached, unless <paramref name="adopt"/> names them. A Queuey that predates managed resources answers with
    /// no apply, and then nothing is marked; what a person detached is still skipped, from what the reads say, and adopt
    /// takes nothing back, since only an apply marks it again.
    /// </summary>
    private async Task<ManagedApply> StartManagedApplyAsync(
        string tenant, DeploymentFileSource? source, IReadOnlyList<string>? adopt,
        IReadOnlyDictionary<string, QueueListItem> existing, IReadOnlyList<DeploymentQueuePlan> plans, bool declaresWorkspace,
        CancellationToken cancellationToken)
    {
        bool adoptsWorkspace = DeploymentAdopt.AdoptsWorkspace(adopt);
        IReadOnlyList<string> adoptQueues = DeploymentAdopt.Queues(adopt);
        DeploymentFileSource? clean = source is null ? null : DeploymentFileSource.Clean(source.Repo, source.Path, source.Commit);

        StartApplyWireResponse? started = await _controlPlane.StartApplyAsync(tenant, new StartApplyWireRequest
        {
            Source = clean is { IsEmpty: false } ? new StartApplySourceWire { Repo = clean.Repo, Path = clean.Path, Commit = clean.Commit } : null,
            Adopt = adoptsWorkspace || adoptQueues.Count > 0
                ? new StartApplyAdoptWire { Workspace = adoptsWorkspace, Queues = adoptQueues.Count > 0 ? adoptQueues.ToList() : null }
                : null,
        }, cancellationToken).ConfigureAwait(false);

        string? token = started?.Token is { Length: > 0 } issued ? issued : null;

        // Det en person har løsrevet, hoppes over også når ingen apply startet (sikkerhetsreviewen 2026-10-06): skrivingene går
        // da uten token, og ville ellers skrevet over det. Uten en apply tar --adopt ikke noe tilbake, for bare en apply merker
        // det som styrt igjen. Workspacets styring står i svaret på starten; uten en apply leses den, når fila har et workspace.
        DeploymentManagementInfo? workspace = started?.Workspace?.ToInfo();
        if (workspace is null && token is null && declaresWorkspace)
            workspace = await WorkspaceManagementAsync(tenant, cancellationToken).ConfigureAwait(false);

        var skipped = new List<SkippedResource>();
        if (workspace is { IsDetached: true } && !(adoptsWorkspace && token is not null))
            skipped.Add(new SkippedResource { Target = "workspace", Management = workspace });
        foreach (DeploymentQueuePlan plan in plans)
        {
            string name = plan.Definition.Name;
            if (existing.TryGetValue(name, out QueueListItem? row) && row.Deployment is { IsDetached: true } detached
                && !(adoptQueues.Contains(name, StringComparer.Ordinal) && token is not null))
                skipped.Add(new SkippedResource { Target = "queues." + name, QueueName = name, Management = detached });
        }

        return new ManagedApply(token, skipped, token is null ? null : started!.Enforcement);
    }

    /// <summary>
    /// What a queue's declared kind and ingress leave it waiting for, read back once its apply landed: a local listener,
    /// a workspace that keeps forwarding a queue the file sends over HTTP, or a credential that is not stored yet, which
    /// makes the ingress refuse every event. Read only when the file declares one of them, so other applies cost nothing.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadinessOfDeclarationsAsync(
        string name, string queueId, DeploymentQueuePlan? plan, CredentialStoring storing, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (plan?.Kind == DeploymentDeliveryKind.LocalForward)
            warnings.Add($"Queue '{name}' delivers to a local listener: its events wait until queuey listen --queue {name} connects, " +
                         "and none go to its URL meanwhile.");

        if (plan?.Kind != DeploymentDeliveryKind.Http && plan?.Ingress?.SignedRequest is null)
            return warnings;

        QueueConfigResponse config = await _controlPlane.GetQueueConfigAsync(queueId, cancellationToken).ConfigureAwait(false);

        if (plan.Kind == DeploymentDeliveryKind.Http && DeploymentDeliveryKinds.FromBackend(config.Delivery?.Kind) == DeploymentDeliveryKind.LocalForward)
            warnings.Add($"Queue '{name}' declares \"kind\": \"http\", but its workspace forwards every queue that inherits its delivery " +
                         "to a local listener, so it keeps forwarding. Turn the workspace's forwarding off in the Queuey console.");

        if (plan.Ingress?.SignedRequest is not null
            && AwaitedCredentialWarning($"Queue '{name}'", "its ingress refuses every event", config.Ingress, storing) is { } waiting)
            warnings.Add(waiting);

        return warnings;
    }

    /// <summary>
    /// The readiness warning for an ingress that waits for a credential: it checks signatures, and the credential it names
    /// is not stored yet. Null otherwise.
    /// </summary>
    internal static string? AwaitedCredentialWarning(string who, string consequence, IngressResponse? ingress, CredentialStoring storing)
    {
        if (ingress?.SignedRequest is not { PendingCredential: { Length: > 0 } awaited } signed || !ChecksSignatures(ingress.AuthMode))
            return null;

        // Queuey F2.3-review (2026-10-06): navnet er lagret av en med skrivetilgang. Det står i teksten og i kommandoen bare
        // når det har den trygge formen (CredentialNameRules.Showable); ellers står en plassholder. Malen er Queuey sin.
        // Utenfor dev limer en person inn verdien (CredentialStoring, F2.9); før sto credentials set her også i prod. Med set
        // må en Queuey fra før credential-forespørsler ha en apply til før ingressen bruker den.
        string template = CredentialNameRules.FitsPendingShape(signed.Template) ? signed.Template! : "provider";
        string again = storing.AsksAPerson ? "" : " Then run queuey apply again.";
        return CredentialNameRules.Showable(awaited) is { } name
            ? $"{who} verifies {template} signatures with the credential '{name}', which is not stored yet, so {consequence}. " +
              storing.HowToStore(name, CredentialStoring.RequestDefaultType) + again
            : $"{who} verifies {template} signatures with a credential that is not stored yet, so {consequence}. Store it " +
              "under the name ingress.signedRequest.credentialRef gives. " +
              storing.HowToStore("<NAME>", CredentialStoring.RequestDefaultType) + again;
    }

    private static bool ChecksSignatures(string? authMode)
        => authMode is not null
           && (authMode.Equals("SignedRequest", StringComparison.OrdinalIgnoreCase)
               || authMode.Equals("ApiKeyAndSignedRequest", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Readiness observations — never failures. The declared state landed; these say the workspace is
    /// not fully wired yet. A queue a sync just created legitimately has no endpoint and starts in
    /// LogOnly, which CONSUMES events to terminal Logged rather than delivering them — so saying
    /// nothing would leave a producer publishing into something that looks like it works.
    /// </summary>
    private static IReadOnlyList<string> ReadinessWarnings(QueueDefinition definition, QueueApplyResponse response)
    {
        if (response.HasDeliveryTarget)
            return Array.Empty<string>();

        return new[]
        {
            $"Queue '{definition.Name}' has no delivery target — neither its own nor one inherited from the " +
            "workspace. It accepts events and logs them without delivering. Set the workspace's default " +
            "endpoint, or give this queue one, to start delivering.",
        };
    }

    internal static QueuePolicyPatchRequest ToPatch(QueuePolicy policy) => new()
    {
        Ordering = policy.Ordering,
        DlqEnabled = policy.DlqEnabled,
        RetentionDays = policy.RetentionDays,
        Idempotent = policy.Idempotent,
        Backoff = RetryBackoffWire.From(policy.Backoff),
        Filter = DeliveryFilterWire.From(policy.Filter),
    };

    /// <summary>
    /// The queue twin of <see cref="SyncDefinitionsAsync"/> — same contract, deliberately: preflight
    /// locally, stop at the first failure, name what was not attempted, and throw unless everything
    /// converged.
    /// </summary>
    private async Task<QueueSyncResult> SyncQueueDefinitionsAsync(
        IReadOnlyList<QueueDefinition> definitions,
        SyncOptions? options,
        string? tenantPublicId,
        QueueApplyHooks? hooks,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? workspaceWarnings = null,
        IReadOnlyList<SkippedResource>? skipped = null,
        string? enforcement = null,
        bool? applyStarted = null)
    {
        options ??= new SyncOptions();

        var selected = (options.QueueFilter is null ? definitions : definitions.Where(options.QueueFilter)).ToList();

        foreach (QueueDefinition def in selected)
            QueueyName.EnsureValid(def.Name, "queue name");

        string tenant = string.Empty;
        if (!options.DryRun)
            tenant = RequireForSync(tenantPublicId);

        var results = new List<QueueApplyResult>();
        var notAttempted = new List<string>();

        for (int i = 0; i < selected.Count; i++)
        {
            QueueDefinition def = selected[i];

            if (options.DryRun)
            {
                results.Add(new QueueApplyResult
                {
                    ModelType = def.ModelType?.FullName ?? string.Empty,
                    Name = def.Name,
                    Succeeded = true,
                    DryRun = true,
                    PolicyApplied = !def.Policy.IsEmpty,
                });
                continue;
            }

            QueueApplyResult outcome;
            try
            {
                outcome = await ApplyQueueAsync(def, tenant, hooks, cancellationToken).ConfigureAwait(false);
            }
            catch (QueueyException ex)
            {
                // Feilet før køen fantes (eller før svaret sa hvilken den er): det er ingen kø å rapportere.
                outcome = new QueueApplyResult
                {
                    ModelType = def.ModelType?.FullName ?? string.Empty,
                    Name = def.Name,
                    Succeeded = false,
                    Error = ex,
                };
            }

            results.Add(outcome);
            if (outcome.Succeeded)
                continue;

            if (!options.ContinueOnError)
            {
                for (int rest = i + 1; rest < selected.Count; rest++)
                    notAttempted.Add(selected[rest].Name);
                break;
            }
        }

        var result = new QueueSyncResult(results, notAttempted)
        {
            WorkspaceWarnings = workspaceWarnings ?? Array.Empty<string>(),
            Skipped = skipped ?? Array.Empty<SkippedResource>(),
            Enforcement = enforcement,
            ApplyStarted = applyStarted,
        };
        result.ThrowIfAnyFailed();
        return result;
    }

    /// <summary>
    /// Package phase: idempotently upsert each declared package (PUT /waas/packages), then assign each
    /// applied stream to the packages it declares (additive, idempotent). No-op when nothing declares a package.
    /// </summary>
    private async Task<IReadOnlyList<PackageApplyResult>> ApplyPackagesAsync(
        IReadOnlyList<StreamDefinition> selected,
        IReadOnlyList<(StreamDefinition Def, string CatalogId)> applied,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        var packageNames = selected
            .SelectMany(d => d.Packages)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (packageNames.Count == 0)
            return Array.Empty<PackageApplyResult>();

        if (options.DryRun)
        {
            return packageNames.Select(name => new PackageApplyResult
            {
                Name = name,
                Succeeded = true,
                DryRun = true,
                AssignedStreams = selected.Count(d => d.Packages.Contains(name, StringComparer.Ordinal)),
            }).ToArray();
        }

        var results = new List<PackageApplyResult>();
        foreach (string name in packageNames)
        {
            string? packageId;
            try
            {
                PackageApplyResponse resp = await _controlPlane
                    .ApplyPackageAsync(new PackageApplyRequest { ProducerTenantPublicId = RequireTenant(), Name = name }, cancellationToken)
                    .ConfigureAwait(false);
                packageId = resp.PublicId;
            }
            catch (QueueyException ex)
            {
                results.Add(new PackageApplyResult { Name = name, Succeeded = false, Error = ex });
                if (!options.ContinueOnError) break;
                continue;
            }

            if (packageId is null)
            {
                results.Add(new PackageApplyResult { Name = name, Succeeded = false });
                if (!options.ContinueOnError) break;
                continue;
            }

            int assigned = 0;
            QueueyException? assignError = null;
            foreach ((StreamDefinition def, string catId) in applied)
            {
                if (!def.Packages.Contains(name, StringComparer.Ordinal))
                    continue;
                try
                {
                    await _controlPlane.AssignStreamAsync(packageId, catId, cancellationToken).ConfigureAwait(false);
                    assigned++;
                }
                catch (QueueyException ex)
                {
                    assignError = ex;
                    break;
                }
            }

            results.Add(new PackageApplyResult
            {
                Name = name,
                PublicId = packageId,
                AssignedStreams = assigned,
                Succeeded = assignError is null,
                Error = assignError,
            });

            if (assignError is not null && !options.ContinueOnError)
                break;
        }

        return results;
    }

    private static StreamPlan ToPlan(StreamDefinition d) => new()
    {
        ModelType = d.ModelType?.FullName,
        Name = d.Name,
        Description = d.Description,
        EventTypes = d.EventTypes,
        IsPublic = d.IsPublic,
        Packages = d.Packages,
        HasPayloadSchema = !string.IsNullOrEmpty(d.PayloadSchema),
    };

    private void RequireForSync() => RequireForSync(null);

    /// <summary>The tenant a sync writes to — <paramref name="tenantPublicId"/> when the caller named one — after checking the rest is configured.</summary>
    private string RequireForSync(string? tenantPublicId)
    {
        string tenant = string.IsNullOrWhiteSpace(tenantPublicId) ? RequireTenant() : tenantPublicId!;
        if (string.IsNullOrWhiteSpace(_options.LicensePublicId))
            throw MissingSetting.License();
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw MissingSetting.ApiKey();
        return tenant;
    }

    private string RequireTenant()
        => string.IsNullOrWhiteSpace(_options.TenantPublicId) ? throw MissingSetting.Tenant() : _options.TenantPublicId!;
}
