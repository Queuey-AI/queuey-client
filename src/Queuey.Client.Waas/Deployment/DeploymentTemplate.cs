using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Queuey.Client.Waas;

/// <summary>
/// Turns a pulled workspace into a file that fits <b>every</b> environment, by replacing the handful
/// of values that genuinely differ with <c>${VAR}</c> references.
/// </summary>
/// <remarks>
/// Most of a pulled file is already portable, and that is the payoff of the thin-queue shape: a queue
/// that owns only <c>/orders</c> says the same thing in staging and production. What does not travel
/// is the workspace's host, the workspace binding itself, and any queue that overrides the host
/// outright. Those three become variables; everything else is left exactly as pulled, because
/// substituting more would be guessing at what the operator considers environment-specific.
/// <para>
/// Credential names travel as they are — that they are names and not ids is precisely what makes them
/// portable.
/// </para>
/// </remarks>
public static class DeploymentTemplate
{
    /// <summary>The variable a templated workspace base URL refers to.</summary>
    public const string BaseUrlVariable = "QUEUEY_BASE_URL";

    /// <summary>
    /// Rewrites <paramref name="file"/> as a portable template. The workspace binding is dropped (a
    /// deploy supplies it from its own connection config), and absolute URLs become variables.
    /// </summary>
    /// <param name="file">The pulled file.</param>
    /// <param name="environment">
    /// A label used only to name the per-queue variables, so two environments' variables cannot be
    /// confused for one another when both are set in the same shell.
    /// </param>
    public static DeploymentFile ToTemplate(DeploymentFile file, string? environment = null)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));

        var template = new DeploymentFile
        {
            // Dropped on purpose: the workspace is what differs between environments, and a deploy
            // already knows its own from --tenant / QUEUEY_TENANT / queuey.json.
            Tenant = null,
            Workspace = file.Workspace is null ? null : new WorkspaceDelivery
            {
                BaseUrl = string.IsNullOrWhiteSpace(file.Workspace.BaseUrl) ? null : Reference(BaseUrlVariable),
                AuthMode = file.Workspace.AuthMode,
                CredentialRef = file.Workspace.CredentialRef,
                AuthHeaderName = file.Workspace.AuthHeaderName,
                Method = file.Workspace.Method,
                TimeoutMs = file.Workspace.TimeoutMs,
                Signing = file.Workspace.Signing,
                RateLimit = file.Workspace.RateLimit,
            },
        };

        foreach (KeyValuePair<string, DeploymentQueue> entry in file.Queues)
        {
            DeploymentQueue q = entry.Value ?? new DeploymentQueue();

            template.Queues[entry.Key] = new DeploymentQueue
            {
                Ordering = q.Ordering,
                MaxAttempts = q.MaxAttempts,
                DlqEnabled = q.DlqEnabled,
                DlqAfterAttempts = q.DlqAfterAttempts,
                RetentionDays = q.RetentionDays,
                Idempotent = q.Idempotent,
                Delivery = q.Delivery is null ? null : new QueueDelivery
                {
                    // A relative path is already portable — leave it. An absolute URL pins a host, so
                    // it becomes a variable named after the queue it belongs to.
                    Url = IsAbsolute(q.Delivery.Url)
                        ? Reference(QueueUrlVariable(entry.Key, environment))
                        : q.Delivery.Url,
                    Inherit = q.Delivery.Inherit,
                    AuthMode = q.Delivery.AuthMode,
                    CredentialRef = q.Delivery.CredentialRef,
                    AuthHeaderName = q.Delivery.AuthHeaderName,
                    TimeoutMs = q.Delivery.TimeoutMs,
                    Signing = q.Delivery.Signing,
                    RateLimit = q.Delivery.RateLimit,
                },
            };
        }

        return template;
    }

    /// <summary>The variable name a queue's absolute URL is templated to.</summary>
    public static string QueueUrlVariable(string queueName, string? environment = null)
    {
        var sb = new StringBuilder("QUEUEY_");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            sb.Append(Sanitize(environment!));
            sb.Append('_');
        }

        sb.Append(Sanitize(queueName));
        sb.Append("_URL");
        return sb.ToString();
    }

    private static string Reference(string variable) => "${" + variable + "}";

    private static bool IsAbsolute(string? url)
        => url != null
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Upper-cases and replaces anything an environment variable name cannot carry.</summary>
    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        return sb.ToString();
    }
}
