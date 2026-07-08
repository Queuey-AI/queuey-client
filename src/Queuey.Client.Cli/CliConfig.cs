using System;
using System.Linq;
using System.Text.Json;
using Queuey.Client;

namespace Queuey.Client.Cli;

/// <summary>The resolved connection config for a CLI invocation.</summary>
internal sealed class ResolvedConfig
{
    public QueueyEnvironment Environment { get; init; } = QueueyEnvironment.Production;
    public Uri? ApiBaseOverride { get; init; }
    public Uri? IngressBaseOverride { get; init; }
    public string? ApiKey { get; init; }
    public string? TenantPublicId { get; init; }
    public string? LicensePublicId { get; init; }
    public string? Source { get; init; }

    /// <summary>Applies the resolved values onto a <see cref="QueueyOptions"/>.</summary>
    public void Apply(QueueyOptions options)
    {
        options.Environment = Environment;
        if (ApiBaseOverride != null) options.ApiBaseAddress = ApiBaseOverride;
        if (IngressBaseOverride != null) options.IngressBaseAddress = IngressBaseOverride;
        options.ApiKey = ApiKey;
        options.TenantPublicId = TenantPublicId;
        options.LicensePublicId = LicensePublicId;
        options.Source = Source;
    }

    public Uri ResolvedApiBase() => ToOptions().ResolveApiBaseAddress();
    public Uri ResolvedIngressBase() => ToOptions().ResolveIngressBaseAddress();

    private QueueyOptions ToOptions()
    {
        var o = new QueueyOptions { Environment = Environment };
        if (ApiBaseOverride != null) o.ApiBaseAddress = ApiBaseOverride;
        if (IngressBaseOverride != null) o.IngressBaseAddress = IngressBaseOverride;
        return o;
    }
}

/// <summary>Resolves connection config with precedence: CLI flag &gt; environment variable &gt; queuey.json &gt; default.</summary>
internal static class CliConfig
{
    public static ResolvedConfig Resolve(ArgMap args, Func<string, string?> getEnv, string? configJson)
    {
        FileConfig file = ParseFile(configJson);

        return new ResolvedConfig
        {
            Environment = ParseEnvironment(First(args.Get("env"), getEnv("QUEUEY_ENV"), file.Environment)),
            ApiBaseOverride = ParseUri(First(args.Get("api-base"), getEnv("QUEUEY_API_BASE"), file.ApiBase)),
            IngressBaseOverride = ParseUri(First(args.Get("ingress-base"), getEnv("QUEUEY_INGRESS_BASE"), file.IngressBase)),
            ApiKey = First(args.Get("api-key"), getEnv("QUEUEY_API_KEY"), file.ApiKey),
            TenantPublicId = First(args.Get("tenant"), getEnv("QUEUEY_TENANT"), file.Tenant),
            LicensePublicId = First(args.Get("license"), getEnv("QUEUEY_LICENSE"), file.License),
            Source = First(args.Get("source"), getEnv("QUEUEY_SOURCE"), file.Source),
        };
    }

    private static string? First(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // Only Production is a built-in environment; point at a locally-running instance with
    // --api-base / --ingress-base (QUEUEY_API_BASE / QUEUEY_INGRESS_BASE) — e.g. for testing.
    private static QueueyEnvironment ParseEnvironment(string? value) => QueueyEnvironment.Production;

    private static Uri? ParseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            throw new QueueyConfigurationException($"Invalid absolute URL: '{value}'.");
        return uri;
    }

    private static FileConfig ParseFile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FileConfig();
        try
        {
            return JsonSerializer.Deserialize<FileConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new FileConfig();
        }
        catch (JsonException ex)
        {
            throw new QueueyConfigurationException($"Could not parse queuey.json: {ex.Message}");
        }
    }

    private sealed class FileConfig
    {
        public string? Environment { get; set; }
        public string? ApiBase { get; set; }
        public string? IngressBase { get; set; }
        public string? ApiKey { get; set; }
        public string? Tenant { get; set; }
        public string? License { get; set; }
        public string? Source { get; set; }
    }
}
