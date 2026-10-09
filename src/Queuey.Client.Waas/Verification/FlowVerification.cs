using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

// Flytverifisering fra klienten (Queuey F2.6, 2026-10-06): Queuey verifiserer, og klienten starter og leser. Før publiserte
// verify selv og fulgte eventen med GET /events. Nå er det POST /queues/{que}/verifications og GET til den er avgjort, så
// CLI-en, MCP-verktøyene og konsollen svarer med de samme trinnene. Ingen stille fallback til den gamle veien: en Queuey uten
// endepunktet gir en feil som sier hva som mangler.

/// <summary>
/// What <see cref="IQueueyService.VerifyFlowAsync"/> asks Queuey to verify. Made by one of three factories, so a request is
/// always exactly one of them: <see cref="FollowEvent"/> follows an event the producer published, <see cref="WaitForEvent"/>
/// waits for the next event of a type the ingress takes, and <see cref="SendTestEvent"/> has Queuey send a test event through
/// the queue's ingress.
/// </summary>
// Fabrikker i stedet for settere (review av queuey-client#51, 2026-10-06): feltene utelukker hverandre, og en kombinasjon
// Queuey nekter, skal ikke kunne bygges. Strammet inn før første tag, fordi det etter en tag ville brutt kallere.
public sealed class FlowVerificationRequest
{
    private FlowVerificationRequest() { }

    /// <summary>The event <see cref="FollowEvent"/> follows (<c>evt_…</c>); null for the other two.</summary>
    public string? EventPublicId { get; private init; }

    /// <summary>
    /// The event type <see cref="WaitForEvent"/> waits for, compared exactly with what the queue records, or the one
    /// <see cref="SendTestEvent"/> sends the test event with.
    /// </summary>
    public string? EventType { get; private init; }

    /// <summary>The signed-request template the ingress must have verified the event with (<c>stripe</c>, …), for <see cref="WaitForEvent"/>.</summary>
    public string? IngressAuth { get; private init; }

    /// <summary>True for <see cref="SendTestEvent"/>.</summary>
    public bool Send { get; private init; }

    /// <summary>How long Queuey follows the event, in whole seconds, at most fifteen minutes. Queuey's default, a minute, when null.</summary>
    public TimeSpan? Timeout { get; private init; }

    // Testeventen, lest og sjekket av SendTestEvent, slik den sendes.
    internal JsonElement? Payload { get; private init; }

    /// <summary>Follows <paramref name="eventPublicId"/>, an event already in the queue, such as one the producer published.</summary>
    /// <param name="eventPublicId">The event's public id (<c>evt_…</c>), as the ingress answered it.</param>
    /// <param name="timeout">How long Queuey follows the event: at least a second; Queuey's default when null.</param>
    /// <exception cref="ArgumentException"><paramref name="eventPublicId"/> is not an event's public id.</exception>
    public static FlowVerificationRequest FollowEvent(string eventPublicId, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(eventPublicId) || !eventPublicId.StartsWith("evt_", StringComparison.Ordinal))
            throw new ArgumentException("An event's public id starts with evt_, as the ingress answered it.", nameof(eventPublicId));

