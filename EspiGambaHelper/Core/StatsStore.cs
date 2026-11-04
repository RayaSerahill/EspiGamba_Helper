using Microsoft.Data.Sqlite;

namespace EspiGambaHelper.Core;

public enum AddOutcome
{
    Added,
    Linked,
    Duplicate,
}

public sealed record ImportResult(int Added = 0, int Linked = 0, int Duplicates = 0)
{
    public ImportResult With(AddOutcome outcome) => outcome switch
    {
        AddOutcome.Added => this with { Added = Added + 1 },
        AddOutcome.Linked => this with { Linked = Linked + 1 },
        _ => this with { Duplicates = Duplicates + 1 },
    };

    public override string ToString() => $"{Added} new, {Linked} updated, {Duplicates} already recorded";
}

public sealed record SourceTotals(GameSource Source, int Games, long Collected, long PaidOut)
{
    public long Profit => Collected - PaidOut;
}

public sealed class StatsSummary(IReadOnlyList<SourceTotals> bySource)
{
    public IReadOnlyList<SourceTotals> BySource { get; } = bySource;
    public int Games => BySource.Sum(t => t.Games);
    public long Collected => BySource.Sum(t => t.Collected);
    public long PaidOut => BySource.Sum(t => t.PaidOut);
    public long Profit => Collected - PaidOut;
    public SourceTotals For(GameSource source) => BySource.FirstOrDefault(t => t.Source == source) ?? new SourceTotals(source, 0, 0, 0);
}

/// <summary>
/// SQLite history of every recorded game. Safe to call from any thread: live IPC events arrive on the sending
/// plugin's threads while the UI reads summaries on the game thread, so one connection is guarded by a lock.
/// </summary>
public sealed class StatsStore : IDisposable
{
    /// <summary>How far apart a live row and its archive row may be and still be the same game.</summary>
    public static readonly TimeSpan LinkWindow = TimeSpan.FromDays(30);

    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private SqliteTransaction? transaction;
    private bool disposed;

    public StatsStore(string connectionString)
    {
        connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute("""
            CREATE TABLE IF NOT EXISTS games (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source INTEGER NOT NULL,
                key TEXT NOT NULL,
                played_at INTEGER NOT NULL,
                player TEXT NOT NULL,
                collected INTEGER NOT NULL,
                paid_out INTEGER NOT NULL,
                live INTEGER NOT NULL,
                match_key TEXT,
                details TEXT NOT NULL,
                recorded_at INTEGER NOT NULL,
                host TEXT NOT NULL DEFAULT '',
                UNIQUE (source, key)
            );
            CREATE INDEX IF NOT EXISTS games_played_at ON games (played_at);
            CREATE INDEX IF NOT EXISTS games_match ON games (source, match_key);
            """);
        if (!HasColumn("games", "host")) AddHostColumn();
    }

    private bool HasColumn(string table, string column)
    {
        using var command = Command($"PRAGMA table_info({table})");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Databases written before 0.2.4.0 have no host column; rebuild it from the payloads the rows kept.</summary>
    private void AddHostColumn()
    {
        Execute("ALTER TABLE games ADD COLUMN host TEXT NOT NULL DEFAULT ''");
        var hosts = new List<(long Id, string Host)>();
        using (var command = Command("SELECT id, source, details FROM games WHERE source <> $blackjack", ("$blackjack", (int)GameSource.Blackjack)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string host;
                try
                {
                    host = (GameSource)reader.GetInt32(1) == GameSource.Wheel
                        ? WheelGames.HostFromDetails(reader.GetString(2))
                        : ScratchGames.HostFromDetails(reader.GetString(2));
                }
                catch (System.Text.Json.JsonException) { continue; }
                if (host.Length > 0) hosts.Add((reader.GetInt64(0), host));
            }
        }
        foreach (var (id, host) in hosts)
            Execute("UPDATE games SET host = $host WHERE id = $id", ("$host", host), ("$id", id));
    }

    public static StatsStore Open(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        // No pooling: the file must close when the plugin unloads, not when the pool is cleared.
        return new StatsStore(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    }

    public static StatsStore InMemory() => new("Data Source=:memory:");

    public AddOutcome Add(GameRecord record)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return AddLocked(record);
        }
    }

