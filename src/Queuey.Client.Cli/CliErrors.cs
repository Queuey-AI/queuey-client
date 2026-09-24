using System;
using System.Collections.Generic;
using System.Text.Json;

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

            Console.WriteLine(JsonSerializer.Serialize(new { error }, CliHost.JsonOut));
            return exitCode;
        }

        Console.Error.WriteLine(label is null ? message : $"{label}: {message}");
        if (!string.IsNullOrWhiteSpace(action))
            Console.Error.WriteLine($"  → {action}");
        return exitCode;
    }
}
