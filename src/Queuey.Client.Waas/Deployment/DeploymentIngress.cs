using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>
/// How Queuey reads events as they arrive — declared on the workspace so every queue inherits it,
/// or on one queue to override.
/// </summary>
/// <remarks>
/// <para>
/// The point of the sources is that an event describes itself. Without them every producer has to
/// send <c>X-Queuey-Event-Type</c> and <c>X-Queuey-Group-Key</c> on every publish; with them the
/// workspace says once that the type is the <c>type</c> field in the body, and every queue reads it
/// the same way — including producers you do not control, like a third-party webhook.
/// </para>
/// <para>
/// Declaring a <see cref="GroupKey"/> also means "lane by it": partitioning needs a key, and a key
/// with nothing partitioning on it would be decoration.
/// </para>
/// </remarks>
public sealed class DeploymentIngress
{
    /// <summary>The values <see cref="AuthMode"/> accepts, in any casing.</summary>
    public static readonly IReadOnlyList<string> AuthModeValues = new[] { "None", "ApiKey", "SignedRequest", "ApiKeyAndSignedRequest" };

    /// <summary>
    /// What the ingress edge demands of a publisher: <c>None</c>, <c>ApiKey</c>,
    /// <c>SignedRequest</c> or <c>ApiKeyAndSignedRequest</c>.
    /// </summary>
    /// <remarks>
    /// The one field here that can stop traffic. Queuey defaults a new queue to <c>None</c> so a
    /// first webhook works without key ceremony — turning it on against publishers that carry no
    /// key rejects them from the next event onward. Roll the credential out first.
    /// </remarks>
    public string? AuthMode { get; set; }

    /// <summary>Where the event type is read from. Null leaves it as it is.</summary>
    public ContextSource? EventType { get; set; }

    /// <summary>Where the group key is read from — and, by declaring it, what to lane on.</summary>
    public ContextSource? GroupKey { get; set; }

    /// <summary>
    /// The status the ingress returns on accept. Default 202. Some senders are stricter than the
    /// HTTP spec and treat anything but 200 as a failed webhook, so those queues set 200.
    /// </summary>
    public int? SuccessStatusCode { get; set; }

    /// <summary>
    /// The provider signature the ingress verifies: a template, such as <c>stripe</c>, and the credential holding the
    /// provider's signing secret. Checked while <see cref="AuthMode"/> is <c>SignedRequest</c> or
    /// <c>ApiKeyAndSignedRequest</c>.
    /// </summary>
    public DeploymentSignedRequest? SignedRequest { get; set; }

    internal bool IsEmpty => AuthMode is null && EventType is null && GroupKey is null && SuccessStatusCode is null && SignedRequest is null;

    /// <summary>
    /// Refuses what Queuey would refuse, or quietly read another way, before anything is sent.
    /// <paramref name="where"/> is the declaration's path in the file, e.g. <c>queues.orders.ingress</c>.
    /// </summary>
    internal void Validate(string where)
    {
        if (AuthMode is not null && !AuthModeValues.Contains(AuthMode.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new QueueyConfigurationException(
                $"{where}.authMode must be one of {string.Join(", ", AuthModeValues)}; got '{AuthMode}'.");

        EventType?.Validate($"{where}.eventType");
        GroupKey?.Validate($"{where}.groupKey");

        if (SignedRequest is not null)
        {
            SignedRequest.Validate($"{where}.signedRequest");

            // Fila sier både at signaturen skal verifiseres og at ingen signatur sjekkes: en av dem er en feil.
            if (AuthMode is { } mode && (mode.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)
                                        || mode.Trim().Equals("ApiKey", StringComparison.OrdinalIgnoreCase)))
                throw new QueueyConfigurationException(
                    $"{where} declares a signedRequest, but authMode {mode.Trim()} checks no signature. Use SignedRequest, or " +
                    "ApiKeyAndSignedRequest to demand both, or remove the signedRequest.");
        }
    }
}

/// <summary>
/// The provider signature an ingress verifies. <see cref="CredentialRef"/> names a stored credential, never the secret;
/// a name no credential has yet is accepted, and the ingress then refuses every event until it is stored and applied
/// again.
/// </summary>
public sealed class DeploymentSignedRequest
{
    /// <summary>The longest template a file may name. Queuey's templates are a word each.</summary>
    internal const int MaxTemplateLength = 64;

    /// <summary>The longest credential reference a file may name: the longest a credential name can be.</summary>
    internal const int MaxCredentialRefLength = 200;

    /// <summary>
    /// The signed-request template from Queuey's catalogue: <c>stripe</c>, or <c>queuey</c> for Queuey's own scheme, which
    /// verifies with the sending API client's signing key and takes no credential.
    /// </summary>
    public string? Template { get; set; }

    /// <summary>
    /// The name of the credential holding the provider's signing secret (or its <c>cred_…</c> id). Store it with
    /// <c>queuey credentials set --name &lt;name&gt; --type HmacSigning --key-id &lt;name&gt; --from-env &lt;VARIABLE&gt;</c>.
    /// </summary>
    public string? CredentialRef { get; set; }

    // Det en pull leser tilbake, for drift-sjekken: id-en til en bundet credential (fila kan navngi den med id), og om
    // ingressen venter på et navn som er lagret nå, så apply ville bundet det. Ikke en del av fila.
    internal string? BoundCredentialId { get; set; }
    internal bool AwaitsStoredCredential { get; set; }

