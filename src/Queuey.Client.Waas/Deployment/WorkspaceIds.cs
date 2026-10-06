using System;

namespace Queuey.Client.Waas;

// F2.7 (2026-10-06): regelen sto i CLI-en (CliErrors), og deploy-fila sjekket den bare når --tenant eller QUEUEY_TENANT
// navnga et annet workspace. Nå står den her, der fila sjekkes, og CLI-en spør den samme regelen.

/// <summary>What a workspace id looks like: <c>ten_</c> and an opaque id, no more than 64 characters in all.</summary>
internal static class WorkspaceIds
{
    /// <summary>
    /// True when <paramref name="value"/> is shaped like a workspace id. Anything else in a place that takes one may be a
    /// secret put there by mistake, such as an API key, so an error says it is not a workspace id instead of showing it.
    /// </summary>
    internal static bool IsOne(string? value)
    {
        if (value is null || value.Length <= 4 || value.Length > 64 || !value.StartsWith("ten_", StringComparison.Ordinal))
            return false;

        foreach (char c in value)
        {
            bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!allowed)
                return false;
        }

        return true;
    }
}
