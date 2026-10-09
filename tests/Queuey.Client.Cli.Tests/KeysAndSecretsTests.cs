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

    /// <summary>
    /// A fake dotnet that logs its arguments and its stdin, so the test can see the secret never was an argument. `list`
    /// answers as dotnet user-secrets does: the secrets file path with the id, and the secrets <paramref name="existing"/> holds;
    /// without an id in the project it fails as dotnet does.
    /// </summary>
    private (string Args, string Stdin) FakeDotnet(int exit = 0, string existing = "")
    {
        string bin = Path.Combine(_dir, "bin"), args = Path.Combine(_dir, "dotnet-args.log"), stdin = Path.Combine(_dir, "dotnet-stdin.log");
        Directory.CreateDirectory(bin);
        string dotnet = Path.Combine(bin, "dotnet");
        File.WriteAllText(dotnet, $$"""
            #!/bin/sh
            echo "$@" >> '{{args}}'
            if [ "$2" = "list" ]; then
              if grep -q UserSecretsId "$4"; then
                echo "Project file path $4."
                echo "Secrets file path /home/u/.microsoft/usersecrets/shop-real-id/secrets.json."
                printf '%s' '{{existing}}'
                exit 0
              fi
              echo "Could not find the global property 'UserSecretsId' in MSBuild project '$4'."
              exit 1
            fi
            cat >> '{{stdin}}'
            exit {{exit}}

            """);
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
        string[] calls = File.ReadAllLines(args);
        Assert.StartsWith("user-secrets list --project ", calls[0]);   // forsjekken, før noe mintes (#69, B2)
        string argv = calls[1];
        Assert.StartsWith("user-secrets set --project ", argv);
        Assert.EndsWith("Shop.csproj", argv);
        Assert.DoesNotContain(Secret, argv);
        JsonElement sent = JsonDocument.Parse(File.ReadAllText(stdin)).RootElement;
        Assert.Equal("hsk_01WS", sent.GetProperty("QUEUEY_SIGNING_KEY_ID").GetString());
        Assert.Equal(Secret, sent.GetProperty("QUEUEY_SIGNING_SECRET").GetString());
        Assert.Equal("user-secrets", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("target").GetString());
        // Id-en dotnet user-secrets faktisk brukte, ikke den regex-en ville lest (#69, K1).
        Assert.Contains("(id shop-real-id)", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("written").GetString());
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

    // ── credentials generate ────────────────────────────────────────────────

    private static RecordingHandler CredentialServer(bool exists = false) => new(req => req.Key switch
    {
        "POST /tenants/ten_1/credentials" when exists => RecordingHandler.Json(HttpStatusCode.Conflict,
            new { error = new { code = "credential_exists", message = "orders-signing holds another secret." } }),
        "POST /tenants/ten_1/credentials" => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "cred_1", name = "orders-signing", type = "HmacSigning", keyId = "orders-signing", version = 1, created = true,
        }),
        _ => throw new InvalidOperationException(req.Key),
    });

    [Fact]
    public async Task Credentials_generate_stores_the_same_value_in_queuey_and_locally_and_never_shows_it()
    {
        RecordingHandler api = CredentialServer();
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env, "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        JsonElement sent = Assert.Single(api.Requests).Json;
        Assert.Equal("orders-signing", sent.GetProperty("name").GetString());
        Assert.Equal("HmacSigning", sent.GetProperty("type").GetString());
        Assert.Equal("orders-signing", sent.GetProperty("keyId").GetString());
        string secret = sent.GetProperty("secret").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret); // 32 byte, base64url uten utfylling
        Assert.Equal($"QUEUEY_DELIVERY_SECRET={secret}\n", File.ReadAllText(env));
        Assert.DoesNotContain(secret, run.Stdout + run.Stderr);
        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("orders-signing", json.GetProperty("deliverySigning").GetProperty("credentialRef").GetString());
        Assert.Equal("QUEUEY_DELIVERY_SECRET", json.GetProperty("variable").GetString());

        // To kjøringer gir to ulike verdier.
        Assert.NotEqual(CredentialsCommand.NewSecret(), CredentialsCommand.NewSecret());
    }

    [Fact]
    public async Task Credentials_generate_without_write_makes_nothing()
    {
        var api = new RecordingHandler(req => throw new InvalidOperationException("Nothing is stored: " + req.Key));

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1");

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("requires --write", run.Stderr);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_name_queuey_already_holds_writes_nothing_here()
    {
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(CredentialServer(exists: true), "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("--replace", run.Stderr);
        Assert.False(File.Exists(env));
    }

    [Fact]
    public async Task Credentials_generate_writes_user_secrets_on_stdin()
    {
        if (OperatingSystem.IsWindows())
            return;
        (string args, string stdin) = FakeDotnet();
        Project();
        RecordingHandler api = CredentialServer();

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", "user-secrets");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        string secret = Assert.Single(api.Requests).Json.GetProperty("secret").GetString()!;
        Assert.DoesNotContain(secret, File.ReadAllText(args));
        Assert.Equal(secret, JsonDocument.Parse(File.ReadAllText(stdin)).RootElement.GetProperty("QUEUEY_DELIVERY_SECRET").GetString());
        Assert.Contains("credentialRef", run.Stdout);
    }

    // ── advise ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Advise_names_keys_mint_for_the_sender_and_credentials_generate_for_the_receiver_with_the_target_that_fits()
    {
        string repo = Path.Combine(_dir, "shop");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "Shop.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><UserSecretsId>shop-1</UserSecretsId></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(repo, "Program.cs"), "app.MapPost(\"/webhooks/orders\", () => Results.Ok());");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo, "--json" }));

        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        string next = string.Join("\n", json.GetProperty("nextSteps").EnumerateArray().Select(s => s.GetString()));
        string receiving = string.Join("\n", json.GetProperty("receivingSteps").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains("queuey keys mint --queue <queue> --profile dev --write user-secrets", next);
        Assert.Contains("queuey credentials generate <queue>-signing --profile dev --write user-secrets", receiving);
        Assert.Contains("QueueyDeliveryVerifier.FromEnvironment()", receiving);

        // Uten UserSecretsId: .env.
        File.WriteAllText(Path.Combine(repo, "Shop.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        CliRun plain = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo, "--json" }));
        Assert.Contains("--write .env", JsonDocument.Parse(plain.Stdout).RootElement.GetProperty("nextSteps").ToString());
    }
}
