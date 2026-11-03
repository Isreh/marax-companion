using Microsoft.Data.Sqlite;
using Xunit;

namespace MaraxTray.Tests;

// A database of version 1, shaped like the first real morning: the boiler filling after power on,
// a hot water draw with garbled lines and an espresso.
public sealed class MigrationTests : IDisposable
{
    private const string VersionOneSchema = """
        CREATE TABLE sessions (id INTEGER PRIMARY KEY, started_at INTEGER NOT NULL, start_exact INTEGER NOT NULL,
            start_hx INTEGER NOT NULL, start_steam INTEGER NOT NULL, ready_at INTEGER, ended_at INTEGER,
            last_seen INTEGER NOT NULL, firmware TEXT NOT NULL);
        CREATE TABLE pump_runs (id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id),
            started_at INTEGER NOT NULL, duration_ms INTEGER NOT NULL, hx_start INTEGER NOT NULL, hx_min INTEGER NOT NULL,
            steam_start INTEGER NOT NULL, mode TEXT NOT NULL);
        CREATE TABLE events (id INTEGER PRIMARY KEY, at INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL);
        CREATE TABLE samples (at INTEGER NOT NULL, session_id INTEGER NOT NULL REFERENCES sessions(id), hx INTEGER NOT NULL,
            steam INTEGER NOT NULL, target INTEGER, boost INTEGER NOT NULL, heat INTEGER NOT NULL, pump INTEGER NOT NULL,
            mode TEXT NOT NULL, rssi INTEGER NOT NULL, bad INTEGER NOT NULL, raw TEXT NOT NULL);
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        PRAGMA user_version = 1;
        """;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "MaraxTrayTests", Guid.NewGuid().ToString("N"));
    private readonly string path;
    private readonly long start = HistoryStore.ToMs(DateTime.Today.AddHours(8).ToUniversalTime());
    private long Water => start + 600_000;
    private long Espresso => start + 700_000;

    public MigrationTests()
    {
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "history.db");

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, VersionOneSchema);
        Execute(connection, $"INSERT INTO sessions VALUES (1, {start}, 1, 22, 22, NULL, NULL, {start + 900_000}, '1.09')");

        for (int i = 0; i < 5; i++)
        {
            Sample(connection, start + i * 1000, 22, 22, null, true, "C1.09,022,D07,022,0000,0,0?");
        }
        for (int i = 0; i < 30; i++)
        {
            string raw = i switch
            {
                1 => "C1.09,127,126?094,0000,0,0?",
                2 => "?1.09,127,126,094,0000,0,0?",
                3 => "C1.09,127,126,094,0000?0,0?",
                _ => "C1.09,127,126,094,0000,0,0?",
            };
            Sample(connection, Water + i * 1000, i is >= 1 and <= 3 ? 0 : 94, 127, i is >= 1 and <= 3 ? null : 126, true, raw);
        }
        for (int i = 0; i < 25; i++)
        {
            Sample(connection, Espresso + i * 1000, 95, 130, 136, true, i % 4 == 1 ? "???b?b?" : "C1.09,130,136,095,0000,1,1?");
        }

        Execute(connection, $"""
            INSERT INTO pump_runs VALUES (1, 1, {start}, 4500, 22, 22, 22, 'C');
            INSERT INTO pump_runs VALUES (2, 1, {Water}, 29500, 94, 0, 127, 'C');
            INSERT INTO pump_runs VALUES (3, 1, {Espresso}, 24500, 95, 95, 130, 'C');
            INSERT INTO events (at, kind, detail) VALUES ({start}, 'machine_on', 'HX 22 °C');
            INSERT INTO events (at, kind, detail) VALUES ({start + 3000}, 'water_missing', 'C1.09,022,D07,022,0000,0,0?');
            INSERT INTO events (at, kind, detail) VALUES ({start + 6000}, 'water_ok', '');
            INSERT INTO events (at, kind, detail) VALUES ({Water}, 'coffee', '30 s, HX 94 °C');
            INSERT INTO events (at, kind, detail) VALUES ({Espresso}, 'coffee', '25 s, HX 95 °C');
            """);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Sample(SqliteConnection connection, long at, int hx, int steam, int? target, bool pump, string raw)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO samples VALUES (@at, 1, @hx, @steam, @target, 0, 0, @pump, 'C', -70, 0, @raw)";
        command.Parameters.AddWithValue("@at", at);
        command.Parameters.AddWithValue("@hx", hx);
        command.Parameters.AddWithValue("@steam", steam);
        command.Parameters.AddWithValue("@target", target is int value ? value : DBNull.Value);
        command.Parameters.AddWithValue("@pump", pump);
        command.Parameters.AddWithValue("@raw", raw);
        command.ExecuteNonQuery();
    }

    private HistoryStore Open() => new(path, new TraySettings());

    [Fact]
    public void HotWaterIsNoLongerACoffee()
    {
        HistoryStore store = Open();

        List<ShotInfo> draws = store.Shots(0, 10);
        Assert.Equal(new[] { PumpRunKind.Espresso, PumpRunKind.Water }, draws.Select(d => d.Kind));
        Assert.Equal(1, store.CoffeesToday());
        Assert.Equal(1, store.Summary(1).HotWaterToday);
    }

    [Fact]
    public void GarbledReadingsAreReplaced()
    {
        HistoryStore store = Open();

        Assert.DoesNotContain(store.Session(1, 1000)!.Samples, s => s.Hx == 0);
        Assert.Equal(94, store.Shots(0, 10).Single(d => d.Kind == PumpRunKind.Water).HxMin);
    }

    [Fact]
    public void EventsFollowTheNewRules()
    {
        List<EventInfo> events = Open().Events(0, 100);

        Assert.DoesNotContain(events, e => e.Kind.StartsWith("water_"));
        Assert.Single(events, e => e.Kind == "coffee");
        Assert.Single(events, e => e.Kind == "hot_water");
        Assert.Single(events, e => e.Kind == "machine_on");
    }

    [Fact]
    public void KeepsABackupAndMigratesOnlyOnce()
    {
        Open();
        SqliteConnection.ClearAllPools();
        DateTime written = File.GetLastWriteTimeUtc(path + ".v1.bak");

        HistoryStore again = Open();

        Assert.True(File.Exists(path + ".v1.bak"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path + ".v1.bak"));
        Assert.Equal(2, again.Shots(0, 10).Count);
        using var backup = new SqliteConnection($"Data Source={path}.v1.bak;Mode=ReadOnly");
        backup.Open();
        using SqliteCommand command = backup.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events WHERE kind = 'coffee'";
        Assert.Equal(2L, command.ExecuteScalar());
    }
}
