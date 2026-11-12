using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client.Cli.Server;

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
            ServerFolder folder = ServerFolder.Resolve(map);
            await ServerDocker.RequireComposeAsync(cancel.Token);
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
        string version = ServerFolder.RequireVersion(map.Get("version"));
        LocalPorts ports = ReadPorts(map);
        string email = (map.Get("email") ?? LocalDeveloper.DefaultEmail).Trim();
        if (email.Count(c => c == '@') != 1 || email.StartsWith('@') || email.EndsWith('@'))
            return CliErrors.Usage(map, "invalid_value", $"--email takes an email address; got '{CliErrors.Shown(email)}'.");

        if (folder.HasPackage)
            return CliErrors.Usage(map, "already_initialized", $"{folder.Path} already holds an installation ({ServerFolder.ComposeFile} is there).",
                "Start it with `queuey server start`, or move to another version with `queuey server upgrade --version <version>`. " +
                "Use --dir for a second installation somewhere else.");

        string extracted = await ServerFolder.ExtractPackageAsync(version, ct);
        try
        {
            Directory.CreateDirectory(folder.Path);
            IReadOnlyList<string> files = folder.WritePackage(extracted);
            File.WriteAllText(folder.File(ServerFolder.EnvFileName), ServerFolder.LocalEnv(version, ports));
            File.WriteAllText(folder.File(ServerFolder.OverrideFile), ServerFolder.LocalOverride + "\n");
            ServerFolder.WritePrivate(folder.File(ServerFolder.ClientSecretFile), ServerFolder.RandomHex(32) + "\n");
            folder.WriteState(new ServerState(version, ports, files, email));
        }
        finally
        {
            ServerFolder.TryDelete(extracted);
        }

        Console.WriteLine($"Queuey {version} is set up in {folder.Path}, for this machine only.");
        Console.WriteLine($"  console  {ports.ConsoleUrl}");
        Console.WriteLine($"  API      {ports.ApiUrl}");
        Console.WriteLine($"  ingress  {ports.IngressUrl}");
        Console.WriteLine($"  sign-in  {ports.IdpUrl} (the bundled Keycloak)");
        Console.WriteLine();
        Console.WriteLine("Next: queuey server start");
        return ExitCodes.Success;
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
        if (await UpAsync(folder, ct) is { } failed)
            return failed;
        return await AfterStartAsync(folder, testDelivery: !map.Has("no-test"), ct);
    }

    // `up -d` venter selv på det pakken krever: migrate fullført, serveren frisk før konsollet. Feiler migrate, feiler up.
    private static async Task<int?> UpAsync(ServerFolder folder, CancellationToken ct)
    {
        Console.Error.WriteLine($"Starting Queuey {folder.Version} in {folder.Path} …");
        DockerResult up = await ServerDocker.ComposeToTerminalAsync(folder.Path, ct, "up", "-d");
        if (up.ExitCode == 0)
            return null;

        DockerResult migrate = await ServerDocker.ComposeAsync(folder.Path, ct, "logs", "--no-color", "--tail", "20", "migrate");
        if (migrate.ExitCode == 0 && !string.IsNullOrWhiteSpace(migrate.Stdout))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("The last lines from migrate:");
            foreach (string line in migrate.Stdout.TrimEnd().Split('\n'))
                Console.Error.WriteLine("  " + TerminalText.Line(line));
        }
        return CliErrors.Write(false, "start_failed", "docker compose up did not start the installation.",
            "See `queuey server status` and `queuey server logs`. The data is untouched.", status: null, ExitCodes.RuntimeError, "Error");
    }

    private static async Task<int> AfterStartAsync(ServerFolder folder, bool testDelivery, CancellationToken ct)
    {
        LocalPorts ports = folder.Ports;
        IReadOnlyList<ServiceState> services = await WaitForHealthyAsync(folder, TimeSpan.FromMinutes(4), ct);
        if (services.FirstOrDefault(s => s.Running && s.Health is not ("healthy" or "")) is { } unhealthy)
            return CliErrors.Write(false, "not_healthy", $"{unhealthy.Service} is {unhealthy.Health} after four minutes.",
                $"See `queuey server logs {unhealthy.Service}`.", status: null, ExitCodes.RuntimeError, "Error");

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
        TestDeliveryResult result = await TestDelivery.RunAsync(ports, developer, http, ct);
        Console.WriteLine($"{result.EventId} went in through the ingress and reached a receiver on this machine in {result.Took.TotalMilliseconds:0} ms " +
                          $"(queue {TestDelivery.QueueName}).");
        return ExitCodes.Success;
    }

    private static HttpClient Http()
        => CliHost.TestHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>How long to wait between looks while services start. A seam: the tests do not wait.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    // Konsollet og Keycloak har ingen avhengige, så `up -d` venter ikke på at de blir friske. Det gjør dette.
    private static async Task<IReadOnlyList<ServiceState>> WaitForHealthyAsync(ServerFolder folder, TimeSpan timeout, CancellationToken ct)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            IReadOnlyList<ServiceState> services = await ServicesAsync(folder, ct);
            if (services.All(s => !s.Running || s.Health is "healthy" or "") || DateTimeOffset.UtcNow >= until)
                return services;
            await Task.Delay(PollInterval, ct);
        }
    }

    // ── status ──────────────────────────────────────────────────────────

    /// <summary>One service, as <c>docker compose ps</c> reports it.</summary>
    internal sealed record ServiceState(string Service, string State, string Health, int ExitCode)
    {
        public bool Running => State == "running";

        /// <summary>A service that runs once and is done, as setup and migrate are when they ended with 0.</summary>
        public bool Completed => State == "exited" && ExitCode == 0;
    }

    private static readonly string[] OneShot = { "setup", "migrate", "keycloak-db" };

    private static async Task<int> StatusAsync(ServerFolder folder, bool json, CancellationToken ct)
    {
        folder.RequirePackage();
        IReadOnlyList<ServiceState> services = await ServicesAsync(folder, ct);
        LocalPorts ports = folder.Ports;
        bool healthy = services.Count > 0
                       && services.Where(s => !OneShot.Contains(s.Service)).All(s => s.Running && s.Health is "healthy" or "")
                       && services.Where(s => OneShot.Contains(s.Service)).All(s => s.Completed);
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

    /// <summary>The installation's services, from <c>docker compose ps --all --format json</c>.</summary>
    internal static async Task<IReadOnlyList<ServiceState>> ServicesAsync(ServerFolder folder, CancellationToken ct)
    {
        DockerResult ps = await ServerDocker.ComposeAsync(folder.Path, ct, "ps", "--all", "--format", "json");
        if (ps.ExitCode != 0)
            throw new QueueyException($"docker compose ps failed: {ServerDocker.Detail(ps)}");
        return ParseServices(ps.Stdout);
    }

    // Compose skriver ett objekt per linje (v2.21 og senere) eller én liste (eldre); begge leses.
    internal static IReadOnlyList<ServiceState> ParseServices(string output)
    {
        string text = output.Trim();
        if (text.Length == 0)
            return Array.Empty<ServiceState>();
        IEnumerable<JsonElement> items = text.StartsWith('[')
            ? JsonDocument.Parse(text).RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
            : text.Split('\n').Where(l => l.Trim().Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        return items.Select(e => new ServiceState(
                Str(e, "Service"), Str(e, "State"), Str(e, "Health"),
                e.TryGetProperty("ExitCode", out JsonElement code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0))
            .OrderBy(s => s.Service, StringComparer.Ordinal)
            .ToList();

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
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
        if (File.Exists(folder.File(ServerFolder.DeveloperFile)))
            File.Delete(folder.File(ServerFolder.DeveloperFile));
        Console.WriteLine($"Deleted the data. The folder {folder.Path} keeps its configuration; `queuey server start` makes a new, empty installation from it.");
        return ExitCodes.Success;
    }

    // ── upgrade ─────────────────────────────────────────────────────────

    private static async Task<int> UpgradeAsync(ServerFolder folder, ArgMap map, CancellationToken ct)
    {
        folder.RequirePackage();
        string version = ServerFolder.RequireVersion(map.Get("version"));
        string? current = folder.Version;
        if (current == version)
            return CliErrors.Usage(map, "same_version", $"The installation already runs {version}.", "Start it with `queuey server start`.");

        // Pakken for den nye versjonen hentes før noe endres, så en versjon som ikke finnes, endrer ingenting.
        string extracted = await ServerFolder.ExtractPackageAsync(version, ct);
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
                if (File.Exists(folder.File(gone)))
                    File.Delete(folder.File(gone));
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
        if (await UpAsync(folder, ct) is { } failed)
            return failed;
        return await AfterStartAsync(folder, testDelivery: !map.Has("no-test"), ct);
    }
}
