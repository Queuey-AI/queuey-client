using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

// F2.7 (2026-10-06): `queuey publish` gikk bare til streams. C5 og Flyt A trenger én event med fast payload og fast
// idempotency-nøkkel til en kø, publisert slik produsenten gjør (nøkkelen, køens ingress-URL), og så
// `queuey verify <kø> --event <id>`. Det er veien som virker overalt, også i prod og mot en ingress med nøkkel, der en
// testevent fra Queuey (`verify --send`) ikke gjør det. Krever ingressen noe denne klienten ikke kan gi (en providers
// signatur, Queuey sin signatur uten signeringsnøkkel, nøkkel og signatur sammen), sies det før noe sendes.

/// <summary>Options for <see cref="IQueueyService.PublishToQueueAsync"/>.</summary>
public sealed class QueuePublishOptions
{
    /// <summary>
    /// Idempotency key, sent as <c>Idempotency-Key</c>. A fixed key makes the event recognizable: publishing it again
    /// within the queue's window answers with the same event, and <see cref="QueuePublishResult.Replayed"/> is true.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Event type, sent as <c>X-Queuey-Event-Type</c>, for a queue that reads it (a stream does).</summary>
    public string? EventType { get; set; }

    /// <summary>Group key, sent as <c>X-Queuey-Group-Key</c>, for a queue that reads it.</summary>
    public string? GroupKey { get; set; }

    /// <summary>The payload's content type. <c>application/json</c> when not set.</summary>
    public string? ContentType { get; set; }

    /// <summary>Trace source, sent as <c>X-Queuey-Source</c>. Overrides <see cref="QueueyOptions.Source"/>.</summary>
    public string? Source { get; set; }
}

/// <summary>What publishing one event to a queue gave. It never holds the payload or a key.</summary>
public sealed class QueuePublishResult
{
    /// <summary>The workspace (<c>ten_…</c>) the queue is in.</summary>
    public string TenantPublicId { get; init; } = default!;

    /// <summary>The queue's name: what its ingress URL has.</summary>
    public string Queue { get; init; } = default!;

    /// <summary>The queue's id (<c>que_…</c>), from the receipt or from reading the workspace's queues; null when neither said.</summary>
    public string? QueuePublicId { get; init; }

    /// <summary>
    /// The event's id (<c>evt_…</c>), for <c>queuey verify &lt;queue&gt; --event &lt;id&gt;</c>. Null when the ingress
    /// accepted the event without a receipt, as a queue whose ingress answers 204 does.
    /// </summary>
    public string? EventPublicId { get; init; }

    /// <summary>When Queuey received the event, by the receipt.</summary>
    public DateTimeOffset? ReceivedAtUtc { get; init; }

    /// <summary>The queue's mode by the receipt: <c>Deliver</c> or <c>LogOnly</c>.</summary>
    public string? Mode { get; init; }

    /// <summary>True when the idempotency key matched an earlier event: this is that event, and nothing new was stored.</summary>
    public bool Replayed { get; init; }

    /// <summary>
    /// The ingress's auth mode as this client read it before publishing (<c>None</c>, <c>ApiKey</c>, …), or null when the
    /// key may not read the queue's configuration, and the ingress alone decided.
    /// </summary>
    public string? IngressAuthMode { get; init; }
}

/// <summary>Publishes one event to a queue the way a producer does, after checking what its ingress requires.</summary>
internal static class QueuePublisher
{
    private const string JsonContentType = "application/json";

    // Kodene ingressen svarer med når en signatur mangler eller ikke holder (SignedRequestMiddleware i Queuey).
    private static readonly HashSet<string> SignatureRefusals = new(StringComparer.Ordinal)
    {
        "missing_required_header", "invalid_timestamp", "timestamp_out_of_range", "signing_key_not_found",
        "signing_key_inactive", "signing_key_expired", "signing_key_revoked", "invalid_content_hash", "invalid_signature",
        "replayed_request",
    };

