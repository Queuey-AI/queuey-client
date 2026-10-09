using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// To queuey-prosesser som fornyer samme innlogging samtidig (PR 4). Refresh-tokenet roterer, og et brukt et som brukes på nytt,
/// trekker tilbake hele tilkoblingen. Uten låsen ville begge brukt det samme, og den andre ville mistet innloggingen. Her går
/// CLI-en som to ekte prosesser mot en ekte HTTP-server på 127.0.0.1, som holder svaret på fornyelsen så de to overlapper.
/// </summary>
public sealed class LoginRenewalProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-login-process-tests", Guid.NewGuid().ToString("N"));

    public LoginRenewalProcessTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".queuey"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(_dir, ".queuey"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Two_processes_renewing_at_once_spend_the_refresh_token_once_and_both_keep_the_login()
    {
        if (OperatingSystem.IsWindows())
            return;

        int port = FreePort();
        string api = $"http://127.0.0.1:{port}";
        using var server = new RotatingTokenServer(api, holdRefresh: TimeSpan.FromSeconds(2));
        server.Start();

        string credentials = Path.Combine(_dir, ".queuey", "credentials.json");
        LoginStore.Write(credentials, new CredentialsFile
        {
            Logins =
            {
                new StoredLogin
                {
                    ApiBase = api, License = "lic_1", Scope = "operate", AccessToken = "at0",
                    AccessTokenExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1), RefreshToken = "rt0",
                    LoggedInAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1),
                },
            },
        });

        Task<(int Exit, string Out)> first = RunCli(api);
        Task<(int Exit, string Out)> second = RunCli(api);
        (int Exit, string Out)[] runs = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(90));

        foreach ((int exit, string output) in runs)
        {
            Assert.True(exit == ExitCodes.Success, output);
            JsonElement line = JsonDocument.Parse(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last()).RootElement;
            Assert.True(line.GetProperty("alreadyLoggedIn").GetBoolean(), output);
        }

        Assert.Equal(1, server.Refreshes);
        Assert.Equal(0, server.Reuses);
        JsonElement stored = JsonDocument.Parse(File.ReadAllText(credentials)).RootElement.GetProperty("logins")[0];
        Assert.Equal("rt1", stored.GetProperty("refreshToken").GetString());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(credentials) & (UnixFileMode)0x1FF);
    }

    [Fact]
    public async Task Two_processes_waiting_on_the_same_code_redeem_it_once_and_both_end_logged_in()
    {
        // Security-review av #66 (BØR 1): begge spurte om samme device-kode, og OpenIddict trekker tilbake hele autorisasjonen når en
        // kode løses inn to ganger. Serveren her gjør det samme, og holder godkjenningen i 2 s så de to overlapper.
        if (OperatingSystem.IsWindows())
            return;

        int port = FreePort();
        string api = $"http://127.0.0.1:{port}";
        using var server = new RotatingTokenServer(api, holdRefresh: TimeSpan.FromSeconds(2));
        server.Start();

        string credentials = Path.Combine(_dir, ".queuey", "credentials.json");
        LoginStore.Write(credentials, new CredentialsFile
        {
            Pending =
            {
                new PendingLogin
                {
                    ApiBase = api, Scope = "operate", DeviceCode = "dc1", UserCode = "WDJB-MJHT",
                    VerificationUri = "https://app.test/connect", Interval = 1,
                    CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1), ExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(9),
                },
            },
        });

        (int Exit, string Out)[] runs = await Task.WhenAll(RunCli(api), RunCli(api)).WaitAsync(TimeSpan.FromSeconds(90));

        foreach ((int exit, string output) in runs)
        {
            Assert.True(exit == ExitCodes.Success, output);
            JsonElement line = JsonDocument.Parse(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last()).RootElement;
            Assert.Equal("logged_in", line.GetProperty("status").GetString());
        }

        Assert.Equal(1, server.DevicePolls);
        Assert.Equal(0, server.Reuses);
        JsonElement stored = JsonDocument.Parse(File.ReadAllText(credentials)).RootElement;
        Assert.Equal("rt1", Assert.Single(stored.GetProperty("logins").EnumerateArray()).GetProperty("refreshToken").GetString());
        Assert.Empty(stored.GetProperty("pending").EnumerateArray());
    }

    private async Task<(int Exit, string Out)> RunCli(string api)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Queuey.Client.Cli.dll"));
        foreach (string arg in new[] { "login", "--api-base", api, "--json" })
            start.ArgumentList.Add(arg);
        foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("QUEUEY_", StringComparison.Ordinal)).ToList())
            start.Environment.Remove(name);
        start.Environment[UserProfiles.PathVariable] = Path.Combine(_dir, ".queuey", "config.json");

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Metadata, refresh med rotasjon og GET /tenants, over ekte HTTP. Et refresh-token virker én gang; brukt igjen gir det
    /// invalid_grant og trekker tilbake alt, som OpenIddict gjør etter slakken.
    /// </summary>
    private sealed class RotatingTokenServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _api;
        private readonly TimeSpan _holdRefresh;
        private readonly object _gate = new();
        private readonly HashSet<string> _refresh = new() { "rt0" };
        private readonly HashSet<string> _deviceCodes = new() { "dc1" };
        private readonly HashSet<string> _access = new();
        private int _issued;

        public RotatingTokenServer(string api, TimeSpan holdRefresh)
        {
            _api = api;
            _holdRefresh = holdRefresh;
            _listener.Prefixes.Add(api + "/");
        }

        public int Refreshes { get; private set; }
        public int DevicePolls { get; private set; }
        public int Reuses { get; private set; }

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }
                    _ = Task.Run(() => HandleAsync(context));
                }
            });
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            string path = request.Url!.AbsolutePath;
            string body = await new StreamReader(request.InputStream).ReadToEndAsync();
            (int status, object answer) = path switch
            {
                "/.well-known/oauth-authorization-server" => (200, new Dictionary<string, string>
                {
                    ["issuer"] = _api + "/",
                    ["device_authorization_endpoint"] = _api + "/connect/device",
                    ["token_endpoint"] = _api + "/connect/token",
                    ["revocation_endpoint"] = _api + "/connect/revoke",
                }),
                "/connect/token" => FakeAuthServer.Form(body)["grant_type"] == "refresh_token"
                    ? await RefreshAsync(FakeAuthServer.Form(body))
                    : await RedeemAsync(FakeAuthServer.Form(body)),
                "/tenants" => Authorized(request) ? (200, Array.Empty<object>()) : (401, new { }),
                _ => (404, new { }),
            };

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer));
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private async Task<(int, object)> RedeemAsync(Dictionary<string, string> form)
        {
            bool valid;
            lock (_gate)
            {
                DevicePolls++;
                valid = _deviceCodes.Remove(form.GetValueOrDefault("device_code") ?? "");
                if (!valid)
                {
                    Reuses++;
                    _refresh.Clear();
                    _access.Clear();
                }
            }

            if (!valid)
                return (400, new Dictionary<string, string> { ["error"] = "invalid_grant" });
            await Task.Delay(_holdRefresh);
            return Issue();
        }

        private (int, object) Issue()
        {
            lock (_gate)
            {
                _issued++;
                _refresh.Add($"rt{_issued}");
                _access.Add($"at{_issued}");
                return (200, new Dictionary<string, object>
                {
                    ["access_token"] = $"at{_issued}", ["token_type"] = "Bearer", ["expires_in"] = 3600,
                    ["refresh_token"] = $"rt{_issued}", ["scope"] = "operate", ["license"] = "lic_1",
                });
            }
        }

        private async Task<(int, object)> RefreshAsync(Dictionary<string, string> form)
        {
            bool valid;
            lock (_gate)
            {
                Refreshes++;
                valid = _refresh.Remove(form.GetValueOrDefault("refresh_token") ?? "");
                if (!valid)
                {
                    Reuses++;
                    _refresh.Clear();
                    _access.Clear();
                }
            }

            if (!valid)
                return (400, new Dictionary<string, string> { ["error"] = "invalid_grant" });

            // Svaret holdes, så den andre prosessen rekker å ville fornye mens den første venter på det.
            await Task.Delay(_holdRefresh);
            return Issue();
        }

        private bool Authorized(HttpListenerRequest request)
        {
            string? header = request.Headers["Authorization"];
            lock (_gate)
                return header is not null && header.StartsWith("Bearer ", StringComparison.Ordinal) && _access.Contains(header[7..]);
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}
