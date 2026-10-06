using System.Net;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Svar fra Queuey sin flytverifisering (F2.6) slik testserverne gir dem: formen POST og GET /queues/{que}/verifications
/// svarer med, med åtte trinn fra inngangen til slutt-tilstanden.
/// </summary>
internal static class FlowAnswers
{
    public const string Created = "2026-10-06T10:00:00Z";

    /// <summary>En verifisering av køen que_orders, som følger eventet i 60 sekunder fra <see cref="Created"/>.</summary>
    public static Dictionary<string, object?> Verification(
        string outcome,
        bool settled = true,
        string mode = "observed_event",
        string? eventId = "evt_1",
        string tenant = "ten_abc",
        string queue = "que_orders",
        object? expectations = null,
        string? summary = null,
        object[]? steps = null,
        int schemaVersion = 1) => new()
    {
        ["schemaVersion"] = schemaVersion,
        ["verificationId"] = "ver_1",
        ["kind"] = "flow",
        ["probeType"] = mode == "active" ? "synthetic_delivery" : "real_delivery",
        ["mode"] = mode,
        ["subject"] = new { workspacePublicId = tenant, queuePublicId = queue, eventPublicId = eventId, eventReceivedAt = eventId is null ? null : Created },
        ["expectations"] = expectations ?? new { },
        ["outcome"] = outcome,
        ["settled"] = settled,
        ["summary"] = summary ?? (outcome == "passed"
            ? $"Event {eventId} went from the ingress to Delivered: the receiver answered 200."
            : $"Verification ended {outcome}."),
        ["steps"] = steps ?? Steps(eventId ?? "evt_1"),
        ["createdAt"] = Created,
        ["observeUntil"] = "2026-10-06T10:01:00Z",
        ["settledAt"] = settled ? "2026-10-06T10:00:05Z" : null,
        ["expiresAt"] = "2026-10-07T10:00:00Z",
    };

    /// <summary>Trinnene for et event som ble levert, med evidensen Queuey gir dem.</summary>
    public static object[] Steps(string eventId = "evt_1") => new object[]
    {
        new { name = "ingress_reached", status = "passed", at = Created, evidence = new { eventId } },
        new { name = "ingress_auth", status = "skipped", at = (string?)null, evidence = new { reason = "not_recorded" } },
        new { name = "persisted", status = "passed", at = Created, evidence = new { eventId } },
        new { name = "routed", status = "passed", at = Created, evidence = new { eventTypeRecorded = true, groupKeyRecorded = false, partitionKeyRecorded = false } },
        new
        {
            name = "delivery_attempted", status = "passed", at = Created,
            evidence = new { attemptId = "att_1", attemptNumber = 1, attempts = 1, targetHost = "hooks.example.com", targetPath = "/orders", sent = true, probe = false },
        },
        new { name = "delivery_auth", status = "passed", at = Created, evidence = new { signing = "queuey", headers = new[] { "X-Queuey-Signature" } } },
        new { name = "receiver_response", status = "passed", at = Created, evidence = new { statusCode = 200, durationMs = 38 } },
        new { name = "final_state", status = "passed", at = Created, evidence = new { status = "Delivered" } },
    };

    /// <summary>Kølista til et workspace, med én kø: orders, que_orders.</summary>
    public static HttpResponseMessage Queues() => RecordingHandler.Json(HttpStatusCode.OK, new[]
    {
        new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true },
    });
}
