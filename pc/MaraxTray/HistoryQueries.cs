using Microsoft.Data.Sqlite;

namespace MaraxTray;

internal sealed record SummaryInfo(
    int CoffeesToday,
    int HotWaterToday,
    long? FirstStartToday,
    bool FirstStartExact,
    int? HeatupToday,
    int RuntimeToday,
    int Coffees,
    int HotWater,
    int Sessions,
    int? AvgHeatupSeconds,
    double? AvgShotSeconds,
    int RuntimeSeconds,
    long? LastCoffeeAt);

internal sealed record SessionInfo(
    long Id,
    long StartedAt,
    bool StartExact,
    int StartHx,
    long? ReadyAt,
    int? HeatupSeconds,
    long? EndedAt,
    long LastSeen,
    int RuntimeSeconds,
    int Coffees,
    int PumpRuns,
    bool ColdStart);

internal sealed record ShotInfo(
    long Id,
    long SessionId,
    long StartedAt,
    double DurationSeconds,
    int HxStart,
    int HxMin,
    int SteamStart,
    string Mode,
    string Kind,
    string KindLabel);

internal sealed record EventInfo(long At, string Kind, string Label, string Detail);

internal sealed record DayInfo(string Day, int Coffees);

internal sealed record HourInfo(int Weekday, int Hour, int Coffees);

internal sealed record SampleInfo(long At, int Hx, int Steam, int? Target, bool Heat, bool Pump);

internal sealed record SessionDetail(SessionInfo Session, IReadOnlyList<SampleInfo> Samples, IReadOnlyList<ShotInfo> Shots);

internal sealed partial class HistoryStore
{
    // run_kind is PumpRunKind.Of, registered on every connection
    private const string Kind = "run_kind(duration_ms, brew_packets, valid_packets)";
    private const string IsCoffee = $"{Kind} = 'espresso'";
    private const string IsDraw = $"{Kind} IN ('espresso', 'water')";

    private const string SessionSelect = """
        SELECT s.id, s.started_at, s.start_exact, s.start_hx, s.ready_at,
            CASE WHEN s.start_exact = 1 AND s.ready_at IS NOT NULL THEN (s.ready_at - s.started_at) / 1000 END,
            s.ended_at, s.last_seen,
            (COALESCE(s.ended_at, s.last_seen) - s.started_at) / 1000,
            (SELECT COUNT(*) FROM pump_runs p WHERE p.session_id = s.id AND run_kind(p.duration_ms, p.brew_packets, p.valid_packets) = 'espresso'),
            (SELECT COUNT(*) FROM pump_runs p WHERE p.session_id = s.id),
            s.start_hx < @cold
        FROM sessions s
        """;

    private const string ShotSelect = $"""
        SELECT id, session_id, started_at, duration_ms, hx_start, hx_min, steam_start, mode, {Kind} FROM pump_runs
        """;

    // The first day of a range of days, counted in the local time of this PC. 0 means everything.
    internal static long RangeStart(int days) =>
        days <= 0 ? 0 : new DateTimeOffset(DateTime.Today.AddDays(1 - days)).ToUnixTimeMilliseconds();

    internal static string EventLabel(string kind) => kind switch
    {
        "machine_on" => "Eingeschaltet",
        "ready" => "Aufgeheizt",
        "coffee" => "Espresso bezogen",
        "hot_water" => "Heißwasser",
        "machine_off" => "Ausgeschaltet",
        "water_missing" => "Wassertank prüfen",
        "water_ok" => "Wasser wieder in Ordnung",
        "esp_restart" => "ESP neu gestartet",
        _ => kind,
    };

    public int CoffeesToday() => Count(PumpRunKind.Espresso, RangeStart(1));

    public SummaryInfo Summary(int days)
    {
        long today = RangeStart(1);
        long from = RangeStart(days);

        SessionInfo? first = Sessions(1, int.MaxValue).LastOrDefault();
        int runtimeToday = Convert.ToInt32(Scalar(
            "SELECT COALESCE(SUM(COALESCE(ended_at, last_seen) - MAX(started_at, @from)), 0) / 1000 FROM sessions WHERE COALESCE(ended_at, last_seen) >= @from",
            ("@from", today)));

        (int sessions, int runtime) = Query(
            "SELECT COUNT(*), COALESCE(SUM(COALESCE(ended_at, last_seen) - started_at), 0) / 1000 FROM sessions WHERE started_at >= @from",
            r => (r.GetInt32(0), r.GetInt32(1)),
            ("@from", from))[0];

        object? avgHeatup = Scalar(
            """
            SELECT AVG(ready_at - started_at) / 1000 FROM sessions
            WHERE started_at >= @from AND start_exact = 1 AND ready_at IS NOT NULL AND start_hx < @cold
            """,
            ("@from", from), ("@cold", settings.ColdStartHxTemp));

        object? avgShot = Scalar($"SELECT AVG(duration_ms) / 1000.0 FROM pump_runs WHERE started_at >= @from AND {IsCoffee}", ("@from", from));
        object? lastCoffee = Scalar($"SELECT MAX(started_at) FROM pump_runs WHERE {IsCoffee}");

        return new SummaryInfo(
            CoffeesToday(),
            Count(PumpRunKind.Water, today),
            first?.StartedAt,
            first?.StartExact ?? false,
            first?.HeatupSeconds,
            runtimeToday,
            Count(PumpRunKind.Espresso, from),
            Count(PumpRunKind.Water, from),
            sessions,
            avgHeatup is DBNull or null ? null : Convert.ToInt32(avgHeatup),
            avgShot is DBNull or null ? null : Convert.ToDouble(avgShot),
            runtime,
            lastCoffee is DBNull or null ? null : Convert.ToInt64(lastCoffee));
    }

