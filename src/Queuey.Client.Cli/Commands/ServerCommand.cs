using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Server.Hosting;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey server</c>: runs a self-hosted Queuey on this machine with Docker Compose, from the package the server image
/// carries for its version. The folder it works in is the package's own, so every step can also be done by hand with
/// <c>docker compose</c> as the package's README describes.
///
/// <code>
/// queuey server init     --version &lt;v&gt; [--dir &lt;path&gt;] [--email &lt;e&gt;] [--console-port …]
/// queuey server start    [--dir] [--no-test]
/// queuey server status   [--dir] [--json]
/// queuey server logs     [service …] [--dir] [--follow] [--tail n] [--since t]
/// queuey server stop     [--dir] [--delete-data [--yes]]
/// queuey server upgrade  --version &lt;v&gt; [--dir] [--no-test]
/// </code>
/// </summary>
// Selfhost 4.8 (2026-11-12). Pakken hentes ut av serverimaget for samme versjon, så CLI-en og Compose-pakken kan ikke gå i
// utakt: det finnes bare én kopi, i Queuey-repoet (deploy/compose), og den følger imaget. CLI-en eier bare filene som er
// installasjonens egne.
internal static class ServerCommand
{
    private static readonly string[] Ports = { "console-port", "api-port", "ingress-port", "idp-port" };

