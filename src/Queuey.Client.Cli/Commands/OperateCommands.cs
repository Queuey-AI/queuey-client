using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// Blindtest 2 (2026-10-09, funn 3): CLI-en skal kunne det MCP kan, med innloggingen. Kommandoene bygger på REST-endepunktene som
// virker med en nøkkel eller en innlogging i dag (kartlagt mot Queuey integration/agents). En innlogging regnes som en nøkkel der:
// prod-policyen gjelder, så en handling som venter på en person gir exit 5 med lenken, som `credentials rotate`.

/// <summary>
/// What the operator commands share: the connection (flag, profile, then the deployment file's workspace), the queue by name or
/// id, and the answers Queuey gives when a person decides (202 <c>pending_approval</c>, 403 <c>approval_required</c>).
/// </summary>
internal static class Operator
{
    /// <summary>The version of the operator commands' <c>--json</c> shape.</summary>
    internal const int JsonSchemaVersion = 1;

    /// <summary>The names of Queuey's event statuses, by the number the API answers with.</summary>
    internal static readonly string[] StatusNames =
        { "Received", "InProgress", "Delivered", "Logged", "Failed", "Sandbox", "Dlq", "Skipped", "Filtered" };

    /// <summary>One command's connection and queue.</summary>
    internal sealed class Session : IDisposable
    {
        public required ResolvedConfig Config { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required QueueyManagement Management { get; init; }
        public required string QueueId { get; init; }
        public string? QueueName { get; init; }

        /// <summary>The queue as the answer names it: its name and id, or its id.</summary>
        public string Shown => QueueName is null ? QueueId : $"{QueueName} ({QueueId})";

        public Task<JsonElement?> GetAsync(IReadOnlyList<KeyValuePair<string, string?>>? query, params string[] segments)
            => Management.OperateAsync(HttpMethod.Get, query, null, segments);

        public Task<JsonElement?> SendAsync(HttpMethod method, IReadOnlyList<KeyValuePair<string, string?>>? query, object? body,
            params string[] segments)
            => Management.OperateAsync(method, query, body, segments);

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>
    /// The session for <paramref name="command"/> on <paramref name="queue"/> (a name or <c>que_…</c>), or the exit code of why
    /// there is none. Sends nothing without a key or a login for the API host.
    /// </summary>
    internal static async Task<(Session? Session, int Exit)> OpenAsync(ArgMap map, string command, string? queue)
    {
        if (string.IsNullOrWhiteSpace(queue))
            return (null, CliErrors.Usage(map, "missing_argument", $"{command} requires a queue: its name or its id (que_…)."));
        queue = queue!.Trim();

        (ResolvedConfig config, string? workspaceFrom) = DeploymentTenant.OrFromDeploymentFile(CliHost.Resolve(map, profiles: true), map);
        if (Logins.Missing(config, CliHost.Env, command) is { } missing)
            return (null, CliErrors.Configuration(map, "not_logged_in", missing.Message, missing.Action));

        ServiceProvider provider = CliHost.BuildProvider(config);
        if (provider.GetRequiredService<IQueueyService>().Management is not QueueyManagement management)
        {
            provider.Dispose();
            return (null, CliErrors.Write(map.Has("json"), "unsupported", $"This build's Queuey client can't run {command}.", null,
                status: null, ExitCodes.RuntimeError));
        }

        if (queue.StartsWith("que_", StringComparison.Ordinal))
            return (new Session { Config = config, Provider = provider, Management = management, QueueId = queue }, ExitCodes.Success);

        // Et navn, som i deploy-fila: Queuey leser køer ved id, så navnet slås opp i workspacet.
        if (string.IsNullOrWhiteSpace(config.TenantPublicId))
        {
            provider.Dispose();
            return (null, CliErrors.Usage(map, "missing_argument", $"A queue name needs its workspace to find the queue '{CliErrors.Shown(queue)}'.",
                "Add --tenant <ten_…> or --profile, run it where queuey.deploy.json names the workspace, or give the queue's id (que_…)."));
        }

        if (workspaceFrom is not null)
            Console.Error.WriteLine($"Workspace {config.TenantPublicId} from {workspaceFrom}.");
        IReadOnlyList<QueueListItem> queues = await management.ListQueuesAsync(config.TenantPublicId!);
        string? id = queues.FirstOrDefault(q => string.Equals(q.DisplayName, queue, StringComparison.Ordinal))?.PublicId;
        if (id is null)
        {
            provider.Dispose();
            return (null, CliErrors.Write(map.Has("json"), "queue_not_found",
                $"No queue named '{CliErrors.Shown(queue)}' in workspace {config.TenantPublicId}.", "Check the name, or give the queue's id (que_…).",
                status: 404, ExitCodes.RuntimeError, "Queuey error"));
        }

        return (new Session { Config = config, Provider = provider, Management = management, QueueId = id, QueueName = queue }, ExitCodes.Success);
    }

    /// <summary>
    /// Whether <paramref name="answer"/> is Queuey giving the action to a person (202 <c>pending_approval</c>). Writes the link
    /// and returns exit 5 when it is; null otherwise.
    /// </summary>
    internal static int? Pending(bool json, JsonElement? answer, string what)
    {
        if (answer is not { ValueKind: JsonValueKind.Object } a || Text(a, "status") != "pending_approval")
            return null;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = JsonSchemaVersion,
                status = "pending_approval",
                operation = Text(a, "operation"),
                approvalUrl = Text(a, "approvalUrl"),
                expiresAt = Text(a, "expiresAt"),
                policyRule = Text(a, "policyRule"),
            }, CliHost.JsonOut));
            return ExitCodes.PendingApproval;
        }

        Console.WriteLine($"Nothing was done yet: a person approves {what}.");
        if (Text(a, "approvalUrl") is { } url)
            Console.WriteLine($"  They approve or reject it here: {TerminalText.Line(url)}");
        if (Text(a, "expiresAt") is { } expires)
            Console.WriteLine($"  The request expires {TerminalText.Line(expires)}.");
        if (Text(a, "operation") is { } operation)
            Console.WriteLine($"  Operation: {TerminalText.Line(operation)}");
        return ExitCodes.PendingApproval;
    }

    /// <summary>Queuey refused because a person does this here (403 <c>approval_required</c>): exit 5, with the page.</summary>
    internal static int ApprovalRequired(bool json, QueueyException refused, string what)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = JsonSchemaVersion,
                status = "approval_required",
                message = refused.Message,
                action = refused.SuggestedAction,
                consoleUrl = refused.ConsoleUrl,
            }, CliHost.JsonOut));
            return ExitCodes.PendingApproval;
        }

        Console.WriteLine($"Nothing was done: a person {what} in this workspace.");
        Console.WriteLine($"  {TerminalText.Line(refused.Message)}");
        if (refused.SuggestedAction is { } action)
            Console.WriteLine($"  {TerminalText.Line(action)}");
        if (refused.ConsoleUrl is { } url)
            Console.WriteLine($"  In the console: {TerminalText.Line(url)}");
        return ExitCodes.PendingApproval;
    }

    internal static bool IsApprovalRequired(QueueyException ex) => ex.StatusCode == 403 && ex.ErrorCode == "approval_required";

    /// <summary>A string property, or null.</summary>
    internal static string? Text(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>An event status as its name, whether Queuey answered with the number or the name.</summary>
    internal static string? Status(JsonElement e, string name = "status")
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
            return n >= 0 && n < StatusNames.Length ? StatusNames[n] : n.ToString(CultureInfo.InvariantCulture);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>An array property, or empty.</summary>
    internal static IEnumerable<JsonElement> Items(JsonElement? e, string name)
        => e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    /// <summary>Writes <c>label: value</c> for each value Queuey gave, through <see cref="TerminalText"/>.</summary>
    internal static void Lines(JsonElement? e, params (string Label, string Name)[] fields)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) return;
        foreach ((string label, string name) in fields)
        {
            if (Text(o, name) is { } value)
                Console.WriteLine($"  {label}: {TerminalText.Line(value)}");
        }
    }

    /// <summary>The usage error for an option value that is not a whole number in range.</summary>
    internal static bool TryNumber(ArgMap map, string option, int min, int max, int fallback, out int value, out int exit)
    {
        exit = 0;
        value = fallback;
        if (map.Get(option) is not { } raw) return true;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max) return true;
        exit = CliErrors.Usage(map, "invalid_value", $"--{option} takes a whole number from {min} to {max}.");
        return false;
    }
}