        return new FlowVerificationRequest { EventPublicId = eventPublicId, Timeout = Checked(timeout) };
    }

    /// <summary>
    /// Waits for the next event the queue's ingress takes with <paramref name="eventType"/> from the start on, and follows it.
    /// Trigger the event once the verification has started: an event that arrived before does not count.
    /// </summary>
    /// <param name="eventType">The event type, compared exactly with what the queue records.</param>
    /// <param name="ingressAuth">
    /// The signed-request template the ingress must have verified the event with, such as <c>stripe</c>, so a provider's flow is
    /// proven with its own event and signature. Null to match on the event type alone.
    /// </param>
    /// <param name="timeout">How long Queuey waits and follows: at least a second; Queuey's default when null.</param>
    public static FlowVerificationRequest WaitForEvent(string eventType, string? ingressAuth = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("An event type is required: without one, the first event that arrived would count, from anyone.", nameof(eventType));
        if (ingressAuth is not null && string.IsNullOrWhiteSpace(ingressAuth))
            throw new ArgumentException("ingressAuth is a signed-request template, such as stripe, or null.", nameof(ingressAuth));

        return new FlowVerificationRequest { EventType = eventType, IngressAuth = ingressAuth, Timeout = Checked(timeout) };
    }

    /// <summary>
    /// Has Queuey send <paramref name="payload"/> as a test event through the queue's ingress, and follow it. It reaches the
    /// real receiver like any other event, so send data it treats as harmless.
    /// </summary>
    /// <remarks>
    /// Queuey sends it only where all three hold: active verification is switched on in that Queuey, the key has
    /// <c>event.publish</c> on the queue in a workspace tagged dev, test or staging (no tag counts as production), and the
    /// queue's ingress takes events without a key or a signature. Otherwise Queuey refuses with
    /// <c>active_verification_disabled</c> or <c>production_workspace</c>, or answers <c>not_tried</c>. Verify the producer's
    /// own event there instead, with <see cref="FollowEvent"/> or <see cref="WaitForEvent"/>.
    /// </remarks>
    /// <param name="payload">The test event's body: one JSON value as UTF-8, at most <see cref="FlowVerifications.MaxTestEventBytes"/> as sent.</param>
    /// <param name="eventType">The event type to send it with, where the queue's ingress reads it; null to send none.</param>
    /// <param name="timeout">How long Queuey follows the event: at least a second; Queuey's default when null.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="payload"/> is not JSON, or larger than Queuey takes. The message never shows a part of it.
    /// </exception>
    public static FlowVerificationRequest SendTestEvent(byte[] payload, string? eventType = null, TimeSpan? timeout = null)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (eventType is not null && string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("eventType is the event type to send the test event with, or null.", nameof(eventType));

        if (!FlowVerifier.TryReadJson(payload, out JsonElement value, out string? where))
            throw new ArgumentException(
                $"The test event is not JSON ({where}). Queuey sends it through the queue's ingress as application/json, so it is one JSON value.");

        // Størrelsen slik Queuey får den: serialisert som forespørselen sender den, uten mellomrom og med escaping. Det er
        // det Queuey måler (64 KB). Over 128 KB svarer Kestrel 413 uten kropp, så grensen sjekkes her, med en melding.
        int size = JsonSerializer.SerializeToUtf8Bytes(value, QueueyJson.Options).Length;
        if (size > FlowVerifications.MaxTestEventBytes)
            throw new ArgumentException(
                $"The test event is {size.ToString("N0", CultureInfo.InvariantCulture)} bytes as JSON, and Queuey takes at most " +
                $"{FlowVerifications.MaxTestEventBytes.ToString("N0", CultureInfo.InvariantCulture)} (64 KB).");

        return new FlowVerificationRequest { Send = true, Payload = value, EventType = eventType, Timeout = Checked(timeout) };
    }

    private static TimeSpan? Checked(TimeSpan? timeout)
        => timeout is { } t && t < TimeSpan.FromSeconds(1)
            ? throw new ArgumentOutOfRangeException(nameof(timeout), "Queuey follows an event for at least a second.")
            : timeout;
}

/// <summary>
/// A flow verification as Queuey answers it: what it follows, its steps from the ingress to the final state, and what they
/// add up to. Its words (<see cref="Mode"/>, <see cref="Outcome"/>, a step's name and status) are text Queuey adds to and
/// never renames: show a value you do not know as it is.
/// </summary>
public sealed class FlowVerification
{
    /// <summary>
    /// The version of this shape. Queuey raises it only for a change that breaks a reader; a new field or a new value comes
    /// without a new version. This client reads version <see cref="FlowVerifications.SchemaVersion"/>, and refuses another
    /// rather than read it wrong.
    /// </summary>
    public int SchemaVersion { get; init; }

    /// <summary>The verification's public id (<c>ver_…</c>).</summary>
    public string VerificationId { get; init; } = string.Empty;

    /// <summary><c>flow</c>.</summary>
    public string? Kind { get; init; }

    /// <summary><c>real_delivery</c> for an event the producer sent, <c>synthetic_delivery</c> for a test event Queuey made.</summary>
    public string? ProbeType { get; init; }

    /// <summary>One of <see cref="FlowVerificationModes"/>.</summary>
    public string? Mode { get; init; }

    /// <summary>The workspace, the queue and the event the verification follows.</summary>
    public FlowVerificationSubject Subject { get; init; } = new();

