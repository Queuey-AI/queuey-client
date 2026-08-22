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
    private static readonly System.Collections.Generic.HashSet<string> Flags =
        new(StringComparer.Ordinal) { "json", "all", "accept-data-loss", "help", "h" };

    public static async Task<int> RunAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;

        // "publish <queue> ..." carries a positional queue name; every other
        // verb is options-only.
        var positional = sub == "publish" && args.Length > 1 && !args[1].StartsWith('-')
            ? args[1]
            : null;
        var optionArgs = positional is null
            ? (args.Length > 1 ? args[1..] : Array.Empty<string>())
            : args[2..];
        var map = ArgMap.Parse(optionArgs, Flags);

        if (map.Has("help") || map.Has("h") || sub is "" or "help")
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        var spoolPath = map.Get("spool");
        if (string.IsNullOrWhiteSpace(spoolPath))
        {
            Console.Error.WriteLine("Missing --spool <path> (the Edge spool file, e.g. queuey-edge/spool.db).");
            return ExitCodes.Usage;
        }

        return sub switch
        {
            "status" => await StatusAsync(spoolPath!, map.Has("json")),
            "publish" => await PublishAsync(spoolPath!, positional, map),
            "run" => await RunHostAsync(spoolPath!, map),
            "drain" => await DrainAsync(spoolPath!, map),
            "retry" => await RetryAsync(spoolPath!, map),
            "discard" => await DiscardAsync(spoolPath!, map),
            "recover" => await RecoverAsync(spoolPath!),
            "reset" => Reset(spoolPath!, map.Has("accept-data-loss")),
            _ => UnknownSub(sub)
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
        {
            Console.Error.WriteLine("Usage: queuey edge publish <queue> --spool <path> --tenant <ten_...> (--data <json> | --file <path>)");
            return ExitCodes.Usage;
        }

        var tenant = map.Get("tenant") ?? Environment.GetEnvironmentVariable("QUEUEY_TENANT");
        if (string.IsNullOrWhiteSpace(tenant))
        {
            Console.Error.WriteLine("Missing --tenant <ten_...> (or QUEUEY_TENANT).");
            return ExitCodes.Usage;
        }

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
            {
                Console.Error.WriteLine($"No payload file at '{file}'.");
                return ExitCodes.RuntimeError;
            }
            payload = await File.ReadAllBytesAsync(file);
        }
        else
        {
            Console.Error.WriteLine("Missing payload: provide --data '<json>' or --file <path>.");
            return ExitCodes.Usage;
        }

        DateTimeOffset? occurredAt = null;
        if (map.Get("occurred-at") is { Length: > 0 } occurredRaw)
        {
            if (!DateTimeOffset.TryParse(occurredRaw, CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                Console.Error.WriteLine($"--occurred-at is not a valid ISO 8601 timestamp: '{occurredRaw}'.");
                return ExitCodes.Usage;
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

        var spool = OpenSpool(spoolPath);
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
                    pending = stats.PendingCount
                }, CliHost.JsonOut));
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
    /// Hosts the Edge transfer loop as a STANDALONE daemon — the complete
    /// Edge story for machines with no .NET application of their own:
    /// <c>queuey edge run</c> under systemd, and anything on the box
    /// publishes durably with <c>queuey edge publish</c> (or its own
    /// embedded Queuey.Edge). Runs until Ctrl-C / SIGTERM; the spool is the
    /// contract, so restarts resume exactly where they left off.
    /// </summary>
    private static async Task<int> RunHostAsync(string spoolPath, ArgMap map)
    {
        var tenant = map.Get("tenant") ?? Environment.GetEnvironmentVariable("QUEUEY_TENANT");
        var apiKey = map.Get("api-key") ?? Environment.GetEnvironmentVariable("QUEUEY_API_KEY");
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine(
                "edge run needs --tenant <ten_...> and --api-key <qak_...> " +
                "(or QUEUEY_TENANT / QUEUEY_API_KEY). Use a publish-only, workspace-scoped key.");
            return ExitCodes.Configuration;
        }

        var ingressBase = map.Get("ingress-base") ?? Environment.GetEnvironmentVariable("QUEUEY_INGRESS_BASE");

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

        builder.Services.AddQueueyEdge(o =>
        {
            o.ApiKey = apiKey;
            o.TenantPublicId = tenant;
            o.Storage.Path = spoolPath;
            o.LocalEndpoint.Port = listenPort;
            if (!string.IsNullOrWhiteSpace(ingressBase))
                o.IngressBaseAddress = new Uri(ingressBase);
            if (map.Get("source") is { Length: > 0 } source)
                o.Source = source;
        });

        Console.WriteLine($"Queuey Edge daemon. Spool: {spoolPath}");
        Console.WriteLine("Publish from anything on this machine with 'queuey edge publish …'; " +
                          "inspect with 'queuey edge status'. Ctrl-C to stop (accepted events survive restarts).");
        if (listenPort is { } lp)
        {
            Console.WriteLine(
                $"Local publish endpoint: POST http://localhost:{lp}/events/{tenant}/{{queue}} " +
                "(loopback only; same wire shape as cloud ingress — 202 = durably accepted locally).");
        }

        await builder.Build().RunAsync();
        return ExitCodes.Success;
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

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    spool = spoolPath,
                    pending = stats.PendingCount,
                    quarantined = stats.QuarantinedCount,
                    oldestPendingAgeSeconds = stats.OldestPendingAge?.TotalSeconds,
                    storageBytes = stats.StorageUsageBytes,
                    lastSettledAtUtc = stats.LastSettledAtUtc,
                    quarantinedEvents = quarantined
                }, CliHost.JsonOut));
                return ExitCodes.Success;
            }

            Console.WriteLine("Queuey Edge spool");
            Console.WriteLine($"  Spool        : {spoolPath}");
            Console.WriteLine($"  Pending      : {stats.PendingCount}");
            Console.WriteLine($"  Quarantined  : {stats.QuarantinedCount}");
            Console.WriteLine($"  Oldest age   : {(stats.OldestPendingAge is { } age ? Humanize(age) : "-")}");
            Console.WriteLine($"  Storage      : {stats.StorageUsageBytes / 1024} KiB");
            Console.WriteLine($"  Last settled : {stats.LastSettledAtUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "-"}");

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
    private static async Task<int> RecoverAsync(string spoolPath)
    {
        if (!File.Exists(spoolPath))
        {
            Console.Error.WriteLine($"No spool at '{spoolPath}'.");
            return ExitCodes.RuntimeError;
        }

        var salvagePath = spoolPath + ".salvage";
        var faultedPath = spoolPath + ".faulted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        long copied = 0, unreadable = 0;

        try
        {
            File.Delete(salvagePath);
            // Not Mode=ReadOnly: ATTACH must be able to CREATE the salvage
            // database on this connection. The source itself is only read.
            await using var source = new SqliteConnection($"Data Source={spoolPath}");
            await source.OpenAsync();
            await using (var attach = source.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE @path AS salvage;";
                attach.Parameters.AddWithValue("@path", salvagePath);
                await attach.ExecuteNonQueryAsync();
            }
            await using (var schema = source.CreateCommand())
            {
                schema.CommandText = """
                    CREATE TABLE salvage.spool AS SELECT * FROM main.spool WHERE 0;
                    """;
                await schema.ExecuteNonQueryAsync();
            }

            // Row-by-row so one unreadable page loses one row, not the batch.
            await using (var ids = source.CreateCommand())
            {
                ids.CommandText = "SELECT id FROM main.spool WHERE state IN ('Accepted','Claimed','Quarantined') ORDER BY id;";
                await using var reader = await ids.ExecuteReaderAsync();
                var all = new System.Collections.Generic.List<long>();
                while (await reader.ReadAsync()) all.Add(reader.GetInt64(0));

                foreach (var id in all)
                {
                    try
                    {
                        await using var copy = source.CreateCommand();
                        copy.CommandText = "INSERT INTO salvage.spool SELECT * FROM main.spool WHERE id = @id;";
                        copy.Parameters.AddWithValue("@id", id);
                        await copy.ExecuteNonQueryAsync();
                        copied++;
                    }
                    catch (SqliteException)
                    {
                        unreadable++;
                    }
                }
            }
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine(
                $"Salvage could not read the spool at all ({ex.Message}). " +
                "If the file is beyond reading, 'queuey edge reset --accept-data-loss' is the remaining option.");
            File.Delete(salvagePath);
            return ExitCodes.RuntimeError;
        }

        SqliteConnection.ClearAllPools();
        File.Move(spoolPath, faultedPath);
        foreach (var sidecar in new[] { spoolPath + "-wal", spoolPath + "-shm" })
            if (File.Exists(sidecar)) File.Move(sidecar, faultedPath + Path.GetExtension(sidecar));
        File.Move(salvagePath, spoolPath);

        // Rebuild indexes/user_version by letting the spool bootstrap treat
        // the salvage as data: rows carried over, schema completed on open.
        await using (var finish = new SqliteConnection($"Data Source={spoolPath}"))
        {
            await finish.OpenAsync();
            await using var cmd = finish.CreateCommand();
            cmd.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_spool_queue_transfer ON spool(queue_name, transfer_id);
                CREATE INDEX IF NOT EXISTS ix_spool_ready ON spool(state, next_attempt_utc, id);
                CREATE INDEX IF NOT EXISTS ix_spool_lane  ON spool(lane, state, id);
                CREATE TABLE IF NOT EXISTS edge_meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);
                UPDATE spool SET state = 'Accepted', claimed_until_utc = NULL WHERE state = 'Claimed';
                PRAGMA user_version = 1;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"Salvaged {copied} event(s) into a fresh spool; {unreadable} unreadable.");
        Console.WriteLine($"The faulted file is preserved at '{faultedPath}' for support.");
        return unreadable == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static int Reset(string spoolPath, bool acknowledged)
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

        SqliteConnection.ClearAllPools();
        var faultedPath = spoolPath + ".faulted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        File.Move(spoolPath, faultedPath);
        foreach (var sidecar in new[] { spoolPath + "-wal", spoolPath + "-shm" })
            if (File.Exists(sidecar)) File.Move(sidecar, faultedPath + Path.GetExtension(sidecar));

        Console.WriteLine($"Spool reset. The old file (and any events in it) is preserved at '{faultedPath}'.");
        Console.WriteLine("A fresh spool will be created on the next publish. Data loss acknowledged by --accept-data-loss.");
        return ExitCodes.Success;
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

    private static SqliteEventSpool OpenSpool(string spoolPath)
        => new(new EdgeStorageOptions { Path = spoolPath }, new CliClock());

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

    private static string Humanize(TimeSpan age) => age switch
    {
        { TotalDays: >= 1 } => $"{(int)age.TotalDays}d {age.Hours}h",
        { TotalHours: >= 1 } => $"{(int)age.TotalHours}h {age.Minutes}m",
        { TotalMinutes: >= 1 } => $"{(int)age.TotalMinutes}m {age.Seconds}s",
        _ => $"{(int)age.TotalSeconds}s"
    };

    private static int UnknownSub(string sub)
    {
        Console.Error.WriteLine($"Unknown edge subcommand '{sub}'. Expected: status | retry | discard | recover | reset.");
        return ExitCodes.Usage;
    }

    private sealed class CliClock : IEdgeClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long MonotonicMilliseconds => Environment.TickCount64;
    }
}
