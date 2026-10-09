using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

/// <summary>One difference between what a deployment file declares and what the workspace holds.</summary>
public sealed class DriftItem
{
    internal DriftItem(string path, string? declared, string? actual)
    {
        Path = path;
        Declared = declared;
        Actual = actual;
    }

    /// <summary>Where the difference is, e.g. <c>queues.orders.retentionDays</c>.</summary>
    public string Path { get; }

    /// <summary>What the file says, or <c>null</c> when it declares nothing there.</summary>
    public string? Declared { get; }

    /// <summary>What the workspace holds, or <c>null</c> when it holds nothing there.</summary>
    public string? Actual { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Path}: declared {Format(Declared)}, actual {Format(Actual)}";

    private static string Format(string? v) => v is null ? "(not declared)" : $"'{v}'";
}

/// <summary>
/// Compares a deployment file against a pulled workspace — the CI gate. Drift is not an error in
/// itself; it is the answer to "would applying this change anything?", which a pipeline turns into a
/// failing build so the divergence gets noticed at review time rather than during an incident.
/// </summary>
/// <remarks>
/// The comparison is <b>one-directional on purpose</b>: only what the file declares is checked. A
/// workspace holding settings the file says nothing about is not drift — that is inheritance and
/// console-owned configuration working as designed, and flagging it would make the gate unusable for
/// any team that does not put every last field under code.
/// </remarks>
public static class DeploymentDrift
{
    /// <summary>How a receiver's URL reads redacted; see <see cref="DeploymentUrls.Redact"/>, which the <c>queuey</c> CLI sets.</summary>
    internal static Func<string, string?>? RedactUrl
    {
        get => DeploymentUrls.Redact;
        set => DeploymentUrls.Redact = value;
    }

    /// <summary>Every difference between <paramref name="declared"/> and <paramref name="actual"/>.</summary>
    public static IReadOnlyList<DriftItem> Compare(DeploymentFile declared, DeploymentFile actual)
        => Compare(declared, actual, new List<string>());

    /// <summary>
    /// As <see cref="Compare(DeploymentFile, DeploymentFile)"/>, and <paramref name="comparedRedacted"/> gets each URL Queuey
    /// showed redacted: the file's URL was compared as it reads redacted, so a change only in the hidden part is not seen here.
    /// </summary>
    internal static IReadOnlyList<DriftItem> Compare(DeploymentFile declared, DeploymentFile actual, List<string> comparedRedacted)
    {
        if (declared is null) throw new ArgumentNullException(nameof(declared));
        if (actual is null) throw new ArgumentNullException(nameof(actual));

        var drift = new List<DriftItem>();
        _comparedRedacted = comparedRedacted;

        CompareWorkspace(declared.Workspace, actual.Workspace, drift);

        foreach (KeyValuePair<string, DeploymentQueue> entry in declared.Queues)
        {
            string prefix = $"queues.{entry.Key}";
            DeploymentQueue want = entry.Value ?? new DeploymentQueue();

            if (!actual.Queues.TryGetValue(entry.Key, out DeploymentQueue? have))
            {
                drift.Add(new DriftItem(prefix, "declared", null));
                continue;
            }

            // Mode in the file's words; "Deliver" and "deliver" are the same mode.
            if (want.Mode is { } mode && !string.Equals(mode.Trim(), have.Mode, StringComparison.OrdinalIgnoreCase))
                drift.Add(new DriftItem(prefix + ".mode", mode, have.Mode));

            Compare(prefix + ".ordering", want.Ordering, have.Ordering, drift);
            Compare(prefix + ".dlqEnabled", want.DlqEnabled, have.DlqEnabled, drift);
            Compare(prefix + ".retentionDays", want.RetentionDays, have.RetentionDays, drift);
            Compare(prefix + ".idempotent", want.Idempotent, have.Idempotent, drift);
            CompareBackoff(prefix, want.Backoff, have.Backoff, drift);

            if (want.Filter is { } wf)
            {
                string declaredFilter = DescribeFilter(wf);
                string actualFilter = DescribeFilter(have.Filter);
                if (!string.Equals(declaredFilter, actualFilter, StringComparison.Ordinal))
                    drift.Add(new DriftItem(prefix + ".filter", declaredFilter, actualFilter));
            }

            if (want.Delivery is { } wd)
            {
                QueueDelivery hd = have.Delivery ?? new QueueDelivery();
                CompareUrl(prefix + ".delivery.url", wd.Url, hd.Url, drift);
                Compare(prefix + ".delivery.authMode", wd.AuthMode, hd.AuthMode, drift);
                Compare(prefix + ".delivery.credentialRef", wd.CredentialRef, hd.CredentialRef, drift);
                // Typen i filas ord (Queuey F2.3): "LocalForward" og "localForward" er samme type.
                Compare(prefix + ".delivery.kind", DeploymentDeliveryKinds.Parse(wd.Kind, prefix + ".delivery.kind")?.ToFileText(), hd.Kind, drift);
            }

            CompareIngress(prefix + ".ingress", want.Ingress, have.Ingress, drift);
        }

        return drift;
    }

