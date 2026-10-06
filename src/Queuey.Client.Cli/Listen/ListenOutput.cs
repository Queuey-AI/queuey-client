using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Queuey.Client.Cli;

/// <summary>
/// What <c>queuey listen</c> prints. With <c>--json</c>: one JSON object per line on stdout (NDJSON), each with
/// <c>schemaVersion</c> and a <c>type</c> — <c>listening</c>, then a <c>delivery</c> per forward, a <c>lost</c> when a
/// workspace session loses one of its queues, and one line that ends the stream: <c>refused</c> (it never listened, an
/// error before the session included), <c>superseded</c> (another session took the queue over) or <c>closed</c>
/// (stopped, terminated, or the connection was lost for good). Addresses are redacted as Queuey redacts them for agents.
/// Notes for a person go to stderr either way. Without <c>--json</c>: lines for a person.
/// </summary>
// Én linje per hendelse, så en agent kan lese strømmen mens lytteren går (F2.5, 2026-10-06). Versjonert fra første
// utgave, som apply --dry-run, plan og verify; JsonOut skriver over flere linjer og kan ikke brukes her.
internal sealed class ListenOutput
{
    /// <summary>The version of each line's shape. A reader checks it before it reads the rest.</summary>
    internal const int JsonSchemaVersion = 1;

    private static readonly JsonSerializerOptions Line = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly bool _json;
    private readonly Action<string> _line;
    private readonly LineWriter? _writer;
    private readonly TextWriter _err;
    private readonly object _gate = new();

    // Satt når siste linje er skrevet (review av #50): en handler som fortsatt holder på, skriver ikke en delivery etter den.
    private bool _ended;

    /// <summary>Writes each line to <paramref name="stdout"/> at once. For tests and for errors before a session.</summary>
    public ListenOutput(bool json, TextWriter stdout, TextWriter stderr)
    {
        _json = json;
        _line = line =>
        {
            stdout.WriteLine(line);
            stdout.Flush();
        };
        _err = stderr;
    }

    /// <summary>Queues each line on <paramref name="stdout"/>, so writing never waits on the reader.</summary>
    public ListenOutput(bool json, LineWriter stdout, TextWriter stderr)
    {
        _json = json;
        _writer = stdout;
        _line = stdout.WriteLine;
        _err = stderr;
    }

    /// <summary>Completes, with why, when nobody reads the output any more. Never completes for a direct writer.</summary>
    public Task<string> Gone => _writer?.Gone ?? new TaskCompletionSource<string>().Task;

    /// <summary>Writes what is queued, within a few seconds.</summary>
    public Task CompleteAsync() => _writer?.CompleteAsync(TimeSpan.FromSeconds(5)) ?? Task.CompletedTask;

    public void Listening(ListenTarget target, string scopeKey, string forwardTo, bool tookOver)
    {
        if (Ended)
            return;
        if (_json)
        {
            Write(new
            {
                schemaVersion = JsonSchemaVersion,
                type = "listening",
                scope = target.Kind,
                id = target.Id,
                name = target.Name,
                scopeKey,
                forwardTo,
                tookOver,
            });
            return;
        }

        lock (_gate)
        {
            if (tookOver)
                _line("Took the queue over from the session that listened on it.");
            string what = target.Name is null ? $"{target.Kind} {target.Id}" : $"{target.Kind} {target.Name} ({target.Id})";
            _line($"Listening on {what} → forwarding to {forwardTo}");
            _line("Only a queue set to forward to a local listener (Local forward) sends events here. Press Ctrl-C to stop.");
            _line(string.Empty);
        }
    }

