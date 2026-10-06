using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// F2.7 (2026-10-06, Kenneth: «Gjør din anbefaling på --profile»): tilkoblingen per miljø — nøkkelen, lisensen, vertene og
// workspacet — bor hos brukeren, i ~/.queuey/config.json, aldri i repoet. `queuey login` skriver fila i Fase 4; til da skrives
// den for hånd, og formatet står i README og `queuey --help`.
//
// Fila holder API-nøkler, så den leses bare når ingen andre enn eieren kan lese eller skrive den (0600 eller strengere), og
// mappa den ligger i, ikke kan skrives av andre. Valgt fremfor en advarsel (som ssh gjør med en privat nøkkel): en advarsel på
// stderr overses i CI og av en agent, og en fil andre kan skrive, kan peke CLI-en mot en annen API-vert og fange nøkkelen.
// Windows har ikke Unix-rettighetene; der beskytter brukerprofilens ACL fila.

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
                SuggestedAction = $"Create it, readable only by you (chmod 600), with \"profiles\": {{ \"{profile}\": {{ \"apiKey\", \"license\", " +
                                  "\"tenant\", \"apiBase\", \"ingressBase\" }} }}. `queuey --help` shows the format.",
            };

        EnsureOnlyTheUserCanReachIt(path);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new QueueyConfigurationException($"Could not read {path}: {ex.GetType().Name}.");
        }

        UserConfig config;
        try
        {
            config = JsonSerializer.Deserialize<UserConfig>(json, ReadOptions) ?? new UserConfig();
        }
        catch (JsonException ex)
        {
            // Meldingen fra parseren kan ta med et tegn av fila; stien i JSON-en og linjen gjør ikke det.
            throw new QueueyConfigurationException(
                $"Could not read {path} at line {(ex.LineNumber ?? 0) + 1}" +
                (string.IsNullOrEmpty(ex.Path) ? "" : $" ({ex.Path})") +
                ": it holds a field the file does not have, a value that is not text, or JSON that is not valid. Nothing of it is shown.")
            {
                SuggestedAction = "Each profile takes apiKey, license, tenant, apiBase, ingressBase and source, all text.",
            };
        }

        if (config.Profiles is not null && config.Profiles.TryGetValue(profile, out ConnectionProfile? found) && found is not null)
            return found;

        IEnumerable<string> names = (config.Profiles?.Keys ?? Enumerable.Empty<string>()).Where(DeploymentProfiles.IsName).OrderBy(n => n, StringComparer.Ordinal);
        string has = names.Any() ? "It has " + string.Join(", ", names) + "." : "It has no profiles.";
        throw new QueueyConfigurationException($"{path} has no profile '{profile}', so --profile {profile} has no connection. {has}")
        {
            SuggestedAction = $"Add \"{profile}\" under \"profiles\" in {path}, with that environment's apiKey, license, tenant and hosts.",
        };
    }

    /// <summary>
    /// Throws when anyone but the owner can read or write the file, or another user can replace it through its directory.
    /// Not on Windows, which has no Unix permissions; the user profile's ACL protects the file there.
    /// </summary>
    internal static void EnsureOnlyTheUserCanReachIt(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        const UnixFileMode GroupOrOthers = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                           | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & GroupOrOthers) != 0)
            throw new QueueyConfigurationException(
                $"{path} holds API keys, and other users can reach it (mode {Octal(mode)}), so it was not read.")
            {
                SuggestedAction = $"Let only you read and write it: chmod 600 {path}",
            };

        // En mappe andre kan skrive i, lar dem bytte ut fila, med mindre sticky-biten hindrer det (som i /tmp).
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null)
            return;
        UnixFileMode directoryMode = File.GetUnixFileMode(directory);
        if ((directoryMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 && (directoryMode & UnixFileMode.StickyBit) == 0)
            throw new QueueyConfigurationException(
                $"{directory} can be written by other users (mode {Octal(directoryMode)}), who could replace {path}, so it was not read.")
            {
                SuggestedAction = $"Let only you write to it: chmod 700 {directory}",
            };
    }

    private static string Octal(UnixFileMode mode) => "0" + Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0');
}