/// <summary><c>queuey queue health &lt;queue&gt;</c>: the queue's traffic, its receivers and its blocked lanes, as Queuey reads them.</summary>
internal static class QueueHealthCommand
{
    internal static readonly CommandOptions Options = new("queue health", flags: new[] { "json" }, values: new[] { "profile" }, positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        (Operator.Session? session, int exit) = await Operator.OpenAsync(map, "queue health", map.FirstPositional);
        if (session is null) return exit;
        using (session)
        {
            JsonElement? snapshot = await session.GetAsync(null, "queues", session.QueueId, "metrics", "snapshot");
            JsonElement? targets = await session.GetAsync(null, "queues", session.QueueId, "targets");
            // Banene krever event.read; en innlogging med bare queue.read får helsen uten dem.
            JsonElement? lanes = null;
            string? lanesUnavailable = null;
            try
            {
                lanes = await session.GetAsync(new[] { Pair("page", "1"), Pair("pageSize", "20") }, "events", session.QueueId, "lanes");
            }
            catch (QueueyForbiddenException ex)
            {
                lanesUnavailable = ex.Message;
            }

            if (map.Has("json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = Operator.JsonSchemaVersion,
                    queuePublicId = session.QueueId,
                    queueName = session.QueueName,
                    snapshot,
                    targets,
                    lanes,
                    lanesUnavailable,
                }, CliHost.JsonOut));
                return ExitCodes.Success;
            }

