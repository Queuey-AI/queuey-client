using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// F2.7 (2026-10-06, Kenneth: «Gjør din anbefaling på --profile»): tilkoblingen per miljø — nøkkelen, lisensen, vertene og
// workspacet — bor hos brukeren, i ~/.queuey/config.json, aldri i repoet. `queuey login --profile` skriver profilen uten nøkkel
// (2026-10-09, WriteLogin); tokenene står i credentials.json ved siden av (LoginStore). For hånd skrives den med en nøkkel, og
// formatet står i README og `queuey --help`.
//
// Fila holder API-nøkler, så den leses bare når ingen andre enn eieren kan lese eller skrive den (0600 eller strengere), og
// mappa den ligger i, ikke kan skrives av andre. Valgt fremfor en advarsel (som ssh gjør med en privat nøkkel): en advarsel på
// stderr overses i CI og av en agent, og en fil andre kan skrive, kan peke CLI-en mot en annen API-vert og fange nøkkelen.
// Windows har ikke Unix-rettighetene; der beskytter brukerprofilens ACL fila.
//
// Herding før tag (review av #53, 2026-10-06), som StrictModes i ssh: en lenke følges til fila den til slutt peker på, og det er
// den som sjekkes og leses. Fila må eies av brukeren som kjører (også når det er root: root leser alles filer, så en fil en
// annen eier, avvises), og hver mappe over den, opp til og med hjemmemappa, må eies av brukeren eller root og ikke kunne
// skrives av andre. Ett avvik fra ssh: en mappe med sticky-bit (som /tmp) godtas, siden andre der ikke kan bytte ut en fil de
// ikke eier, og fila må være brukerens egen. ACL-er leses ikke; det står i README og `queuey --help`.

/// <summary>One environment's connection, as <c>~/.queuey/config.json</c> holds it under <c>profiles</c>.</summary>
internal sealed class ConnectionProfile
{
    public string? ApiKey { get; set; }
    public string? License { get; set; }
    public string? Tenant { get; set; }
    public string? ApiBase { get; set; }
    public string? IngressBase { get; set; }
    public string? Source { get; set; }
}

/// <summary>The user's connections, one per profile, from <c>~/.queuey/config.json</c> (or <c>QUEUEY_USER_CONFIG</c>).</summary>
internal static class UserProfiles
{
    /// <summary>The variable that names another file than <c>~/.queuey/config.json</c>, such as one CI writes for the run.</summary>
    internal const string PathVariable = "QUEUEY_USER_CONFIG";

    private sealed class UserConfig
    {
        public Dictionary<string, ConnectionProfile?>? Profiles { get; set; }
    }

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Et felt med skrivefeil («apikey» for «apiKey» leses likt, men «api_key» ikke) ville gitt en tilkobling uten nøkkel.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The file the connections are read from: <c>QUEUEY_USER_CONFIG</c>, else <c>~/.queuey/config.json</c>.</summary>
    internal static string PathOf(Func<string, string?> env)
    {
        if (env(PathVariable) is { } named && !string.IsNullOrWhiteSpace(named))
            return named.Trim();

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            throw new QueueyConfigurationException(
                $"--profile needs the user's connections in ~/.queuey/config.json, and this process has no home directory.")
            {
                SuggestedAction = $"Set {PathVariable} to the file that holds them.",
            };
        return Path.Combine(home, ".queuey", "config.json");
    }