    private static void CompareWorkspace(DeploymentWorkspace? want, DeploymentWorkspace? have, List<DriftItem> drift)
    {
        if (want is null) return;
        DeploymentWorkspace actual = have ?? new DeploymentWorkspace();

        // Queuey lagrer merket med små bokstaver; fila kan skrive Prod.
        Compare("workspace.environment", want.EnvironmentToSend, actual.Environment, drift);
        Compare("workspace.ordering", want.Ordering, actual.Ordering, drift);
        Compare("workspace.dlqEnabled", want.DlqEnabled, actual.DlqEnabled, drift);
        Compare("workspace.retentionDays", want.RetentionDays, actual.RetentionDays, drift);
        Compare("workspace.idempotent", want.Idempotent, actual.Idempotent, drift);
        CompareBackoff("workspace", want.Backoff, actual.Backoff, drift);

        CompareIngress("workspace.ingress", want.Ingress, actual.Ingress, drift);

        if (want.Delivery is { } wd)
        {
            WorkspaceDelivery hd = actual.Delivery ?? new WorkspaceDelivery();
            CompareUrl("workspace.delivery.baseUrl", wd.BaseUrl, hd.BaseUrl, drift);
            Compare("workspace.delivery.authMode", wd.AuthMode, hd.AuthMode, drift);
            Compare("workspace.delivery.credentialRef", wd.CredentialRef, hd.CredentialRef, drift);
            Compare("workspace.delivery.authHeaderName", wd.AuthHeaderName, hd.AuthHeaderName, drift);
            Compare("workspace.delivery.method", wd.Method, hd.Method, drift);
            Compare("workspace.delivery.timeoutMs", wd.TimeoutMs, hd.TimeoutMs, drift);
        }
    }

    private static void CompareBackoff(string prefix, RetryBackoff? backoff, RetryBackoff? haveBackoff, List<DriftItem> drift)
    {
        // Backoff per field, like everything else: a file may own the base delay and leave the rest.
        if (backoff is not null)
        {
            Compare(prefix + ".backoff.baseDelayMs", backoff.BaseDelayMs, haveBackoff?.BaseDelayMs, drift);
            Compare(prefix + ".backoff.maxDelayMs", backoff.MaxDelayMs, haveBackoff?.MaxDelayMs, drift);
            if (backoff.Jitter is { } jitter && !string.Equals(jitter, haveBackoff?.Jitter, StringComparison.OrdinalIgnoreCase))
                drift.Add(new DriftItem(prefix + ".backoff.jitter", jitter, haveBackoff?.Jitter));
        }
    }

    /// <summary>
    /// A filter as one comparable line. No filter and a filter with no conditions both deliver every
    /// event, so they read the same — that is how a file removes the filter it had.
    /// </summary>
    internal static string DescribeFilter(DeliveryFilter? filter)
        => filter?.Conditions is not { Count: > 0 } ? "(delivers every event)" : filter.Describe();

    /// <summary>Ingress differs per field like everything else — a source is its kind plus its name.</summary>
    private static void CompareIngress(string prefix, DeploymentIngress? want, DeploymentIngress? have, List<DriftItem> drift)
    {
        if (want is null) return;
        DeploymentIngress actual = have ?? new DeploymentIngress();

        Compare($"{prefix}.authMode", want.AuthMode, actual.AuthMode, drift);
        Compare($"{prefix}.successStatusCode", want.SuccessStatusCode, actual.SuccessStatusCode, drift);
        CompareSource($"{prefix}.eventType", want.EventType, actual.EventType, drift);
        CompareSource($"{prefix}.groupKey", want.GroupKey, actual.GroupKey, drift);
        CompareSignedRequest($"{prefix}.signedRequest", want.SignedRequest, actual.SignedRequest, drift);
    }

