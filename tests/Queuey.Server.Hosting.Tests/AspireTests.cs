using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Queuey.Client;
using Queuey.Server.Hosting;

namespace Queuey.Server.Hosting.Tests;

/// <summary>
/// <c>AddQueuey</c> and <c>WithReference</c> in an AppHost's model, without running it: the resource stays out of the
/// manifest, and an app gets Queuey.Client's variables, with host.docker.internal for a container and nothing at publish.
/// </summary>
public sealed class AspireTests
{
    [Fact]
    public async Task An_app_gets_queuey_clients_variables_and_a_container_gets_host_docker_internal()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true, Args = [] });
        var queuey = builder.AddQueuey("queuey", "1.0.0", o => o.ApiPort = 18080);
        var tool = builder.AddExecutable("tool", "dotnet", ".").WithReference(queuey);
        var app = builder.AddContainer("app", "busybox").WithReference(queuey);
        queuey.Resource.Connection.SetResult(new QueueyServerConnection("/x", "1.0.0", new LocalPorts(Api: 18080), "d@x",
            new AppKeyRecord("qak_k.s", "ten_1", "lic_1")));

        var onHost = await Environment(tool.Resource, DistributedApplicationOperation.Run);
        var inContainer = await Environment(app.Resource, DistributedApplicationOperation.Run);

        Assert.Equal("http://127.0.0.1:18080", onHost[QueueyEnvironmentVariables.ApiBase]);
        Assert.Equal("http://host.docker.internal:18080", inContainer[QueueyEnvironmentVariables.ApiBase]);
        Assert.Equal("qak_k.s", inContainer[QueueyEnvironmentVariables.ApiKey]);
        Assert.Equal("ten_1", onHost[QueueyEnvironmentVariables.Tenant]);
    }

    [Fact]
    public async Task Publishing_gets_nothing_from_queuey_and_the_resource_is_not_in_the_manifest()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true, Args = [] });
        var queuey = builder.AddQueuey("queuey", "1.0.0");
        var tool = builder.AddExecutable("tool", "dotnet", ".").WithReference(queuey);

        // Ingen installasjon kjører ved publish; ventet callbacken på den, ville publish hengt.
        var published = await Environment(tool.Resource, DistributedApplicationOperation.Publish).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(QueueyEnvironmentVariables.ApiBase, published.Keys);
        Assert.Contains(queuey.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>(), a => a.Callback is null);
        Assert.Equal("1.0.0", queuey.Resource.Options.Version);
    }

    private static async Task<Dictionary<string, object>> Environment(IResource resource, DistributedApplicationOperation operation)
    {
        var variables = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(operation), resource, variables);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await annotation.Callback(context);
        return variables;
    }
}
