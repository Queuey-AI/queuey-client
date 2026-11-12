using System.ComponentModel;
using Queuey.Client.Cli;
using Queuey.Client.Cli.Server;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// <c>queuey server</c> against a Docker that answers from the test. What is under test is what the CLI asks Docker to do,
/// and what it writes in the folder: the package comes out of the image for the version, the installation's own files
/// survive an upgrade, and the data is deleted only when that is asked for twice. The real run against Docker is in the
/// PR (selfhost 4.8): init, start with the test delivery, stop, upgrade, delete.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ServerCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queuey-server-cli-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dir;
    private readonly FakeDocker _docker = new();

    public ServerCommandTests()
    {
        _dir = Path.Combine(_root, "server");
        _docker.Images["queuey/server:1.0.0"] = Package("1.0.0", "README.md", "docs/operations.md");
        _docker.Images["queuey/server:1.1.0"] = Package("1.1.0", "README.md", "docs/upgrade.md");
        ServerDocker.Runner = _docker.RunAsync;
        ServerCommand.IsTerminal = () => false;
    }

    public void Dispose()
    {
        ServerDocker.Runner = (_, _) => throw new InvalidOperationException("No docker outside a test.");
        ServerCommand.IsTerminal = () => !Console.IsInputRedirected && !Console.IsOutputRedirected;
        ServerCommand.ReadLine = Console.ReadLine;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── init ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Init_writes_the_package_from_the_versions_image_and_an_installation_for_this_machine()
    {
        var run = await Run("server", "init", "--version", "1.0.0", "--dir", _dir, "--console-port", "13000");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal("name: queuey # 1.0.0", File.ReadAllText(Path.Combine(_dir, "compose.yaml")));
        Assert.True(File.Exists(Path.Combine(_dir, "docs", "operations.md")));

        var env = ServerFolder.ReadEnv(Path.Combine(_dir, ".env"));
        Assert.Equal("1.0.0", env["QUEUEY_VERSION"]);
        Assert.Equal("idp", env["COMPOSE_PROFILES"]);
        Assert.Equal("http://127.0.0.1:13000", env["QUEUEY_CONSOLE_URL"]);
        Assert.Equal(ServerFolder.ProjectNameFor(_dir), env["COMPOSE_PROJECT_NAME"]);
        Assert.NotEqual(ServerFolder.ProjectNameFor(_dir), ServerFolder.ProjectNameFor(_dir + "-2"));
        Assert.Equal("13000", env["QUEUEY_CONSOLE_PORT"]);
        Assert.Equal("http://127.0.0.1:8082/realms/queuey", env["QUEUEY_OIDC_ISSUER"]);

        string overrides = File.ReadAllText(Path.Combine(_dir, "compose.override.yaml"));
        Assert.Contains("Cors__AllowLoopbackHttp=true", overrides);
        Assert.Contains("127.0.0.1:${QUEUEY_CONSOLE_PORT:-3000}:3000", overrides);
        Assert.Contains("host.docker.internal:host-gateway", overrides);

        Assert.Equal(64, File.ReadAllText(Path.Combine(_dir, "oidc-client-secret")).Trim().Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "oidc-client-secret")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
        }

        Assert.Contains("create queuey/server:1.0.0", _docker.Calls);
        Assert.Contains(_docker.Calls, c => c.StartsWith("cp container-1:/app/package/. ", StringComparison.Ordinal));
        Assert.Contains("rm container-1", _docker.Calls);
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("compose", StringComparison.Ordinal) && !c.StartsWith("compose version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Init_refuses_a_folder_that_already_holds_an_installation_and_reads_no_image()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "compose.yaml"), "mine");

        var run = await Run("server", "init", "--version", "1.0.0", "--dir", _dir);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("already holds an installation", run.Stderr);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir, "compose.yaml")));
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Init_writes_only_into_a_new_or_empty_folder()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "README.md"), "the repository's own");

        var run = await Run("server", "init", "--version", "1.0.0", "--dir", _dir);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("not empty", run.Stderr);
        Assert.Equal("the repository's own", File.ReadAllText(Path.Combine(_dir, "README.md")));
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Init_refuses_when_docker_still_has_the_volumes_of_an_earlier_installation_in_the_folder()
    {
        _docker.Volumes = $"{ServerFolder.ProjectNameFor(_dir)}_postgres\n{ServerFolder.ProjectNameFor(_dir)}_secrets\n";

        var run = await Run("server", "init", "--version", "1.0.0", "--dir", _dir);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("docker volume rm", run.Stderr);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task Nothing_starts_stops_or_deletes_when_the_compose_project_belongs_to_another_folder()
    {
        await Init();
        _docker.Owners = "/home/someone/queuey\n";

        var start = await Run("server", "start", "--dir", _dir);
        var stop = await Run("server", "stop", "--dir", _dir);
        var delete = await Run("server", "stop", "--delete-data", "--yes", "--dir", _dir);
        var upgrade = await Run("server", "upgrade", "--version", "1.1.0", "--dir", _dir);

        foreach (var run in new[] { start, stop, delete, upgrade })
        {
            Assert.Equal(ExitCodes.Configuration, run.Exit);
            Assert.Contains("belongs to the installation in /home/someone/queuey", run.Stderr);
        }
        Assert.DoesNotContain(_docker.Calls, c => c is "compose up -d" or "compose stop" || c.Contains("down", StringComparison.Ordinal) || c.Contains("backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_folder_init_made_does_not_start_when_a_port_listens_beyond_127_0_0_1()
    {
        await Init();
        _docker.Config = """{"name":"queuey-x","services":{"server":{"ports":[{"host_ip":"127.0.0.1","published":"8080"}]},"console":{"ports":[{"published":"3000"}]}}}""";

        var run = await Run("server", "start", "--dir", _dir);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("console publishes port 3000 on more than 127.0.0.1", run.Stderr);
        Assert.DoesNotContain("compose up -d", _docker.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("1.0")]
    public async Task Init_takes_only_a_pinned_version(string? version)
    {
        var args = new List<string> { "server", "init", "--dir", _dir };
        if (version is not null) args.AddRange(new[] { "--version", version });

        var run = await Run(args.ToArray());

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task An_image_that_is_not_here_and_cannot_be_pulled_leaves_no_folder_behind()
    {
        var run = await Run("server", "init", "--version", "9.9.9", "--dir", _dir);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("could not be pulled", run.Stderr);
        Assert.Contains("pull --quiet queuey/server:9.9.9", _docker.Calls);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task A_version_whose_image_carries_no_package_is_refused()
    {
        _docker.Images["queuey/server:0.9.0"] = new Dictionary<string, string>();

        var run = await Run("server", "init", "--version", "0.9.0", "--dir", _dir);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("has no Compose package", run.Stderr);
        Assert.False(Directory.Exists(_dir));
        Assert.Contains("rm container-1", _docker.Calls);
    }

    [Fact]
    public async Task Without_docker_every_verb_says_what_to_install()
    {
        _docker.Missing = true;

        var run = await Run("server", "status", "--dir", _dir);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("docker command was not found", run.Stderr);
        Assert.Contains("Docker Desktop", run.Stderr);
    }

    [Fact]
    public async Task A_verb_on_a_folder_without_an_installation_says_how_to_make_one()
    {
        var run = await Run("server", "start", "--dir", _dir);

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("queuey server init --version", run.Stderr);
    }

    // ── stop ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_keeps_the_data()
    {
        await Init();

        var run = await Run("server", "stop", "--dir", _dir);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("compose stop", _docker.Calls);
        Assert.DoesNotContain(_docker.Calls, c => c.Contains("down", StringComparison.Ordinal));
        Assert.Contains("The data is kept", run.Stdout);
    }

    [Fact]
    public async Task Deleting_the_data_without_a_terminal_needs_yes_and_deletes_nothing_without_it()
    {
        await Init();

        var run = await Run("server", "stop", "--delete-data", "--dir", _dir);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--yes", run.Stderr);
        Assert.DoesNotContain(_docker.Calls, c => c.Contains("down", StringComparison.Ordinal) || c == "compose stop");
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("DELETE it")]
    public async Task At_a_terminal_only_the_word_delete_deletes(string answer)
    {
        await Init();
        ServerCommand.IsTerminal = () => true;
        ServerCommand.ReadLine = () => answer;

        var run = await Run("server", "stop", "--delete-data", "--dir", _dir);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("Nothing was deleted", run.Stdout);
        Assert.DoesNotContain(_docker.Calls, c => c.Contains("down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleting_the_data_removes_the_volumes_and_the_developer_and_keeps_the_configuration()
    {
        await Init();
        File.WriteAllText(Path.Combine(_dir, "developer.json"), "{}");

        var run = await Run("server", "stop", "--delete-data", "--yes", "--dir", _dir);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("compose down --volumes --remove-orphans", _docker.Calls);
        Assert.False(File.Exists(Path.Combine(_dir, "developer.json")));
        Assert.True(File.Exists(Path.Combine(_dir, ".env")));
        Assert.True(File.Exists(Path.Combine(_dir, "compose.yaml")));
    }

    // ── upgrade ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Upgrade_backs_up_first_then_swaps_the_package_and_keeps_what_is_the_installations()
    {
        await Init();
        string secret = File.ReadAllText(Path.Combine(_dir, "oidc-client-secret"));
        File.WriteAllText(Path.Combine(_dir, "developer.json"), "{\"email\":\"d@x\"}");
        File.AppendAllText(Path.Combine(_dir, ".env"), "MY_OWN=kept\n");
        File.WriteAllText(Path.Combine(_dir, "compose.override.yaml"), "services: {} # mine");
        Directory.CreateDirectory(Path.Combine(_dir, "license"));
        File.WriteAllText(Path.Combine(_dir, "license", "queuey.license"), "signed");
        _docker.Calls.Clear();

        var run = await Run("server", "upgrade", "--version", "1.1.0", "--dir", _dir, "--no-test");

        Assert.Equal(ExitCodes.Success, run.Exit);
        int backup = _docker.Calls.IndexOf("compose run --rm backup");
        int plan = _docker.Calls.IndexOf("compose run --rm migrate migrate plan");
        int up = _docker.Calls.IndexOf("compose up -d");
        Assert.True(backup >= 0 && backup < plan && plan < up, string.Join(" | ", _docker.Calls));

        Assert.Equal("name: queuey # 1.1.0", File.ReadAllText(Path.Combine(_dir, "compose.yaml")));
        Assert.True(File.Exists(Path.Combine(_dir, "docs", "upgrade.md")));
        Assert.False(File.Exists(Path.Combine(_dir, "docs", "operations.md")), "a file the new package no longer has goes");

        var env = ServerFolder.ReadEnv(Path.Combine(_dir, ".env"));
        Assert.Equal("1.1.0", env["QUEUEY_VERSION"]);
        Assert.Equal("kept", env["MY_OWN"]);
        Assert.Equal(secret, File.ReadAllText(Path.Combine(_dir, "oidc-client-secret")));
        Assert.Equal("services: {} # mine", File.ReadAllText(Path.Combine(_dir, "compose.override.yaml")));
        Assert.Equal("signed", File.ReadAllText(Path.Combine(_dir, "license", "queuey.license")));
        Assert.Equal("{\"email\":\"d@x\"}", File.ReadAllText(Path.Combine(_dir, "developer.json")));
    }

    [Fact]
    public async Task A_failed_backup_upgrades_nothing()
    {
        await Init();
        _docker.Fail.Add("compose run --rm backup");

        var run = await Run("server", "upgrade", "--version", "1.1.0", "--dir", _dir);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("nothing was upgraded", run.Stderr);
        Assert.Equal("1.0.0", ServerFolder.ReadEnv(Path.Combine(_dir, ".env"))["QUEUEY_VERSION"]);
        Assert.Equal("name: queuey # 1.0.0", File.ReadAllText(Path.Combine(_dir, "compose.yaml")));
        Assert.DoesNotContain("compose up -d", _docker.Calls);
    }

    [Fact]
    public async Task Upgrade_to_the_version_it_runs_is_refused_before_anything_runs()
    {
        await Init();
        _docker.Calls.Clear();

        var run = await Run("server", "upgrade", "--version", "1.0.0", "--dir", _dir);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(new[] { "compose version --short" }, _docker.Calls);
    }

    // ── status, logs ────────────────────────────────────────────────────

    [Fact]
    public async Task Status_is_running_only_when_every_service_is_healthy_and_the_one_shots_ended_well()
    {
        await Init();
        _docker.Ps = Ps(("server", "running", "healthy", 0), ("console", "running", "healthy", 0), ("migrate", "exited", "", 0));

        var running = await Run("server", "status", "--dir", _dir, "--json");
        _docker.Ps = Ps(("server", "running", "unhealthy", 0), ("migrate", "exited", "", 0));
        var degraded = await Run("server", "status", "--dir", _dir);
        _docker.Ps = Ps(("server", "exited", "", 143), ("migrate", "exited", "", 0));
        var stopped = await Run("server", "status", "--dir", _dir);

        Assert.Equal(ExitCodes.Success, running.Exit);
        Assert.Contains("\"state\": \"running\"", running.Stdout);
        Assert.Equal(ExitCodes.RuntimeError, degraded.Exit);
        Assert.Contains(": degraded", degraded.Stdout);
        Assert.Equal(ExitCodes.RuntimeError, stopped.Exit);
        Assert.Contains(": stopped", stopped.Stdout);
        Assert.Contains("the data is kept", stopped.Stdout);
    }

    [Fact]
    public void Compose_ps_is_read_as_one_object_per_line_and_as_one_list()
    {
        const string lines = "{\"Service\":\"server\",\"State\":\"running\",\"Health\":\"healthy\",\"ExitCode\":0}\n{\"Service\":\"migrate\",\"State\":\"exited\",\"Health\":\"\",\"ExitCode\":1}\n";
        const string list = "[{\"Service\":\"server\",\"State\":\"running\",\"Health\":\"healthy\",\"ExitCode\":0},{\"Service\":\"migrate\",\"State\":\"exited\",\"Health\":\"\",\"ExitCode\":1}]";

        foreach (string output in new[] { lines, list })
        {
            var services = ServerCommand.ParseServices(output);
            Assert.Equal(new[] { "migrate", "server" }, services.Select(s => s.Service));
            Assert.False(services[0].Completed);
            Assert.True(services[1].Running);
        }
        Assert.Empty(ServerCommand.ParseServices(""));
    }

    [Fact]
    public async Task Logs_passes_the_services_and_options_to_compose()
    {
        await Init();

        var run = await Run("server", "logs", "server", "migrate", "--tail", "50", "--since", "10m", "--dir", _dir);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("compose logs --tail 50 --since 10m server migrate", _docker.Calls);
    }

    [Fact]
    public async Task An_unknown_server_subcommand_lists_the_ones_there_are()
    {
        var run = await Run("server", "deploy");

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("init | start | status | logs | stop | upgrade", run.Stderr);
    }

    // ── start og testleveringen ─────────────────────────────────────────

    [Fact]
    public async Task The_test_delivery_makes_an_account_of_its_own_when_the_person_only_sees_the_platforms()
    {
        await Init();
        WriteDeveloper();
        var api = new FakeQueuey(me: """{"licenses":[{"licensePublicId":"lic_platform","role":"Viewer","accessSource":"platform"}]}""",
            tenants: "[]");

        var run = await Start(api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("evt_test reached a receiver on this machine", run.Stdout.Replace("went in through the ingress and ", ""));
        Assert.Contains("POST /onboarding", api.Handler.Requests.Select(r => r.Key));
        Assert.Contains("POST /tenants", api.Handler.Requests.Select(r => r.Key));
        Assert.Contains("POST /queues", api.Handler.Requests.Select(r => r.Key));
        Assert.All(api.Handler.Requests.Where(r => r.Path is "/tenants" or "/queues"), r => Assert.Equal("lic_own", api.LicenseOf(r)));
    }

    [Fact]
    public async Task The_test_delivery_uses_the_persons_own_account_and_the_queue_it_made_before()
    {
        await Init();
        WriteDeveloper();
        var api = new FakeQueuey(
            me: """{"licenses":[{"licensePublicId":"lic_platform","role":"Viewer","accessSource":"platform"},{"licensePublicId":"lic_mine","role":"Owner","accessSource":"direct"}]}""",
            tenants: """[{"publicId":"ten_1","queues":[{"publicId":"que_old","name":"server-test","archivedAtUtc":"2026-11-01T00:00:00Z"},{"publicId":"que_1","name":"server-test","archivedAtUtc":null}]}]""");

        var run = await Start(api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        var keys = api.Handler.Requests.Select(r => r.Key).ToList();
        Assert.DoesNotContain("POST /onboarding", keys);
        Assert.DoesNotContain("POST /queues", keys);
        Assert.Contains("PATCH /queues/que_1/delivery", keys);
        Assert.Equal("lic_mine", api.LicenseOf(api.Handler.Requests.Single(r => r.Key == "PATCH /queues/que_1/delivery")));
    }

    [Fact]
    public async Task An_event_that_never_reaches_the_receiver_fails_start_and_says_where_to_look()
    {
        await Init();
        WriteDeveloper();
        var api = new FakeQueuey(me: """{"licenses":[{"licensePublicId":"lic_mine","role":"Owner","accessSource":"direct"}]}""",
            tenants: """[{"publicId":"ten_1","queues":[{"publicId":"que_1","name":"server-test","archivedAtUtc":null}]}]""") { Delivers = false };
        TimeSpan before = TestDelivery.Timeout;
        TestDelivery.Timeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var run = await Start(api);

            Assert.Equal(ExitCodes.RuntimeError, run.Exit);
            Assert.Contains("did not reach the receiver", run.Stderr);
            Assert.Contains("queuey server logs server", run.Stderr);
        }
        finally
        {
            TestDelivery.Timeout = before;
        }
    }

    [Fact]
    public async Task The_first_start_creates_the_developer_with_a_private_password_file_and_never_shows_the_admin_password()
    {
        await Init();
        var keycloak = new FakeKeycloak(createStatus: System.Net.HttpStatusCode.Created);

        var run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "server", "start", "--no-test", "--dir", _dir }), keycloak.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        string file = Path.Combine(_dir, "developer.json");
        Assert.Contains("\"subject\": \"kc-user-1\"", File.ReadAllText(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Contains("compose run --rm migrate bootstrap-admin --provider oidc --sub kc-user-1 --email developer@queuey.localhost --role Admin.Owner --yes", _docker.Calls);
        Assert.Contains("POST /admin/realms/queuey/clients", keycloak.Handler.Requests.Select(r => r.Key));
        Assert.DoesNotContain(_docker.AdminPassword, run.Stdout + run.Stderr);
        Assert.DoesNotContain(_docker.AdminPassword, string.Join(" ", _docker.Calls));
        Assert.All(keycloak.Handler.Requests, r => Assert.Equal("127.0.0.1", r.Uri.Host));
    }

    [Fact]
    public async Task A_developer_who_exists_already_gets_a_new_password_only_when_exactly_one_person_has_the_email()
    {
        await Init();
        var one = new FakeKeycloak(System.Net.HttpStatusCode.Conflict, found: """[{"id":"kc-existing"}]""");
        var run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "server", "start", "--no-test", "--dir", _dir }), one.Handler);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("PUT /admin/realms/queuey/users/kc-existing/reset-password", one.Handler.Requests.Select(r => r.Key));

        File.Delete(Path.Combine(_dir, "developer.json"));
        var two = new FakeKeycloak(System.Net.HttpStatusCode.Conflict, found: """[{"id":"a"},{"id":"b"}]""");
        var refused = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "server", "start", "--no-test", "--dir", _dir }), two.Handler);

        Assert.Equal(ExitCodes.RuntimeError, refused.Exit);
        Assert.DoesNotContain(two.Handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.False(File.Exists(Path.Combine(_dir, "developer.json")));
    }

    /// <summary>The Keycloak admin API, as the first start uses it.</summary>
    private sealed class FakeKeycloak
    {
        public FakeKeycloak(System.Net.HttpStatusCode createStatus, string found = "[]")
            => Handler = new RecordingHandler(request =>
            {
                string key = request.Key;
                if (key.EndsWith("/openid-connect/token", StringComparison.Ordinal))
                    return Json(System.Net.HttpStatusCode.OK, """{"access_token":"admin-token"}""");
                if (key == "POST /admin/realms/queuey/users")
                {
                    var created = new HttpResponseMessage(createStatus);
                    if (createStatus == System.Net.HttpStatusCode.Created)
                        created.Headers.Location = new Uri("http://127.0.0.1:8082/admin/realms/queuey/users/kc-user-1");
                    return created;
                }
                if (key == "GET /admin/realms/queuey/users") return Json(System.Net.HttpStatusCode.OK, found);
                if (request.Method == HttpMethod.Put) return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
                if (key == "POST /admin/realms/queuey/clients") return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
                throw new InvalidOperationException($"Unexpected {key}");
            });

        public RecordingHandler Handler { get; }

        private static HttpResponseMessage Json(System.Net.HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private void WriteDeveloper()
        => File.WriteAllText(Path.Combine(_dir, "developer.json"), """{"email":"developer@queuey.localhost","password":"pw","subject":"sub-1"}""");

    private async Task<CliRun> Start(FakeQueuey api)
    {
        _docker.Ps = Ps(("server", "running", "healthy", 0), ("migrate", "exited", "", 0));
        return await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "server", "start", "--dir", _dir }), api.Handler);
    }

    /// <summary>
    /// Keycloak, the API and the ingress of a local installation. The ingress delivers the event the way the server would:
    /// it posts it to the address the queue was given, with host.docker.internal as this machine.
    /// </summary>
    private sealed class FakeQueuey
    {
        private string? _deliveryUrl;

        public FakeQueuey(string me, string tenants) => Handler = new RecordingHandler(request => Answer(request, me, tenants));

        public RecordingHandler Handler { get; }
        public bool Delivers { get; init; } = true;

        public string? LicenseOf(RecordedRequest request)
            => Handler.Headers[Handler.Requests.IndexOf(request)].TryGetValue("X-License-PublicId", out string? license) ? license : null;

        private HttpResponseMessage Answer(RecordedRequest request, string me, string tenants)
        {
            string key = request.Key;
            if (key.EndsWith("/openid-connect/token", StringComparison.Ordinal))
                return Json("""{"access_token":"token"}""");
            if (key == "GET /auth/me") return Json(me);
            if (key == "POST /onboarding") return Json("""{"licenseId":"lic_own"}""");
            if (key == "GET /tenants") return Json(tenants);
            if (key == "POST /tenants") return Json("""{"publicId":"ten_new"}""");
            if (key == "POST /queues") return Json("""{"publicId":"que_new"}""");
            if (key.EndsWith("/delivery", StringComparison.Ordinal) && request.Method == HttpMethod.Patch)
            {
                _deliveryUrl = request.Json.GetProperty("url").GetString();
                return Json("{}");
            }
            if (request.Method == HttpMethod.Patch) return Json("{}");
            if (key.StartsWith("POST /events/", StringComparison.Ordinal))
            {
                if (Delivers)
                    _ = Task.Run(async () =>
                    {
                        using var deliver = new HttpClient();
                        await deliver.PostAsync(_deliveryUrl!.Replace("host.docker.internal", "127.0.0.1"),
                            new StringContent(request.Body!, System.Text.Encoding.UTF8, "application/json"));
                    });
                return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted) { Content = new StringContent("""{"eventId":"evt_test"}""") };
            }
            if (key.StartsWith("GET /events/", StringComparison.Ordinal))
                return Json(Delivers ? """{"status":2}""" : """{"status":1}""");
            throw new InvalidOperationException($"Unexpected {key}");
        }

        private static HttpResponseMessage Json(string body)
            => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    // ── støtte ──────────────────────────────────────────────────────────

    private async Task Init()
    {
        var run = await Run("server", "init", "--version", "1.0.0", "--dir", _dir);
        Assert.Equal(ExitCodes.Success, run.Exit);
        _docker.Calls.Clear();
    }

    private static Task<CliRun> Run(params string[] args) => CliHarness.RunAsync(() => CliEntry.RunAsync(args));

    private static Dictionary<string, string> Package(string version, params string[] more)
    {
        var files = new Dictionary<string, string> { ["compose.yaml"] = $"name: queuey # {version}", [".env.example"] = "QUEUEY_VERSION=" };
        foreach (string file in more)
            files[file] = $"{file} of {version}";
        return files;
    }

    private static string Ps(params (string Service, string State, string Health, int Exit)[] services)
        => string.Join("\n", services.Select(s => $"{{\"Service\":\"{s.Service}\",\"State\":\"{s.State}\",\"Health\":\"{s.Health}\",\"ExitCode\":{s.Exit}}}"));

    /// <summary>
    /// Docker as the CLI uses it: images that carry a package (or none), <c>create</c>/<c>cp</c>/<c>rm</c>, and compose calls
    /// that succeed unless listed in <see cref="Fail"/>. Every call is recorded as its arguments joined by spaces.
    /// </summary>
    private sealed class FakeDocker
    {
        private string? _createdFrom;

        public Dictionary<string, Dictionary<string, string>> Images { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = new();
        public HashSet<string> Fail { get; } = new(StringComparer.Ordinal);
        public bool Missing { get; set; }
        public string Ps { get; set; } = "";
        public string Config { get; set; } = LoopbackConfig;
        public string Owners { get; set; } = "";
        public string Volumes { get; set; } = "";
        public string AdminPassword { get; set; } = "adm1n-only-in-the-volume";

        public const string LoopbackConfig = """{"name":"queuey-x","services":{"server":{"ports":[{"host_ip":"127.0.0.1","published":"8080"}]},"console":{"ports":[{"host_ip":"127.0.0.1","published":"3000"}]}}}""";

        public Task<DockerResult> RunAsync(DockerCall call, CancellationToken ct)
        {
            if (Missing)
                throw new Win32Exception("No such file or directory");
            string line = string.Join(" ", call.Args);
            Calls.Add(line);
            if (Fail.Contains(line))
                return Done(1, "", "it failed");

            string[] a = call.Args.ToArray();
            switch (a[0])
            {
                case "image":
                    return Done(Images.ContainsKey(a[^1]) ? 0 : 1, "sha256:x", "");
                case "pull":
                    return Done(1, "", $"Error response from daemon: pull access denied for {a[^1]}");
                case "create":
                    _createdFrom = a[1];
                    return Done(0, "container-1\n", "");
                case "cp":
                    foreach (var (path, content) in Images[_createdFrom!])
                    {
                        string target = Path.Combine(a[2], path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllText(target, content);
                    }
                    return Done(Images[_createdFrom!].Count > 0 ? 0 : 1, "", "");
                case "rm":
                    return Done(0, "", "");
                case "compose" when a.Length > 1 && a[1] == "ps":
                    return Done(0, Ps, "");
                case "compose" when a.Length > 1 && a[1] == "config":
                    return Done(0, Config, "");
                case "compose" when a.Contains("cat"):
                    return Done(0, AdminPassword + "\n", "");
                case "ps":
                    return Done(0, Owners, "");
                case "volume":
                    return Done(0, Volumes, "");
                default:
                    return Done(0, "", "");
            }
        }

        private static Task<DockerResult> Done(int exit, string stdout, string stderr) => Task.FromResult(new DockerResult(exit, stdout, stderr));
    }
}