            Console.WriteLine($"Queue {TerminalText.Line(session.Shown)}");
            Operator.Lines(snapshot, ("state", "processState"), ("received", "received"), ("delivered", "delivered"), ("failed", "failed"),
                ("success rate", "successRate"), ("depth", "depthSample"), ("in flight", "inFlightSample"), ("dlq", "dlqSample"),
                ("locked until", "lockedUntilUtc"), ("locked because", "lockedReason"), ("failure streak", "deliveryFailureStreak"));

            JsonElement[] receivers = targets is { ValueKind: JsonValueKind.Array } t ? t.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            Console.WriteLine(receivers.Length == 0 ? "  receivers: none" : $"  receivers: {receivers.Length}");
            foreach (JsonElement r in receivers)
            {
                string line = $"    {Operator.Text(r, "targetName") ?? Operator.Text(r, "targetId")}: {Operator.Text(r, "state") ?? "?"}";
                if (Operator.Text(r, "consecutiveFailures") is { } failures && failures != "0") line += $", {failures} failure(s) in a row";
                if (Operator.Text(r, "lastFailureClass") is { } cls) line += $", last failure {cls}";
                if (Operator.Text(r, "requiresActionReason") is { } why) line += $" — {why}";
                Console.WriteLine(TerminalText.Line(line) + $" (target {TerminalText.Line(Operator.Text(r, "targetId") ?? "?")})");
            }

            if (lanes is { ValueKind: JsonValueKind.Object } l)
            {
                if (l.TryGetProperty("counts", out JsonElement counts))
                    Console.WriteLine(TerminalText.Line($"  lanes: {Operator.Text(counts, "total") ?? "?"} ({Operator.Text(counts, "active") ?? "0"} active, " +
                                                       $"{Operator.Text(counts, "blocked") ?? "0"} blocked, {Operator.Text(counts, "held") ?? "0"} held)"));
                if (l.TryGetProperty("problemLanes", out JsonElement problems))
                {
                    foreach (JsonElement lane in Operator.Items(problems, "items").Take(10))
                        Console.WriteLine(TerminalText.Line($"    {Operator.Text(lane, "partitionKey") ?? "(no key)"}: {Operator.Text(lane, "status")}, " +
                                                           $"blocked by {Operator.Text(lane, "blockingEventPublicId") ?? "-"}, {Operator.Text(lane, "pendingCount") ?? "0"} waiting"));
                }
            }
            else if (lanesUnavailable is not null)
                Console.WriteLine("  lanes: not shown — reading them needs event.read.");

