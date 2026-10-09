using System;

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
        bool tenantOk = tenant is not null && Queuey.Client.Waas.WorkspaceIds.IsOne(tenant);
        bool queueOk = queue is not null && queue.StartsWith("que_", StringComparison.Ordinal) && queue.Length <= 64
                       && queue.Substring(4).IndexOfAny(new[] { '/', '?', '#', '&' }) < 0;
        return tenantOk && queueOk ? $"{console}/console/t/{tenant}/q/{queue}?panel=security"
            : tenantOk ? $"{console}/console/t/{tenant}?tab=security"
            : $"{console}/console/t";
    }
}