    public static async Task<QueuePublishResult> RunAsync(
        QueueyClient client,
        QueueyControlPlaneClient controlPlane,
        IQueueyManagement management,
        QueueyOptions options,
        string queue,
        byte[] payload,
        QueuePublishOptions publish,
        CancellationToken cancellationToken)
    {
        string tenant = !string.IsNullOrWhiteSpace(options.TenantPublicId)
            ? options.TenantPublicId!
            : throw new QueueyConfigurationException("Publishing to a queue needs the workspace (ten_…) it is in, and none is set.")
            {
                SuggestedAction = "Set QueueyOptions.TenantPublicId (in the CLI: --tenant, QUEUEY_TENANT, tenant in queuey.json, or the deployment file's tenant).",
            };

        // Køen: navnet ingress-URL-en har, og der nøkkelen kan lese den, id-en og hva ingressen krever.
        QueueListItem? row = await FindAsync(management, tenant, queue, cancellationToken).ConfigureAwait(false);
        string name = row?.DisplayName ?? (queue.StartsWith("que_", StringComparison.Ordinal)
            ? throw new QueueyConfigurationException(
                "The ingress takes a queue by its name, and finding the name of a queue id needs a key that may read the workspace's queues (queue.read).")
            {
                SuggestedAction = "Give the queue's name, the one in its ingress URL (/events/<workspace>/<name>).",
            }
            : queue);

        if (row is { IngressClosed: true })
            throw new QueueyException(
                "The queue's ingress is closed: it takes no new events while its backlog drains, so nothing was published.",
                errorCode: "ingress_closed")
            {
                SuggestedAction = "Open the queue's ingress again in the Queuey console, then publish.",
            };

        IngressResponse? ingress = row?.PublicId is { } id
            ? await ReadIngressAsync(controlPlane, id, cancellationToken).ConfigureAwait(false)
            : null;
        if (ingress is not null && IngressRefusal(ingress, options, name) is { } refusal)
            throw refusal;

        PublishResult? receipt;
        try
        {
            receipt = await client.Ingress.PublishAsync(name, payload, new PublishOptions
            {
                ContentType = string.IsNullOrWhiteSpace(publish.ContentType) ? JsonContentType : publish.ContentType,
                EventType = publish.EventType,
                GroupKey = publish.GroupKey,
                IdempotencyKey = publish.IdempotencyKey,
                Source = publish.Source,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // 2xx uten kvittering: en ingress som svarer 204, sender ingen kropp. Eventet er tatt imot, men id-en er ukjent.
            receipt = null;
        }
        catch (QueueyAuthException ex) when (ex.ErrorCode is { } code && SignatureRefusals.Contains(code))
        {
            throw new QueueyException(
                "The queue's ingress checks a signature on each event, and this publish carried the API key, so the ingress refused " +
                $"it and nothing was stored. Queuey said: {ex.Message}",
                ex.StatusCode, code)
            {
                SuggestedAction = SignedEventAction(name, template: null),
            };
        }
        catch (QueueyForbiddenException ex) when (ex.ErrorCode is "missing_permission" or "client_scope_denied")
        {
            throw new QueueyException(
                $"This key may not publish to the queue, so nothing was stored. Queuey said: {ex.Message}", ex.StatusCode, ex.ErrorCode)
            {
                SuggestedAction = "Publish with a key that may publish to the queue (event.publish), the producer's own.",
            };
        }

        return new QueuePublishResult
        {
            TenantPublicId = tenant,
            Queue = name,
            QueuePublicId = receipt?.QueuePublicId ?? row?.PublicId,
            EventPublicId = string.IsNullOrWhiteSpace(receipt?.EventId) ? null : receipt!.EventId,
            ReceivedAtUtc = receipt?.ReceivedAtUtc,
            Mode = receipt?.Mode,
            Replayed = receipt?.Replayed ?? false,
            IngressAuthMode = ingress?.AuthMode,
        };
    }

    /// <summary>
    /// The workspace's row for <paramref name="queue"/>, by its name or id; null when the key may not read the workspace's
    /// queues. Throws when it may, and the workspace has no such queue.
    /// </summary>
    private static async Task<QueueListItem?> FindAsync(IQueueyManagement management, string tenant, string queue, CancellationToken cancellationToken)
    {
        IReadOnlyList<QueueListItem> rows;
        try
        {
            rows = await management.ListQueuesAsync(tenant, cancellationToken).ConfigureAwait(false);
        }
        catch (QueueyForbiddenException)
        {
            // En nøkkel som bare kan publisere (IngressOnly), leser ikke køene. Da avgjør ingressen alene.
            return null;
        }

        bool byId = queue.StartsWith("que_", StringComparison.Ordinal);
        QueueListItem? row = rows.FirstOrDefault(r => byId
            ? string.Equals(r.PublicId, queue, StringComparison.Ordinal)
            : string.Equals(r.DisplayName, queue, StringComparison.Ordinal));

        // Navnet vises ikke: det er det brukeren skrev, og det kan være en hemmelighet limt inn på feil plass.
        return row ?? throw new QueueyNotFoundException(
            $"Workspace {tenant} has no queue by that {(byId ? "id" : "name")}, so nothing was published.", "queue_not_found")
        {
            SuggestedAction = "Check the queue's name in the deployment file or the Queuey console, and that the key reaches the right workspace.",
        };
    }

    /// <summary>The queue's effective ingress, or null when the key may not read the queue's configuration.</summary>
    private static async Task<IngressResponse?> ReadIngressAsync(QueueyControlPlaneClient controlPlane, string queuePublicId, CancellationToken cancellationToken)
    {
        try
        {
            return (await controlPlane.GetQueueConfigAsync(queuePublicId, cancellationToken).ConfigureAwait(false)).Ingress;
        }
        catch (QueueyForbiddenException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why this client cannot publish to an ingress that wants <paramref name="ingress"/>, or null when it can: an API key
    /// it does not have, a provider's signature, Queuey's signature it does not make, or a key and a signature together.
    /// </summary>
    internal static QueueyException? IngressRefusal(IngressResponse ingress, QueueyOptions options, string queue)
    {
        bool hasApiKey = !string.IsNullOrWhiteSpace(options.ApiKey);
        // Klienten signerer bare uten API-nøkkel: med begge satt sender den nøkkelen (QueueyClient.BuildIngressAuthenticator).
        bool signs = !hasApiKey && !string.IsNullOrWhiteSpace(options.SigningKeyId) && !string.IsNullOrWhiteSpace(options.SigningSecret);
        string? template = Word(ingress.SignedRequest?.Template);
        bool queueyTemplate = string.Equals(template, "queuey", StringComparison.OrdinalIgnoreCase);

        switch (ingress.AuthMode?.Trim().ToLowerInvariant())
        {
            case "apikey" when !hasApiKey:
                return new QueueyException(
                    "The queue's ingress takes events with an API key that may publish to it, and none is configured, so nothing was published.",
                    errorCode: "ingress_requires_api_key")
                {
                    SuggestedAction = "Set the producer's key: QueueyOptions.ApiKey (in the CLI: --api-key, QUEUEY_API_KEY or apiKey in queuey.json).",
                };

            case "signedrequest" when queueyTemplate && signs:
                return null;

            case "signedrequest":
                return new QueueyException(
                    queueyTemplate
                        ? "The queue's ingress checks Queuey's signature on each event (a signed request with the queuey template), " +
                          "made with an ingress signing key, and this publish would carry the API key instead, so nothing was published."
                        : $"The queue's ingress verifies {template ?? "a provider's"} signatures, which only the provider's own " +
                          "webhook carries, so nothing was published.",
                    errorCode: "ingress_requires_signature")
                {
                    SuggestedAction = SignedEventAction(queue, queueyTemplate ? null : template),
                };

            case "apikeyandsignedrequest":
                return new QueueyException(
                    "The queue's ingress takes events with both an API key and a signature" +
                    (template is null ? "" : $" ({template})") + ", and a publish here carries one of them, so nothing was published.",
                    errorCode: "ingress_requires_signature")
                {
                    SuggestedAction = SignedEventAction(queue, queueyTemplate ? null : template),
                };

            default:
                // None, en nøkkel klienten har, eller en modus denne klienten ikke kjenner: ingressen avgjør.
                return null;
        }
    }

    /// <summary>What to do instead, for an ingress that wants a signature this publish does not carry.</summary>
    private static string SignedEventAction(string queue, string? template)
    {
        string shown = Word(queue) ?? "<queue>";
        return template is null
            ? $"Publish from the producer, which signs each event, and follow it with `queuey verify {shown} --event <evt_…>`; or wait " +
              $"for its next event with `queuey verify {shown} --event-type <type>`."
            : $"Trigger the event at the provider (for Stripe: stripe trigger <event>), and follow it with " +
              $"`queuey verify {shown} --event-type <type> --ingress-auth {template}`.";
    }

    // Et ord som er trygt å sette inn i en kommando: et kønavn eller en mal fra katalogen. Noe annet vises ikke.
    private static string? Word(string? value)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed!.Length > 64)
            return null;

        foreach (char c in trimmed)
        {
            bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
            if (!allowed)
                return null;
        }

        return trimmed;
    }
}
