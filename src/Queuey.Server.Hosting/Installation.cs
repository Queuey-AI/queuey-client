using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Server.Hosting;

/// <summary>One service, as <c>docker compose ps</c> reports it.</summary>
internal sealed record ServiceState(string Service, string State, string Health, int ExitCode)
{
    public bool Running => State == "running";

    /// <summary>A service that runs once and is done, as setup and migrate are when they ended with 0.</summary>
    public bool Completed => State == "exited" && ExitCode == 0;

    /// <summary>Running, and healthy or without a health check.</summary>
    public bool Healthy => Running && Health is "healthy" or "";
}

/// <summary>
/// The steps a local installation goes through, shared by <c>queuey server</c> and <see cref="QueueyServer"/>: make the
/// folder, check it is this folder's own and for this machine only, start it, and wait until it is healthy. Every failure
/// throws <see cref="QueueyConfigurationException"/> (the folder or the machine must change) or <see cref="QueueyException"/>.
/// </summary>
internal static class Installation
{
    /// <summary>The services that run once and exit.</summary>
    public static readonly string[] OneShot = { "setup", "migrate", "keycloak-db" };

    /// <summary>How long to wait between looks while services start. A seam: the tests do not wait.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Makes an installation for this machine only in a new or empty folder: the package of <paramref name="version"/> from
    /// its image, the local <c>.env</c> and override, the console's client secret, and the state. Starts nothing.
    /// </summary>
    public static async Task InitAsync(ServerFolder folder, string version, LocalPorts ports, string email, Action<string>? log, CancellationToken ct)
    {
        if (folder.HasPackage)
            throw new QueueyConfigurationException($"{folder.Path} already holds an installation ({ServerFolder.ComposeFile} is there).");
        // Bare en ny eller tom mappe: --dir . i en repo-rot ville ellers skrevet over README.md og docs/ (security review av 4.8).
        if (!folder.IsMissingOrEmpty)
            throw new QueueyConfigurationException($"{folder.Path} is not empty, and an installation is made only in a new or empty folder.");

        // Volumer fra en tidligere installasjon i samme mappe ville blitt tatt i bruk av den nye, med gamle hemmeligheter.
        string project = ServerFolder.ProjectNameFor(folder.Path);
        DockerResult volumes = await ServerDocker.DockerAsync(ct, "volume", "ls", "--quiet", "--filter", $"label=com.docker.compose.project={project}");
        if (volumes.ExitCode == 0 && volumes.Stdout.Trim().Length > 0)
            throw new QueueyConfigurationException($"Docker still has the volumes of an earlier installation in {folder.Path} (project {project}).")
            {
                SuggestedAction = $"Remove them with `docker volume rm {string.Join(" ", volumes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}` " +
                                  "if that data may go, or choose another folder.",
            };

        string extracted = await ServerFolder.ExtractPackageAsync(version, log, ct);
        try
        {
            folder.CreatePrivate();
            IReadOnlyList<string> files = folder.WritePackage(extracted);
            File.WriteAllText(folder.File(ServerFolder.EnvFileName), ServerFolder.LocalEnv(version, ports, project));
            File.WriteAllText(folder.File(ServerFolder.OverrideFile), ServerFolder.LocalOverride + "\n");
            ServerFolder.WritePrivate(folder.File(ServerFolder.ClientSecretFile), ServerFolder.RandomHex(32) + "\n");
            folder.WriteState(new ServerState(version, ports, files, email));
        }
        finally
        {
            ServerFolder.TryDelete(extracted);
        }
    }

