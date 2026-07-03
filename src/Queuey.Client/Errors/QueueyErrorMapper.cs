using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client;

/// <summary>
/// Maps a non-success <see cref="HttpResponseMessage"/> to the appropriate typed
/// <see cref="QueueyException"/>, preserving the API error code and message.
/// </summary>
/// <remarks>
/// The Queuey API does not (yet) use one error shape. This mapper tolerates all observed shapes:
/// <list type="bullet">
/// <item>nested <c>{ "error": { "code", "message" } }</c> (auth + business errors),</item>
/// <item>flat <c>{ "error": "ip_not_allowed", "message": "…" }</c> (error is a string code),</item>
/// <item><c>{ "StatusCode", "Message" }</c> (unhandled exceptions),</item>
/// <item>a plain-text body (e.g. the license middleware's 400), and</item>
/// <item>an empty body (e.g. ingress 404, license 403) — the failure is derived from the status.</item>
/// </list>
/// </remarks>
internal static class QueueyErrorMapper
{
    public static async Task<QueueyException> CreateAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int status = (int)response.StatusCode;
        string body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        ParseError(body, out string? code, out string? message);

        if (string.IsNullOrWhiteSpace(message))
        {
            message = response.ReasonPhrase;
            if (string.IsNullOrWhiteSpace(message))
                message = $"Queuey request failed with status {status}.";
        }

        return status switch
        {
            400 => new QueueyValidationException(message!, code),
            401 => new QueueyAuthException(message!, code),
            403 => new QueueyForbiddenException(message!, code),
            404 => new QueueyNotFoundException(message!, code),
            409 => new QueueyConflictException(message!, code),
            422 => new QueueyLoopDetectedException(message!, code),
            _ => new QueueyException(message!, status, code),
        };
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
            return string.Empty;

        try
        {
#if NET8_0_OR_GREATER
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    /// <summary>Best-effort extraction of an error code + message from any of the known body shapes.</summary>
    internal static void ParseError(string? body, out string? code, out string? message)
    {
        code = null;
        message = null;

        if (string.IsNullOrWhiteSpace(body))
            return;

        string trimmed = body!.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            // Plain-text body (e.g. "Missing required header: X-License-PublicId").
            message = body.Trim();
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                message = body.Trim();
                return;
            }

            if (TryGetProperty(root, "error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    code = GetString(error, "code");
                    message = GetString(error, "message");
                }
                else if (error.ValueKind == JsonValueKind.String)
                {
                    // Flat shape: error is the code string, message is a sibling.
                    code = error.GetString();
                    message = GetString(root, "message");
                }
            }

            // Fallbacks for the exception shape { StatusCode, Message } and stray top-level fields.
            message ??= GetString(root, "message");
            code ??= GetString(root, "code");
        }
        catch (JsonException)
        {
            message = body.Trim();
        }
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        // Case-insensitive lookup (handles "error"/"Error", "message"/"Message").
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string name)
        => TryGetProperty(obj, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
