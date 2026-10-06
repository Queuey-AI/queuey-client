using System;

namespace Queuey.Client.Waas;

// F2.7-review (2026-10-06): en kommando som foreslås (`queuey verify <kø> --event <id>`), settes sammen av verdier fra
// serveren eller fra den som kaller: et kønavn, en mal, en id. Et navn med linjeskift eller skilletegn som skallet tolker,
// ville gjort forslaget til noe annet enn det ser ut som. Her står det som er trygt å sette inn.

/// <summary>The words a suggested command may hold as they are: a plain name, or a public id of a given kind.</summary>
internal static class CommandWords
{
    /// <summary>
    /// <paramref name="value"/>, trimmed, when it is a plain word: letters, digits, <c>.</c>, <c>-</c> and <c>_</c>, at most
    /// 64 characters, as a queue name or a template key is. Null otherwise, so the caller writes a placeholder.
    /// </summary>
    internal static string? Word(string? value)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed!.Length > 64)
            return null;

        foreach (char c in trimmed)
        {
            bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
            if (!allowed)
                return null;
        }

        return trimmed;
    }

    /// <summary>
    /// <paramref name="value"/> when it is a public id with <paramref name="prefix"/> (<c>que_</c>, <c>evt_</c> …): the
    /// prefix, then letters, digits, <c>_</c> and <c>-</c>, at most 64 characters. Null otherwise.
    /// </summary>
    internal static string? Id(string? value, string prefix)
    {
        if (value is null || value.Length <= prefix.Length || value.Length > 64 || !value.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        foreach (char c in value)
        {
            bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!allowed)
                return null;
        }

        return value;
    }
}