    public List<DayInfo> Daily(int days) => Query(
        $"""
        SELECT date(started_at / 1000, 'unixepoch', 'localtime') AS day, COUNT(*) FROM pump_runs
        WHERE started_at >= @from AND {IsCoffee} GROUP BY day ORDER BY day
        """,
        r => new DayInfo(r.GetString(0), r.GetInt32(1)),
        ("@from", RangeStart(days)));

    public List<HourInfo> Hours(int days) => Query(
        $"""
        SELECT CAST(strftime('%w', started_at / 1000, 'unixepoch', 'localtime') AS INTEGER) AS weekday,
            CAST(strftime('%H', started_at / 1000, 'unixepoch', 'localtime') AS INTEGER) AS hour, COUNT(*)
        FROM pump_runs WHERE started_at >= @from AND {IsCoffee} GROUP BY weekday, hour
        """,
        r => new HourInfo(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)),
        ("@from", RangeStart(days)));

    public List<SessionInfo> Sessions(int days, int limit) => Query(
        $"{SessionSelect} WHERE s.started_at >= @from ORDER BY s.started_at DESC LIMIT @limit",
        ReadSession,
        ("@cold", settings.ColdStartHxTemp), ("@from", RangeStart(days)), ("@limit", limit));

    // espressos and hot water, the pump runs someone asked for
    public List<ShotInfo> Shots(int days, int limit) => Query(
        $"{ShotSelect} WHERE started_at >= @from AND {IsDraw} ORDER BY started_at DESC LIMIT @limit",
        ReadShot,
        ("@from", RangeStart(days)), ("@limit", limit));

    public List<EventInfo> Events(int days, int limit) => Query(
        "SELECT at, kind, detail FROM events WHERE at >= @from ORDER BY at DESC, id DESC LIMIT @limit",
        r => new EventInfo(r.GetInt64(0), r.GetString(1), EventLabel(r.GetString(1)), r.GetString(2)),
        ("@from", RangeStart(days)), ("@limit", limit));

    public SessionDetail? Session(long? id, int maxPoints)
    {
        SessionInfo? session = Query(
            id is null ? $"{SessionSelect} ORDER BY s.started_at DESC LIMIT 1" : $"{SessionSelect} WHERE s.id = @id",
            ReadSession,
            ("@cold", settings.ColdStartHxTemp), ("@id", id)).FirstOrDefault();
        if (session is null)
        {
            return null;
        }

        List<SampleInfo> samples = Query(
            "SELECT at, hx, steam, target, heat, pump FROM samples WHERE session_id = @id ORDER BY at",
            r => new SampleInfo(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetBoolean(4), r.GetBoolean(5)),
            ("@id", session.Id));
        int step = Math.Max(1, (samples.Count + maxPoints - 1) / maxPoints);
        List<SampleInfo> thinned = samples.Where((_, index) => index % step == 0 || index == samples.Count - 1).ToList();

        List<ShotInfo> shots = Query(
            $"{ShotSelect} WHERE session_id = @id AND {IsDraw} ORDER BY started_at",
            ReadShot,
            ("@id", session.Id));
        return new SessionDetail(session, thinned, shots);
    }

    public IEnumerable<SessionInfo> ExportSessions() => Stream(
        $"{SessionSelect} ORDER BY s.started_at", ReadSession, ("@cold", settings.ColdStartHxTemp));

    public IEnumerable<ShotInfo> ExportPumpRuns() => Stream($"{ShotSelect} ORDER BY started_at", ReadShot);

    public IEnumerable<EventInfo> ExportEvents() => Stream(
        "SELECT at, kind, detail FROM events ORDER BY at, id",
        r => new EventInfo(r.GetInt64(0), r.GetString(1), EventLabel(r.GetString(1)), r.GetString(2)));

    public IEnumerable<object?[]> ExportSamples() => Stream(
        "SELECT at, session_id, hx, steam, target, boost, heat, pump, mode, rssi, bad, raw FROM samples ORDER BY at",
        r =>
        {
            var row = new object?[r.FieldCount];
            r.GetValues(row!);
            return row;
        });

    private int Count(string kind, long from) => Convert.ToInt32(Scalar(
        $"SELECT COUNT(*) FROM pump_runs WHERE started_at >= @from AND {Kind} = @kind",
        ("@from", from), ("@kind", kind)));

    private static SessionInfo ReadSession(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetInt64(1),
        r.GetBoolean(2),
        r.GetInt32(3),
        r.IsDBNull(4) ? null : r.GetInt64(4),
        r.IsDBNull(5) ? null : r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetInt64(6),
        r.GetInt64(7),
        r.GetInt32(8),
        r.GetInt32(9),
        r.GetInt32(10),
        r.GetBoolean(11));

    private static ShotInfo ReadShot(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetInt64(1),
        r.GetInt64(2),
        r.GetInt64(3) / 1000.0,
        r.GetInt32(4),
        r.GetInt32(5),
        r.GetInt32(6),
        r.GetString(7),
        r.GetString(8),
        PumpRunKind.Label(r.GetString(8)));
}