    internal void Validate(string where)
    {
        if (string.IsNullOrWhiteSpace(Template))
            throw new QueueyConfigurationException(
                $"{where} needs a template: the provider signature to verify, such as stripe.");
        if (Template!.Trim().Length > MaxTemplateLength)
            throw new QueueyConfigurationException($"{where}.template is longer than {MaxTemplateLength} characters.");
        if (CredentialRef is not null && CredentialRef.Trim().Length == 0)
            throw new QueueyConfigurationException(
                $"{where}.credentialRef is empty. Name the credential that holds the signing secret, or leave the field out for a template that takes none.");
        if (CredentialRef is not null && CredentialRef.Trim().Length > MaxCredentialRefLength)
            throw new QueueyConfigurationException($"{where}.credentialRef is longer than {MaxCredentialRefLength} characters, the longest a credential name can be.");

        // Queuey lagrer et navn som ikke finnes ennå, og viser det tilbake. En hemmelighet limt inn der navnet skal stå, avvises
        // derfor før noe sendes, og gjentas ikke (samme prefikser som Queuey, F2.3).
        if (CredentialRef is not null && SecretPrefixes.Any(p => CredentialRef.Trim().StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            throw new QueueyConfigurationException(
                $"{where}.credentialRef looks like a secret, not the name of a credential. Its value is not shown.")
            {
                SuggestedAction = "Store the secret with queuey credentials set --name <name> --type HmacSigning --key-id <name> --from-env <VARIABLE>, name that credential here, and keep the secret out of the file.",
            };
    }

    private static readonly string[] SecretPrefixes = { "whsec_", "sk_live_", "sk_test_", "rk_live_", "rk_test_", "qak_" };
}

/// <summary>
/// Where one context value is read from. <c>name</c> is required, and an empty name removes the source;
/// <c>from</c> is required whenever the name names something.
/// </summary>
public sealed class ContextSource
{
    /// <summary>The values <see cref="From"/> accepts, in any casing.</summary>
    public static readonly IReadOnlyList<string> FromValues = new[] { "header", "query", "body" };

    /// <summary>Creates an empty source (for deserialization).</summary>
    public ContextSource() { }

    /// <summary>Creates a source.</summary>
    public ContextSource(string? from, string? name)
    {
        From = from;
        Name = name;
    }

    /// <summary>
    /// Where the value is read: <c>header</c>, <c>query</c>, or <c>body</c>, the top level of the JSON you post.
    /// Required whenever <c>name</c> is not empty.
    /// </summary>
    // Før 2026-10-05 var from «header» og name tom når fila ikke skrev dem. {"from":"body"} ble da sendt som et tomt navn
    // og fjernet kilden som var lagret, og {"name":"type"} betydde header uten at fila sa det.
    public string? From { get; set; }

    /// <summary>
    /// The header, query parameter, or body field to read. Required: <c>""</c> removes the source, so the
    /// value is no longer read from anywhere.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>True when the declaration removes the source rather than setting one.</summary>
    internal bool Clears => Name is not null && Name.Trim().Length == 0;

    /// <summary>Refuses a source Queuey would read another way than the file says, before anything is sent.</summary>
    internal void Validate(string where)
    {
        if (Name is null)
            throw new QueueyConfigurationException(
                $"{where} needs a name: the header, query parameter or body field to read. To remove the source, write \"name\": \"\".");

        if (Clears)
            return;

        if (From is null)
            throw new QueueyConfigurationException(
                $"{where} needs \"from\": {string.Join(", ", FromValues)}, the place '{Name}' is read from.");

        if (!FromValues.Contains(From.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new QueueyConfigurationException(
                $"{where}.from must be one of {string.Join(", ", FromValues)}; got '{From}'.");
    }
}

// ── wire ──────────────────────────────────────────────────────────────────────

internal sealed class PatchIngressWireRequest
{
    public string? AuthMode { get; set; }
    public ContextSourceWire? EventType { get; set; }
    public ContextSourceWire? GroupKey { get; set; }
    public int? SuccessStatusCode { get; set; }

    // Navnet sendes som fila skriver det: Queuey slår det opp og venter på en credential som ikke finnes ennå (F2.3).
    public SignedRequestWire? SignedRequest { get; set; }
}

internal sealed class SignedRequestWire
{
    public string? Template { get; set; }
    public string? CredentialRef { get; set; }
}

/// <summary>The signed request an ingress keeps, as Queuey reads it back: a bound <c>cred_…</c> id, or the name it waits for.</summary>
internal sealed class SignedRequestResponse
{
    public string? Template { get; set; }
    public string? CredentialRef { get; set; }
    public string? PendingCredential { get; set; }
}

internal sealed class ContextSourceWire
{
    public string From { get; set; } = default!;
    public string Name { get; set; } = default!;
}

/// <summary>Wire shape of <c>PATCH /tenants/{ten}</c>: the workspace itself. Null leaves a field alone.</summary>
internal sealed class PatchWorkspaceWireRequest
{
    public string? Environment { get; set; }
}

internal sealed class PatchTenantPolicyWireRequest
{
    public string? Ordering { get; set; }
    public bool? DlqEnabled { get; set; }
    public int? RetentionDays { get; set; }
    public bool? Idempotent { get; set; }
    public RetryBackoffWire? Backoff { get; set; }
}

internal sealed class IngressResponse
{
    public string? AuthMode { get; set; }
    public ContextSourceWire? EventType { get; set; }
    public ContextSourceWire? GroupKey { get; set; }
    public int SuccessStatusCode { get; set; }

    // Null fra et API som er eldre enn feltet (Queuey F2.3), eller når ingressen ikke har noen signert forespørsel.
    public SignedRequestResponse? SignedRequest { get; set; }
}
