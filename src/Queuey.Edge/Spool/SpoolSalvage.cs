using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Queuey.Edge;

/// <summary>
/// The salvage behind <c>queuey edge recover</c>: every still-owned row a
/// faulted spool can still read, copied into a new file the operator swaps in.
///
/// <para>The new file gets the spool's own schema
/// (<see cref="SqliteEventSpool.CreateSchema"/>), never a copy of the faulted
/// file's. Until 2026-10-19 the salvage was <c>CREATE TABLE … AS SELECT</c>,
/// which drops every constraint: <c>id</c> lost its
/// <c>INTEGER PRIMARY KEY AUTOINCREMENT</c>, the next accept got id NULL and
/// threw, and a recovered spool could not take a single new event.</para>
///
/// <para>Rows are copied by column name, ids included. On a spool that began
/// as v1, <c>payload_enc</c> is the last column (v2 added it with ALTER
/// TABLE), where the current schema has it eighth; a copy by position would
/// shift every value after the payload.</para>
///
/// <para>Not carried over: the <c>settled</c> correlations (a repeat of an
/// event settled before the fault is accepted again, and Cloud deduplicates
/// it on its transfer id) and <c>edge_meta</c>.</para>
/// </summary>
internal static class SpoolSalvage
{
    internal readonly record struct Result(long Copied, long Unreadable);

    /// <summary>
    /// Writes the salvage of <paramref name="faultedPath"/> to
    /// <paramref name="salvagePath"/> (replacing any file there); no row of
    /// the faulted file changes. A row that cannot be read is counted and
    /// skipped.
    /// Throws <see cref="SqliteException"/> when the faulted file cannot be
    /// read at all, and <see cref="Client.QueueyConfigurationException"/> when
    /// a newer Queuey.Edge wrote it.
    /// </summary>
    internal static Result Copy(string faultedPath, string salvagePath)
    {
        CreateOwnerOnly(salvagePath);

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = salvagePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        conn.Open();

        // auto_vacuum is fixed once the file has its first page. Set before
        // the schema, it spares the first open a full-file VACUUM.
        Execute(conn, "PRAGMA auto_vacuum = INCREMENTAL;");
        SqliteEventSpool.CreateSchema(conn);

        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE @path AS faulted;";
            attach.Parameters.AddWithValue("@path", faultedPath);
            attach.ExecuteNonQuery();
        }

        RefuseNewerSchema(conn, faultedPath);

        var columns = string.Join(", ", SharedColumns(conn).Select(c => "\"" + c + "\""));
        long copied = 0, unreadable = 0;

        // Row by row so one unreadable page loses one row, not the batch.
        foreach (var id in OwnedIds(conn))
        {
            try
            {
                using var copy = conn.CreateCommand();
                copy.CommandText = $"INSERT INTO main.spool ({columns}) SELECT {columns} FROM faulted.spool WHERE id = @id;";
                copy.Parameters.AddWithValue("@id", id);
                copy.ExecuteNonQuery();
                copied++;
            }
            catch (SqliteException)
            {
                unreadable++;
            }
        }

        // A claim is a lease held by a process that is gone.
        Execute(conn, "UPDATE main.spool SET state = 'Accepted', claimed_until_utc = NULL WHERE state = 'Claimed';");
        ContinueIdSequence(conn);

        return new Result(copied, unreadable);
    }

    /// <summary>
    /// The salvage holds the spool's payloads, so it is owner-only from its
    /// first byte, as the spool file is. Best effort, as for the spool: some
    /// filesystems (FAT on an SD card) have no Unix modes. The journal of an
    /// earlier salvage that crashed goes first: SQLite would roll it back
    /// into the new file.
    /// </summary>
    private static void CreateOwnerOnly(string path)
    {
        File.Delete(path + "-journal");
        using (new FileStream(path, FileMode.Create, FileAccess.Write)) { }
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A newer schema may hold columns this binary does not know, and the copy
    /// by name would drop them without a word. The newer CLI recovers it.
    /// </summary>
    private static void RefuseNewerSchema(SqliteConnection conn, string faultedPath)
    {
        using var read = conn.CreateCommand();
        read.CommandText = "PRAGMA faulted.user_version;";
        var version = (long)read.ExecuteScalar()!;
        if (version > SqliteEventSpool.CurrentSchemaVersion)
        {
            throw new Client.QueueyConfigurationException(
                $"The spool at '{faultedPath}' was written by a NEWER Queuey.Edge " +
                $"(schema v{version}; this binary understands v{SqliteEventSpool.CurrentSchemaVersion}). " +
                "Recover it with the queuey CLI of that version.");
        }
    }

    /// <summary>The current schema's columns that the faulted file also has, in the current schema's order.</summary>
    private static List<string> SharedColumns(SqliteConnection conn)
    {
        var faulted = new HashSet<string>(TableColumns(conn, "faulted"), StringComparer.OrdinalIgnoreCase);
        return TableColumns(conn, "main").Where(faulted.Contains).ToList();
    }

    private static List<string> TableColumns(SqliteConnection conn, string schema)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info(spool);";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    private static List<long> OwnedIds(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM faulted.spool WHERE state IN ('Accepted','Claimed','Quarantined') ORDER BY id;";
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// AUTOINCREMENT never hands out an id twice; this carries that over from
    /// the faulted file. Without it the next accept would reuse the id of an
    /// event that settled before the fault, and the file kept for support
    /// would name a different event by the same id. Best effort: an
    /// unreadable <c>sqlite_sequence</c> costs only this, never the salvage.
    /// </summary>
    private static void ContinueIdSequence(SqliteConnection conn)
    {
        try
        {
            Execute(conn, """
                UPDATE main.sqlite_sequence
                SET seq = MAX(seq, COALESCE((SELECT MAX(seq) FROM faulted.sqlite_sequence WHERE name = 'spool'), 0))
                WHERE name = 'spool';
                INSERT INTO main.sqlite_sequence (name, seq)
                SELECT 'spool', seq FROM faulted.sqlite_sequence
                WHERE name = 'spool' AND NOT EXISTS (SELECT 1 FROM main.sqlite_sequence WHERE name = 'spool')
                ORDER BY seq DESC LIMIT 1;
                """);
        }
        catch (SqliteException) { }
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
