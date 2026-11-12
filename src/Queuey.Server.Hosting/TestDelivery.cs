using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Queuey.Client;

namespace Queuey.Server.Hosting;

/// <summary>What the test delivery did: the event, where it went, and how long from the ingress to the receiver.</summary>
internal sealed record TestDeliveryResult(string EventId, string Workspace, string Queue, TimeSpan Took);

/// <summary>
/// The test delivery <c>queuey server start</c> ends with: one event in through the installation's ingress, the way a
/// producer sends it, and out to a receiver this CLI runs on this machine, the way a developer's own receiver gets it.
/// It signs in as the developer (<see cref="LocalDeveloper"/>), and makes what it needs the first time: the account, a
/// workspace when there is none, and the queue <c>server-test</c>.
/// </summary>
internal static class TestDelivery
{
    public const string QueueName = "server-test";
    private const int DeliverMode = 3;      // QueueMode.Deliver
    private const int Delivered = 2;        // EventStatus.Delivered

    /// <summary>How long to wait for the event at the receiver. A seam: the tests do not wait a minute.</summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public static async Task<TestDeliveryResult> RunAsync(LocalPorts ports, DeveloperRecord developer, HttpClient http, CancellationToken ct)
    {
        (LocalApi api, string tenant) = await LocalWorkspace.OpenAsync(ports, developer, http, ct);
        string queue = await QueueAsync(api, tenant, ct);

        using var receiver = Receiver.Start();
        string nonce = ServerFolder.RandomHex(12);
        await api.SendAsync(HttpMethod.Patch, $"/queues/{queue}/delivery", new { url = $"http://host.docker.internal:{receiver.Port}/{nonce}" }, ct);

        var clock = Stopwatch.StartNew();
        using var sent = await http.PostAsync($"{ports.IngressUrl}/events/{tenant}/{QueueName}",
            LocalApi.Json(new { test = "queuey server start", nonce }), ct);
        string sentBody = await sent.Content.ReadAsStringAsync(ct);
        if (sent.StatusCode != HttpStatusCode.Accepted)
            throw new QueueyException($"The ingress did not take the test event ({(int)sent.StatusCode}): {LocalDeveloper.Short(sentBody)}");
        string eventId = JsonDocument.Parse(sentBody).RootElement.GetProperty("eventId").GetString()!;

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(Timeout);
        try
        {
            await receiver.WaitForAsync("/" + nonce, nonce, wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            string status = await EventStatusAsync(api, queue, eventId, ct);
            throw new QueueyException(
                $"The test event {eventId} was taken in, but did not reach the receiver on this machine within {Timeout.TotalSeconds:0} s ({status}).")
            {
                SuggestedAction = $"See why with `queuey server logs server`, or open the queue {QueueName} in the console.",
            };
        }
        TimeSpan took = clock.Elapsed;

        // Mottakeren har svart 200; statusen settes like etter. Testen venter litt på den, så «levert» betyr det Queuey sier.
        for (int i = 0; i < 20 && await EventStatusAsync(api, queue, eventId, ct) != "Delivered"; i++)
            await Task.Delay(250, ct);
        return new TestDeliveryResult(eventId, tenant, queue, took);
    }

    // Køen server-test i arbeidsområdet, laget og satt til å levere første gang.
    private static async Task<string> QueueAsync(LocalApi api, string tenant, CancellationToken ct)
    {
        if (await LocalWorkspace.QueueAsync(api, tenant, QueueName, ct) is { } existing)
            return existing;

        using var queue = await api.SendAsync(HttpMethod.Post, "/queues", new { tenantPublicId = tenant, displayName = QueueName }, ct);
        string queueId = queue.RootElement.GetProperty("publicId").GetString()!;
        (await api.SendAsync(HttpMethod.Patch, $"/queues/{queueId}", new { mode = DeliverMode }, ct)).Dispose();
        return queueId;
    }

    private static async Task<string> EventStatusAsync(LocalApi api, string queue, string eventId, CancellationToken ct)
    {
        try
        {
            using var found = await api.GetJsonAsync($"/events/{queue}/{eventId}", ct);
            return found.RootElement.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.Number
                ? status.GetInt32() switch { Delivered => "Delivered", int other => $"status {other}" }
                : "status unknown";
        }
        catch (QueueyException)
        {
            return "status unknown";
        }
    }

    /// <summary>
    /// The receiver: a minimal HTTP/1.1 server on this machine's loopback interface, on a free port, that answers 200 to a
    /// request to its one path with the nonce in the body, and 404 to anything else. Docker Desktop brings
    /// <c>host.docker.internal</c> here. Not <see cref="HttpListener"/>: it matches the Host header, which is
    /// <c>host.docker.internal</c> and not an address it listens on.
    /// </summary>
    internal sealed class Receiver : IDisposable
    {
        private readonly TcpListener _listener;

        // Testeventet er noen titalls byte; mer enn dette leses ikke, så en annen prosess kan ikke fylle minnet.
        private const int MaxBody = 1024 * 1024;

        private Receiver(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public static Receiver Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new Receiver(listener);
        }

        /// <summary>Waits for a request to <paramref name="path"/> whose body holds <paramref name="nonce"/>.</summary>
        public async Task WaitForAsync(string path, string nonce, CancellationToken ct)
        {
            while (true)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                using NetworkStream stream = client.GetStream();
                // Én forbindelse om gangen, så en som ikke sender noe, får ti sekunder før neste slipper til.
                using var perConnection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perConnection.CancelAfter(TimeSpan.FromSeconds(10));
                string? target;
                string body;
                try
                {
                    (target, body) = await ReadRequestAsync(stream, perConnection.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue;
                }
                bool ours = target == path && body.Contains(nonce, StringComparison.Ordinal);
                byte[] answer = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(ours ? "200 OK" : "404 Not Found")}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(answer, ct);
                if (ours)
                    return;
            }
        }

        // Forespørselslinjen, headerne til den tomme linjen, og en kropp med Content-Length eller chunked.
        private static async Task<(string? Target, string Body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new List<byte>();
            var chunk = new byte[8192];
            int headerEnd;
            while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0)
            {
                int read = await stream.ReadAsync(chunk, ct);
                if (read == 0 || buffer.Count > 64 * 1024)
                    return (null, "");
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            string[] head = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray()).Split("\r\n");
            string? target = head[0].Split(' ') is { Length: >= 2 } parts ? parts[1] : null;
            var headers = head.Skip(1).Select(h => h.Split(':', 2)).Where(h => h.Length == 2)
                .ToDictionary(h => h[0].Trim().ToLowerInvariant(), h => h[1].Trim());
            var body = buffer.GetRange(headerEnd + 4, buffer.Count - headerEnd - 4);

            async Task<bool> More()
            {
                if (body.Count > MaxBody)
                    return false;
                int read = await stream.ReadAsync(chunk, ct);
                if (read > 0) body.AddRange(chunk.AsSpan(0, read).ToArray());
                return read > 0;
            }

            if (headers.TryGetValue("content-length", out string? length) && int.TryParse(length, out int expected))
            {
                if (expected > MaxBody)
                    return (target, "");
                while (body.Count < expected && await More()) { }
                return (target, Encoding.UTF8.GetString(body.Take(expected).ToArray()));
            }
            if (headers.TryGetValue("transfer-encoding", out string? encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                // Les til den avsluttende 0-chunken; innholdet holder til å finne nonce-en.
                while (IndexOf(body, "\r\n0\r\n\r\n"u8.ToArray()) < 0 && !StartsWith(body, "0\r\n\r\n"u8.ToArray()) && await More()) { }
                return (target, Encoding.UTF8.GetString(body.ToArray()));
            }
            return (target, Encoding.UTF8.GetString(body.ToArray()));
        }

        private static int IndexOf(List<byte> data, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= data.Count; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        private static bool StartsWith(List<byte> data, byte[] prefix) => data.Count >= prefix.Length && IndexOf(data.GetRange(0, prefix.Length), prefix) == 0;

        public void Dispose() => _listener.Stop();
    }
}
