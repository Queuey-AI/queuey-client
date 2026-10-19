using Microsoft.Data.Sqlite;

namespace Queuey.Edge.Tests.TestSupport;

/// <summary>
/// En spool-fil slik schema v2 skrev den (0.1.0-preview.12 og eldre): settlede rader ble stående i <c>spool</c> som
/// <c>Transferred</c> med tom payload. DDL-en er kopiert fra v2-koden, ikke fra dagens, så migreringen testes mot det
/// en ekte gammel fil inneholder. Lenket inn i CLI-testene også.
/// </summary>
public static class V2SpoolFile
{
    public sealed record Row(
        string TransferId,
        string State,
        string AcceptedAtUtc,
        string? TransferredUtc = null,
        string? CloudEventId = null,
        string Queue = "orders",
        string? GroupKey = null,
        string? LastReason = null);

    public static void Create(string path, params Row[] rows) => Write(path, migratedFromV1: false, rows);

    /// <summary>
    /// Samme v2-fil, slik en spool som startet på v1 ser ut: v1 → v2 la til <c>payload_enc</c> med ALTER TABLE, så
    /// kolonnen står sist, der en fersk spool har den som nummer åtte.
    /// </summary>
    public static void CreateMigratedFromV1(string path, params Row[] rows) => Write(path, migratedFromV1: true, rows);

    private static void Write(string path, bool migratedFromV1, Row[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                PRAGMA auto_vacuum = INCREMENTAL;
                PRAGMA journal_mode = WAL;
                CREATE TABLE spool (
                    id                INTEGER PRIMARY KEY AUTOINCREMENT,
                    envelope_version  INTEGER NOT NULL,
                    transfer_id       TEXT    NOT NULL,
                    queue_name        TEXT    NOT NULL,
                    tenant_public_id  TEXT    NOT NULL,
                    content_type      TEXT    NOT NULL,
                    payload           BLOB    NOT NULL,
                    {(migratedFromV1 ? "" : "payload_enc       INTEGER NOT NULL DEFAULT 0,")}
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
                {(migratedFromV1 ? "ALTER TABLE spool ADD COLUMN payload_enc INTEGER NOT NULL DEFAULT 0;" : "")}
                CREATE UNIQUE INDEX ux_spool_queue_transfer ON spool(queue_name, transfer_id);
                CREATE INDEX ix_spool_ready ON spool(state, next_attempt_utc, id);
                CREATE INDEX ix_spool_lane  ON spool(lane, state, id);
                CREATE TABLE edge_meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();

            foreach (var row in rows)
            {
                using var insert = conn.CreateCommand();
                insert.CommandText = """
                    INSERT INTO spool (envelope_version, transfer_id, queue_name, tenant_public_id, content_type,
                                       payload, group_key, lane, occurred_at_utc, enqueued_mono, state,
                                       next_attempt_utc, last_class, last_reason, cloud_event_id, transferred_utc,
                                       accepted_at_utc)
                    VALUES (1, @transferId, @queue, 'ten_test', 'application/json',
                            CASE WHEN @state = 'Transferred' THEN X'' ELSE X'7B7D' END,
                            @groupKey, @lane, @accepted, 0, @state,
                            @accepted, @lastClass, @lastReason, @cloudEventId, @transferred,
                            @accepted);
                    """;
                insert.Parameters.AddWithValue("@transferId", row.TransferId);
                insert.Parameters.AddWithValue("@queue", row.Queue);
                insert.Parameters.AddWithValue("@groupKey", (object?)row.GroupKey ?? DBNull.Value);
                insert.Parameters.AddWithValue("@lane", row.GroupKey is null ? row.Queue : row.Queue + "\u001f" + row.GroupKey);
                insert.Parameters.AddWithValue("@accepted", row.AcceptedAtUtc);
                insert.Parameters.AddWithValue("@state", row.State);
                insert.Parameters.AddWithValue("@lastClass", row.LastReason is null ? DBNull.Value : "EventRejected");
                insert.Parameters.AddWithValue("@lastReason", (object?)row.LastReason ?? DBNull.Value);
                insert.Parameters.AddWithValue("@cloudEventId", (object?)row.CloudEventId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@transferred", (object?)row.TransferredUtc ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();
    }

    public static long Scalar(string path, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
