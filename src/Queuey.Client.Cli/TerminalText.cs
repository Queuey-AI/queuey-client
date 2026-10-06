using System.Text;
using System.Text.RegularExpressions;

namespace Queuey.Client.Cli;

// F2.7-review (2026-10-06): `events get` og `publish` skriver verdier produsenten eller serveren styrer (source, groupKey,
// holdReason, errorMessage, kønavn) til terminalen. En ANSI-sekvens der kan flytte markøren, skjule tekst eller endre vinduets
// tittel, og et linjeskift kan lage en linje som ser ut som CLI-ens egen. Menneskeutskriften går derfor gjennom dette;
// --json er data, og JSON-koderen escaper kontrolltegn selv.

/// <summary>A value from outside the CLI, made safe to write to a terminal.</summary>
internal static class TerminalText
{
    // CSI (ESC [ … final byte), OSC (ESC ] … BEL eller ESC \) og de andre to-tegns escape-sekvensene.
    private static readonly Regex Escapes = new(
        @"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)?|[@-Z\\-_])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="value"/> on one line: escape sequences, control characters and bidirectional overrides removed, and a
    /// line break or tab turned into a space.
    /// </summary>
    internal static string Line(string? value) => Clean(value, keepLineBreaks: false);

    /// <summary>As <see cref="Line"/>, keeping line breaks, for a block such as pretty-printed JSON.</summary>
    internal static string Block(string? value) => Clean(value, keepLineBreaks: true);

    /// <summary>Whether <paramref name="value"/> has a control character or a bidirectional override, which <see cref="Line"/> removes.</summary>
    internal static bool HasUnsafeCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (char c in value!)
        {
            if (char.IsControl(c) || IsBidiControl(c))
                return true;
        }
        return false;
    }

    private static string Clean(string? value, bool keepLineBreaks)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        string text = Escapes.Replace(value!, string.Empty);
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n' && keepLineBreaks)
                clean.Append(c);
            else if (c is '\n' or '\r' or '\t')
                clean.Append(keepLineBreaks && c == '\r' ? "" : " ");
            else if (!char.IsControl(c) && !IsBidiControl(c))
                clean.Append(c);
        }

        return clean.ToString();
    }

    // Tegn som snur skriveretningen og kan få tekst til å se annerledes ut enn den er (U+061C, U+200E/F, U+202A–E, U+2066–9).
    private static bool IsBidiControl(char c)
        => c is '؜' or '‎' or '‏' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');
}
