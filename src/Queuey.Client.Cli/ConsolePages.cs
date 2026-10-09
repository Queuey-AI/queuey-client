using System;
using System.Linq;

namespace Queuey.Client.Cli;

/// <summary>
/// Links to the Queuey console, built as Queuey builds them (<c>ConsoleLinks</c> in Queuey): the console's origin from the
/// login, else Queuey's own for Queuey's hosts. Null when the console is not known, so no half link is shown.
/// </summary>
internal static class ConsolePages
{
    /// <summary>The console of Queuey's own hosts.</summary>
    internal const string Production = "https://app.queuey.ai";

    /// <summary>The console's origin for <paramref name="config"/>, or null.</summary>
    internal static string? Base(ResolvedConfig config)
    {
        if (config.Login?.Login.ConsoleBase is { } fromLogin)
            return fromLogin.TrimEnd('/');
        return string.Equals(config.ResolvedApiBase().GetLeftPart(UriPartial.Authority),
            new QueueyOptions().ResolveApiBaseAddress().GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            ? Production
            : null;
    }

    /// <summary>
    /// The page where a person makes or looks at a key: the queue's Security panel, or the workspace's Security tab, or the
    /// list of workspaces. Only public ids go in.
    /// </summary>
    internal static string? Security(ResolvedConfig config, string? tenant, string? queue)
    {
        if (Base(config) is not { } console)
            return null;
        // Bare [A-Za-z0-9_] etter prefikset (security-review av #69, K3): id-en havner i en URL og i terminalen.
        bool tenantOk = IsId(tenant, "ten_");
        bool queueOk = IsId(queue, "que_");
        return tenantOk && queueOk ? $"{console}/console/t/{tenant}/q/{queue}?panel=security"
            : tenantOk ? $"{console}/console/t/{tenant}?tab=security"
            : $"{console}/console/t";
    }

    /// <summary>
    /// The page where a person sees a queue's delivery, or the workspace's when <paramref name="queue"/> is null: where the full
    /// receiver URL a key or a login reads redacted can be read (Queuey #514). Null when the console is not known.
    /// </summary>
    internal static string? Delivery(ResolvedConfig config, string? tenant, string? queue)
    {
        if (Base(config) is not { } console || !IsId(tenant, "ten_"))
            return null;
        return IsId(queue, "que_") ? $"{console}/console/t/{tenant}/q/{queue}" : $"{console}/console/t/{tenant}";
    }

    private static bool IsId(string? value, string prefix)
        => value is { Length: > 4 and <= 64 } && value.StartsWith(prefix, StringComparison.Ordinal)
           && value.Substring(prefix.Length).All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}
