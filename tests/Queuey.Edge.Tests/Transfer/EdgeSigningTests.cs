using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Queuey.Client;
using Queuey.Edge;
using Queuey.Edge.Tests.TestSupport;

namespace Queuey.Edge.Tests.Transfer;

/// <summary>
/// Edge med signeringsnøkkel (Kenneth 2026-10-09): Edge signerer hver sending med klientens HMAC, i det den sender, så et
/// event som ventet i spoolen får fersk signatur. Hemmeligheten ligger aldri i spoolen, signeringsparet vinner over
/// API-nøkkelen, og konfigurasjonen leses etter klientens regler.
/// </summary>
public class EdgeSigningTests
{
    private const string KeyId = "hsk_01EDGE";
    private const string Secret = "edge-signing-secret-value";

    // ── signaturen ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_signed_transfer_carries_the_hmac_the_ingress_verifies_and_no_api_key()
    {
        var clock = new FakeClock();
        var ingress = new VerifyingIngress(Secret);
        var channel = Channel(ingress, clock, o => { o.SigningKeyId = KeyId; o.SigningSecret = Secret; });

        var attempt = await channel.SendAsync(Envelope(clock), 1, CancellationToken.None);

        Assert.Equal(TransferClass.Accepted, attempt.Outcome.Class);
        var seen = Assert.Single(ingress.Seen);
        Assert.True(seen.Verified, "the signature is the HMAC of the canonical request with the secret");
        Assert.Equal(KeyId, seen.Headers[QueueyHeaders.KeyId]);
        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), seen.Headers[QueueyHeaders.Timestamp]);
        Assert.False(seen.Headers.ContainsKey(QueueyHeaders.ApiKey));
        Assert.DoesNotContain(seen.Headers.Values, v => v.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_event_that_waited_in_the_spool_through_an_outage_is_signed_when_it_is_sent()
    {
        using var fx = new SpoolFixture();
        var ingress = new VerifyingIngress(Secret, refuseFirst: true);
        var channel = Channel(ingress, fx.Clock, o => { o.SigningKeyId = KeyId; o.SigningSecret = Secret; });
        var published = fx.Clock.UtcNow;
        await fx.Spool.EnqueueAsync(fx.Envelope(), CancellationToken.None);

        // Første forsøk mens nettet er nede, så seks timer i spoolen.
        var first = (await fx.Spool.ClaimReadyAsync(4, TimeSpan.FromMinutes(2), CancellationToken.None)).Single();
        await channel.SendAsync(first.Envelope, 1, CancellationToken.None);
        fx.Clock.Advance(TimeSpan.FromHours(6));
        var attempt = await channel.SendAsync(first.Envelope, 2, CancellationToken.None);

        Assert.Equal(TransferClass.Accepted, attempt.Outcome.Class);
        var seen = ingress.Seen.ToArray();
        Assert.Equal(2, seen.Length);
        Assert.All(seen, s => Assert.True(s.Verified));
        Assert.Equal(published.ToUnixTimeSeconds(), long.Parse(seen[0].Headers[QueueyHeaders.Timestamp], CultureInfo.InvariantCulture));
        Assert.Equal(published.AddHours(6).ToUnixTimeSeconds(), long.Parse(seen[1].Headers[QueueyHeaders.Timestamp], CultureInfo.InvariantCulture));
        Assert.NotEqual(seen[0].Headers[QueueyHeaders.Nonce], seen[1].Headers[QueueyHeaders.Nonce]);
    }

    [Fact]
    public async Task The_secret_is_never_in_the_spool_and_a_published_event_arrives_signed()
    {
        var dir = Directory.CreateTempSubdirectory("queuey-edge-signing-");
        try
        {
            var ingress = new VerifyingIngress(Secret);
            await using (var edge = await QueueyEdge.StartAsync(o =>
                         {
                             o.SigningKeyId = KeyId;
                             o.SigningSecret = Secret;
                             o.TenantPublicId = "ten_test";
                             o.IngressBaseAddress = new Uri("https://ingress.test/");
                             o.Storage.Path = Path.Combine(dir.FullName, "spool.db");
                             o.HttpMessageHandlerFactory = ingress.Another;
                         }))
            {
                await edge.PublishAsync("orders", new { seq = 1 });
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && ingress.Seen.IsEmpty)
                    await Task.Delay(50);
            }

            var seen = Assert.Single(ingress.Seen);
            Assert.True(seen.Verified);
            Assert.InRange(long.Parse(seen.Headers[QueueyHeaders.Timestamp], CultureInfo.InvariantCulture),
                DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var secret = Encoding.UTF8.GetBytes(Secret);
            foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
                Assert.False(Contains(File.ReadAllBytes(file.FullName), secret), $"{file.Name} holds the signing secret");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_node_with_only_a_signing_key_sends_no_health_report_since_the_check_in_takes_an_api_key()
    {
        // Queuey's check-in (EdgeNodeResolveMiddleware) validerer bare X-Api-Key per 2026-10-09: en signert rapport ville fått 401.
        var dir = Directory.CreateTempSubdirectory("queuey-edge-signing-health-");
        try
        {
            var ingress = new VerifyingIngress(Secret);
            await using (var edge = await QueueyEdge.StartAsync(o =>
                         {
                             o.SigningKeyId = KeyId;
                             o.SigningSecret = Secret;
                             o.TenantPublicId = "ten_test";
                             o.IngressBaseAddress = new Uri("https://ingress.test/");
                             o.Storage.Path = Path.Combine(dir.FullName, "spool.db");
                             o.Health.ReportToCloud = true;
                             o.HttpMessageHandlerFactory = ingress.Another;
                         }))
            {
                await edge.PublishAsync("orders", new { seq = 1 });
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && ingress.Seen.IsEmpty)
                    await Task.Delay(50);
                await Task.Delay(1000);
            }

            Assert.Contains(ingress.Seen, s => s.Path == "/events/ten_test/orders" && s.Verified);
            Assert.DoesNotContain(ingress.Seen, s => s.Path.EndsWith("/health", StringComparison.Ordinal));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_health_key_from_the_environment_goes_only_to_the_check_in_and_events_stay_signed()
    {
        // Security-review av #70 (B1): QUEUEY_EDGE_HEALTH_API_KEY er bare for innsjekken.
        var dir = Directory.CreateTempSubdirectory("queuey-edge-health-key-");
        try
        {
            var ingress = new VerifyingIngress(Secret);
            var env = Env(("QUEUEY_SIGNING_KEY_ID", KeyId), ("QUEUEY_SIGNING_SECRET", Secret), ("QUEUEY_TENANT", "ten_test"),
                ("QUEUEY_EDGE_HEALTH_API_KEY", "qak_h.healthonly"));
            await using (var edge = await QueueyEdge.StartAsync(o =>
                         {
                             o.UseEnvironmentVariables(env);
                             o.IngressBaseAddress = new Uri("https://ingress.test/");
                             o.Storage.Path = Path.Combine(dir.FullName, "spool.db");
                             o.Health.ReportToCloud = true;
                             o.HttpMessageHandlerFactory = ingress.Another;
                         }))
            {
                await edge.PublishAsync("orders", new { seq = 1 });
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline
                       && !(ingress.Seen.Any(s => s.Path.StartsWith("/events/", StringComparison.Ordinal))
                            && ingress.Seen.Any(s => s.Path.EndsWith("/health", StringComparison.Ordinal))))
                    await Task.Delay(50);
            }

            var events = ingress.Seen.Where(s => s.Path.StartsWith("/events/", StringComparison.Ordinal)).ToArray();
            var health = ingress.Seen.Where(s => s.Path.EndsWith("/health", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(events);
            Assert.NotEmpty(health);
            Assert.All(events, s =>
            {
                Assert.True(s.Verified);
                Assert.False(s.Headers.ContainsKey(QueueyHeaders.ApiKey));
            });
            Assert.All(health, s =>
            {
                Assert.Equal("qak_h.healthonly", s.Headers[QueueyHeaders.ApiKey]);
                Assert.False(s.Headers.ContainsKey(QueueyHeaders.Signature));
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Edge_reaches_the_client_only_through_its_public_surface()
    {
        // Security-review av #70 (K1): ingen InternalsVisibleTo fra Queuey.Client til Queuey.Edge, så pakkene kan versjoneres hver for seg.
        var friends = typeof(QueueyOptions).Assembly.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false)
            .Cast<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName)
            .ToArray();

        Assert.DoesNotContain("Queuey.Edge", friends);
    }

    [Theory]
    [InlineData("""{"error":{"code":"timestamp_out_of_range","message":"Timestamp is outside the allowed window."}}""", TransferReason.ClockSkew)]
    [InlineData("""{"error":{"code":"invalid_signature","message":"Signature mismatch."}}""", TransferReason.AuthenticationRejected)]
    [InlineData("not json", TransferReason.AuthenticationRejected)]
    public void A_401_for_the_timestamp_is_the_clock_not_the_key(string body, TransferReason reason)
    {
        // Security-review av #70 (K2).
        var outcome = new TransferOutcomeClassifier().ClassifyResponse(401, body, null, DateTimeOffset.UtcNow, 1);

        Assert.Equal(TransferClass.RequiresAction, outcome.Class);
        Assert.Equal(reason, outcome.Reason);
    }

    [Fact]
    public void The_runbook_installs_edge_env_as_root_only_and_starts_after_the_clock_is_synchronised()
    {
        // Security-review av #70 (K2, K3).
        string runbook = File.ReadAllText(RepoFile("docs/edge-operations.md"));
        string readme = File.ReadAllText(RepoFile("src/Queuey.Edge/README.md"));

        Assert.Contains("sudo install -m 600 -o root -g root /tmp/edge.env /etc/queuey/edge.env", runbook);
        Assert.Contains("After=network-online.target time-sync.target", runbook);
        Assert.Contains("Wants=network-online.target time-sync.target", runbook);
        Assert.DoesNotContain("owned by the queuey user", runbook);
        Assert.DoesNotContain("never rejects a", readme);
        Assert.Contains("timestamp_out_of_range", readme);
        // B1: ingen nøkkel i argv eller som literal i koden.
        foreach (string text in new[] { runbook, readme })
        {
            Assert.DoesNotContain("--api-key qak", text);
            Assert.DoesNotContain("o.ApiKey = \"", text);
        }
        Assert.Contains("QUEUEY_EDGE_HEALTH_API_KEY", readme);
        Assert.Contains("QUEUEY_EDGE_HEALTH_API_KEY", runbook);
    }

    [Fact]
    public async Task With_both_the_signing_pair_wins_over_the_api_key()
    {
        var clock = new FakeClock();
        var ingress = new VerifyingIngress(Secret);
        var channel = Channel(ingress, clock, o =>
        {
            o.ApiKey = "qak_id.licensewide";
            o.SigningKeyId = KeyId;
            o.SigningSecret = Secret;
        });

        await channel.SendAsync(Envelope(clock), 1, CancellationToken.None);

        var seen = Assert.Single(ingress.Seen);
        Assert.True(seen.Verified);
        Assert.False(seen.Headers.ContainsKey(QueueyHeaders.ApiKey));
    }

    [Fact]
    public async Task A_publish_key_alone_works_as_before()
    {
        var clock = new FakeClock();
        var ingress = new VerifyingIngress(Secret);
        var channel = Channel(ingress, clock, o => o.ApiKey = "qak_id.publishonly");

        await channel.SendAsync(Envelope(clock), 1, CancellationToken.None);

        var seen = Assert.Single(ingress.Seen);
        Assert.Equal("qak_id.publishonly", seen.Headers[QueueyHeaders.ApiKey]);
        Assert.False(seen.Headers.ContainsKey(QueueyHeaders.Signature));
    }

    // ── konfigurasjonen ──────────────────────────────────────────────────

    [Fact]
    public void The_environment_gives_the_pair_and_then_QUEUEY_API_KEY_is_not_read()
    {
        var env = Env(("QUEUEY_SIGNING_KEY_ID", KeyId), ("QUEUEY_SIGNING_SECRET", Secret), ("QUEUEY_API_KEY", "qak_id.licensewide"),
            ("QUEUEY_TENANT", "ten_env"), ("QUEUEY_INGRESS_BASE", "http://localhost:5084"));

        var options = new QueueyEdgeOptions().UseEnvironmentVariables(env);

        Assert.Equal(KeyId, options.SigningKeyId);
        Assert.Equal(Secret, options.SigningSecret);
        Assert.Null(options.ApiKey);
        Assert.Equal("ten_env", options.TenantPublicId);
        Assert.Equal(new Uri("http://localhost:5084"), options.IngressBaseAddress);
        options.Validate();
    }

    [Fact]
    public void Without_the_pair_QUEUEY_API_KEY_is_read_and_a_value_set_in_code_wins()
    {
        var options = new QueueyEdgeOptions { TenantPublicId = "ten_code" }
            .UseEnvironmentVariables(Env(("QUEUEY_API_KEY", "qak_id.publishonly"), ("QUEUEY_TENANT", "ten_env")));

        Assert.Equal("qak_id.publishonly", options.ApiKey);
        Assert.Equal("ten_code", options.TenantPublicId);
        Assert.Null(options.SigningKeyId);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void Dot_env_gives_the_pair_only_in_development(string environment, bool read)
    {
        var dir = Directory.CreateTempSubdirectory("queuey-edge-dotenv-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, ".env"),
                $"QUEUEY_SIGNING_KEY_ID={KeyId}\nQUEUEY_SIGNING_SECRET={Secret}\nQUEUEY_TENANT=ten_from_file\nQUEUEY_EDGE_HEALTH_API_KEY=qak_h.file\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(dir.FullName, ".env"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // Klientens .env-søm via InternalsVisibleTo til testprosjektet: Edge selv bruker bare de offentlige metodene (K1).
            var env = Env(("DOTNET_ENVIRONMENT", environment));
            var options = new QueueyEdgeOptions().FillFrom(client => client.UseEnvironmentVariables(env, dir.FullName), env);

            Assert.Equal(read ? KeyId : null, options.SigningKeyId);
            Assert.Equal(read ? Secret : null, options.SigningSecret);
            Assert.Null(options.TenantPublicId); // fra .env leses bare paret
            Assert.Null(options.Health.ApiKey);    // og aldri helse-nøkkelen
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Use_settings_reads_a_dotnet_configuration_and_no_dot_env()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["QUEUEY_SIGNING_KEY_ID"] = KeyId,
            ["QUEUEY_SIGNING_SECRET"] = Secret,
            ["QUEUEY_TENANT"] = "ten_settings",
        }).Build();

        var options = new QueueyEdgeOptions().UseSettings(configuration);

        Assert.Equal(KeyId, options.SigningKeyId);
        Assert.Equal(Secret, options.SigningSecret);
        Assert.Equal("ten_settings", options.TenantPublicId);
    }

    [Fact]
    public void Half_a_pair_or_no_credential_fails_at_validation()
    {
        var half = new QueueyEdgeOptions { TenantPublicId = "ten_test", SigningKeyId = KeyId, ApiKey = "qak_id.x" };
        var none = new QueueyEdgeOptions { TenantPublicId = "ten_test" };
        var pair = new QueueyEdgeOptions { TenantPublicId = "ten_test", SigningKeyId = KeyId, SigningSecret = Secret };

        Assert.Contains("go together", Assert.Throws<QueueyConfigurationException>(half.Validate).Message);
        Assert.Contains("signing key", Assert.Throws<QueueyConfigurationException>(none.Validate).Message);
        pair.Validate();
    }

    // ── plumbing ─────────────────────────────────────────────────────────

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static HttpTransferChannel Channel(VerifyingIngress ingress, FakeClock clock, Action<QueueyEdgeOptions> credentials)
    {
        var options = new QueueyEdgeOptions { TenantPublicId = "ten_test", IngressBaseAddress = new Uri("https://ingress.example") };
        credentials(options);
        options.Validate();
        return new HttpTransferChannel(new HttpClient(ingress), options, new TransferOutcomeClassifier(), clock);
    }

    private static EventEnvelope Envelope(FakeClock clock) => new(
        Version: EventEnvelope.CurrentVersion,
        TransferId: "transfer-1",
        Queue: "orders",
        TenantPublicId: "ten_test",
        ContentType: "application/json",
        Payload: """{"orderId":"10042"}"""u8.ToArray(),
        OccurredAtUtc: clock.UtcNow);

    private static bool Contains(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>
    /// A fake ingress that verifies as Queuey's does: the canonical request from <see cref="QueueyCanonicalRequest"/>, the
    /// content hash over the bytes that arrived, and HMAC-SHA256 with the secret as UTF-8, in lowercase hex.
    /// </summary>
    internal sealed class VerifyingIngress : HttpMessageHandler
    {
        private readonly byte[] _secret;
        private bool _refuseNext;

        public VerifyingIngress(string secret, bool refuseFirst = false, System.Collections.Concurrent.ConcurrentQueue<Received>? seen = null)
        {
            _secret = Encoding.UTF8.GetBytes(secret);
            _refuseNext = refuseFirst;
            Seen = seen ?? new();
        }

        /// <summary>A fresh handler that records into the same list: HttpClient disposes its handler.</summary>
        public VerifyingIngress Another() => new(Encoding.UTF8.GetString(_secret), seen: Seen);

        public System.Collections.Concurrent.ConcurrentQueue<Received> Seen { get; }

        public sealed record Received(string Path, Dictionary<string, string> Headers, bool Verified);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            Seen.Enqueue(new Received(request.RequestUri!.AbsolutePath, headers, Verify(request, body, headers)));

            if (_refuseNext)
            {
                _refuseNext = false;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"eventId":"evt_1"}""", Encoding.UTF8, "application/json")
            };
        }

        private bool Verify(HttpRequestMessage request, byte[] body, Dictionary<string, string> headers)
        {
            if (!headers.TryGetValue(QueueyHeaders.Timestamp, out var timestamp)
                || !headers.TryGetValue(QueueyHeaders.Nonce, out var nonce)
                || !headers.TryGetValue(QueueyHeaders.ContentSha256, out var contentSha)
                || !headers.TryGetValue(QueueyHeaders.Signature, out var signature))
                return false;
            if (Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant() != contentSha)
                return false;
            var canonical = QueueyCanonicalRequest.Build(request.Method.Method, request.RequestUri!, timestamp, nonce, contentSha);
            var expected = Convert.ToHexString(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
        }
    }
}
