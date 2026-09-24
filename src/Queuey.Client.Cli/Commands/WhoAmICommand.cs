using System;
using System.Text.Json;

namespace Queuey.Client.Cli;

internal static class WhoAmICommand
{
    internal static readonly CommandOptions Options = new("whoami", flags: new[] { "json" });

    public static int Run(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        ResolvedConfig config = CliHost.Resolve(map);

        if (map.Has("json"))
        {
            var payload = new
            {
                environment = config.Environment.ToString(),
                apiHost = config.ResolvedApiBase().ToString(),
                ingressHost = config.ResolvedIngressBase().ToString(),
                tenant = config.TenantPublicId,
                license = config.LicensePublicId,
                apiKeySet = !string.IsNullOrEmpty(config.ApiKey),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine("Queuey CLI");
        Console.WriteLine($"  Environment : {config.Environment}");
        Console.WriteLine($"  API host    : {config.ResolvedApiBase()}");
        Console.WriteLine($"  Ingress host: {config.ResolvedIngressBase()}");
        Console.WriteLine($"  Tenant      : {config.TenantPublicId ?? "(not set)"}");
        Console.WriteLine($"  License     : {config.LicensePublicId ?? "(not set)"}");
        Console.WriteLine($"  API key     : {CliHost.MaskKey(config.ApiKey)}");
        return ExitCodes.Success;
    }
}
