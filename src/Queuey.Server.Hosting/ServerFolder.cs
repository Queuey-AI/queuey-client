using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Queuey.Client;

namespace Queuey.Server.Hosting;

/// <summary>
/// The ports a local installation publishes on this machine, and the addresses that follow from them. The addresses are
/// 127.0.0.1, not localhost: the ports listen on IPv4 loopback only, and localhost tries [::1] first, where another program
/// could listen and get the admin password or a token (security review of 4.8, 2026-11-12).
/// </summary>
internal sealed record LocalPorts(int Console = 3000, int Api = 8080, int Ingress = 8081, int Idp = 8082)
{
    [System.Text.Json.Serialization.JsonIgnore] public string ConsoleUrl => $"http://127.0.0.1:{Console}";
    [System.Text.Json.Serialization.JsonIgnore] public string ApiUrl => $"http://127.0.0.1:{Api}";
    [System.Text.Json.Serialization.JsonIgnore] public string IngressUrl => $"http://127.0.0.1:{Ingress}";
    [System.Text.Json.Serialization.JsonIgnore] public string IdpUrl => $"http://127.0.0.1:{Idp}";
}

/// <summary>
/// What <c>queuey server</c> keeps about an installation it made, in <c>.queuey-server.json</c> in the folder: the ports, and
/// which files came from the package, so an upgrade can remove the ones the new version no longer has.
/// </summary>
/// <param name="DeveloperEmail">The email the first person gets, when the first start creates them.</param>
internal sealed record ServerState(string Version, LocalPorts Ports, IReadOnlyList<string> PackageFiles, string DeveloperEmail);

/// <summary>
/// An installation folder: the Compose package from the server image, plus the files that are this installation's own
/// (<c>.env</c>, <c>oidc-client-secret</c>, <c>compose.override.yaml</c>, the license, backups). It is the same folder the
/// package's README describes, so <c>docker compose</c> run by hand in it does what <c>queuey server</c> does.
/// </summary>
internal sealed class ServerFolder
{
    public const string ComposeFile = "compose.yaml";
    public const string EnvFileName = ".env";
    public const string OverrideFile = "compose.override.yaml";
    public const string ClientSecretFile = "oidc-client-secret";
    public const string StateFile = ".queuey-server.json";
    public const string DeveloperFile = "developer.json";
    public const string AppKeyFile = "app-key.json";

    /// <summary>The folder the package sits in inside the server image (Queuey 4.8 and later).</summary>
    public const string PackageInImage = "/app/package";

