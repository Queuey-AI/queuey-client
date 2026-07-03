using System;

namespace Queuey.Client;

/// <summary>The result of a successful publish (HTTP 202 Accepted).</summary>
public sealed class PublishResult
{
    /// <summary>Public id (<c>que_…</c>) of the queue the event landed on.</summary>
    public string QueuePublicId { get; init; } = default!;

    /// <summary>Public id (<c>evt_…</c>) of the accepted event (the original event id on a replay).</summary>
    public string EventId { get; init; } = default!;

    /// <summary>Server receive time.</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>
    /// Queue mode as a string: <c>"LogOnly"</c> or <c>"Deliver"</c> on the production route,
    /// <c>"sandbox"</c> on the sandbox route.
    /// </summary>
    public string Mode { get; init; } = default!;

    /// <summary>True when this publish was deduplicated against a prior event with the same idempotency key.</summary>
    public bool Replayed { get; init; }

    /// <summary>Sandbox echo, present only for sandbox publishes.</summary>
    public SandboxPublishInfo? Sandbox { get; init; }
}

/// <summary>Sandbox echo returned on the sandbox publish route.</summary>
public sealed class SandboxPublishInfo
{
    /// <summary>Always true on the sandbox route.</summary>
    public bool Enabled { get; init; }

    /// <summary>The effective, clamped sandbox policy the server applied.</summary>
    public SandboxPolicy? Policy { get; init; }
}

/// <summary>The effective sandbox behavior policy the server resolved from the sandbox query parameters.</summary>
public sealed class SandboxPolicy
{
    /// <summary>Status code the sandbox target returns while failing.</summary>
    public int FailStatusCode { get; init; }

    /// <summary>Number of times to fail before succeeding (−1 = fail forever).</summary>
    public int? FailCount { get; init; }

    /// <summary>Error message returned while failing.</summary>
    public string? FailErrorMessage { get; init; }

    /// <summary>Status code returned on success.</summary>
    public int SuccessStatusCode { get; init; }

    /// <summary>Fixed delay in milliseconds.</summary>
    public int DelayMs { get; init; }

    /// <summary>Random jitter in milliseconds.</summary>
    public int JitterMs { get; init; }

    /// <summary>Optional response timeout in milliseconds.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Optional explicit status-code pattern applied per attempt.</summary>
    public int[]? Pattern { get; init; }

    /// <summary>Optional Retry-After (seconds) for rate-limit simulation.</summary>
    public int? RateLimitRetryAfterSec { get; init; }

    /// <summary>Sandbox run TTL in seconds.</summary>
    public int TtlSec { get; init; }
}
