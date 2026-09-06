using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Queuey.Client;

/// <summary>
/// The naming contract for Queuey queues and streams — enforced client-side so a bad name fails
/// locally (at startup, or in a network-free dry run) instead of as an opaque server error midway
/// through a deploy.
/// </summary>
/// <remarks>
/// <para>
/// A name lives in the ingress URL (<c>/events/{tenant}/{queue}</c>) and in integration code, so the
/// backend pins it: lowercase, starts with a letter or digit, then letters, digits, <c>.</c>,
/// <c>-</c> or <c>_</c>, at most <see cref="MaxLength"/> characters, and not a reserved routing
/// segment. These rules mirror the server's validator byte for byte — when they drift, the server
/// wins and this class must be updated.
/// </para>
/// <para>
/// <see cref="Normalize"/> exists for the one case where there is no user-authored string to honor:
/// deriving a name from a CLR type (<c>OrderCreated</c> → <c>order-created</c>). A name the caller
/// wrote themselves is never rewritten — it is validated, and an invalid one throws with the
/// normalized form as a suggestion.
/// </para>
/// </remarks>
public static class QueueyName
{
    /// <summary>The maximum length of a queue or stream name.</summary>
    public const int MaxLength = 64;

    // Mirrors the server-side validator: first character is a lowercase letter or digit, the rest
    // may also include '.', '-' and '_'.
    private static readonly Regex Pattern =
        new Regex(@"^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);

    // Names that collide with a segment the ingress router treats specially — a queue called
    // "sandbox" would be unreachable on the sandbox route.
    private static readonly string[] Reserved = { "sandbox" };

    /// <summary>Whether <paramref name="value"/> is a valid Queuey queue/stream name.</summary>
    public static bool IsValid(string? value) => Validate(value) is null;

    /// <summary>
    /// Validates a name. Returns <c>null</c> when it is valid, otherwise a human-readable reason
    /// suitable for an error message. Surrounding whitespace is ignored (the server trims too).
    /// </summary>
    public static string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "a name is required.";

        string candidate = value!.Trim();

        if (candidate.Length > MaxLength)
            return $"names must be at most {MaxLength} characters; got {candidate.Length}.";

        if (!Pattern.IsMatch(candidate))
            return "names must be lowercase and contain only letters, digits, '.', '-' or '_', " +
                   "and must start with a letter or digit.";

        if (IsReserved(candidate))
            return $"'{candidate}' is a reserved name.";

        return null;
    }

    /// <summary>
    /// Throws a <see cref="QueueyConfigurationException"/> when <paramref name="value"/> is not a
    /// valid name, quoting the offending value and — when one exists — suggesting the normalized form.
    /// </summary>
    /// <param name="value">The name to check.</param>
    /// <param name="what">What is being named, for the message (e.g. <c>"stream name"</c>).</param>
    public static void EnsureValid(string? value, string what = "name")
    {
        if (IsValid(value))
            return;

        throw new QueueyConfigurationException($"Invalid Queuey {what} '{value}': {Hint(value)}");
    }

    /// <summary>
    /// A one-line explanation of why <paramref name="value"/> is not a valid name — the rule it broke,
    /// plus the normalized form as a suggestion when one exists. Empty when the name is valid.
    /// Use it to enrich an error raised elsewhere (e.g. a publish that 404s on a mistyped queue).
    /// </summary>
    public static string Hint(string? value)
    {
        string? reason = Validate(value);
        return reason is null ? string.Empty : reason + Suggestion(value);
    }

    /// <summary>
    /// Derives a valid name from an arbitrary string — used for the convention fallback, where a
    /// stream or queue takes its name from a CLR type. PascalCase and acronym boundaries become
    /// hyphens (<c>OrderCreated</c> → <c>order-created</c>, <c>HTTPOrderCreated</c> →
    /// <c>http-order-created</c>), anything outside the allowed character set becomes a separator,
    /// runs of separators collapse, and the result is lowercased, trimmed and truncated to
    /// <see cref="MaxLength"/>.
    /// </summary>
    /// <returns>
    /// The normalized name, or an empty string when nothing usable remains. Normalization is
    /// best-effort: it can still yield a reserved name (a type called <c>Sandbox</c>), so callers
    /// validate the result rather than assuming it.
    /// </returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string source = value!.Trim();

        // Generic type names arrive as "Envelope`1" — drop the arity marker.
        int arity = source.IndexOf('`');
        if (arity >= 0)
            source = source.Substring(0, arity);

        var sb = new StringBuilder(source.Length + 8);

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            if (IsUpperAscii(c))
            {
                // A new word starts after a lowercase letter or digit ("orderCreated"), and at the
                // tail of an acronym followed by a word ("HTTPOrder" → "http-order").
                bool afterWord = i > 0 && (IsLowerAscii(source[i - 1]) || IsDigit(source[i - 1]));
                bool acronymTail = i > 0 && IsUpperAscii(source[i - 1])
                                         && i + 1 < source.Length && IsLowerAscii(source[i + 1]);
                if (afterWord || acronymTail)
                    AppendSeparator(sb);

                sb.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (IsAllowedBody(c))
            {
                if (IsSeparator(c))
                    AppendSeparator(sb, c);
                else
                    sb.Append(c);
                continue;
            }

            // Whitespace, '/', '+', non-ASCII letters, … all read as a word break.
            AppendSeparator(sb);
        }

        string result = TrimSeparators(sb.ToString());

        if (result.Length > MaxLength)
            result = TrimSeparators(result.Substring(0, MaxLength));

        return result;
    }

    private static bool IsReserved(string candidate)
    {
        for (int i = 0; i < Reserved.Length; i++)
        {
            if (string.Equals(Reserved[i], candidate, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>" Did you mean 'x'?" — omitted when normalizing yields nothing usable, something
    /// still invalid, or the value the caller already wrote.</summary>
    private static string Suggestion(string? value)
    {
        string normalized = Normalize(value);
        if (normalized.Length == 0 || !IsValid(normalized))
            return string.Empty;

        if (string.Equals(normalized, value?.Trim(), StringComparison.Ordinal))
            return string.Empty;

        return $" Did you mean '{normalized}'?";
    }

    // Never lead with a separator, and never emit two in a row — so "  Order / Created " and
    // "Order--Created" both land on "order-created".
    private static void AppendSeparator(StringBuilder sb, char separator = '-')
    {
        if (sb.Length == 0 || IsSeparator(sb[sb.Length - 1]))
            return;

        sb.Append(separator);
    }

    private static string TrimSeparators(string value) => value.Trim('-', '.', '_');

    private static bool IsSeparator(char c) => c == '-' || c == '.' || c == '_';

    private static bool IsAllowedBody(char c) => IsLowerAscii(c) || IsDigit(c) || IsSeparator(c);

    private static bool IsUpperAscii(char c) => c >= 'A' && c <= 'Z';

    private static bool IsLowerAscii(char c) => c >= 'a' && c <= 'z';

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