    // Samme form som scripts/release/server-image.sh godtar: en festet versjon, aldri «latest».
    private static readonly Regex VersionShape = new(@"^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ServerFolder(string path) => Path = System.IO.Path.GetFullPath(path);

    /// <summary>The folder's full path.</summary>
    public string Path { get; }

    /// <summary>The variable that names the installation folder when nothing else does.</summary>
    public const string DirectoryVariable = "QUEUEY_SERVER_DIR";

    /// <summary>
    /// The installation folder: the one chosen, else <c>QUEUEY_SERVER_DIR</c>, else <c>~/.queuey/server</c>. The CLI and the
    /// hosting packages resolve it the same way, so they find the same installation.
    /// </summary>
    public static ServerFolder Resolve(string? chosen, Func<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(chosen))
            chosen = env(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(chosen))
            chosen = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".queuey", "server");
        return new ServerFolder(chosen!.Trim());
    }

    /// <summary>A file in the folder.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Whether the folder holds a Compose package.</summary>
    public bool HasPackage => System.IO.File.Exists(File(ComposeFile));

    /// <summary>Throws when the folder holds no installation, with how to make one.</summary>
    public void RequirePackage()
    {
        if (!HasPackage)
            throw new QueueyConfigurationException($"There is no Queuey installation in {Path}: it has no {ComposeFile}.")
            {
                SuggestedAction = "Make one with `queuey server init --version <version>`, or point at yours with --dir.",
            };
    }

    /// <summary>The installation's <c>QUEUEY_VERSION</c> from <c>.env</c>, or null.</summary>
    public string? Version
        => System.IO.File.Exists(File(EnvFileName)) && ReadEnv(File(EnvFileName)).TryGetValue("QUEUEY_VERSION", out string? v) && v.Length > 0 ? v : null;

    /// <summary>What <c>init</c> or <c>upgrade</c> wrote down, or null for a folder set up by hand.</summary>
    public ServerState? State
        => System.IO.File.Exists(File(StateFile)) ? JsonSerializer.Deserialize<ServerState>(System.IO.File.ReadAllText(File(StateFile)), StateJson) : null;

    /// <summary>The ports: from the state, else the package's defaults.</summary>
    public LocalPorts Ports => State?.Ports ?? new LocalPorts();

    public void WriteState(ServerState state) => System.IO.File.WriteAllText(File(StateFile), JsonSerializer.Serialize(state, StateJson) + "\n");

    /// <summary>The image a version runs, as the package's compose.yaml names it.</summary>
    public static string ServerImage(string version) => $"queuey/server:{version}";

    /// <summary>Whether <paramref name="version"/> is a pinned version, such as 1.0.0 or 1.0.0-rc.1; never <c>latest</c>.</summary>
    public static bool IsPinnedVersion(string? version) => version is not null && VersionShape.IsMatch(version);

    /// <summary>
    /// The Compose package of <paramref name="version"/>, copied out of its server image into a new temporary folder. The
    /// image is pulled when it is not on this machine. Returns the folder; the caller deletes it.
    /// </summary>
    public static async Task<string> ExtractPackageAsync(string version, Action<string>? log, CancellationToken ct)
    {
        string image = ServerImage(version);
        if ((await ServerDocker.DockerAsync(ct, "image", "inspect", "--format", "{{.Id}}", image)).ExitCode != 0)
        {
            log?.Invoke($"Pulling {image} …");
            DockerResult pulled = await ServerDocker.DockerAsync(ct, "pull", "--quiet", image);
            if (pulled.ExitCode != 0)
                throw new QueueyConfigurationException($"The image {image} is not on this machine, and it could not be pulled: {ServerDocker.Detail(pulled)}")
                {
                    SuggestedAction = $"Check the version, or load the image first (docker load -i <archive>).",
                };
        }

        DockerResult created = await ServerDocker.DockerAsync(ct, "create", image);
        if (created.ExitCode != 0)
            throw new QueueyException($"Could not read {image}: {ServerDocker.Detail(created)}");
        string container = created.Stdout.Trim();

        string target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "queuey-server-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            DockerResult copied = await ServerDocker.DockerAsync(ct, "cp", $"{container}:{PackageInImage}/.", target);
            if (copied.ExitCode != 0 || !System.IO.File.Exists(System.IO.Path.Combine(target, ComposeFile)))
                throw new QueueyConfigurationException($"{image} has no Compose package in {PackageInImage}.")
                {
                    SuggestedAction = "A local Queuey needs a version that carries its package in the image. Use a newer version, " +
                                      "or set the installation up by hand from the package's README.",
                };
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        finally
        {
            await ServerDocker.DockerAsync(CancellationToken.None, "rm", container);
        }
    }

    /// <summary>
    /// Copies every file of an extracted package into the folder, replacing the package's own files and leaving the
    /// installation's. Returns the files, as paths relative to the folder with '/'.
    /// </summary>
    public IReadOnlyList<string> WritePackage(string extracted)
    {
        var written = new List<string>();
        foreach (string source in Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            string relative = System.IO.Path.GetRelativePath(extracted, source).Replace('\\', '/');
            if (IsInstallationsOwn(relative))
                continue;
            if (Inside(relative) is not { } destination)
                continue;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            System.IO.File.Copy(source, destination, overwrite: true);
            written.Add(relative);
        }
        return written;
    }

    // En pakke skal aldri ha disse, men skulle den få dem, er det installasjonens filer som gjelder.
    private static bool IsInstallationsOwn(string relative)
        => relative is EnvFileName or OverrideFile or ClientSecretFile or StateFile or DeveloperFile or AppKeyFile
           || relative.StartsWith("backups/", StringComparison.Ordinal)
           || (relative.StartsWith("license/", StringComparison.Ordinal) && relative.EndsWith(".license", StringComparison.Ordinal));

    /// <summary>
    /// The <c>.env</c> of an installation only this machine uses: the bundled Keycloak (profile <c>idp</c>, always on through
    /// <c>COMPOSE_PROFILES</c>), and every address on 127.0.0.1.
    /// </summary>
    /// <summary>
    /// The Compose project of a folder: <c>queuey-</c> and a hash of its path. The package names its project <c>queuey</c>,
    /// so without this two folders on one machine would share containers and volumes, and deleting one installation's data
    /// would delete the other's.
    /// </summary>
    public static string ProjectNameFor(string path)
        => "queuey-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path)))).Substring(0, 10).ToLowerInvariant();

    public static string LocalEnv(string version, LocalPorts ports, string project) => $"""
        # Written by `queuey server init` for an installation only this machine uses: every address is on 127.0.0.1,
        # without TLS, and the bundled Keycloak signs people in. The settings are the package's; see .env.example and
        # README.md. `queuey server upgrade` changes QUEUEY_VERSION and nothing else.
        QUEUEY_VERSION={version}
        COMPOSE_PROFILES=idp
        # This folder's own Compose project, so another installation on this machine is never touched.
        COMPOSE_PROJECT_NAME={project}

        QUEUEY_CONSOLE_URL={ports.ConsoleUrl}
        QUEUEY_API_URL={ports.ApiUrl}
        QUEUEY_INGRESS_URL={ports.IngressUrl}
        QUEUEY_CONSOLE_PORT={ports.Console}
        QUEUEY_API_PORT={ports.Api}
        QUEUEY_INGRESS_PORT={ports.Ingress}

        QUEUEY_OIDC_PROVIDER_ID=oidc
        QUEUEY_OIDC_PROVIDER_NAME=Keycloak
        QUEUEY_OIDC_CONSOLE_CLIENT_ID=queuey-console
        QUEUEY_OIDC_AUDIENCE=queuey-api

        QUEUEY_IDP_URL={ports.IdpUrl}
        QUEUEY_IDP_PORT={ports.Idp}
        QUEUEY_OIDC_AUTHORITY=http://keycloak:8080/realms/queuey
        QUEUEY_OIDC_ISSUER={ports.IdpUrl}/realms/queuey
        QUEUEY_OIDC_REQUIRE_HTTPS_METADATA=false

        """;

    /// <summary>
    /// <c>compose.override.yaml</c>, which Compose reads after <c>compose.yaml</c>: what makes the installation one for this
    /// machine only. The ports listen on the loopback interface alone, the console may be plain http on 127.0.0.1, and
    /// deliveries may reach this machine as <c>host.docker.internal</c>, where a developer's own receiver runs.
    /// </summary>
    public const string LocalOverride = """
        # Written by `queuey server init`: what makes this installation one for this machine only. Compose reads it after
        # compose.yaml, and `queuey server upgrade` keeps it.
        services:
          server:
            environment:
              # The console is http://127.0.0.1, which the server accepts only when told that only this machine uses it.
              - Cors__AllowLoopbackHttp=true
              # Your own receivers on this machine, as http://host.docker.internal:<port>/...
              - Delivery__Egress__AllowedHosts__0=host.docker.internal
            extra_hosts:
              - host.docker.internal:host-gateway
            ports: !override
              - 127.0.0.1:${QUEUEY_API_PORT:-8080}:8080
              - 127.0.0.1:${QUEUEY_INGRESS_PORT:-8081}:8081
          console:
            ports: !override
              - 127.0.0.1:${QUEUEY_CONSOLE_PORT:-3000}:3000
          keycloak:
            ports: !override
              - 127.0.0.1:${QUEUEY_IDP_PORT:-8082}:8080
        """;

    /// <summary>
    /// Writes a file only the user may read, as the client secret and the developer's password are. It is created with that
    /// mode, so it is never readable by others, not even for the moment between writing and a chmod.
    /// </summary>
    public static void WritePrivate(string path, string content)
    {
        if (System.IO.File.Exists(path))
            System.IO.File.Delete(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates the installation folder so only the user can enter it (0700), and sets that mode on a folder that exists.
    /// Only the folder's owner may set it, so a folder someone else made is refused.
    /// </summary>
    // Security review av 4.9 (2026-11-12): en tom mappe en annen bruker har laget med 0777 (/tmp/queuey) ble godtatt, og
    // den brukeren kunne bytte compose.yaml før neste `up`.
    public void CreatePrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path);
            return;
        }
        Directory.CreateDirectory(Path, Private);
        try
        {
            System.IO.File.SetUnixFileMode(Path, Private);
        }
        catch (UnauthorizedAccessException)
        {
            throw new QueueyConfigurationException($"{Path} belongs to another user, so an installation is not made there.")
            {
                SuggestedAction = "Choose a folder of your own, such as the default ~/.queuey/server.",
            };
        }
    }

    /// <summary>Throws when others than the user may write in the folder, so they could change what Compose runs.</summary>
    public void RequirePrivate()
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(Path))
            return;
        UnixFileMode mode = System.IO.File.GetUnixFileMode(Path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new QueueyConfigurationException($"Others than you may write in {Path}, so they could change what it runs. Nothing was started.")
            {
                SuggestedAction = $"Run `chmod 700 {Path}` if the folder is yours, and check its files.",
            };
    }

    /// <summary>Whether the folder is missing or empty, the only places init writes to.</summary>
    public bool IsMissingOrEmpty => !Directory.Exists(Path) || !Directory.EnumerateFileSystemEntries(Path).Any();

    /// <summary>A path in the folder from a relative path, or null when it would lead outside it.</summary>
    public string? Inside(string relative)
    {
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
        return full.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>A random secret as hex, as the package's README makes the client secret with <c>openssl rand -hex 32</c>.</summary>
    public static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    /// <summary>Sets <c>QUEUEY_VERSION</c> in <c>.env</c>, leaving every other line as it was.</summary>
    public void SetVersion(string version)
    {
        string path = File(EnvFileName);
        string[] lines = System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path) : Array.Empty<string>();
        bool found = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("QUEUEY_VERSION=", StringComparison.Ordinal))
            {
                lines[i] = "QUEUEY_VERSION=" + version;
                found = true;
            }
        }
        IEnumerable<string> result = found ? lines : lines.Append("QUEUEY_VERSION=" + version);
        System.IO.File.WriteAllText(path, string.Join("\n", result) + "\n");
    }

    /// <summary>The <c>NAME=value</c> lines of an env file. Comments and blank lines are skipped; quotes are not interpreted.</summary>
    public static Dictionary<string, string> ReadEnv(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in System.IO.File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || eq <= 0)
                continue;
            values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return values;
    }

    public static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
