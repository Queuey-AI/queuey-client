using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Funnene fra Kenneths andre blindtest (2026-10-09), del A: workspacet fra deploy-fila, advise som følger skillen,
/// create-tenant med profilen, event-id-en i Edge og templateKey i forslagene.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class BlindTest2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-blind2", Guid.NewGuid().ToString("N"));
    private readonly string _before = Directory.GetCurrentDirectory();

    public BlindTest2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_before);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string DevProfileWithoutTenant = """
        { "profiles": { "dev": { "apiKey": "qak_dev.key-for-dev", "license": "lic_dev",
                                 "apiBase": "https://api.test", "ingressBase": "https://ingress.test" } } }
        """;

    private Dictionary<string, string> Env()
        => new(StringComparer.Ordinal) { [UserProfiles.PathVariable] = UserProfilesTests.WriteUserConfig(_dir, DevProfileWithoutTenant) };

    private void DeployFile(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "queuey.deploy.json"), json);
        Directory.SetCurrentDirectory(_dir);
    }

    private static RecordingHandler Minting(string tenant) => new(req => req.Key == $"POST /hmacclients/tenants/{tenant}"
        ? RecordingHandler.Json(HttpStatusCode.OK, new
        {
            clientPublicId = "hcl_1", clientName = "queuey-cli", keyId = "hsk_01WS", secret = "s3cr3t-value", scope = "workspace",
            tenantPublicId = tenant, type = "signing",
        })
        : throw new InvalidOperationException(req.Key));

    // ── #5: workspacet fra deploy-fila ──────────────────────────────────────

    [Fact]
    public async Task Keys_mint_with_a_profile_takes_the_workspace_the_deployment_file_names_for_it_and_says_so()
    {
        DeployFile("""
            { "tenant": "${QUEUEY_TENANT}", "queues": {},
              "profiles": { "dev": { "variables": { "QUEUEY_TENANT": "ten_file" } } } }
            """);
        RecordingHandler api = Minting("ten_file");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "keys", "mint", "--profile", "dev", "--write", ".env", "--json" }), api, Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("POST /hmacclients/tenants/ten_file", Assert.Single(api.Requests).Key);
        Assert.Contains("Workspace ten_file from queuey.deploy.json (profile dev).", run.Stderr);
        Assert.Equal("queuey.deploy.json (profile dev)", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("workspaceFrom").GetString());
    }

    [Fact]
    public async Task A_tenant_flag_wins_over_the_deployment_file()
    {
        DeployFile("""{ "tenant": "ten_file", "queues": {} }""");
        RecordingHandler api = Minting("ten_flag");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "keys", "mint", "--profile", "dev", "--tenant", "ten_flag", "--write", ".env", "--json" }), api, Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("POST /hmacclients/tenants/ten_flag", Assert.Single(api.Requests).Key);
        Assert.DoesNotContain("from queuey.deploy.json", run.Stderr);
    }

    [Fact]
    public async Task Credentials_generate_takes_the_workspace_from_the_deployment_file_too()
    {
        DeployFile("""{ "tenant": "ten_file", "queues": {} }""");
        var api = new RecordingHandler(req => req.Key == "POST /tenants/ten_file/credentials"
            ? RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "cred_1", name = "warehouse-delivery", type = "HmacSigning", created = true })
            : throw new InvalidOperationException(req.Key));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[]
        {
            "credentials", "generate", "warehouse-delivery", "--write", ".env", "--api-key", "qak_dev.key-for-dev", "--license", "lic_dev",
            "--api-base", "https://api.test", "--json",
        }), api, new Dictionary<string, string> { [UserProfiles.PathVariable] = Path.Combine(_dir, "none.json") });

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("POST /tenants/ten_file/credentials", Assert.Single(api.Requests).Key);
        Assert.Contains("Workspace ten_file from queuey.deploy.json.", run.Stderr);
    }

    [Fact]
    public async Task Without_any_workspace_the_refusal_names_the_deployment_file_as_a_place_for_it()
    {
        Directory.SetCurrentDirectory(_dir);
        var api = new RecordingHandler(req => throw new InvalidOperationException(req.Key));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "keys", "mint", "--profile", "dev", "--write", ".env" }), api, Env());

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("tenant in queuey.deploy.json", run.Stdout + run.Stderr);
        Assert.Empty(api.Requests);
    }
}
