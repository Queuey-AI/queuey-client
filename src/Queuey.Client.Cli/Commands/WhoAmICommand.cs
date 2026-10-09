using System;
using System.Text.Json;

namespace Queuey.Client.Cli;

internal static class WhoAmICommand
{
    internal static readonly CommandOptions Options = new("whoami", flags: new[] { "json" }, values: new[] { "profile" });

    public static int Run(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        // whoami viser tilkoblingen, så med en profil er det profilens (F2.7). Den leser ikke deploy-fila: den kobler ikke til.
        ResolvedConfig config = CliHost.Resolve(map, profiles: true);

        if (map.Has("json"))
        {
            var payload = new
            {
                environment = config.HostsLabel(),
                apiHost = config.ResolvedApiBase().ToString(),
                ingressHost = config.ResolvedIngressBase().ToString(),
                tenant = config.TenantPublicId,
                license = config.LicensePublicId,
                apiKeySet = !string.IsNullOrEmpty(config.ApiKey),
                // Med en profil (F2.7): hvilken, og fila tilkoblingen ble lest fra. Null uten.
                profile = config.Profile,
                profileFile = config.ProfileFile,
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        Console.WriteLine("Queuey CLI");
        if (config.Profile is { } profile)
            Console.WriteLine($"  Profile     : {profile} ({config.ProfileFile})");
        Console.WriteLine($"  Environment : {config.HostsLabel()}");
        Console.WriteLine($"  API host    : {config.ResolvedApiBase()}");
        Console.WriteLine($"  Ingress host: {config.ResolvedIngressBase()}");
        Console.WriteLine($"  Tenant      : {config.TenantPublicId ?? "(not set)"}");
        Console.WriteLine($"  License     : {config.LicensePublicId ?? "(not set)"}");
        Console.WriteLine($"  API key     : {CliHost.MaskKey(config.ApiKey)}");
        return ExitCodes.Success;
    }
}
