using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// queuey login, logout og innloggingen kommandoene bruker (PR 4 i login-plan.md, 2026-10-09): device-flyten mot en falsk
/// server, credentials.json med 0600 i en mappe med 0700, profilfletting som ikke rører andre profiler, fornyelse med
/// roterende refresh-tokens, og en gammel profil med apiKey som virker som før.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class LoginCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-login-tests", Guid.NewGuid().ToString("N"));
    private readonly string _queuey;
    private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly List<TimeSpan> _delays = new();
    private readonly List<Uri> _opened = new();

    private readonly Func<DateTimeOffset> _clock = LoginTokens.Now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = LoginCommand.Delay;
    private readonly Func<bool> _terminal = LoginCommand.IsTerminal;
    private readonly Func<Uri, bool> _browser = LoginCommand.OpenBrowser;

    public LoginCommandTests()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // ~/.queuey finnes ikke før CLI-en lager den, så testen ser mappa CLI-en lager.
        _queuey = Path.Combine(_dir, ".queuey");

        LoginTokens.Now = () => _now;
        LoginCommand.Delay = (wait, _) =>
        {
            _delays.Add(wait);
            _now += wait;
            return Task.CompletedTask;
        };
        LoginCommand.IsTerminal = () => false;
        LoginCommand.OpenBrowser = link =>
        {
            _opened.Add(link);
            return true;
        };
    }

    public void Dispose()
    {
        LoginTokens.Now = _clock;
        LoginCommand.Delay = _delay;
        LoginCommand.IsTerminal = _terminal;
        LoginCommand.OpenBrowser = _browser;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string UserFile => Path.Combine(_queuey, "config.json");
    private string CredentialsFile => Path.Combine(_queuey, "credentials.json");

    private Dictionary<string, string> Env(params (string Name, string Value)[] extra)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [UserProfiles.PathVariable] = UserFile };
        foreach ((string name, string value) in extra)
            env[name] = value;
        return env;
    }

    private Task<CliRun> Run(FakeAuthServer server, params string[] args)
        => CliHarness.RunAsync(() => CliEntry.RunAsync(args), server.Handler, Env());

    private static JsonElement Line(string stdout, int index = 0)
        => JsonDocument.Parse(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[index]).RootElement;

    private JsonElement Stored() => JsonDocument.Parse(File.ReadAllText(CredentialsFile)).RootElement;

    /// <summary>En innlogging som om den alt var gjort, med tokenene <paramref name="server"/> kjenner, for lisensen lic_new på api.test.</summary>
    private void StoreLogin(FakeAuthServer server, TimeSpan accessLeft, string license = "lic_new")
    {
        (string access, string refresh) = server.Issue();
        Directory.CreateDirectory(_queuey);
        LoginStore.Write(CredentialsFile, new CredentialsFile
        {
            Logins =
            {
                new StoredLogin
                {
                    ApiBase = FakeAuthServer.Api, License = license, Scope = "operate", User = "ada@example.com",
                    IngressBase = "https://ingress.test", AccessToken = access, AccessTokenExpiresAt = _now + accessLeft,
                    RefreshToken = refresh, LoggedInAt = _now - TimeSpan.FromDays(1),
                },
            },
        });
    }

    private static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path) & (UnixFileMode)0x1FF;

    // ── device-flyten ────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_terminal_login_shows_the_link_exits_5_and_keeps_the_code_for_the_next_run()
    {
        var server = new FakeAuthServer();

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--json");

        Assert.True(run.Exit == ExitCodes.PendingApproval, run.Stdout + run.Stderr);
        JsonElement waiting = Line(run.Stdout);
        Assert.Equal("waiting_for_person", waiting.GetProperty("status").GetString());
        Assert.Equal("https://app.test/connect?code=WDJB-MJHT", waiting.GetProperty("link").GetString());
        Assert.Equal("WDJB-MJHT", waiting.GetProperty("userCode").GetString());
        Assert.Contains("queuey login --wait", waiting.GetProperty("action").GetString());

        // Endepunktet fra metadataen, som offentlig klient uten hemmelighet, og scopet operate som standard.
        Dictionary<string, string> device = Assert.Single(server.FormsTo("/connect/device"));
        Assert.Equal("queuey-cli", device["client_id"]);
        Assert.Equal("operate", device["scope"]);
        Assert.False(device.ContainsKey("client_secret"));
        Assert.Empty(server.FormsTo("/connect/token")); // personen kan ikke ha godkjent ennå
        Assert.Empty(_opened);                          // ingen terminal: ingen nettleser

        JsonElement pending = Assert.Single(Stored().GetProperty("pending").EnumerateArray());
        Assert.Equal("WDJB-MJHT", pending.GetProperty("userCode").GetString());
        Assert.Equal(FakeAuthServer.Api, pending.GetProperty("apiBase").GetString());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(CredentialsFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, ModeOf(_queuey));
        }
    }

    [Fact]
    public async Task Running_login_again_after_approval_finishes_with_the_kept_code()
    {
        var server = new FakeAuthServer();
        CliRun first = await Run(server, "login", "--api-base", FakeAuthServer.Api);
        Assert.Equal(ExitCodes.PendingApproval, first.Exit);
        Assert.Contains("https://app.test/connect?code=WDJB-MJHT", first.Stdout);
        Assert.Contains("WDJB-MJHT", first.Stdout);

        _now += TimeSpan.FromSeconds(30);
        server.PollAnswers.Enqueue("approve");
        CliRun second = await Run(server, "login", "--api-base", FakeAuthServer.Api);

        Assert.True(second.Exit == ExitCodes.Success, second.Stdout + second.Stderr);
        Assert.Contains("Logged in to https://api.test as ada@example.com, license lic_new, scope operate.", second.Stdout);
        Assert.Single(server.FormsTo("/connect/device")); // samme kode, ingen ny
        Dictionary<string, string> poll = Assert.Single(server.FormsTo("/connect/token"));
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", poll["grant_type"]);
        Assert.Equal("queuey-cli", poll["client_id"]);
        Assert.Empty(_delays); // koden var eldre enn intervallet

        JsonElement stored = Stored();
        Assert.Empty(stored.GetProperty("pending").EnumerateArray());
        JsonElement login = Assert.Single(stored.GetProperty("logins").EnumerateArray());
        Assert.Equal("lic_new", login.GetProperty("license").GetString());
        Assert.Equal("at1-opaque", login.GetProperty("accessToken").GetString());
        Assert.Equal("rt1-opaque", login.GetProperty("refreshToken").GetString());
        Assert.Equal("https://ingress.test", login.GetProperty("ingressBase").GetString());
        Assert.Equal(_now + TimeSpan.FromHours(1), login.GetProperty("accessTokenExpiresAt").GetDateTimeOffset());
        Assert.DoesNotContain("opaque", second.Stdout + second.Stderr);
    }

    [Fact]
    public async Task Login_wait_polls_at_the_interval_and_adds_five_seconds_for_each_slow_down()
    {
        var server = new FakeAuthServer();
        foreach (string answer in new[] { "authorization_pending", "slow_down", "authorization_pending", "approve" })
            server.PollAnswers.Enqueue(answer);

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--wait", "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("waiting_for_person", Line(run.Stdout, 0).GetProperty("status").GetString());
        JsonElement done = Line(run.Stdout, 1);
        Assert.Equal("logged_in", done.GetProperty("status").GetString());
        Assert.False(done.GetProperty("alreadyLoggedIn").GetBoolean());
        Assert.Equal("lic_new", done.GetProperty("license").GetString());
        Assert.Equal(new[] { 5, 5, 10, 10 }, _delays.Select(d => (int)d.TotalSeconds).ToArray());
        Assert.Equal(4, server.FormsTo("/connect/token").Count());
    }

    [Theory]
    [InlineData("access_denied", "The login was declined in the Queuey console.")]
    [InlineData("expired_token", "The login code expired before anyone approved it.")]
    public async Task A_declined_or_expired_code_fails_and_the_next_login_gets_a_new_one(string error, string message)
    {
        var server = new FakeAuthServer();
        server.PollAnswers.Enqueue(error);

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--wait", "--json");

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        JsonElement failed = Line(run.Stdout, 1).GetProperty("error");
        Assert.Equal(error, failed.GetProperty("code").GetString());
        Assert.Equal(message, failed.GetProperty("message").GetString());
        Assert.Empty(Stored().GetProperty("pending").EnumerateArray());

        CliRun again = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--json");
        Assert.Equal(ExitCodes.PendingApproval, again.Exit);
        Assert.Equal(2, server.FormsTo("/connect/device").Count());
    }

    [Fact]
    public async Task A_kept_code_past_its_time_is_never_polled_and_a_new_one_is_asked_for()
    {
        var server = new FakeAuthServer();
        await Run(server, "login", "--api-base", FakeAuthServer.Api);

        _now += TimeSpan.FromMinutes(11);
        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--json");

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        Assert.Empty(server.FormsTo("/connect/token"));
        Assert.Equal(2, server.FormsTo("/connect/device").Count());
        Assert.Single(Stored().GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task In_a_terminal_login_opens_the_browser_and_waits()
    {
        LoginCommand.IsTerminal = () => true;
        var server = new FakeAuthServer();
        server.PollAnswers.Enqueue("approve");

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--scope", "read");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("https://app.test/connect?code=WDJB-MJHT", Assert.Single(_opened).AbsoluteUri);
        Assert.Equal("read", Assert.Single(server.FormsTo("/connect/device"))["scope"]);
        Assert.Contains("Waiting for approval", run.Stderr);
    }

    [Fact]
    public async Task A_queuey_without_login_metadata_says_to_use_an_api_key()
    {
        var server = new FakeAuthServer { OffersLogin = false };

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("https://api.test does not offer login", run.Stderr);
        Assert.Contains("API key", run.Stderr);
    }

    [Fact]
    public async Task Login_when_already_logged_in_says_so_and_asks_for_no_code()
    {
        var server = new FakeAuthServer();
        StoreLogin(server, TimeSpan.FromMinutes(30));

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.True(Line(run.Stdout).GetProperty("alreadyLoggedIn").GetBoolean());
        Assert.Empty(server.FormsTo("/connect/device"));
        Assert.Contains("GET /tenants", server.Handler.Requests.Select(r => r.Key)); // tilkoblingen er prøvd mot Queuey
    }

    [Fact]
    public async Task After_a_login_the_next_login_finds_it_even_when_queuey_grants_a_scope_of_its_own()
    {
        var server = new FakeAuthServer();
        server.PollAnswers.Enqueue("approve");
        Assert.Equal(ExitCodes.Success, (await Run(server, "login", "--api-base", FakeAuthServer.Api, "--scope", "read", "--wait")).Exit);
        Assert.Equal("read", Assert.Single(Stored().GetProperty("logins").EnumerateArray()).GetProperty("scope").GetString());

        CliRun again = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--scope", "read");

        Assert.True(again.Exit == ExitCodes.Success, again.Stdout + again.Stderr);
        Assert.Contains("Already logged in to https://api.test", again.Stdout);
        Assert.Single(server.FormsTo("/connect/device"));
    }

    [Fact]
    public async Task A_login_queuey_has_revoked_is_dropped_and_login_starts_over()
    {
        var server = new FakeAuthServer();
        StoreLogin(server, TimeSpan.FromMinutes(30));
        server.RevokeAll(); // koblet fra i konsollet

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--json");

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        Assert.Empty(Stored().GetProperty("logins").EnumerateArray());
        Assert.Single(server.FormsTo("/connect/device"));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("token_endpoint")]
    public async Task Login_metadata_that_points_away_from_the_api_host_is_not_used(string field)
    {
        // Security-review av #66 (KAN 3): koder og tokens sendes bare til API-ets egen opprinnelse.
        var server = new FakeAuthServer();
        if (field == "issuer") server.Issuer = "https://evil.test/";
        else server.TokenEndpoint = "https://evil.test/token";

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--json");

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Equal("login_metadata_invalid", Line(run.Stdout).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(server.FormsTo("/connect/device"));
    }

    [Fact]
    public async Task Login_over_plain_http_to_another_machine_sends_nothing()
    {
        var server = new FakeAuthServer();

        CliRun run = await Run(server, "login", "--api-base", "http://api.test");

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("is plain http on another machine", run.Stderr);
        Assert.Empty(server.Handler.Requests);
    }

    [Fact]
    public async Task A_lock_another_process_holds_is_reported_and_never_starts_a_new_login()
    {
        // Security-review av #66 (KAN 4): tidsavbruddet på låsen ble lest som en innlogging som var slutt.
        var server = new FakeAuthServer();
        StoreLogin(server, TimeSpan.Zero); // må fornyes, under låsen
        TimeSpan before = LoginStore.LockTimeout;
        LoginStore.LockTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var held = new FileStream(CredentialsFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api);

            Assert.Equal(ExitCodes.Configuration, run.Exit);
            Assert.Contains("Another queuey process has held the lock on your logins", run.Stderr);
            Assert.Empty(server.FormsTo("/connect/device"));
            Assert.Single(Stored().GetProperty("logins").EnumerateArray());
        }
        finally
        {
            LoginStore.LockTimeout = before;
        }
    }

    [Fact]
    public async Task A_folder_others_can_write_to_gets_no_login_written_in_it()
    {
        // Security-review av #66 (KAN 5): mappa sjekkes før den første skrivingen, ikke først ved neste lesing.
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_queuey);
        File.SetUnixFileMode(_queuey, (UnixFileMode)0x1FF); // 0777, uten sticky-bit
        var server = new FakeAuthServer();

        CliRun run = await Run(server, "login", "--api-base", FakeAuthServer.Api);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains($"{_queuey} can be written by other users (mode 0777)", run.Stderr);
        Assert.Contains("so nothing was written", run.Stderr);
        Assert.False(File.Exists(CredentialsFile));
    }

    [Fact]
    public async Task A_license_that_is_not_a_license_id_is_refused_and_an_unsafe_ingress_is_not_kept()
    {
        // Security-review av #66 (KAN 6): verdiene i token-svaret styres av serveren og vises og lagres.
        var server = new FakeAuthServer { License = "lic_x\u001b[2Jqak_secret", IngressBase = "http://ingress.elsewhere.test" };
        server.PollAnswers.Enqueue("approve");

        CliRun refused = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--wait");

        Assert.Equal(ExitCodes.RuntimeError, refused.Exit);
        Assert.Contains("without a license id (lic_…)", refused.Stderr);
        Assert.DoesNotContain("qak_secret", refused.Stdout + refused.Stderr);
        Assert.Empty(Stored().GetProperty("logins").EnumerateArray());
        Assert.Empty(Stored().GetProperty("pending").EnumerateArray());

        server.License = "lic_new";
        server.PollAnswers.Enqueue("approve");
        CliRun kept = await Run(server, "login", "--api-base", FakeAuthServer.Api, "--wait");
        Assert.True(kept.Exit == ExitCodes.Success, kept.Stdout + kept.Stderr);
        JsonElement login = Assert.Single(Stored().GetProperty("logins").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, login.GetProperty("ingressBase").ValueKind);
    }

    // ── profilen ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_with_a_profile_writes_the_license_hosts_and_its_environments_workspace_and_leaves_other_profiles()
    {
        Directory.CreateDirectory(_queuey);
        const string prod = """{ "apiKey": "qak_prod.key-for-prod", "license": "lic_prod", "tenant": "ten_prod", "source": "ci" }""";
        UserProfilesTests.WriteUserConfig(_queuey, $$"""
            {
              // Kenneths prod-nøkkel
              "profiles": {
                "prod": {{prod}},
                "dev": { "source": "laptop" }
              }
            }
            """);
        string prodBefore = JsonNode.Parse(prod)!.ToJsonString();

        var server = new FakeAuthServer();
        server.Workspaces.AddRange(new[]
        {
            FakeAuthServer.Workspace("ten_prod1", "shop", null),
            FakeAuthServer.Workspace("ten_olddev", "old", "dev", archived: true),
            FakeAuthServer.Workspace("ten_dev1", "queuey-dev", "dev"),
        });
        server.PollAnswers.Enqueue("approve");

        CliRun run = await Run(server, "login", "--profile", "dev", "--api-base", FakeAuthServer.Api, "--wait", "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        JsonElement done = Line(run.Stdout, 1);
        Assert.Equal("ten_dev1", done.GetProperty("tenant").GetString());
        Assert.Equal(UserFile, done.GetProperty("profileFile").GetString());

        JsonNode file = JsonNode.Parse(File.ReadAllText(UserFile))!;
        JsonNode dev = file["profiles"]!["dev"]!;
        Assert.Equal("lic_new", (string?)dev["license"]);
        Assert.Equal("ten_dev1", (string?)dev["tenant"]);
        Assert.Equal("https://api.test", (string?)dev["apiBase"]);
        Assert.Equal("https://ingress.test", (string?)dev["ingressBase"]);
        Assert.Equal("laptop", (string?)dev["source"]);
        Assert.Null(dev["apiKey"]);
        Assert.Equal(prodBefore, file["profiles"]!["prod"]!.ToJsonString());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(UserFile));

        // Profilen leses som før, uten nøkkel.
        ConnectionProfile loaded = UserProfiles.Load("dev", name => name == UserProfiles.PathVariable ? UserFile : null, out _);
        Assert.Null(loaded.ApiKey);
        Assert.Equal("ten_dev1", loaded.Tenant);
    }

    [Fact]
    public async Task Without_a_workspace_for_the_environment_the_profile_gets_none_and_login_says_how_to_make_one()
    {
        var server = new FakeAuthServer();
        server.Workspaces.Add(FakeAuthServer.Workspace("ten_prod1", "shop", "prod"));
        server.PollAnswers.Enqueue("approve");

        CliRun run = await Run(server, "login", "--profile", "dev", "--api-base", FakeAuthServer.Api, "--wait");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("License lic_new has no dev workspace yet.", run.Stdout);
        Assert.Contains("queuey create-tenant --name queuey-dev --environment dev", run.Stdout);
        Assert.Null(JsonNode.Parse(File.ReadAllText(UserFile))!["profiles"]!["dev"]!["tenant"]);

        // Med to i samme miljø velges ingen: login lister dem.
        server.Workspaces.Add(FakeAuthServer.Workspace("ten_a", "a", "prod"));
        server.Workspaces.Add(FakeAuthServer.Workspace("ten_b", "b", null)); // uten merke regnes det som prod
        CliRun several = await Run(server, "login", "--profile", "prod", "--api-base", FakeAuthServer.Api);
        Assert.True(several.Exit == ExitCodes.Success, several.Stdout + several.Stderr);
        Assert.Contains("License lic_new has several prod workspaces: ten_prod1 (shop), ten_a (a), ten_b (b).", several.Stdout);
    }

    [Fact]
    public async Task A_profile_with_an_api_key_keeps_its_host_license_and_workspace_and_login_only_fills_what_it_lacks()
    {
        // Security-review av #66 (BØR 2): login skrev om vert, lisens og workspace i en profil med nøkkel.
        Directory.CreateDirectory(_queuey);
        UserProfilesTests.WriteUserConfig(_queuey, """
            { "profiles": { "dev": { "apiKey": "qak_dev.key-for-dev", "apiBase": "https://api.test", "license": "lic_old", "tenant": "ten_old" } } }
            """);
        var server = new FakeAuthServer();
        server.Workspaces.Add(FakeAuthServer.Workspace("ten_dev1", "queuey-dev", "dev"));
        server.PollAnswers.Enqueue("approve");

        CliRun run = await Run(server, "login", "--profile", "dev", "--wait");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("has an apiKey, which wins over the login, so only the fields it lacked were filled", run.Stderr);
        JsonNode dev = JsonNode.Parse(File.ReadAllText(UserFile))!["profiles"]!["dev"]!;
        Assert.Equal("qak_dev.key-for-dev", (string?)dev["apiKey"]);
        Assert.Equal("https://api.test", (string?)dev["apiBase"]);
        Assert.Equal("lic_old", (string?)dev["license"]);
        Assert.Equal("ten_old", (string?)dev["tenant"]);
        Assert.Equal("https://ingress.test", (string?)dev["ingressBase"]); // manglet, og ble fylt
        Assert.DoesNotContain("key-for-dev", run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task A_queuey_variable_that_disagrees_with_the_profile_stops_login_before_anything_is_sent()
    {
        // Samme regel som --profile ellers (F2.7): en QUEUEY_API_BASE igjen i skallet blandes aldri inn i profilen. Flagget vinner.
        Directory.CreateDirectory(_queuey);
        UserProfilesTests.WriteUserConfig(_queuey, """{ "profiles": { "dev": { "license": "lic_new", "apiBase": "https://api.test" } } }""");
        var server = new FakeAuthServer();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "login", "--profile", "dev" }), server.Handler,
            Env(("QUEUEY_API_BASE", "https://api.other.test")));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("QUEUEY_API_BASE is set to another API host than profile dev", run.Stderr);
        Assert.Empty(server.Handler.Requests);

        // Overstyringen er fortsatt enkel: --api-base, eller QUEUEY_API_BASE uten profil.
        server.PollAnswers.Enqueue("approve");
        CliRun flagged = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "login", "--wait" }), server.Handler,
            Env(("QUEUEY_API_BASE", FakeAuthServer.Api)));
        Assert.True(flagged.Exit == ExitCodes.Success, flagged.Stdout + flagged.Stderr);
    }

    // ── kommandoene med innloggingen ─────────────────────────────────────────

    private static HttpResponseMessage CreatedQueue(RecordedRequest req)
        => req.Key == "POST /queues"
            ? RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_1", tenantPublicId = "ten_1", displayName = "orders" })
            : throw new InvalidOperationException(req.Key);

    private Task<CliRun> CreateQueue(FakeAuthServer server, params (string, string)[] env)
        => CliHarness.RunAsync(() => CliEntry.RunAsync(new[]
        {
            "create-queue", "--tenant", "ten_1", "--name", "orders", "--api-base", FakeAuthServer.Api,
            "--config", Path.Combine(_dir, "no-queuey.json"),
        }), server.Handler, Env(env));

    [Fact]
    public async Task Without_an_api_key_a_command_sends_the_login_as_a_bearer_with_its_license()
    {
        var server = new FakeAuthServer(CreatedQueue);
        StoreLogin(server, TimeSpan.FromMinutes(30));

        CliRun run = await CreateQueue(server);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Dictionary<string, string> headers = server.Handler.Headers[server.Handler.Requests.FindIndex(r => r.Key == "POST /queues")];
        Assert.Equal("Bearer at1-opaque", headers["Authorization"]);
        Assert.Equal("lic_new", headers["X-License-PublicId"]);
        Assert.False(headers.ContainsKey("X-Api-Key"));
        Assert.Empty(server.FormsTo("/connect/token")); // tokenet hadde tid igjen
    }

    [Fact]
    public async Task An_expired_access_token_is_renewed_and_the_new_refresh_token_replaces_the_old()
    {
        var server = new FakeAuthServer(CreatedQueue);
        StoreLogin(server, TimeSpan.FromSeconds(30)); // under marginen på 60 sekunder

        CliRun run = await CreateQueue(server);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Dictionary<string, string> refresh = Assert.Single(server.FormsTo("/connect/token"));
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("rt1-opaque", refresh["refresh_token"]);
        Assert.Equal("queuey-cli", refresh["client_id"]);
        Dictionary<string, string> headers = server.Handler.Headers[server.Handler.Requests.FindIndex(r => r.Key == "POST /queues")];
        Assert.Equal("Bearer at2-opaque", headers["Authorization"]);

        JsonElement login = Assert.Single(Stored().GetProperty("logins").EnumerateArray());
        Assert.Equal("rt2-opaque", login.GetProperty("refreshToken").GetString());
        Assert.Equal("at2-opaque", login.GetProperty("accessToken").GetString());
    }

    [Fact]
    public async Task A_refresh_token_queuey_no_longer_takes_ends_the_login_here_too()
    {
        var server = new FakeAuthServer(CreatedQueue);
        StoreLogin(server, TimeSpan.Zero);
        server.RevokeAll();

        CliRun run = await CreateQueue(server);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("The login to https://api.test for license lic_new has ended", run.Stderr);
        Assert.Contains("queuey login", run.Stderr);
        Assert.Empty(Stored().GetProperty("logins").EnumerateArray());
        Assert.DoesNotContain("POST /queues", server.Handler.Requests.Select(r => r.Key));
    }

    [Fact]
    public async Task An_old_profile_with_an_api_key_works_as_before_and_the_key_wins_over_a_login()
    {
        var server = new FakeAuthServer(req => req switch
        {
            { Method.Method: "GET" } when req.Path.StartsWith("/tenants/", StringComparison.Ordinal)
                => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            { Method.Method: "PUT", Path: "/queues" }
                => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = true, hasDeliveryTarget = false }),
            _ => throw new InvalidOperationException(req.Key),
        });
        StoreLogin(server, TimeSpan.Zero, license: "lic_dev"); // utløpt: ville gitt en fornyelse om den ble brukt
        UserProfilesTests.WriteUserConfig(_queuey, UserProfilesTests.TwoProfiles);
        string deploy = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(deploy, """{ "tenant": "ten_dev", "queues": { "orders": {} }, "profiles": { "dev": {} } }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--file", deploy, "--profile", "dev" }), server.Handler, Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.All(server.Handler.Headers, h =>
        {
            Assert.Equal("qak_dev.key-for-dev", h["X-Api-Key"]);
            Assert.False(h.ContainsKey("Authorization"));
        });
        Assert.Empty(server.FormsTo("/connect/token"));
    }

    [Fact]
    public async Task A_profile_without_an_api_key_connects_with_the_login_for_its_host_and_license()
    {
        var server = new FakeAuthServer(req => req switch
        {
            { Method.Method: "GET" } when req.Path.StartsWith("/tenants/", StringComparison.Ordinal)
                => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
            { Method.Method: "PUT", Path: "/queues" }
                => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = true, hasDeliveryTarget = false }),
            _ => throw new InvalidOperationException(req.Key),
        });
        StoreLogin(server, TimeSpan.FromMinutes(30), license: "lic_dev");
        UserProfilesTests.WriteUserConfig(_queuey, """
            { "profiles": { "dev": { "license": "lic_dev", "tenant": "ten_dev", "apiBase": "https://api.test", "ingressBase": "https://ingress.test" } } }
            """);
        string deploy = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(deploy, """{ "tenant": "ten_dev", "queues": { "orders": {} }, "profiles": { "dev": {} } }""");

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--file", deploy, "--profile", "dev" }), server.Handler, Env());

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("PUT /queues", server.Handler.Requests.Select(r => r.Key));
        Assert.All(server.Handler.Headers, h =>
        {
            Assert.Equal("Bearer at1-opaque", h["Authorization"]);
            Assert.Equal("lic_dev", h["X-License-PublicId"]);
            Assert.False(h.ContainsKey("X-Api-Key"));
        });
    }

    [Fact]
    public async Task Whoami_shows_the_login_and_never_a_token()
    {
        var server = new FakeAuthServer();
        StoreLogin(server, TimeSpan.FromMinutes(30));

        CliRun human = await Run(server, "whoami", "--api-base", FakeAuthServer.Api, "--config", Path.Combine(_dir, "no-queuey.json"));
        Assert.Equal(ExitCodes.Success, human.Exit);
        Assert.Contains("Login       : ada@example.com via queuey-cli, license lic_new, scope operate", human.Stdout);
        Assert.Contains("License     : lic_new", human.Stdout);

        CliRun json = await Run(server, "whoami", "--api-base", FakeAuthServer.Api, "--config", Path.Combine(_dir, "no-queuey.json"), "--json");
        JsonElement login = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("login");
        Assert.Equal("operate", login.GetProperty("scope").GetString());
        Assert.Equal("queuey-cli", login.GetProperty("client").GetString());
        Assert.DoesNotContain("opaque", human.Stdout + json.Stdout);
        Assert.Empty(server.Handler.Requests); // whoami kobler ikke til
    }

    [Fact]
    public async Task A_credentials_file_others_can_read_is_not_read()
    {
        if (OperatingSystem.IsWindows())
            return;
        var server = new FakeAuthServer(CreatedQueue);
        StoreLogin(server, TimeSpan.FromMinutes(30));
        File.SetUnixFileMode(CredentialsFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        CliRun run = await CreateQueue(server);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains($"{CredentialsFile} holds login tokens, and other users can reach it (mode 0640), so it was not read.", run.Stderr);
        Assert.Empty(server.Handler.Requests);
    }

    // ── logout ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_revokes_the_connection_with_queuey_and_forgets_it_here()
    {
        var server = new FakeAuthServer(CreatedQueue);
        StoreLogin(server, TimeSpan.FromMinutes(30));

        CliRun run = await Run(server, "logout", "--api-base", FakeAuthServer.Api, "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("logged_out", Line(run.Stdout).GetProperty("status").GetString());
        Dictionary<string, string>[] revoked = server.FormsTo("/connect/revoke").ToArray();
        Assert.Equal(new[] { "rt1-opaque", "at1-opaque" }, revoked.Select(f => f["token"]).ToArray());
        Assert.Equal(new[] { "refresh_token", "access_token" }, revoked.Select(f => f["token_type_hint"]).ToArray());
        Assert.All(revoked, f => Assert.Equal("queuey-cli", f["client_id"]));
        Assert.Empty(Stored().GetProperty("logins").EnumerateArray());

        CliRun again = await Run(server, "logout", "--api-base", FakeAuthServer.Api);
        Assert.Equal(ExitCodes.Success, again.Exit);
        Assert.Contains("Not logged in to https://api.test.", again.Stdout);

        CliRun command = await CreateQueue(server);
        Assert.Equal(ExitCodes.Configuration, command.Exit);
        Assert.Contains("queuey login", command.Stderr);
    }
}
