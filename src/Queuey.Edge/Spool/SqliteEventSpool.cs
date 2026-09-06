using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Queuey.Edge;

/// <summary>
/// The boring durable spool: one SQLite file, WAL, <c>synchronous = FULL</c>.
/// "Boring" is the design goal — the local database is not the product.
///
/// <para>Durability: <see cref="SpoolDurability.Durable"/> means a committed
/// transaction under <c>synchronous = FULL</c> (fsync per commit; WAL +
/// NORMAL can lose the most recent commits on power loss, which is exactly
/// the event we just promised to own). Asserted by test via
/// <see cref="InspectDurabilityPragmas"/>, referenced nowhere else.</para>
///
/// <para>Corruption: any <c>SQLITE_CORRUPT/NOTADB</c> flips the spool to
/// FAULTED — operations throw <see cref="QueueyStorageFaultedException"/>,
/// the file is preserved untouched, and recovery is an explicit operator
/// action. Queuey never silently starts a fresh spool.</para>
///
/// <para>Deletion surface (rev 4 F4): settled cleanup after
/// <c>SettledRetention</c>, explicit operator discard — nothing else. Age
/// never deletes.</para>
///
/// <para>At rest (2026-08-29): the payload is blanked the moment a row is
/// settled (the row stays for correlation), freed pages are overwritten
/// (<c>secure_delete</c>), the file is owner-only on Unix, and payloads can
/// be AES-GCM encrypted with a customer-held key (<see cref="SpoolPayloadProtection"/>).
/// The device is the most exposed place a payload ever sits.</para>
///
/// <para>Capacity (2026-09-06): the limit is measured against LIVE pages
/// (<c>page_count - freelist_count</c>), never the file size. SQLite hands
/// freed pages to new rows before it grows the file, so a spool that hit
/// <c>MaxSpoolBytes</c> accepts again the moment its backlog settles —
/// settling blanks the payload, which is what frees the pages. Shrinking the
/// file back is the sweep's background job and never gates an accept.</para>
/// </summary>
public sealed class SqliteEventSpool : IEventSpool
{
    private const int CurrentSchemaVersion = 2;
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private readonly EdgeStorageOptions _options;
    private readonly IEdgeClock _clock;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private volatile string? _faultReason;
    private bool _bootstrapped;

