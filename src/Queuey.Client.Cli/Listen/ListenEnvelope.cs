using System;
using System.Collections.Generic;

namespace Queuey.Client.Cli;

/// <summary>CLI-side mirror of the backend listen envelope (deserialized from the SignalR "event" message).</summary>
internal sealed record ListenEnvelope(
    string? EventId,
    string? QueuePublicId,
    string? QueueName,
    string? EventType,
    string? GroupKey,
    string Method,
    string PathAndQuery,
    string? OriginalUrl,
    List<ListenHeader>? Headers,
    string? ContentType,
    string BodyBase64,
    string? CorrelationId = null,
    string? Mode = null,
    // The headers Queuey's signing set, by name. Null from a Queuey older than single-owner listening (2026-10-06).
    List<string>? SignatureHeaders = null);

internal sealed record ListenHeader(string Name, string Value);

/// <summary>Reply from the hub's legacy <c>Listen</c> call, which a Queuey older than single-owner listening has alone.</summary>
internal sealed record ListenAck(string ScopeKey, string Mode);

/// <summary>
/// Reply from the hub's <c>ListenAsOwner</c>: listening (and whether another session was taken over), or refused with a
/// code such as <c>listener_already_connected</c>, a message, and since when the scope has been held.
/// </summary>
internal sealed record ListenReply(
    bool Listening,
    string? ScopeKey = null,
    bool TookOver = false,
    string? Code = null,
    string? Message = null,
    DateTimeOffset? HeldSinceUtc = null);

/// <summary>
/// Reply from the hub's <c>Heartbeat</c>: whether this session still owns its scope. Null says nothing: from an older
/// Queuey, or on a connection that has just reconnected and not yet claimed its scope again.
/// </summary>
internal sealed record ListenBeat(bool Owner);

/// <summary>
/// What the hub tells a session listening on a workspace when another session took one of the workspace's queues over
/// (client method <c>lost</c>). The session keeps the workspace's other queues.
/// </summary>
internal sealed record ListenLost(string QueuePublicId, string Message);