    internal static readonly Dictionary<string, CommandOptions> Verbs = new(StringComparer.Ordinal)
    {
        ["init"] = new("server init", values: new[] { "dir", "version", "email" }.Concat(Ports)),
        ["start"] = new("server start", flags: new[] { "no-test" }, values: new[] { "dir" }),
        ["status"] = new("server status", flags: new[] { "json" }, values: new[] { "dir" }),
        ["logs"] = new("server logs", flags: new[] { "follow", "timestamps" }, values: new[] { "dir", "tail", "since" }, positionals: 16),
        ["stop"] = new("server stop", flags: new[] { "delete-data", "yes" }, values: new[] { "dir" },
            hints: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["volumes"] = "Deleting the data is --delete-data, which asks first.",
                ["force"] = "Deleting the data is --delete-data, which asks first.",
            }),
        ["upgrade"] = new("server upgrade", flags: new[] { "no-test" }, values: new[] { "dir", "version" }),
    };

    /// <summary>Whether a person is at a terminal. A seam: the tests are never at one.</summary>
    internal static Func<bool> IsTerminal { get; set; } = () => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>Reads the person's answer. A seam, so a test can answer.</summary>
    internal static Func<string?> ReadLine { get; set; } = Console.ReadLine;

    public static async Task<int> RunAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
        if (sub is "" or "help" or "-h" or "--help")
        {
            Console.WriteLine(Usage.Text);
            return sub.Length == 0 ? ExitCodes.Usage : ExitCodes.Success;
        }

        if (!Verbs.TryGetValue(sub, out CommandOptions? options))
            return CliErrors.Write(CliErrors.WantsJson(rest), "unknown_subcommand",
                $"Unknown server subcommand '{CliErrors.Shown(sub)}'. Expected: {string.Join(" | ", Verbs.Keys)}.", action: null, status: null, ExitCodes.Usage);

        if (!options.TryParse(rest, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        // Ctrl+C stopper det som venter (logs --follow, start), og docker med det.
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            ServerFolder folder = ServerFolder.Resolve(map.Get("dir"), CliHost.Env);
            await ServerDocker.RequireComposeAsync(cancel.Token);
            // Det som endrer installasjonen, tar samme lås som Queuey.Server.Hosting, så en AppHost som starter samtidig venter.
            using FolderLock? held = sub is "init" or "start" or "stop" or "upgrade"
                ? await FolderLock.TakeAsync(folder, Console.Error.WriteLine, cancel.Token)
                : null;
            return sub switch
            {
                "init" => await InitAsync(folder, map, cancel.Token),
                "start" => await StartAsync(folder, map, cancel.Token),
                "status" => await StatusAsync(folder, map.Has("json"), cancel.Token),
                "logs" => await LogsAsync(folder, map, cancel.Token),
                "stop" => await StopAsync(folder, map, cancel.Token),
                "upgrade" => await UpgradeAsync(folder, map, cancel.Token),
                _ => ExitCodes.Usage,
            };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return ExitCodes.RuntimeError;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    // ── init ────────────────────────────────────────────────────────────

    private static async Task<int> InitAsync(ServerFolder folder, ArgMap map, CancellationToken ct)
    {
        string version = RequireVersion(map);
        LocalPorts ports = ReadPorts(map);
        string email = (map.Get("email") ?? LocalDeveloper.DefaultEmail).Trim();
        if (email.Count(c => c == '@') != 1 || email.StartsWith('@') || email.EndsWith('@'))
            return CliErrors.Usage(map, "invalid_value", $"--email takes an email address; got '{CliErrors.Shown(email)}'.");

        if (folder.HasPackage)
            return CliErrors.Usage(map, "already_initialized", $"{folder.Path} already holds an installation ({ServerFolder.ComposeFile} is there).",
                "Start it with `queuey server start`, or move to another version with `queuey server upgrade --version <version>`. " +
                "Use --dir for a second installation somewhere else.");
        if (!folder.IsMissingOrEmpty)
            return CliErrors.Usage(map, "folder_not_empty", $"{folder.Path} is not empty, and init writes only into a new or empty folder.",
                "Choose another --dir, such as a new folder for the installation.");

        await Installation.InitAsync(folder, version, ports, email, Console.Error.WriteLine, ct);

        Console.WriteLine($"Queuey {version} is set up in {folder.Path}, for this machine only.");
        Console.WriteLine($"  console  {ports.ConsoleUrl}");
        Console.WriteLine($"  API      {ports.ApiUrl}");
        Console.WriteLine($"  ingress  {ports.IngressUrl}");
        Console.WriteLine($"  sign-in  {ports.IdpUrl} (the bundled Keycloak)");
        Console.WriteLine();
        Console.WriteLine("Next: queuey server start");
        return ExitCodes.Success;
    }

    private static string RequireVersion(ArgMap map)
    {
        string? version = map.Get("version")?.Trim();
        if (string.IsNullOrEmpty(version))
            throw new CliUsageException("missing_argument", "Missing --version <version>, such as 1.0.0.",
                "The version is pinned: an installation runs the version you name until you upgrade it.");
        if (!ServerFolder.IsPinnedVersion(version))
            throw new CliUsageException("invalid_value", $"--version takes a pinned version such as 1.0.0 or 1.0.0-rc.1; got '{CliErrors.Shown(version)}'.", null);
        return version;
    }

    private static LocalPorts ReadPorts(ArgMap map)
    {
        int Port(string option, int fallback)
        {
            string? value = map.Get(option);
            if (value is null) return fallback;
            if (!int.TryParse(value, out int port) || port is < 1 or > 65535)
                throw new CliUsageException("invalid_value", $"--{option} takes a port, 1–65535; got '{CliErrors.Shown(value)}'.", null);
            return port;
        }

        var defaults = new LocalPorts();
        var ports = new LocalPorts(Port("console-port", defaults.Console), Port("api-port", defaults.Api),
            Port("ingress-port", defaults.Ingress), Port("idp-port", defaults.Idp));
        if (new[] { ports.Console, ports.Api, ports.Ingress, ports.Idp }.Distinct().Count() != 4)
            throw new CliUsageException("invalid_value", "The four ports must differ.", null);
        return ports;
    }

    // ── start ───────────────────────────────────────────────────────────

    private static async Task<int> StartAsync(ServerFolder folder, ArgMap map, CancellationToken ct)
    {
        folder.RequirePackage();
        await Installation.GuardAsync(folder, ct);
        await Installation.UpAsync(folder, Console.Error.WriteLine, ct);
        return await AfterStartAsync(folder, testDelivery: !map.Has("no-test"), ct);
    }

    private static async Task<int> AfterStartAsync(ServerFolder folder, bool testDelivery, CancellationToken ct)
    {
        LocalPorts ports = folder.Ports;
        await Installation.WaitForHealthyAsync(folder, TimeSpan.FromMinutes(4), ct);

        Console.WriteLine($"Queuey {folder.Version} is running.");
        Console.WriteLine($"  console  {ports.ConsoleUrl}");
        Console.WriteLine($"  ingress  {ports.IngressUrl}");

        if (folder.State is not { } state)
        {
            Console.WriteLine("No test delivery: this installation was not made by `queuey server init`, so the CLI has nobody to sign in as.");
            return ExitCodes.Success;
        }

        using HttpClient http = Http();
        bool isNew = LocalDeveloper.Read(folder) is null;
        DeveloperRecord developer = await LocalDeveloper.EnsureAsync(folder, state.DeveloperEmail, http, ct);

        Console.WriteLine();
        Console.WriteLine($"Sign in to the console as {developer.Email}; the password is in {folder.File(ServerFolder.DeveloperFile)}." +
                          (isNew ? " You are the installation's owner." : ""));

        if (!testDelivery)
            return ExitCodes.Success;

        Console.WriteLine();
        Console.Write("Test delivery … ");
        TestDeliveryResult result = await TestDelivery.RunAsync(ports, developer, http, AppKeyRecord.Read(folder)?.Tenant, ct);
        Console.WriteLine($"{result.EventId} went in through the ingress and reached a receiver on this machine in {result.Took.TotalMilliseconds:0} ms " +
                          $"(queue {TestDelivery.QueueName}).");
        return ExitCodes.Success;
    }

    private static HttpClient Http()
        => CliHost.TestHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    // ── status ──────────────────────────────────────────────────────────

    private static async Task<int> StatusAsync(ServerFolder folder, bool json, CancellationToken ct)
    {
        folder.RequirePackage();
        IReadOnlyList<ServiceState> services = await Installation.ServicesAsync(folder, ct);
        LocalPorts ports = folder.Ports;
        bool healthy = services.Count > 0
                       && services.Where(s => !Installation.OneShot.Contains(s.Service)).All(s => s.Healthy)
                       && services.Where(s => Installation.OneShot.Contains(s.Service)).All(s => s.Completed);
        // Etter `stop` står containerne igjen som exited; da er installasjonen stoppet, ikke halvveis.
        string state = services.All(s => !s.Running) ? "stopped" : healthy ? "running" : "degraded";

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                folder = folder.Path,
                version = folder.Version,
                state,
                madeByInit = folder.State is not null,
                addresses = folder.State is null ? null : new { console = ports.ConsoleUrl, api = ports.ApiUrl, ingress = ports.IngressUrl, signIn = ports.IdpUrl },
                services = services.Select(s => new { service = s.Service, state = s.State, health = s.Health.Length == 0 ? null : s.Health, exitCode = s.Running ? (int?)null : s.ExitCode }),
            }, CliHost.JsonOut));
        }
        else
        {
            Console.WriteLine($"Queuey {folder.Version ?? "(no QUEUEY_VERSION)"} in {folder.Path}: {state}");
            foreach (ServiceState s in services)
                Console.WriteLine($"  {s.Service,-12} {s.State}{(s.Health.Length > 0 ? $", {s.Health}" : "")}{(!s.Running ? $" (exit {s.ExitCode})" : "")}");
            if (state == "stopped")
                Console.WriteLine("  Nothing runs. Start it with `queuey server start`; the data is kept.");
            else if (folder.State is not null)
                Console.WriteLine($"  console {ports.ConsoleUrl} · ingress {ports.IngressUrl}");
        }
        return state == "running" ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    // ── logs ────────────────────────────────────────────────────────────

    private static async Task<int> LogsAsync(ServerFolder folder, ArgMap map, CancellationToken ct)
    {
        folder.RequirePackage();
        var args = new List<string> { "logs" };
        if (map.Has("follow")) args.Add("--follow");
        if (map.Has("timestamps")) args.Add("--timestamps");
        args.AddRange(new[] { "--tail", map.Get("tail") ?? "200" });
        if (map.Get("since") is { } since) args.AddRange(new[] { "--since", since });
        args.AddRange(map.Positionals);
        DockerResult logs = await ServerDocker.ComposeToTerminalAsync(folder.Path, ct, args.ToArray());
        return logs.ExitCode == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    // ── stop ────────────────────────────────────────────────────────────

    private static async Task<int> StopAsync(ServerFolder folder, ArgMap map, CancellationToken ct)
    {
        folder.RequirePackage();
        await Installation.GuardAsync(folder, ct);
        if (!map.Has("delete-data"))
        {
            DockerResult stopped = await ServerDocker.ComposeToTerminalAsync(folder.Path, ct, "stop");
            if (stopped.ExitCode != 0)
                return CliErrors.Write(false, "stop_failed", "docker compose stop failed.", null, status: null, ExitCodes.RuntimeError, "Error");
            Console.WriteLine("Stopped. The data is kept; `queuey server start` starts it again.");
            return ExitCodes.Success;
        }

        // Sletting må bes om (planen for 4.8): --delete-data, og så et ja. I en terminal skrives ordet; uten en må --yes stå.
        string what = $"every queue, event, account and secret of the installation in {folder.Path}";
        if (!map.Has("yes"))
        {
            if (!IsTerminal())
                return CliErrors.Usage(map, "confirmation_required", $"--delete-data deletes {what}, and nothing asks again without a terminal.",
                    "Add --yes to delete it. Take a backup first with `docker compose run --rm backup` in the folder, if you may want it back.");
            Console.Write($"This deletes {what}. It cannot be undone. Type delete to go on: ");
            if (ReadLine()?.Trim() != "delete")
            {
                Console.WriteLine("Nothing was deleted.");
                return ExitCodes.Usage;
            }
        }

        DockerResult down = await ServerDocker.ComposeToTerminalAsync(folder.Path, ct, "down", "--volumes", "--remove-orphans");
        if (down.ExitCode != 0)
            return CliErrors.Write(false, "delete_failed", "docker compose down --volumes failed.", null, status: null, ExitCodes.RuntimeError, "Error");
        // Personen og nøkkelen fantes bare i dataene som nå er borte.
        foreach (string gone in new[] { ServerFolder.DeveloperFile, ServerFolder.AppKeyFile })
            if (File.Exists(folder.File(gone)))
                File.Delete(folder.File(gone));
        Console.WriteLine($"Deleted the data. The folder {folder.Path} keeps its configuration; `queuey server start` makes a new, empty installation from it.");
        return ExitCodes.Success;
    }

    // ── upgrade ─────────────────────────────────────────────────────────

    private static async Task<int> UpgradeAsync(ServerFolder folder, ArgMap map, CancellationToken ct)
    {
        folder.RequirePackage();
        string version = RequireVersion(map);
        string? current = folder.Version;
        if (current == version)
            return CliErrors.Usage(map, "same_version", $"The installation already runs {version}.", "Start it with `queuey server start`.");

        await Installation.GuardAsync(folder, ct);

        // Pakken for den nye versjonen hentes før noe endres, så en versjon som ikke finnes, endrer ingenting.
        string extracted = await ServerFolder.ExtractPackageAsync(version, Console.Error.WriteLine, ct);
        try
        {
            // 1. Backup, som README-en sier: det er veien tilbake. Backup-tjenesten trenger Postgres, og compose run starter den.
            Console.Error.WriteLine($"Backing up {current ?? "the installation"} first …");
            DockerResult backup = await ServerDocker.ComposeToTerminalAsync(folder.Path, ct, "run", "--rm", "backup");
            if (backup.ExitCode != 0)
                return CliErrors.Write(false, "backup_failed", "The backup failed, so nothing was upgraded.",
                    "See the output above. The installation is as it was.", status: null, ExitCodes.RuntimeError, "Error");

            // 2. Pakkefilene byttes; installasjonens egne (.env, hemmeligheter, override, lisens, backups) står.
            IReadOnlyList<string> previous = folder.State?.PackageFiles ?? Array.Empty<string>();
            IReadOnlyList<string> files = folder.WritePackage(extracted);
            foreach (string gone in previous.Except(files, StringComparer.Ordinal))
                if (folder.Inside(gone) is { } path && File.Exists(path))
                    File.Delete(path);
            folder.SetVersion(version);
            if (folder.State is { } state)
                folder.WriteState(state with { Version = version, PackageFiles = files });
        }
        finally
        {
            ServerFolder.TryDelete(extracted);
        }

        // 3. Hva den nye versjonen gjør med databasene, og hva tilbake betyr etterpå. Endrer ingenting.
        Console.WriteLine();
        Console.WriteLine($"What {version} will do to the databases:");
        DockerResult plan = await ServerDocker.ComposeToTerminalAsync(folder.Path, ct, "run", "--rm", "migrate", "migrate", "plan");
        if (plan.ExitCode != 0)
            return CliErrors.Write(false, "plan_failed", $"migrate plan failed with {version}, so it was not started.",
                $"The package files and QUEUEY_VERSION are {version}'s now. Go back by restoring the backup in backups/ (see README.md), " +
                "or fix what the plan says and run `queuey server start`.", status: null, ExitCodes.RuntimeError, "Error");

        // 4. Start, som `start`: migreringene kjøres før serveren.
        Console.WriteLine();
        await Installation.UpAsync(folder, Console.Error.WriteLine, ct);
        return await AfterStartAsync(folder, testDelivery: !map.Has("no-test"), ct);
    }
}
