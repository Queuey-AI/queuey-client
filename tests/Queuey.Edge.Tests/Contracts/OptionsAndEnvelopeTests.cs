using Queuey.Client;
using Queuey.Edge;

namespace Queuey.Edge.Tests.Contracts;

public class OptionsAndEnvelopeTests
{
    [Fact]
    public void Options_require_api_key_tenant_and_storage_path()
    {
        Assert.Throws<QueueyConfigurationException>(() => new QueueyEdgeOptions
        {
            TenantPublicId = "ten_x"
        }.Validate());

        Assert.Throws<QueueyConfigurationException>(() => new QueueyEdgeOptions
        {
            ApiKey = "qak_id.secret"
        }.Validate());

        var valid = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_x" };
        valid.Validate(); // default storage path is defensible
    }

    [Fact]
    public void Max_spool_bytes_must_exceed_headroom()
    {
        var o = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_x" };
        o.Storage.HeadroomBytes = 4 * 1024 * 1024;
        o.Storage.MaxSpoolBytes = o.Storage.HeadroomBytes;

        Assert.Throws<QueueyConfigurationException>(o.Validate);
    }

    [Theory]
    [InlineData(512L * 1024 * 1024, 4L * 1024 * 1024)] // the default spool keeps the fixed 4 MB
    [InlineData(16L * 1024 * 1024, 4L * 1024 * 1024)]
    [InlineData(8L * 1024 * 1024, 2L * 1024 * 1024)]
    [InlineData(1L * 1024 * 1024, 256L * 1024)]        // a 1 MB test spool still takes 768 KB of events
    public void The_default_headroom_shrinks_to_a_quarter_of_a_small_spool(long maxSpoolBytes, long headroomBytes)
    {
        var o = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_x" };
        o.Storage.MaxSpoolBytes = maxSpoolBytes;

        Assert.Equal(headroomBytes, o.Storage.HeadroomBytes);
        Assert.Equal(maxSpoolBytes - headroomBytes, o.Storage.AcceptCeilingBytes);
        o.Validate();
    }

    [Theory]
    [InlineData("gw-SN-48213")]
    [InlineData("press_line.2")]
    [InlineData("0199fc2e-7a41-7c3d-9b1e-4f2a6d8c0e11")]
    public void A_node_id_of_letters_digits_dots_underscores_and_dashes_up_to_64_is_valid(string nodeId)
    {
        var o = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_x" };
        o.Health.NodeId = nodeId;
        o.Validate();

        o.Health.NodeId = new string('a', 64);
        o.Validate();
    }

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("gw SN 48213", "it has U+0020")]
    [InlineData("gw/48213", "it has '/'")]
    [InlineData("gw%2F48213", "it has '%'")]
    [InlineData("gåteway", "it has 'å'")]
    [InlineData("..", "cannot be '.' or '..'")]
    public void An_invalid_node_id_is_refused_at_composition(string nodeId, string reason)
    {
        var o = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_x" };
        o.Health.NodeId = nodeId;

        var ex = Assert.Throws<QueueyConfigurationException>(o.Validate);
        Assert.Contains("QueueyEdgeOptions.Health.NodeId", ex.Message);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void A_node_id_longer_than_64_is_refused_with_queuey_s_limit()
    {
        var o = new QueueyEdgeOptions { ApiKey = "qak_id.secret", TenantPublicId = "ten_x" };
        o.Health.NodeId = new string('a', 65);

        var ex = Assert.Throws<QueueyConfigurationException>(o.Validate);
        Assert.Contains("is 65 characters", ex.Message);
        Assert.Contains("at most 64", ex.Message);
    }

    [Fact]
    public void QUEUEY_NODE_ID_fills_the_node_id_and_a_value_set_in_code_wins()
    {
        Func<string, string?> env = name => name == "QUEUEY_NODE_ID" ? " gw-SN-48213\n" : null;

        Assert.Equal("gw-SN-48213", new QueueyEdgeOptions().UseSettings(env).Health.NodeId);

        var inCode = new QueueyEdgeOptions();
        inCode.Health.NodeId = "from-code";
        Assert.Equal("from-code", inCode.UseEnvironmentVariables(env).Health.NodeId);

        Assert.Null(new QueueyEdgeOptions().UseSettings(name => name == "QUEUEY_NODE_ID" ? "  " : null).Health.NodeId);
    }

    [Fact]
    public void A_headroom_that_is_set_is_kept_whatever_the_limit()
    {
        var storage = new EdgeStorageOptions { HeadroomBytes = 32 * 1024, MaxSpoolBytes = 1024 * 1024 };

        Assert.Equal(32 * 1024, storage.HeadroomBytes);
    }

    [Fact]
    public void Ingress_address_resolves_from_environment_or_override()
    {
        var prod = new QueueyEdgeOptions { ApiKey = "k", TenantPublicId = "t" };
        Assert.Equal("https://ingress.queuey.ai/", prod.ResolveIngressBaseAddress().ToString());

        var local = new QueueyEdgeOptions
        {
            ApiKey = "k",
            TenantPublicId = "t",
            IngressBaseAddress = new Uri("http://localhost:5084")
        };
        Assert.Equal("http://localhost:5084/", local.ResolveIngressBaseAddress().ToString());
    }

    [Fact]
    public void Lane_is_queue_alone_or_queue_plus_group_key()
    {
        var bare = Envelope(groupKey: null);
        var grouped = Envelope(groupKey: "cust-42");

        Assert.Equal("orders", bare.Lane);
        Assert.Equal("orders\u001fcust-42", grouped.Lane);
    }

    [Fact]
    public void Lane_concatenation_cannot_collide_across_boundaries()
    {
        // ("ab", "c") and ("a", "bc") must be different lanes — the unit
        // separator guarantees it.
        var first = Envelope(queue: "ab", groupKey: "c");
        var second = Envelope(queue: "a", groupKey: "bc");

        Assert.NotEqual(first.Lane, second.Lane);
    }

    [Fact]
    public void Envelope_carries_current_version()
    {
        Assert.Equal(1, EventEnvelope.CurrentVersion);
        Assert.Equal(EventEnvelope.CurrentVersion, Envelope().Version);
    }

    private static EventEnvelope Envelope(string queue = "orders", string? groupKey = null) => new(
        Version: EventEnvelope.CurrentVersion,
        TransferId: Uuid7.NewString(DateTimeOffset.UtcNow),
        Queue: queue,
        TenantPublicId: "ten_x",
        ContentType: "application/json",
        Payload: [1, 2, 3],
        OccurredAtUtc: DateTimeOffset.UtcNow,
        GroupKey: groupKey);
}