    /// <summary>
    /// Before anything that starts, stops or deletes: the folder's Compose project must be this folder's alone, and a folder
    /// init made must still publish every port on 127.0.0.1 only. Throws with what is wrong.
    /// </summary>
    // Security review av 4.8 (2026-11-12): pakken heter `queuey`, så to mapper delte prosjekt, og --delete-data kunne slettet
    // den andres data. Og at installasjonen er lokal, hviler på at Compose leser override-filen og forstår !override; det
    // sjekkes i konfigurasjonen Compose faktisk bruker.
    public static async Task GuardAsync(ServerFolder folder, CancellationToken ct)
    {
        DockerResult config = await ServerDocker.ComposeAsync(folder.Path, ct, "config", "--format", "json");
        if (config.ExitCode != 0)
            throw new QueueyConfigurationException($"docker compose cannot read the installation in {folder.Path}: {ServerDocker.Detail(config)}")
            {
                SuggestedAction = "Run `docker compose config` in the folder to see what is wrong.",
            };
        using JsonDocument effective = JsonDocument.Parse(config.Stdout);
        string project = effective.RootElement.GetProperty("name").GetString()!;

        DockerResult owners = await ServerDocker.DockerAsync(ct, "ps", "--all", "--filter", $"label=com.docker.compose.project={project}",
            "--format", "{{.Label \"com.docker.compose.project.working_dir\"}}");
        string? other = owners.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(dir => !string.Equals(dir, folder.Path, StringComparison.Ordinal));
        if (other is not null)
            throw new QueueyConfigurationException(
                $"The Compose project '{project}' belongs to the installation in {other}, not to {folder.Path}. Nothing was started, stopped or deleted.")
            {
                SuggestedAction = $"Use `--dir {other}` for that installation. Two folders need two project names (COMPOSE_PROJECT_NAME in .env).",
            };

        if (folder.State is null)
            return;
        if (effective.RootElement.TryGetProperty("services", out JsonElement services))
            foreach (JsonProperty service in services.EnumerateObject())
                if (service.Value.TryGetProperty("ports", out JsonElement ports))
                    foreach (JsonElement port in ports.EnumerateArray())
                        if (!port.TryGetProperty("host_ip", out JsonElement ip) || ip.GetString() != "127.0.0.1")
                            throw new QueueyConfigurationException(
                                $"{service.Name} publishes port {(port.TryGetProperty("published", out JsonElement p) ? p.ToString() : "?")} on more than 127.0.0.1, " +
                                "so the installation is no longer one for this machine only. Nothing was started.")
                            {
                                SuggestedAction = $"Restore {ServerFolder.OverrideFile} as init wrote it, and check that COMPOSE_FILE is not set. " +
                                                  "An installation for other people is set up from .env.example, without queuey server.",
                            };
    }

    /// <summary>
    /// <c>docker compose up -d</c>, which waits for what the package requires itself: migrate done, the server healthy
    /// before the console. When migrate fails, up fails; its last lines go to the log.
    /// </summary>
    public static async Task UpAsync(ServerFolder folder, Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"Starting Queuey {folder.Version} in {folder.Path} …");
        DockerResult up = await ServerDocker.ComposeLoggedAsync(folder.Path, log, ct, "up", "-d");
        if (up.ExitCode == 0)
            return;

        DockerResult migrate = await ServerDocker.ComposeAsync(folder.Path, ct, "logs", "--no-color", "--tail", "20", "migrate");
        if (log is not null && migrate.ExitCode == 0 && !string.IsNullOrWhiteSpace(migrate.Stdout))
        {
            log("The last lines from migrate:");
            foreach (string line in migrate.Stdout.TrimEnd().Split('\n'))
                log("  " + Text.Line(line));
        }
        throw new QueueyException("docker compose up did not start the installation.")
        {
            SuggestedAction = "See `queuey server status` and `queuey server logs`. The data is untouched.",
        };
    }

    /// <summary>Waits until every running service is healthy; throws when one is not within <paramref name="timeout"/>.</summary>
    // Konsollet og Keycloak har ingen avhengige, så `up -d` venter ikke på at de blir friske. Det gjør dette.
    public static async Task WaitForHealthyAsync(ServerFolder folder, TimeSpan timeout, CancellationToken ct)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            IReadOnlyList<ServiceState> services = await ServicesAsync(folder, ct);
            ServiceState? waiting = services.FirstOrDefault(s => s.Running && !s.Healthy);
            if (waiting is null)
                return;
            if (DateTimeOffset.UtcNow >= until)
                throw new QueueyException($"{waiting.Service} is {waiting.Health} after {timeout.TotalMinutes:0} minutes.")
                {
                    SuggestedAction = $"See `queuey server logs {waiting.Service}`.",
                };
            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>The installation's services, from <c>docker compose ps --all --format json</c>.</summary>
    public static async Task<IReadOnlyList<ServiceState>> ServicesAsync(ServerFolder folder, CancellationToken ct)
    {
        DockerResult ps = await ServerDocker.ComposeAsync(folder.Path, ct, "ps", "--all", "--format", "json");
        if (ps.ExitCode != 0)
            throw new QueueyException($"docker compose ps failed: {ServerDocker.Detail(ps)}");
        return ParseServices(ps.Stdout);
    }

    // Compose skriver ett objekt per linje (v2.21 og senere) eller én liste (eldre); begge leses.
    public static IReadOnlyList<ServiceState> ParseServices(string output)
    {
        string text = output.Trim();
        if (text.Length == 0)
            return Array.Empty<ServiceState>();
        IEnumerable<JsonElement> items = text.StartsWith('[')
            ? JsonDocument.Parse(text).RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
            : text.Split('\n').Where(l => l.Trim().Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        return items.Select(e => new ServiceState(
                LocalWorkspace.Str(e, "Service"), LocalWorkspace.Str(e, "State"), LocalWorkspace.Str(e, "Health"),
                e.TryGetProperty("ExitCode", out JsonElement code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0))
            .OrderBy(s => s.Service, StringComparer.Ordinal)
            .ToList();
    }
}
