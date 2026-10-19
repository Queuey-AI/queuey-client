using System.Text.Json;
using Queuey.Client.Cli;
using ListenEnd = Queuey.Client.Cli.ListenCommand.ListenEnd;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// <c>queuey listen</c> står til den stoppes (langtesten 2026-10-19). Lytteren stoppet knapt en time etter queuey login: hubben
/// lukker forbindelsen når access-tokenet går ut, uten å la SignalR koble seg til igjen. Nå kobler økten seg til igjen selv,
/// med et fornyet token, krever køen igjen med samme sesjons-id, og prøver til den lykkes. Den slutter bare når den stoppes,
/// når en annen økt har køen, og når Queuey avviser den av en grunn et nytt forsøk ikke endrer. Hubben er en falsk her, og
/// ventetidene er injisert, så ingen test venter på ekte tid.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ListenSessionTests : IDisposable
{
    private static readonly ListenTarget Queue = new("queue", "que_orders", Name: "orders");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-listen-session-tests", Guid.NewGuid().ToString("N"));
    private readonly Func<DateTimeOffset> _clock = LoginTokens.Now;
    private DateTimeOffset _now = new(2026, 10, 19, 18, 25, 0, TimeSpan.Zero);
    private readonly StringWriter _stdout = new(), _stderr = new();
    private readonly List<TimeSpan> _waits = new();

    public ListenSessionTests()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(Path.Combine(_dir, ".queuey"));
        else
        {
            Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(_dir, ".queuey"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        LoginTokens.Now = () => _now;
    }

    public void Dispose()
    {
        LoginTokens.Now = _clock;
        CliHost.TestHandler = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Credentials => Path.Combine(_dir, ".queuey", "credentials.json");

    private ListenSession Session(FakeListenHub hub, ListenLoginToken? login = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(hub.Connect, Queue, "https://api.test/hubs/listen", "http://localhost:5000",
            new ListenOutput(json: true, _stdout, _stderr), login)
        {
            Delay = delay ?? ((wait, _) =>
            {
                lock (_waits)
                    _waits.Add(wait);
                return Task.CompletedTask;
            }),
            Jitter = () => 0.5,
            // Ingen hjerteslag med mindre testen ber om dem.
            BeatWait = ct => Task.Delay(Timeout.Infinite, ct),
        };

    /// <summary>A login stored as if <c>queuey login</c> had run, with tokens <paramref name="server"/> knows, and its token for a session.</summary>
    private ListenLoginToken Login(FakeAuthServer server)
    {
        (string access, string refresh) = server.Issue();
        LoginStore.Write(Credentials, new CredentialsFile
        {
            Logins =
            {
                new StoredLogin
                {
                    ApiBase = FakeAuthServer.Api, License = "lic_new", Scope = "operate", AccessToken = access,
                    AccessTokenExpiresAt = _now + TimeSpan.FromHours(1), RefreshToken = refresh, LoggedInAt = _now,
                },
            },
        });
        CliHost.TestHandler = server.Handler;
        StoredLogin stored = LoginStore.Read(Credentials).Logins.Single();
        return new ListenLoginToken(new LoginTokens(Credentials, new Uri(FakeAuthServer.Api), stored));
    }

    private string[] Types()
        => _stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()!)
            .ToArray();

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task A_connection_Queuey_closes_is_followed_by_a_new_one_that_claims_the_queue_again_as_the_same_session()
    {
        var hub = new FakeListenHub();
        ListenSession session = Session(hub);
        Task<ListenEnd> run = session.RunAsync(takeOver: true);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        // Som Context.Abort() i hubben: Closed uten feil, og SignalR prøver ikke selv.
        hub.Connections[0].Drop(null);
        await Until(() => hub.Claims.Count == 2, "the claim on the new connection");
        session.End(new ListenEnd("stopped"));
        ListenEnd end = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("stopped", end.Reason);
        Assert.Equal(2, hub.Connections.Count);
        Assert.Equal((session.SessionId, true), hub.Claims[0]);
        Assert.Equal((session.SessionId, false), hub.Claims[1]);
        Assert.Equal(new[] { "listening", "reconnecting", "reconnected" }, Types());
        Assert.Equal(new[] { TimeSpan.Zero }, _waits);
        Assert.Contains("Queuey closed the connection", _stderr.ToString());
        Assert.True(hub.Connections[1].Stopped, "the session freed the queue on the connection it had last");
    }

    [Fact]
    public async Task After_its_quick_attempts_fail_the_session_keeps_trying_every_30_to_60_seconds_until_it_is_back()
    {
        // Før ga SignalR opp etter åtte forsøk på halvannet minutt, og kommandoen avsluttet med connection_lost.
        var hub = new FakeListenHub();
        for (int i = 0; i < 9; i++)
            hub.Starts.Enqueue(() => throw new HttpRequestException("Connection refused"));
        ListenSession session = Session(hub);
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        hub.StartsAfterFirst();
        hub.Connections[0].Drop(new IOException("The remote party closed the WebSocket connection"));
        await Until(() => hub.Claims.Count == 2, "the claim once Queuey answers again");

        Assert.False(run.IsCompleted, "the session listens again");
        session.End(new ListenEnd("stopped"));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        TimeSpan[] expected = ListenSession.ReconnectDelays.Concat(Enumerable.Repeat(TimeSpan.FromSeconds(45), 3)).ToArray();
        Assert.Equal(expected, _waits);
        Assert.Equal(11, hub.Connections.Count);
        Assert.Equal(hub.Claims[0].SessionId, hub.Claims[1].SessionId);
        Assert.Equal(Enumerable.Repeat("reconnecting", 10).Prepend("listening").Append("reconnected"), Types());
        Assert.Contains("attempt 10 in 45s (Connection refused)", _stderr.ToString());
    }

    [Fact]
    public void Waits_after_the_quick_attempts_are_spread_between_30_and_60_seconds()
    {
        var hub = new FakeListenHub();
        ListenSession early = Session(hub);
        Assert.Equal(TimeSpan.Zero, early.RetryDelay(1));

        var low = new ListenSession(hub.Connect, Queue, "u", "f", new ListenOutput(true, _stdout, _stderr), null) { Jitter = () => 0 };
        var high = new ListenSession(hub.Connect, Queue, "u", "f", new ListenOutput(true, _stdout, _stderr), null) { Jitter = () => 0.999 };
        int later = ListenSession.ReconnectDelays.Length + 1;
        Assert.Equal(TimeSpan.FromSeconds(30), low.RetryDelay(later));
        Assert.InRange(high.RetryDelay(later + 100), TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task An_access_token_that_has_run_out_is_renewed_for_the_new_connection()
    {
        var server = new FakeAuthServer();
        var hub = new FakeListenHub();
        ListenLoginToken login = Login(server);
        hub.Token = login.GetAsync;
        ListenSession session = Session(hub, login);
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        // En time etter innloggingen: tokenet er utløpt, og hubben lukker forbindelsen ved neste hjerteslag.
        _now += TimeSpan.FromHours(1);
        hub.Connections[0].Drop(null);
        await Until(() => hub.Claims.Count == 2, "the claim with the renewed token");
        session.End(new ListenEnd("stopped"));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "at1-opaque", "at2-opaque" }, hub.Connections.Select(c => c.Token));
        Assert.Equal("rt1-opaque", Assert.Single(server.FormsTo("/connect/token"))["refresh_token"]);
        Assert.Equal("at2-opaque", JsonDocument.Parse(File.ReadAllText(Credentials)).RootElement.GetProperty("logins")[0].GetProperty("accessToken").GetString());
    }

    [Fact]
    public async Task A_token_Queuey_refuses_while_it_looks_fresh_here_is_renewed_once_and_the_session_comes_back()
    {
        // Klokkene går ulikt: hubben har lukket for et token som her har mer enn ett minutt igjen.
        var server = new FakeAuthServer();
        var hub = new FakeListenHub();
        ListenLoginToken login = Login(server);
        hub.Token = login.GetAsync;
        hub.Replies.Enqueue(Accepted);
        hub.Replies.Enqueue(new ListenReply(false, Code: "unauthorized", Message: "Unauthorized: a valid X-Api-Key header, or a token from queuey login as Authorization: Bearer, is required."));
        ListenSession session = Session(hub, login);
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        hub.Connections[0].Drop(null);
        await Until(() => hub.Claims.Count == 3, "the claim with the renewed token");
        Assert.False(run.IsCompleted, "the session listens again");
        session.End(new ListenEnd("stopped"));
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "at1-opaque", "at1-opaque", "at2-opaque" }, hub.Connections.Select(c => c.Token));
        Assert.Equal(new[] { "listening", "reconnecting", "reconnecting", "reconnected" }, Types());
    }

    [Fact]
    public async Task A_login_Queuey_still_refuses_after_renewing_ends_the_session_and_says_to_log_in_again()
    {
        var server = new FakeAuthServer();
        var hub = new FakeListenHub();
        ListenLoginToken login = Login(server);
        hub.Token = login.GetAsync;
        hub.Replies.Enqueue(Accepted);
        hub.Replies.Enqueue(new ListenReply(false, Code: "unauthorized", Message: "Unauthorized."));
        hub.Replies.Enqueue(new ListenReply(false, Code: "unauthorized", Message: "Unauthorized."));
        ListenSession session = Session(hub, login);
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        hub.Connections[0].Drop(null);
        ListenEnd end = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(("refused", "unauthorized"), (end.Reason, end.Code));
        Assert.Contains("queuey login", end.Action);
        Assert.Equal(3, hub.Claims.Count);
        Assert.Equal(ExitCodes.Configuration, ListenCommand.Finish(end, new ListenOutput(true, new StringWriter(), new StringWriter()), 0));
    }

    [Fact]
    public async Task A_key_Queuey_no_longer_accepts_ends_the_session_at_the_first_refusal()
    {
        var hub = new FakeListenHub();
        hub.Replies.Enqueue(Accepted);
        hub.Replies.Enqueue(new ListenReply(false, Code: "unauthorized", Message: "Unauthorized."));
        ListenSession session = Session(hub);
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        hub.Connections[0].Drop(null);
        ListenEnd end = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(("refused", "unauthorized"), (end.Reason, end.Code));
        Assert.Equal(2, hub.Claims.Count);
    }

    [Fact]
    public async Task A_login_that_can_no_longer_be_renewed_ends_the_session_and_says_to_log_in_again()
    {
        var server = new FakeAuthServer();
        var hub = new FakeListenHub();
        ListenLoginToken login = Login(server);
        hub.Token = login.GetAsync;
        ListenSession session = Session(hub, login);
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        // Tilkoblingen er koblet fra i konsollet: fornyelsen gir invalid_grant.
        _now += TimeSpan.FromHours(1);
        server.RevokeAll();
        hub.Connections[0].Drop(null);
        ListenEnd end = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(("refused", "login_ended"), (end.Reason, end.Code));
        Assert.Contains("has ended", end.Message);
        Assert.Contains("queuey login", end.Action);
        Assert.Single(hub.Claims);
    }

    [Fact]
    public async Task Another_session_holding_the_queue_when_this_one_comes_back_ends_it_as_superseded()
    {
        var hub = new FakeListenHub();
        hub.Replies.Enqueue(Accepted);
        hub.Replies.Enqueue(new ListenReply(false, Code: "listener_already_connected", Message: "another queuey listen session listens on listen:queue:que_orders."));
        ListenSession session = Session(hub);
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        hub.Connections[0].Drop(null);
        ListenEnd end = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("superseded", end.Reason);
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("scope_not_found")]
    public async Task A_refusal_another_attempt_does_not_change_ends_the_session_with_its_code(string code)
    {
        var hub = new FakeListenHub();
        hub.Replies.Enqueue(Accepted);
        hub.Replies.Enqueue(new ListenReply(false, Code: code, Message: "Forbidden: no."));
        ListenSession session = Session(hub);
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        hub.Connections[0].Drop(null);
        ListenEnd end = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(("refused", code), (end.Reason, end.Code));
        Assert.Equal(2, hub.Claims.Count);
    }

    [Fact]
    public async Task A_heartbeat_that_says_another_session_owns_the_queue_ends_it_as_superseded()
    {
        var hub = new FakeListenHub { Beat = new ListenBeat(Owner: false) };
        var session = new ListenSession(hub.Connect, Queue, "https://api.test/hubs/listen", "http://localhost:5000",
            new ListenOutput(json: true, _stdout, _stderr), null)
        {
            BeatWait = async _ => await Task.Yield(),
        };

        ListenEnd end = await session.RunAsync(takeOver: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("superseded", end.Reason);
    }

    [Fact]
    public async Task Ctrl_C_while_the_session_waits_to_try_again_stops_it_at_once()
    {
        var hub = new FakeListenHub();
        hub.Starts.Enqueue(() => throw new HttpRequestException("Connection refused"));
        ListenSession session = Session(hub, delay: (wait, ct) => wait == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(Timeout.Infinite, ct));
        Task<ListenEnd> run = session.RunAsync(takeOver: false);
        await Until(() => hub.Claims.Count == 1, "the first claim");

        hub.StartsAfterFirst();
        hub.Connections[0].Drop(null);
        await Until(() => hub.Connections.Count == 2 && Types().Count(t => t == "reconnecting") == 2, "the wait before the second attempt");
        session.End(new ListenEnd("stopped"));
        ListenEnd end = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("stopped", end.Reason);
        Assert.Equal(2, hub.Connections.Count);
    }

    private static readonly ListenReply Accepted = new(true, ScopeKey: "listen:queue:que_orders");

    /// <summary>
    /// En falsk listen-hub. Hver tilkobling spør om tokenet når den starter, som SignalR gjør, og svarer på ListenAsOwner med
    /// det neste i <see cref="Replies"/>, ellers med listening.
    /// </summary>
    private sealed class FakeListenHub
    {
        private readonly object _gate = new();
        private bool _firstStarted;
        private bool _scriptedStarts;

        public List<FakeConnection> Connections { get; } = new();
        public List<(string SessionId, bool TakeOver)> Claims { get; } = new();
        public Queue<ListenReply> Replies { get; } = new();

        /// <summary>How the starts after the first go, in order, once <see cref="StartsAfterFirst"/> is called; then they succeed.</summary>
        public Queue<Action> Starts { get; } = new();

        public ListenBeat? Beat { get; init; } = new(Owner: true);
        public Func<Task<string?>>? Token { get; set; }

        public void StartsAfterFirst()
        {
            lock (_gate)
                _scriptedStarts = true;
        }

        public IListenConnection Connect()
        {
            var connection = new FakeConnection(this);
            lock (_gate)
                Connections.Add(connection);
            return connection;
        }

        internal void Starting()
        {
            lock (_gate)
            {
                if (!_firstStarted)
                {
                    _firstStarted = true;
                    return;
                }
                if (!_scriptedStarts || Starts.Count == 0)
                    return;
                Starts.Dequeue()();
            }
        }

        internal ListenReply Claim(string sessionId, bool takeOver)
        {
            lock (_gate)
            {
                Claims.Add((sessionId, takeOver));
                return Replies.Count > 0 ? Replies.Dequeue() : Accepted;
            }
        }
    }

    private sealed class FakeConnection : IListenConnection
    {
        private readonly FakeListenHub _hub;
        private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeConnection(FakeListenHub hub) => _hub = hub;

        public string? Token { get; private set; }
        public bool Stopped { get; private set; }
        public bool IsConnected { get; private set; }
        public Task<Exception?> Closed => _closed.Task;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_hub.Token is not null)
                Token = await _hub.Token();
            _hub.Starting();
            IsConnected = true;
        }

        /// <summary>The connection closes from Queuey's side or the network's.</summary>
        public void Drop(Exception? why)
        {
            IsConnected = false;
            _closed.TrySetResult(why);
        }

        public Task<TResult> InvokeAsync<TResult>(string method, object?[] args, CancellationToken cancellationToken)
        {
            if (!IsConnected)
                throw new InvalidOperationException("The connection is not active.");
            object? result = method switch
            {
                "ListenAsOwner" => _hub.Claim((string)args[2]!, (bool)args[3]!),
                "Heartbeat" => _hub.Beat,
                _ => throw new InvalidOperationException($"The fake hub has no {method}."),
            };
            return Task.FromResult((TResult)result!);
        }

        public Task InvokeAsync(string method, object?[] args, CancellationToken cancellationToken)
        {
            if (method == "Stop")
                Stopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
