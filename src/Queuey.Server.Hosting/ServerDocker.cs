using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Queuey.Client;

namespace Queuey.Server.Hosting;

/// <summary>One call to the <c>docker</c> command: its arguments, the folder it runs in, and whether its output goes straight to the terminal.</summary>
/// <param name="Args">The arguments after <c>docker</c>.</param>
/// <param name="WorkingDirectory">The installation folder, for <c>docker compose</c>; null for a plain <c>docker</c> call.</param>
/// <param name="ToTerminal">True when a person should see the output as it comes, as with <c>logs --follow</c>. Nothing is captured then.</param>
/// <param name="Lines">Each line of output as it comes, besides capturing it, as <c>compose up</c>'s progress for a log. Null captures only.</param>
internal sealed record DockerCall(IReadOnlyList<string> Args, string? WorkingDirectory, bool ToTerminal, Action<string>? Lines = null);

/// <summary>What a <see cref="DockerCall"/> ended with. Stdout and stderr are empty for a call to the terminal.</summary>
internal sealed record DockerResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs <c>docker</c> for <c>queuey server</c> and <see cref="QueueyServer"/>. The CLI never talks to the Docker API itself: it runs the same commands an
/// operator would, in the installation folder, so what it does can be repeated by hand.
/// </summary>
internal static class ServerDocker
{
    /// <summary>The process runner. A seam: the tests answer each call themselves and start no process.</summary>
    internal static Func<DockerCall, CancellationToken, Task<DockerResult>> Runner { get; set; } = RunProcessAsync;

    /// <summary><c>docker compose &lt;args&gt;</c> in the installation folder, with its output captured.</summary>
    public static Task<DockerResult> ComposeAsync(string folder, CancellationToken ct, params string[] args)
        => Runner(new DockerCall(new[] { "compose" }.Concat(args).ToArray(), folder, ToTerminal: false), ct);

    /// <summary><c>docker compose &lt;args&gt;</c> in the installation folder, with its output on the terminal.</summary>
    public static Task<DockerResult> ComposeToTerminalAsync(string folder, CancellationToken ct, params string[] args)
        => Runner(new DockerCall(new[] { "compose" }.Concat(args).ToArray(), folder, ToTerminal: true), ct);

    /// <summary>
    /// <c>docker compose &lt;args&gt;</c> in the installation folder, each line of output to <paramref name="log"/> as it comes
    /// (when there is one) and captured as well.
    /// </summary>
    public static Task<DockerResult> ComposeLoggedAsync(string folder, Action<string>? log, CancellationToken ct, params string[] args)
        => Runner(new DockerCall(new[] { "compose" }.Concat(args).ToArray(), folder, ToTerminal: false, log), ct);

    /// <summary><c>docker &lt;args&gt;</c>, with its output captured.</summary>
    public static Task<DockerResult> DockerAsync(CancellationToken ct, params string[] args)
        => Runner(new DockerCall(args, null, ToTerminal: false), ct);

    /// <summary>
    /// Throws when Docker with Compose v2 cannot be run here, with what to do. Every verb checks this first, so a missing
    /// Docker is one clear message and not a failure halfway through.
    /// </summary>
    public static async Task RequireComposeAsync(CancellationToken ct)
    {
        DockerResult result;
        try
        {
            result = await DockerAsync(ct, "compose", "version", "--short");
        }
        catch (Win32Exception)
        {
            throw Missing("The docker command was not found.");
        }

        if (result.ExitCode != 0)
            throw Missing("docker compose did not answer" + (Detail(result) is { } detail ? $": {detail}" : "."));

        static QueueyConfigurationException Missing(string message) => new(message + " A local Queuey runs with Docker Compose.")
        {
            SuggestedAction = "Install Docker Desktop, or Docker Engine with the Compose plugin (v2), and start it.",
        };
    }

    /// <summary>The first line of what a failed call said on stderr, else on stdout, for an error message.</summary>
    public static string? Detail(DockerResult result)
    {
        string text = !string.IsNullOrWhiteSpace(result.Stderr) ? result.Stderr : result.Stdout;
        string? line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return line is null ? null : Text.Line(line);
    }

    private static async Task<DockerResult> RunProcessAsync(DockerCall call, CancellationToken ct)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = !call.ToTerminal,
            RedirectStandardError = !call.ToTerminal,
        };
        foreach (string arg in call.Args)
            start.ArgumentList.Add(arg);
        if (call.WorkingDirectory is not null)
            start.WorkingDirectory = call.WorkingDirectory;

        using var process = Process.Start(start) ?? throw new Win32Exception("docker did not start.");
        Task<string> stdout = call.ToTerminal ? Task.FromResult("") : ReadAsync(process.StandardOutput, call.Lines);
        Task<string> stderr = call.ToTerminal ? Task.FromResult("") : ReadAsync(process.StandardError, call.Lines);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C under `logs --follow` skal stoppe docker også, ikke la den gå videre i bakgrunnen.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return new DockerResult(process.ExitCode, await stdout, await stderr);
    }

    // Hele utskriften fanges; med en mottaker får den også hver linje i det den kommer.
    private static async Task<string> ReadAsync(System.IO.StreamReader reader, Action<string>? lines)
    {
        if (lines is null)
            return await reader.ReadToEndAsync();
        var all = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            all.AppendLine(line);
            lines(Text.Line(line));
        }
        return all.ToString();
    }
}