    /// <summary>
    /// Profile <paramref name="profile"/>'s connection, and the file it came from. Throws
    /// <see cref="QueueyConfigurationException"/> when the file is missing, others can reach it, it cannot be read, or it
    /// has no such profile. Nothing it holds is ever shown, and a key least of all.
    /// </summary>
    internal static ConnectionProfile Load(string profile, Func<string, string?> env, out string path)
    {
        path = PathOf(env);
        if (!File.Exists(path))
            throw new QueueyConfigurationException(
                $"--profile {profile} needs its connection in {path}, and there is no such file.")
            {
                SuggestedAction = $"Run `queuey login --profile {profile}`, which writes it. Or create it, readable only by you (chmod 600), with " +
                                  $"\"profiles\": {{ \"{profile}\": {{ \"apiKey\", \"license\", \"tenant\", \"apiBase\", \"ingressBase\" }} }}. " +
                                  "`queuey --help` shows the format.",
            };

        UserConfig config = Read(path);
        if (config.Profiles is not null && config.Profiles.TryGetValue(profile, out ConnectionProfile? found) && found is not null)
            return found;

        IEnumerable<string> names = (config.Profiles?.Keys ?? Enumerable.Empty<string>()).Where(DeploymentProfiles.IsName).OrderBy(n => n, StringComparer.Ordinal);
        string has = names.Any() ? "It has " + string.Join(", ", names) + "." : "It has no profiles.";
        throw new QueueyConfigurationException($"{path} has no profile '{profile}', so --profile {profile} has no connection. {has}")
        {
            SuggestedAction = $"Run `queuey login --profile {profile}`, which adds it. Or add \"{profile}\" under \"profiles\" in {path}, " +
                              "with that environment's apiKey, license, tenant and hosts.",
        };
    }

    /// <summary>
    /// Profile <paramref name="profile"/>'s connection, or null when the file or the profile is not there yet, as for a first
    /// <c>queuey login --profile</c>. A file others can reach, or one that cannot be read, still throws.
    /// </summary>
    internal static ConnectionProfile? TryLoad(string profile, Func<string, string?> env, out string path)
    {
        path = PathOf(env);
        if (!File.Exists(path))
            return null;
        UserConfig config = Read(path);
        return config.Profiles is not null && config.Profiles.TryGetValue(profile, out ConnectionProfile? found) ? found : null;
    }

