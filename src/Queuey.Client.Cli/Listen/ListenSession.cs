using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using ListenEnd = Queuey.Client.Cli.ListenCommand.ListenEnd;

namespace Queuey.Client.Cli;

/// <summary>
/// One <c>queuey listen</c> session: it claims its queue or workspace, beats to keep the claim, and when the connection is
/// lost, from either side, connects again with a new connection and a fresh token and claims the scope again under the same
/// session id, for as long as it takes. It ends only when it is stopped (<see cref="End"/>), when another session has the
/// scope (superseded), or when Queuey refuses it for a reason another attempt does not change.
/// </summary>
// 2026-10-19 (langtest): lytteren stoppet knapt en time etter queuey login. Hubben sjekker tokenet fra WebSocketens
// oppkobling ved hvert hjerteslag, og når access-tokenet (1 t) går ut, lukker den forbindelsen med Context.Abort(). Det sier
// til SignalR-klienten at den ikke skal koble seg til igjen, så Closed kom uten feil, og kommandoen avsluttet.
internal sealed class ListenSession
{
    /// <summary>
    /// The waits before the first attempts to connect again. Queuey keeps the claim of a session whose connection is gone for
    /// 10 s, and the first four attempts land within that.
    /// </summary>
    internal static readonly TimeSpan[] ReconnectDelays =
    {
        TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
    };

    /// <summary>After <see cref="ReconnectDelays"/>, each wait is between these two, spread so listeners do not come back at once.</summary>
    internal static readonly TimeSpan RetryFloor = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan RetryCeiling = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(8);

    private readonly Func<IListenConnection> _connect;
    private readonly ListenTarget _target;
    private readonly string _hubUrl;
    private readonly string _forwardTo;
    private readonly ListenOutput _output;
    private readonly ListenLoginToken? _login;
    private readonly TaskCompletionSource<ListenEnd> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private volatile IListenConnection? _current;
    private volatile bool _ownerAware = true;

    /// <param name="connect">A new, unstarted connection to the hub, for the first one and each one after a loss.</param>
    /// <param name="login">The login's token, when the session uses a login rather than an API key.</param>
    public ListenSession(
        Func<IListenConnection> connect, ListenTarget target, string hubUrl, string forwardTo, ListenOutput output, ListenLoginToken? login)
    {
        _connect = connect;
        _target = target;
        _hubUrl = hubUrl;
        _forwardTo = forwardTo;
        _output = output;
        _login = login;
    }

    /// <summary>The session's own id, the same on every connection, so a session that connects again keeps its scope.</summary>
    public string SessionId { get; } = "cli_" + Guid.NewGuid().ToString("N");

    /// <summary>The connection the session uses now, or is trying to.</summary>
    public IListenConnection? Current => _current;

    /// <summary>Waits before an attempt to connect again; a seam for tests.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>A number in [0, 1) that spreads the waits after <see cref="ReconnectDelays"/>; a seam for tests.</summary>
    internal Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>Waits between heartbeats; a seam for tests.</summary>
    internal Func<CancellationToken, Task> BeatWait { get; init; } = ct => Task.Delay(HeartbeatInterval, ct);

    /// <summary>Ends the session with <paramref name="end"/>, unless it has ended already.</summary>
    public void End(ListenEnd end)
    {
        if (_ended.TrySetResult(end))
            _stopping.Cancel();
    }

    /// <summary>
    /// Listens until the session ends, and says how. The scope is freed and the connection closed before this returns, so
    /// the line written after it can say the queue is free.
    /// </summary>
    public async Task<ListenEnd> RunAsync(bool takeOver)
    {
        ListenEnd start = await StartAsync(takeOver);
        if (start.Reason != "listening")
            End(start);

        Task heartbeats = _ended.Task.IsCompleted ? Task.CompletedTask : HeartbeatLoopAsync(_stopping.Token);
        while (!_ended.Task.IsCompleted)
        {
            IListenConnection connection = _current!;
            await Task.WhenAny(_ended.Task, connection.Closed);
            if (_ended.Task.IsCompleted)
                break;

            Exception? why = await connection.Closed;
            await DisposeQuietlyAsync(connection);
            if (await ReconnectAsync(why) is { } end)
                End(end);
        }

        ListenEnd result = await _ended.Task;
        await heartbeats;
        // Økten stoppes før siste linje (review av #50, K5): den som leser linjen, kan stole på at køen er fri.
        await StopQuietlyAsync(_current);
        return result;
    }

