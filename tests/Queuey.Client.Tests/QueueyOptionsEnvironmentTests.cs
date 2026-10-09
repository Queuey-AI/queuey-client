using System;
using System.Collections.Generic;
using Queuey.Client;

namespace Queuey.Client.Tests;

/// <summary>
/// Miljøvariablene SDK-en leser (2026-10-09): det `queuey keys mint --write .env` skriver, skal en produsent kunne bruke uten å
/// lese variablene selv. En verdi satt i koden vinner.
/// </summary>
public class QueueyOptionsEnvironmentTests
{
    private static Func<string, string?> Env(Dictionary<string, string> values) => name => values.TryGetValue(name, out string? v) ? v : null;

    [Fact]
    public void The_signing_key_queuey_keys_mint_writes_is_read_from_the_environment()
    {
        var options = new QueueyOptions().UseEnvironmentVariables(Env(new()
        {
            ["QUEUEY_SIGNING_KEY_ID"] = "hk_1",
            ["QUEUEY_SIGNING_SECRET"] = " s3cret ",
            ["QUEUEY_TENANT"] = "ten_1",
            ["QUEUEY_INGRESS_BASE"] = "http://localhost:5084",
        }));

        Assert.Equal("hk_1", options.SigningKeyId);
        Assert.Equal("s3cret", options.SigningSecret);
        Assert.Equal("ten_1", options.TenantPublicId);
        Assert.Equal(new Uri("http://localhost:5084"), options.IngressBaseAddress);
        Assert.Null(options.ApiKey);
        Assert.IsType<HmacRequestSigner>(QueueyClient.BuildIngressAuthenticator(options));
    }

    [Fact]
    public void A_value_set_in_code_wins_over_the_environment()
    {
        var options = new QueueyOptions { SigningKeyId = "from-code", TenantPublicId = "ten_code" }
            .UseEnvironmentVariables(Env(new() { ["QUEUEY_SIGNING_KEY_ID"] = "hk_env", ["QUEUEY_TENANT"] = "ten_env" }));

        Assert.Equal("from-code", options.SigningKeyId);
        Assert.Equal("ten_code", options.TenantPublicId);
    }
}
