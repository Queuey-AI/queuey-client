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
    string BodyBase64);

internal sealed record ListenHeader(string Name, string Value);

/// <summary>Reply from the hub's <c>Listen</c> call.</summary>
internal sealed record ListenAck(string ScopeKey, string Mode);
