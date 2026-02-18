using System.Globalization;
using Microsoft.Data.Sqlite;

sealed class SessionRepository
{
    private readonly string _connectionString;
    // SQLite operations are serialized to avoid cross-thread access issues for one shared DB file.
    private readonly object _sync = new();

    public SessionRepository(RuntimeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DbPath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = settings.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    public SessionRecord? GetActiveSession(string userName)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, user_name, started_at_ms, expires_at_ms
                FROM sessions
                WHERE user_name = $user_name AND ended_at_ms IS NULL
                LIMIT 1";
            cmd.Parameters.AddWithValue("$user_name", userName);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new SessionRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3)
            );
        }
    }

    public List<SessionRecord> ListExpiredActiveSessions(long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, user_name, started_at_ms, expires_at_ms
                FROM sessions
                WHERE ended_at_ms IS NULL AND expires_at_ms <= $now_ms
                ORDER BY expires_at_ms ASC";
            cmd.Parameters.AddWithValue("$now_ms", nowMs);

            using var reader = cmd.ExecuteReader();
            var result = new List<SessionRecord>();
            while (reader.Read())
            {
                result.Add(new SessionRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)
                ));
            }

            return result;
        }
    }

    public List<SoonEndingSessionRecord> ListSoonEndingCandidates(long nowMs, long deadlineMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, user_name, expires_at_ms
                FROM sessions
                WHERE ended_at_ms IS NULL
                  AND expires_at_ms > $now_ms
                  AND expires_at_ms <= $deadline_ms
                  AND (soon_notified_at_ms IS NULL OR soon_notified_at_ms = 0)
                ORDER BY expires_at_ms ASC";
            cmd.Parameters.AddWithValue("$now_ms", nowMs);
            cmd.Parameters.AddWithValue("$deadline_ms", deadlineMs);

            using var reader = cmd.ExecuteReader();
            var result = new List<SoonEndingSessionRecord>();
            while (reader.Read())
            {
                result.Add(new SoonEndingSessionRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2)
                ));
            }

            return result;
        }
    }

    public List<SessionHistoryRecord> ListSessionsIntersectingRange(string userName, long rangeStartMs, long rangeEndMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, started_at_ms, expires_at_ms, ended_at_ms, ended_reason
                FROM sessions
                WHERE user_name = $user_name
                  AND started_at_ms < $range_end_ms
                  AND COALESCE(ended_at_ms, expires_at_ms) > $range_start_ms
                ORDER BY started_at_ms ASC";
            cmd.Parameters.AddWithValue("$user_name", userName);
            cmd.Parameters.AddWithValue("$range_start_ms", rangeStartMs);
            cmd.Parameters.AddWithValue("$range_end_ms", rangeEndMs);

            using var reader = cmd.ExecuteReader();
            var result = new List<SessionHistoryRecord>();
            while (reader.Read())
            {
                result.Add(new SessionHistoryRecord(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)
                ));
            }

            return result;
        }
    }

    public long SumCompletedUsageMs(string userName, long startMs, long endMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(SUM(
                    CASE
                      WHEN ended_at_ms <= $start_ms OR started_at_ms >= $end_ms THEN 0
                      ELSE MIN(ended_at_ms, $end_ms) - MAX(started_at_ms, $start_ms)
                    END
                ), 0)
                FROM sessions
                WHERE user_name = $user_name
                  AND ended_at_ms IS NOT NULL
                  AND started_at_ms < $end_ms
                  AND ended_at_ms > $start_ms";
            cmd.Parameters.AddWithValue("$user_name", userName);
            cmd.Parameters.AddWithValue("$start_ms", startMs);
            cmd.Parameters.AddWithValue("$end_ms", endMs);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }
    }

    public void InsertSession(string userName, long startedAtMs, long expiresAtMs, long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO sessions (user_name, started_at_ms, expires_at_ms, ended_at_ms, ended_reason, soon_notified_at_ms, created_at_ms, updated_at_ms)
                VALUES ($user_name, $started_at_ms, $expires_at_ms, NULL, NULL, NULL, $now_ms, $now_ms)";
            cmd.Parameters.AddWithValue("$user_name", userName);
            cmd.Parameters.AddWithValue("$started_at_ms", startedAtMs);
            cmd.Parameters.AddWithValue("$expires_at_ms", expiresAtMs);
            cmd.Parameters.AddWithValue("$now_ms", nowMs);
            cmd.ExecuteNonQuery();
        }
    }

    public void ExtendSession(long id, long expiresAtMs, long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE sessions
                SET expires_at_ms = $expires_at_ms, soon_notified_at_ms = NULL, updated_at_ms = $now_ms
                WHERE id = $id AND ended_at_ms IS NULL";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$expires_at_ms", expiresAtMs);
            cmd.Parameters.AddWithValue("$now_ms", nowMs);
            cmd.ExecuteNonQuery();
        }
    }

    public bool MarkSoonEndingNotified(long id, long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE sessions
                SET soon_notified_at_ms = $soon_notified_at_ms, updated_at_ms = $now_ms
                WHERE id = $id
                  AND ended_at_ms IS NULL
                  AND (soon_notified_at_ms IS NULL OR soon_notified_at_ms = 0)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$soon_notified_at_ms", nowMs);
            cmd.Parameters.AddWithValue("$now_ms", nowMs);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool EndSession(long id, long endedAtMs, string reason, long nowMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE sessions
                SET ended_at_ms = $ended_at_ms, ended_reason = $reason, updated_at_ms = $now_ms
                WHERE id = $id AND ended_at_ms IS NULL";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ended_at_ms", endedAtMs);
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$now_ms", nowMs);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public List<AuditLogRecord> ListAuditEventsIntersectingRange(string userName, long rangeStartMs, long rangeEndMs, int limit)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, action, requested_window_minutes, granted_window_minutes, granted_until_ms, details, occurred_at_ms
                FROM audit_log
                WHERE target_user = $user_name
                  AND occurred_at_ms >= $range_start_ms
                  AND occurred_at_ms < $range_end_ms
                ORDER BY occurred_at_ms DESC, id DESC
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$user_name", userName);
            cmd.Parameters.AddWithValue("$range_start_ms", rangeStartMs);
            cmd.Parameters.AddWithValue("$range_end_ms", rangeEndMs);
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));

            using var reader = cmd.ExecuteReader();
            var result = new List<AuditLogRecord>();
            while (reader.Read())
            {
                result.Add(new AuditLogRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(6)
                ));
            }

            return result;
        }
    }

    public void InsertAuditEvent(
        string targetUser,
        string action,
        int? requestedWindowMinutes,
        int? grantedWindowMinutes,
        long? grantedUntilMs,
        string? details,
        long occurredAtMs)
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO audit_log (
                  target_user,
                  action,
                  actor,
                  requested_window_minutes,
                  granted_window_minutes,
                  granted_until_ms,
                  details,
                  occurred_at_ms
                )
                VALUES (
                  $target_user,
                  $action,
                  $actor,
                  $requested_window_minutes,
                  $granted_window_minutes,
                  $granted_until_ms,
                  $details,
                  $occurred_at_ms
                )";

            cmd.Parameters.AddWithValue("$target_user", targetUser);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$actor", string.Empty);
            cmd.Parameters.AddWithValue("$requested_window_minutes", (object?)requestedWindowMinutes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$granted_window_minutes", (object?)grantedWindowMinutes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$granted_until_ms", (object?)grantedUntilMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$occurred_at_ms", occurredAtMs);
            cmd.ExecuteNonQuery();
        }
    }

    private void Initialize()
    {
        lock (_sync)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sessions (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  user_name TEXT NOT NULL,
                  started_at_ms INTEGER NOT NULL,
                  expires_at_ms INTEGER NOT NULL,
                  ended_at_ms INTEGER,
                  ended_reason TEXT,
                  soon_notified_at_ms INTEGER,
                  created_at_ms INTEGER NOT NULL,
                  updated_at_ms INTEGER NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_sessions_active_user
                  ON sessions (user_name)
                  WHERE ended_at_ms IS NULL;

                CREATE INDEX IF NOT EXISTS idx_sessions_user_start
                  ON sessions (user_name, started_at_ms);

                CREATE TABLE IF NOT EXISTS audit_log (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  target_user TEXT NOT NULL,
                  action TEXT NOT NULL,
                  actor TEXT NOT NULL,
                  requested_window_minutes INTEGER,
                  granted_window_minutes INTEGER,
                  granted_until_ms INTEGER,
                  details TEXT,
                  occurred_at_ms INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_audit_log_user_time
                  ON audit_log (target_user, occurred_at_ms DESC);

                CREATE INDEX IF NOT EXISTS idx_audit_log_time
                  ON audit_log (occurred_at_ms DESC);
            ";
            cmd.ExecuteNonQuery();

            // Backward-compatible migration for existing DBs created before this column was introduced.
            EnsureColumnExists(conn, "sessions", "soon_notified_at_ms", "INTEGER");
        }
    }

    private static void EnsureColumnExists(SqliteConnection conn, string tableName, string columnName, string columnTypeSql)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = checkCmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnTypeSql}";
        alterCmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