    /// <summary>
    /// The signed request's template and credential (Queuey F2.3). A credential the file names by id matches the bound one's
    /// id; one the ingress waits for matches by name, until it is stored: then apply would bind it, and that is drift.
    /// </summary>
    private static void CompareSignedRequest(string path, DeploymentSignedRequest? want, DeploymentSignedRequest? have, List<DriftItem> drift)
    {
        if (want is null) return;

        string? declaredTemplate = want.Template?.Trim().ToLowerInvariant();
        string? actualTemplate = have?.Template?.Trim().ToLowerInvariant();
        if (!string.Equals(declaredTemplate, actualTemplate, StringComparison.Ordinal))
            drift.Add(new DriftItem(path + ".template", declaredTemplate, actualTemplate));

        if (want.CredentialRef?.Trim() is not { } declared)
            return;

        // Navnet ingressen venter på, er lagret av en med skrivetilgang (Queuey F2.3-review): det vises bare i den trygge formen.
        if (have?.AwaitsStoredCredential == true)
        {
            drift.Add(new DriftItem(path + ".credentialRef", declared,
                CredentialNameRules.Showable(have.CredentialRef) is { } shown
                    ? $"waiting for '{shown}', which is stored now: apply points the ingress at it"
                    : "waiting for a credential that is stored now: apply points the ingress at it"));
            return;
        }

        if (!string.Equals(declared, have?.CredentialRef, StringComparison.Ordinal)
            && !string.Equals(declared, have?.BoundCredentialId, StringComparison.Ordinal))
            drift.Add(new DriftItem(path + ".credentialRef", declared,
                have?.CredentialRef is null ? null : CredentialNameRules.Showable(have.CredentialRef) ?? "(a name that is not shown)"));
    }

    private static void CompareSource(string path, ContextSource? want, ContextSource? have, List<DriftItem> drift)
    {
        if (want is null) return;

        string? actual = have is null || string.IsNullOrWhiteSpace(have.Name) ? null : $"{Word(have.From)}:{have.Name!.Trim()}";

        // Et tomt navn fjerner kilden: i synk når Queuey ikke leser verdien noe sted (2026-10-05). Før ble
        // "header:" sammenlignet med ingenting, og fila meldte drift for alltid.
        if (want.Clears)
        {
            if (actual is not null) drift.Add(new DriftItem(path, "(removed)", actual));
            return;
        }

        string declared = $"{Word(want.From)}:{want.Name?.Trim()}";
        if (string.Equals(declared, actual, StringComparison.Ordinal)) return;

        drift.Add(new DriftItem(path, declared, actual));

        // Queuey skriver kilden med små bokstaver; fila kan skrive Body.
        static string? Word(string? from) => from?.Trim().ToLowerInvariant();
    }

    [ThreadStatic] private static List<string>? _comparedRedacted;

    /// <summary>
    /// A URL. Equal is in sync. When Queuey showed it redacted (Queuey #514: the marker, or a query or user info left out),
    /// the file's URL is compared as it reads redacted, and the path is listed: a change only in the hidden part does not show
    /// here (plan sees it). Without the rules (an SDK outside the CLI), a redacted URL is compared by its scheme and host, and
    /// any other difference is drift: never «in sync» by silence. Both sides are always shown redacted: the file's URL may be a
    /// secret from a variable.
    /// </summary>
    // Security-review av #72 (B1, B2, N4).
    private static void CompareUrl(string path, string? want, string? have, List<DriftItem> drift)
    {
        if (want is null) return;
        if (string.Equals(want, have, StringComparison.Ordinal)) return;

        bool redactedReading = DeploymentUrls.CarriesMarker(have)
                               || (have is not null && DeploymentUrls.Redact is { } r && string.Equals(r(want), have, StringComparison.Ordinal));
        if (redactedReading)
            _comparedRedacted?.Add(path);

        if (DeploymentUrls.Redact is { } redact)
        {
            if (have is not null && string.Equals(redact(want), have, StringComparison.Ordinal))
                return;
        }
        else if (redactedReading && DeploymentUrls.SameOrigin(want, have))
            return;

        drift.Add(new DriftItem(path, DeploymentUrls.Shown(want), DeploymentUrls.Shown(have)));
    }

    /// <summary>A field the file does not declare is never drift — see the type's remarks.</summary>
    private static void Compare(string path, string? want, string? have, List<DriftItem> drift)
    {
        if (want is null) return;
        if (string.Equals(want, have, StringComparison.Ordinal)) return;
        drift.Add(new DriftItem(path, want, have));
    }

    private static void Compare(string path, int? want, int? have, List<DriftItem> drift)
    {
        if (want is null) return;
        if (want == have) return;
        drift.Add(new DriftItem(path, want.Value.ToString(), have?.ToString()));
    }

    private static void Compare(string path, bool? want, bool? have, List<DriftItem> drift)
    {
        if (want is null) return;
        if (want == have) return;
        drift.Add(new DriftItem(path, want.Value ? "true" : "false", have is null ? null : (have.Value ? "true" : "false")));
    }
}
