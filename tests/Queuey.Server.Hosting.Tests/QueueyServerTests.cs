using System.Net;
using System.Text;
using Queuey.Client;
using Queuey.Server.Hosting;

namespace Queuey.Server.Hosting.Tests;

/// <summary>
/// <see cref="QueueyServer.EnsureRunningAsync"/> against a Docker and a Keycloak and Queuey that answer from the test. What is
/// under test is what it asks for and what it keeps: the first call makes the installation, the developer and the key; a
/// later call reuses the key; and it never runs another version than the folder holds.
/// </summary>
[Collection(nameof(DockerSeam))]
public sealed class QueueyServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queuey-server-hosting-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dir;
    private readonly FakeDocker _docker = new();
    private readonly FakeQueuey _queuey = new();

    public QueueyServerTests()
    {
        _dir = Path.Combine(_root, "server");
        Directory.CreateDirectory(_root);
        ServerDocker.Runner = _docker.RunAsync;
        QueueyServer.HttpFactory = () => new HttpClient(_queuey, disposeHandler: false);
        Installation.PollInterval = TimeSpan.Zero;
    }

    public void Dispose()
    {
        ServerDocker.Runner = (_, _) => throw new InvalidOperationException("No docker outside a test.");
        QueueyServer.HttpFactory = () => throw new InvalidOperationException("No HTTP outside a test.");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private QueueyServerOptions Options(string version = "1.0.0") => new(version) { Directory = _dir, ApiPort = 18080, IngressPort = 18081 };

    [Fact]
    public async Task The_first_call_makes_the_installation_the_developer_and_a_key_for_the_workspace()
    {
        var lines = new List<string>();
        var options = Options();
        options.Log = lines.Add;

        QueueyServerConnection connection = await QueueyServer.EnsureRunningAsync(options);

        Assert.Equal(new Uri("http://127.0.0.1:18080"), connection.ApiBase);
        Assert.Equal(new Uri("http://127.0.0.1:18081"), connection.IngressBase);
        Assert.Equal("ten_local", connection.TenantPublicId);
        Assert.Equal("lic_own", connection.LicensePublicId);
        Assert.Equal("qak_minted.secret", connection.ApiKey);
        Assert.Contains("compose up -d", _docker.Calls);
        Assert.Contains(_docker.Calls, c => c.Contains("bootstrap-admin", StringComparison.Ordinal));
        Assert.Contains("POST /licenses/lic_own/apikeys/starter", _queuey.Calls);
        Assert.Contains(lines, l => l.Contains("Starting Queuey 1.0.0", StringComparison.Ordinal));

        string kept = Path.Combine(_dir, "app-key.json");
        Assert.Contains("qak_minted.secret", File.ReadAllText(kept));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(kept));
        Assert.DoesNotContain("qak_minted", connection.ToString());
    }

    [Fact]
    public async Task A_later_call_starts_the_same_installation_and_reuses_the_key()
    {
        await QueueyServer.EnsureRunningAsync(Options());
        _docker.Calls.Clear();
        _queuey.Calls.Clear();

        QueueyServerConnection again = await QueueyServer.EnsureRunningAsync(Options());

        Assert.Equal("qak_minted.secret", again.ApiKey);
        Assert.DoesNotContain(_queuey.Calls, c => c.Contains("apikeys", StringComparison.Ordinal));
        Assert.DoesNotContain(_docker.Calls, c => c.StartsWith("create", StringComparison.Ordinal) || c.Contains("bootstrap-admin", StringComparison.Ordinal));
        Assert.Contains("compose up -d", _docker.Calls);
    }

    [Fact]
    public async Task It_never_runs_another_version_than_the_folder_holds()
    {
        await QueueyServer.EnsureRunningAsync(Options());
        _docker.Calls.Clear();

        var refused = await Assert.ThrowsAsync<QueueyConfigurationException>(() => QueueyServer.EnsureRunningAsync(Options("1.1.0")));

        Assert.Contains("runs Queuey 1.0.0, not 1.1.0", refused.Message);
        Assert.Contains("queuey server upgrade --version 1.1.0", refused.SuggestedAction);
        Assert.DoesNotContain("compose up -d", _docker.Calls);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1")]
    public async Task Only_a_pinned_version_is_taken_and_nothing_runs_before_that_is_checked(string version)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => QueueyServer.EnsureRunningAsync(Options(version)));

        Assert.Empty(_docker.Calls);
    }

    [Fact]
    public async Task A_folder_set_up_by_hand_gets_no_developer_and_no_key()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "compose.yaml"), "name: queuey");
        File.WriteAllText(Path.Combine(_dir, ".env"), "QUEUEY_VERSION=1.0.0\n");

        var refused = await Assert.ThrowsAsync<QueueyConfigurationException>(() => QueueyServer.EnsureRunningAsync(Options()));

        Assert.Contains("set up by hand", refused.Message);
        Assert.Empty(_queuey.Calls);
    }

    [Fact]
    public void An_app_in_a_container_reaches_this_machine_as_host_docker_internal()
    {
        var connection = new QueueyServerConnection("/x", "1.0.0", new LocalPorts(Api: 18080, Ingress: 18081), "d@x",
            new AppKeyRecord("qak_k.s", "ten_1", "lic_1"));

        var host = connection.ToEnvironment();
        var container = connection.ToEnvironment(fromContainer: true);

        Assert.Equal("http://127.0.0.1:18080", host[QueueyEnvironmentVariables.ApiBase]);
        Assert.Equal("http://host.docker.internal:18081", container[QueueyEnvironmentVariables.IngressBase]);
        Assert.Equal("qak_k.s", container[QueueyEnvironmentVariables.ApiKey]);
        Assert.Equal("ten_1", container[QueueyEnvironmentVariables.Tenant]);
        Assert.Equal("lic_1", container[QueueyEnvironmentVariables.License]);
    }

    /// <summary>Keycloak and the API of a local installation, the way the first call meets them.</summary>
    private sealed class FakeQueuey : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            lock (Calls) Calls.Add(key);
            HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
                => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            if (key.EndsWith("/openid-connect/token", StringComparison.Ordinal)) return Task.FromResult(Json("""{"access_token":"t"}"""));
            if (key == "POST /admin/realms/queuey/users")
            {
                var created = new HttpResponseMessage(HttpStatusCode.Created);
                created.Headers.Location = new Uri("http://127.0.0.1:8082/admin/realms/queuey/users/kc-1");
                return Task.FromResult(created);
            }
            if (key == "POST /admin/realms/queuey/clients") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
            if (key == "GET /auth/me") return Task.FromResult(Json("""{"licenses":[{"licensePublicId":"lic_own","role":"Owner","accessSource":"direct"}]}"""));
            if (key == "GET /tenants") return Task.FromResult(Json("""[{"publicId":"ten_local","queues":[]}]"""));
            if (key == "POST /licenses/lic_own/apikeys/starter") return Task.FromResult(Json("""{"apiKey":"qak_minted.secret"}"""));
            throw new InvalidOperationException($"Unexpected {key}");
        }
    }
}

