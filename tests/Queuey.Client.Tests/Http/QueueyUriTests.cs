using System;
using Queuey.Client;

namespace Queuey.Client.Tests;

public class QueueyUriTests
{
    [Fact]
    public void Authority_only_host_builds_root_relative_path()
    {
        Uri uri = QueueyUri.Build(new Uri("https://ingress.queuey.ai"), null, "events", "ten_abc", "orders");
        Assert.Equal("https://ingress.queuey.ai/events/ten_abc/orders", uri.ToString());
    }

    [Fact]
    public void Local_http_host_with_port_is_preserved()
    {
        Uri uri = QueueyUri.Build(new Uri("http://localhost:5084"), null, "events", "ten_abc", "orders");
        Assert.Equal("http://localhost:5084/events/ten_abc/orders", uri.ToString());
    }

    [Theory]
    [InlineData("http://localhost:8080/queuey/")]  // trailing slash
    [InlineData("http://localhost:8080/queuey")]   // no trailing slash
    public void Base_path_prefix_is_preserved(string baseAddress)
    {
        Uri uri = QueueyUri.Build(new Uri(baseAddress), null, "events", "ten_abc", "orders");
        Assert.Equal("http://localhost:8080/queuey/events/ten_abc/orders", uri.ToString());
    }

    [Fact]
    public void Query_is_appended_without_touching_the_path()
    {
        Uri uri = QueueyUri.Build(new Uri("http://localhost:5084"), "sb_fail_code=500&sb_fail_count=1",
            "events", "sandbox", "ten_abc", "orders");
        Assert.Equal("/events/sandbox/ten_abc/orders", uri.AbsolutePath);
        Assert.Equal("?sb_fail_code=500&sb_fail_count=1", uri.Query);
    }

    [Fact]
    public void Path_segments_are_url_encoded()
    {
        Uri uri = QueueyUri.Build(new Uri("https://ingress.queuey.ai"), null, "events", "ten_abc", "my orders");
        // AbsoluteUri keeps the escaping that goes on the wire (ToString() unescapes %20 for display).
        Assert.Equal("https://ingress.queuey.ai/events/ten_abc/my%20orders", uri.AbsoluteUri);
    }
}
