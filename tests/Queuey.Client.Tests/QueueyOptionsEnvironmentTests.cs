using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// .env i Development (blindtesten 2026-10-09, funn 9): .NET leser ikke .env selv, så nøkkelen `queuey keys mint --write .env`
/// skrev, nådde ikke appen. Bare QUEUEY_*-navn, bare en vanlig fil eid av brukeren som git ikke sporer, og miljøet vinner.
/// </summary>
[Collection("DotEnv")]
public sealed class DotEnvFileTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "queuey-dotenv-tests", Guid.NewGuid().ToString("N"));
    private readonly Func<string, string[], int?> _git = DotEnvFile.Git;
    private readonly Func<string, uint?> _owner = DotEnvFile.OwnerOf;
    private readonly Func<uint?> _me = DotEnvFile.CurrentUser;

    public DotEnvFileTests()
    {
        System.IO.Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(_dir, ".env"),
            "# app\nDATABASE_URL=postgres://x\nexport QUEUEY_SIGNING_KEY_ID=hsk_01FILE\nQUEUEY_SIGNING_SECRET='file secret'\nQUEUEY_TENANT=ten_file\n");
        DotEnvFile.Git = (_, _) => 1;       // ikke sporet
        DotEnvFile.OwnerOf = _ => 501;
        DotEnvFile.CurrentUser = () => 501;
    }

    public void Dispose()
    {
        DotEnvFile.Git = _git;
        DotEnvFile.OwnerOf = _owner;
        DotEnvFile.CurrentUser = _me;
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
        => name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void In_development_the_queuey_names_in_dot_env_fill_what_the_environment_does_not_set()
    {
        var options = new QueueyOptions().UseEnvironmentVariables(Env(("DOTNET_ENVIRONMENT", "Development"), ("QUEUEY_TENANT", "ten_env")), _dir);

        Assert.Equal("hsk_01FILE", options.SigningKeyId);
        Assert.Equal("file secret", options.SigningSecret);
        Assert.Equal("ten_env", options.TenantPublicId); // miljøet vinner
        Assert.Null(options.ApiKey);
        Assert.Equal(new[] { "QUEUEY_SIGNING_KEY_ID", "QUEUEY_SIGNING_SECRET", "QUEUEY_TENANT" }, DotEnvFile.Read(_dir).Keys.OrderBy(k => k).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_development_dot_env_is_never_read(string? environment)
    {
        var options = new QueueyOptions().UseEnvironmentVariables(
            environment is null ? Env() : Env(("ASPNETCORE_ENVIRONMENT", environment)), _dir);

        Assert.Null(options.SigningKeyId);
        Assert.Null(options.TenantPublicId);
    }

    [Fact]
    public void A_dot_env_git_tracks_or_another_user_owns_is_not_read()
    {
        DotEnvFile.Git = (_, _) => 0;
        Assert.Empty(DotEnvFile.Read(_dir));

        DotEnvFile.Git = (_, _) => 1;
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            DotEnvFile.OwnerOf = _ => 0;
            Assert.Empty(DotEnvFile.Read(_dir));
            DotEnvFile.OwnerOf = _ => null; // eieren kan ikke leses: ikke lest
            Assert.Empty(DotEnvFile.Read(_dir));
        }

        // git kan ikke svare inne i et repo: ikke lest.
        DotEnvFile.OwnerOf = _ => 501;
        DotEnvFile.Git = (_, _) => null;
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(_dir, ".git"));
        Assert.Empty(DotEnvFile.Read(_dir));
    }

    [Fact]
    public void The_real_owner_check_finds_the_users_own_file()
    {
        DotEnvFile.OwnerOf = _owner;
        DotEnvFile.CurrentUser = _me;
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return;

        Assert.Equal(DotEnvFile.CurrentUser(), DotEnvFile.OwnerOf(System.IO.Path.Combine(_dir, ".env")));
        Assert.NotEmpty(DotEnvFile.Read(_dir));
    }

    [Fact]
    public void A_link_named_dot_env_is_not_read()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return;
        string other = System.IO.Path.Combine(_dir, "other");
        System.IO.Directory.CreateDirectory(other);
        System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(other, ".env"), System.IO.Path.Combine(_dir, ".env"));

        Assert.Empty(DotEnvFile.Read(other));
    }

    [Theory]
    [InlineData("QUEUEY_A=plain", "QUEUEY_A", "plain")]
    [InlineData("export QUEUEY_A='x y'", "QUEUEY_A", "x y")]
    [InlineData("QUEUEY_A=\"a\\\"b\"", "QUEUEY_A", "a\"b")]
    [InlineData("QUEUEY_A=v # note", "QUEUEY_A", "v")]
    public void A_line_is_read_as_dotenv_files_write_it(string line, string name, string value)
        => Assert.Equal((name, value), DotEnvFile.Parse(line));
}
