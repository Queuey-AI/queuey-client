using System;
using System.Linq;
using System.Text.Json;

namespace Queuey.Client.Cli;

/// <summary>
/// How a command that failed says so. With <c>--json</c> the error is JSON on stdout — the same
/// stream the answer would have used, so a script or an agent that parses the output gets
/// <c>{"error":{"code","message","action"}}</c> instead of prose on stderr. Without it, the message
/// and the suggested action go to stderr as before.
/// </summary>
internal static class CliErrors
{
    public static int Write(string[] args, string code, string message, string? action, int? status, int exitCode, string label)
    {
        if (args.Contains("--json", StringComparer.Ordinal))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                error = new { code, message, action, status },
            }, CliHost.JsonOut));
            return exitCode;
        }

        Console.Error.WriteLine($"{label}: {message}");
        if (!string.IsNullOrWhiteSpace(action))
            Console.Error.WriteLine($"  → {action}");
        return exitCode;
    }
}
