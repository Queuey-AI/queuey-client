namespace Queuey.Client.Waas;

// Queuey F2.3-review (2026-10-06): en verdi fra fila kan komme fra en ${VAR}, og da er feilen det eneste stedet den vises:
// en miljøverdi, kanskje en hemmelighet satt i feil variabel. Leveringstypen og miljø-merket gjentok hele verdien. En feil
// viser den nå som CLI-en viser et ord den ikke kjenner (CliErrors.Shown, #44): høyst de tre første tegnene.

/// <summary>A value from the deployment file as an error may show it, by the CLI's rule for a word it does not know.</summary>
internal static class ShownValue
{
    /// <summary>How many characters of a value an error shows, at most.</summary>
    internal const int Characters = 3;

    /// <summary>
    /// The first <see cref="Characters"/> characters of <paramref name="value"/>, cut sooner at a character a name does not
    /// have, and "…" for the rest. A value that short is shown whole.
    /// </summary>
    internal static string Of(string value)
    {
        int safe = 0;
        while (safe < value.Length && safe < Characters && IsNameCharacter(value[safe]))
            safe++;
        return safe == value.Length ? value : value.Substring(0, safe) + "…";

        static bool IsNameCharacter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-';
    }
}