            return ExitCodes.Success;
        }
    }

    internal static KeyValuePair<string, string?> Pair(string key, string? value) => new(key, value);
}

/// <summary>
/// <c>queuey diagnose &lt;queue&gt;</c>: Queuey's incident report for the queue, and what the recent failed deliveries have in
/// common — the response codes, the endpoints and the errors — from their attempts.
/// </summary>
internal static class DiagnoseCommand
{
    internal static readonly CommandOptions Options = new("diagnose", flags: new[] { "json" }, values: new[] { "profile", "take" }, positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }
        if (!Operator.TryNumber(map, "take", 1, 25, 10, out int take, out int bad)) return bad;

        (Operator.Session? session, int exit) = await Operator.OpenAsync(map, "diagnose", map.FirstPositional);
        if (session is null) return exit;
        using (session)
        {
            JsonElement? report = await session.GetAsync(null, "events", session.QueueId, "incident-report");
            JsonElement? failed = await session.GetAsync(new[]
            {
                QueueHealthCommand.Pair("hasFailures", "true"), QueueHealthCommand.Pair("page", "1"),
                QueueHealthCommand.Pair("pageSize", take.ToString(CultureInfo.InvariantCulture)),
                QueueHealthCommand.Pair("sortBy", "createdatutc"), QueueHealthCommand.Pair("sortDirection", "desc"),
            }, "events", session.QueueId);

            // Det MCP grupperer (diagnose_queue_failure), regnet ut her fra forsøkene til de siste feilede eventene.
            var codes = new Dictionary<string, int>(StringComparer.Ordinal);
            var endpoints = new Dictionary<string, int>(StringComparer.Ordinal);
            var errors = new Dictionary<string, int>(StringComparer.Ordinal);
            var inspected = new List<string>();
            int attempts = 0;
            foreach (JsonElement item in Operator.Items(failed, "items"))
            {
                if (Operator.Text(item, "publicId") is not { } id) continue;
                inspected.Add(id);
                JsonElement? detail = await session.GetAsync(null, "events", session.QueueId, id);
                foreach (JsonElement attempt in Operator.Items(detail, "attempts"))
                {
                    attempts++;
                    Count(codes, Operator.Text(attempt, "responseCode") ?? "no response");
                    if (Operator.Text(attempt, "targetEndpoint") is { } endpoint) Count(endpoints, endpoint);
                    if (Operator.Text(attempt, "errorMessage") is { } error) Count(errors, error.Length > 160 ? error[..160] + "…" : error);
                }
            }

            var summary = new
            {
                failedEventsInspected = inspected.Count,
                failedAttemptsInspected = attempts,
                topResponseCodes = Top(codes),
                topEndpoints = Top(endpoints),
                topErrors = Top(errors),
            };

            if (map.Has("json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = Operator.JsonSchemaVersion,
                    queuePublicId = session.QueueId,
                    queueName = session.QueueName,
                    report,
                    summary,
                    inspectedEvents = inspected,
                }, CliHost.JsonOut));
                return ExitCodes.Success;
            }

            Console.WriteLine($"Queue {TerminalText.Line(session.Shown)}: " +
                              TerminalText.Line($"{(report is { } r ? Operator.Text(r, "incidentType") : null) ?? "unknown"}" +
                                                $"{(report is { } s && Operator.Text(s, "severity") is { } sev ? $" ({sev})" : "")}"));
            Operator.Lines(report, ("locked because", "lockedReason"), ("locked until", "lockedUntilUtc"));
            if (report is { ValueKind: JsonValueKind.Object } rep && rep.TryGetProperty("lastFailedEvent", out JsonElement last) && last.ValueKind == JsonValueKind.Object)
            {
                Console.WriteLine(TerminalText.Line($"  last failed event: {Operator.Text(last, "publicId")} → {Operator.Text(last, "targetEndpoint") ?? "?"}, " +
                                                   $"{Operator.Text(last, "responseCode") ?? "no response"}{(Operator.Text(last, "errorMessage") is { } m ? $" — {m}" : "")}"));
            }
            foreach (JsonElement step in Operator.Items(report, "requiredActions"))
                Console.WriteLine("  required: " + TerminalText.Line(step.ValueKind == JsonValueKind.String ? step.GetString()! : step.GetRawText()));
            foreach (JsonElement step in Operator.Items(report, "recommendedActions"))
                Console.WriteLine("  recommended: " + TerminalText.Line(step.ValueKind == JsonValueKind.String ? step.GetString()! : step.GetRawText()));

            Console.WriteLine($"  {inspected.Count} recent failed event(s), {attempts} attempt(s):");
            Write("response codes", summary.topResponseCodes);
            Write("endpoints", summary.topEndpoints);
            Write("errors", summary.topErrors);
            return ExitCodes.Success;
        }
    }

    private static void Count(Dictionary<string, int> into, string key) => into[key] = into.TryGetValue(key, out int n) ? n + 1 : 1;

    private static object[] Top(Dictionary<string, int> counts)
        => counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(5)
            .Select(p => (object)new { value = p.Key, count = p.Value }).ToArray();

    private static void Write(string label, object[] top)
    {
        if (top.Length == 0) return;
        string joined = string.Join(", ", top.Select(t => JsonSerializer.SerializeToElement(t)).Select(e => $"{Operator.Text(e, "value")} ×{Operator.Text(e, "count")}"));
        Console.WriteLine($"    {label}: {TerminalText.Line(joined)}");
    }
}

