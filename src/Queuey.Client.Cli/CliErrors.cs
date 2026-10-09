using System;
using System.Collections.Generic;
using System.Text.Json;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// How a command that failed says so. With <c>--json</c> the error is JSON on stdout — the same
/// stream the answer would have used, so a script or an agent that parses the output gets
/// <c>{"error":{"code","message","action","status"}}</c> instead of prose on stderr. Without it, the
/// message and the suggested action go to stderr.
/// </summary>
internal static class CliErrors
{
    private static readonly HashSet<string> JsonSwitch = new(StringComparer.Ordinal) { "json" };

    /// <summary>
    /// Whether a command line asks for JSON, read by the parser the commands use — so <c>--json=true</c>
    /// counts and <c>--json=false</c> does not. For errors raised before or outside a command's own parse.
    /// </summary>
    public static bool WantsJson(IEnumerable<string> args) => ArgMap.Parse(args, JsonSwitch).Has("json");

    /// <summary>How many characters of a command, subcommand or argument an error shows, at most.</summary>
    internal const int ShownCharacters = 3;

    /// <summary>
    /// A command, subcommand or argument as an error may show it: its first <see cref="ShownCharacters"/> characters, cut
    /// sooner at a character a name does not have, and "…" for the rest. A word that starts with a dash and then looks like
    /// an option name (<see cref="LooksLikeAnOptionName"/>) is shown whole. A word can be a secret pasted in the wrong place,
    /// and a secret can be letters and digits only. A word the command knows, such as a hint's, is shown whole by the caller.
    /// </summary>
    // Review 2026-10-05: `whoami --api-key:qak_… --json` skrev nøkkelen tilbake i feilen. Re-review samme dag: å kutte ved
    // første tegn et navn ikke har, var ikke nok. `edge run --mqtt-user bob FAKEpw123` viste passordet helt, og et heks-token
    // etter `whoami --json` også. Nå vises tre tegn.
    internal static string Shown(string word)
    {
        if (word.StartsWith("-", StringComparison.Ordinal) && LooksLikeAnOptionName(word.TrimStart('-')))
            return word;

        int safe = 0;
        while (safe < word.Length && safe < ShownCharacters && IsNameCharacter(word[safe]))
            safe++;
        return safe == word.Length ? word : word.Substring(0, safe) + "…";

        static bool IsNameCharacter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-';
    }

    /// <summary>
    /// Whether an option name, without its dashes, is one an error may show: short lowercase kebab-case, as every option
    /// of the CLI is (<c>^[a-z][a-z0-9-]{0,31}$</c>). Anything else may be a secret, and the error says "an unknown option".
    /// </summary>
    internal static bool LooksLikeAnOptionName(string name)
    {
        if (name.Length is 0 or > 32 || name[0] is not (>= 'a' and <= 'z'))
            return false;

        foreach (char c in name)
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                return false;
        return true;
    }

    /// <summary>
    /// Whether a value an error would show as a workspace is one: <c>ten_</c> and an id. Anything else in
    /// <c>--tenant</c> or <c>QUEUEY_TENANT</c> may be a secret put there by mistake, and the error says it is not a
    /// workspace id instead of showing it.
    /// </summary>
    // Re-review 2026-10-05: en API-nøkkel i QUEUEY_TENANT, en forveksling i CI, ble skrevet ut ved hver apply og verify.
    // F2.7: regelen er deploy-filas (WorkspaceIds i Queuey.Client.Waas), så flagget og fila sier det samme.
    internal static bool LooksLikeAWorkspaceId(string value) => WorkspaceIds.IsOne(value);

    /// <summary>A usage error (exit 2): the command line itself is wrong, and nothing was sent.</summary>
    public static int Usage(ArgMap map, string code, string message, string? action = null, IReadOnlyDictionary<string, object?>? details = null)
        => Write(map.Has("json"), code, message, action, status: null, ExitCodes.Usage, label: null, details);

    /// <summary>A configuration error (exit 3): what the command needs is missing or contradicts itself.</summary>
    public static int Configuration(ArgMap map, string code, string message, string? action = null)
        => Write(map.Has("json"), code, message, action, status: null, ExitCodes.Configuration, label: null);

    /// <summary>Writes one error, as JSON on stdout or as prose on stderr, and returns <paramref name="exitCode"/>.</summary>
    public static int Write(
        bool json, string code, string message, string? action, int? status, int exitCode, string? label = null,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        if (json)
        {
            var error = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
                ["action"] = action,
                ["status"] = status,
            };
            if (details is not null)
                foreach (KeyValuePair<string, object?> detail in details)
                    error[detail.Key] = detail.Value;

            // Én linje (gullflyten 2026-10-09): en feil kan komme midt i en strøm som leses linje for linje, som verify --json etter
            // waiting-linjen, og er fortsatt ett JSON-objekt for den som leser hele stdout.
            Console.WriteLine(JsonSerializer.Serialize(new { error }, CliHost.JsonLine));
            return exitCode;
        }

        // Meldingen og handlingen kan komme fra Queuey (detail, title, errors, eller rå tekst fra et svar), så de går gjennom
        // TerminalText, som annet serveren styrer (F2.7-regelen, re-review av #58): en ESC- eller OSC-sekvens kunne flyttet
        // markøren eller endret vinduets tittel, og et linjeskift kunne laget en linje som ser ut som CLI-ens egen. --json er
        // data, og JSON-koderen escaper kontrolltegn selv.
        string said = TerminalText.Line(message);
        Console.Error.WriteLine(label is null ? said : $"{label}: {said}");
        if (!string.IsNullOrWhiteSpace(action))
            Console.Error.WriteLine($"  → {TerminalText.Line(action)}");
        return exitCode;
    }
}

/// <summary>
/// A usage error found after the options were parsed — a value that cannot be what its option is for — thrown where the
/// value is read and written by <see cref="CliEntry"/> with exit 2, as <see cref="CliErrors.Usage"/> writes one.
/// </summary>
internal sealed class CliUsageException : Exception
{
    public CliUsageException(string code, string message, string? action)
        : base(message)
    {
        Code = code;
        Action = action;
    }

    /// <summary>The error code, such as <c>invalid_value</c>.</summary>
    public string Code { get; }

    /// <summary>What to do instead.</summary>
    public string? Action { get; }
}
