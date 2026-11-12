using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Queuey.Server.Hosting;

namespace Aspire.Hosting;

/// <summary>Adds a self-hosted Queuey to an Aspire AppHost, and gives apps what Queuey.Client needs to use it.</summary>
public static class QueueyResourceBuilderExtensions
{
    /// <summary>
    /// Adds a self-hosted Queuey on this machine, from Queuey's image for <paramref name="version"/>. When the AppHost runs,
    /// it makes sure the local installation runs (and makes it the first time); on restore, build or publish it does
    /// nothing. The dashboard shows it with links to the console, the API and the ingress.
    /// </summary>
    /// <param name="builder">The AppHost's builder.</param>
    /// <param name="name">The resource's name, such as <c>queuey</c>.</param>
    /// <param name="version">The Queuey version, pinned, such as <c>1.0.0</c>.</param>
    /// <param name="configure">The folder, the ports or the developer's email, when the defaults are not wanted. The
    /// defaults are <c>queuey server</c>'s, so the AppHost and the CLI use the same installation.</param>
    public static IResourceBuilder<QueueyResource> AddQueuey(
        this IDistributedApplicationBuilder builder, string name, string version, Action<QueueyServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new QueueyServerOptions(version);
        configure?.Invoke(options);
        var resource = new QueueyResource(name, options);

        return builder.AddResource(resource)
            .ExcludeFromManifest()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Queuey",
                State = KnownResourceStates.NotStarted,
                Properties = ImmutableArray.Create(new ResourcePropertySnapshot("queuey.version", version)),
            })
            // Bare i run-modus: Aspire sender ikke InitializeResourceEvent ved publish, og restore og build kjører ingen AppHost.
            .OnInitializeResource((queuey, initialize, ct) =>
            {
                // Installasjonen kan ta minutter første gang (pull, migreringer, Keycloak); AppHost-en venter ikke på den,
                // men det gjør WaitFor og WithReference.
                _ = Task.Run(() => StartAsync(queuey, initialize, ct), CancellationToken.None);
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Gives the app what Queuey.Client reads: <c>QUEUEY_API_BASE</c>, <c>QUEUEY_INGRESS_BASE</c>, <c>QUEUEY_API_KEY</c>,
    /// <c>QUEUEY_TENANT</c> and <c>QUEUEY_LICENSE</c>, for the developer's workspace. A container gets the addresses as
    /// <c>host.docker.internal</c>. Use <c>WaitFor(queuey)</c> as well, so the app starts after Queuey is healthy.
    /// </summary>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder, IResourceBuilder<QueueyResource> queuey)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(queuey);
        bool fromContainer = builder.Resource is ContainerResource;
        return builder.WithEnvironment(async context =>
        {
            // Ved publish finnes ingen lokal Queuey; manifestet får ingenting herfra.
            if (context.ExecutionContext.IsPublishMode)
                return;
            QueueyServerConnection connection = await queuey.Resource.Connection.Task.WaitAsync(context.CancellationToken);
            foreach (var (key, value) in connection.ToEnvironment(fromContainer))
                context.EnvironmentVariables[key] = value;
        });
    }

    private static async Task StartAsync(QueueyResource queuey, InitializeResourceEvent initialize, CancellationToken ct)
    {
        ILogger log = initialize.Logger;
        ResourceNotificationService notifications = initialize.Notifications;
        await notifications.PublishUpdateAsync(queuey, s => s with { State = KnownResourceStates.Starting });
        try
        {
            queuey.Options.Log = line => log.LogInformation("{Line}", line);
            QueueyServerConnection connection = await QueueyServer.EnsureRunningAsync(queuey.Options, ct);
            queuey.Connection.TrySetResult(connection);
            log.LogInformation("Queuey {Version} runs in {Directory}. Sign in to {Console} as {Email}; the password is in developer.json there.",
                connection.Version, connection.Directory, connection.ConsoleUrl, connection.DeveloperEmail);
            await notifications.PublishUpdateAsync(queuey, s => s with
            {
                State = KnownResourceStates.Running,
                Urls = ImmutableArray.Create(
                    new UrlSnapshot("console", connection.ConsoleUrl.ToString(), IsInternal: false),
                    new UrlSnapshot("api", connection.ApiBase.ToString(), IsInternal: false),
                    new UrlSnapshot("ingress", connection.IngressBase.ToString(), IsInternal: false)),
                Properties = s.Properties.AddRange(new[]
                {
                    new ResourcePropertySnapshot("queuey.directory", connection.Directory),
                    new ResourcePropertySnapshot("queuey.tenant", connection.TenantPublicId),
                    new ResourcePropertySnapshot("queuey.developer", connection.DeveloperEmail),
                }),
            });
        }
        catch (Exception ex)
        {
            queuey.Connection.TrySetException(ex);
            log.LogError("Queuey did not start: {Message}", ex.Message);
            await notifications.PublishUpdateAsync(queuey, s => s with { State = KnownResourceStates.FailedToStart });
        }
    }
}
