using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
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
/// stops, or takes the queue over with <c>--take-over</c>, and the session it took over from stops. A queue under a
/// workspace another session listens on is that session's too.
/// </summary>
internal static class ListenCommand
{
    // --forward-exact og --exact er brytere. Før 2026-09-24 sto de ikke her, så en url rett etter en
    // av dem ble lest som verdien dens og forsvant. --yes tas fortsatt imot, så skript som sendte den, virker; lytting
    // spør ikke om noe lenger.
    internal static readonly CommandOptions Options = new(
        "listen",
        flags: new[] { "json", "take-over", "forward-exact", "exact", "yes", "y" },
        values: new[] { "forward-to", "local-baseurl", "queue", "profile" },
        hints: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Fjernet 2026-10-06 (F2.5): tee ble aldri bygget i Queuey, og lovet en levering til endepunktet som ikke skjedde.
            ["tee"] = "--tee is gone: listening never delivered to the real endpoint as well. A queue set to forward to a "
                      + "local listener (Local forward) delivers to the listener only, and your local response is the outcome.",
        });

    /// <summary>
    /// The connection to listen with: as before profiles without one; with one (F2.7), the profile's connection and the
    /// workspace the deployment file's values for it name, which have to agree, so the listener hears the workspace apply
    /// wrote to.
    /// </summary>
    internal static ResolvedConfig Connection(ArgMap map)
    {
        string? profile = CliHost.Profile(map);
        if (profile is null)
            return CliHost.Resolve(map);

        (string? fileTenant, string filePath) = DeploymentTenant.FromDeploymentOption(map, profile);
        return CliHost.ResolveForDeployment(map, fileTenant, filePath);
    }

    /// <summary>The exit code after SIGTERM, as a shell reports a process the signal ended: 128 + 15.</summary>
    internal const int TerminatedExitCode = 143;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(8);

    // Fire forsøk innen 10 s, fristen Queuey holder kravet til en sesjon som mistet tilkoblingen (re-review av #50): med
    // standardplanen (0, 2, 10, 30 s) nådde bare de to første fram i tide. Etter det prøver den videre i halvannet minutt.
    internal static readonly TimeSpan[] ReconnectDelays =
    {
        TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
    };
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string[] args)
    {
        // Med --json er hver linje på stdout én hendelse, også en feil før økten (review av queuey-client #50, K2): et
        // agentprogram som leser strømmen, får én refused-linje i stedet for et objekt over flere linjer.
        bool json = CliErrors.WantsJson(args);
        var output = new ListenOutput(json, new LineWriter(LineWriter.OpenStdout()), Console.Error);
        try
        {
            return await RunAsync(args, json, output);
        }
        finally
        {
            await output.CompleteAsync();
        }
    }

    private static async Task<int> RunAsync(string[] args, bool json, ListenOutput output)
    {
        int Refuse(string code, string message, string? action, int exitCode)
        {
            if (!json)
                return CliErrors.Write(json: false, code, message, action, status: null, exitCode);
            output.Refused(code, message, action, heldSinceUtc: null);
            return exitCode;
        }

        if (!Options.TryParse(args, out ArgMap map, out int failure, (code, message, action, _) => Refuse(code, message, action, ExitCodes.Usage)))
            return failure;
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
            return Refuse("missing_argument", "listen requires --forward-to <origin>, e.g. --forward-to http://localhost:5000", null, ExitCodes.Usage);
        }

        // By default the path of the queue's endpoint is appended to --forward-to (path fidelity — the webhook
        // arrives at the route it would in production). --forward-exact posts to --forward-to VERBATIM instead, for
        // bridging to a fixed local endpoint (e.g. a local ingress route /events/{tenant}/{queue}).
        bool preservePath = !(map.Has("forward-exact") || map.Has("exact"));

        ResolvedConfig config;
        ListenTarget target;
        try
        {
            // Konfigurasjonen leses inne i try (re-review av #50, K2): en --tenant som ikke er ten_…, en ugyldig --api-base og
            // en queuey.json som ikke kan leses, ga før et JSON-objekt over flere linjer fra CliEntry.
            config = Connection(map);
            if (string.IsNullOrWhiteSpace(config.ApiKey) && config.Login is null)
                return Refuse("config_error", "An API key or a login is required: run `queuey login`, or set --api-key, QUEUEY_API_KEY, or apiKey in queuey.json.",
                    null, ExitCodes.Configuration);

            target = await ResolveTargetAsync(map, config);
        }
        // Uten --json skriver CliEntry feilen, som for hver annen kommando, med de samme kodene som her.
        catch (CliUsageException ex) when (json)
        {
            return Refuse(ex.Code, ex.Message, ex.Action, ExitCodes.Usage);
        }
        catch (QueueyConfigurationException ex) when (json)
        {
            return Refuse("config_error", ex.Message, ex.SuggestedAction, ExitCodes.Configuration);
        }
        catch (QueueyException ex) when (json)
        {
            return Refuse(ex.ErrorCode ?? "queuey_error", ex.Message, ex.SuggestedAction, ExitCodes.RuntimeError);
        }
        catch (HttpRequestException ex) when (json)
        {
            return Refuse("unreachable", $"Could not reach Queuey: {ex.Message}", "Check --api-base (QUEUEY_API_BASE) and the network.", ExitCodes.RuntimeError);
        }
        catch (TaskCanceledException) when (json)
        {
            return Refuse("timeout", "The request timed out.", null, ExitCodes.RuntimeError);
        }
        catch (CliFileException ex) when (json)
        {
            return Refuse(ex.Code, ex.Message, ex.Action, ExitCodes.Configuration);
        }
        catch (Exception ex) when (json)
        {
            return Refuse("internal_error", $"{ex.GetType().Name}: {ex.Message}",
                "This is a bug in the queuey CLI. Run the command again without --json for the stack trace, and report it.",
                ExitCodes.RuntimeError);
        }

        if (preservePath && forwardUri.AbsolutePath.Trim('/').Length > 0)
        {
            output.Note($"Note: --forward-to has a path ({forwardUri.AbsolutePath}), and the path of the queue's endpoint is added after it. "
                        + $"Give the origin alone ({forwardUri.GetLeftPart(UriPartial.Authority)}) to receive at the endpoint's own path, "
                        + "or --forward-exact to post to --forward-to as given.");
        }

        try
        {
            return await ListenAsync(config, target, forwardTo!, preservePath, map.Has("take-over"), output);
        }
        catch (Exception ex) when (json)
        {
            // Også en feil i CLI-en selv er én linje med --json (re-review av #50): ellers skrev CliEntry et objekt over flere.
            return Refuse("internal_error", $"{ex.GetType().Name}: {ex.Message}",
                "This is a bug in the queuey CLI. Run the command again without --json for the stack trace, and report it.",
                ExitCodes.RuntimeError);
        }
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
                // En nøkkel som er satt, vinner; ellers innloggingen, som Bearer. Tokenet hentes ved hver tilkobling, også etter et
                // brudd, så en økt som varer lenger enn tokenet, fornyer det.
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                    options.Headers["X-Api-Key"] = config.ApiKey!;
                else if (config.Login is { } login)
                    options.AccessTokenProvider = async () => await login.AccessTokenAsync(CancellationToken.None);
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .WithAutomaticReconnect(ReconnectDelays)
            .Build();

        // The session's own id, kept across reconnects, so a session that reconnects keeps its queue.
        string sessionId = "cli_" + Guid.NewGuid().ToString("N");
        bool ownerAware = true;
        long forwarded = 0;
        var ended = new TaskCompletionSource<ListenEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient { Timeout = ListenForwarder.LocalTimeout };

        connection.On<ListenEnvelope>("event", async env =>
        {
            LocalForwardResult result = await ListenForwarder.ForwardAsync(http, env, forwardTo, CancellationToken.None, preservePath);
            Interlocked.Increment(ref forwarded);

            // The local response is the delivery's outcome, so it goes back before anything is printed (review of
            // queuey-client #50, K3): output that blocks or fails must not cost the answer.
            if (string.Equals(env.Mode, "Redirect", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(env.CorrelationId))
                await AckAsync(connection, env.CorrelationId, result);

            output.Delivery(env, result);
        });

        connection.On<ListenLost>("lost", lost => output.Lost(lost));

        // Ingen leser lenger (pipen er lukket, eller den leser ikke): økten stopper og frigjør køen (re-review av #50, K3b).
        _ = output.Gone.ContinueWith(
            gone => ended.TrySetResult(new ListenEnd("output_closed", gone.Result)),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

        connection.Reconnecting += _ =>
        {
            output.Reconnecting();
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            // A reconnect gets a new connection id. The same session claims its queue again on it and keeps it. A session
            // that took the queue over meanwhile has it now; any other refusal is a refusal (review of #50, K6).
            try
            {
                ListenReply reply = await ClaimAsync(connection, target, sessionId, takeOver: false, ownerAware, CancellationToken.None);
                if (reply.Listening)
                    output.Reconnected(reply.ScopeKey);
                else if (reply.Code == "listener_already_connected")
                    ended.TrySetResult(new ListenEnd("superseded", reply.Message ?? "Another queuey listen session has the queue now."));
                else
                    ended.TrySetResult(new ListenEnd("refused", reply.Message ?? "Queuey refused the listen session.", reply.Code ?? "refused", HeldSinceUtc: reply.HeldSinceUtc));
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
            ended.TrySetResult(new ListenEnd("stopped"));
        };
        Console.CancelKeyPress += onCancel;
        // SIGTERM fra en agent eller en prosessleder avslutter som Ctrl-C, med siste linje og exit 143 (review av #50, K5).
        using PosixSignalRegistration? terminate = OnTerminate(() => ended.TrySetResult(new ListenEnd("terminated")));

        using var heartbeats = new CancellationTokenSource();
        ListenEnd end;
        try
        {
            end = await StartAsync(connection, target, sessionId, takeOver, hubUrl, forwardTo, output, isOwnerAware => ownerAware = isOwnerAware);
            if (end.Reason == "listening")
            {
                _ = HeartbeatLoopAsync(connection, ended, heartbeats.Token);
                end = await ended.Task;
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            heartbeats.Cancel();
        }

        // Økten stoppes før siste linje (review av #50, K5): den som leser linjen, kan stole på at køen er fri.
        await StopQuietlyAsync(connection);
        return Finish(end, output, Interlocked.Read(ref forwarded));
    }

    /// <summary>Connects and claims the scope. <c>listening</c> once it does, else why not.</summary>
    private static async Task<ListenEnd> StartAsync(
        HubConnection connection, ListenTarget target, string sessionId, bool takeOver, string hubUrl, string forwardTo,
        ListenOutput output, Action<bool> ownerAware)
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
            catch (HubException ex) when (IsUnknownMethod(ex, "ListenAsOwner"))
            {
                // A Queuey from before single-owner listening: it has only the old method, and no take-over.
                ownerAware(false);
                output.Note("Note: this Queuey does not know single-owner listening yet" + (takeOver ? ", so --take-over has no effect." : "."));
                reply = await ClaimAsync(connection, target, sessionId, takeOver, ownerAware: false, starting.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            return new ListenEnd("refused", $"Could not connect to {hubUrl}: {ex.Message}", "unreachable",
                Action: "Check --api-base (QUEUEY_API_BASE) and the network.");
        }
        catch (HubException ex)
        {
            return new ListenEnd("refused", ServerMessage(ex), "refused");
        }

        if (!reply.Listening)
            return new ListenEnd("refused", reply.Message ?? "Queuey refused the listen session.", reply.Code ?? "refused", HeldSinceUtc: reply.HeldSinceUtc);

        output.Listening(target, reply.ScopeKey ?? string.Empty, forwardTo, reply.TookOver);
        return new ListenEnd("listening");
    }

    /// <summary>Runs <paramref name="terminated"/> on SIGTERM instead of ending the process; null where the platform has no SIGTERM.</summary>
    private static PosixSignalRegistration? OnTerminate(Action terminated)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                terminated();
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the line that ends the stream, and says what the process exits with.</summary>
    internal static int Finish(ListenEnd end, ListenOutput output, long forwarded)
    {
        try
        {
            switch (end.Reason)
            {
                case "refused":
                    string code = end.Code ?? "refused";
                    output.Refused(code, end.Message ?? "Queuey refused the listen session.", end.Action ?? ActionFor(code), end.HeldSinceUtc);
                    return code == "unauthorized" ? ExitCodes.Configuration : ExitCodes.RuntimeError;
                case "superseded":
                    output.Superseded(end.Message ?? "Another queuey listen session took the queue over.", forwarded);
                    return ExitCodes.RuntimeError;
                case "connection_lost":
                    output.Closed("connection_lost", end.Message, forwarded);
                    return ExitCodes.RuntimeError;
                case "terminated":
                    output.Closed("terminated", null, forwarded);
                    return TerminatedExitCode;
                case "output_closed":
                    // Nobody reads the output any more; the session is stopped and the exit code says it ended badly.
                    return ExitCodes.RuntimeError;
                default:
                    output.Closed("stopped", null, forwarded);
                    return ExitCodes.Success;
            }
        }
        catch (IOException)
        {
            return ExitCodes.RuntimeError;
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
    /// Sends the local response back, and tries again when the call fails (review of #50, N1): Queuey waits 20 s, and an
    /// answer that is lost while the connection blinks would otherwise read as no answer.
    /// </summary>
    private static async Task AckAsync(HubConnection connection, string correlationId, LocalForwardResult result)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await connection.InvokeAsync("Ack", correlationId, result.Status, result.DurationMs, result.Error);
                return;
            }
            catch when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
            catch
            {
                // Queuey reads what the hub kept, or holds the event when the session has gone.
            }
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

    /// <summary>Frees the queue and closes the connection, each within a few seconds whatever the network does.</summary>
    private static async Task StopQuietlyAsync(HubConnection connection)
    {
        try
        {
            if (connection.State == HubConnectionState.Connected)
            {
                using var stopping = new CancellationTokenSource(StopTimeout);
                await connection.InvokeAsync("Stop", stopping.Token);
            }
        }
        catch
        {
            // Best-effort; the claim also runs out once heartbeats stop.
        }

        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(StopTimeout);
        }
        catch
        {
            // The process ends either way.
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> says the server has no hub method named <paramref name="method"/>. ASP.NET Core
    /// answers <c>Failed to invoke 'X' due to an error on the server. HubException: Method does not exist.</c>; older
    /// servers wrote <c>Unknown hub method 'X'</c>.
    /// </summary>
    // Review av queuey-client #50, K1: bare «Unknown hub method» ble sjekket, så reserven til den gamle Listen slo aldri til.
    internal static bool IsUnknownMethod(HubException ex, string method)
        => ex.Message.Contains($"'{method}'", StringComparison.Ordinal)
           && (ex.Message.Contains("Method does not exist", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("Unknown hub method", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// How a session ended: <c>listening</c> (it has not), <c>refused</c>, <c>superseded</c>, <c>connection_lost</c>,
    /// <c>stopped</c>, <c>terminated</c> or <c>output_closed</c>.
    /// </summary>
    internal sealed record ListenEnd(
        string Reason, string? Message = null, string? Code = null, string? Action = null, DateTimeOffset? HeldSinceUtc = null);
}

/// <summary>What a session listens on: <c>queue</c> or <c>tenant</c>, the id, and the queue's name when it was given by name.</summary>
internal sealed record ListenTarget(string Kind, string Id, string? Name = null);
