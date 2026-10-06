using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey listen</c> — receives the deliveries of a queue that forwards to a local listener (Local forward) over an
/// outbound, authenticated SignalR session (no inbound port exposed) and replays each faithfully to a local endpoint.
/// Stripe-<c>listen</c> style. One session listens on a queue at a time: the first one. Another is refused until it
/// stops, or takes the queue over with <c>--take-over</c>, and the session it took over from stops.
/// </summary>
internal static class ListenCommand
{
    // --forward-exact og --exact er brytere. Før 2026-09-24 sto de ikke her, så en url rett etter en
    // av dem ble lest som verdien dens og forsvant. --yes tas fortsatt imot, så skript som sendte den, virker; lytting
    // spør ikke om noe lenger.
    internal static readonly CommandOptions Options = new(
        "listen",
        flags: new[] { "json", "take-over", "forward-exact", "exact", "yes", "y" },
        values: new[] { "forward-to", "local-baseurl", "queue" },
        hints: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Fjernet 2026-10-06 (F2.5): tee ble aldri bygget i Queuey, og lovet en levering til endepunktet som ikke skjedde.
            ["tee"] = "--tee is gone: listening never delivered to the real endpoint as well. A queue set to forward to a "
                      + "local listener (Local forward) delivers to the listener only, and your local response is the outcome.",
        });

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(8);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h"))
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        string? forwardTo = map.Get("forward-to") ?? map.Get("local-baseurl");
        if (string.IsNullOrWhiteSpace(forwardTo)
            || !Uri.TryCreate(forwardTo, UriKind.Absolute, out Uri? forwardUri)
            || (forwardUri.Scheme != Uri.UriSchemeHttp && forwardUri.Scheme != Uri.UriSchemeHttps))
        {
            return CliErrors.Usage(map, "missing_argument", "listen requires --forward-to <origin>, e.g. --forward-to http://localhost:5000");
        }

        // By default the path of the queue's endpoint is appended to --forward-to (path fidelity — the webhook
        // arrives at the route it would in production). --forward-exact posts to --forward-to VERBATIM instead, for
        // bridging to a fixed local endpoint (e.g. a local ingress route /events/{tenant}/{queue}).
        bool preservePath = !(map.Has("forward-exact") || map.Has("exact"));

        ResolvedConfig config = CliHost.Resolve(map);
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return CliErrors.Configuration(map, "config_error", "An API key is required (--api-key, QUEUEY_API_KEY, or queuey.json).");

        ListenTarget target = await ResolveTargetAsync(map, config);

        var output = new ListenOutput(map.Has("json"), Console.Out, Console.Error);
        if (preservePath && forwardUri.AbsolutePath.Trim('/').Length > 0)
        {
            output.Note($"Note: --forward-to has a path ({forwardUri.AbsolutePath}), and the path of the queue's endpoint is added after it. "
                        + $"Give the origin alone ({forwardUri.GetLeftPart(UriPartial.Authority)}) to receive at the endpoint's own path, "
                        + "or --forward-exact to post to --forward-to as given.");
        }

        return await ListenAsync(config, target, forwardTo!, preservePath, map.Has("take-over"), output);
    }

    /// <summary>
    /// What the session listens on: <c>--queue</c> by id (<c>que_…</c>) or by name in its workspace, else
    /// <c>--tenant</c>, else the configured workspace.
    /// </summary>
    internal static async Task<ListenTarget> ResolveTargetAsync(ArgMap map, ResolvedConfig config)
    {
        string? queue = map.Get("queue");
        if (!string.IsNullOrWhiteSpace(queue))
        {
            if (queue.StartsWith("que_", StringComparison.Ordinal))
                return new ListenTarget("queue", queue);

            // Et navn, som i deploy-fila og brukerhistoriene (`--queue orders`): hubben kjenner bare id-er.
            if (string.IsNullOrWhiteSpace(config.TenantPublicId))
            {
                throw new CliUsageException("missing_argument",
                    $"A queue name needs its workspace to find the queue '{queue}'.",
                    "Add --tenant <ten_…> (or set QUEUEY_TENANT), or give the queue's id: --queue que_…");
            }

            using ServiceProvider provider = CliHost.BuildProvider(config);
            IReadOnlyList<QueueListItem> queues = await provider.GetRequiredService<IQueueyService>().Management
                .ListQueuesAsync(config.TenantPublicId!);
            string? id = queues.FirstOrDefault(q => string.Equals(q.DisplayName, queue, StringComparison.Ordinal))?.PublicId;
            if (id is null)
            {
                throw new QueueyNotFoundException($"No queue named '{queue}' in workspace {config.TenantPublicId}.", "queue_not_found")
                {
                    SuggestedAction = "Check the name, or give the queue's id: --queue que_…",
                };
            }

            return new ListenTarget("queue", id, Name: queue);
        }

        string? tenant = map.Get("tenant") ?? config.TenantPublicId;
        if (!string.IsNullOrWhiteSpace(tenant))
            return new ListenTarget("tenant", tenant);

        throw new CliUsageException("missing_argument",
            "listen requires a scope: --queue <name|que_…>, --tenant <ten_…>, or a configured workspace.", action: null);
    }

    private static async Task<int> ListenAsync(
        ResolvedConfig config, ListenTarget target, string forwardTo, bool preservePath, bool takeOver, ListenOutput output)
    {
        string hubUrl = config.ResolvedApiBase().ToString().TrimEnd('/') + "/hubs/listen";

        // WebSockets uten forhandling (Kenneth, 2026-10-06): med forhandling må forhandlingen og WebSocketen treffe samme
        // replika av API-et, og sticky sessions er ikke satt for api i ACA. Uten er det én forbindelse til én replika.
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.Headers["X-Api-Key"] = config.ApiKey!;
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .WithAutomaticReconnect()
            .Build();

        // The session's own id, kept across reconnects, so a session that reconnects keeps its queue.
        string sessionId = "cli_" + Guid.NewGuid().ToString("N");
        bool ownerAware = true;
        long forwarded = 0;
        var ended = new TaskCompletionSource<ListenEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient();

        connection.On<ListenEnvelope>("event", async env =>
        {
            LocalForwardResult result = await ListenForwarder.ForwardAsync(http, env, forwardTo, CancellationToken.None, preservePath);
            Interlocked.Increment(ref forwarded);
            output.Delivery(env, result);

            // The local response is the delivery's outcome. Best-effort: if it never lands, Queuey sees that the session
            // went away and holds the event, or records that nothing answered.
            if (string.Equals(env.Mode, "Redirect", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(env.CorrelationId))
            {
                try { await connection.InvokeAsync("Ack", env.CorrelationId, result.Status, result.DurationMs, result.Error); }
                catch { /* connection dropped */ }
            }
        });

        connection.Reconnecting += _ =>
        {
            output.Note("… connection lost, reconnecting");
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            // A reconnect gets a new connection id. The same session claims its queue again on it and keeps it; a session
            // that took the queue over meanwhile has it now.
            try
            {
                ListenReply reply = await ClaimAsync(connection, target, sessionId, takeOver: false, ownerAware, CancellationToken.None);
                if (reply.Listening)
                    output.Note($"… reconnected — listening on {reply.ScopeKey}");
                else
                    ended.TrySetResult(new ListenEnd("superseded", reply.Message ?? "Another queuey listen session has the queue now."));
            }
            catch (Exception ex)
            {
                ended.TrySetResult(new ListenEnd("connection_lost", $"Reconnected, but could not listen again: {ex.Message}"));
            }
        };
        // Fires when the automatic reconnect gives up, or the server closes the connection: it will not come back.
        connection.Closed += ex =>
        {
            ended.TrySetResult(new ListenEnd("connection_lost",
                $"The connection to Queuey was lost and did not come back{(ex is null ? "." : $": {ex.Message}")}"));
            return Task.CompletedTask;
        };

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            ended.TrySetResult(new ListenEnd("stopped", null));
        };
        Console.CancelKeyPress += onCancel;

        using var heartbeats = new CancellationTokenSource();
        try
        {
            ListenReply reply;
            try
            {
                using var starting = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await connection.StartAsync(starting.Token);
                try
                {
                    reply = await ClaimAsync(connection, target, sessionId, takeOver, ownerAware: true, starting.Token);
                }
                catch (HubException ex) when (ex.Message.Contains("Unknown hub method", StringComparison.OrdinalIgnoreCase))
                {
                    // A Queuey from before single-owner listening: it has only the old method, and no take-over.
                    ownerAware = false;
                    output.Note("Note: this Queuey does not know single-owner listening yet"
                                + (takeOver ? ", so --take-over has no effect." : "."));
                    reply = await ClaimAsync(connection, target, sessionId, takeOver, ownerAware: false, starting.Token);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
            {
                output.Refused("unreachable", $"Could not connect to {hubUrl}: {ex.Message}",
                    "Check --api-base (QUEUEY_API_BASE) and the network.", heldSinceUtc: null);
                return ExitCodes.RuntimeError;
            }
            catch (HubException ex)
            {
                output.Refused("refused", ServerMessage(ex), action: null, heldSinceUtc: null);
                return ExitCodes.RuntimeError;
            }

            if (!reply.Listening)
            {
                string code = reply.Code ?? "refused";
                output.Refused(code, reply.Message ?? "Queuey refused the listen session.", ActionFor(code), reply.HeldSinceUtc);
                return code == "unauthorized" ? ExitCodes.Configuration : ExitCodes.RuntimeError;
            }

            output.Listening(target, reply.ScopeKey ?? string.Empty, forwardTo, reply.TookOver);
            _ = HeartbeatLoopAsync(connection, ended, heartbeats.Token);

            ListenEnd end = await ended.Task;
            switch (end.Reason)
            {
                case "superseded":
                    output.Superseded(end.Message!, Interlocked.Read(ref forwarded));
                    return ExitCodes.RuntimeError;
                case "connection_lost":
                    output.Closed("connection_lost", end.Message, Interlocked.Read(ref forwarded));
                    return ExitCodes.RuntimeError;
                default:
                    output.Closed("stopped", null, Interlocked.Read(ref forwarded));
                    return ExitCodes.Success;
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            heartbeats.Cancel();
            await StopQuietlyAsync(connection);
        }
    }

    /// <summary>Claims the scope as this session; against an older Queuey, with the method it has.</summary>
    private static async Task<ListenReply> ClaimAsync(
        HubConnection connection, ListenTarget target, string sessionId, bool takeOver, bool ownerAware, CancellationToken ct)
    {
        if (ownerAware)
            return await connection.InvokeAsync<ListenReply>("ListenAsOwner", target.Kind, target.Id, sessionId, takeOver, ct);

        try
        {
            ListenAck ack = await connection.InvokeAsync<ListenAck>("Listen", target.Kind, target.Id, "redirect", ct);
            return new ListenReply(Listening: true, ScopeKey: ack.ScopeKey);
        }
        catch (HubException ex)
        {
            return new ListenReply(Listening: false, Code: "refused", Message: ServerMessage(ex));
        }
    }

    /// <summary>
    /// Beats every few seconds, which keeps the session's claim on its queue. A beat that says this session no longer
    /// owns the queue means another one took it over.
    /// </summary>
    private static async Task HeartbeatLoopAsync(HubConnection connection, TaskCompletionSource<ListenEnd> ended, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);
                ListenBeat? beat = await connection.InvokeAsync<ListenBeat?>("Heartbeat", ct);
                if (beat is { Owner: false })
                {
                    ended.TrySetResult(new ListenEnd("superseded",
                        "Another queuey listen session took the queue over, and forwards go to it now."));
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Reconnecting: the claim outlives a missed beat or two, and Reconnected claims again.
            }
        }
    }

    private static async Task StopQuietlyAsync(HubConnection connection)
    {
        try
        {
            if (connection.State == HubConnectionState.Connected)
                await connection.InvokeAsync("Stop");
        }
        catch
        {
            // Best-effort; the claim also runs out once heartbeats stop.
        }
        await connection.DisposeAsync();
    }

    /// <summary>What to do about a refusal, by its code.</summary>
    internal static string? ActionFor(string code) => code switch
    {
        "listener_already_connected" => "Stop the other session, or run queuey listen again with --take-over to take the queue over.",
        "unauthorized" => "Check the API key: --api-key, QUEUEY_API_KEY or queuey.json.",
        "forbidden" => "The key needs queue.listen on that queue or workspace (a Build, Full access or ProducerAdmin key).",
        "scope_not_found" => "Check the id, or give the queue's name with its workspace: --queue <name> --tenant <ten_…>.",
        _ => null,
    };

    // "An unexpected error occurred invoking 'Listen' on the server. HubException: Forbidden: …" → "Forbidden: …".
    private static string ServerMessage(HubException ex)
    {
        const string marker = "HubException: ";
        int at = ex.Message.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? ex.Message : ex.Message[(at + marker.Length)..];
    }

    private sealed record ListenEnd(string Reason, string? Message);
}

/// <summary>What a session listens on: <c>queue</c> or <c>tenant</c>, the id, and the queue's name when it was given by name.</summary>
internal sealed record ListenTarget(string Kind, string Id, string? Name = null);
