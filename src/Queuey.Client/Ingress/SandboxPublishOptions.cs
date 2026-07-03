using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Queuey.Client;

/// <summary>
/// Options for <see cref="IQueueyIngress.PublishSandboxAsync{T}"/>. In addition to the normal publish
/// fields, these map to the <c>sb_*</c> sandbox query parameters that drive simulated delivery behavior.
/// </summary>
public sealed class SandboxPublishOptions
{
    /// <summary><c>sb_fail_code</c> — status code returned while failing (server default 500, clamped 100–599).</summary>
    public int? FailStatusCode { get; set; }

    /// <summary><c>sb_fail_count</c> — number of failures before success (−1 = fail forever).</summary>
    public int? FailCount { get; set; }

    /// <summary><c>sb_fail_msg</c> — error message returned while failing.</summary>
    public string? FailMessage { get; set; }

    /// <summary><c>sb_success_code</c> — status code returned on success.</summary>
    public int? SuccessStatusCode { get; set; }

    /// <summary><c>sb_delay_ms</c> — fixed delay in milliseconds.</summary>
    public int? DelayMs { get; set; }

    /// <summary><c>sb_jitter_ms</c> — random jitter in milliseconds.</summary>
    public int? JitterMs { get; set; }

    /// <summary><c>sb_timeout_ms</c> — response timeout in milliseconds.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary><c>sb_pattern</c> — explicit per-attempt status-code pattern.</summary>
    public int[]? Pattern { get; set; }

    /// <summary><c>sb_retry_after_sec</c> — Retry-After seconds for rate-limit simulation.</summary>
    public int? RetryAfterSec { get; set; }

    /// <summary><c>sb_ttl_sec</c> — sandbox run TTL in seconds.</summary>
    public int? TtlSec { get; set; }

    /// <summary><c>runId</c> — associates the publish with a sandbox run (ignored if unknown).</summary>
    public string? RunId { get; set; }

    /// <summary>Event type, sent as <c>X-Queuey-Event-Type</c>.</summary>
    public string? EventType { get; set; }

    /// <summary>Group / partition key, sent as <c>X-Queuey-Group-Key</c>.</summary>
    public string? GroupKey { get; set; }

    /// <summary>Idempotency key, sent as <c>Idempotency-Key</c>.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Trace source, sent as <c>X-Queuey-Source</c>.</summary>
    public string? Source { get; set; }

    /// <summary>Overrides the request content type (defaults to <c>application/json</c> for typed payloads).</summary>
    public string? ContentType { get; set; }

    /// <summary>Builds the sandbox query string (without a leading <c>?</c>); empty when nothing is set.</summary>
    internal string BuildQuery()
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                parts.Add($"{key}={Uri.EscapeDataString(value!)}");
        }

        string? Int(int? v) => v?.ToString(CultureInfo.InvariantCulture);

        Add("sb_fail_code", Int(FailStatusCode));
        Add("sb_fail_count", Int(FailCount));
        Add("sb_fail_msg", FailMessage);
        Add("sb_success_code", Int(SuccessStatusCode));
        Add("sb_delay_ms", Int(DelayMs));
        Add("sb_jitter_ms", Int(JitterMs));
        Add("sb_timeout_ms", Int(TimeoutMs));
        if (Pattern is { Length: > 0 })
            Add("sb_pattern", string.Join(",", Pattern.Select(p => p.ToString(CultureInfo.InvariantCulture))));
        Add("sb_retry_after_sec", Int(RetryAfterSec));
        Add("sb_ttl_sec", Int(TtlSec));
        Add("runId", RunId);

        return string.Join("&", parts);
    }
}
