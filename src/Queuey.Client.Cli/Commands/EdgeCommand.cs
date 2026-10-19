using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Queuey.Edge;
using Queuey.Edge.Mqtt;

namespace Queuey.Client.Cli;

/// <summary>
/// Operator verbs for a Queuey Edge spool. Everything here works directly
/// on the spool FILE (WAL allows a concurrent reader/writer alongside a
/// running host), so an operator can inspect and act without stopping the
/// application.
///
/// <code>
/// queuey edge status  --spool &lt;path&gt; [--json]
/// queuey edge retry   --spool &lt;path&gt; (--id N | --all)
/// queuey edge discard --spool &lt;path&gt; --id N
/// queuey edge recover --spool &lt;path&gt;
/// queuey edge reset   --spool &lt;path&gt; --accept-data-loss
/// </code>
/// </summary>
internal static class EdgeCommand
{
    // Hvert underverb tar sine egne valg, så et valg som bare gir mening for ett av dem, er ukjent for
    // resten i stedet for stille ignorert (2026-09-24).
    internal static readonly System.Collections.Generic.Dictionary<string, CommandOptions> Verbs = new(StringComparer.Ordinal)
    {
        ["status"] = new("edge status", flags: new[] { "json" }, values: new[] { "spool" }),
        ["publish"] = new("edge publish", flags: new[] { "json" },
            values: new[] { "spool", "spool-max-mb", "data", "file", "content-type", "idempotency-key", "event-type", "group-key", "occurred-at" }),
        ["run"] = new("edge run", flags: new[] { "report-health", "mqtt-tls" },
            values: new[]
            {
                "spool", "spool-max-mb", "backoff-cap-seconds", "listen", "node-name", "node-id",
                "mqtt", "mqtt-routes", "mqtt-user", "mqtt-password", "mqtt-retained", "mqtt-id-field"
            }),
        ["kick"] = new("edge kick", values: new[] { "spool" }),
        ["drain"] = new("edge drain", values: new[] { "spool", "timeout" }),
        ["retry"] = new("edge retry", flags: new[] { "all" }, values: new[] { "spool", "id" }),
        ["discard"] = new("edge discard", values: new[] { "spool", "id" }),
        ["recover"] = new("edge recover", values: new[] { "spool" }),
        ["reset"] = new("edge reset", flags: new[] { "accept-data-loss" }, values: new[] { "spool" }),
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;
        if (sub is "" or "help" or "-h" or "--help")
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        // "publish <queue> ..." carries a positional queue name; every other
        // verb is options-only.
        var positional = sub == "publish" && args.Length > 1 && !args[1].StartsWith('-')
            ? args[1]
            : null;
        var optionArgs = positional is null
            ? (args.Length > 1 ? args[1..] : Array.Empty<string>())
            : args[2..];

        if (!Verbs.TryGetValue(sub, out CommandOptions? options))
            return UnknownSub(sub, optionArgs);

        if (!options.TryParse(optionArgs, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h"))
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        var spoolPath = map.Get("spool");
        if (string.IsNullOrWhiteSpace(spoolPath))
            return CliErrors.Usage(map, "missing_argument", "Missing --spool <path> (the Edge spool file, e.g. queuey-edge/spool.db).");

        return sub switch
        {
            "status" => await StatusAsync(spoolPath!, map.Has("json")),
            "publish" => await PublishAsync(spoolPath!, positional, map),
            "run" => await RunHostAsync(spoolPath!, map),
            "kick" => await KickAsync(spoolPath!),
            "drain" => await DrainAsync(spoolPath!, map),
            "retry" => await RetryAsync(spoolPath!, map),
            "discard" => await DiscardAsync(spoolPath!, map),
            "recover" => Recover(spoolPath!),
            "reset" => await ResetAsync(spoolPath!, map.Has("accept-data-loss")),
            _ => UnknownSub(sub, optionArgs),
        };
    }

    // ── publish ─────────────────────────────────────────────────────────

    /// <summary>
    /// The shell/IoT publish path: durably enqueue ONE event into the local
    /// spool — the same accept boundary as the in-process
    /// <c>PublishAsync</c>. Because the spool is SQLite in WAL mode, any
    /// program on the machine (bash, Python, cron, a C binary) can hand
    /// events to a co-resident Edge host this way; the host's transfer loop
    /// picks new rows up within its idle poll (~1 s). No host running? The
    /// event still sits durably and drains whenever one next starts.
    /// </summary>
    private static async Task<int> PublishAsync(string spoolPath, string? queue, ArgMap map)
    {
        if (string.IsNullOrWhiteSpace(queue))
            return CliErrors.Usage(map, "missing_argument",
                "Usage: queuey edge publish <queue> --spool <path> --tenant <ten_...> (--data <json> | --file <path>)");

        var tenant = EdgeTenant(map);
        if (string.IsNullOrWhiteSpace(tenant))
            return CliErrors.Usage(map, "missing_argument", "Missing --tenant <ten_...> (or QUEUEY_TENANT).");

        if (!TryReadSpoolMaxBytes(map, out var maxSpoolBytes, out var limitError))
            return CliErrors.Usage(map, "invalid_value", limitError!);

        byte[] payload;
        var data = map.Get("data");
        var file = map.Get("file");
        if (!string.IsNullOrEmpty(data))
        {
            payload = System.Text.Encoding.UTF8.GetBytes(data);
        }
        else if (!string.IsNullOrEmpty(file))
        {
            if (!File.Exists(file))
                return CliErrors.Write(map.Has("json"), "missing_file", $"No payload file at '{file}'.", action: null, status: null, ExitCodes.RuntimeError);
            payload = CliFiles.ReadAllBytes(file);
        }
        else
        {
            return CliErrors.Usage(map, "missing_body", "Missing payload: provide --data '<json>' or --file <path>.");
        }

        DateTimeOffset? occurredAt = null;
        if (map.Get("occurred-at") is { Length: > 0 } occurredRaw)
        {
            if (!DateTimeOffset.TryParse(occurredRaw, CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return CliErrors.Usage(map, "invalid_value", $"--occurred-at is not a valid ISO 8601 timestamp: '{occurredRaw}'.");
            }
            occurredAt = parsed;
        }

        var envelope = EventEnvelope.Create(
            queue: queue!,
            tenantPublicId: tenant!,
            payload: payload,
            contentType: map.Get("content-type") ?? "application/json",
            idempotencyKey: map.Get("idempotency-key"),
            eventType: map.Get("event-type"),
            groupKey: map.Get("group-key"),
            source: map.Get("source"),
            occurredAtUtc: occurredAt);

        var spool = OpenSpool(spoolPath, maxSpoolBytes);
        try
        {
            var accept = await spool.EnqueueAsync(envelope, CancellationToken.None);
            var stats = await spool.GetStatsAsync(CancellationToken.None);

            if (map.Has("json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    transferId = accept.TransferId,
                    queue = envelope.Queue,
                    acceptedAtUtc = accept.AcceptedAtUtc,
                    occurredAtUtc = envelope.OccurredAtUtc,
                    pending = stats.PendingCount,
                    alreadyAccepted = accept.AlreadyAccepted
                }, CliHost.JsonOut));
            }
            else if (accept.AlreadyAccepted)
            {
                Console.WriteLine($"Already accepted under this idempotency key; nothing new stored. transferId={accept.TransferId} pending={stats.PendingCount}");
            }
            else
            {
                Console.WriteLine($"Accepted durably. transferId={accept.TransferId} pending={stats.PendingCount}");
                Console.WriteLine("A running Queuey Edge host on this machine transfers it; check with 'queuey edge status'.");
            }
            return ExitCodes.Success;
        }
        catch (QueueySpoolFullException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.RuntimeError;
        }
        catch (QueueyStorageFaultedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.RuntimeError;
        }
    }

    // ── run ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The daemon's keys, by the same rules as <c>Queuey.Client</c> and an embedded Edge: <c>QUEUEY_SIGNING_KEY_ID</c> and
    /// <c>QUEUEY_SIGNING_SECRET</c> from the environment (in Development also from <c>.env</c>), else <c>QUEUEY_API_KEY</c>;
    /// and <c>QUEUEY_EDGE_HEALTH_API_KEY</c>, which only the health check-in sends. <c>--api-key</c> still works for a node
    /// that publishes with an API key, but no text suggests it: argv is visible to every user on the machine, and the
    /// signing secret has no flag at all. The node's fixed id comes along: <c>--node-id</c>, else <c>QUEUEY_NODE_ID</c>.
    /// </summary>
    // Edge-signering (Kenneth 2026-10-09): daemonen leser de samme variablene som SDK-en, gjennom QueueyEdgeOptions.
    internal static QueueyEdgeOptions EdgeCredentials(ArgMap map)
    {
        // Security-review av #70 (K-a): flagget virker fortsatt, men den som bruker det, får vite hvor nøkkelen hører hjemme.
        if (!string.IsNullOrWhiteSpace(map.Get("api-key")))
            Console.Error.WriteLine("Warning: --api-key: argv is visible to every user on this machine; move it to QUEUEY_API_KEY or " +
                                    "QUEUEY_EDGE_HEALTH_API_KEY in edge.env.");
        return new QueueyEdgeOptions { ApiKey = map.Get("api-key"), Health = { NodeId = map.Get("node-id") } }
            .UseEnvironmentVariables(CliHost.Env);
    }

    /// <summary>
    /// Hosts the Edge transfer loop as a STANDALONE daemon — the complete
    /// Edge story for machines with no .NET application of their own:
    /// <c>queuey edge run</c> under systemd, and anything on the box
    /// publishes durably with <c>queuey edge publish</c> (or its own
    /// embedded Queuey.Edge). Runs until Ctrl-C / SIGTERM; the spool is the
    /// contract, so restarts resume exactly where they left off.
    /// </summary>
    private static async Task<int> RunHostAsync(string spoolPath, ArgMap map)
    {
        var tenant = EdgeTenant(map);
        var credentials = EdgeCredentials(map);
        if (string.IsNullOrWhiteSpace(credentials.SigningKeyId) != string.IsNullOrWhiteSpace(credentials.SigningSecret))
        {
            Console.Error.WriteLine(
                "QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET go together: set both, as queuey keys mint --write writes them.");
            return ExitCodes.Configuration;
        }
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(credentials.SigningKeyId) && string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            Console.Error.WriteLine(
                "edge run needs --tenant <ten_...> (or QUEUEY_TENANT) and a key: the signing pair QUEUEY_SIGNING_KEY_ID and " +
                "QUEUEY_SIGNING_SECRET in its environment, which queuey keys mint --write <file> writes for the service's " +
                "environment file; or a publish-only key in QUEUEY_API_KEY.");
            return ExitCodes.Configuration;
        }

        // Sjekket her, der operatøren ser det: Validate i verten ville først slått inn når daemonen starter.
        var nodeId = credentials.Health.NodeId;
        try
        {
            EdgeNodeId.Validate(nodeId, map.Get("node-id") is not null ? "--node-id" : QueueyEdgeEnvironmentVariables.NodeId);
        }
        catch (Queuey.Client.QueueyConfigurationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Configuration;
        }

        var ingressBase = map.Get("ingress-base") ?? Environment.GetEnvironmentVariable("QUEUEY_INGRESS_BASE");

        // Opt-in encryption at rest. Environment only — an option file next to
        // the spool would keep the key beside the data it protects.
        byte[]? spoolKey = null;
        if (Environment.GetEnvironmentVariable("QUEUEY_SPOOL_KEY") is { Length: > 0 } spoolKeyRaw)
        {
            try { spoolKey = Convert.FromBase64String(spoolKeyRaw.Trim()); }
            catch (FormatException) { spoolKey = Array.Empty<byte>(); }
            if (spoolKey.Length != SpoolPayloadProtection.KeyBytes)
            {
                Console.Error.WriteLine(
                    $"QUEUEY_SPOOL_KEY must be a base64-encoded {SpoolPayloadProtection.KeyBytes}-byte key " +
                    "(generate one with 'openssl rand -base64 32').");
                return ExitCodes.Usage;
            }
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Logging.AddFilter("Queuey", Microsoft.Extensions.Logging.LogLevel.Information);

        int? listenPort = null;
        if (map.Get("listen") is { Length: > 0 } listenRaw)
        {
            if (!int.TryParse(listenRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
            {
                Console.Error.WriteLine($"--listen must be a port number (got '{listenRaw}').");
                return ExitCodes.Usage;
            }
            listenPort = port;
        }

        if (!TryReadSpoolMaxBytes(map, out var maxSpoolBytes, out var limitError)
            || !TryReadBackoffCap(map, out var backoffCap, out limitError))
        {
            Console.Error.WriteLine(limitError);
            return ExitCodes.Usage;
        }

        // Fleet view: --report-health (or QUEUEY_REPORT_HEALTH=1) makes the
        // node check in to the console. Outbound only; never billed.
        var reportHealth = map.Has("report-health")
            || Environment.GetEnvironmentVariable("QUEUEY_REPORT_HEALTH") is "1" or "true";
        var nodeName = map.Get("node-name") ?? Environment.GetEnvironmentVariable("QUEUEY_NODE_NAME");

        builder.Services.AddQueueyEdge(o =>
        {
            o.ApiKey = credentials.ApiKey;
            o.SigningKeyId = credentials.SigningKeyId;
            o.SigningSecret = credentials.SigningSecret;
            o.Health.ApiKey = credentials.Health.ApiKey;
            o.TenantPublicId = tenant;
            o.Storage.Path = spoolPath;
            if (maxSpoolBytes is { } max)
                o.Storage.MaxSpoolBytes = max;
            if (backoffCap is { } cap)
                o.Transfer.BackoffCap = cap;
            if (spoolKey is not null)
                o.Storage.PayloadKey = spoolKey;
            o.LocalEndpoint.Port = listenPort;
            o.Health.ReportToCloud = reportHealth;
            if (!string.IsNullOrWhiteSpace(nodeName))
                o.Health.NodeName = nodeName;
            o.Health.NodeId = nodeId;
            if (!string.IsNullOrWhiteSpace(ingressBase))
                o.IngressBaseAddress = new Uri(ingressBase);
            if (map.Get("source") is { Length: > 0 } source)
                o.Source = source;
        });

        // MQTT intake: --mqtt host[:port] --mqtt-routes "filter=queue[@segment];…"
        // (or QUEUEY_MQTT / QUEUEY_MQTT_ROUTES). The broker is normally on this
        // same gateway; every message is fsync'd into the spool before it is acked.
        var mqtt = map.Get("mqtt") ?? CliHost.Env("QUEUEY_MQTT");
        var mqttRoutes = map.Get("mqtt-routes") ?? CliHost.Env("QUEUEY_MQTT_ROUTES");
        var mqttIdField = map.Get("mqtt-id-field") ?? CliHost.Env("QUEUEY_MQTT_ID_FIELD");
        if (!string.IsNullOrWhiteSpace(mqtt) || !string.IsNullOrWhiteSpace(mqttRoutes))
        {
            if (string.IsNullOrWhiteSpace(mqtt) || string.IsNullOrWhiteSpace(mqttRoutes))
            {
                Console.Error.WriteLine("MQTT intake needs BOTH --mqtt <host[:port]> and --mqtt-routes \"filter=queue[@segment];…\" (or QUEUEY_MQTT / QUEUEY_MQTT_ROUTES).");
                return ExitCodes.Configuration;
            }
            var hostPort = mqtt.Split(':', 2);
            var mqttUser = map.Get("mqtt-user") ?? CliHost.Env("QUEUEY_MQTT_USER");
            var mqttPass = map.Get("mqtt-password") ?? CliHost.Env("QUEUEY_MQTT_PASSWORD");
            var mqttRetained = map.Get("mqtt-retained") ?? CliHost.Env("QUEUEY_MQTT_RETAINED");
            try
            {
                var routes = Queuey.Edge.Mqtt.MqttRoute.ParseMany(mqttRoutes);
                if (!string.IsNullOrWhiteSpace(mqttIdField))
                {
                    // Every route deduplicates on the same field. Checked here, where the operator is looking: the
                    // source's own check runs when the host starts, and would end the daemon with a stack trace.
                    foreach (var route in routes)
                    {
                        route.IdempotencyKeyField = mqttIdField.Trim();
                        route.Validate();
                    }
                }
                var retained = string.IsNullOrWhiteSpace(mqttRetained)
                    ? Queuey.Edge.Mqtt.MqttRetainedMessages.FirstSubscribe
                    : Queuey.Edge.Mqtt.MqttSourceOptions.ParseRetained(mqttRetained);
                builder.Services.AddQueueyEdgeMqttSource(m =>
                {
                    m.Server = hostPort[0];
                    if (hostPort.Length == 2 && int.TryParse(hostPort[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp))
                        m.Port = mp;
                    m.UseTls = map.Has("mqtt-tls");
                    m.Username = mqttUser;
                    m.Password = mqttPass;
                    m.RetainedMessages = retained;
                    m.Routes.AddRange(routes);
                });
            }
            catch (Queuey.Client.QueueyConfigurationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCodes.Configuration;
            }
        }

        Console.WriteLine($"Queuey Edge daemon. Spool: {spoolPath}");
        Console.WriteLine("Publish from anything on this machine with 'queuey edge publish …'; " +
                          "inspect with 'queuey edge status'. Ctrl-C to stop (accepted events survive restarts).");
        if (reportHealth)
        {
            Console.WriteLine(
                $"Health reporting: ON — this node appears as '{nodeName ?? Environment.MachineName}' under Edge nodes " +
                "in the Queuey console (outbound only; reports are not events).");
            if (nodeId is not null)
                Console.WriteLine($"Node id: {nodeId} (configured; it stays the same with a new spool).");
        }
        if (!string.IsNullOrWhiteSpace(mqtt))
        {
            Console.WriteLine($"MQTT intake: {mqtt} → {mqttRoutes} (QoS 1; a message is acked only after it is durably in the spool; " +
                              "a full spool makes the intake step back from the broker until there is room).");
            if (!string.IsNullOrWhiteSpace(mqttIdField))
                Console.WriteLine($"MQTT deduplication: a message whose '{mqttIdField.Trim()}' and payload Edge already holds is acked as a duplicate, not a new event.");
        }
        if (listenPort is { } lp)
        {
            Console.WriteLine(
                $"Local publish endpoint: POST http://localhost:{lp}/events/{tenant}/{{queue}} " +
                "(loopback only; same wire shape as cloud ingress — 202 = durably accepted locally).");
        }

        await builder.Build().RunAsync();
        return ExitCodes.Success;
    }

    // ── kick ────────────────────────────────────────────────────────────

    /// <summary>
    /// "I fixed the cause — try NOW": collapses every pending event's
    /// backoff wait to zero (unpaused the queue, rotated the key, restored
    /// the route). Ordering and dedup are untouched; only the waiting goes.
    /// </summary>
    private static async Task<int> KickAsync(string spoolPath)
    {
        var spool = OpenSpool(spoolPath);
        try
        {
            var kicked = await spool.KickAsync(CancellationToken.None);
            Console.WriteLine(kicked > 0
                ? $"Kicked {kicked} pending event(s) — due now; a running Edge host picks them up within its idle poll."
                : "Nothing waiting on a backoff — pending events (if any) are already due.");
            return ExitCodes.Success;
        }
        catch (QueueyStorageFaultedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.RuntimeError;
        }
    }

    // ── drain ───────────────────────────────────────────────────────────

    /// <summary>
    /// Waits until the spool has no pending events — the uninstall/shutdown
    /// gate: removing Edge must never silently abandon accepted events. The
    /// draining itself is done by a RUNNING Edge host; this verb only
    /// watches, and exits non-zero if the backlog doesn't empty in time.
    /// </summary>
    private static async Task<int> DrainAsync(string spoolPath, ArgMap map)
    {
        if (!File.Exists(spoolPath))
        {
            Console.WriteLine("No spool file — nothing to drain.");
            return ExitCodes.Success;
        }

        var timeout = TimeSpan.FromSeconds(
            long.TryParse(map.Get("timeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 600);

        var spool = OpenSpool(spoolPath);
        var deadline = DateTime.UtcNow + timeout;
        long lastPending = -1;

        while (DateTime.UtcNow < deadline)
        {
            SpoolStats stats;
            try
            {
                stats = await spool.GetStatsAsync(CancellationToken.None);
            }
            catch (QueueyStorageFaultedException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCodes.RuntimeError;
            }

            if (stats.PendingCount == 0)
            {
                Console.WriteLine(stats.QuarantinedCount > 0
                    ? $"Drained. NOTE: {stats.QuarantinedCount} quarantined event(s) remain — retry or discard them explicitly before removing the spool."
                    : "Drained. The spool holds no pending events.");
                return ExitCodes.Success;
            }

            if (stats.PendingCount != lastPending)
            {
                lastPending = stats.PendingCount;
                Console.WriteLine($"pending={stats.PendingCount} oldest={(stats.OldestPendingAge is { } age ? $"{(int)age.TotalSeconds}s" : "-")}");
            }

            await Task.Delay(500);
        }

        Console.Error.WriteLine(
            $"Timed out after {timeout.TotalSeconds:F0}s with {lastPending} event(s) still pending. " +
            "Draining requires a RUNNING Queuey Edge host with working connectivity — check 'queuey edge status' " +
            "and the host's logs. Do not delete the spool: it holds accepted events.");
        return ExitCodes.RuntimeError;
    }

    // ── status ──────────────────────────────────────────────────────────

    private static async Task<int> StatusAsync(string spoolPath, bool json)
    {
        if (!File.Exists(spoolPath))
        {
            Console.Error.WriteLine($"No spool at '{spoolPath}'.");
            return ExitCodes.RuntimeError;
        }

        var spool = OpenSpool(spoolPath);
        try
        {
            var stats = await spool.GetStatsAsync(CancellationToken.None);
            var quarantined = await ListQuarantinedAsync(spoolPath);
            var nodeId = await spool.GetMetaAsync(EdgeMetaKeys.NodeId, CancellationToken.None);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    spool = spoolPath,
                    nodeId,
                    pending = stats.PendingCount,
                    quarantined = stats.QuarantinedCount,
                    oldestPendingAgeSeconds = stats.OldestPendingAge?.TotalSeconds,
                    storageBytes = stats.StorageUsageBytes,
                    lastSettledAtUtc = stats.LastSettledAtUtc,
                    nextAttemptUtc = stats.NextAttemptUtc,
                    quarantinedEvents = quarantined
                }, CliHost.JsonOut));
                return ExitCodes.Success;
            }

            Console.WriteLine("Queuey Edge spool");
            Console.WriteLine($"  Spool        : {spoolPath}");
            Console.WriteLine($"  Node id      : {nodeId ?? "- (health reporting never enabled)"}");
            Console.WriteLine($"  Pending      : {stats.PendingCount}");
            Console.WriteLine($"  Quarantined  : {stats.QuarantinedCount}");
            Console.WriteLine($"  Oldest age   : {(stats.OldestPendingAge is { } age ? Humanize(age) : "-")}");
            Console.WriteLine($"  Storage      : {stats.StorageUsageBytes / 1024} KiB");
            Console.WriteLine($"  Last settled : {stats.LastSettledAtUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "-"}");
            Console.WriteLine($"  Next attempt : {FormatNextAttempt(stats)}");

            if (quarantined.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Quarantined events (retry with 'queuey edge retry --id N', drop with 'discard'):");
                foreach (var q in quarantined)
                    Console.WriteLine($"    #{q.Id}  {q.Queue}  {q.TransferId}  {q.Reason} (HTTP {q.Status?.ToString() ?? "-"})  attempts={q.Attempts}");
            }
            return ExitCodes.Success;
        }
        catch (QueueyStorageFaultedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.RuntimeError;
        }
    }

    // ── retry / discard ─────────────────────────────────────────────────

    private static async Task<int> RetryAsync(string spoolPath, ArgMap map)
    {
        var spool = OpenSpool(spoolPath);

        if (map.Has("all"))
        {
            var retried = 0;
            foreach (var q in await ListQuarantinedAsync(spoolPath))
            {
                if (await spool.RetryQuarantinedAsync(q.Id, CancellationToken.None)) retried++;
            }
            Console.WriteLine($"Retried {retried} quarantined event(s) — back in the drain.");
            return ExitCodes.Success;
        }

        if (!TryGetId(map, out var id)) return ExitCodes.Usage;

        if (await spool.RetryQuarantinedAsync(id, CancellationToken.None))
        {
            Console.WriteLine($"Event #{id} is back in the drain.");
            return ExitCodes.Success;
        }
        Console.Error.WriteLine($"Event #{id} is not quarantined (only quarantined events can be retried).");
        return ExitCodes.RuntimeError;
    }

    private static async Task<int> DiscardAsync(string spoolPath, ArgMap map)
    {
        if (!TryGetId(map, out var id)) return ExitCodes.Usage;

        var spool = OpenSpool(spoolPath);
        if (await spool.DiscardQuarantinedAsync(id, CancellationToken.None))
        {
            // Loud on purpose: this is one of the three doors out of the
            // spool, taken in the operator's name.
            Console.WriteLine($"Event #{id} DISCARDED (explicit operator action; it will never be delivered).");
            return ExitCodes.Success;
        }
        Console.Error.WriteLine($"Event #{id} is not quarantined — pending events cannot be discarded.");
        return ExitCodes.RuntimeError;
    }

    // ── recover / reset ─────────────────────────────────────────────────

    /// <summary>
    /// Best-effort salvage: copy every still-owned row that can be read into
    /// a fresh spool, report exactly what could not be, then swap files. The
    /// faulted original is kept beside the new spool for support.
    /// </summary>
    private static int Recover(string spoolPath)
    {
        if (!File.Exists(spoolPath))
        {
            Console.Error.WriteLine($"No spool at '{spoolPath}'.");
            return ExitCodes.RuntimeError;
        }

        var salvagePath = spoolPath + ".salvage";
        SpoolSalvage.Result salvage;

        try
        {
            salvage = SpoolSalvage.Copy(spoolPath, salvagePath);
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine(
                $"Salvage could not read the spool at all ({ex.Message}). " +
                "If the file is beyond reading, 'queuey edge reset --accept-data-loss' is the remaining option.");
            File.Delete(salvagePath);
            return ExitCodes.RuntimeError;
        }
        catch (Queuey.Client.QueueyConfigurationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            File.Delete(salvagePath);
            return ExitCodes.RuntimeError;
        }

        // The salvage is complete before it is swapped in: the spool's own
        // schema, owned rows with their ids, claims released.
        var faultedPath = PreserveAside(spoolPath);
        File.Move(salvagePath, spoolPath);

        Console.WriteLine($"Salvaged {salvage.Copied} event(s) into a fresh spool; {salvage.Unreadable} unreadable.");
        // Neither is an event lost, so neither changes the exit code; both change what Cloud sees.
        if (salvage.MetaUnreadable)
            Console.WriteLine("The node identity could not be read: health reporting mints a new one, and Queuey Cloud sees a new node.");
        if (salvage.SettledUnreadable)
            Console.WriteLine("The settled events could not be read: a repeat of one is accepted again (Queuey Cloud deduplicates it on its transfer id).");
        Console.WriteLine($"The faulted file is preserved at '{faultedPath}' for support.");
        return salvage.Unreadable == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static async Task<int> ResetAsync(string spoolPath, bool acknowledged)
    {
        if (!acknowledged)
        {
            // The whole point of StorageFaulted is that data loss is a HUMAN
            // decision. The flag is that decision, in the operator's name.
            Console.Error.WriteLine(
                "reset abandons every event in the spool. If that is what you intend, run again with --accept-data-loss. " +
                "To keep readable events, use 'queuey edge recover' instead.");
            return ExitCodes.Usage;
        }

        if (!File.Exists(spoolPath))
        {
            Console.Error.WriteLine($"No spool at '{spoolPath}'.");
            return ExitCodes.RuntimeError;
        }

        // Read before the file moves: its WAL may hold the newest write, and
        // SQLite finds a WAL only beside the file it belongs to.
        var node = ReadNodeId(spoolPath);

        var faultedPath = PreserveAside(spoolPath);

        Console.WriteLine($"Spool reset. The old file (and any events in it) is preserved at '{faultedPath}'.");
        Console.WriteLine(await KeepNodeIdAsync(spoolPath, node));
        Console.WriteLine("Data loss acknowledged by --accept-data-loss.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Moves the spool aside as <c>&lt;spool&gt;.faulted-&lt;utc&gt;</c>, with
    /// its <c>-wal</c> and <c>-shm</c>, and returns the new path. SQLite finds
    /// a WAL only at the database's own name plus <c>-wal</c>, so each sidecar
    /// keeps its suffix. Until 2026-10-19 the name came from
    /// <c>Path.GetExtension</c>, which for <c>spool.db-wal</c> is
    /// <c>.db-wal</c>: opening the preserved file then left every commit still
    /// in its WAL out of sight.
    /// </summary>
    internal static string PreserveAside(string spoolPath)
    {
        SqliteConnection.ClearAllPools();
        var faultedPath = spoolPath + ".faulted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        File.Move(spoolPath, faultedPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(spoolPath + suffix)) File.Move(spoolPath + suffix, faultedPath + suffix);
        return faultedPath;
    }

    /// <summary>
    /// The node id in a spool file that may be faulted. <c>Readable</c> is
    /// false when the file would not give it up; a readable file without one
    /// (health reporting never ran) gives <c>(true, null)</c>.
    /// </summary>
    private static (bool Readable, string? NodeId) ReadNodeId(string spoolPath)
    {
        try
        {
            // Not ReadOnly: a read-only connection leaves the -wal/-shm it opened
            // behind, and they would travel with the preserved file. As the last
            // connection, this one folds the WAL in and removes them, as
            // recover's does.
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = spoolPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM edge_meta WHERE k = @k;";
            cmd.Parameters.AddWithValue("@k", EdgeMetaKeys.NodeId);
            return (true, cmd.ExecuteScalar() as string);
        }
        catch (SqliteException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Reset starts the events over, not the machine: the node keeps its id,
    /// so Queuey Cloud sees the same node. Only the id moves to the fresh
    /// spool, never <c>settled</c>. Best effort: an id that cannot be read or
    /// written means a new node, never a failed reset.
    /// </summary>
    private static async Task<string> KeepNodeIdAsync(string spoolPath, (bool Readable, string? NodeId) node)
    {
        const string nextPublish = "A fresh spool will be created on the next publish.";
        const string newNode = "health reporting mints a new one, and Queuey Cloud sees a new node.";

        if (!node.Readable)
            return $"The node id could not be read from the old file: {newNode} {nextPublish}";
        if (node.NodeId is not { } nodeId)
            return nextPublish;

        try
        {
            await OpenSpool(spoolPath).SetMetaAsync(EdgeMetaKeys.NodeId, nodeId, CancellationToken.None);
            SqliteConnection.ClearAllPools();
            return $"The fresh spool keeps node id {nodeId}: this is still the same node in Queuey Cloud.";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or Queuey.Client.QueueyException)
        {
            return $"The node id {nodeId} could not be written to a fresh spool ({ex.Message}): {newNode}";
        }
    }

    // ── plumbing ────────────────────────────────────────────────────────

    private sealed record QuarantinedRow(long Id, string Queue, string TransferId, string? Reason, int? Status, long Attempts);

    private static async Task<System.Collections.Generic.List<QuarantinedRow>> ListQuarantinedAsync(string spoolPath)
    {
        var rows = new System.Collections.Generic.List<QuarantinedRow>();
        await using var conn = new SqliteConnection($"Data Source={spoolPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, queue_name, transfer_id, last_reason, last_status, attempts
            FROM spool WHERE state = 'Quarantined' ORDER BY id LIMIT 50;
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new QuarantinedRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : (int)reader.GetInt64(4),
                reader.GetInt64(5)));
        }
        return rows;
    }

    private static SqliteEventSpool OpenSpool(string spoolPath, long? maxSpoolBytes = null)
    {
        var storage = new EdgeStorageOptions { Path = spoolPath };
        if (maxSpoolBytes is { } max) storage.MaxSpoolBytes = max;
        return new SqliteEventSpool(storage, new CliClock());
    }

    /// <summary>
    /// The spool limit from <c>--spool-max-mb</c> or <c>QUEUEY_SPOOL_MAX_MB</c>, in bytes; null when neither is set (the
    /// default, 512 MB). <c>edge run</c> and <c>edge publish</c> both read it: they write the same spool, and a publish that
    /// used the default would fill a spool the daemon holds to a smaller limit.
    /// </summary>
    internal static bool TryReadSpoolMaxBytes(ArgMap map, out long? bytes, out string? error)
    {
        bytes = null;
        if (!TryReadWhole(map, "spool-max-mb", "QUEUEY_SPOOL_MAX_MB", 1, int.MaxValue, "a whole number of megabytes, 1 or more",
                out var megabytes, out error))
            return false;
        if (megabytes is { } mb) bytes = mb * 1024L * 1024L;
        return true;
    }

    /// <summary>
    /// The transfer backoff cap from <c>--backoff-cap-seconds</c> or <c>QUEUEY_BACKOFF_CAP_SECONDS</c> (1–3600); null when
    /// neither is set (the default, 5 minutes). After an outage, the first transfer comes at most this long after the
    /// network is back.
    /// </summary>
    internal static bool TryReadBackoffCap(ArgMap map, out TimeSpan? cap, out string? error)
    {
        cap = null;
        if (!TryReadWhole(map, "backoff-cap-seconds", "QUEUEY_BACKOFF_CAP_SECONDS", 1, 3600, "a whole number of seconds from 1 to 3600",
                out var seconds, out error))
            return false;
        if (seconds is { } s) cap = TimeSpan.FromSeconds(s);
        return true;
    }

    private static bool TryReadWhole(ArgMap map, string flag, string variable, int min, int max, string expected,
        out int? value, out string? error)
    {
        value = null;
        error = null;
        var fromFlag = map.Get(flag);
        var raw = fromFlag ?? CliHost.Env(variable);
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < min || parsed > max)
        {
            error = $"{(fromFlag is null ? variable : "--" + flag)} must be {expected} (got '{CliErrors.Shown(raw.Trim())}').";
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryGetId(ArgMap map, out long id)
    {
        id = 0;
        var raw = map.Get("id");
        if (raw is null || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
        {
            Console.Error.WriteLine("Missing or invalid --id <N> (see 'queuey edge status' for ids).");
            return false;
        }
        return true;
    }

    /// <summary>
    /// "due now" when a pending row is ready or in flight; "in Xs" while the
    /// earliest waiting row sits out its backoff; "-" when nothing is pending.
    /// </summary>
    private static string FormatNextAttempt(SpoolStats stats)
    {
        if (stats.PendingCount == 0) return "-";
        if (stats.NextAttemptUtc is not { } next) return "due now";
        var wait = next - DateTimeOffset.UtcNow;
        return wait <= TimeSpan.Zero ? "due now" : $"in {Humanize(wait)}";
    }

    private static string Humanize(TimeSpan age) => age switch
    {
        { TotalDays: >= 1 } => $"{(int)age.TotalDays}d {age.Hours}h",
        { TotalHours: >= 1 } => $"{(int)age.TotalHours}h {age.Minutes}m",
        { TotalMinutes: >= 1 } => $"{(int)age.TotalMinutes}m {age.Seconds}s",
        _ => $"{(int)age.TotalSeconds}s"
    };

    /// <summary>
    /// The workspace from --tenant or QUEUEY_TENANT, which edge reads itself rather than through queuey.json. A value that
    /// is not a workspace id fails as it does everywhere else (<see cref="CliConfig.WorkspaceId"/>), without being shown.
    /// </summary>
    private static string? EdgeTenant(ArgMap map)
    {
        if (map.Get("tenant") is { } flag && !string.IsNullOrWhiteSpace(flag))
            return CliConfig.WorkspaceId(flag, "--tenant");
        if (Environment.GetEnvironmentVariable("QUEUEY_TENANT") is { } env && !string.IsNullOrWhiteSpace(env))
            return CliConfig.WorkspaceId(env, "QUEUEY_TENANT");
        return null;
    }

    private static int UnknownSub(string sub, string[] rest)
        => CliErrors.Write(CliErrors.WantsJson(rest), "unknown_subcommand",
            $"Unknown edge subcommand '{CliErrors.Shown(sub)}'. Expected: {string.Join(" | ", Verbs.Keys)}.", action: null, status: null, ExitCodes.Usage);

    private sealed class CliClock : IEdgeClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long MonotonicMilliseconds => Environment.TickCount64;
    }
}