    public SqliteEventSpool(EdgeStorageOptions options, IEdgeClock clock)
    {
        _options = options;
        _clock = clock;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    /// <summary>Null when healthy; the fault description once storage has faulted.</summary>
    public string? FaultReason => _faultReason;

    // ── accept ──────────────────────────────────────────────────────────

    public async Task<SpoolAccept> EnqueueAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GuardedAsync(conn =>
            {
                var (storedPayload, storedEnc) = ProtectForStorage(envelope.Payload);
                EnsureCapacityFor(conn, storedPayload.LongLength);

                var now = _clock.UtcNow;
                using var tx = conn.BeginTransaction();
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO spool (
                        envelope_version, transfer_id, queue_name, tenant_public_id, content_type,
                        payload, payload_enc, event_type, group_key, lane, source, occurred_at_utc,
                        enqueued_mono, state, attempts, next_attempt_utc, accepted_at_utc)
                    VALUES (
                        @version, @transferId, @queue, @tenant, @contentType,
                        @payload, @enc, @eventType, @groupKey, @lane, @source, @occurredAt,
                        @mono, 'Accepted', 0, @now, @now)
                    RETURNING id;
                    """;
                insert.Parameters.AddWithValue("@version", envelope.Version);
                insert.Parameters.AddWithValue("@transferId", envelope.TransferId);
                insert.Parameters.AddWithValue("@queue", envelope.Queue);
                insert.Parameters.AddWithValue("@tenant", envelope.TenantPublicId);
                insert.Parameters.AddWithValue("@contentType", envelope.ContentType);
                insert.Parameters.AddWithValue("@payload", storedPayload);
                insert.Parameters.AddWithValue("@enc", storedEnc);
                insert.Parameters.AddWithValue("@eventType", (object?)envelope.EventType ?? DBNull.Value);
                insert.Parameters.AddWithValue("@groupKey", (object?)envelope.GroupKey ?? DBNull.Value);
                insert.Parameters.AddWithValue("@lane", envelope.Lane);
                insert.Parameters.AddWithValue("@source", (object?)envelope.Source ?? DBNull.Value);
                insert.Parameters.AddWithValue("@occurredAt", Format(envelope.OccurredAtUtc));
                insert.Parameters.AddWithValue("@mono", _clock.MonotonicMilliseconds);
                insert.Parameters.AddWithValue("@now", Format(now));

                long id;
                try
                {
                    id = (long)insert.ExecuteScalar()!;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
                {
                    // Same (queue, transfer id) already accepted — the caller
                    // supplied their own idempotency key twice. One spool row,
                    // one identity: return the ORIGINAL accept.
                    tx.Rollback();
                    return LookupExistingAccept(conn, envelope);
                }

                // The commit is the durable boundary: under synchronous=FULL
                // this returns only after fsync. PublishAsync's promise IS
                // this line.
                tx.Commit();
                return new SpoolAccept(id, envelope.TransferId, now);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ── transfer-loop surface ───────────────────────────────────────────

    public Task<IReadOnlyList<ClaimedEvent>> ClaimReadyAsync(int maxLanes, TimeSpan lease, CancellationToken cancellationToken)
        => WriteLockedAsync<IReadOnlyList<ClaimedEvent>>(conn =>
        {
            var now = _clock.UtcNow;
            using var tx = conn.BeginTransaction();

            // The ready set: per lane, the HEAD (oldest row still owned by
            // Edge — Accepted or Claimed; Quarantined and Transferred rows do
            // not participate, which is exactly the step-aside rule). A lane
            // yields a claim only when its head is Accepted AND due: a head
            // in flight means the lane is busy (≤1 in-flight per lane = FIFO
            // at Cloud receive time), a head not yet due means the lane
            // waits (strict order, no overtaking).
            using var pick = conn.CreateCommand();
            pick.Transaction = tx;
            pick.CommandText = """
                SELECT s.id, s.envelope_version, s.transfer_id, s.queue_name, s.tenant_public_id,
                       s.content_type, s.payload, s.event_type, s.group_key, s.source,
                       s.occurred_at_utc, s.attempts, s.last_delay_ms, s.payload_enc
                FROM spool s
                WHERE s.state = 'Accepted'
                  AND s.next_attempt_utc <= @now
                  AND s.id = (SELECT MIN(id) FROM spool h
                              WHERE h.lane = s.lane AND h.state IN ('Accepted', 'Claimed'))
                ORDER BY s.id
                LIMIT @maxLanes;
                """;
            pick.Parameters.AddWithValue("@now", Format(now));
            pick.Parameters.AddWithValue("@maxLanes", maxLanes);

            var claims = new List<ClaimedEvent>();
            var unreadable = new List<(long Id, string Why)>();
            using (var reader = pick.ExecuteReader())
            {
                while (reader.Read())
                {
                    // A row this key cannot open is stepped aside like any other
                    // quarantine: never sent (the bytes would be wrong), never
                    // dropped (an operator with the right key can retry it).
                    if (!TryOpenPayload((byte[])reader.GetValue(6), reader.GetInt64(13), out var payload, out var why))
                    {
                        unreadable.Add((reader.GetInt64(0), why));
                        continue;
                    }

                    var envelope = new EventEnvelope(
                        Version: (int)reader.GetInt64(1),
                        TransferId: reader.GetString(2),
                        Queue: reader.GetString(3),
                        TenantPublicId: reader.GetString(4),
                        ContentType: reader.GetString(5),
                        Payload: payload,
                        OccurredAtUtc: Parse(reader.GetString(10)),
                        EventType: reader.IsDBNull(7) ? null : reader.GetString(7),
                        GroupKey: reader.IsDBNull(8) ? null : reader.GetString(8),
                        Source: reader.IsDBNull(9) ? null : reader.GetString(9));

                    claims.Add(new ClaimedEvent(
                        SpoolId: reader.GetInt64(0),
                        Envelope: envelope,
                        Attempts: (int)reader.GetInt64(11),
                        LastDelay: TimeSpan.FromMilliseconds(reader.GetInt64(12))));
                }
            }

            foreach (var (id, why) in unreadable)
            {
                using var park = conn.CreateCommand();
                park.Transaction = tx;
                park.CommandText = """
                    UPDATE spool
                    SET state = 'Quarantined', claimed_until_utc = NULL, attempts = attempts + 1,
                        last_class = 'PayloadUnreadable', last_reason = @why
                    WHERE id = @id;
                    """;
                park.Parameters.AddWithValue("@why", why);
                park.Parameters.AddWithValue("@id", id);
                park.ExecuteNonQuery();
            }

            if (claims.Count > 0)
            {
                using var claim = conn.CreateCommand();
                claim.Transaction = tx;
                claim.CommandText =
                    $"UPDATE spool SET state = 'Claimed', claimed_until_utc = @until WHERE id IN ({string.Join(",", claims.ConvertAll(c => c.SpoolId))});";
                claim.Parameters.AddWithValue("@until", Format(now + lease));
                claim.ExecuteNonQuery();
            }

            tx.Commit();
            return claims;
        }, cancellationToken);

    public Task SettleAsync(long spoolId, CloudAck ack, CancellationToken cancellationToken)
        => WriteLockedAsync<object?>(conn =>
        {
            // Cloud holds custody now: the payload leaves the device at once
            // (its pages return to the freelist, which is what lets a full
            // spool accept again), while the row stays for correlation
            // until SettledRetention sweeps it.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spool
                SET state = 'Transferred', claimed_until_utc = NULL, payload = X'',
                    cloud_event_id = @cloudEventId, transferred_utc = @at
                WHERE id = @id AND state <> 'Transferred';
                """;
            cmd.Parameters.AddWithValue("@cloudEventId", (object?)ack.CloudEventId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@at", Format(ack.AtUtc));
            cmd.Parameters.AddWithValue("@id", spoolId);
            cmd.ExecuteNonQuery();
            return null;
        }, cancellationToken);

    public Task RescheduleAsync(long spoolId, TransferOutcome outcome, DateTimeOffset nextAttemptUtc, TimeSpan attemptDelay, CancellationToken cancellationToken)
        => WriteLockedAsync<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spool
                SET state = 'Accepted', claimed_until_utc = NULL,
                    attempts = attempts + 1, next_attempt_utc = @next,
                    last_class = @class, last_reason = @reason,
                    last_status = @status, last_evidence = @evidence, last_delay_ms = @delay
                WHERE id = @id;
                """;
            AddOutcomeParameters(cmd, outcome);
            cmd.Parameters.AddWithValue("@next", Format(nextAttemptUtc));
            cmd.Parameters.AddWithValue("@delay", (long)attemptDelay.TotalMilliseconds);
            cmd.Parameters.AddWithValue("@id", spoolId);
            cmd.ExecuteNonQuery();
            return null;
        }, cancellationToken);

    public Task QuarantineAsync(long spoolId, TransferOutcome outcome, CancellationToken cancellationToken)
        => WriteLockedAsync<object?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spool
                SET state = 'Quarantined', claimed_until_utc = NULL,
                    attempts = attempts + 1,
                    last_class = @class, last_reason = @reason,
                    last_status = @status, last_evidence = @evidence
                WHERE id = @id;
                """;
            AddOutcomeParameters(cmd, outcome);
            cmd.Parameters.AddWithValue("@id", spoolId);
            cmd.ExecuteNonQuery();
            return null;
        }, cancellationToken);

