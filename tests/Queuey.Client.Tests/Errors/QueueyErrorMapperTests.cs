using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Tests;

public class QueueyErrorMapperTests
{
    [Fact]
    public void Parses_nested_envelope()
    {
        QueueyErrorMapper.ParseError("{\"error\":{\"code\":\"queue_paused\",\"message\":\"Queue is paused.\"}}",
            out string? code, out string? message);
        Assert.Equal("queue_paused", code);
        Assert.Equal("Queue is paused.", message);
    }

    [Fact]
    public void Parses_flat_string_error()
    {
        QueueyErrorMapper.ParseError("{\"error\":\"ip_not_allowed\",\"message\":\"Source IP is not permitted.\"}",
            out string? code, out string? message);
        Assert.Equal("ip_not_allowed", code);
        Assert.Equal("Source IP is not permitted.", message);
    }

    [Fact]
    public void Parses_exception_shape()
    {
        QueueyErrorMapper.ParseError("{\"StatusCode\":500,\"Message\":\"boom\"}", out string? code, out string? message);
        Assert.Null(code);
        Assert.Equal("boom", message);
    }

    [Fact]
    public void Parses_plain_text_body()
    {
        QueueyErrorMapper.ParseError("Missing required header: X-License-PublicId", out string? code, out string? message);
        Assert.Null(code);
        Assert.Equal("Missing required header: X-License-PublicId", message);
    }

    [Fact]
    public void Handles_empty_body()
    {
        QueueyErrorMapper.ParseError("", out string? code, out string? message);
        Assert.Null(code);
        Assert.Null(message);
    }

    [Theory]
    [InlineData(400, typeof(QueueyValidationException))]
    [InlineData(401, typeof(QueueyAuthException))]
    [InlineData(403, typeof(QueueyForbiddenException))]
    [InlineData(404, typeof(QueueyNotFoundException))]
    [InlineData(409, typeof(QueueyConflictException))]
    [InlineData(422, typeof(QueueyLoopDetectedException))]
    [InlineData(500, typeof(QueueyException))]
    public async Task Maps_status_to_typed_exception(int status, System.Type expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{\"error\":{\"code\":\"x_code\",\"message\":\"msg\"}}", Encoding.UTF8, "application/json"),
        };

        QueueyException ex = await QueueyErrorMapper.CreateAsync(response, CancellationToken.None);

        Assert.IsType(expected, ex);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal("x_code", ex.ErrorCode);
        Assert.Equal("msg", ex.Message);
    }

    [Fact]
    public async Task Empty_404_body_still_maps_to_not_found()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        QueueyException ex = await QueueyErrorMapper.CreateAsync(response, CancellationToken.None);
        Assert.IsType<QueueyNotFoundException>(ex);
        Assert.Equal(404, ex.StatusCode);
    }
}
