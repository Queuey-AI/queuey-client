using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Queuey.Client.Cli;

/// <summary>
/// One connection to Queuey's listen hub, as a <see cref="ListenSession"/> uses it: started once, closed once. A session that
/// loses one connects again with a new one.
/// </summary>
internal interface IListenConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// Completes when the connection closes after it started, from either side, with the error when there was one. It does
    /// not complete for a connection that never started.
    /// </summary>
    Task<Exception?> Closed { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task<TResult> InvokeAsync<TResult>(string method, object?[] args, CancellationToken cancellationToken);

    Task InvokeAsync(string method, object?[] args, CancellationToken cancellationToken);
}

/// <summary>A SignalR connection to <c>/hubs/listen</c>, with the deliveries and notices of the session it serves.</summary>
internal sealed class HubListenConnection : IListenConnection
{
    private readonly HubConnection _hub;
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="hubUrl">The hub's address.</param>
    /// <param name="apiKey">The API key, which wins when it is set.</param>
    /// <param name="accessToken">Otherwise the login's access token, asked for when the connection starts.</param>
    /// <param name="onEvent">A delivery to forward.</param>
    /// <param name="onLost">A workspace session lost one of its queues.</param>
    public HubListenConnection(
        string hubUrl, string? apiKey, Func<Task<string?>>? accessToken, Func<ListenEnvelope, Task> onEvent, Action<ListenLost> onLost)
    {
        // WebSockets uten forhandling (Kenneth, 2026-10-06): med forhandling må forhandlingen og WebSocketen treffe samme
        // replika av API-et, og sticky sessions er ikke satt for api i ACA. Uten er det én forbindelse til én replika.
        // Ingen automatisk gjenoppkobling (2026-10-19): sesjonen kobler seg til igjen selv, med en ny forbindelse og et
        // fornyet token, og gir aldri opp. SignalR sin plan ga opp etter halvannet minutt, og gjorde ingenting når Queuey
        // lukket forbindelsen selv, som den gjør når login-tokenet går ut.
        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                    options.Headers["X-Api-Key"] = apiKey!;
                else if (accessToken is not null)
                    options.AccessTokenProvider = accessToken;
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();

        _hub.On("event", onEvent);
        _hub.On("lost", onLost);
        _hub.Closed += ex =>
        {
            _closed.TrySetResult(ex);
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _hub.State == HubConnectionState.Connected;

    public Task<Exception?> Closed => _closed.Task;

    public Task StartAsync(CancellationToken cancellationToken) => _hub.StartAsync(cancellationToken);

    public Task<TResult> InvokeAsync<TResult>(string method, object?[] args, CancellationToken cancellationToken)
        => _hub.InvokeCoreAsync<TResult>(method, args, cancellationToken);

    public Task InvokeAsync(string method, object?[] args, CancellationToken cancellationToken)
        => _hub.InvokeCoreAsync(method, args, cancellationToken);

    public ValueTask DisposeAsync() => _hub.DisposeAsync();
}