    public Task<SpoolStats> GetStatsAsync(CancellationToken cancellationToken)
        => GuardedAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM spool WHERE state IN ('Accepted', 'Claimed')),
                    (SELECT COUNT(*) FROM spool WHERE state = 'Quarantined'),
                    (SELECT MIN(accepted_at_utc) FROM spool WHERE state IN ('Accepted', 'Claimed')),
                    (SELECT (page_count - freelist_count) * page_size
                     FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size()),
                    (SELECT MAX(transferred_utc) FROM spool WHERE state = 'Transferred'),
                    (SELECT MIN(s.next_attempt_utc) FROM spool s
                     WHERE s.state = 'Accepted'
                       AND s.id = (SELECT MIN(h.id) FROM spool h
                                   WHERE h.lane = s.lane AND h.state IN ('Accepted', 'Claimed')));
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();

            var oldest = reader.IsDBNull(2) ? (DateTimeOffset?)null : Parse(reader.GetString(2));
            return new SpoolStats(
                PendingCount: reader.GetInt64(0),
                QuarantinedCount: reader.GetInt64(1),
                OldestPendingAge: oldest is null ? null : _clock.UtcNow - oldest.Value,
                StorageUsageBytes: reader.GetInt64(3),
                LastSettledAtUtc: reader.IsDBNull(4) ? null : Parse(reader.GetString(4)),
                NextAttemptUtc: reader.IsDBNull(5) ? null : Parse(reader.GetString(5)));
        }, cancellationToken);

    public Task<int> SweepAsync(CancellationToken cancellationToken)
        => WriteLockedAsync(conn =>
        {
            var now = _clock.UtcNow;
            var touched = 0;

            // 1. Expired leases return to ready — the whole of restart
            //    recovery: a crashed holder's claims come back by timeout.
            using (var expire = conn.CreateCommand())
            {
                expire.CommandText = """
                    UPDATE spool SET state = 'Accepted', claimed_until_utc = NULL
                    WHERE state = 'Claimed' AND claimed_until_utc < @now;
                    """;
                expire.Parameters.AddWithValue("@now", Format(now));
                touched += expire.ExecuteNonQuery();
            }

            // 2. Settled cleanup — the ONLY deletion in the sweep. Accepted
            //    and Quarantined rows are never touched here, whatever their
            //    age (rev 4 F4).
            using (var delete = conn.CreateCommand())
            {
                delete.CommandText = "DELETE FROM spool WHERE state = 'Transferred' AND transferred_utc < @cutoff;";
                delete.Parameters.AddWithValue("@cutoff", Format(now - _options.SettledRetention));
                touched += delete.ExecuteNonQuery();
            }

            // 3. Give freed pages back to the OS. Bounded by TIME, not by a
            //    page count: a 512 MB spool that just drained must not take
            //    hours of 128-page nibbles to shrink, but the sweep holds the
            //    write gate, so it yields well before an accept would notice.
            ShrinkFile(conn);

            return touched;
        }, cancellationToken);

    public Task<int> KickAsync(CancellationToken cancellationToken)
        => WriteLockedAsync(conn =>
        {
            // Collapse waiting only: due-now + ladder reset. States, ids and
            // lane order are untouched, so FIFO and dedup semantics hold.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spool SET next_attempt_utc = @now, last_delay_ms = 0
                WHERE state = 'Accepted' AND next_attempt_utc > @now;
                """;
            cmd.Parameters.AddWithValue("@now", Format(_clock.UtcNow));
            return cmd.ExecuteNonQuery();
        }, cancellationToken);

    public Task<bool> RetryQuarantinedAsync(long spoolId, CancellationToken cancellationToken)
        => WriteLockedAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spool SET state = 'Accepted', next_attempt_utc = @now
                WHERE id = @id AND state = 'Quarantined';
                """;
            cmd.Parameters.AddWithValue("@now", Format(_clock.UtcNow));
            cmd.Parameters.AddWithValue("@id", spoolId);
            return cmd.ExecuteNonQuery() == 1;
        }, cancellationToken);

    public Task<bool> DiscardQuarantinedAsync(long spoolId, CancellationToken cancellationToken)
        => WriteLockedAsync(conn =>
        {
            // Explicit operator action — one of the three doors out of the
            // spool. Only quarantined rows are discardable; pending events
            // cannot be deleted through any API.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM spool WHERE id = @id AND state = 'Quarantined';";
            cmd.Parameters.AddWithValue("@id", spoolId);
            return cmd.ExecuteNonQuery() == 1;
        }, cancellationToken);

    // ── test hook ───────────────────────────────────────────────────────

    /// <summary>
    /// The durability pragmas of a spool-opened connection, for the test
    /// that freezes <c>synchronous = FULL</c>. Per-connection settings are
    /// invisible from outside, so the assertion must run on OUR connection.
    /// </summary>
    internal (string JournalMode, long Synchronous) InspectDurabilityPragmas()
    {
        using var conn = Open();
        using var journal = conn.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        var mode = (string)journal.ExecuteScalar()!;
        using var sync = conn.CreateCommand();
        sync.CommandText = "PRAGMA synchronous;";
        var level = (long)sync.ExecuteScalar()!;
        return (mode, level);
    }

    public Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
        => GuardedAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM edge_meta WHERE k = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }, cancellationToken);

    public Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
        => WriteLockedAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO edge_meta (k, v) VALUES ($k, $v) ON CONFLICT(k) DO UPDATE SET v = excluded.v;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
            return true;
        }, cancellationToken);

    // ── plumbing ────────────────────────────────────────────────────────

    private async Task<T> WriteLockedAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GuardedAsync(work, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private Task<T> GuardedAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        ThrowIfFaulted();
        return Task.Run(() =>
        {
            try
            {
                using var conn = Open();
                return work(conn);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                Fault($"SQLite reported corruption ({ex.SqliteErrorCode}): {ex.Message}");
                throw new QueueyStorageFaultedException(
                    $"The Edge spool at '{_options.Path}' is faulted: {_faultReason} " +
                    "The file has been preserved. Run 'queuey-edge recover' to salvage readable events " +
                    "into a fresh spool, or 'queuey-edge reset --accept-data-loss' to start clean.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 13 /* SQLITE_FULL */)
            {
                throw new QueueySpoolFullException(
                    $"The disk hosting the Edge spool at '{_options.Path}' is full. " +
                    "Queuey cannot take custody of new events until space is available; draining continues.");
            }
        }, ct);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);

        var directoryCreatedHere = false;
        if (!_bootstrapped)
        {
            var dir = Path.GetDirectoryName(_options.Path)!;
            directoryCreatedHere = !Directory.Exists(dir);
            Directory.CreateDirectory(dir);
        }

        conn.Open();

        if (!_bootstrapped)
            RestrictFileModes(directoryCreatedHere);

        using (var pragmas = conn.CreateCommand())
        {
            // FULL is the product, not tuning: WAL+NORMAL can lose the most
            // recent commits on power loss — exactly the events we promised
            // to own. Asserted by SpoolDurabilityTests.
            pragmas.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                PRAGMA secure_delete = ON;
                """;
            pragmas.ExecuteNonQuery();
        }

        if (!_bootstrapped)
        {
            Bootstrap(conn);
            _bootstrapped = true;
        }

        return conn;
    }

    private void Bootstrap(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            var verdict = (string)check.ExecuteScalar()!;
            if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Fault($"integrity_check failed: {verdict}");
                throw new SqliteException("database disk image is malformed", 11);
            }
        }

        long version;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            version = (long)read.ExecuteScalar()!;
        }

        switch (version)
        {
            case 0:
                CreateSchema(conn);
                break;
            case 1:
                MigrateV1ToV2(conn);
                break;
            case CurrentSchemaVersion:
                break;
            case > CurrentSchemaVersion:
                // A DOWNGRADE. Refusing loudly beats a best-effort read of a
                // schema this binary has never seen — silent best-effort is
                // how spools get corrupted.
                throw new Client.QueueyConfigurationException(
                    $"The Edge spool at '{_options.Path}' was written by a NEWER Queuey.Edge " +
                    $"(schema v{version}; this binary understands v{CurrentSchemaVersion}). " +
                    "Downgrades are not supported: upgrade the package, or drain with the newer version first.");
            default:
                // Forward-only migrations land here as future versions ship.
                break;
        }

        EnsureIncrementalVacuum(conn);
    }

    /// <summary>
    /// <c>auto_vacuum</c> is baked into the file header the moment the file
    /// gets its first page — and <c>Open</c> sets <c>journal_mode = WAL</c>
    /// before the schema exists, so a pragma inside <see cref="CreateSchema"/>
    /// was silently ignored: every v1 spool was written with
    /// <c>auto_vacuum = NONE</c>, where <c>incremental_vacuum</c> is a no-op
    /// and the file never shrinks (found 2026-09-06). Switching an existing
    /// file needs a one-time <c>VACUUM</c>. Best effort: a spool that cannot
    /// afford the rebuild right now (disk nearly full) still works — freed
    /// pages are reused either way — it just keeps its size until the next
    /// start.
    /// </summary>
    private static void EnsureIncrementalVacuum(SqliteConnection conn)
    {
        using var mode = conn.CreateCommand();
        mode.CommandText = "PRAGMA auto_vacuum;";
        if ((long)mode.ExecuteScalar()! == 2)
            return;

        try
        {
            using var convert = conn.CreateCommand();
            convert.CommandText = "PRAGMA auto_vacuum = INCREMENTAL; VACUUM;";
            convert.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (!IsCorruption(ex))
        {
            // Not fatal — see remarks. Corruption still propagates to the
            // caller's fault handling.
        }
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS spool (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                envelope_version  INTEGER NOT NULL,
                transfer_id       TEXT    NOT NULL,
                queue_name        TEXT    NOT NULL,
                tenant_public_id  TEXT    NOT NULL,
                content_type      TEXT    NOT NULL,
                payload           BLOB    NOT NULL,
                payload_enc       INTEGER NOT NULL DEFAULT 0,
                event_type        TEXT,
                group_key         TEXT,
                lane              TEXT    NOT NULL,
                source            TEXT,
                occurred_at_utc   TEXT    NOT NULL,
                enqueued_mono     INTEGER NOT NULL,
                state             TEXT    NOT NULL,
                attempts          INTEGER NOT NULL DEFAULT 0,
                next_attempt_utc  TEXT    NOT NULL,
                claimed_until_utc TEXT,
                last_class        TEXT,
                last_reason       TEXT,
                last_status       INTEGER,
                last_evidence     TEXT,
                last_delay_ms     INTEGER NOT NULL DEFAULT 0,
                cloud_event_id    TEXT,
                transferred_utc   TEXT,
                accepted_at_utc   TEXT    NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_spool_queue_transfer ON spool(queue_name, transfer_id);
            CREATE INDEX IF NOT EXISTS ix_spool_ready ON spool(state, next_attempt_utc, id);
            CREATE INDEX IF NOT EXISTS ix_spool_lane  ON spool(lane, state, id);

            CREATE TABLE IF NOT EXISTS edge_meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);

            PRAGMA user_version = {CurrentSchemaVersion};
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// v1 → v2: the payload_enc scheme column. Existing rows are plain (0).
    /// Idempotent on the column: a salvage copy of a v2 spool already carries
    /// it while its user_version says v1 (recover rebuilds the schema by
    /// letting bootstrap run), and ALTER ADD on an existing column is an error.
    /// </summary>
    private static void MigrateV1ToV2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('spool') WHERE name = 'payload_enc';";
            if ((long)probe.ExecuteScalar()! == 0)
            {
                using var add = conn.CreateCommand();
                add.Transaction = tx;
                add.CommandText = "ALTER TABLE spool ADD COLUMN payload_enc INTEGER NOT NULL DEFAULT 0;";
                add.ExecuteNonQuery();
            }
        }
        using (var bump = conn.CreateCommand())
        {
            bump.Transaction = tx;
            bump.CommandText = "PRAGMA user_version = 2;";
            bump.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private (byte[] Stored, long Scheme) ProtectForStorage(byte[] payload)
        => _options.PayloadKey is { } key
            ? (SpoolPayloadProtection.Protect(key, payload), SpoolPayloadProtection.SchemeAesGcmV1)
            : (payload, 0L);

    private bool TryOpenPayload(byte[] stored, long scheme, out byte[] payload, out string why)
    {
        payload = stored; why = string.Empty;
        if (scheme == 0) return true;
        if (scheme != SpoolPayloadProtection.SchemeAesGcmV1)
        {
            why = $"payload scheme {scheme} is unknown to this Queuey.Edge";
            return false;
        }
        if (_options.PayloadKey is not { } key)
        {
            why = "payload is encrypted at rest but no spool key is configured (QUEUEY_SPOOL_KEY)";
            return false;
        }
        try { payload = SpoolPayloadProtection.Unprotect(key, stored); return true; }
        catch (CryptographicException)
        {
            why = "payload could not be opened with the configured spool key (rotated key? use the key it was written with)";
            return false;
        }
    }

    // 0600 on the file (the WAL/SHM sidecars inherit it from SQLite), 0700 on
    // the directory only when this spool created it — never on a directory the
    // operator pointed at and may share. Best effort by design: a filesystem
    // that refuses modes must not refuse durability.
    private void RestrictFileModes(bool directoryCreatedHere)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            if (directoryCreatedHere && Path.GetDirectoryName(_options.Path) is { Length: > 0 } dir)
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(_options.Path))
                File.SetUnixFileMode(_options.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Test hook: is <c>secure_delete</c> on for spool connections (1 = on)?</summary>
    internal long InspectSecureDelete()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA secure_delete;";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Test hook: the raw stored bytes + scheme + state of one row, as the file holds them.</summary>
    internal (byte[] Payload, long Scheme, string State) InspectRow(long spoolId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload, payload_enc, state FROM spool WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", spoolId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException($"No spool row {spoolId}.");
        return ((byte[])reader.GetValue(0), reader.GetInt64(1), reader.GetString(2));
    }
///
    private static readonly TimeSpan ShrinkBudget = TimeSpan.FromMilliseconds(250);
    private const int ShrinkBatchPages = 256;

    private static void ShrinkFile(SqliteConnection conn)
    {
        var deadline = Environment.TickCount64 + (long)ShrinkBudget.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            using var free = conn.CreateCommand();
            free.CommandText = "SELECT freelist_count FROM pragma_freelist_count();";
            if ((long)free.ExecuteScalar()! == 0)
                return;

            using var vacuum = conn.CreateCommand();
            vacuum.CommandText = $"PRAGMA incremental_vacuum({ShrinkBatchPages});";
            vacuum.ExecuteNonQuery();
        }
    }

    private void EnsureCapacityFor(SqliteConnection conn, long incomingPayloadBytes)
    {
        // LIVE bytes, not file size: pages on the freelist (settled payloads,
        // swept rows) are reused by the next insert before the file grows,
        // so counting them as occupied would keep refusing accepts after the
        // backlog has already drained — exactly the recovery the limit must
        // not block.
        using var size = conn.CreateCommand();
        size.CommandText =
            "SELECT (page_count - freelist_count) * page_size " +
            "FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size();";
        var currentBytes = (long)size.ExecuteScalar()!;

        // Headroom stays reserved for BOOKKEEPING (settling transfers): a
        // spool that cannot record success re-sends forever. Accepting new
        // events stops before that margin is touched.
        if (currentBytes + incomingPayloadBytes > _options.MaxSpoolBytes - _options.HeadroomBytes)
        {
            throw new QueueySpoolFullException(
                $"The Edge spool at '{_options.Path}' has reached its configured limit " +
                $"({_options.MaxSpoolBytes / (1024 * 1024)} MB). Queuey cannot take custody of this event. " +
                "Draining continues; the limit is the honest backpressure of lossless retention — " +
                "raise MaxSpoolBytes or restore connectivity so the backlog drains.");
        }
    }

    private SpoolAccept LookupExistingAccept(SqliteConnection conn, EventEnvelope envelope)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, accepted_at_utc FROM spool WHERE queue_name = @queue AND transfer_id = @transferId;";
        cmd.Parameters.AddWithValue("@queue", envelope.Queue);
        cmd.Parameters.AddWithValue("@transferId", envelope.TransferId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new SpoolAccept(reader.GetInt64(0), envelope.TransferId, Parse(reader.GetString(1)));
    }

    private static void AddOutcomeParameters(SqliteCommand cmd, TransferOutcome outcome)
    {
        cmd.Parameters.AddWithValue("@class", outcome.Class.ToString());
        cmd.Parameters.AddWithValue("@reason", outcome.Reason.ToString());
        cmd.Parameters.AddWithValue("@status", (object?)outcome.Evidence?.StatusCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@evidence", (object?)outcome.Evidence?.Snippet ?? DBNull.Value);
    }

    private void ThrowIfFaulted()
    {
        if (_faultReason is { } reason)
        {
            throw new QueueyStorageFaultedException(
                $"The Edge spool at '{_options.Path}' is faulted: {reason} " +
                "The file has been preserved. Run 'queuey-edge recover' to salvage readable events " +
                "into a fresh spool, or 'queuey-edge reset --accept-data-loss' to start clean.");
        }
    }

    private void Fault(string reason) => _faultReason = reason;

    private static bool IsCorruption(SqliteException ex)
        => ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */;

    private static string Format(DateTimeOffset value)
        => value.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