    /// <summary>The first connection and claim: <c>listening</c> once it has the scope, else why not.</summary>
    private async Task<ListenEnd> StartAsync(bool takeOver)
    {
        IListenConnection connection = _current = _connect();
        ListenReply reply;
        try
        {
            using var starting = new CancellationTokenSource(StartTimeout);
            await connection.StartAsync(starting.Token);
            reply = await ClaimAsync(connection, takeOver, starting.Token);
        }
        catch (Exception ex) when (LoginTokens.IsEnded(ex))
        {
            return LoginEnded(ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            return new ListenEnd("refused", $"Could not connect to {_hubUrl}: {ex.Message}", "unreachable",
                Action: "Check --api-base (QUEUEY_API_BASE) and the network.");
        }
        catch (HubException ex)
        {
            return new ListenEnd("refused", ListenCommand.ServerMessage(ex), "refused");
        }

        if (!reply.Listening)
            return Refused(reply);

        _output.Listening(_target, reply.ScopeKey ?? string.Empty, _forwardTo, reply.TookOver);
        return new ListenEnd("listening");
    }

    /// <summary>
    /// Connects again after <paramref name="lost"/> and claims the scope as the same session. Null once it listens again, or
    /// when the session was stopped meanwhile; otherwise how the session ends.
    /// </summary>
    private async Task<ListenEnd?> ReconnectAsync(Exception? lost)
    {
        string reason = lost is null ? "Queuey closed the connection" : TerminalText.Line(lost.Message);
        bool renewed = false;
        for (int attempt = 1; ; attempt++)
        {
            TimeSpan wait = RetryDelay(attempt);
            _output.Reconnecting(attempt, wait, reason);
            try
            {
                await Delay(wait, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return null;
            }

            IListenConnection connection = _current = _connect();
            ListenReply reply;
            try
            {
                using var starting = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                starting.CancelAfter(StartTimeout);
                await connection.StartAsync(starting.Token);
                reply = await ClaimAsync(connection, takeOver: false, starting.Token);
            }
            catch (Exception ex) when (LoginTokens.IsEnded(ex))
            {
                return LoginEnded(ex);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                // Nettet, Queuey som starter på nytt, en fornyelse som ikke kom gjennom nå: neste forsøk kan lykkes.
                await DisposeQuietlyAsync(connection);
                reason = TerminalText.Line(ex.Message);
                continue;
            }

            if (reply.Listening)
            {
                _output.Reconnected(reply.ScopeKey);
                return null;
            }

            if (reply.Code == ListenerAlreadyConnected)
                return new ListenEnd("superseded", reply.Message ?? "Another queuey listen session has the queue now.");

            if (reply.Code == "unauthorized" && _login is not null && !renewed)
            {
                // Queuey nektet et token som så ferskt ut her (klokkene går ulikt): ett forsøk til med et fornyet token. Nektes
                // også det, er innloggingen koblet fra.
                renewed = true;
                _login.Refused();
                await DisposeQuietlyAsync(connection);
                reason = reply.Message ?? "Queuey refused the access token";
                attempt = 0;
                continue;
            }

            return Refused(reply);
        }
    }

    /// <summary>The wait before attempt <paramref name="attempt"/> (from 1) to connect again.</summary>
    internal TimeSpan RetryDelay(int attempt)
        => attempt <= ReconnectDelays.Length
            ? ReconnectDelays[attempt - 1]
            : RetryFloor + TimeSpan.FromMilliseconds((RetryCeiling - RetryFloor).TotalMilliseconds * Jitter());

    /// <summary>Claims the scope as this session; against an older Queuey, with the method it has.</summary>
    private async Task<ListenReply> ClaimAsync(IListenConnection connection, bool takeOver, CancellationToken ct)
    {
        if (_ownerAware)
        {
            try
            {
                return await connection.InvokeAsync<ListenReply>("ListenAsOwner",
                    new object?[] { _target.Kind, _target.Id, SessionId, takeOver }, ct);
            }
            catch (HubException ex) when (ListenCommand.IsUnknownMethod(ex, "ListenAsOwner"))
            {
                // A Queuey from before single-owner listening: it has only the old method, and no take-over.
                _ownerAware = false;
                _output.Note("Note: this Queuey does not know single-owner listening yet" + (takeOver ? ", so --take-over has no effect." : "."));
            }
        }

        try
        {
            ListenAck ack = await connection.InvokeAsync<ListenAck>("Listen", new object?[] { _target.Kind, _target.Id, "redirect" }, ct);
            return new ListenReply(Listening: true, ScopeKey: ack.ScopeKey);
        }
        catch (HubException ex)
        {
            return new ListenReply(Listening: false, Code: "refused", Message: ListenCommand.ServerMessage(ex));
        }
    }

    /// <summary>
    /// Beats every few seconds, which keeps the session's claim on its scope. A beat that says this session no longer owns
    /// the scope means another one took it over.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await BeatWait(ct);
                IListenConnection? connection = _current;
                if (connection is null || !connection.IsConnected)
                    continue;

                ListenBeat? beat = await connection.InvokeAsync<ListenBeat?>("Heartbeat", Array.Empty<object?>(), ct);
                if (beat is { Owner: false })
                {
                    End(new ListenEnd("superseded", "Another queuey listen session took the queue over, and forwards go to it now."));
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Forbindelsen er borte eller på vei bort (et kall som avbrytes da, er også en OperationCanceledException):
                // kravet tåler et tapt slag eller to, og den nye forbindelsen krever scopet igjen.
            }
        }
    }

