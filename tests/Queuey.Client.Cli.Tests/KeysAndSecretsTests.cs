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
    public async Task An_api_key_already_in_the_file_is_reported_masked_and_the_answer_says_the_sdk_reads_it_from_the_environment()
    {
        // Security-review av #69 (K2 og funksjonelt): den gamle nøkkelen publiserer til den trekkes tilbake, og SDK-en leser
        // QUEUEY_API_KEY bare fra miljøet, ikke fra .env.
        RecordingHandler api = Server(_ => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            clientPublicId = "acl_1", clientName = "queuey-cli", keyId = "kid1", secret = "qak_kid1.publishonly", queuePublicId = "que_1",
            scope = "queue", tenantPublicId = "ten_1", type = "api-key",
        }));
        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, "QUEUEY_API_KEY=qak_old.oldsecretvalue\n");

        CliRun human = await Run(api, "keys", "mint", "--queue", "que_1", "--type", "api-key", "--write", env);
        File.WriteAllText(env, "QUEUEY_API_KEY=qak_old.oldsecretvalue\n");
        CliRun json = await Run(api, "keys", "mint", "--queue", "que_1", "--type", "api-key", "--write", env, "--json");

        Assert.True(human.Exit == ExitCodes.Success, human.Stdout + human.Stderr);
        Assert.Contains("held another QUEUEY_API_KEY (qak_old.**** (set)) before. It may still work, and it may reach the whole license", human.Stderr);
        Assert.Contains("reads QUEUEY_API_KEY from the environment, not from a file", human.Stdout);
        Assert.DoesNotContain("UseEnvironmentVariables", human.Stdout);
        Assert.Equal("qak_old.**** (set)", JsonDocument.Parse(json.Stdout).RootElement.GetProperty("replacedApiKey").GetString());
        Assert.DoesNotContain("oldsecretvalue", human.Stdout + human.Stderr + json.Stdout + json.Stderr);
        Assert.DoesNotContain("publishonly", human.Stdout + human.Stderr + json.Stdout + json.Stderr);
    }

    [Theory]
    [InlineData("hsk_01OLD", true)]
    [InlineData("not-a-key-id-maybe-a-secret", false)]
    public async Task Only_an_old_value_shaped_as_a_signing_key_id_is_named(string old, bool named)
    {
        // Security-review av #69 (R2-3).
        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, $"QUEUEY_SIGNING_KEY_ID={old}\n");

        CliRun run = await Run(Server(), "keys", "mint", "--tenant", "ten_1", "--write", env, "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal(named, run.Stderr.Contains("held key hsk_01OLD"));
        Assert.Equal(named ? old : null, JsonDocument.Parse(run.Stdout).RootElement.GetProperty("replacedKeyId").GetString());
        if (!named)
            Assert.DoesNotContain(old, run.Stdout + run.Stderr);
    }

    [Theory]
    [InlineData("qak_kid.secret", "qak_kid.**** (set)")]
    [InlineData("all-of-it-may-be-secret", "(set)")]
    [InlineData("secretpart.rest", "(set)")]
    [InlineData("qak_\u001b[2J.rest", "(set)")]
    [InlineData("qak_123456789012345678901234567890123.rest", "(set)")]
    [InlineData(null, "(not set)")]
    public void A_masked_key_shows_its_prefix_only_when_it_is_shaped_as_a_key_id(string? key, string shown)
        => Assert.Equal(shown, CliHost.MaskKey(key)); // security-review av #69 (R2-2), også for whoami

    [Fact]
    public async Task A_signing_pair_in_a_file_is_read_from_the_environment_or_from_env_in_development()
    {
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(Server(), "keys", "mint", "--tenant", "ten_1", "--write", env);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("UseEnvironmentVariables(): from the environment, and from .env in Development", run.Stdout);
    }

    [Theory]
    [InlineData("ten_a-b", null, "https://app.queuey.ai/console/t")]
    [InlineData("ten_1", "que_a/../b", "https://app.queuey.ai/console/t/ten_1?tab=security")]
    [InlineData("ten_1", "que_1\u001b[2J", "https://app.queuey.ai/console/t/ten_1?tab=security")]
    public async Task An_id_outside_letters_digits_and_underscore_gets_no_link_of_its_own_and_no_escape_reaches_the_terminal(
        string tenant, string? queue, string console)
    {
        // Security-review av #69 (K3): id-en havner både i lenken og i kommandoen Guide foreslår.
        var api = new RecordingHandler(req => throw new InvalidOperationException("Nothing is minted: " + req.Key));
        string[] args = new[] { "keys", "mint", "--tenant", tenant }
            .Concat(queue is null ? Array.Empty<string>() : new[] { "--queue", queue })
            .Concat(new[] { "--api-key", "qak_kid.secret", "--license", "lic_1", "--config", Path.Combine(_dir, "none.json") })
            .ToArray();

        CliRun human = await CliHarness.RunAsync(() => CliEntry.RunAsync(args), api);
        CliRun json = await CliHarness.RunAsync(() => CliEntry.RunAsync(args.Append("--json").ToArray()), api);

        Assert.Empty(api.Requests);
        Assert.True(human.Exit == ExitCodes.Success, human.Stdout + human.Stderr);
        Assert.DoesNotContain('\u001b', human.Stdout + human.Stderr); // char: kultursammenligning ignorerer kontrolltegn
        Assert.Contains(console, human.Stdout);
        Assert.Equal(console, JsonDocument.Parse(json.Stdout).RootElement.GetProperty("consoleUrl").GetString());
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
        Assert.Contains("queuey keys mint --profile dev --write user-secrets", next);
        Assert.Contains("queuey credentials generate <queue>-signing --profile dev --write user-secrets", receiving);
        Assert.Contains("QueueyDeliveryVerifier.FromEnvironment()", receiving);

        // Uten UserSecretsId: .env.
        File.WriteAllText(Path.Combine(repo, "Shop.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        CliRun plain = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "advise", repo, "--json" }));
        Assert.Contains("--write .env", JsonDocument.Parse(plain.Stdout).RootElement.GetProperty("nextSteps").ToString());
    }

    // ── credentials generate: det som står der fra før (#69 B1, B3, K5) ────

    [Fact]
    public async Task A_delivery_secret_already_in_the_target_is_not_overwritten_without_replace()
    {
        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, "QUEUEY_DELIVERY_SECRET=the-old-one\n");
        RecordingHandler api = Listing("another-one");

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("already holds QUEUEY_DELIVERY_SECRET", run.Stderr);
        // Security-review av #69 (R2-1): Queuey har ikke navnet, så --replace bytter bare verdien her.
        Assert.Contains("Queuey holds no credential named 'orders-signing', so --replace replaces only the value here", run.Stderr);
        Assert.Contains("the value here may belong to another credential whose deliveries then fail.", run.Stderr);
        Assert.Equal("GET /tenants/ten_1/credentials", Assert.Single(api.Requests).Key);
        Assert.Equal("QUEUEY_DELIVERY_SECRET=the-old-one\n", File.ReadAllText(env));
        Assert.DoesNotContain("the-old-one", run.Stdout + run.Stderr);
    }

    /// <summary>A Queuey that lists credentials of <paramref name="names"/> and stores nothing.</summary>
    private static RecordingHandler Listing(params string[] names) => new(req => req.Key switch
    {
        "GET /tenants/ten_1/credentials" => RecordingHandler.Json(HttpStatusCode.OK,
            names.Select(n => new { publicId = "cred_" + n, name = n, type = "HmacSigning", keyId = n, version = 1 }).ToArray()),
        _ => throw new InvalidOperationException("Nothing is stored: " + req.Key),
    });

    [Fact]
    public async Task A_delivery_secret_here_for_a_name_queuey_holds_says_replace_also_hits_every_queue_that_names_it()
    {
        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, "QUEUEY_DELIVERY_SECRET=the-old-one\n");
        RecordingHandler api = Listing("orders-signing");

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env, "--json");

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        string answer = run.Stdout + run.Stderr;
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Contains("--replace also replaces it in Queuey, for every queue and ingress that names it, and the value here may belong "
                        + "to another credential whose deliveries then fail.", error.GetProperty("action").GetString());
        Assert.True(error.GetProperty("inQueuey").GetBoolean());
        Assert.Equal("GET /tenants/ten_1/credentials", Assert.Single(api.Requests).Key);
        Assert.Equal("QUEUEY_DELIVERY_SECRET=the-old-one\n", File.ReadAllText(env));
        Assert.DoesNotContain("the-old-one", answer);
    }

    [Fact]
    public async Task An_answer_without_an_id_writes_nothing_here()
    {
        // Security-review av #69 (R2-4): Queuey har ikke bekreftet at den lagret noe.
        string env = Path.Combine(_dir, ".env");
        var api = new RecordingHandler(req => req.Key switch
        {
            "POST /tenants/ten_1/credentials" => RecordingHandler.Json(HttpStatusCode.OK, new { name = "orders-signing", created = true }),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("did not confirm that it stored 'orders-signing'", run.Stderr);
        Assert.False(File.Exists(env));
    }

    [Fact]
    public async Task With_replace_the_new_value_goes_to_both_and_the_answer_says_what_it_replaced_and_tightened()
    {
        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, "QUEUEY_DELIVERY_SECRET=the-old-one\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(env, (UnixFileMode)0x1A4); // 0644
        var api = new RecordingHandler(req => req.Key == "POST /tenants/ten_1/credentials"
            ? RecordingHandler.Json(HttpStatusCode.OK, new
            {
                publicId = "cred_1", name = "orders-signing", type = "HmacSigning", version = 2, created = false, secretReplaced = true,
                boundQueues = new[] { "orders" },
            })
            : throw new InvalidOperationException(req.Key));

        CliRun json = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env, "--replace", "--json");

        Assert.True(json.Exit == ExitCodes.Success, json.Stdout + json.Stderr);
        Assert.True(api.Requests[0].Json.GetProperty("replace").GetBoolean());
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.True(root.GetProperty("replacedLocally").GetBoolean());
        Assert.True(root.GetProperty("secretReplaced").GetBoolean());
        if (!OperatingSystem.IsWindows())
            Assert.Equal("0644", root.GetProperty("tightenedFrom").GetString());
        Assert.Equal($"QUEUEY_DELIVERY_SECRET={api.Requests[0].Json.GetProperty("secret").GetString()}\n", File.ReadAllText(env));

        File.WriteAllText(env, "QUEUEY_DELIVERY_SECRET=again\n");
        CliRun human = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env, "--replace");
        Assert.Contains("used by every queue and ingress that names it", human.Stdout);
        Assert.Contains("The ingress of orders waited for this name", human.Stdout);
        Assert.Contains("in place of the value there", human.Stdout);
    }

    [Fact]
    public async Task A_name_queuey_holds_is_refused_with_what_a_new_value_would_hit()
    {
        CliRun run = await Run(CredentialServer(exists: true), "credentials", "generate", "orders-signing", "--tenant", "ten_1",
            "--write", Path.Combine(_dir, ".env"));

        Assert.Contains("used by every queue and ingress that names it", run.Stderr);
    }

    [Fact]
    public async Task A_store_queuey_gives_to_a_person_exits_5_and_writes_nothing()
    {
        string env = Path.Combine(_dir, ".env");
        var api = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.Accepted, new
        {
            status = "pending_approval", approvalUrl = "https://app.test/console/t/ten_1/credential-requests/creq_1",
            message = "A person stores secrets in a prod workspace.",
        }));

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", env, "--json");

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        Assert.Equal("pending_approval", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("status").GetString());
        Assert.False(File.Exists(env));
    }

    [Fact]
    public async Task A_delivery_secret_in_the_user_secrets_is_found_before_anything_is_stored()
    {
        if (OperatingSystem.IsWindows())
            return;
        FakeDotnet(existing: "QUEUEY_DELIVERY_SECRET = the-old-one\n");
        Project();
        RecordingHandler api = Listing();

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", "user-secrets");

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("already holds QUEUEY_DELIVERY_SECRET", run.Stderr);
        Assert.Equal("GET /tenants/ten_1/credentials", Assert.Single(api.Requests).Key); // bare oppslaget, ingenting lagres
    }

    [Fact]
    public async Task A_write_that_fails_after_replace_says_the_receiver_now_rejects_deliveries()
    {
        if (OperatingSystem.IsWindows())
            return;
        FakeDotnet(exit: 1, existing: "QUEUEY_DELIVERY_SECRET = the-old-one\n");
        Project();
        var api = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            publicId = "cred_1", name = "orders-signing", type = "HmacSigning", version = 2, created = false, secretReplaced = true,
        }));

        CliRun run = await Run(api, "credentials", "generate", "orders-signing", "--tenant", "ten_1", "--write", "user-secrets", "--replace");

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("Queuey now has the new secret", run.Stderr);
        Assert.Contains("will reject deliveries", run.Stderr);
    }
}
