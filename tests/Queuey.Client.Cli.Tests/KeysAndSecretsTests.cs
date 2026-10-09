using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Nøkler og hemmeligheter etter Queuey #513 (2026-10-09): workspace-nøkler, --type api-key, approval_required, --write
/// user-secrets og credentials generate. Hemmeligheten står aldri i utdataene eller i argumentene til en prosess.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class KeysAndSecretsTests : IDisposable
{
    private const string Secret = "s3cr3t-Workspace-Value";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-keys-secrets", Guid.NewGuid().ToString("N"));
    private readonly Func<string?> _path = UserSecretsTarget.PathVariable;
    private readonly Func<string> _folder = UserSecretsTarget.WorkingFolder;

    public KeysAndSecretsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        UserSecretsTarget.PathVariable = _path;
        UserSecretsTarget.WorkingFolder = _folder;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Task<CliRun> Run(RecordingHandler api, params string[] args)
        => CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(args)), api);

    private static RecordingHandler Server(Func<RecordedRequest, HttpResponseMessage>? mint = null) => new(req => req.Key switch
    {
        "POST /hmacclients/tenants/ten_1" or "POST /hmacclients/queues/que_1" => mint?.Invoke(req) ?? RecordingHandler.Json(HttpStatusCode.OK, new
        {
            clientPublicId = "hcl_1", clientName = "queuey-cli", keyId = "hsk_01WS", secret = Secret, queuePublicId = (string?)null,
            scope = "workspace", tenantPublicId = "ten_1", origin = "Ada via Queuey CLI", type = "signing",
        }),
        _ => throw new InvalidOperationException(req.Key),
    });

    // ── keys mint (Queuey #513) ─────────────────────────────────────────────

    [Fact]
    public async Task Without_a_queue_keys_mint_mints_for_the_workspace()
    {
        RecordingHandler api = Server();
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(api, "keys", "mint", "--tenant", "ten_1", "--write", env, "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        RecordedRequest sent = Assert.Single(api.Requests);
        Assert.Equal("POST /hmacclients/tenants/ten_1", sent.Key);
        Assert.False(sent.Json.TryGetProperty("type", out _)); // signing er standard, og sendes ikke
        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("workspace", json.GetProperty("scope").GetString());
        Assert.Equal("ten_1", json.GetProperty("tenantPublicId").GetString());
        Assert.Equal("Ada via Queuey CLI", json.GetProperty("origin").GetString());
        Assert.Equal("signing", json.GetProperty("type").GetString());
        Assert.Contains($"QUEUEY_SIGNING_SECRET={Secret}", File.ReadAllText(env));
        Assert.DoesNotContain(Secret, run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task Type_api_key_mints_a_publishing_key_written_as_QUEUEY_API_KEY()
    {
        RecordingHandler api = Server(_ => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            clientPublicId = "acl_1", clientName = "queuey-cli", keyId = "kid1", secret = "qak_kid1.publishonly", queuePublicId = "que_1",
            scope = "queue", tenantPublicId = "ten_1", type = "api-key",
        }));
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(api, "keys", "mint", "--queue", "que_1", "--type", "api-key", "--write", env);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("api-key", Assert.Single(api.Requests).Json.GetProperty("type").GetString());
        Assert.Equal("QUEUEY_API_KEY=qak_kid1.publishonly\n", File.ReadAllText(env));
        Assert.Contains("a publishing API key", run.Stdout);
        Assert.DoesNotContain("publishonly", run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task Approval_required_exits_5_with_the_link_and_writes_nothing()
    {
        RecordingHandler api = Server(_ => RecordingHandler.Json(HttpStatusCode.Forbidden, new
        {
            error = new
            {
                code = "approval_required",
                message = "A login does not mint signing keys in a prod workspace.",
                action = "A person makes it at https://app.test/console/t/ten_1?tab=security",
            },
        }));
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(api, "keys", "mint", "--tenant", "ten_1", "--write", env, "--json");

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("approval_required", json.GetProperty("status").GetString());
        Assert.Contains("https://app.test/console/t/ten_1?tab=security", json.GetProperty("action").GetString());
        Assert.False(File.Exists(env));
    }

    // ── --write user-secrets ────────────────────────────────────────────────

    /// <summary>A fake dotnet that logs its arguments and its stdin, so the test can see the secret never was an argument.</summary>
    private (string Args, string Stdin) FakeDotnet(int exit = 0)
    {
        string bin = Path.Combine(_dir, "bin"), args = Path.Combine(_dir, "dotnet-args.log"), stdin = Path.Combine(_dir, "dotnet-stdin.log");
        Directory.CreateDirectory(bin);
        string dotnet = Path.Combine(bin, "dotnet");
        File.WriteAllText(dotnet, $"#!/bin/sh\necho \"$@\" >> '{args}'\ncat >> '{stdin}'\nexit {exit}\n");
        File.SetUnixFileMode(dotnet, (UnixFileMode)0x1C0);
        UserSecretsTarget.PathVariable = () => bin;
        return (args, stdin);
    }

    private string Project(bool withId = true)
    {
        string project = Path.Combine(_dir, "app");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Shop.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup>{(withId ? "<UserSecretsId>shop-123</UserSecretsId>" : "")}</PropertyGroup></Project>");
        UserSecretsTarget.WorkingFolder = () => project;
        return project;
    }

    [Fact]
    public async Task User_secrets_get_the_key_on_stdin_never_as_an_argument()
    {
        if (OperatingSystem.IsWindows())
            return;
        (string args, string stdin) = FakeDotnet();
        Project();

        CliRun run = await Run(Server(), "keys", "mint", "--tenant", "ten_1", "--write", "user-secrets", "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        string argv = File.ReadAllText(args);
        Assert.StartsWith("user-secrets set --project ", argv);
        Assert.EndsWith("Shop.csproj\n", argv);
        Assert.DoesNotContain(Secret, argv);
        JsonElement sent = JsonDocument.Parse(File.ReadAllText(stdin)).RootElement;
        Assert.Equal("hsk_01WS", sent.GetProperty("QUEUEY_SIGNING_KEY_ID").GetString());
        Assert.Equal(Secret, sent.GetProperty("QUEUEY_SIGNING_SECRET").GetString());
        Assert.Equal("user-secrets", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("target").GetString());
        Assert.DoesNotContain(Secret, run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task A_project_without_a_user_secrets_id_is_told_how_to_add_one_before_anything_is_minted()
    {
        if (OperatingSystem.IsWindows())
            return;
        FakeDotnet();
        Project(withId: false);
        var api = new RecordingHandler(req => throw new InvalidOperationException("Nothing is minted: " + req.Key));

        CliRun run = await Run(api, "keys", "mint", "--tenant", "ten_1", "--write", "user-secrets");

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("has no UserSecretsId", run.Stderr);
        Assert.Contains("dotnet user-secrets init", run.Stderr);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_failing_dotnet_says_to_revoke_the_key_and_never_shows_the_secret()
    {
        if (OperatingSystem.IsWindows())
            return;
        FakeDotnet(exit: 1);
        Project();

        CliRun run = await Run(Server(), "keys", "mint", "--tenant", "ten_1", "--write", "user-secrets");

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("queuey keys revoke hsk_01WS", run.Stderr);
        Assert.DoesNotContain(Secret, run.Stdout + run.Stderr);
    }
}
