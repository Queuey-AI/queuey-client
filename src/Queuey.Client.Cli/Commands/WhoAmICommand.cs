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

        // whoami viser tilkoblingen, så med en profil er det profilens (F2.7). Den leser ikke deploy-fila og kobler ikke til, så et
        // utløpt token fornyes ikke her: innloggingen vises slik den er lagret.
        ResolvedConfig config = CliHost.Resolve(map, profiles: true);

        if (map.Has("json"))
        {
            var payload = new
            {
                // «hosts», ikke «environment» (review av #65, K1): det er vertene, og et workspace har sitt eget miljø.
                hosts = config.HostsLabel(),
                apiHost = config.ResolvedApiBase().ToString(),
                ingressHost = config.ResolvedIngressBase().ToString(),
                tenant = config.TenantPublicId,
                license = config.LicensePublicId,
                apiKeySet = !string.IsNullOrEmpty(config.ApiKey),
                // Innloggingen kommandoene bruker uten nøkkel (queuey login): null med en nøkkel, eller uten innlogging. Aldri et token.
                login = config.Login?.Login is { } login
                    ? new
                    {
                        license = login.License,
                        scope = login.Scope,
                        client = login.ClientId,
                        user = login.User,
                        loggedInAt = login.LoggedInAt,
                        accessTokenExpiresAt = login.AccessTokenExpiresAt,
                        renewable = !string.IsNullOrWhiteSpace(login.RefreshToken),
                    }
                    : null,
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
        Console.WriteLine($"  Hosts       : {config.HostsLabel()}");
        Console.WriteLine($"  API host    : {config.ResolvedApiBase()}");
        Console.WriteLine($"  Ingress host: {config.ResolvedIngressBase()}");
        Console.WriteLine($"  Tenant      : {config.TenantPublicId ?? "(not set)"}");
        Console.WriteLine($"  License     : {config.LicensePublicId ?? "(not set)"}");
        Console.WriteLine($"  API key     : {CliHost.MaskKey(config.ApiKey)}");
        if (config.Login?.Login is { } stored)
        {
            string person = stored.User is null ? "" : $"{TerminalText.Line(stored.User)} via ";
            Console.WriteLine($"  Login       : {person}{stored.ClientId}, license {stored.License}, scope {stored.Scope}, " +
                              $"logged in {stored.LoggedInAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        }
        else if (string.IsNullOrEmpty(config.ApiKey))
        {
            Console.WriteLine("  Login       : (none; run `queuey login`)");
        }
        return ExitCodes.Success;
    }
}
