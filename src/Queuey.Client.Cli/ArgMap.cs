using System;
using System.Collections.Generic;

namespace Queuey.Client.Cli;

/// <summary>
/// A tiny, dependency-free argument parser: <c>--key value</c>, <c>--key=value</c>, boolean <c>--flag</c>,
/// and positionals. Boolean flag names (passed in) never consume the following token as a value.
/// Which options a command accepts is <see cref="CommandOptions"/>' business, not this parser's.
/// </summary>
internal sealed class ArgMap
{
    private readonly Dictionary<string, string?> _options;
    private readonly List<string> _positionals;

    private ArgMap(Dictionary<string, string?> options, List<string> positionals, List<string> keys, List<string> badSwitches)
    {
        _options = options;
        _positionals = positionals;
        Keys = keys;
        BadSwitches = badSwitches;
    }

    /// <summary>Every option name as it appeared, in order, including one given twice or as <c>=false</c>.</summary>
    internal IReadOnlyList<string> Keys { get; }

    /// <summary>Boolean flags given a value other than <c>true</c> or <c>false</c>, e.g. <c>--json=yes</c>.</summary>
    internal IReadOnlyList<string> BadSwitches { get; }

    /// <summary>Positional arguments, in order.</summary>
    public IReadOnlyList<string> Positionals => _positionals;

    /// <summary>The first positional, or null.</summary>
    public string? FirstPositional => _positionals.Count > 0 ? _positionals[0] : null;

    /// <summary>True when the option/flag was present (with or without a value).</summary>
    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>The option's value, or null when absent or valueless.</summary>
    public string? Get(string name) => _options.TryGetValue(name, out string? v) ? v : null;

    public static ArgMap Parse(IEnumerable<string> args, ISet<string> booleanFlags)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positionals = new List<string>();
        var keys = new List<string>();
        var badSwitches = new List<string>();
        var list = args as IList<string> ?? new List<string>(args);

        for (int i = 0; i < list.Count; i++)
        {
            string token = list[i];

            if (!IsOption(token))
            {
                positionals.Add(token);
                continue;
            }

            string key = token.TrimStart('-');

            int eq = key.IndexOf('=');
            if (eq >= 0)
            {
                string name = key.Substring(0, eq);
                string value = key.Substring(eq + 1);
                keys.Add(name);

                if (booleanFlags.Contains(name))
                {
                    // Et flagg med verdi betyr det verdien sier: --json=false gir ikke JSON. Før 2026-09-24 var
                    // flagget satt uansett verdi. En annen verdi enn true og false er en feil kommandoen melder.
                    if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        options.Remove(name);
                        continue;
                    }

                    if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                        badSwitches.Add(name);
                    options[name] = null;
                    continue;
                }

                options[name] = value;
                continue;
            }

            keys.Add(key);

            if (booleanFlags.Contains(key))
            {
                options[key] = null; // present ⇒ true
                continue;
            }

            // Value option: consume the next token unless it is itself an option.
            if (i + 1 < list.Count && !IsOption(list[i + 1]))
                options[key] = list[++i];
            else
                options[key] = null;
        }

        return new ArgMap(options, positionals, keys, badSwitches);
    }

    // A leading '-' marks an option, except a bare "-" or a negative number.
    private static bool IsOption(string token)
        => token.StartsWith("-", StringComparison.Ordinal) && token.Length > 1 && !char.IsDigit(token[1]);
}
