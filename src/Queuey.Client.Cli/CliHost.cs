using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>Shared helpers: config resolution, DI host construction, and output formatting.</summary>
internal static class CliHost
{
    public static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ResolvedConfig Resolve(ArgMap args)
        => CliConfig.Resolve(args, Environment.GetEnvironmentVariable, ReadConfigJson(args));

    public static ServiceProvider BuildProvider(ResolvedConfig config, Action<IQueueyBuilder>? build = null)
    {
        var services = new ServiceCollection();
        services.AddQueuey(config.Apply, build);
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
