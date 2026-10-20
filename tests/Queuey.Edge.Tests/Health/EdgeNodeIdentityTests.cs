using Microsoft.Extensions.DependencyInjection;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Health;

/// <summary>
/// One node, one id: the health check-in and an intake that names itself after the node (the MQTT source's client
/// id) ask the same <see cref="EdgeNodeIdentity"/>, so they cannot mint two ids however their first calls race.
/// </summary>
public class EdgeNodeIdentityTests
{
    [Fact]
    public async Task The_check_in_and_an_intake_get_the_same_minted_id_however_their_first_calls_race()
    {
        using var fx = new SpoolFixture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQueueyEdge(o =>
        {
            o.ApiKey = "qak_id.secret";
            o.TenantPublicId = "ten_test";
            o.Storage.Path = fx.Options.Path;
        });
        await using var provider = services.BuildServiceProvider();
        var reporter = provider.GetRequiredService<EdgeHealthReporter>();
        var identity = provider.GetRequiredService<EdgeNodeIdentity>();

        var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => i % 2 == 0
            ? reporter.GetNodeIdAsync(CancellationToken.None)
            : identity.GetNodeIdAsync(CancellationToken.None))));

        var spool = provider.GetRequiredService<IEventSpool>();
        Assert.Single(ids.Distinct());
        Assert.Equal(ids[0], await spool.GetMetaAsync(EdgeMetaKeys.NodeId, CancellationToken.None));
    }
}
