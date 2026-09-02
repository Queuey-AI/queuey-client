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
    /// <summary>Every difference between <paramref name="declared"/> and <paramref name="actual"/>.</summary>
    public static IReadOnlyList<DriftItem> Compare(DeploymentFile declared, DeploymentFile actual)
    {
        if (declared is null) throw new ArgumentNullException(nameof(declared));
        if (actual is null) throw new ArgumentNullException(nameof(actual));

        var drift = new List<DriftItem>();

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

            Compare(prefix + ".ordering", want.Ordering, have.Ordering, drift);
            Compare(prefix + ".dlqEnabled", want.DlqEnabled, have.DlqEnabled, drift);
            Compare(prefix + ".retentionDays", want.RetentionDays, have.RetentionDays, drift);
            Compare(prefix + ".idempotent", want.Idempotent, have.Idempotent, drift);

            if (want.Delivery is { } wd)
            {
                QueueDelivery hd = have.Delivery ?? new QueueDelivery();
                Compare(prefix + ".delivery.url", wd.Url, hd.Url, drift);
                Compare(prefix + ".delivery.authMode", wd.AuthMode, hd.AuthMode, drift);
                Compare(prefix + ".delivery.credentialRef", wd.CredentialRef, hd.CredentialRef, drift);
            }
        }

        return drift;
    }

    private static void CompareWorkspace(WorkspaceDelivery? want, WorkspaceDelivery? have, List<DriftItem> drift)
    {
        if (want is null) return;
        WorkspaceDelivery actual = have ?? new WorkspaceDelivery();

        Compare("workspace.baseUrl", want.BaseUrl, actual.BaseUrl, drift);
        Compare("workspace.authMode", want.AuthMode, actual.AuthMode, drift);
        Compare("workspace.credentialRef", want.CredentialRef, actual.CredentialRef, drift);
        Compare("workspace.authHeaderName", want.AuthHeaderName, actual.AuthHeaderName, drift);
        Compare("workspace.method", want.Method, actual.Method, drift);
        Compare("workspace.timeoutMs", want.TimeoutMs, actual.TimeoutMs, drift);
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