/// <summary><c>queuey events search &lt;queue&gt;</c>: a queue's events by status, idempotency key, type, group, source and time.</summary>
internal static class EventsSearchCommand
{
    internal static readonly CommandOptions Options = new(
        "events search",
        flags: new[] { "json", "has-failures" },
        values: new[] { "profile", "status", "idempotency-key", "event-type", "group-key", "source", "from", "to", "response-code", "page", "page-size" },
        positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }
        if (!Operator.TryNumber(map, "page", 1, 100_000, 1, out int page, out int bad)) return bad;
        if (!Operator.TryNumber(map, "page-size", 1, 200, 20, out int pageSize, out bad)) return bad;
        foreach (string time in new[] { "from", "to" })
        {
            if (map.Get(time) is { } raw && !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                return CliErrors.Usage(map, "invalid_value", $"--{time} takes a time, such as 2026-10-09T12:00:00Z.");
        }

        var query = new List<KeyValuePair<string, string?>>();
        // Statusene som komma-liste: --status dlq,failed. Queuey tar navnene uavhengig av store og små bokstaver.
        foreach (string status in (map.Get("status") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            query.Add(QueueHealthCommand.Pair("statuses", status));
        // Idempotency-Key lagres som eventets deliveryKey (Queuey Ingress); filteret heter det.
        query.Add(QueueHealthCommand.Pair("deliveryKey", map.Get("idempotency-key")));
        query.Add(QueueHealthCommand.Pair("eventTypeKey", map.Get("event-type")));
        query.Add(QueueHealthCommand.Pair("groupKey", map.Get("group-key")));
        query.Add(QueueHealthCommand.Pair("source", map.Get("source")));
        query.Add(QueueHealthCommand.Pair("createdFromUtc", Utc(map.Get("from"))));
        query.Add(QueueHealthCommand.Pair("createdToUtc", Utc(map.Get("to"))));
        query.Add(QueueHealthCommand.Pair("responseCode", map.Get("response-code")));
        if (map.Has("has-failures")) query.Add(QueueHealthCommand.Pair("hasFailures", "true"));
        query.Add(QueueHealthCommand.Pair("page", page.ToString(CultureInfo.InvariantCulture)));
        query.Add(QueueHealthCommand.Pair("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)));
        query.Add(QueueHealthCommand.Pair("sortBy", "createdatutc"));
        query.Add(QueueHealthCommand.Pair("sortDirection", "desc"));

        (Operator.Session? session, int exit) = await Operator.OpenAsync(map, "events search", map.FirstPositional);
        if (session is null) return exit;
        using (session)
        {
            JsonElement? result = await session.GetAsync(query, "events", session.QueueId);
            JsonElement[] items = Operator.Items(result, "items").ToArray();
            string? total = result is { } r ? Operator.Text(r, "totalCount") : null;

            if (map.Has("json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = Operator.JsonSchemaVersion,
                    queuePublicId = session.QueueId,
                    queueName = session.QueueName,
                    page,
                    pageSize,
                    totalCount = total is null ? (long?)null : long.Parse(total, CultureInfo.InvariantCulture),
                    items = items.Select(i => new
                    {
                        publicId = Operator.Text(i, "publicId"),
                        status = Operator.Status(i),
                        createdAtUtc = Operator.Text(i, "createdAtUtc"),
                        lastAttemptAtUtc = Operator.Text(i, "lastAttemptAtUtc"),
                        attemptCount = Operator.Text(i, "attemptCount") is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : (int?)null,
                        hasFailures = Operator.Text(i, "hasFailures") == "true",
                        idempotencyKey = Operator.Text(i, "deliveryKey"),
                        eventTypeKey = Operator.Text(i, "eventTypeKey"),
                        groupKey = Operator.Text(i, "groupKey"),
                        source = Operator.Text(i, "source"),
                        holdReason = Operator.Text(i, "holdReason"),
                    }),
                }, CliHost.JsonOut));
                return ExitCodes.Success;
            }

            Console.WriteLine($"{items.Length} of {total ?? "?"} event(s) in {TerminalText.Line(session.Shown)}, newest first (page {page}):");
            foreach (JsonElement i in items)
            {
                string line = $"  {Operator.Text(i, "publicId")}  {Operator.Status(i) ?? "?"}  {Operator.Text(i, "createdAtUtc")}  " +
                              $"{Operator.Text(i, "attemptCount") ?? "0"} attempt(s)";
                if (Operator.Text(i, "deliveryKey") is { } key) line += $"  key {key}";
                if (Operator.Text(i, "eventTypeKey") is { } type) line += $"  {type}";
                Console.WriteLine(TerminalText.Line(line));
            }
            Console.WriteLine("Read one with: queuey events get <evt_…> --queue " + TerminalText.Line(session.QueueName ?? session.QueueId));
            return ExitCodes.Success;
        }
    }

    private static string? Utc(string? value)
        => value is null ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
}

/// <summary>
/// <c>queuey resume &lt;queue&gt;</c>: verify the receiver and resume delivery (<c>verify-and-resume</c>). Queuey probes the
/// receiver, sends the event at the head again, and lifts the lock when it is delivered.
/// </summary>
internal static class ResumeCommand
{
    internal static readonly CommandOptions Options = new(
        "resume", flags: new[] { "json", "dry-run" }, values: new[] { "profile", "target", "replay" }, positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }
        string replay = (map.Get("replay") ?? "none").Trim().ToLowerInvariant();
        if (replay is not ("none" or "failed" or "dlq"))
            return CliErrors.Usage(map, "invalid_value", "--replay takes none (the default), failed or dlq: which events Queuey sends again once the receiver answers.");
        bool json = map.Has("json");

        (Operator.Session? session, int exit) = await Operator.OpenAsync(map, "resume", map.FirstPositional);
        if (session is null) return exit;
        using (session)
        {
            string? target = map.Get("target")?.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                JsonElement? targets = await session.GetAsync(null, "queues", session.QueueId, "targets");
                string[] ids = targets is { ValueKind: JsonValueKind.Array } t
                    ? t.EnumerateArray().Select(e => Operator.Text(e, "targetId")).OfType<string>().ToArray()
                    : Array.Empty<string>();
                if (ids.Length != 1)
                    return CliErrors.Write(json, ids.Length == 0 ? "no_target" : "several_targets",
                        ids.Length == 0
                            ? $"Queue {session.Shown} has no receiver to verify and resume."
                            : $"Queue {session.Shown} delivers to {ids.Length} receivers: {string.Join(", ", ids.Select(TerminalText.Line))}.",
                        ids.Length == 0 ? null : "Name the one to resume with --target <id>; queuey queue health shows their state.",
                        status: null, ExitCodes.Usage);
                target = ids[0];
            }

            var query = new[] { QueueHealthCommand.Pair("replay", replay), QueueHealthCommand.Pair("dryRun", map.Has("dry-run") ? "true" : null) };
            JsonElement? answer;
            try
            {
                answer = await session.SendAsync(HttpMethod.Post, query, null, "queues", session.QueueId, "targets", target!, "verify-and-resume");
            }
            catch (QueueyException ex) when (Operator.IsApprovalRequired(ex))
            {
                return Operator.ApprovalRequired(json, ex, "resumes delivery");
            }

            if (Operator.Pending(json, answer, $"resuming delivery to {session.Shown}") is { } pending)
                return pending;

            bool dryRun = map.Has("dry-run");
            bool ok = dryRun || (answer is { } a && Operator.Text(a, "ok") == "true");
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = Operator.JsonSchemaVersion, queuePublicId = session.QueueId, targetId = target, dryRun, result = answer,
                }, CliHost.JsonOut));
                return ok ? ExitCodes.Success : ExitCodes.RuntimeError;
            }

            if (dryRun)
            {
                Console.WriteLine($"Dry run for {TerminalText.Line(session.Shown)}, target {TerminalText.Line(target!)}: nothing was sent.");
                Operator.Lines(answer, ("would resume", "wouldResume"), ("locked because", "lockReason"), ("would send again", "wouldReplay"));
                foreach (JsonElement w in Operator.Items(answer, "warnings"))
                    Console.WriteLine("  warning: " + TerminalText.Line(w.ValueKind == JsonValueKind.String ? w.GetString()! : w.GetRawText()));
                return ExitCodes.Success;
            }

            JsonElement result = answer ?? default;
            if (ok)
            {
                Console.WriteLine($"Delivery to {TerminalText.Line(session.Shown)} resumed: the receiver answered and the lock is " +
                                  (Operator.Text(result, "lockCleared") == "true" ? "lifted." : "not held."));
                Operator.Lines(result, ("head event", "headEventPublicId"), ("head delivered", "headEventDelivered"), ("sent again", "replayed"));
                return ExitCodes.Success;
            }

            Console.WriteLine($"Not resumed: the receiver for {TerminalText.Line(session.Shown)} did not answer as it should.");
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("probe", out JsonElement probe))
                Operator.Lines(probe, ("status", "statusCode"), ("error", "error"), ("url", "targetUrl"));
            Operator.Lines(result, ("failure", "failureClass"), ("next", "suggestedActionInstruction"));
            return ExitCodes.RuntimeError;
        }
    }
}

