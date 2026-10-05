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
    public void Parses_an_aspnet_validation_problem_into_its_title_and_each_error()
    {
        // ASP.NET svarer slik før kontrolleren når kroppen ikke kan leses, for eksempel med et felt en eldre server ikke
        // kjenner: Queuey i prod svarer slik på backoff og filter. Før 2026-10-05 ble det «Bad Request» uten noe mer.
        const string body = """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,
             "errors":{"$.backoff":["The JSON property 'backoff' could not be mapped to any .NET member contained in type 'PatchTenantPolicyRequest'."],
                       "request":["The request field is required."]},
             "traceId":"00-1"}
            """;

        QueueyErrorMapper.ParseError(body, out string? code, out string? message);

        Assert.Null(code);
        Assert.Equal(
            "One or more validation errors occurred. $.backoff: The JSON property 'backoff' could not be mapped to any .NET member " +
            "contained in type 'PatchTenantPolicyRequest'. request: The request field is required.", message);
    }

    [Fact]
    public void Parses_a_problem_without_errors_into_its_detail_or_title()
    {
        QueueyErrorMapper.ParseError("""{"title":"Arm requires a step body","status":400}""", out _, out string? titled);
        QueueyErrorMapper.ParseError("""{"title":"Too large","detail":"The payload is over 1 MB.","status":413}""", out _, out string? detailed);

        Assert.Equal("Arm requires a step body", titled);
        Assert.Equal("The payload is over 1 MB.", detailed);
    }

    [Fact]
    public async Task A_validation_problem_says_what_was_wrong_instead_of_bad_request()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent(
                """{"title":"One or more validation errors occurred.","status":400,"errors":{"$.filter":["The JSON property 'filter' could not be mapped."]}}""",
                Encoding.UTF8, "application/problem+json"),
        };

        QueueyException ex = await QueueyErrorMapper.CreateAsync(response, CancellationToken.None);

        Assert.IsType<QueueyValidationException>(ex);
        Assert.Equal("One or more validation errors occurred. $.filter: The JSON property 'filter' could not be mapped.", ex.Message);
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

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task The_suggested_action_rides_along_on_every_status(int status)
    {
        // Feilkoder med foreslått handling (2026-09-23): en agent skal kunne handle på svaret uten
        // å tolke prosa.
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"filter_required\",\"message\":\"Name what to replay.\",\"action\":\"Send \\\"all\\\": true to replay every event.\"}}",
                Encoding.UTF8, "application/json"),
        };

        QueueyException ex = await QueueyErrorMapper.CreateAsync(response, CancellationToken.None);

        Assert.Equal("filter_required", ex.ErrorCode);
        Assert.Equal("Name what to replay.", ex.Message);
        Assert.Equal("Send \"all\": true to replay every event.", ex.SuggestedAction);
    }

    [Fact]
    public async Task No_action_is_null_not_empty()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"code\":\"x\",\"message\":\"y\"}}", Encoding.UTF8, "application/json"),
        };

        Assert.Null((await QueueyErrorMapper.CreateAsync(response, CancellationToken.None)).SuggestedAction);
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
