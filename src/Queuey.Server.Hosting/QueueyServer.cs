using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Server.Hosting;

/// <summary>How <see cref="QueueyServer.EnsureRunningAsync"/> runs a local Queuey.</summary>
public sealed class QueueyServerOptions
{
    /// <param name="version">The Queuey version, pinned, such as <c>1.0.0</c>. Never <c>latest</c>: an installation runs the
    /// version it was made with until it is upgraded with <c>queuey server upgrade</c>.</param>
    public QueueyServerOptions(string version) => Version = version ?? throw new ArgumentNullException(nameof(version));

    /// <summary>The Queuey version the installation runs.</summary>
    public string Version { get; }

    /// <summary>
    /// The installation folder. Unset, it is <c>QUEUEY_SERVER_DIR</c>, else <c>~/.queuey/server</c>: the same installation
    /// <c>queuey server</c> uses.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>The console's port on 127.0.0.1, when the installation is made. Default 3000.</summary>
    public int ConsolePort { get; set; } = 3000;

    /// <summary>The API's port on 127.0.0.1, when the installation is made. Default 8080.</summary>
    public int ApiPort { get; set; } = 8080;

    /// <summary>The ingress's port on 127.0.0.1, when the installation is made. Default 8081.</summary>
    public int IngressPort { get; set; } = 8081;

    /// <summary>The bundled Keycloak's port on 127.0.0.1, when the installation is made. Default 8082.</summary>
    public int IdpPort { get; set; } = 8082;

    /// <summary>The email the developer signs in to the console with, when the installation is made.</summary>
    public string DeveloperEmail { get; set; } = LocalDeveloper.DefaultEmail;

    /// <summary>How long to wait for every service to be healthy. Default four minutes; the first start pulls and migrates.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Progress lines, such as Docker Compose's, for a log. Null writes nothing.</summary>
    public Action<string>? Log { get; set; }
}

/// <summary>
/// A running local Queuey and what an app needs to use it with Queuey.Client: the addresses, the workspace, and an API key
/// that may publish to that workspace and administer its queues and streams.
/// </summary>
public sealed class QueueyServerConnection
{
    internal QueueyServerConnection(string directory, string version, LocalPorts ports, string developerEmail, AppKeyRecord key)
    {
        Directory = directory;
        Version = version;
        ConsoleUrl = new Uri(ports.ConsoleUrl);
        ApiBase = new Uri(ports.ApiUrl);
        IngressBase = new Uri(ports.IngressUrl);
        DeveloperEmail = developerEmail;
        TenantPublicId = key.Tenant;
        LicensePublicId = key.License;
        ApiKey = key.ApiKey;
    }

    /// <summary>The installation folder.</summary>
    public string Directory { get; }

    /// <summary>The Queuey version it runs.</summary>
    public string Version { get; }

    /// <summary>The console, for a browser on this machine.</summary>
    public Uri ConsoleUrl { get; }

    /// <summary>The API, from this machine.</summary>
    public Uri ApiBase { get; }

    /// <summary>The ingress, from this machine.</summary>
    public Uri IngressBase { get; }

    /// <summary>Who signs in to the console. The password is in <c>developer.json</c> in the folder.</summary>
    public string DeveloperEmail { get; }

    /// <summary>The developer's workspace (<c>ten_…</c>).</summary>
    public string TenantPublicId { get; }

    /// <summary>The developer's account (<c>lic_…</c>).</summary>
    public string LicensePublicId { get; }

    /// <summary>A key for the workspace, made once and kept in the folder (readable by the user only).</summary>
    public string ApiKey { get; }

    /// <summary>
    /// The environment variables Queuey.Client reads: <c>QUEUEY_API_BASE</c>, <c>QUEUEY_INGRESS_BASE</c>,
    /// <c>QUEUEY_API_KEY</c>, <c>QUEUEY_TENANT</c> and <c>QUEUEY_LICENSE</c>.
    /// </summary>
    /// <param name="fromContainer">
    /// True for an app in a container, which reaches this machine as <c>host.docker.internal</c> instead of 127.0.0.1. Docker
    /// Desktop forwards that to the ports on 127.0.0.1; on a Linux host it does not, since they listen on loopback only.
    /// </param>
    public IReadOnlyDictionary<string, string> ToEnvironment(bool fromContainer = false) => new Dictionary<string, string>
    {
        [QueueyEnvironmentVariables.ApiBase] = Host(ApiBase, fromContainer),
        [QueueyEnvironmentVariables.IngressBase] = Host(IngressBase, fromContainer),
        [QueueyEnvironmentVariables.ApiKey] = ApiKey,
        [QueueyEnvironmentVariables.Tenant] = TenantPublicId,
        [QueueyEnvironmentVariables.License] = LicensePublicId,
    };

    private static string Host(Uri address, bool fromContainer)
        => (fromContainer ? new UriBuilder(address) { Host = "host.docker.internal" }.Uri : address).ToString().TrimEnd('/');

    /// <summary>The addresses and the workspace, never the key.</summary>
    public override string ToString() => $"Queuey {Version} at {ApiBase} (ingress {IngressBase}, console {ConsoleUrl}), workspace {TenantPublicId}";
}

