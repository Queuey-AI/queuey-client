using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Queuey.Client.Waas;

/// <summary>
/// Expands <c>${VAR}</c> references in a deployment file against the environment, so one committed
/// file can converge several workspaces.
/// </summary>
/// <remarks>
/// <para>
/// The thin-queue shape already makes most of a file environment-independent — a queue that owns only
/// <c>/orders</c> says the same thing everywhere. What is left is the handful of values that genuinely
/// differ: the workspace's host, and any queue that overrides it outright. Those become variables.
/// </para>
/// <para>
/// An unset variable is an <b>error</b>, never an empty string. Expanding <c>${WEBHOOK_HOST}</c> to
/// nothing would quietly produce a base URL of <c>https://</c> and a deploy that "succeeded" while
/// pointing at nowhere. Use <c>${VAR:-fallback}</c> when a default is genuinely intended.
/// </para>
/// <para>
/// A file may not read the CLI's own <c>QUEUEY_</c> settings, such as <c>QUEUEY_API_KEY</c>, except
/// <c>QUEUEY_TENANT</c>, <c>QUEUEY_WORKSPACE_ENVIRONMENT</c> and the <c>QUEUEY_…_URL</c> and
/// <c>QUEUEY_…_DELIVERY_KIND</c> names <c>pull --as</c> writes. What a file expands is stored in Queuey and sent with each
/// delivery, so a <c>${QUEUEY_API_KEY}</c> in a delivery URL would hand the key to the receiver. A value that starts like a
/// secret (<c>qak_</c>, <c>whsec_</c>, <c>sk_live_</c> …) is refused whatever its variable is called, a default included.
/// </para>
/// <para>
/// A receiver whose URL carries a token, such as a Slack or Zapier hook or an Azure Function's <c>?code=</c>, takes the
/// whole URL from one variable: <c>"url": "${ORDERS_HOOK_URL}"</c>. A secret sent in a header is a credential, which a file
/// names in <c>credentialRef</c> with an <c>authMode</c>: a name, never a value.
/// </para>
/// </remarks>
public static class DeploymentVariables
{
    // Herding før tag (review av #53, 2026-10-06): fila kunne lese hvilken som helst variabel, også ${QUEUEY_API_KEY} i en
    // leverings-URL, og i CI med nøkkelen i miljøet havnet den i Queuey sin konfigurasjon og hos mottakeren.
    //
    // Kenneth valgte den smale regelen samme dag: QUEUEY_ og prefiksene på verdien, ikke navn som inneholder KEY, SECRET,
    // TOKEN eller PASSWORD. Den brede regelen stengte legitime mottakere med en token i URL-en (Slack, Zapier, ?code= i Azure
    // Functions) og navn som KEYCLOAK_URL og PARTITION_KEY. En slik mottaker tar hele URL-en fra én variabel, og en hemmelighet
    // i en header er en credential (credentialRef med en authMode).
    //
    // QUEUEY_ er CLI-ens egne innstillinger: nøkkelen, lisensen, vertene, profilen og brukerfila. En fil får bare de navnene
    // Queuey sine egne verktøy skriver inn i en fil. Hele navnerommet er avvist, ikke en liste over farlige navn, så en
    // innstilling CLI-en får senere, er dekket uten at noen husker å legge den til.
    //
    // VIKTIG: CLI-ens egne innstillinger får aldri et navn som slutter på _URL eller _DELIVERY_KIND, siden de er unntatt her.
    // Derfor heter API-verten QUEUEY_API_BASE og ikke QUEUEY_API_URL. En ny innstilling med en av de endelsene ville en
    // deploy-fil kunnet lese. Testen QueueySettingsTests holder CLI-en til det.
    private static readonly string[] AllowedQueueyNames = { "QUEUEY_TENANT", "QUEUEY_WORKSPACE_ENVIRONMENT" };

    private static readonly string[] AllowedQueueySuffixes = { "_URL", "_DELIVERY_KIND" };

    /// <summary>
    /// Why a deployment file may not read <paramref name="name"/>, as the end of a sentence, or null when it may: a
    /// <c>QUEUEY_</c> setting of the CLI's own. Names are compared in upper case, since Windows reads them so.
    /// </summary>
    internal static string? RefusalOf(string name)
    {
        string upper = name.Trim().ToUpperInvariant();
        if (!upper.StartsWith("QUEUEY_", StringComparison.Ordinal))
            return null;

        bool allowed = AllowedQueueyNames.Contains(upper, StringComparer.Ordinal)
                       || AllowedQueueySuffixes.Any(s => upper.Length > "QUEUEY_".Length + s.Length && upper.EndsWith(s, StringComparison.Ordinal));
        return allowed ? null : "it is one of the CLI's own QUEUEY_ settings, such as the API key it connects with";
    }

