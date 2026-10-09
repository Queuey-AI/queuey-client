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
            ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01A",
            ["QUEUEY_SIGNING_SECRET"] = " s3cret ",
            ["QUEUEY_TENANT"] = "ten_1",
            ["QUEUEY_INGRESS_BASE"] = "http://localhost:5084",
        }));

        Assert.Equal("hsk_01A", options.SigningKeyId);
        Assert.Equal("s3cret", options.SigningSecret);
        Assert.Equal("ten_1", options.TenantPublicId);
        Assert.Equal(new Uri("http://localhost:5084"), options.IngressBaseAddress);
        Assert.Null(options.ApiKey);
        Assert.IsType<HmacRequestSigner>(QueueyClient.BuildIngressAuthenticator(options));
    }

    [Fact]
    public void With_a_signing_pair_in_the_environment_the_license_wide_api_key_is_not_read()
    {
        // Security-review av #67 (KAN G): minste privilegium. En satt API-nøkkel ville vunnet over signeringen ved publisering.
        var both = new QueueyOptions().UseEnvironmentVariables(Env(new()
        {
            ["QUEUEY_API_KEY"] = "qak_wide.key", ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01A", ["QUEUEY_SIGNING_SECRET"] = "s",
        }));
        Assert.Null(both.ApiKey);
        Assert.IsType<HmacRequestSigner>(QueueyClient.BuildIngressAuthenticator(both));

        var keyOnly = new QueueyOptions().UseEnvironmentVariables(Env(new() { ["QUEUEY_API_KEY"] = "qak_wide.key" }));
        Assert.Equal("qak_wide.key", keyOnly.ApiKey);
    }

    [Fact]
    public void A_value_set_in_code_wins_over_the_environment()
    {
        var options = new QueueyOptions { SigningKeyId = "from-code", TenantPublicId = "ten_code" }
            .UseEnvironmentVariables(Env(new() { ["QUEUEY_SIGNING_KEY_ID"] = "hsk_01ENV", ["QUEUEY_TENANT"] = "ten_env" }));

        Assert.Equal("from-code", options.SigningKeyId);
        Assert.Equal("ten_code", options.TenantPublicId);
    }
}
