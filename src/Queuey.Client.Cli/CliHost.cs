using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>Shared helpers: config resolution, DI host construction, and output formatting.</summary>
internal static class CliHost
{
    // Relaxed escaping: output meant for a terminal and an agent, not for embedding in HTML. The default
    // encoder writes ` as \u0060 and — as \u2014, which is valid JSON nobody can read.
    public static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// <see cref="JsonOut"/> on one line: for output a reader takes a line at a time (NDJSON), such as <c>verify --json</c>, and
    /// for a <c>--json</c> error, which can come in such a stream.
    /// </summary>
    public static readonly JsonSerializerOptions JsonLine = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The connection for a command that takes no profile: flags, the <c>QUEUEY_</c> variables, <c>queuey.json</c>. Refused
    /// when <c>QUEUEY_PROFILE</c> is set, since the command would connect somewhere else than the profile says.
    /// </summary>
    public static ResolvedConfig Resolve(ArgMap args) => Resolve(args, profiles: false);

    /// <summary>
    /// The connection: with a profile (<c>--profile</c> or <c>QUEUEY_PROFILE</c>, for a command that takes one when
    /// <paramref name="profiles"/> is true), the profile's connection in the user's file; otherwise flags, the
    /// <c>QUEUEY_</c> variables and <c>queuey.json</c>, as before profiles.
    /// </summary>
    internal static ResolvedConfig Resolve(ArgMap args, bool profiles)
    {
        string? profile = Profile(args);
        if (profile is null)
            return Logins.Attach(CliConfig.Resolve(args, Env, ReadConfigJson(args)), Env);

        // F2.7: en kommando uten profiler ville koblet til med queuey.json og QUEUEY_-variablene, ikke profilens miljø.
        if (!profiles)
            throw new QueueyConfigurationException(
                "QUEUEY_PROFILE is set, and this command does not take a profile, so it would connect with queuey.json and the " +
                "QUEUEY_ variables instead of the profile's connection. Nothing was sent.")
            {
                SuggestedAction = "Unset QUEUEY_PROFILE for this command. apply, plan, verify, publish, listen, events get, " +
                                  "credentials and whoami take a profile.",
            };

        // En profil uten apiKey bruker innloggingen for sin API-vert og lisens (queuey login, 2026-10-09).
        ConnectionProfile connection = UserProfiles.Load(profile, Env, out string path);
        return Logins.Attach(CliConfig.ResolveProfile(args, Env, profile, connection, path), Env);
    }

    /// <summary>
    /// The profile <c>--profile</c> or <c>QUEUEY_PROFILE</c> selects, or null for none. A value that is not a profile name is
    /// a usage error that never shows it, since it may be something else pasted there.
    /// </summary>
    internal static string? Profile(ArgMap args)
    {
        string? flagged = args.Get("profile");
        bool fromFlag = !string.IsNullOrWhiteSpace(flagged);
        string? chosen = fromFlag ? flagged : Env("QUEUEY_PROFILE");
        if (string.IsNullOrWhiteSpace(chosen))
            return null;

        chosen = chosen!.Trim();
        if (!DeploymentProfiles.IsName(chosen))
            throw new CliUsageException("invalid_value",
                $"{(fromFlag ? "--profile" : "QUEUEY_PROFILE")} is not a profile name. Its value is not shown.",
                "A profile name is lowercase letters, digits, '.', '-' and '_', starting with a letter or digit, such as dev or " +
                "prod. A key is never a profile name.");
        return chosen;
    }

    /// <summary>
    /// <see cref="Resolve(ArgMap, bool)"/> for a command that acts on a deployment file's workspace: pointed at the
    /// file's tenant, and refused when <c>--tenant</c> or <c>QUEUEY_TENANT</c>, or the profile's connection, names another one.
    /// </summary>
    public static ResolvedConfig ResolveForDeployment(ArgMap args, string? fileTenant, string filePath)
        => DeploymentTenant.Resolve(Resolve(args, profiles: true), args, Env, fileTenant, filePath);

    // Miljøet konfigurasjonen leses fra. En søm av samme grunn som TestHandler: en test skal ikke måtte
    // endre prosessens miljø for å styre QUEUEY_TENANT.
    internal static Func<string, string?> Env { get; set; } = Environment.GetEnvironmentVariable;

    // Testsøm: CLI-testene kjører kommandoene i prosessen. Står en handler her, går hvert kall dit i
    // stedet for ut på nettet, så en test ser nøyaktig hva en kommando ville sendt. Alltid null ellers.
    internal static HttpMessageHandler? TestHandler { get; set; }

    // Testsøm for queuey listen, som skriver strømmen sin rett til fd 1 og ikke gjennom Console.Out (review av
    // queuey-client #50, K3b). Står en writer her, går strømmen dit. Alltid null ellers.
    internal static TextWriter? StreamOut { get; set; }

    public static ServiceProvider BuildProvider(ResolvedConfig config, Action<IQueueyBuilder>? build = null)
    {
        var services = new ServiceCollection();
        services.AddQueuey(config.Apply, build);
        if (TestHandler is { } handler)
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => handler));
        return services.BuildServiceProvider();
    }

    /// <summary>Masks a <c>qak_&lt;keyId&gt;.&lt;secret&gt;</c> key to <c>qak_&lt;keyId&gt;.**** (set)</c>.</summary>
    public static string MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(not set)";
        int dot = key.IndexOf('.');
        string prefix = dot > 0 ? key.Substring(0, dot) : (key.Length > 6 ? key.Substring(0, 6) : key);
        return prefix + ".**** (set)";
    }

    private static string? ReadConfigJson(ArgMap args)
    {
        string path = args.Get("config") ?? "queuey.json";
        return File.Exists(path) ? CliFiles.ReadAllText(path) : null;
    }
}
