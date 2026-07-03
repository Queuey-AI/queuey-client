using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Examples;

/// <summary>
/// Connection config, read from environment variables with local-dev defaults. In a real app you'd
/// bind these from configuration / a secret store — never hard-code the API key.
/// </summary>
public sealed class ExampleConfig
{
    public string ApiBase { get; init; } = "http://localhost:5223";
    public string IngressBase { get; init; } = "http://localhost:5084";
    public string? Tenant { get; init; }
    public string? License { get; init; }
    public string? ApiKey { get; init; }

    public static ExampleConfig FromEnvironment() => new()
    {
        ApiBase = Env("QUEUEY_API_BASE", "http://localhost:5223"),
        IngressBase = Env("QUEUEY_INGRESS_BASE", "http://localhost:5084"),
        Tenant = Env("QUEUEY_TENANT", "ten_your_tenant"),
        License = Env("QUEUEY_LICENSE", "lic_your_license"),
        ApiKey = Env("QUEUEY_API_KEY", "qak_your.key"),
    };

    /// <summary>Applies this config onto a <see cref="QueueyOptions"/> (used by AddQueuey).</summary>
    public void Apply(QueueyOptions options)
    {
        options.ApiBaseAddress = new Uri(ApiBase);
        options.IngressBaseAddress = new Uri(IngressBase);
        options.TenantPublicId = Tenant;
        options.LicensePublicId = License;
        options.ApiKey = ApiKey;
    }

    public void Print()
    {
        Console.WriteLine("Config (set QUEUEY_* env vars to override):");
        Console.WriteLine($"  API host     : {ApiBase}");
        Console.WriteLine($"  Ingress host : {IngressBase}");
        Console.WriteLine($"  Tenant       : {Tenant}");
        Console.WriteLine($"  License      : {License}");
        Console.WriteLine($"  API key      : {Mask(ApiKey)}");
    }

    private static string Env(string key, string fallback)
    {
        string? v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(not set)";
        int dot = key.IndexOf('.');
        return (dot > 0 ? key[..dot] : key) + ".**** ";
    }
}
