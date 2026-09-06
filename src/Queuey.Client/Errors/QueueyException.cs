using System;

namespace Queuey.Client;

/// <summary>Base type for every error surfaced by the Queuey SDK.</summary>
public class QueueyException : Exception
{
    /// <summary>HTTP status code returned by the API, when the error originated from a response.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The machine-readable error code from the API error envelope
    /// (<c>{ "error": { "code", "message" } }</c>), when present.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>Creates a <see cref="QueueyException"/>.</summary>
    public QueueyException(string message, int? statusCode = null, string? errorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

/// <summary>Thrown when the request body fails server-side validation (HTTP 400).</summary>
public sealed class QueueyValidationException : QueueyException
{
    /// <summary>Creates a <see cref="QueueyValidationException"/>.</summary>
    public QueueyValidationException(string message, string? errorCode = null)
        : base(message, 400, errorCode) { }
}

/// <summary>Thrown when credentials are missing or invalid (HTTP 401).</summary>
public sealed class QueueyAuthException : QueueyException
{
    /// <summary>Creates a <see cref="QueueyAuthException"/>.</summary>
    public QueueyAuthException(string message, string? errorCode = null)
        : base(message, 401, errorCode) { }
}

/// <summary>Thrown when the caller is authenticated but not permitted (HTTP 403).</summary>
public sealed class QueueyForbiddenException : QueueyException
{
    /// <summary>Creates a <see cref="QueueyForbiddenException"/>.</summary>
    public QueueyForbiddenException(string message, string? errorCode = null)
        : base(message, 403, errorCode) { }
}

/// <summary>Thrown when the target tenant, queue, or resource does not exist (HTTP 404).</summary>
public sealed class QueueyNotFoundException : QueueyException
{
    /// <summary>Creates a <see cref="QueueyNotFoundException"/>.</summary>
    public QueueyNotFoundException(string message, string? errorCode = null)
        : base(message, 404, errorCode) { }
}

/// <summary>Thrown on a conflicting state, such as a paused queue (HTTP 409).</summary>
public sealed class QueueyConflictException : QueueyException
{
    /// <summary>Creates a <see cref="QueueyConflictException"/>.</summary>
    public QueueyConflictException(string message, string? errorCode = null)
        : base(message, 409, errorCode) { }
}

/// <summary>Thrown when the server detects an event-distribution loop (HTTP 422).</summary>
public sealed class QueueyLoopDetectedException : QueueyException
{
    /// <summary>Creates a <see cref="QueueyLoopDetectedException"/>.</summary>
    public QueueyLoopDetectedException(string message, string? errorCode = null)
        : base(message, 422, errorCode) { }
}

/// <summary>
/// Thrown for client-side misconfiguration before any request is made — for example,
/// invoking a management/partner/SyncStreams operation without a
/// <see cref="QueueyOptions.ManagementToken"/>.
/// </summary>
public sealed class QueueyConfigurationException : QueueyException
{
    /// <summary>Creates a <see cref="QueueyConfigurationException"/>.</summary>
    public QueueyConfigurationException(string message)
        : base(message) { }
}