/// <summary><c>queuey unlock &lt;queue&gt;</c>: lifts the lock Queuey put on a queue's delivery.</summary>
internal static class UnlockCommand
{
    internal static readonly CommandOptions Options = new("unlock", flags: new[] { "json" }, values: new[] { "profile" }, positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }
        bool json = map.Has("json");

        (Operator.Session? session, int exit) = await Operator.OpenAsync(map, "unlock", map.FirstPositional);
        if (session is null) return exit;
        using (session)
        {
            JsonElement? answer;
            try
            {
                answer = await session.SendAsync(HttpMethod.Post, null, null, "queues", session.QueueId, "unlock");
            }
            catch (QueueyException ex) when (Operator.IsApprovalRequired(ex))
            {
                return Operator.ApprovalRequired(json, ex, "unlocks a queue");
            }

            if (Operator.Pending(json, answer, $"unlocking {session.Shown}") is { } pending)
                return pending;

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = Operator.JsonSchemaVersion, queuePublicId = session.QueueId, status = "unlocked",
                }, CliHost.JsonOut));
            else
                Console.WriteLine($"Unlocked {TerminalText.Line(session.Shown)}: delivery continues. queuey queue health shows whether it stays so.");
            return ExitCodes.Success;
        }
    }
}

/// <summary>
/// The delivering half of <c>queuey replay</c>: one event (<c>--redeliver</c>) or the events with a status
/// (<c>--status dlq</c>), sent to the queue's receiver again.
/// </summary>
internal static class Redeliver
{
    /// <summary>The most events a replay sends by default: what a key or a login sends in prod without a person.</summary>
    internal const int DefaultMax = 100;

