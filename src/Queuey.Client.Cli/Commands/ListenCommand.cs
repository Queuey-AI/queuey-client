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
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "tee", "help", "h" };
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
            try
            {
                (int status, long ms) = await ListenForwarder.ForwardAsync(http, env, forwardTo!, CancellationToken.None);
                Console.WriteLine($"  {env.Method,-6} {env.PathAndQuery}  →  {status} ({ms}ms)  [{label}]");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {env.Method,-6} {env.PathAndQuery}  →  forward failed: {ex.Message}  [{label}]");
            }
        });

        connection.Reconnecting += _ => { Console.Error.WriteLine("… connection lost, reconnecting"); return Task.CompletedTask; };
        connection.Reconnected += _ => { Console.Error.WriteLine("… reconnected"); return Task.CompletedTask; };

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