    public ImportResult AddRange(IEnumerable<GameRecord> records)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using (transaction = connection.BeginTransaction())
            {
                try
                {
                    var result = new ImportResult();
                    foreach (var record in records) result = result.With(AddLocked(record));
                    transaction.Commit();
                    return result;
                }
                finally
                {
                    transaction = null;
                }
            }
        }
    }

    private AddOutcome AddLocked(GameRecord record)
    {
        var playedAt = record.PlayedAt.ToUnixTimeMilliseconds();
        var exists = Scalar("SELECT COUNT(*) FROM games WHERE source = $source AND key = $key",
            ("$source", (int)record.Source), ("$key", record.Key)) is long count && count > 0;
        if (exists) return AddOutcome.Duplicate;
        if (!record.Live && record.MatchKey != null)
        {
            // The archive row of a game first seen live carries the same content under a different id:
            // upgrade that row (archives know prices the live event lacks) instead of counting the game twice.
            var liveId = Scalar("""
                SELECT id FROM games
                WHERE source = $source AND match_key = $match AND live = 1 AND ABS(played_at - $playedAt) <= $window
                ORDER BY ABS(played_at - $playedAt) LIMIT 1
                """,
                ("$source", (int)record.Source), ("$match", record.MatchKey), ("$playedAt", playedAt),
                ("$window", (long)LinkWindow.TotalMilliseconds));
            if (liveId is long id)
            {
                // The archive also knows the host, which a live scratch row may have missed.
                Execute("""
                    UPDATE games SET key = $key, collected = $collected, paid_out = $paidOut, details = $details, live = 0,
                        host = CASE WHEN $host <> '' THEN $host ELSE host END
                    WHERE id = $id
                    """,
                    ("$key", record.Key), ("$collected", record.Collected), ("$paidOut", record.PaidOut),
                    ("$details", record.Details), ("$host", record.Host), ("$id", id));
                return AddOutcome.Linked;
            }
        }
        Execute("""
            INSERT INTO games (source, key, played_at, player, collected, paid_out, live, match_key, details, recorded_at, host)
            VALUES ($source, $key, $playedAt, $player, $collected, $paidOut, $live, $match, $details, $recordedAt, $host)
            """,
            ("$source", (int)record.Source), ("$key", record.Key), ("$playedAt", playedAt), ("$player", record.Player),
            ("$collected", record.Collected), ("$paidOut", record.PaidOut), ("$live", record.Live ? 1 : 0),
            ("$match", (object?)record.MatchKey ?? DBNull.Value), ("$details", record.Details),
            ("$recordedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$host", record.Host));
        return AddOutcome.Added;
    }

    /// <summary>
    /// Rows in the range that <paramref name="hostedBy"/> may see: blackjack always (SimpleBlackjack keeps no shared stats),
    /// scratch and wheel only when hosted by that character ("Name" or "Name@World", case-insensitive). Null means every row.
    /// </summary>
    private const string RangeAndHost = """
        played_at >= $from AND played_at < $to AND ($anyHost = 1 OR source = 0 OR ($host <> '' AND
            (host = $host COLLATE NOCASE OR substr(host, 1, length($host) + 1) = ($host || '@') COLLATE NOCASE)))
        """;

    private static (string Name, object Value)[] RangeParameters(DateTimeOffset from, DateTimeOffset to, string? hostedBy) =>
    [
        ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()),
        ("$anyHost", hostedBy == null ? 1 : 0), ("$host", hostedBy?.Trim() ?? ""),
    ];

    /// <summary>Totals per game for rows played at or after <paramref name="from"/> and before <paramref name="to"/>.</summary>
    public StatsSummary Summarize(DateTimeOffset from, DateTimeOffset to, string? hostedBy = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var command = Command($"""
                SELECT source, COUNT(*), COALESCE(SUM(collected), 0), COALESCE(SUM(paid_out), 0) FROM games
                WHERE {RangeAndHost} GROUP BY source ORDER BY source
                """, RangeParameters(from, to, hostedBy));
            using var reader = command.ExecuteReader();
            var totals = new List<SourceTotals>();
            while (reader.Read())
                totals.Add(new SourceTotals((GameSource)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
            return new StatsSummary(totals);
        }
    }

    /// <summary>Newest rows first within the range.</summary>
    public List<GameRecord> Recent(DateTimeOffset from, DateTimeOffset to, int limit, string? hostedBy = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var command = Command($"""
                SELECT source, key, played_at, player, collected, paid_out, live, match_key, details, host FROM games
                WHERE {RangeAndHost} ORDER BY played_at DESC, id DESC LIMIT $limit
                """, [.. RangeParameters(from, to, hostedBy), ("$limit", limit)]);
            using var reader = command.ExecuteReader();
            var records = new List<GameRecord>();
            while (reader.Read())
                records.Add(new GameRecord((GameSource)reader.GetInt32(0), reader.GetString(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), reader.GetString(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetString(8), reader.GetInt32(6) != 0,
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(9)));
            return records;
        }
    }

    public long Count()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Scalar("SELECT COUNT(*) FROM games") as long? ?? 0;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        // Microsoft.Data.Sqlite refuses to run a command that is not attached to the connection's open transaction.
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            connection.Dispose();
        }
    }
}
