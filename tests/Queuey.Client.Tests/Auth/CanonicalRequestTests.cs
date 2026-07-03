using System;
using Queuey.Client;

namespace Queuey.Client.Tests;

public class CanonicalRequestTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("?", "")]
    [InlineData("?a=1", "a=1")]
    [InlineData("?runId=run_1", "runId=run_1")]
    [InlineData("?b=2&a=1&a=0", "a=0&a=1&b=2")]          // flatten multi-value + ordinal sort
    [InlineData("?q=a+b", "q=a%20b")]                     // '+' decodes to space, re-encodes to %20
    [InlineData("?q=hello%20world&x=a%2Bb&city=%C3%A6", "city=%C3%A6&q=hello%20world&x=a%2Bb")]
    public void NormalizeQuery_matches_server(string raw, string expected)
        => Assert.Equal(expected, QueueyCanonicalRequest.NormalizeQuery(raw));

    [Fact]
    public void Build_uppercases_method_and_defaults_empty_path_to_slash()
    {
        string canonical = QueueyCanonicalRequest.Build(
            method: "post",
            requestUri: new Uri("https://ingress.queuey.ai/"),
            timestamp: "1700000000",
            nonce: "n1",
            contentSha256: "abc");

        Assert.Equal("POST\n/\n\n1700000000\nn1\nabc", canonical);
    }

    [Fact]
    public void Build_uses_path_and_normalized_query()
    {
        string canonical = QueueyCanonicalRequest.Build(
            method: "POST",
            requestUri: new Uri("https://ingress.queuey.ai/events/ten_abc/orders?b=2&a=1"),
            timestamp: "1700000000",
            nonce: "n1",
            contentSha256: "sha");

        Assert.Equal("POST\n/events/ten_abc/orders\na=1&b=2\n1700000000\nn1\nsha", canonical);
    }
}
