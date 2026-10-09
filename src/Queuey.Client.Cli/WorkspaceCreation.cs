using System;
using System.Linq;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// When <c>queuey apply</c> creates the workspace itself: nothing names one, the file says <c>dev</c> or <c>test</c>, no CI is
/// detected, and no delivery names a credential. Otherwise a missing workspace is the error it always was, with
/// <c>queuey create-tenant --environment …</c> as the way out.
/// </summary>
// Gullflyten 2026-10-09 ga apply rett til å lage workspacet. Reviewen av #65 (M1, B1): i CI er et manglende workspace en feil i
// oppsettet, ikke et nytt miljø, og en fil som ble kjørt der, laget et nytt workspace per kjøring. Et workspace som er nytt,
// har heller ingen credentials, så en levering som navngir en, feilet etter at workspacet var laget, og neste forsøk laget et
// til. Derfor bare dev og test, utenfor CI, og uten credential i leveringen.
internal static class WorkspaceCreation
{
    /// <summary>The environments apply creates a workspace for.</summary>
    private static readonly string[] Creatable = { "dev", "test" };

    /// <summary>The name of a workspace apply creates: neutral, never taken from the machine it runs on.</summary>
    public static string NameFor(string environment) => $"queuey-{environment}";

    /// <summary>
    /// Null when apply may create a workspace marked <paramref name="environment"/> for <paramref name="file"/>; otherwise the
    /// error for a missing workspace, with why it is not created and how to make one.
    /// </summary>
    /// <param name="file">The file, with its <c>${VAR}</c> expanded.</param>
    public static QueueyConfigurationException? Refusal(string environment, DeploymentFile file, string path, Func<string, string?> env)
    {
        string? why = !Creatable.Contains(environment, StringComparer.Ordinal)
            ? $"apply creates a workspace only for dev or test, and {path} says {environment}"
            : DetectedCi(env) is { } variable
                ? $"apply does not create a workspace in CI ({variable} is set), where a missing workspace is a mistake in the setup"
                : DeliveryCredential(file) is { } where
                    ? $"apply does not create one for {path}: {where} names a credential, which a new workspace does not have yet"
                    : null;
        if (why is null)
            return null;

        return new QueueyConfigurationException($"A workspace (ten_…) is required for this call, and none is set, and {why}.")
        {
            SuggestedAction = $"Name the workspace with --tenant, QUEUEY_TENANT, tenant in {path} or in the profile. To make one: "
                              + $"`queuey create-tenant --name {NameFor(environment)} --environment {environment}`"
                              + (DeliveryCredential(file) is null ? "" : ", then store the credential in it with `queuey credentials set --tenant ten_… …`")
                              + ", then apply with --tenant ten_….",
        };
    }

    /// <summary>The CI variable that is set, or null when none is: <c>CI</c>, and the ones GitHub Actions sets.</summary>
    internal static string? DetectedCi(Func<string, string?> env)
    {
        foreach (string name in new[] { "CI", "GITHUB_ACTIONS", "GITHUB_REF", "GITHUB_WORKFLOW_REF", "TF_BUILD", "GITLAB_CI", "BUILDKITE", "JENKINS_URL" })
        {
            string? value = env(name)?.Trim();
            if (!string.IsNullOrEmpty(value) && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0")
                return name;
        }

        return null;
    }

    /// <summary>Where the file's delivery first names a credential, or null when none does.</summary>
    private static string? DeliveryCredential(DeploymentFile file)
    {
        WorkspaceDelivery? workspace = file.Workspace?.Delivery;
        if (!string.IsNullOrWhiteSpace(workspace?.CredentialRef)) return "workspace.delivery.credentialRef";
        if (!string.IsNullOrWhiteSpace(workspace?.Signing?.CredentialRef)) return "workspace.delivery.signing.credentialRef";

        foreach (DeploymentQueuePlan plan in file.Resolve())
        {
            if (!string.IsNullOrWhiteSpace(plan.Delivery?.CredentialRef)) return $"queues.{plan.Definition.Name}.delivery.credentialRef";
            if (!string.IsNullOrWhiteSpace(plan.Delivery?.Signing?.CredentialRef)) return $"queues.{plan.Definition.Name}.delivery.signing.credentialRef";
        }

        return null;
    }
}
