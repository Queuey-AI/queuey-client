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
/// is the workspace's host, its environment, the workspace binding itself, and any queue that
/// overrides the host outright. The binding is dropped and the rest become variables; everything else
/// is left exactly as pulled, because substituting more would be guessing at what the operator
/// considers environment-specific.
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
    /// The variable a templated workspace environment refers to. Not <c>QUEUEY_ENV</c>, which names the Queuey
    /// deployment the CLI talks to.
    /// </summary>
    public const string EnvironmentVariable = "QUEUEY_WORKSPACE_ENVIRONMENT";

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
            Schema = file.Schema,
            // Profilene gir verdier til variablene malen skriver (F2.7), så de følger med som de står.
            Profiles = file.Profiles,
            Workspace = file.Workspace is null ? null : new DeploymentWorkspace
            {
                // Miljø-merket er det som skiller miljøene (Queuey F2.2), så det blir en variabel som vertene.
                Environment = string.IsNullOrWhiteSpace(file.Workspace.Environment) ? null : Reference(EnvironmentVariable),
                // Behaviour and ingress travel untouched — a lane strategy and a "the type is in
                // the body" rule mean the same thing in every environment.
                Ordering = file.Workspace.Ordering,
                DlqEnabled = file.Workspace.DlqEnabled,
                RetentionDays = file.Workspace.RetentionDays,
                Idempotent = file.Workspace.Idempotent,
                Backoff = file.Workspace.Backoff,
                Ingress = file.Workspace.Ingress,
                Delivery = file.Workspace.Delivery is null ? null : new WorkspaceDelivery
                {
                    BaseUrl = string.IsNullOrWhiteSpace(file.Workspace.Delivery.BaseUrl) ? null : Reference(BaseUrlVariable),
                    AuthMode = file.Workspace.Delivery.AuthMode,
                    CredentialRef = file.Workspace.Delivery.CredentialRef,
                    AuthHeaderName = file.Workspace.Delivery.AuthHeaderName,
                    Method = file.Workspace.Delivery.Method,
                    TimeoutMs = file.Workspace.Delivery.TimeoutMs,
                    Signing = file.Workspace.Delivery.Signing,
                    RateLimit = file.Workspace.Delivery.RateLimit,
                },
            },
        };

        foreach (KeyValuePair<string, DeploymentQueue> entry in file.Queues)
        {
            DeploymentQueue q = entry.Value ?? new DeploymentQueue();

            template.Queues[entry.Key] = new DeploymentQueue
            {
                Mode = q.Mode,
                Ordering = q.Ordering,
                DlqEnabled = q.DlqEnabled,
                RetentionDays = q.RetentionDays,
                Idempotent = q.Idempotent,
                Backoff = q.Backoff,
                Filter = q.Filter,
                Ingress = q.Ingress,
                Delivery = q.Delivery is null ? null : new QueueDelivery
                {
                    // Leveringstypen hører til miljøet (Queuey F2.3): en lokal lytter i dev, HTTP ellers. Den blir en variabel
                    // per kø, som en absolutt URL.
                    Kind = string.IsNullOrWhiteSpace(q.Delivery.Kind) ? null : Reference(QueueKindVariable(entry.Key, environment)),
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

    /// <summary>The variable name a queue's delivery kind is templated to: <c>http</c> or <c>localForward</c> per environment.</summary>
    public static string QueueKindVariable(string queueName, string? environment = null)
    {
        string url = QueueUrlVariable(queueName, environment);
        return url.Substring(0, url.Length - "_URL".Length) + "_DELIVERY_KIND";
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