    /// <summary>What a session waits for, or what active mode sent its test event with. Empty for an observed event.</summary>
    public FlowVerificationExpectations Expectations { get; init; } = new();

    /// <summary>One of <see cref="FlowVerificationOutcomes"/>.</summary>
    public string? Outcome { get; init; }

    /// <summary>True when the result is final: the steps do not change any more.</summary>
    public bool Settled { get; init; }

    /// <summary>The result in one sentence, and for <c>not_tried</c> what to do instead.</summary>
    public string? Summary { get; init; }

    /// <summary>The steps in order, from <c>ingress_reached</c> to <c>final_state</c>.</summary>
    public IReadOnlyList<FlowVerificationStep> Steps { get; init; } = Array.Empty<FlowVerificationStep>();

    /// <summary>When the verification started.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When Queuey stops following the event.</summary>
    public DateTimeOffset ObserveUntil { get; init; }

    /// <summary>When it was settled; null while Queuey follows the event.</summary>
    public DateTimeOffset? SettledAt { get; init; }

    /// <summary>When it can be relied on no longer.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>True when the verification is settled and passed: the event was delivered by its flow.</summary>
    [JsonIgnore]
    public bool Passed => Settled && string.Equals(Outcome, FlowVerificationOutcomes.Passed, StringComparison.Ordinal);
}

/// <summary>What a flow verification is about.</summary>
public sealed class FlowVerificationSubject
{
    /// <summary>The workspace (<c>ten_…</c>).</summary>
    public string? WorkspacePublicId { get; init; }

    /// <summary>The queue (<c>que_…</c>).</summary>
    public string? QueuePublicId { get; init; }

    /// <summary>The event it follows (<c>evt_…</c>); null while a session has matched none, and when active mode sent nothing the queue took.</summary>
    public string? EventPublicId { get; init; }

    /// <summary>When the event arrived; null while there is no event.</summary>
    public DateTimeOffset? EventReceivedAt { get; init; }
}

/// <summary>What a session waits for, or what active mode sent its test event with.</summary>
public sealed class FlowVerificationExpectations
{
    /// <summary>The event type a session waits for, or the one active mode sent the test event with.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventType { get; init; }

    /// <summary>The template a session waits for the ingress to have verified the event with (<c>stripe</c>, …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IngressAuth { get; init; }

    /// <summary>What active mode sent the test event through the ingress with: <c>none</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SentWith { get; init; }
}

/// <summary>One step of a flow: its name, its status, when it happened, and the evidence for it.</summary>
public sealed class FlowVerificationStep
{
    /// <summary>
    /// <c>ingress_reached</c>, <c>ingress_auth</c>, <c>persisted</c>, <c>routed</c>, <c>delivery_attempted</c>,
    /// <c>delivery_auth</c>, <c>receiver_response</c> or <c>final_state</c>.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>One of <see cref="FlowStepStatuses"/>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>When the step happened; null for a step that is pending, skipped, or has no moment of its own.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>What shows it.</summary>
    public FlowStepEvidence Evidence { get; init; } = new();
}

/// <summary>
/// The evidence for a step, as Queuey gives it: public ids, statuses, times, header names and failure classes, never a
/// payload value, a secret, a header value or a receiver's response body. Each field is optional. This client reads the
/// fields below and nothing else, so it never shows a field it does not know.
/// </summary>
public sealed class FlowStepEvidence
{
    /// <summary>Why a step failed, was skipped or is pending, as a stable id (<c>not_recorded</c>, <c>event_replayed</c>, …), or Queuey's decision reason.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>The event's public id (<c>evt_…</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventId { get; init; }

    /// <summary>The signed-request template the ingress checked the event's signature with (<c>stripe</c>, …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scheme { get; init; }

    /// <summary>The public id of the stored credential whose secret verified the event at the ingress.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CredentialId { get; init; }

    /// <summary>Whether the event has an event type recorded. The type itself is not given.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EventTypeRecorded { get; init; }

    /// <summary>Whether the event has a customer key recorded. The key itself is not given.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? GroupKeyRecorded { get; init; }

    /// <summary>Whether the event has an ordering key recorded. The key itself is not given.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PartitionKeyRecorded { get; init; }

    /// <summary>The attempt's public id (<c>att_…</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttemptId { get; init; }