/// <summary>
/// Starts a self-hosted Queuey on this machine for local development, with Docker Compose, from Queuey's pinned server
/// image. It is the installation <c>queuey server</c> makes and runs, with the same folder and defaults.
/// </summary>
public static class QueueyServer
{
    /// <summary>
    /// Makes sure the local Queuey runs, and returns what an app needs to use it. The first call makes the installation (in
    /// a new or empty folder), creates the developer, their account and workspace, and a key; later calls start it if it is
    /// stopped and return the same. Idempotent, and safe to call from two processes at once.
    /// </summary>
    /// <exception cref="QueueyConfigurationException">Docker is missing, the folder holds another version or another
    /// installation, or the installation is no longer one for this machine only.</exception>
    /// <exception cref="QueueyException">It did not start, or did not get healthy in time.</exception>
    public static async Task<QueueyServerConnection> EnsureRunningAsync(QueueyServerOptions options, CancellationToken cancellationToken = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (!ServerFolder.IsPinnedVersion(options.Version))
            throw new ArgumentException($"The version must be pinned, such as 1.0.0 or 1.0.0-rc.1; got '{options.Version}'.", nameof(options));

        await ServerDocker.RequireComposeAsync(cancellationToken);
        ServerFolder folder = ServerFolder.Resolve(options.Directory, Environment.GetEnvironmentVariable);

        using FolderLock _ = await FolderLock.TakeAsync(folder, cancellationToken);
        if (!folder.HasPackage)
        {
            var ports = new LocalPorts(options.ConsolePort, options.ApiPort, options.IngressPort, options.IdpPort);
            await Installation.InitAsync(folder, options.Version, ports, options.DeveloperEmail, options.Log, cancellationToken);
        }
        else if (folder.Version != options.Version)
        {
            throw new QueueyConfigurationException(
                $"The installation in {folder.Path} runs Queuey {folder.Version}, not {options.Version}. Nothing was started.")
            {
                SuggestedAction = $"Upgrade it with `queuey server upgrade --version {options.Version}` (it takes a backup first), " +
                                  $"or ask for {folder.Version}.",
            };
        }

        if (folder.State is not { } state)
            throw new QueueyConfigurationException(
                $"The installation in {folder.Path} was set up by hand, so there is no developer to make a key with.")
            {
                SuggestedAction = "Use a folder `queuey server init` made, or a new one.",
            };

        await Installation.GuardAsync(folder, cancellationToken);
        await Installation.UpAsync(folder, options.Log, cancellationToken);
        await Installation.WaitForHealthyAsync(folder, options.StartTimeout, cancellationToken);

        using HttpClient http = HttpFactory();
        DeveloperRecord developer = await LocalDeveloper.EnsureAsync(folder, state.DeveloperEmail, http, cancellationToken);
        AppKeyRecord key = await AppKey.EnsureAsync(folder, developer, http, cancellationToken);
        return new QueueyServerConnection(folder.Path, state.Version, state.Ports, developer.Email, key);
    }

    /// <summary>The HTTP client to Keycloak and the API. A seam: the tests answer the calls themselves.</summary>
    internal static Func<HttpClient> HttpFactory { get; set; } = () => new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
}

/// <summary>The key an app gets, kept in <c>app-key.json</c> in the folder (readable by the user only).</summary>
internal sealed record AppKeyRecord(string ApiKey, string Tenant, string License);

/// <summary>
/// The key <see cref="QueueyServer.EnsureRunningAsync"/> gives an app: a starter key for the developer's workspace, which may
/// publish there and administer its queues and streams, made once and reused.
/// </summary>
internal static class AppKey
{
    public static async Task<AppKeyRecord> EnsureAsync(ServerFolder folder, DeveloperRecord developer, HttpClient http, CancellationToken ct)
    {
        (LocalApi api, string tenant) = await LocalWorkspace.OpenAsync(folder.Ports, developer, http, ct);
        string path = folder.File(ServerFolder.AppKeyFile);
        if (File.Exists(path) && JsonSerializer.Deserialize<AppKeyRecord>(File.ReadAllText(path), Text.Json) is { } kept
            && kept.Tenant == tenant && kept.License == api.License)
            return kept;

        // Startnøkkelen for arbeidsområdet (POST /licenses/{lic}/apikeys/starter): ProducerAdmin for det, og aldri mer enn
        // utvikleren selv har.
        using JsonDocument minted = await api.SendAsync(HttpMethod.Post, $"/licenses/{api.License}/apikeys/starter",
            new { name = "queuey-server-hosting", tenantPublicId = tenant }, ct);
        string? apiKey = minted.RootElement.TryGetProperty("apiKey", out JsonElement value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(apiKey))
            throw new QueueyException($"Queuey did not make a key for the workspace {tenant}.");

        var key = new AppKeyRecord(apiKey!, tenant, api.License!);
        ServerFolder.WritePrivate(path, JsonSerializer.Serialize(key, Text.Json) + "\n");
        return key;
    }
}

/// <summary>
/// A lock on the installation folder across processes, so an AppHost and the CLI starting at once do not both create the
/// developer or the key. A file held open without sharing; the operating system releases it if the process dies.
/// </summary>
internal sealed class FolderLock : IDisposable
{
    private readonly FileStream? _stream;

    private FolderLock(FileStream? stream) => _stream = stream;

    public static async Task<FolderLock> TakeAsync(ServerFolder folder, CancellationToken ct)
    {
        // Mappen lages av init; før det finnes ingenting å låse, og init krever en ny eller tom mappe uansett.
        string? parent = Path.GetDirectoryName(folder.Path);
        if (parent is null || !System.IO.Directory.Exists(parent))
            return new FolderLock(null);
        string path = Path.Combine(parent, "." + Path.GetFileName(folder.Path) + ".lock");
        while (true)
        {
            try
            {
                return new FolderLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException)
            {
                await Task.Delay(500, ct);
            }
        }
    }

    public void Dispose() => _stream?.Dispose();
}
