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

    public static ResolvedConfig Resolve(ArgMap args)
        => CliConfig.Resolve(args, Env, ReadConfigJson(args));

    /// <summary>
    /// <see cref="Resolve"/> for a command that acts on a deployment file's workspace: pointed at the
    /// file's tenant, and refused when <c>--tenant</c> or <c>QUEUEY_TENANT</c> names another one.
    /// </summary>
    public static ResolvedConfig ResolveForDeployment(ArgMap args, string? fileTenant, string filePath)
        => DeploymentTenant.Resolve(Resolve(args), args, Env, fileTenant, filePath);

    // Miljøet konfigurasjonen leses fra. En søm av samme grunn som TestHandler: en test skal ikke måtte
    // endre prosessens miljø for å styre QUEUEY_TENANT.
    internal static Func<string, string?> Env { get; set; } = Environment.GetEnvironmentVariable;

    // Testsøm: CLI-testene kjører kommandoene i prosessen. Står en handler her, går hvert kall dit i
    // stedet for ut på nettet, så en test ser nøyaktig hva en kommando ville sendt. Alltid null ellers.
    internal static HttpMessageHandler? TestHandler { get; set; }

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
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
