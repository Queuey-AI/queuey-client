using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Tests;

public class ApiKeyAuthenticatorTests
{
    [Fact]
    public async Task Sets_X_Api_Key_header_verbatim()
    {
        var auth = new ApiKeyAuthenticator("qak_kid.secret");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://ingress.queuey.ai/events/ten_a/orders")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };

        await auth.AuthenticateAsync(request, Array.Empty<byte>());

        Assert.Equal("qak_kid.secret", request.Headers.GetValues(QueueyHeaders.ApiKey).Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_missing_key(string? key)
        => Assert.Throws<ArgumentException>(() => new ApiKeyAuthenticator(key!));
}