    /// <summary>The attempt's number among the event's attempts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AttemptNumber { get; init; }

    /// <summary>How many delivery attempts the event has had, not counting attempts Queuey held before sending.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempts { get; init; }

    /// <summary>The delivery target's id (<c>tgt_…</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetId { get; init; }

    /// <summary>The receiver's host, and its port when not the default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetHost { get; init; }

    /// <summary>The path Queuey delivered to, with every part that may carry a secret shown as <c>…</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetPath { get; init; }

    /// <summary>Whether the request left Queuey.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Sent { get; init; }

    /// <summary>Whether the attempt was a probe, not the worker's ordinary delivery.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Probe { get; init; }

    /// <summary>The auth scheme Queuey put on the request: <c>bearer</c>, <c>api_key</c>, <c>basic</c> or <c>oauth2</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Auth { get; init; }

    /// <summary>The template Queuey signed the request with (<c>queuey</c>, <c>stripe</c>, …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signing { get; init; }

    /// <summary>True when Queuey recalculated the provider's signature for this delivery.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Resigned { get; init; }

    /// <summary>The names of the headers the auth and the signature travel in. Never their values.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Headers { get; init; }

    /// <summary>The receiver's HTTP status code.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusCode { get; init; }

    /// <summary>How long the attempt took, in milliseconds.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; init; }

    /// <summary>Queuey's class of the failure (<c>AuthenticationFailed</c>, <c>TargetUnavailable</c>, …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureClass { get; init; }

    /// <summary>What Queuey decided after the attempt (<c>RetryLater</c>, <c>MoveToDlq</c>, <c>HoldQueue</c>, …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Decision { get; init; }

    /// <summary>The event's status in Queuey (<c>Delivered</c>, <c>Failed</c>, <c>Dlq</c>, …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    /// <summary>When Queuey tries the event next, while it waits for a retry.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? NextRetryAt { get; init; }

    /// <summary>Why the event's lane is held, while it is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HoldReason { get; init; }

    /// <summary>Queuey's reason for holding the event's latest delivery before sending it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeldBy { get; init; }

    /// <summary>For a fan-out delivery, how many targets got the event.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Targets { get; init; }
}

/// <summary>The modes of a flow verification. Queuey adds modes and never renames them.</summary>
public static class FlowVerificationModes
{
    /// <summary>It follows an event the caller named.</summary>
    public const string ObservedEvent = "observed_event";

    /// <summary>It matched the first event after its start that met the expectations.</summary>
    public const string ObservedSession = "observed_session";

    /// <summary>Queuey made a test event and sent it through the queue's ingress.</summary>
    public const string Active = "active";
}

/// <summary>The outcomes of a flow verification. Queuey adds outcomes and never renames them.</summary>
public static class FlowVerificationOutcomes
{
    /// <summary>Still following.</summary>
    public const string Pending = "pending";

    /// <summary>Delivered, and every step passed or did not apply.</summary>
    public const string Passed = "passed";

    /// <summary>A step failed.</summary>
    public const string Failed = "failed";

    /// <summary>No final state, or no matching event, before Queuey stopped following.</summary>
    public const string TimedOut = "timed_out";

    /// <summary>Active mode could not try the flow; the summary says what to do instead.</summary>
    public const string NotTried = "not_tried";
}

/// <summary>The statuses of a step. Queuey adds statuses and never renames them.</summary>
public static class FlowStepStatuses
{
    /// <summary>The step happened as it should.</summary>
    public const string Passed = "passed";

    /// <summary>The step went wrong; the evidence says how.</summary>
    public const string Failed = "failed";

    /// <summary>The step does not apply, or Queuey has no record of it.</summary>
    public const string Skipped = "skipped";

    /// <summary>The step has not happened yet.</summary>
    public const string Pending = "pending";
}

/// <summary>Constants for flow verifications.</summary>
public static class FlowVerifications
{
    /// <summary>
    /// The version of <see cref="FlowVerification"/>'s shape this client reads. Queuey's contract: the version goes up only
    /// for a change that breaks a reader of the shape, so another version is one this client could read wrong.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>The largest test event Queuey takes: 64 KB of JSON, counted as it is sent.</summary>
    public const int MaxTestEventBytes = 64 * 1024;

