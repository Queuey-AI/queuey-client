using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

// F2.7 (2026-10-06): `queuey events get`. CLI-en leser et event slik REST serverer det: konvolutten til alle med
// event.read, og innholdet (payload, headerverdier, mottakerens svar) bare når nøkkelen har event.payload.read og køens
// synlighet slipper verdier ut. Hver titt på innholdet logges av Queuey før det svares, så innholdet hentes bare når
// den som kaller ber om det.

/// <summary>One event as Queuey's REST API serves it: the envelope, and the content only when asked for and allowed.</summary>
public sealed class EventRead
{
    /// <summary>The queue the event is in (<c>que_…</c>).</summary>
    public string QueuePublicId { get; init; } = default!;

    /// <summary>The event (<c>evt_…</c>).</summary>
    public string EventPublicId { get; init; } = default!;

    /// <summary>The event's status by name (<c>Delivered</c>, <c>Failed</c>, <c>Dlq</c> …), or null for a value this client does not know.</summary>
    public string? Status { get; init; }

    /// <summary>
    /// The queue's payload visibility as Queuey reports it: <c>shape</c> (values on request), <c>values</c>, or <c>shapeOnly</c>
    /// (values are never served).
    /// </summary>
    public string? PayloadVisibility { get; init; }

    /// <summary>True when this key may reveal the event's content: <c>event.payload.read</c>, and a visibility that lets values out.</summary>
    public bool CanRevealContent { get; init; }

    /// <summary>
    /// The envelope as <c>GET /events/{queue}/{event}</c> answers it: status, timings, attempts and their decisions, and
    /// the values-free shape of the payload when Queuey serves it. Never the payload, header values or the receiver's responses.
    /// </summary>
    public JsonElement Envelope { get; init; }

    /// <summary>
    /// The content as <c>GET /events/{queue}/{event}/content</c> answers it — the payload, the resolved header values and the
    /// receiver's responses — when it was asked for; null otherwise. Queuey records the look before it answers.
    /// </summary>
    public JsonElement? Content { get; init; }
}

/// <summary>Reads one event, and its content when asked for and allowed.</summary>
internal static class EventReader
{
    public static async Task<EventRead> RunAsync(
        QueueyControlPlaneClient controlPlane,
        IQueueyManagement management,
        string? tenantPublicId,
        string queue,
        string eventPublicId,
        bool revealContent,
        CancellationToken cancellationToken)
    {
        string queuePublicId = await FlowVerifier.QueuePublicIdAsync(management, tenantPublicId, queue, cancellationToken).ConfigureAwait(false);
        JsonElement envelope = await controlPlane.GetEventEnvelopeJsonAsync(queuePublicId, eventPublicId, cancellationToken).ConfigureAwait(false);

        string? visibility = String(envelope, "payloadVisibility");
        bool canReveal = Bool(envelope, "canRevealContent");

        JsonElement? content = null;
        if (revealContent)
        {
            // Queuey avgjør, og sier det i konvolutten: en titt som ikke kan gis, prøves ikke, så ingenting logges forgjeves.
            if (!canReveal)
                throw string.Equals(visibility, "shapeOnly", StringComparison.Ordinal)
                    ? new QueueyForbiddenException(
                        "The queue's payload visibility is shape only: Queuey serves its payload values to nobody.", "payload_visibility_shape_only")
                    {
                        SuggestedAction = "Read the event without its content; the envelope has its values-free shape.",
                    }
                    : new QueueyForbiddenException(
                        "This key may not read payload values: revealing an event's content needs event.payload.read on its queue.",
                        "payload_read_not_allowed")
                    {
                        SuggestedAction = "Read the event without its content, or use a key whose permissions include event.payload.read " +
                                          "(a key's own permission set; no key profile includes it). Every look is recorded.",
                    };

            content = await controlPlane.GetEventContentJsonAsync(queuePublicId, eventPublicId, cancellationToken).ConfigureAwait(false);
        }

        return new EventRead
        {
            QueuePublicId = queuePublicId,
            EventPublicId = String(envelope, "publicId") ?? eventPublicId,
            Status = StatusName(envelope),
            PayloadVisibility = visibility,
            CanRevealContent = canReveal,
            Envelope = envelope,
            Content = content,
        };
    }

    /// <summary>
    /// The status by name. Queuey sends the number (<c>EventStatus</c>), read leniently: a name works too, and a number this
    /// client does not know is null rather than an error.
    /// </summary>
    internal static string? StatusName(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("status", out JsonElement status))
            return null;

        return status.ValueKind switch
        {
            JsonValueKind.Number when status.TryGetInt32(out int n) => n switch
            {
                0 => "Received",
                1 => "InProgress",
                2 => "Delivered",
                3 => "Logged",
                4 => "Failed",
                5 => "Sandbox",
                6 => "Dlq",
                7 => "Skipped",
                8 => "Filtered",
                _ => null,
            },
            JsonValueKind.String => status.GetString(),
            _ => null,
        };
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