    /// <summary>The file at <paramref name="path"/>, checked and parsed. Nothing it holds is ever shown in an error.</summary>
    private static UserConfig Read(string path)
    {
        // Fila som leses, er den som ble sjekket: lenkens endelige mål.
        string target = EnsureOnlyTheUserCanReachIt(path);

        string json;
        try
        {
            json = File.ReadAllText(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new QueueyConfigurationException($"Could not read {path}: {ex.GetType().Name}.");
        }

        try
        {
            return JsonSerializer.Deserialize<UserConfig>(json, ReadOptions) ?? new UserConfig();
        }
        catch (JsonException ex)
        {
            // Meldingen fra parseren kan ta med et tegn av fila, og stien har navnene i den: en nøkkel limt inn som feltnavn
            // eller profilnavn sto der. Stien vises med feltene fila har og navn med formen til et profilnavn; resten maskeres.
            string? at = JsonErrorPaths.Mask(ex.Path, JsonErrorPaths.Fields("profiles"), DeploymentProfiles.IsName,
                JsonErrorPaths.Fields("apiKey", "license", "tenant", "apiBase", "ingressBase", "source"));
            throw new QueueyConfigurationException(
                $"Could not read {path} at line {(ex.LineNumber ?? 0) + 1}" +
                (at is null ? "" : $" ({at})") +
                ": it holds a field the file does not have, a value that is not text, or JSON that is not valid. Nothing of it is shown.")
            {
                SuggestedAction = "Each profile takes apiKey, license, tenant, apiBase, ingressBase and source, all text.",
            };
        }
    }

    /// <summary>
    /// Writes what <c>queuey login --profile</c> found into profile <paramref name="profile"/>: the license, the hosts and the
    /// workspace, or no workspace when <paramref name="tenant"/> is null. Every other profile, and every other field of this
    /// one (an <c>apiKey</c>, a <c>source</c>), stays as it was. A profile with an <c>apiKey</c> keeps its hosts as they are
    /// (a missing host is Queuey's own), gets the license only when it has none, and the workspace only when it has none and
    /// already had the login's license, since the key wins over the login. The file is created readable only by the user (0600) when it is not there, and
    /// replaced whole otherwise. Returns the file, whether the profile has an <c>apiKey</c>, and the workspace it names now.
    /// </summary>
    // Profilfletting (PR 4): fila leses som JSON-tre, ikke som UserConfig, så felt en nyere CLI har skrevet blir stående. Kommentarer
    // går tapt, siden System.Text.Json ikke skriver dem tilbake; det står i README.
    internal static (string Path, bool HasApiKey, string? Tenant) WriteLogin(
        string profile, Func<string, string?> env, string license, string apiBase, string? ingressBase, string? tenant)
    {
        string path = PathOf(env);
        string target = Path.GetFullPath(path);
        JsonObject root = new();
        if (File.Exists(path))
        {
            target = EnsureOnlyTheUserCanReachIt(path);
            _ = Read(path); // Samme sjekk av formen som når fila leses, så en fil med feil aldri blir skrevet over halvveis.
            root = JsonNode.Parse(File.ReadAllText(target), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? new JsonObject();
        }

        if (root["profiles"] is not JsonObject profiles)
            root["profiles"] = profiles = new JsonObject();
        if (profiles[profile] is not JsonObject entry)
            profiles[profile] = entry = new JsonObject();

        // En profil med apiKey kobler til med nøkkelen, og den er skrevet av en person. Login flytter den aldri (security-review
        // av #66, BØR 2, runde 2): vertene skrives ikke, for en vert som mangler, er Queueys egen, og å fylle den ville sendt
        // nøkkelen til innloggingens vert. Lisensen fylles bare når den mangler, og workspacet bare når det mangler og profilen
        // alt hadde innloggingens lisens.
        bool hasApiKey = Has(entry, "apiKey");
        if (hasApiKey)
        {
            bool sameLicense = entry["license"] is JsonValue had && had.TryGetValue(out string? hadLicense)
                               && string.Equals(hadLicense?.Trim(), license, StringComparison.Ordinal);
            Set(entry, "license", license, fillOnly: true);
            if (sameLicense)
                Set(entry, "tenant", tenant, fillOnly: true);
        }
        else
        {
            Set(entry, "license", license, fillOnly: false);
            Set(entry, "apiBase", apiBase, fillOnly: false);
            Set(entry, "ingressBase", ingressBase, fillOnly: false);
            Set(entry, "tenant", tenant, fillOnly: false);
        }

        PrivateFiles.WriteAllText(target, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (path, hasApiKey, entry["tenant"] is JsonValue written && written.TryGetValue(out string? kept) ? kept : null);

        static bool Has(JsonObject entry, string name)
            => entry[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text);

        static void Set(JsonObject entry, string name, string? value, bool fillOnly)
        {
            if (fillOnly && Has(entry, name))
                return;
            if (value is null)
            {
                if (!fillOnly)
                    entry.Remove(name);
            }
            else
            {
                entry[name] = value;
            }
        }
    }

    /// <summary>
    /// The file to read for <paramref name="path"/>: the file a link finally points to, or the path itself. Throws when
    /// anyone but the user running the CLI can reach that file: another user owns it, others may read or write it, or a
    /// folder above it, up to and including the home folder, belongs to another user than the user or root, or others can
    /// write to it without the sticky bit. Not on Windows, which has no Unix permissions; the user profile's ACL protects the
    /// file there. ACLs on Unix are not read.
    /// </summary>
    internal static string EnsureOnlyTheUserCanReachIt(string path) => Ensure(path, Home(), "API keys");

    /// <summary><see cref="EnsureOnlyTheUserCanReachIt(string)"/> for a file that holds <paramref name="holds"/>, as its error says.</summary>
    internal static string EnsureOnlyTheUserCanReachFileHolding(string path, string holds) => Ensure(path, Home(), holds);

    /// <summary><see cref="EnsureOnlyTheUserCanReachIt(string)"/> with the home folder the walk stops at given.</summary>
    internal static string EnsureOnlyTheUserCanReachIt(string path, string? home) => Ensure(path, home, "API keys");

    private static string Ensure(string path, string? home, string holds)
    {
        if (OperatingSystem.IsWindows())
            return path;

        string target = FinalTarget(path);
        string shown = string.Equals(target, Path.GetFullPath(path), StringComparison.Ordinal) ? path : $"{path} (a link to {target})";
        uint me = CurrentUser();

        (uint owner, UnixFileMode mode) = Inspect(target);
        if (owner != me)
            throw new QueueyConfigurationException(
                $"{shown} belongs to another user (uid {owner}), not the one running queuey (uid {me}), so it was not read.")
            {
                SuggestedAction = "Keep your connections in a file of your own, readable only by you (chmod 600).",
            };

        const UnixFileMode GroupOrOthers = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                           | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & GroupOrOthers) != 0)
            throw new QueueyConfigurationException(
                $"{shown} holds {holds}, and other users can reach it (mode {Octal(mode)}), so it was not read.")
            {
                SuggestedAction = $"Let only you read and write it: chmod 600 {target}",
            };

        Folders(Path.GetDirectoryName(target), home, me, shown, "it was not read");
        return target;
    }

    /// <summary>
    /// Throws when another user could replace a file the CLI is about to write in <paramref name="folder"/>: the folder, or one
    /// above it up to the home folder, belongs to another user than the user or root, or others can write to it without the
    /// sticky bit. The same rules as for a file it reads. Not on Windows.
    /// </summary>
    // Security-review av #66 (KAN 5): en ny credentials.json eller config.json ble skrevet uten at mappene ble sjekket, så en
    // mappe andre kunne skrive i, ble først oppdaget ved neste lesing, etter at tokenene var der.
    internal static void EnsureOnlyTheUserCanWriteIn(string folder)
    {
        if (OperatingSystem.IsWindows())
            return;
        string full = Path.GetFullPath(folder);
        Folders(full, Home(), CurrentUser(), Path.Combine(full, "…"), "nothing was written");
    }

    private static void Folders(string? start, string? home, uint me, string shown, string outcome)
    {
        // Hver mappe over fila, som ssh: til og med hjemmemappa når fila ligger der, ellers helt til roten.
        for (string? folder = start; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            (uint folderOwner, UnixFileMode folderMode) = Inspect(folder);
            if (folderOwner != me && folderOwner != 0)
                throw new QueueyConfigurationException(
                    $"{folder} belongs to another user (uid {folderOwner}), who could replace {shown}, so {outcome}.")
                {
                    SuggestedAction = "Keep the file in a folder that belongs to you, such as ~/.queuey.",
                };

            // En mappe andre kan skrive i, lar dem bytte ut fila, med mindre sticky-biten hindrer det (som i /tmp).
            if ((folderMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 && (folderMode & UnixFileMode.StickyBit) == 0)
                throw new QueueyConfigurationException(
                    $"{folder} can be written by other users (mode {Octal(folderMode)}), who could replace {shown}, so {outcome}.")
                {
                    SuggestedAction = $"Let only you write to it: chmod go-w {folder}",
                };

            if (home is not null && string.Equals(folder, home, StringComparison.Ordinal))
                break;
        }
    }

    /// <summary>The file a link at <paramref name="path"/> finally points to, as a full path; the path itself when it is no link.</summary>
    private static string FinalTarget(string path)
    {
        string full = Path.GetFullPath(path);
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(full, returnFinalTarget: true);
            return target is null ? full : Path.GetFullPath(target.FullName);
        }
        catch (IOException ex)
        {
            throw new QueueyConfigurationException($"Could not follow the link {path} to a file ({ex.GetType().Name}), so it was not read.");
        }
    }

    /// <summary>The home folder as a full path without a trailing separator, or null when there is none.</summary>
    private static string? Home()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return null;

        string full = Path.GetFullPath(home);
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    /// <summary>
    /// Who owns a file or folder, and its mode, following links as stat(2) does. A seam: a test can make a file belong to
    /// another user, which a test cannot do on disk without root.
    /// </summary>
    internal static Func<string, (uint Owner, UnixFileMode Mode)> Inspect = Native.Inspect;

    /// <summary>The user the process runs as (its effective uid). A seam, as <see cref="Inspect"/>.</summary>
    internal static Func<uint> CurrentUser = Native.CurrentUser;

    /// <summary>
    /// The effective uid from libc's <c>geteuid</c>, loaded by name, or null when no libc could be loaded: the fallback
    /// <see cref="CurrentUser"/> takes when the runtime's shim has no <c>SystemNative_GetEUid</c>.
    /// </summary>
    internal static uint? EffectiveUserFromLibc() => Native.LibcEffectiveUser();

    // .NET har ikke et offentlig API for eieren av en fil; File.GetUnixFileMode gir bare modusen. Runtimens egen shim har det:
    // libSystem.Native, som File.GetUnixFileMode selv kaller, fyller FileStatus med Flags, Mode, Uid og Gid først, og de fire har
    // stått der siden .NET Core 2.0. Bufferen er romslig, så felt runtimen legger til bak dem, får plass. Modusen fra den
    // sammenlignes med File.GetUnixFileMode, så en annen rekkefølge i en senere runtime gir en feil, aldri en eier som er lest
    // feil. Finnes ikke shimen, leses fila ikke: sjekken faller aldri bort i stillhet.
    //
    // Orkestreringen (review av #55, 2026-10-06): uid-en til prosessen har en reserve i libc sin geteuid, lastet ved navn med
    // NativeLibrary (libc.so.6 i glibc, libc.so i musl, libSystem.dylib i macOS). Eieren av en fil har ingen reserve: stat(2)
    // har ulik struct per plattform, og en eier som er lest feil, er verre enn en fil som ikke leses. release.yml kjører
    // --profile i den publiserte binærfila for hver RID, så en runtime uten shimen stopper releasen, ikke brukeren.
    private static class Native
    {
        private static readonly string[] Libc = { "libc.so.6", "libc.so", "libSystem.dylib" };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GetEUidFunction();

        [StructLayout(LayoutKind.Sequential, Size = 512)]
        private struct FileStatus
        {
            public int Flags;
            public int Mode;
            public uint Uid;
            public uint Gid;
        }

        [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true)]
        private static extern int Stat(string path, out FileStatus status);

        [DllImport("libSystem.Native", EntryPoint = "SystemNative_GetEUid")]
        private static extern uint GetEUid();

        internal static (uint Owner, UnixFileMode Mode) Inspect(string path)
        {
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows has no Unix owner or mode.");

            UnixFileMode mode = File.GetUnixFileMode(path);
            int result;
            FileStatus status;
            try
            {
                result = Stat(path, out status);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw Unchecked(path);
            }

            if (result != 0 || (status.Mode & 0xFFF) != ((int)mode & 0xFFF))
                throw Unchecked(path);

            return (status.Uid, mode);
        }

        internal static uint CurrentUser()
        {
            try
            {
                return GetEUid();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return LibcEffectiveUser() ?? throw new QueueyConfigurationException(
                    "Could not tell which user runs queuey on this .NET runtime, so the connections file was not read.")
                {
                    SuggestedAction = "Update the queuey CLI, or run without --profile.",
                };
            }
        }

        internal static uint? LibcEffectiveUser()
        {
            foreach (string name in Libc)
            {
                if (NativeLibrary.TryLoad(name, out IntPtr library) && NativeLibrary.TryGetExport(library, "geteuid", out IntPtr geteuid))
                    return Marshal.GetDelegateForFunctionPointer<GetEUidFunction>(geteuid)();
            }

            return null;
        }

        private static QueueyConfigurationException Unchecked(string path) => new(
            $"Could not check who owns {path} on this .NET runtime, so it was not read.")
        {
            SuggestedAction = "Update the queuey CLI, or run without --profile.",
        };
    }

    private static string Octal(UnixFileMode mode) => "0" + Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0');
}
