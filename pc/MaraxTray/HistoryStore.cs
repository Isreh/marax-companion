using Microsoft.Data.Sqlite;

namespace MaraxTray;

internal sealed record SessionRow(long Id, DateTime StartedAt, bool StartExact, int StartHx, DateTime? ReadyAt, DateTime LastSeen);

internal sealed record PumpRunRow(
    long SessionId,
    DateTime StartedAt,
    TimeSpan Duration,
    int HxStart,
    int HxMin,
    int SteamStart,
    string Mode,
    int BrewPackets = 0,
    int ValidPackets = 0);

// Keeps the collected data in an SQLite file. Every call uses its own connection,
// because the tray writes on the UI thread while the web server reads on its own threads.
internal sealed partial class HistoryStore
{
    private const int SchemaVersion = 2;

    private const string Schema = """
        CREATE TABLE sessions (
            id INTEGER PRIMARY KEY,
            started_at INTEGER NOT NULL,
            start_exact INTEGER NOT NULL,
            start_hx INTEGER NOT NULL,
            start_steam INTEGER NOT NULL,
            ready_at INTEGER,
            ended_at INTEGER,
            last_seen INTEGER NOT NULL,
            firmware TEXT NOT NULL
        );
        CREATE TABLE pump_runs (
            id INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            started_at INTEGER NOT NULL,
            duration_ms INTEGER NOT NULL,
            hx_start INTEGER NOT NULL,
            hx_min INTEGER NOT NULL,
            steam_start INTEGER NOT NULL,
            mode TEXT NOT NULL,
            brew_packets INTEGER NOT NULL DEFAULT 0,
            valid_packets INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE events (
            id INTEGER PRIMARY KEY,
            at INTEGER NOT NULL,
            kind TEXT NOT NULL,
            detail TEXT NOT NULL
        );
        CREATE TABLE samples (
            at INTEGER NOT NULL,
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            hx INTEGER NOT NULL,
            steam INTEGER NOT NULL,
            target INTEGER,
            boost INTEGER NOT NULL,
            heat INTEGER NOT NULL,
            pump INTEGER NOT NULL,
            mode TEXT NOT NULL,
            rssi INTEGER NOT NULL,
            bad INTEGER NOT NULL,
            raw TEXT NOT NULL
        );
        CREATE TABLE meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE INDEX sessions_started ON sessions(started_at);
        CREATE INDEX pump_runs_started ON pump_runs(started_at);
        CREATE INDEX pump_runs_session ON pump_runs(session_id);
        CREATE INDEX events_at ON events(at);
        CREATE INDEX samples_session ON samples(session_id, at);
        """;

    private readonly string connectionString;
    private readonly TraySettings settings;