    public static async Task<int> RunAsync(ArgMap map, string? eventId, string? queue)
    {
        bool json = map.Has("json");
        if (!Operator.TryNumber(map, "max", 1, 500, DefaultMax, out int max, out int bad)) return bad;
        string[] statuses = (map.Get("status") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (eventId is null && statuses.Length == 0)
            return CliErrors.Usage(map, "missing_argument",
                "replay sends again either one event (queuey replay <evt_…> --queue <q> --redeliver) or the events with a status " +
                "(queuey replay --queue <q> --status dlq).");
        if (eventId is not null && statuses.Length > 0)
            return CliErrors.Usage(map, "invalid_value", "replay takes one event or --status, not both.");

        (Operator.Session? session, int exit) = await Operator.OpenAsync(map, "replay", queue);
        if (session is null) return exit;
        using (session)
        {
            JsonElement? answer;
            try
            {
                answer = eventId is not null
                    ? await session.SendAsync(HttpMethod.Post, null, null, "events", session.QueueId, eventId, "replay")
                    : await session.SendAsync(HttpMethod.Post, new[] { QueueHealthCommand.Pair("dryRun", map.Has("dry-run") ? "true" : null) },
                        new { filter = new { statuses }, maxItems = max }, "events", session.QueueId, "replay");
            }
            catch (QueueyException ex) when (Operator.IsApprovalRequired(ex))
            {
                return Operator.ApprovalRequired(json, ex, "sends these events again");
            }

            if (Operator.Pending(json, answer, $"sending events in {session.Shown} again") is { } pending)
                return pending;

            JsonElement result = answer ?? default;
            bool ok = eventId is null || Operator.Text(result, "ok") == "true";
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = Operator.JsonSchemaVersion, queuePublicId = session.QueueId, dryRun = map.Has("dry-run"), result = answer,
                }, CliHost.JsonOut));
                return ok ? ExitCodes.Success : ExitCodes.RuntimeError;
            }

            if (eventId is not null)
            {
                Console.WriteLine(ok
                    ? $"Queuey sends {TerminalText.Line(eventId)} to the receiver of {TerminalText.Line(session.Shown)} again."
                    : $"Not sent again: {TerminalText.Line(Operator.Text(result, "reason") ?? "Queuey gave no reason")}.");
                return ok ? ExitCodes.Success : ExitCodes.RuntimeError;
            }

            if (map.Has("dry-run"))
            {
                Console.WriteLine($"Dry run: {Operator.Text(result, "matched") ?? "?"} event(s) match, {Operator.Text(result, "wouldAct") ?? "?"} would be sent again " +
                                  $"(at most {max}). Nothing was sent.");
                foreach (JsonElement w in Operator.Items(result, "warnings"))
                    Console.WriteLine("  warning: " + TerminalText.Line(w.ValueKind == JsonValueKind.String ? w.GetString()! : w.GetRawText()));
                return ExitCodes.Success;
            }

            Console.WriteLine($"Sending {Operator.Text(result, "updated") ?? "0"} of {Operator.Text(result, "requested") ?? "?"} event(s) again; " +
                              $"{Operator.Text(result, "skipped") ?? "0"} skipped.");
            if (Operator.Text(result, "nextOlderThanEvent") is { } next)
                Console.WriteLine($"  More match: run it again to send the next {max} (older than {TerminalText.Line(next)}).");
            return ExitCodes.Success;
        }
    }
}
