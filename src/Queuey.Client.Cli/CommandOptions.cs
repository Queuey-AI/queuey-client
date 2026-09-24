using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Cli;

/// <summary>
/// The options one command accepts, declared by the command itself. An option it does not declare
/// fails the command with exit 2, the option's name and the options the command does accept — as
/// JSON on stdout with <c>--json</c>.
/// </summary>
internal sealed class CommandOptions
{
    // Før 2026-09-24 ble et ukjent valg ignorert. `apply --plan` i en CLI fra før --plan ble da en ekte
    // apply, og det samme ble en skrivefeil som --paln. Et sikkerhetsnett som blir en skriving når det
    // staves feil, er verre enn ingen.

    /// <summary>The connection options every command takes: GLOBAL OPTIONS in the usage text.</summary>
    public static readonly IReadOnlyList<string> Global = new[]
    {
        "api-base", "ingress-base", "api-key", "tenant", "license", "source", "config",
    };

    // Valg som ikke finnes lenger, men som noen kan ha i et skript: de avvises, og sier hva som gjelder.
    private static readonly IReadOnlyDictionary<string, string> GlobalHints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["env"] = "--env is not an option: production is the only built-in environment. Point at another " +
                  "instance with --api-base and --ingress-base.",
    };

    private readonly string[] _own;
    private readonly HashSet<string> _values;

    /// <param name="command">The command as typed after <c>queuey</c>, e.g. <c>apply</c> or <c>edge status</c>.</param>
    /// <param name="flags">Switches: present or not, and never followed by a value.</param>
    /// <param name="values">Options that take a value. The global options are added to every command.</param>
    /// <param name="positionals">How many arguments the command takes besides options.</param>
    /// <param name="hints">What to say about a word the command used to take or someone might expect, by name.</param>
    public CommandOptions(
        string command,
        IEnumerable<string>? flags = null,
        IEnumerable<string>? values = null,
        int positionals = 0,
        IReadOnlyDictionary<string, string>? hints = null)
    {
        Command = command;
        string[] ownValues = (values ?? Array.Empty<string>()).ToArray();
        string[] ownFlags = (flags ?? Array.Empty<string>()).ToArray();
        _own = ownValues.Concat(ownFlags).ToArray();
        Flags = new HashSet<string>(ownFlags, StringComparer.Ordinal) { "help", "h" };
        _values = new HashSet<string>(ownValues.Concat(Global), StringComparer.Ordinal);
        Positionals = positionals;
        Hints = hints ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>The command, as the error messages name it.</summary>
    public string Command { get; }

    /// <summary>The switches, <c>--help</c> and <c>-h</c> included.</summary>
    public ISet<string> Flags { get; }

    /// <summary>How many arguments the command takes besides options.</summary>
    public int Positionals { get; }

    /// <summary>What to say about a particular unknown word, by name.</summary>
    public IReadOnlyDictionary<string, string> Hints { get; }

    /// <summary>Whether the command takes <paramref name="option"/> (without dashes).</summary>
    public bool Accepts(string option) => Flags.Contains(option) || _values.Contains(option);

    /// <summary>Every option the command accepts, as it is typed: its own first, then the global ones.</summary>
    public IReadOnlyList<string> Valid => _own.Concat(new[] { "help" }).Concat(Global).Select(o => "--" + o).ToArray();

    /// <summary>
    /// Parses <paramref name="args"/>, or writes why it cannot — unknown option, a switch given a
    /// value, an argument too many — and returns false with the exit code to return.
    /// </summary>
    public bool TryParse(IEnumerable<string> args, out ArgMap map, out int exitCode)
    {
        map = ArgMap.Parse(args, Flags);
        exitCode = ExitCodes.Success;

        if (map.Keys.FirstOrDefault(k => !Flags.Contains(k) && !_values.Contains(k)) is { } unknown)
        {
            string? hint = Hints.TryGetValue(unknown, out string? own) ? own : GlobalHints.TryGetValue(unknown, out string? global) ? global : null;
            exitCode = CliErrors.Usage(map, "unknown_option",
                $"Unknown option --{unknown} for queuey {Command}.",
                (hint is null ? "" : hint + " ") + ValidOptionsSentence(),
                new Dictionary<string, object?> { ["option"] = "--" + unknown, ["validOptions"] = Valid });
            return false;
        }

        if (map.BadSwitches.FirstOrDefault() is { } badSwitch)
        {
            exitCode = CliErrors.Usage(map, "invalid_option_value",
                $"--{badSwitch} is a switch for queuey {Command}: give it alone, or as --{badSwitch}=true or --{badSwitch}=false.");
            return false;
        }

        if (map.Positionals.Count > Positionals)
        {
            string extra = map.Positionals[Positionals];
            string? hint = Hints.TryGetValue(extra, out string? own) ? own : null;
            exitCode = CliErrors.Usage(map, "unexpected_argument",
                $"Unexpected argument '{extra}' for queuey {Command}.",
                (hint is null ? "" : hint + " ") + (Positionals == 0
                    ? $"queuey {Command} takes options only. {ValidOptionsSentence()}"
                    : $"queuey {Command} takes {Positionals} argument{(Positionals == 1 ? "" : "s")} besides its options. See `queuey --help`."));
            return false;
        }

        return true;
    }

    private string ValidOptionsSentence()
        => $"Valid options for queuey {Command}: {string.Join(", ", _own.Concat(new[] { "help" }).Select(o => "--" + o))}; " +
           $"and on every command: {string.Join(", ", Global.Select(o => "--" + o))}.";
}
