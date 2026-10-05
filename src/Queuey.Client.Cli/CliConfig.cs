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

    /// <summary>The same config pointed at another tenant — the one a deployment file names.</summary>
    public ResolvedConfig WithTenant(string? tenantPublicId) => new()
    {
        Environment = Environment,
        ApiBaseOverride = ApiBaseOverride,
        IngressBaseOverride = IngressBaseOverride,
        ApiKey = ApiKey,
        TenantPublicId = string.IsNullOrWhiteSpace(tenantPublicId) ? TenantPublicId : tenantPublicId,
        LicensePublicId = LicensePublicId,
        Source = Source,
    };

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
            TenantPublicId = Workspace(
                (args.Get("tenant"), "--tenant"),
                (getEnv("QUEUEY_TENANT"), "QUEUEY_TENANT"),
                (file.Tenant, $"tenant in {args.Get("config") ?? "queuey.json"}")),
            LicensePublicId = First(args.Get("license"), getEnv("QUEUEY_LICENSE"), file.License),
            Source = First(args.Get("source"), getEnv("QUEUEY_SOURCE"), file.Source),
        };
    }

    private static string? First(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>The first tenant that is set, by precedence, which has to be a workspace id (<see cref="WorkspaceId"/>).</summary>
    private static string? Workspace(params (string? Value, string Source)[] candidates)
    {
        foreach ((string? value, string source) in candidates)
            if (!string.IsNullOrWhiteSpace(value))
                return WorkspaceId(value!, source);
        return null;
    }

    /// <summary>
    /// <paramref name="value"/>, trimmed, when it is a workspace id (<c>ten_…</c>). Anything else is a usage error that
    /// names <paramref name="source"/> and never the value: an API key put there by mistake was sent in request URLs
    /// (<c>/tenants/&lt;value&gt;/…</c>) and printed by whoami.
    /// </summary>
    // Re-review 2026-10-05: en API-nøkkel i QUEUEY_TENANT, en forveksling i CI, ble brukt som workspace og skrevet ut. Sjekket
    // der en tenant leses fra kommandolinjen, miljøet eller queuey.json, ikke først når den brukes.
    internal static string WorkspaceId(string value, string source)
    {
        string trimmed = value.Trim();
        if (CliErrors.LooksLikeAWorkspaceId(trimmed))
            return trimmed;

        throw new CliUsageException("invalid_value",
            $"{source} is not a workspace id. Its value is not shown, since it may be a secret.",
            "A workspace id starts with ten_. An API key belongs in --api-key or QUEUEY_API_KEY.");
    }

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