    public void Delivery(ListenEnvelope env, LocalForwardResult result)
    {
        if (Ended)
            return;
        string path = UrlRedaction.EndpointPath(env.OriginalUrl, env.PathAndQuery);
        if (_json)
        {
            Write(new
            {
                schemaVersion = JsonSchemaVersion,
                type = "delivery",
                eventId = string.IsNullOrEmpty(env.EventId) ? null : env.EventId,
                eventType = env.EventType,
                queue = env.QueuePublicId,
                method = env.Method,
                path,
                localUrl = UrlRedaction.Redact(result.LocalUrl.ToString()),
                status = result.Status,
                durationMs = result.DurationMs,
                signatureHeaders = SignatureHeaders(env),
                error = result.Error,
            });
            return;
        }

        string label = string.Join(" ", new[] { env.EventType, env.EventId }.Where(s => !string.IsNullOrEmpty(s)));
        lock (_gate)
        {
            if (result.Error is null)
                _line($"  {env.Method,-6} {path}  →  {result.Status} ({result.DurationMs}ms)  [{label}]");
            else
                _err.WriteLine($"  {env.Method,-6} {path}  →  {result.Status}, {result.Error}  [{label}]");
        }
    }

    /// <summary>A workspace session lost one of its queues to a session that took it over; it keeps listening.</summary>
    public void Lost(ListenLost lost)
    {
        if (Ended)
            return;
        if (_json)
        {
            Write(new { schemaVersion = JsonSchemaVersion, type = "lost", queue = lost.QueuePublicId, message = lost.Message });
            return;
        }

        lock (_gate)
            _err.WriteLine($"Lost {lost.QueuePublicId}: {lost.Message}");
    }

    public void Refused(string code, string message, string? action, DateTimeOffset? heldSinceUtc)
    {
        if (!End())
            return;
        if (_json)
        {
            Write(new { schemaVersion = JsonSchemaVersion, type = "refused", code, message, action, heldSinceUtc });
            return;
        }

        lock (_gate)
        {
            _err.WriteLine($"Listen refused: {message}");
            if (!string.IsNullOrWhiteSpace(action))
                _err.WriteLine($"  → {action}");
        }
    }

    public void Superseded(string message, long forwarded)
    {
        if (!End())
            return;
        if (_json)
        {
            Write(new { schemaVersion = JsonSchemaVersion, type = "superseded", message, forwarded });
            return;
        }

        lock (_gate)
            _err.WriteLine($"Taken over: {message} Forwarded {forwarded} event(s).");
    }

    /// <param name="reason"><c>stopped</c> (Ctrl-C), <c>terminated</c> (SIGTERM) or <c>connection_lost</c> (it will not come back).</param>
    public void Closed(string reason, string? message, long forwarded)
    {
        if (!End())
            return;
        if (_json)
        {
            Write(new { schemaVersion = JsonSchemaVersion, type = "closed", reason, message, forwarded });
            return;
        }

        lock (_gate)
        {
            if (message is not null)
                _err.WriteLine(message);
            _line(string.Empty);
            _line($"Stopped. Forwarded {forwarded} event(s).");
        }
    }

    /// <summary>A note for a person, on stderr: never part of the JSON stream.</summary>
    public void Note(string text)
    {
        lock (_gate)
            _err.WriteLine(text);
    }

    /// <summary>
    /// The headers Queuey's signing set on the forward. A Queuey older than the field doesn't say, and then the
    /// forward's headers named like a signature stand in.
    /// </summary>
    internal static IReadOnlyList<string> SignatureHeaders(ListenEnvelope env)
        => env.SignatureHeaders
           ?? (env.Headers ?? new List<ListenHeader>())
               .Select(h => h.Name)
               .Where(name => name.Contains("signature", StringComparison.OrdinalIgnoreCase))
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToList();

    private bool Ended
    {
        get { lock (_gate) return _ended; }
    }

    /// <summary>Marks the stream as ended; false when it already was, so only one line ends it.</summary>
    private bool End()
    {
        lock (_gate)
        {
            if (_ended)
                return false;
            _ended = true;
            return true;
        }
    }

    private void Write(object line)
    {
        string text = JsonSerializer.Serialize(line, Line);
        lock (_gate)
            _line(text);
    }
}
