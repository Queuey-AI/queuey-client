using System;
using System.Collections.Generic;
using Queuey.Client;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

public class CliConfigTests
{
    private static readonly HashSet<string> NoFlags = new(StringComparer.Ordinal);

    private static Func<string, string?> Env(params (string Key, string Value)[] vars)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string value) in vars) dict[key] = value;
        return name => dict.TryGetValue(name, out string? v) ? v : null;
    }

    [Fact]
    public void Defaults_to_production_hosts()
    {
        ResolvedConfig config = CliConfig.Resolve(ArgMap.Parse(Array.Empty<string>(), NoFlags), _ => null, null);

        Assert.Equal(QueueyEnvironment.Production, config.Environment);
        Assert.Equal("https://api.queuey.ai/", config.ResolvedApiBase().ToString());
        Assert.Equal("https://ingress.queuey.ai/", config.ResolvedIngressBase().ToString());
    }

    [Fact]
    public void Flag_beats_env_beats_json()
    {
        string json = "{\"apiKey\":\"json-key\"}";

        // json only
        ResolvedConfig fromJson = CliConfig.Resolve(ArgMap.Parse(Array.Empty<string>(), NoFlags), _ => null, json);
        Assert.Equal("json-key", fromJson.ApiKey);

        // env beats json
        ResolvedConfig fromEnv = CliConfig.Resolve(ArgMap.Parse(Array.Empty<string>(), NoFlags), Env(("QUEUEY_API_KEY", "env-key")), json);
        Assert.Equal("env-key", fromEnv.ApiKey);

        // flag beats env
        ResolvedConfig fromFlag = CliConfig.Resolve(ArgMap.Parse(new[] { "--api-key", "flag-key" }, NoFlags), Env(("QUEUEY_API_KEY", "env-key")), json);
        Assert.Equal("flag-key", fromFlag.ApiKey);
    }

    [Fact]
    public void Environment_development_resolves_dev_hosts()
    {
        ResolvedConfig config = CliConfig.Resolve(ArgMap.Parse(Array.Empty<string>(), NoFlags), Env(("QUEUEY_ENV", "development")), null);

        Assert.Equal(QueueyEnvironment.Development, config.Environment);
        Assert.Equal("https://devapi.queuey.ai/", config.ResolvedApiBase().ToString());
    }

    [Fact]
    public void Explicit_local_hosts_are_applied()
    {
        ResolvedConfig config = CliConfig.Resolve(
            ArgMap.Parse(new[] { "--api-base", "http://localhost:5223", "--ingress-base", "http://localhost:5084" }, NoFlags),
            _ => null, null);

        Assert.Equal("http://localhost:5223/", config.ResolvedApiBase().ToString());
        Assert.Equal("http://localhost:5084/", config.ResolvedIngressBase().ToString());
    }

    [Fact]
    public void Resolved_config_applies_onto_options()
    {
        ResolvedConfig config = CliConfig.Resolve(
            ArgMap.Parse(new[] { "--tenant", "ten_abc", "--license", "lic_1", "--api-key", "qak_k.s" }, NoFlags),
            _ => null, null);

        var options = new QueueyOptions();
        config.Apply(options);

        Assert.Equal("ten_abc", options.TenantPublicId);
        Assert.Equal("lic_1", options.LicensePublicId);
        Assert.Equal("qak_k.s", options.ApiKey);
    }
}