    /// <summary>What to do instead of reading a variable <see cref="RefusalOf"/> refuses.</summary>
    internal const string RefusalAction =
        "A deployment file may use QUEUEY_TENANT, QUEUEY_WORKSPACE_ENVIRONMENT, and the QUEUEY_…_URL and " +
        "QUEUEY_…_DELIVERY_KIND names pull --as writes; give a variable of your own a name without QUEUEY_. " + SecretPattern;

    /// <summary>Where a secret goes instead of into a value a file expands.</summary>
    internal const string SecretPattern =
        "A receiver whose URL carries a token takes the whole URL from one variable, such as \"url\": \"${ORDERS_HOOK_URL}\". " +
        "A secret sent in a header is a credential (queuey credentials set), named in credentialRef with an authMode: a " +
        "name, never its value.";

    /// <summary>Expands every <c>${VAR}</c> in <paramref name="value"/>. Null and literal text pass through.</summary>
    /// <param name="value">The text to expand.</param>
    /// <param name="lookup">Variable resolver; defaults to the process environment.</param>
    /// <param name="context">What is being expanded, for the error message (e.g. <c>workspace.delivery.baseUrl</c>).</param>
    public static string? Expand(string? value, Func<string, string?>? lookup = null, string? context = null)
        => Expand(value, lookup, context, notSetHint: null);

    /// <summary>
    /// <see cref="Expand(string?, Func{string, string?}?, string?)"/> with <paramref name="notSetHint"/> added to the error for
    /// a variable that is not set: where else it could have come from, such as a deployment file's profile.
    /// </summary>
    internal static string? Expand(string? value, Func<string, string?>? lookup, string? context, string? notSetHint)
    {
        if (string.IsNullOrEmpty(value) || value!.IndexOf("${", StringComparison.Ordinal) < 0)
            return value;

        lookup ??= Environment.GetEnvironmentVariable;

        var sb = new StringBuilder(value.Length);
        int i = 0;

        while (i < value.Length)
        {
            int start = value.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(value, i, value.Length - i);
                break;
            }

            int end = value.IndexOf('}', start + 2);
            if (end < 0)
                throw new QueueyConfigurationException(
                    $"Unterminated ${{…}} in {context ?? "the deployment file"}: '{value}'. Add the closing brace.");

            sb.Append(value, i, start - i);

            string token = value.Substring(start + 2, end - start - 2);
            sb.Append(Resolve(token, lookup, context, value, notSetHint));
            i = end + 1;
        }

        return sb.ToString();
    }

    /// <summary>The variable names referenced by <paramref name="value"/>, for a dry run's report.</summary>
    public static IReadOnlyList<string> Referenced(string? value)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(value)) return names;

        int i = 0;
        while (true)
        {
            int start = value!.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0) break;
            int end = value.IndexOf('}', start + 2);
            if (end < 0) break;

            string token = value.Substring(start + 2, end - start - 2);
            int sep = token.IndexOf(":-", StringComparison.Ordinal);
            names.Add((sep < 0 ? token : token.Substring(0, sep)).Trim());
            i = end + 1;
        }

        return names;
    }

    private static string Resolve(string token, Func<string, string?> lookup, string? context, string whole, string? notSetHint)
    {
        int sep = token.IndexOf(":-", StringComparison.Ordinal);
        string name = (sep < 0 ? token : token.Substring(0, sep)).Trim();
        string? fallback = sep < 0 ? null : token.Substring(sep + 2);

        if (name.Length == 0)
            throw new QueueyConfigurationException(
                $"Empty ${{}} in {context ?? "the deployment file"}: '{whole}'.");

        // Før oppslaget: en QUEUEY_-innstilling, som nøkkelen, leses aldri, så den kan heller ikke havne i en feil eller en logg.
        if (RefusalOf(name) is { } why)
            throw new QueueyConfigurationException(
                $"${{{name}}} in {context ?? "the deployment file"} is a variable a deployment file may not read: {why}. What a " +
                "file expands is stored in Queuey, and a delivery URL sends it on. It was not read.")
            {
                SuggestedAction = RefusalAction,
            };

        string? resolved = lookup(name);
        string? value = !string.IsNullOrEmpty(resolved) ? resolved : fallback;

        // Hvilken som helst variabel kan holde en nøkkel (MY_VAR=qak_…), også en standardverdi: prefiksene på verdien fanger det
        // navneregelen ikke ser.
        if (value is not null && CredentialNameRules.HasSecretPrefix(value.Trim()))
            throw new QueueyConfigurationException(
                $"${{{name}}} in {context ?? "the deployment file"} has a value that starts like a secret (a key prefix such as " +
                "qak_ or whsec_), so it was not used. The value is not shown.")
            {
                SuggestedAction = SecretPattern,
            };

        if (value is not null)
            return value;

        throw new QueueyConfigurationException(
            $"Environment variable '{name}' is referenced by {context ?? "the deployment file"} " +
            $"('{whole}') but is not set. Set it, or give it a default with ${{{name}:-value}}." +
            (notSetHint is null ? "" : " " + notSetHint));
    }
}
