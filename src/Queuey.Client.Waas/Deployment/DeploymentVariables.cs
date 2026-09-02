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
/// </remarks>
public static class DeploymentVariables
{
    /// <summary>Expands every <c>${VAR}</c> in <paramref name="value"/>. Null and literal text pass through.</summary>
    /// <param name="value">The text to expand.</param>
    /// <param name="lookup">Variable resolver; defaults to the process environment.</param>
    /// <param name="context">What is being expanded, for the error message (e.g. <c>workspace.baseUrl</c>).</param>
    public static string? Expand(string? value, Func<string, string?>? lookup = null, string? context = null)
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
            sb.Append(Resolve(token, lookup, context, value));
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

    private static string Resolve(string token, Func<string, string?> lookup, string? context, string whole)
    {
        int sep = token.IndexOf(":-", StringComparison.Ordinal);
        string name = (sep < 0 ? token : token.Substring(0, sep)).Trim();
        string? fallback = sep < 0 ? null : token.Substring(sep + 2);

        if (name.Length == 0)
            throw new QueueyConfigurationException(
                $"Empty ${{}} in {context ?? "the deployment file"}: '{whole}'.");

        string? resolved = lookup(name);
        if (!string.IsNullOrEmpty(resolved))
            return resolved!;

        if (fallback is not null)
            return fallback;

        throw new QueueyConfigurationException(
            $"Environment variable '{name}' is referenced by {context ?? "the deployment file"} " +
            $"('{whole}') but is not set. Set it, or give it a default with ${{{name}:-value}}.");
    }
}
