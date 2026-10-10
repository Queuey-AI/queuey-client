using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Cli;

// queuey login (2026-10-09, PR 4 i login-plan.md): tokenene fra innloggingen står i credentials.json ved siden av
// config.json, ikke i den. config.json har profilene, som en person kan skrive og lese; credentials.json skrives bare av
// CLI-en. Fila sjekkes som config.json (UserProfiles.EnsureOnlyTheUserCanReachIt) før den leses, og skrives med 0600 i en
// mappe med 0700.
//
// Refresh-tokenet roterer: hvert svar gir et nytt, og et gammelt brukt på nytt trekker tilbake hele tilkoblingen. To
// kommandoer som fornyer samtidig, ville gjort nettopp det. Derfor skjer hver endring av fila under en lås (Lock), og den som
// fornyer, leser fila på nytt inne i låsen: har en annen prosess fornyet imens, brukes det nye tokenet.

/// <summary>One login: a connection to one API host and one license, with its tokens.</summary>
internal sealed class StoredLogin
{
    /// <summary>The API host, as <see cref="LoginStore.HostKey"/> writes it.</summary>
    public string ApiBase { get; set; } = "";

    public string License { get; set; } = "";

    /// <summary><c>read</c> or <c>operate</c>.</summary>
    public string Scope { get; set; } = "";

    public string ClientId { get; set; } = OAuthClient.ClientId;

    /// <summary>The person, when Queuey says who approved the login (<c>user</c> in the token answer). Null otherwise.</summary>
    public string? User { get; set; }

    /// <summary>The ingress host Queuey gave with the token (<c>ingress_base</c>), or null.</summary>
    public string? IngressBase { get; set; }

    /// <summary>
    /// The console's origin, as the login's verification link named it, such as <c>https://app.queuey.ai</c>: where commands
    /// link a person to a page. Null for a login stored before it was kept.
    /// </summary>
    public string? ConsoleBase { get; set; }

    /// <summary>True when <see cref="IngressBase"/> was given with <c>--ingress-base</c>: a renewal then keeps it.</summary>
    public bool IngressBaseFromFlag { get; set; }

    public string AccessToken { get; set; } = "";
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset LoggedInAt { get; set; }
}

/// <summary>A device code waiting for a person to approve it, so the next <c>queuey login</c> can pick it up.</summary>
internal sealed class PendingLogin
{
    public string ApiBase { get; set; } = "";
    public string Scope { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string VerificationUri { get; set; } = "";
    public string? VerificationUriComplete { get; set; }

    /// <summary>
    /// The profile the login was started for, or null. <c>queuey login --profile p --wait</c> finds the code, and its API host,
    /// by it: the profile gets the host only after approval.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>The <c>--ingress-base</c> the login was started with, or null; the run that finishes it keeps it.</summary>
    public string? IngressBase { get; set; }

    /// <summary>Seconds to wait between polls, as Queuey last asked (raised by 5 on each <c>slow_down</c>).</summary>
    public int Interval { get; set; } = 5;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>The link a person opens: the one with the code in it when Queuey gives one.</summary>
    public string Link => string.IsNullOrWhiteSpace(VerificationUriComplete) ? VerificationUri : VerificationUriComplete!;
}

/// <summary>What <c>credentials.json</c> holds.</summary>
internal sealed class CredentialsFile
{
    public int Version { get; set; } = 1;
    public List<StoredLogin> Logins { get; set; } = new();
    public List<PendingLogin> Pending { get; set; } = new();

    public IEnumerable<StoredLogin> For(string apiBase) => Logins.Where(l => string.Equals(l.ApiBase, apiBase, StringComparison.Ordinal));

    public StoredLogin? Find(string apiBase, string license)
        => For(apiBase).FirstOrDefault(l => string.Equals(l.License, license, StringComparison.Ordinal));
}

/// <summary>Reads and writes <c>credentials.json</c>, next to the user's <c>config.json</c>, under a lock between processes.</summary>
internal static class LoginStore
{
    internal const string FileName = "credentials.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>How long a command waits for another queuey process that holds the lock, before it gives up.</summary>
    internal static TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The file: <c>credentials.json</c> in the folder of the user's connections (<c>~/.queuey</c>, or the folder of <c>QUEUEY_USER_CONFIG</c>).</summary>
    internal static string PathOf(Func<string, string?> env)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(UserProfiles.PathOf(env)))!, FileName);

    /// <summary>The key a login is stored under: the API host's origin and path, without a trailing slash.</summary>
    internal static string HostKey(Uri apiBase) => apiBase.GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>The file's logins and pending codes; an empty file when there is none. Throws when others can reach it.</summary>
    internal static CredentialsFile Read(string path)
    {
        if (!File.Exists(path))
            return new CredentialsFile();

        string target = UserProfiles.EnsureOnlyTheUserCanReachFileHolding(path, "login tokens");
        try
        {
            return JsonSerializer.Deserialize<CredentialsFile>(File.ReadAllText(target), Json) ?? new CredentialsFile();
        }
        catch (JsonException)
        {
            // Innholdet vises aldri: fila holder tokens.
            throw new QueueyConfigurationException($"{path} could not be read as the CLI writes it. Nothing of it is shown.")
            {
                SuggestedAction = $"Delete {path} and run `queuey login` again.",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new QueueyConfigurationException($"Could not read {path}: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Writes <paramref name="file"/> to <paramref name="path"/> whole or not at all: a new file readable only by the user
    /// (0600), moved over the old one, in a folder only the user can open (0700) when the CLI creates it.
    /// </summary>
    internal static void Write(string path, CredentialsFile file)
    {
        string target = File.Exists(path) ? UserProfiles.EnsureOnlyTheUserCanReachFileHolding(path, "login tokens") : Path.GetFullPath(path);
        PrivateFiles.WriteAllText(target, JsonSerializer.Serialize(file, Json));
    }

    /// <summary>
    /// Takes the lock on the user's logins, waiting while another queuey process holds it. Dispose releases it; so does the
    /// process ending, since it is the operating system's lock on an open file (flock on Unix).
    /// </summary>
    internal static async Task<IDisposable> LockAsync(string path, CancellationToken cancellationToken)
    {
        string lockPath = path + ".lock";
        string folder = Path.GetDirectoryName(Path.GetFullPath(lockPath))!;
        PrivateFiles.EnsureFolder(folder);
        UserProfiles.EnsureOnlyTheUserCanWriteIn(folder);
        DateTimeOffset until = DateTimeOffset.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream(lockPath, options);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new QueueyConfigurationException(
                    $"Another queuey process has held the lock on your logins ({lockPath}) for {LockTimeout.TotalSeconds:0} seconds.")
                {
                    SuggestedAction = "Wait for it to finish, and run the command again.",
                };
            }
        }
    }
}

/// <summary>Files only the user may read: created with 0600, in folders created with 0700, replaced whole.</summary>
internal static class PrivateFiles
{
    internal static void EnsureFolder(string folder)
    {
        if (Directory.Exists(folder))
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(folder);
        else
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Writes a new file next to <paramref name="target"/>, readable only by the user, and moves it over <paramref name="target"/>.</summary>
    internal static void WriteAllText(string target, string contents)
    {
        string folder = Path.GetDirectoryName(target)!;
        EnsureFolder(folder);
        UserProfiles.EnsureOnlyTheUserCanWriteIn(folder);
        string temp = Path.Combine(folder, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch { /* fila ble kanskje aldri laget */ }
            throw new CliFileException("file_unwritable", $"Could not write {target}: {ex.Message}",
                "Check that this user may write to the folder.", ex);
        }
    }
}
