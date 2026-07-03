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
        QueueyOptions options)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
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
    public Task<SyncResult> SyncModelsAsync(SyncOptions? options = null, CancellationToken cancellationToken = default)
        => SyncDefinitionsAsync(Registry.Streams, options, cancellationToken);

    /// <inheritdoc />
    public Task<SyncResult> SyncModelsAsync(IEnumerable<Type> modelTypes, SyncOptions? options = null, CancellationToken cancellationToken = default)
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

        // Fail fast on missing credentials before any network call (dry runs need none).
        if (!options.DryRun)
            RequireForSync();

        var streamResults = new List<StreamApplyResult>();
        var applied = new List<(StreamDefinition Def, string CatalogId)>(); // succeeded streams + their cat_ ids

        foreach (StreamDefinition def in selected)
        {
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

            try
            {
                StreamApplyResult r = await ApplyStreamAsync(def, cancellationToken).ConfigureAwait(false);
                streamResults.Add(r);
                if (r.PublicId is { } catId)
                    applied.Add((def, catId));
            }
            catch (QueueyException ex)
            {
                streamResults.Add(new StreamApplyResult
                {
                    ModelType = def.ModelType?.FullName ?? string.Empty,
                    Name = def.Name,
                    Succeeded = false,
                    Error = ex,
                    Packages = def.Packages,
                });

                if (options.StopOnFirstError)
                    break;
            }
        }

        IReadOnlyList<PackageApplyResult> packageResults =
            await ApplyPackagesAsync(selected, applied, options, cancellationToken).ConfigureAwait(false);

        var result = new SyncResult(streamResults, packageResults);
        if (options.StopOnFirstError)
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
                if (options.StopOnFirstError) break;
                continue;
            }

            if (packageId is null)
            {
                results.Add(new PackageApplyResult { Name = name, Succeeded = false });
                if (options.StopOnFirstError) break;
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

            if (assignError is not null && options.StopOnFirstError)
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

    private void RequireForSync()
    {
        RequireTenant();
        if (string.IsNullOrWhiteSpace(_options.LicensePublicId))
            throw new QueueyConfigurationException("LicensePublicId is required for SyncModels. Set QueueyOptions.LicensePublicId.");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new QueueyConfigurationException("An API key is required for SyncModels. Set QueueyOptions.ApiKey.");
    }

    private string RequireTenant()
    {
        if (string.IsNullOrWhiteSpace(_options.TenantPublicId))
            throw new QueueyConfigurationException("TenantPublicId (the producer tenant) is required for SyncModels. Set QueueyOptions.TenantPublicId.");
        return _options.TenantPublicId!;
    }
}
