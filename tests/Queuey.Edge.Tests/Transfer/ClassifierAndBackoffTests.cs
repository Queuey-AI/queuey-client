using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using Queuey.Edge;

namespace Queuey.Edge.Tests.Transfer;

/// <summary>
/// The classification table (behaviour) and the backoff ladders. Behaviour
/// is the frozen part; the table below IS the contract from plan rev 4 §7.
/// </summary>
public class ClassifierAndBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly TransferOutcomeClassifier _sut = new();

    [Theory]
    [InlineData(202, TransferClass.Accepted, TransferReason.Accepted)]
    [InlineData(200, TransferClass.Accepted, TransferReason.Accepted)]
    [InlineData(204, TransferClass.Accepted, TransferReason.Accepted)]
    [InlineData(500, TransferClass.Transient, TransferReason.CloudServerError)]
    [InlineData(502, TransferClass.Transient, TransferReason.CloudServerError)]
    [InlineData(503, TransferClass.Transient, TransferReason.CloudServerError)]
    [InlineData(504, TransferClass.Transient, TransferReason.CloudServerError)]
    [InlineData(408, TransferClass.Transient, TransferReason.CloudServerError)]
    [InlineData(429, TransferClass.Throttled, TransferReason.RateLimited)]
    [InlineData(401, TransferClass.RequiresAction, TransferReason.AuthenticationRejected)]
    [InlineData(403, TransferClass.RequiresAction, TransferReason.Forbidden)]
    [InlineData(404, TransferClass.RequiresAction, TransferReason.RouteUnknown)]
    [InlineData(402, TransferClass.RequiresAction, TransferReason.BillingBlocked)]
    [InlineData(409, TransferClass.RequiresAction, TransferReason.QueuePaused)]
    [InlineData(400, TransferClass.EventRejected, TransferReason.MalformedRequest)]
    [InlineData(413, TransferClass.EventRejected, TransferReason.PayloadTooLarge)]
    [InlineData(415, TransferClass.EventRejected, TransferReason.UnsupportedContentType)]
    [InlineData(422, TransferClass.EventRejected, TransferReason.MalformedRequest)]
    [InlineData(302, TransferClass.Unknown, TransferReason.Unclassified)]
    public void Status_codes_classify_per_the_table(int status, TransferClass cls, TransferReason reason)
    {
        var outcome = _sut.ClassifyResponse(status, null, null, Now, 1);

        Assert.Equal(cls, outcome.Class);
        Assert.Equal(reason, outcome.Reason);
    }

    [Fact]
    public void Retry_after_rides_the_throttle_outcome()
    {
        var outcome = _sut.ClassifyResponse(429, null,
            new RetryConditionHeaderValue(TimeSpan.FromSeconds(17)), Now, 1);

        Assert.Equal(TimeSpan.FromSeconds(17), outcome.RetryAfter);
    }

    [Fact]
    public void A_503_with_retry_after_is_throttling_not_an_outage()
    {
        var outcome = _sut.ClassifyResponse(503, null,
            new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)), Now, 1);

        Assert.Equal(TransferClass.Throttled, outcome.Class);
        Assert.Equal(TimeSpan.FromSeconds(30), outcome.RetryAfter);
    }

    [Fact]
    public void Timeouts_are_indeterminate_and_therefore_transient()
    {
        var outcome = _sut.ClassifyException(new TaskCanceledException(), Now, 3);

        Assert.Equal(TransferClass.Transient, outcome.Class);
        Assert.Equal(TransferReason.Timeout, outcome.Reason);
    }

    [Fact]
    public void Socket_errors_keep_their_diagnosis()
    {
        var dns = _sut.ClassifyException(
            new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)), Now, 1);
        var refused = _sut.ClassifyException(
            new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)), Now, 1);

        Assert.Equal((TransferClass.Transient, TransferReason.DnsFailure), (dns.Class, dns.Reason));
        Assert.Equal((TransferClass.Transient, TransferReason.ConnectionRefused), (refused.Class, refused.Reason));
    }

    [Fact]
    public void Tls_failures_require_action_not_retries()
    {
        var outcome = _sut.ClassifyException(
            new HttpRequestException("x", new AuthenticationException("cert")), Now, 1);

        Assert.Equal(TransferClass.RequiresAction, outcome.Class);
        Assert.Equal(TransferReason.TlsFailure, outcome.Reason);
    }

    [Fact]
    public void Evidence_snippet_is_bounded_and_never_payload()
    {
        var outcome = _sut.ClassifyResponse(500, new string('x', 10_000), null, Now, 1);

        Assert.Equal(TransferEvidence.MaxSnippetLength, outcome.Evidence!.Snippet!.Length);
    }

    // ── backoff ─────────────────────────────────────────────────────────

    private static EdgeTransferOptions TransferOptions() => new()
    {
        BackoffBase = TimeSpan.FromSeconds(1),
        BackoffCap = TimeSpan.FromMinutes(5),
        RequiresActionProbeInitial = TimeSpan.FromMinutes(5),
        RequiresActionProbeCap = TimeSpan.FromHours(1)
    };

    [Fact]
    public void Transient_backoff_stays_within_base_and_cap()
    {
        var policy = new BackoffPolicy(TransferOptions());
        var transient = new TransferOutcome(TransferClass.Transient, TransferReason.Timeout, null);

        var last = TimeSpan.Zero;
        for (var i = 0; i < 50; i++)
        {
            last = policy.NextDelay(transient, last);
            Assert.InRange(last, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        }
    }

    [Fact]
    public void Retry_after_is_a_floor_with_bounded_jitter_on_top()
    {
        // Cloud's burst guard is per licence: every node of a fleet that
        // reconnects together gets the same hint. The hint is never cut
        // short (that would be ignoring it) — but nodes must not all knock
        // again on the same second.
        var policy = new BackoffPolicy(TransferOptions(), new Random(7));
        var throttled = new TransferOutcome(TransferClass.Throttled, TransferReason.RateLimited, null)
        {
            RetryAfter = TimeSpan.FromSeconds(42)
        };

        var seen = new HashSet<TimeSpan>();
        for (var i = 0; i < 100; i++)
        {
            var delay = policy.NextDelay(throttled, TimeSpan.FromSeconds(1));
            Assert.InRange(delay, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(42 * (1 + BackoffPolicy.RetryAfterJitterFraction)));
            seen.Add(delay);
        }
        Assert.True(seen.Count > 1, "the jitter must actually spread the fleet");
    }

    [Fact]
    public void Requires_action_ladder_starts_at_probe_initial_and_caps_at_an_hour()
    {
        var policy = new BackoffPolicy(TransferOptions());
        var blocked = new TransferOutcome(TransferClass.RequiresAction, TransferReason.AuthenticationRejected, null);

        var first = policy.NextDelay(blocked, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(5), first);

        var delay = first;
        for (var i = 0; i < 10; i++) delay = policy.NextDelay(blocked, delay);
        Assert.Equal(TimeSpan.FromHours(1), delay);
    }
}
