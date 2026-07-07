using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey listen</c> — receives delivered webhooks on this machine over an outbound, authenticated SignalR
/// session (no inbound port exposed) and replays each faithfully to a local endpoint. Stripe-<c>listen</c> style.
/// </summary>
internal static class ListenCommand
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "tee", "help", "h", "yes", "y" };
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(8);

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h"))
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        string? forwardTo = map.Get("forward-to") ?? map.Get("local-baseurl");
        if (string.IsNullOrWhiteSpace(forwardTo) || !Uri.TryCreate(forwardTo, UriKind.Absolute, out _))
        {
            Console.Error.WriteLine("listen requires --forward-to <absolute url>, e.g. --forward-to http://localhost:5094");
            return ExitCodes.Usage;
        }

        ResolvedConfig config = CliHost.Resolve(map);
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            Console.Error.WriteLine("An API key is required (--api-key, QUEUEY_API_KEY, or queuey.json).");
            return ExitCodes.Configuration;
        }

        if (!TryResolveScope(map, config, out string scopeKind, out string publicId, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCodes.Usage;
        }

        string mode = map.Has("tee") ? "tee" : "redirect";
        bool redirect = mode == "redirect";

        // Redirect suppresses the real endpoint for the whole scope — loud on any scope, and a required
        // confirmation for a tenant (which covers every queue under it). There is no server-side "is this
        // prod" signal to check, so this is the safety net.
        if (redirect)
        {
            Console.Error.WriteLine("⚠  redirect: the real endpoint will NOT fire — all matching deliveries come to you only.");
            Console.Error.WriteLine("   Don't run this against production traffic. Use --tee to also deliver for real.");
        }
        if (redirect && scopeKind == "tenant" && !map.Has("yes") && !map.Has("y"))
        {
            Console.Error.Write($"Divert ALL of tenant {publicId}'s deliveries to you (redirect)? [y/N] ");
            string? answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Aborted.");
                return ExitCodes.Usage;
            }
        }

        string hubUrl = config.ResolvedApiBase().ToString().TrimEnd('/') + "/hubs/listen";

        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.Headers["X-Api-Key"] = config.ApiKey!)
            .WithAutomaticReconnect()
            .Build();

        using var http = new HttpClient();
        long received = 0;

        connection.On<ListenEnvelope>("event", async env =>
        {
            Interlocked.Increment(ref received);
            string label = env.EventType ?? env.EventId ?? "event";
            int status;
            long ms;
            string? error = null;
            try
            {
                (status, ms) = await ListenForwarder.ForwardAsync(http, env, forwardTo!, CancellationToken.None);
                Console.WriteLine($"  {env.Method,-6} {env.PathAndQuery}  →  {status} ({ms}ms)  [{label}]");
            }
            catch (Exception ex)
            {
                // Your local receiver is unreachable — report it as a 502 so a redirect delivery records the
                // failure honestly (rather than the backend timing out waiting for an ack).
                status = 502;
                ms = 0;
                error = ex.Message;
                Console.Error.WriteLine($"  {env.Method,-6} {env.PathAndQuery}  →  forward failed: {ex.Message}  [{label}]");
            }

            // Redirect: return the local response so the delivery records the real status code. Best-effort —
            // if this never lands, the backend falls back to a timeout outcome. Tee needs no ack (the real
            // endpoint drives the record).
            if (string.Equals(env.Mode, "Redirect", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(env.CorrelationId))
            {
                try { await connection.InvokeAsync("Ack", env.CorrelationId, status, ms, error); }
                catch { /* connection dropped — the delivery's ack-timeout covers it */ }
            }
        });

        connection.Reconnecting += _ => { Console.Error.WriteLine("… connection lost, reconnecting"); return Task.CompletedTask; };
        connection.Reconnected += async _ =>
        {
            // Automatic reconnect gets a NEW ConnectionId. The server cleared our scope on the drop
            // (OnDisconnectedAsync) and does NOT re-hydrate it, so we must re-invoke Listen to re-join the
            // group + re-mark the session active. Without this the CLI stays "connected" but silently
            // receives nothing (and, for a redirect session, the queue just holds — awaiting_local_listener).
            try
            {
                ListenAck ack = await connection.InvokeAsync<ListenAck>("Listen", scopeKind, publicId, mode);
                Console.Error.WriteLine($"… reconnected — re-listening on {ack.ScopeKey}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"… reconnected, but could not re-establish the listen scope: {ex.Message}");
            }
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await connection.StartAsync(cts.Token);
            ListenAck ack = await connection.InvokeAsync<ListenAck>("Listen", scopeKind, publicId, mode, cts.Token);
            Console.WriteLine($"Listening on {ack.ScopeKey} ({ack.Mode}) → forwarding to {forwardTo}");
            Console.WriteLine("Press Ctrl-C to stop.");
            Console.WriteLine();

            await HeartbeatLoopAsync(connection, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C — fall through to graceful stop.
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Could not connect to {hubUrl}: {ex.Message}");
            return ExitCodes.RuntimeError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Listen session ended: {ex.Message}");
            return ExitCodes.RuntimeError;
        }
        finally
        {
            await StopQuietlyAsync(connection);
        }

        Console.WriteLine();
        Console.WriteLine($"Stopped. Forwarded {Interlocked.Read(ref received)} event(s).");
        return ExitCodes.Success;
    }

    private static async Task HeartbeatLoopAsync(HubConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);
                await connection.InvokeAsync("Heartbeat", ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient (reconnecting) — keep the loop alive; the TTL refreshes on the next beat.
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
            // Best-effort; the session also expires server-side once heartbeats stop.
        }
        await connection.DisposeAsync();
    }

    // Scope precedence: explicit --queue, then explicit --tenant, then the configured tenant.
    private static bool TryResolveScope(ArgMap map, ResolvedConfig config, out string scopeKind, out string publicId, out string? error)
    {
        string? queue = map.Get("queue");
        string? tenant = map.Get("tenant");

        if (!string.IsNullOrWhiteSpace(queue))
        {
            scopeKind = "queue";
            publicId = queue!;
        }
        else if (!string.IsNullOrWhiteSpace(tenant))
        {
            scopeKind = "tenant";
            publicId = tenant!;
        }
        else if (!string.IsNullOrWhiteSpace(config.TenantPublicId))
        {
            scopeKind = "tenant";
            publicId = config.TenantPublicId!;
        }
        else
        {
            scopeKind = publicId = string.Empty;
            error = "listen requires a scope: --queue <que_…>, --tenant <ten_…>, or a configured tenant.";
            return false;
        }

        error = null;
        return true;
    }
}
