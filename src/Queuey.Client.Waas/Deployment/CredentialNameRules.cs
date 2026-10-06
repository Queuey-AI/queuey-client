using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas;

// Queuey F2.3-review (2026-10-06): et navn på en credential som ikke er lagret ennå, lagres av Queuey og vises tilbake, i
// lesingen, i planen, i oppsettsvurderingen og i kommandoene de foreslår. Før sjekket ingen formen, så
// "x --from-env A; curl … | sh; #" eller et navn med linjeskift ble limt rett inn i en shell-kommando eller i tekst en agent
// leser. Reglene her er Queuey sine (Queuey.SharedKernel.Security.CredentialNameRules), med de samme testvektorene: formen
// som er trygg uten quoting i sh, zsh, PowerShell og cmd, og anslaget som holder en limt inn hemmelighet ute. Skrevet uten
// API-er netstandard2.0 mangler.

/// <summary>
/// The rules for the credential a signed request names (<c>ingress.signedRequest.credentialRef</c>): the conservative shape
/// a name must have, the guesses that keep a pasted secret out of it, and the one way the CLI puts a name Queuey reports into
/// text or a suggested command (<see cref="Showable"/>). The same rules as Queuey's.
/// </summary>
internal static class CredentialNameRules
{
    /// <summary>The longest a name can be: the longest a credential's name can be.</summary>
    internal const int MaxLength = 200;

    /// <summary>The shape of a name as a regular expression, the one Queuey and the deployment file's schema publish.</summary>
    internal const string PendingNamePattern = "^[A-Za-z0-9][A-Za-z0-9._:@/-]{0,199}$";

    private static readonly string[] SecretPrefixes = { "whsec_", "sk_live_", "sk_test_", "rk_live_", "rk_test_", "qak_" };

    private static readonly char[] Separators = { '.', '_', ':', '@', '/', '-' };

    /// <summary>
    /// True when <paramref name="name"/> starts with a letter or digit and holds only letters, digits and <c>. _ : @ / -</c>,
    /// at most <see cref="MaxLength"/> characters: inert in a shell command and in text.
    /// </summary>
    internal static bool FitsPendingShape(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > MaxLength || !IsAsciiLetterOrDigit(name[0]))
            return false;

        foreach (char c in name)
        {
            if (!IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != ':' && c != '@' && c != '/' && c != '-')
                return false;
        }

        return true;
    }

    /// <summary>True when a reference starts like a provider's or Queuey's secret, or looks random (<see cref="LooksRandom"/>).</summary>
    internal static bool LooksLikeASecret(string reference) => HasSecretPrefix(reference) || LooksRandom(reference);

    /// <summary>True when a reference starts like a provider's or Queuey's secret (<c>whsec_</c>, <c>sk_live_</c>, <c>qak_</c> …).</summary>
    internal static bool HasSecretPrefix(string reference)
        => SecretPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a value looks like a random secret without a known prefix: a long stretch of hex (raw hex, a UUID such as a
    /// Polar signing key, an Azure key such as Suunto's), or a long stretch as varied as random text that switches between
    /// lower case, upper case and digits more often than words do (base64, generated passwords). A cheap guess, not a scanner.
    /// </summary>
    internal static bool LooksRandom(string value)
    {
        string[] runs = value.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        string core = string.Concat(runs);

        if (core.Length >= 24 && core.All(IsAsciiHexDigit))
            return true;

        foreach (string run in runs)
        {
            if (run.Length >= 20 && (run.All(IsAsciiHexDigit) || ReadsAsRandom(run, minBitsPerChar: 4.0)))
                return true;
        }

        return core.Length >= 32 && ReadsAsRandom(core, minBitsPerChar: 4.4);
    }

    /// <summary>
    /// The name to put into text or a suggested command, or null when it should be left out: it does not fit the shape, or
    /// it looks like a secret. Every warning that shows a name Queuey reports goes through this.
    /// </summary>
    internal static string? Showable(string? name)
    {
        string? trimmed = name?.Trim();
        return FitsPendingShape(trimmed) && !LooksLikeASecret(trimmed!) ? trimmed : null;
    }

    private static bool ReadsAsRandom(string text, double minBitsPerChar)
    {
        var counts = new Dictionary<char, int>();
        foreach (char c in text)
            counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;

        double bits = 0;
        foreach (int n in counts.Values)
        {
            double p = (double)n / text.Length;
            bits -= p * Log2(p);
        }

        if (bits < minBitsPerChar)
            return false;

        int words = 1;
        for (int i = 1; i < text.Length; i++)
        {
            char a = text[i - 1], b = text[i];
            if ((IsLower(a) && IsUpper(b)) || (IsLetter(a) && IsDigit(b)) || (IsDigit(a) && IsLetter(b)))
                words++;
        }

        return (double)text.Length / words < 4.0;
    }

    // Math.Log2 som Queuey, så de to sidene dømmer likt rett på terskelen (review av F2.3, 2026-10-06): Math.Log(p, 2) kan
    // avvike i siste bit og havne på den andre siden. netstandard2.0 mangler Math.Log2, og der står Math.Log(p, 2); CLI-en
    // og net8-forbrukere bruker net8-bygget.
    private static double Log2(double value)
#if NET5_0_OR_GREATER
        => Math.Log2(value);
#else
        => Math.Log(value, 2);
#endif

    private static bool IsLower(char c) => c >= 'a' && c <= 'z';
    private static bool IsUpper(char c) => c >= 'A' && c <= 'Z';
    private static bool IsLetter(char c) => IsLower(c) || IsUpper(c);
    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsAsciiLetterOrDigit(char c) => IsLetter(c) || IsDigit(c);
    private static bool IsAsciiHexDigit(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