    public HistoryStore(string path, TraySettings settings)
    {
        this.settings = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        using SqliteConnection connection = Open();
        Run(connection, null, "PRAGMA journal_mode = WAL");
        long version;
        using (SqliteCommand read = Prepare(connection, "PRAGMA user_version", []))
        {
            version = (long)read.ExecuteScalar()!;
        }
        switch (version)
        {
            case SchemaVersion:
                return;
            case 0:
                Run(connection, null, $"BEGIN; {Schema} PRAGMA user_version = {SchemaVersion}; COMMIT;");
                return;
            case 1:
                string backup = path + ".v1.bak";
                if (!File.Exists(backup))
                {
                    Run(connection, null, $"VACUUM INTO '{backup.Replace("'", "''")}'");
                }
                MigrateFromVersion1(connection);
                return;
            default:
                throw new NotSupportedException($"Die Datenbank {path} stammt von einer neueren Version der App.");
        }
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MaraxTray", "history.db");

    // Whether the ESP loses power together with the machine. Unknown until it has been observed.
    public bool? EspFollowsMachine
    {
        get
        {
            string? value = Query("SELECT value FROM meta WHERE key = 'esp_follows_machine'", r => r.GetString(0)).FirstOrDefault();
            return value is null ? null : value == "1";
        }
        set => Execute(
            "INSERT INTO meta (key, value) VALUES ('esp_follows_machine', @value) ON CONFLICT (key) DO UPDATE SET value = @value",
            ("@value", value == true ? "1" : "0"));
    }

    public SessionRow? LoadOpenSession() => Query(
        "SELECT id, started_at, start_exact, start_hx, ready_at, last_seen FROM sessions WHERE ended_at IS NULL ORDER BY id DESC LIMIT 1",
        r => new SessionRow(
            r.GetInt64(0),
            FromMs(r.GetInt64(1)),
            r.GetBoolean(2),
            r.GetInt32(3),
            r.IsDBNull(4) ? null : FromMs(r.GetInt64(4)),
            FromMs(r.GetInt64(5)))).FirstOrDefault();

    public long AddSession(DateTime startedAt, bool exact, int hx, int steam, string firmware, DateTime lastSeen) => (long)Scalar(
        """
        INSERT INTO sessions (started_at, start_exact, start_hx, start_steam, last_seen, firmware)
        VALUES (@started, @exact, @hx, @steam, @seen, @firmware) RETURNING id
        """,
        ("@started", ToMs(startedAt)), ("@exact", exact), ("@hx", hx), ("@steam", steam), ("@seen", ToMs(lastSeen)), ("@firmware", firmware))!;

    public void SetReady(long sessionId, DateTime at) =>
        Execute("UPDATE sessions SET ready_at = @at WHERE id = @id", ("@at", ToMs(at)), ("@id", sessionId));

    public void CloseSession(long sessionId, DateTime endedAt) => Execute(
        "UPDATE sessions SET ended_at = @at, last_seen = @at WHERE id = @id", ("@at", ToMs(endedAt)), ("@id", sessionId));

    public void AddPumpRun(PumpRunRow run) => Execute(
        """
        INSERT INTO pump_runs (session_id, started_at, duration_ms, hx_start, hx_min, steam_start, mode, brew_packets, valid_packets)
        VALUES (@session, @started, @duration, @hx, @min, @steam, @mode, @brew, @valid)
        """,
        ("@session", run.SessionId), ("@started", ToMs(run.StartedAt)), ("@duration", (long)run.Duration.TotalMilliseconds),
        ("@hx", run.HxStart), ("@min", run.HxMin), ("@steam", run.SteamStart), ("@mode", run.Mode),
        ("@brew", run.BrewPackets), ("@valid", run.ValidPackets));

    public void AddEvent(DateTime at, string kind, string detail) => Execute(
        "INSERT INTO events (at, kind, detail) VALUES (@at, @kind, @detail)",
        ("@at", ToMs(at)), ("@kind", kind), ("@detail", detail));

    public void AddSample(long sessionId, DateTime at, MaraxPacket packet) => Execute(
        """
        INSERT INTO samples (at, session_id, hx, steam, target, boost, heat, pump, mode, rssi, bad, raw)
        VALUES (@at, @session, @hx, @steam, @target, @boost, @heat, @pump, @mode, @rssi, @bad, @raw);
        UPDATE sessions SET last_seen = @at WHERE id = @session;
        """,
        ("@at", ToMs(at)), ("@session", sessionId), ("@hx", packet.HxTemp), ("@steam", packet.SteamTemp), ("@target", packet.SteamTarget),
        ("@boost", packet.BoostCountdown), ("@heat", packet.Heating), ("@pump", packet.Pump), ("@mode", packet.Mode),
        ("@rssi", packet.Rssi), ("@bad", packet.BadLines), ("@raw", packet.RawLine));

    internal static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    // Version 1 counted every pump run of the right length as a coffee, took the readings of
    // garbled lines and raised the water warning while the boiler filled. The raw lines in the
    // samples hold everything needed to redo that.
    private void MigrateFromVersion1(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        Run(connection, transaction, "ALTER TABLE pump_runs ADD COLUMN brew_packets INTEGER NOT NULL DEFAULT 0");
        Run(connection, transaction, "ALTER TABLE pump_runs ADD COLUMN valid_packets INTEGER NOT NULL DEFAULT 0");

        var samples = new List<(long Row, long Session, int Hx, int Steam, int? Target, int Boost, bool Heat, string Raw)>();
        using (SqliteCommand read = Prepare(connection, "SELECT rowid, session_id, hx, steam, target, boost, heat, raw FROM samples ORDER BY session_id, at", []))
        {
            read.Transaction = transaction;
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                samples.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6), reader.GetString(7)));
            }
        }

        // the same rule as PacketGuard: readings next to a garbled raw line repeat the last clean ones
        (long Session, int Hx, int Steam, int? Target, int Boost, bool Heat)? trusted = null;
        foreach (var sample in samples)
        {
            if (trusted?.Session != sample.Session || MaraxLine.TryParse(sample.Raw) is not null)
            {
                trusted = (sample.Session, sample.Hx, sample.Steam, sample.Target, sample.Boost, sample.Heat);
                continue;
            }
            var clean = trusted.Value;
            if ((clean.Hx, clean.Steam, clean.Target, clean.Boost, clean.Heat) != (sample.Hx, sample.Steam, sample.Target, sample.Boost, sample.Heat))
            {
                Run(connection, transaction,
                    "UPDATE samples SET hx = @hx, steam = @steam, target = @target, boost = @boost, heat = @heat WHERE rowid = @row",
                    ("@hx", clean.Hx), ("@steam", clean.Steam), ("@target", clean.Target), ("@boost", clean.Boost), ("@heat", clean.Heat),
                    ("@row", sample.Row));
            }
        }

        var runs = new List<(long Id, long Session, long Start, long Duration, int HxStart)>();
        using (SqliteCommand read = Prepare(connection, "SELECT id, session_id, started_at, duration_ms, hx_start FROM pump_runs", []))
        {
            read.Transaction = transaction;
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                runs.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4)));
            }
        }

        Run(connection, transaction, "DELETE FROM events WHERE kind IN ('coffee', 'water_missing', 'water_ok')");
        foreach (var run in runs)
        {
            var during = new List<(int Hx, string Raw)>();
            using (SqliteCommand read = Prepare(connection,
                "SELECT hx, raw FROM samples WHERE session_id = @session AND pump = 1 AND at BETWEEN @start AND @end ORDER BY at",
                [("@session", run.Session), ("@start", run.Start), ("@end", run.Start + run.Duration)]))
            {
                read.Transaction = transaction;
                using SqliteDataReader reader = read.ExecuteReader();
                while (reader.Read())
                {
                    during.Add((reader.GetInt32(0), reader.GetString(1)));
                }
            }

            int hxStart = during.Count > 0 ? during[0].Hx : run.HxStart;
            int hxMin = during.Count > 0 ? during.Min(s => s.Hx) : hxStart;
            bool?[] brewing = during.Select(s => MaraxLine.TryParse(s.Raw)?.Brewing).Where(b => b is not null).ToArray();
            int brew = brewing.Count(b => b == true);
            Run(connection, transaction,
                "UPDATE pump_runs SET hx_start = @start, hx_min = @min, brew_packets = @brew, valid_packets = @valid WHERE id = @id",
                ("@start", hxStart), ("@min", hxMin), ("@brew", brew), ("@valid", brewing.Length), ("@id", run.Id));

            string kind = PumpRunKind.Of(run.Duration, brew, brewing.Length, settings);
            string? eventKind = kind == PumpRunKind.Espresso ? "coffee" : kind == PumpRunKind.Water ? "hot_water" : null;
            if (eventKind is not null)
            {
                string detail = kind == PumpRunKind.Espresso ? $"{run.Duration / 1000.0:0} s, HX {hxStart} °C" : $"{run.Duration / 1000.0:0} s";
                Run(connection, transaction, "INSERT INTO events (at, kind, detail) VALUES (@at, @kind, @detail)",
                    ("@at", run.Start), ("@kind", eventKind), ("@detail", detail));
            }
        }

        Run(connection, transaction, $"PRAGMA user_version = {SchemaVersion}");
        transaction.Commit();
    }

    private static void Run(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Prepare(connection, sql, parameters);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        connection.CreateFunction("run_kind",
            (long duration, long brew, long valid) => PumpRunKind.Of(duration, brew, valid, settings), isDeterministic: true);
        return connection;
    }

    private static SqliteCommand Prepare(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Prepare(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Prepare(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters) =>
        Stream(sql, map, parameters).ToList();

    // Reads row by row, for exports that are too large to hold in memory.
    private IEnumerable<T> Stream<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Prepare(connection, sql, parameters);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return map(reader);
        }
    }
}