/// <summary>The Docker seam is static, so the classes that set it run one at a time.</summary>
[CollectionDefinition(nameof(DockerSeam), DisableParallelization = true)]
public sealed class DockerSeam;

/// <summary>Docker as the hosting library uses it, answering for one healthy installation of 1.0.0.</summary>
internal sealed class FakeDocker
{
    public List<string> Calls { get; } = new();

    public Task<DockerResult> RunAsync(DockerCall call, CancellationToken ct)
    {
        string line = string.Join(" ", call.Args);
        lock (Calls) Calls.Add(line);
        string[] a = call.Args.ToArray();
        DockerResult Done(int exit = 0, string stdout = "") => new(exit, stdout, "");
        return Task.FromResult(a[0] switch
        {
            "image" => Done(),
            "create" => Done(stdout: "container-1\n"),
            "cp" => Write(a[2]),
            "compose" when a[1] == "config" => Done(stdout: """{"name":"queuey-x","services":{"server":{"ports":[{"host_ip":"127.0.0.1","published":"18080"}]}}}"""),
            "compose" when a[1] == "ps" => Done(stdout: """{"Service":"server","State":"running","Health":"healthy","ExitCode":0}"""),
            "compose" when a.Contains("cat") => Done(stdout: "admin-pw\n"),
            _ => Done(),
        });

        static DockerResult Write(string target)
        {
            File.WriteAllText(Path.Combine(target, "compose.yaml"), "name: queuey");
            File.WriteAllText(Path.Combine(target, ".env.example"), "QUEUEY_VERSION=");
            return new DockerResult(0, "", "");
        }
    }
}