    /// <summary>The error code for a Queuey that has no flow verification.</summary>
    public const string UnavailableCode = "flow_verification_unavailable";

    /// <summary>The error code for an answer in a shape this client does not read.</summary>
    public const string UnsupportedSchemaCode = "verification_schema_unsupported";
}

// ── wire ──────────────────────────────────────────────────────────────────────

/// <summary>The body of <c>POST /queues/{que}/verifications</c>. Null fields are left out; Queuey refuses a field it does not know.</summary>
internal sealed class FlowVerificationWireRequest
{
    public string? EventPublicId { get; set; }
    public int? TimeoutSeconds { get; set; }
    public string? EventType { get; set; }
    public string? IngressAuth { get; set; }
    public bool? Send { get; set; }
    public JsonElement? Payload { get; set; }
}

/// <summary>
/// Starts a flow verification and reads it until Queuey has settled it. Queuey does the verifying; this only asks and waits.
/// </summary>
internal static class FlowVerifier
{
    /// <summary>How long each read asks Queuey to wait for the verification to settle: the most it waits.</summary>
    internal const int WaitSecondsPerRead = 20;

    // Klientens egen frist, bare mot en server som aldri avgjør: tiden Queuey følger eventen (høyst 15 minutter når
    // forespørselen ikke sier noe), og to minutter til. Queuey avgjør verifiseringen selv når tiden er ute.
    private static readonly TimeSpan LongestFollow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    // En server som svarer straks i stedet for å vente, leses ikke oftere enn én gang i sekundet.
    private static readonly TimeSpan MinReadInterval = TimeSpan.FromSeconds(1);

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    public static async Task<FlowVerification> RunAsync(
        QueueyControlPlaneClient controlPlane,
        IQueueyManagement management,
        string? tenantPublicId,
        string queue,
        FlowVerificationRequest request,
        IProgress<FlowVerification>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        (string queuePublicId, FlowVerification verification) =
            await StartAndQueueAsync(controlPlane, management, tenantPublicId, queue, request, cancellationToken).ConfigureAwait(false);
        progress?.Report(verification);

        return await FollowAsync(controlPlane, queuePublicId, verification, (request.Timeout ?? LongestFollow) + Grace - clock.Elapsed,
            progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the verification and returns it as Queuey answered the start, without reading it again: Queuey follows the event
    /// on its own until its time is up, and <see cref="ResumeAsync"/> reads it later by its id.
    /// </summary>
    // Gullflyten 2026-10-09: `queuey verify --background` gir id-en med en gang, så agenten kan trigge eventen uten å gjette.
    public static async Task<FlowVerification> StartAsync(
        QueueyControlPlaneClient controlPlane,
        IQueueyManagement management,
        string? tenantPublicId,
        string queue,
        FlowVerificationRequest request,
        CancellationToken cancellationToken)
        => (await StartAndQueueAsync(controlPlane, management, tenantPublicId, queue, request, cancellationToken).ConfigureAwait(false)).Verification;

    private static async Task<(string QueuePublicId, FlowVerification Verification)> StartAndQueueAsync(
        QueueyControlPlaneClient controlPlane,
        IQueueyManagement management,
        string? tenantPublicId,
        string queue,
        FlowVerificationRequest request,
        CancellationToken cancellationToken)
    {
        FlowVerificationWireRequest wire = ToWire(request);
        string queuePublicId = await QueuePublicIdAsync(management, tenantPublicId, queue, cancellationToken).ConfigureAwait(false);
        return (queuePublicId, await StartAsync(controlPlane, queuePublicId, wire, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Reads a verification that was started earlier (<c>ver_…</c>) until Queuey has settled it. Queuey keeps it under its
    /// queue, so the queue is needed too.
    /// </summary>
    public static async Task<FlowVerification> ResumeAsync(
        QueueyControlPlaneClient controlPlane,
        IQueueyManagement management,
        string? tenantPublicId,
        string queue,
        string verificationId,
        IProgress<FlowVerification>? progress,
        CancellationToken cancellationToken)
    {
        string queuePublicId = await QueuePublicIdAsync(management, tenantPublicId, queue, cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        FlowVerification verification = await ReadAsync(controlPlane, queuePublicId, verificationId, cancellationToken).ConfigureAwait(false);
        progress?.Report(verification);

        return await FollowAsync(controlPlane, queuePublicId, verification, LongestFollow + Grace - clock.Elapsed, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<FlowVerification> FollowAsync(
        QueueyControlPlaneClient controlPlane,
        string queuePublicId,
        FlowVerification verification,
        TimeSpan giveUpAfter,
        IProgress<FlowVerification>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (!verification.Settled && clock.Elapsed < giveUpAfter)
        {
            var read = Stopwatch.StartNew();
            verification = await ReadAsync(controlPlane, queuePublicId, verification.VerificationId, cancellationToken).ConfigureAwait(false);
            progress?.Report(verification);

            if (!verification.Settled && read.Elapsed < MinReadInterval)
                await Task.Delay(MinReadInterval - read.Elapsed, cancellationToken).ConfigureAwait(false);
        }

        return verification;
    }

    private static async Task<FlowVerification> ReadAsync(
        QueueyControlPlaneClient controlPlane, string queuePublicId, string verificationId, CancellationToken cancellationToken)
    {
        string call = $"GET /queues/{queuePublicId}/verifications/{verificationId}";
        try
        {
            return Checked(
                await controlPlane.GetVerificationAsync(queuePublicId, verificationId, WaitSecondsPerRead, cancellationToken).ConfigureAwait(false),
                call);
        }
        catch (JsonException)
        {
            throw NotAVerification(call);
        }
    }

    /// <summary>The request as Queuey takes it. The factories of <see cref="FlowVerificationRequest"/> have checked it.</summary>
    internal static FlowVerificationWireRequest ToWire(FlowVerificationRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        return new FlowVerificationWireRequest
        {
            EventPublicId = request.EventPublicId,
            EventType = request.EventType,
            IngressAuth = request.IngressAuth,
            Send = request.Send ? true : null,
            Payload = request.Payload,
            TimeoutSeconds = request.Timeout is { } timeout ? (int)Math.Ceiling(timeout.TotalSeconds) : null,
        };
    }

    /// <summary>
    /// Reads one JSON value from UTF-8 bytes, after a byte order mark when there is one. When they are not JSON,
    /// <paramref name="where"/> says where it went wrong, by line and byte: never a part of the bytes.
    /// </summary>
    internal static bool TryReadJson(byte[] bytes, out JsonElement value, out string? where)
    {
        int start = bytes.Length >= 3 && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2] ? 3 : 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, start, bytes.Length - start));
            value = doc.RootElement.Clone();
            where = null;
            return true;
        }
        catch (JsonException ex)
        {
            value = default;
            where = $"line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1}";
            return false;
        }
    }

    /// <summary>
    /// The queue's public id: <paramref name="queue"/> itself when it is one (<c>que_…</c>), else the queue of that name in
    /// the workspace.
    /// </summary>
    internal static async Task<string> QueuePublicIdAsync(
        IQueueyManagement management, string? tenantPublicId, string queue, CancellationToken cancellationToken)
    {
        if (queue.StartsWith("que_", StringComparison.Ordinal))
            return queue;

        if (string.IsNullOrWhiteSpace(tenantPublicId))
            throw new QueueyConfigurationException("Finding a queue by its name needs the workspace (ten_…) it is in, and none is set.")
            {
                SuggestedAction = "Set QueueyOptions.TenantPublicId (in the CLI: --tenant, QUEUEY_TENANT, or tenant in queuey.json), " +
                                  "or give the queue's id (que_…) instead of its name.",
            };

        foreach (QueueListItem row in await management.ListQueuesAsync(tenantPublicId!, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(row.DisplayName, queue, StringComparison.Ordinal) && !string.IsNullOrEmpty(row.PublicId))
                return row.PublicId!;
        }

        // Navnet vises ikke: det er det brukeren skrev, og det kan være en hemmelighet limt inn på feil plass.
        throw new QueueyNotFoundException($"Workspace {tenantPublicId} has no queue by that name.", "queue_not_found")
        {
            SuggestedAction = "Give the queue's name exactly as the deployment file declares it, or its id (que_…).",
        };
    }

    private static async Task<FlowVerification> StartAsync(
        QueueyControlPlaneClient controlPlane, string queuePublicId, FlowVerificationWireRequest wire, CancellationToken cancellationToken)
    {
        string call = $"POST /queues/{queuePublicId}/verifications";
        try
        {
            return Checked(await controlPlane.StartVerificationAsync(queuePublicId, wire, cancellationToken).ConfigureAwait(false), call);
        }
        catch (QueueyException ex) when (ex.StatusCode == 405 || (ex.StatusCode == 404 && string.IsNullOrEmpty(ex.ErrorCode)))
        {
            // 404 uten kode kommer både fra en Queuey uten endepunktet og fra en kø som ikke finnes: mellomvaren som slår opp
            // køen i ruten, svarer uten kropp. En lesing av køen skiller dem. Den nye Queuey-en svarer ellers med en kode.
            bool? exists = ex.StatusCode == 404 ? await QueueExistsAsync(controlPlane, queuePublicId, cancellationToken).ConfigureAwait(false) : true;
            if (exists == false)
                throw new QueueyNotFoundException($"Queue {queuePublicId} was not found.", "queue_not_found")
                {
                    SuggestedAction = "Check the queue's id, and that the key belongs to the queue's workspace.",
                };

            throw Unavailable(queuePublicId, ex.StatusCode!.Value, queueUnknown: exists is null);
        }
        catch (QueueyException ex) when (ex.StatusCode is >= 200 and < 300)
        {
            // Et 2xx uten kropp.
            throw NotAVerification(call);
        }
        catch (JsonException)
        {
            throw NotAVerification(call);
        }
    }

    private static async Task<bool?> QueueExistsAsync(QueueyControlPlaneClient controlPlane, string queuePublicId, CancellationToken cancellationToken)
    {
        try
        {
            await controlPlane.GetQueueStoredAsync(queuePublicId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (QueueyNotFoundException)
        {
            return false;
        }
        catch (QueueyException)
        {
            return null;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>The verification, when it is one in the shape this client reads; otherwise an error that says what it got.</summary>
    private static FlowVerification Checked(FlowVerification? verification, string call)
    {
        if (verification is null
            || verification.SchemaVersion <= 0
            || string.IsNullOrEmpty(verification.VerificationId)
            || !verification.VerificationId.StartsWith("ver_", StringComparison.Ordinal)
            || verification.Subject is null
            || verification.Expectations is null
            || verification.Steps is null)
            throw NotAVerification(call);

        // Kontrakten er at Queuey øker schemaVersion bare ved brudd (review av queuey-client#51). En annen versjon er en form
        // denne klienten kan lese feil, så den vises ikke som et utfall.
        if (verification.SchemaVersion != FlowVerifications.SchemaVersion)
            throw new QueueyException(
                $"Queuey answered verification {verification.VerificationId} in shape {verification.SchemaVersion}, and this client reads " +
                $"shape {FlowVerifications.SchemaVersion}. Its outcome is not shown, since this client could read it wrong.",
                errorCode: FlowVerifications.UnsupportedSchemaCode)
            {
                SuggestedAction = "Update the Queuey client (the queuey CLI, or Queuey.Client.Waas) to a version that reads it.",
            };

        return verification;
    }

    private static QueueyException Unavailable(string queuePublicId, int status, bool queueUnknown)
        => new(
            (queueUnknown
                ? $"Queuey answered {status} without a reason to POST /queues/{queuePublicId}/verifications: either this Queuey cannot " +
                  $"verify flows, or queue {queuePublicId} does not exist for this key."
                : $"This Queuey cannot verify flows: it answered {status} to POST /queues/{queuePublicId}/verifications, the endpoint " +
                  "verify uses.") +
            " Nothing was sent.",
            status,
            FlowVerifications.UnavailableCode)
        {
            SuggestedAction = "Verifying needs a Queuey with flow verification. Point QueueyOptions.ApiBaseAddress (in the CLI: " +
                              "--api-base or QUEUEY_API_BASE) at one.",
        };

    private static QueueyException NotAVerification(string call)
        => new(
            $"Queuey answered {call} with something that is not a flow verification, so its outcome is unknown.",
            errorCode: FlowVerifications.UnavailableCode)
        {
            SuggestedAction = "Check that QueueyOptions.ApiBaseAddress (in the CLI: --api-base or QUEUEY_API_BASE) points at Queuey's API.",
        };
}