    private const string ListenerAlreadyConnected = "listener_already_connected";

    private ListenEnd Refused(ListenReply reply)
    {
        string code = reply.Code ?? "refused";
        // Med en innlogging er tokenet nettopp fornyet, så et unauthorized betyr at tilkoblingen er koblet fra, ikke en nøkkel.
        string? action = code == "unauthorized" && _login is not null
            ? "The login was disconnected or lost its access: run `queuey login`, then queuey listen again."
            : null;
        return new ListenEnd("refused", reply.Message ?? "Queuey refused the listen session.", code, action, reply.HeldSinceUtc);
    }

    private static ListenEnd LoginEnded(Exception ex)
        => new("refused", ex.Message, "login_ended",
            Action: (ex as QueueyConfigurationException)?.SuggestedAction is { } action ? action + " Then run queuey listen again." : null);

    /// <summary>Frees the scope and closes the connection, each within a few seconds whatever the network does.</summary>
    private static async Task StopQuietlyAsync(IListenConnection? connection)
    {
        if (connection is null)
            return;

        try
        {
            if (connection.IsConnected)
            {
                using var stopping = new CancellationTokenSource(StopTimeout);
                await connection.InvokeAsync("Stop", Array.Empty<object?>(), stopping.Token);
            }
        }
        catch
        {
            // Best-effort; the claim also runs out once heartbeats stop.
        }

        await DisposeQuietlyAsync(connection);
    }

    private static async Task DisposeQuietlyAsync(IListenConnection connection)
    {
        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(StopTimeout);
        }
        catch
        {
            // The connection is done with either way.
        }
    }
}

/// <summary>
/// The login's access token for the connections of one session: renewed when it runs out, and when Queuey refused the one
/// sent last.
/// </summary>
internal sealed class ListenLoginToken
{
    private readonly Func<string?, CancellationToken, Task<string>> _token;
    private volatile string? _sent;
    private volatile string? _refused;

    public ListenLoginToken(LoginTokens login) : this(login.AccessTokenAsync)
    {
    }

    /// <param name="token">The token, given the one Queuey refused, if any.</param>
    internal ListenLoginToken(Func<string?, CancellationToken, Task<string>> token) => _token = token;

    /// <summary>The token for a connection that starts now.</summary>
    public async Task<string?> GetAsync()
    {
        string token = await _token(_refused, CancellationToken.None);
        _sent = token;
        return token;
    }

    /// <summary>Queuey refused the token sent last: the next connection gets a renewed one.</summary>
    public void Refused() => _refused = _sent;
}
